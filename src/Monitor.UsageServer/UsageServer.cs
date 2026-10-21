using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Monitor.Core;

namespace Monitor.UsageServer;

public static class UsageServer
{
    private const int MaximumRequestBytes = 4096;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static WebApplication Create(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        // 框架请求日志会包含任意URL，代理诊断也可能包含来源IP；统计服务不保留这类请求日志。
        builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.None);
        var keys = new UsageKeys(builder.Configuration["Usage:ReportKey"], builder.Configuration["Usage:AdminKey"]);
        var limitText = builder.Configuration["Usage:RequestsPerMinute"];
        var requestsPerMinute = 600;
        if (limitText is not null && (!int.TryParse(limitText, NumberStyles.None, CultureInfo.InvariantCulture, out requestsPerMinute) || requestsPerMinute < 1))
            throw new InvalidOperationException("Usage:RequestsPerMinute 必须是正整数。");

        var urls = builder.Configuration["urls"] ?? "http://127.0.0.1:5088";
        foreach (var url in urls.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            // 公网TLS由本机反向代理终止；禁止误配为对外暴露的HTTP监听。
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme is not ("http" or "https"))
                throw new InvalidOperationException("使用统计服务只能绑定本机回环地址，公网访问须通过HTTPS反向代理。");
        }
        if (string.IsNullOrWhiteSpace(urls)) throw new InvalidOperationException("服务监听地址不能为空。");
        builder.WebHost.UseUrls(urls);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = MaximumRequestBytes);
        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        });
        builder.Services.AddSingleton(keys);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton(provider => new UsageState(provider.GetRequiredService<TimeProvider>(), requestsPerMinute));
        builder.Services.AddHostedService<UsageCleanupService>();
        var app = builder.Build();
        app.UseForwardedHeaders();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            // 使用路由器实际选中的端点；尾斜杠、大小写等路由等价形式必须共享鉴权与限流。
            var authorization = context.GetEndpoint()?.Metadata.GetMetadata<UsageAuthorization>();
            if (authorization is not null)
            {
                var sourceAddress = context.Connection.RemoteIpAddress;
                if (sourceAddress is null)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }
                if (!context.RequestServices.GetRequiredService<UsageState>().TryAcquireRequest(sourceAddress))
                {
                    context.Response.Headers.RetryAfter = "60";
                    context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                    return;
                }
                if (!keys.Authorize(context.Request, authorization.Admin))
                {
                    context.Response.Headers.WWWAuthenticate = "Bearer";
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
                // 管理列表包含IP，禁止代理和浏览器缓存。
                context.Response.Headers.CacheControl = "no-store";
            }
            await next(context);
        });
        app.MapGet("/health", () => Results.Json(new { status = "ok" }));
        app.MapPost("/api/usage/heartbeat", ReceiveHeartbeat).WithMetadata(new UsageAuthorization(false));
        app.MapGet("/api/usage/clients", (HttpContext context, UsageState state) =>
        {
            if (!TryReadPage(context.Request, "page", 1, int.MaxValue, out var page)
                || !TryReadPage(context.Request, "pageSize", 50, 100, out var pageSize))
                return Results.BadRequest();
            return Results.Json(state.Snapshot(page, pageSize));
        }).WithMetadata(new UsageAuthorization(true));
        return app;
    }

    private static async Task<IResult> ReceiveHeartbeat(HttpContext context, UsageState state)
    {
        if (context.Request.ContentLength > MaximumRequestBytes) return PayloadTooLarge(context);
        if (!context.Request.HasJsonContentType()) return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        UsageHeartbeat? heartbeat;
        try
        {
            // 先在Kestrel的4KiB边界内完整读取，避免坏JSON提前返回后仍需排空未知长度body。
            using var body = new MemoryStream(MaximumRequestBytes);
            await context.Request.Body.CopyToAsync(body, MaximumRequestBytes, context.RequestAborted);
            heartbeat = JsonSerializer.Deserialize<UsageHeartbeat>(body.GetBuffer().AsSpan(0, (int)body.Length), JsonOptions);
        }
        catch (JsonException)
        {
            return Results.BadRequest();
        }
        catch (BadHttpRequestException exception) when (exception.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return PayloadTooLarge(context);
        }
        if (heartbeat is null || heartbeat.InstallationId == Guid.Empty || !IsValidVersion(heartbeat.Version)) return Results.BadRequest();
        return state.TryRecord(heartbeat, context.Connection.RemoteIpAddress!)
            ? Results.NoContent()
            : Results.StatusCode(StatusCodes.Status429TooManyRequests);
    }

    private static IResult PayloadTooLarge(HttpContext context)
    {
        // 超限body无法安全排空；明确关闭HTTP/1连接，避免客户端复用后撞上服务端中止。
        if (HttpProtocol.IsHttp10(context.Request.Protocol) || HttpProtocol.IsHttp11(context.Request.Protocol))
            context.Response.Headers.Connection = "close";
        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    }

    private static bool IsValidVersion(string? version) => version is { Length: >= 1 and <= 64 }
        && version.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '+' or '-' or '_');

    private static bool TryReadPage(HttpRequest request, string key, int defaultValue, int maximum, out int value)
    {
        value = defaultValue;
        if (!request.Query.TryGetValue(key, out var values)) return true;
        return values.Count == 1 && int.TryParse(values[0], NumberStyles.None, CultureInfo.InvariantCulture, out value) && value >= 1 && value <= maximum;
    }
}

internal sealed record UsageAuthorization(bool Admin);
