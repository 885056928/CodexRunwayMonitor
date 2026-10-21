using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Monitor.Core;
using Monitor.UsageServer;

// 仅用于本机合成测试；部署密钥必须由运维独立生成。
const string reportKey = "synthetic-report-key-for-local-tests-0001";
const string adminKey = "synthetic-admin-key-for-local-tests-00002";
var tests = new List<(string Name, Func<Task> Run)>();

tests.Add(("启动拒绝缺失、占位、重复和长度非法的密钥", async () =>
{
    foreach (var pair in new[] { ("", adminKey), (reportKey, ""), (reportKey, reportKey), ("short", adminKey), (new string('x', 129), adminKey), (new string('中', 32), adminKey), (new string(' ', 32), adminKey), ("CHANGE_ME_" + new string('x', 32), adminKey) })
    {
        var rejected = false;
        try
        {
            await using var app = UsageServer.Create(["--Usage:ReportKey=" + pair.Item1, "--Usage:AdminKey=" + pair.Item2]);
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }
        Check(rejected, "无效密钥应拒绝启动");
    }
}
));

tests.Add(("心跳不足180秒在线，边界过期后完全移除IP", () =>
{
    var clock = new TestClock();
    var store = new UsageState(clock, 600);
    var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
    Check(store.TryRecord(new(id, "1.0.0"), IPAddress.Parse("203.0.113.4")), "应记录心跳");
    clock.Advance(TimeSpan.FromSeconds(179));
    Check(store.Snapshot(1, 50).OnlineClients == 1, "179秒仍在线");
    clock.Advance(TimeSpan.FromSeconds(1));
    var snapshot = store.Snapshot(1, 50);
    Check(snapshot.OnlineClients == 0 && snapshot.OnlineIps == 0 && snapshot.Clients.Length == 0, "180秒移除");
    return Task.CompletedTask;
}
));

tests.Add(("按安装ID稳定分页，汇总客户端及IP数量且更新覆盖旧IP", () =>
{
    var store = new UsageState(new TestClock(), 600);
    var first = Guid.Parse("00000000-0000-0000-0000-000000000001");
    var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
    var ip = IPAddress.Parse("203.0.113.4");
    store.TryRecord(new(second, "2.0"), ip);
    store.TryRecord(new(first, "1.0"), ip);
    Check(store.Snapshot(1, 1).Clients.Single().InstallationId == first, "首分页ID排序");
    Check(store.Snapshot(2, 1).Clients.Single().InstallationId == second, "次分页ID排序");
    Check(store.Snapshot(3, 1).Clients.Length == 0, "超出页为空");
    Check(store.Snapshot(int.MaxValue, 100).Clients.Length == 0, "大页码不溢出");
    Check(store.Snapshot(1, 50).OnlineIps == 1, "相同IP去重");
    store.TryRecord(new(first, "1.1"), IPAddress.Parse("198.51.100.9"));
    var snapshot = store.Snapshot(1, 50);
    Check(snapshot.OnlineClients == 2 && snapshot.OnlineIps == 2 && snapshot.Clients[0].Version == "1.1", "更新记录");
    return Task.CompletedTask;
}
));

tests.Add(("容量上限10000拒绝新增，允许更新，过期释放容量", () =>
{
    var clock = new TestClock();
    var store = new UsageState(clock, 600);
    var ip = IPAddress.Loopback;
    for (var i = 1; i <= 10000; i++)
        Check(store.TryRecord(new(new Guid(i, 0, 0, new byte[8]), "1"), ip), "限内记录应成功");
    Check(!store.TryRecord(new(Guid.NewGuid(), "1"), ip), "第10001条被拒绝");
    Check(store.TryRecord(new(new Guid(1, 0, 0, new byte[8]), "2"), ip), "满载仍可更新");
    clock.Advance(TimeSpan.FromSeconds(180));
    store.RemoveExpired();
    Check(store.TryRecord(new(Guid.NewGuid(), "1"), ip), "过期释放容量");
    Check(store.Snapshot(1, 50).OnlineClients == 1, "仅新记录在线");
    return Task.CompletedTask;
}
));

tests.Add(("每IP独立限流且一分钟后恢复", () =>
{
    var clock = new TestClock();
    var store = new UsageState(clock, 2);
    var first = IPAddress.Parse("203.0.113.1");
    var second = IPAddress.Parse("203.0.113.2");
    Check(store.TryAcquireRequest(first) && store.TryAcquireRequest(first), "限内通过");
    Check(!store.TryAcquireRequest(first) && store.TryAcquireRequest(second), "IP独立");
    clock.Advance(TimeSpan.FromMinutes(1));
    Check(store.TryAcquireRequest(first), "窗口恢复");
    return Task.CompletedTask;
}
));

tests.Add(("真实HTTP密钥隔离及健康信息最小化", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    using var health = await host.Client.GetAsync("/health");
    Check(health.StatusCode == HttpStatusCode.OK && await health.Content.ReadAsStringAsync() == "{\"status\":\"ok\"}", "健康仅返回状态");
    foreach (var key in new[] { "", adminKey, reportKey + "x" })
    {
        using var response = await host.SendHeartbeat(key);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "心跳拒绝错误密钥");
    }
    foreach (var key in new[] { "", reportKey, adminKey + "x" })
    {
        using var response = await host.GetSnapshot(key);
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "列表拒绝错误密钥");
    }
    using var accepted = await host.SendHeartbeat(reportKey);
    Check(accepted.StatusCode == HttpStatusCode.NoContent, "合法心跳204");
    using var listing = await host.GetSnapshot(adminKey);
    Check((await listing.Content.ReadFromJsonAsync<UsageSnapshot>())?.OnlineClients == 1, "合法管理可读取");
}
));

tests.Add(("真实HTTP不信任body IP，只采纳可信代理的最后一层", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    using var direct = await host.SendHeartbeat(reportKey, """{"installationId":"00000000-0000-0000-0000-000000000001","version":"1","ipAddress":"192.0.2.99"}""");
    Check(direct.StatusCode == HttpStatusCode.NoContent, "直连心跳");
    using var snapshotResponse = await host.GetSnapshot(adminKey);
    var directSnapshot = await snapshotResponse.Content.ReadFromJsonAsync<UsageSnapshot>();
    Check(IPAddress.IsLoopback(IPAddress.Parse(directSnapshot!.Clients.Single().IpAddress)), "body不能伪造来源");
    using var forwarded = await host.SendHeartbeat(reportKey, """{"installationId":"00000000-0000-0000-0000-000000000002","version":"1"}""", "192.0.2.99, 203.0.113.8");
    Check(forwarded.StatusCode == HttpStatusCode.NoContent, "代理心跳");
    using var forwardedResponse = await host.GetSnapshot(adminKey);
    var snapshot = await forwardedResponse.Content.ReadFromJsonAsync<UsageSnapshot>();
    Check(snapshot!.Clients[1].IpAddress == "203.0.113.8", "仅取最后一层可信代理地址");
}
));

tests.Add(("转发头中间件忽略非回环来源伪造的IP", async () =>
{
    await using var app = UsageServer.Create(["--Usage:ReportKey=" + reportKey, "--Usage:AdminKey=" + adminKey]);
    var context = new DefaultHttpContext();
    context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.4");
    context.Request.Headers["X-Forwarded-For"] = "192.0.2.99";
    var middleware = new ForwardedHeadersMiddleware(_ => Task.CompletedTask, NullLoggerFactory.Instance,
        app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>());
    await middleware.Invoke(context);
    Check(context.Connection.RemoteIpAddress.Equals(IPAddress.Parse("203.0.113.4")), "非回环代理不能更改来源地址");
}
));

tests.Add(("真实HTTP拒绝坏输入和非法分页", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    foreach (var body in new[] { "null", "{}", "[]", "bad-json", """{"installationId":"00000000-0000-0000-0000-000000000000","version":"1"}""", """{"installationId":"not-guid","version":"1"}""", """{"installationId":"00000000-0000-0000-0000-000000000001","version":null}""", """{"installationId":"00000000-0000-0000-0000-000000000001","version":""}""", """{"installationId":"00000000-0000-0000-0000-000000000001","version":"中文"}""", """{"installationId":"00000000-0000-0000-0000-000000000001","version":"bad value"}""", "{\"installationId\":\"00000000-0000-0000-0000-000000000001\",\"version\":\"" + new string('a', 65) + "\"}" })
    {
        using var response = await host.SendHeartbeat(reportKey, body);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "拒绝非法心跳: " + body);
    }
    using var maximumVersion = await host.SendHeartbeat(reportKey, "{\"installationId\":\"00000000-0000-0000-0000-000000000001\",\"version\":\"" + new string('a', 64) + "\"}");
    Check(maximumVersion.StatusCode == HttpStatusCode.NoContent, "64字符版本有效");
    foreach (var query in new[] { "?page=0", "?page=-1", "?page=x", "?pageSize=0", "?pageSize=101", "?pageSize=x", "?page=1&page=2" })
    {
        using var response = await host.GetSnapshot(adminKey, query);
        Check(response.StatusCode == HttpStatusCode.BadRequest, "拒绝非法分页: " + query);
    }
}
));

tests.Add(("ContentLength及chunked超限均返回413且后续请求成功", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    foreach (var chunked in new[] { false, true })
        foreach (var validPrefix in new[] { false, true })
        {
            var body = validPrefix ? "{\"installationId\":\"00000000-0000-0000-0000-000000000001\",\"version\":\"1\"}" + new string(' ', 4097) : new string('x', 4097);
            using var large = await host.SendHeartbeat(reportKey, body, chunked: chunked);
            Check(large.StatusCode == HttpStatusCode.RequestEntityTooLarge, chunked ? "chunked 4KiB上限" : "ContentLength 4KiB上限");
            Check(large.Headers.ConnectionClose == true, "HTTP/1超限后告知客户端关闭连接，避免复用排空失败的连接");
            using var next = await host.SendHeartbeat(reportKey);
            Check(next.StatusCode == HttpStatusCode.NoContent, "超限响应后合法请求仍成功");
        }
}
));

tests.Add(("真实HTTP限流返回429并附Retry-After", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey, 2);
    using var first = await host.SendHeartbeat(reportKey);
    using var second = await host.SendHeartbeat(reportKey);
    using var third = await host.SendHeartbeat(reportKey);
    Check(first.StatusCode == HttpStatusCode.NoContent && second.StatusCode == HttpStatusCode.NoContent, "前两次通过");
    Check(third.StatusCode == HttpStatusCode.TooManyRequests && third.Headers.RetryAfter is not null, "超限429");
    using var health = await host.Client.GetAsync("/health");
    Check(health.StatusCode == HttpStatusCode.OK, "限流不影响健康检查");
}
));

tests.Add(("Core上报与管理客户端通过真实回环HTTP互通且关闭取消等待", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    using var reportHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    using var adminHttp = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
    var identity = Guid.Parse("9da1cabe-3c7f-4f53-b01b-46b1063f84ab");
    var clock = new WaitingClock();
    await using var reporter = new UsageReporter(reportHttp, new MemoryIdentityStore(identity),
        new UsageEndpoint { ServerUrl = host.Client.BaseAddress!.AbsoluteUri, ReportKey = reportKey }, "1.0", clock: clock);
    await reporter.ApplyAsync(true);
    // 真实响应结束后上报循环才进入60秒等待，使用该信号避免固定延时猜测心跳完成。
    await clock.Waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var snapshot = await new UsageAdminClient(adminHttp).GetSnapshotAsync(host.Client.BaseAddress!, adminKey, 1, 50);
    var client = snapshot.Clients.Single();
    Check(client.InstallationId == identity && client.Version == "1.0" && IPAddress.IsLoopback(IPAddress.Parse(client.IpAddress)), "Core DTO、授权和路径互通");
    await reporter.ApplyAsync(false).WaitAsync(TimeSpan.FromSeconds(5));
    Check(clock.Inner.PendingTimers == 0, "关闭上报必须取消等待");
}
));

tests.Add(("框架诊断日志不持久化来源IP或授权密钥", async () =>
{
    using var logs = new RecordingLogs();
    await using var host = await TestHost.Start(reportKey, adminKey, logs: logs);
    using var heartbeat = await host.SendHeartbeat(reportKey, forwardedFor: "203.0.113.123");
    using var unknown = await host.Client.GetAsync("/203.0.113.123");
    Check(!logs.Messages.IsEmpty, "测试日志接收器应实际收到主机生命周期日志");
    Check(!logs.Messages.Any(message => message.Contains("203.0.113.123", StringComparison.Ordinal)
        || message.Contains(reportKey, StringComparison.Ordinal) || message.Contains(adminKey, StringComparison.Ordinal)), "诊断日志不能保留来源IP或密钥");
}
));

tests.Add(("路由尾斜杠及大小写变体都必须鉴权且正确密钥可用", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey);
    foreach (var path in new[] { "/api/usage/clients/", "/API/USAGE/CLIENTS/", "/API/USAGE/CLIENTS" })
    {
        using var denied = await host.GetSnapshot("", path: path);
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "管理路由必须鉴权: " + path);
        using var permitted = await host.GetSnapshot(adminKey, path: path);
        Check(permitted.StatusCode == HttpStatusCode.OK, "正确管理密钥兼容路由: " + path);
        using var wrongKey = await host.GetSnapshot(reportKey, path: path);
        Check(wrongKey.StatusCode == HttpStatusCode.Unauthorized, "尾斜杠仍隔离两种密钥");
    }
    foreach (var path in new[] { "/api/usage/heartbeat/", "/API/USAGE/HEARTBEAT/", "/API/USAGE/HEARTBEAT" })
    {
        using var denied = await host.SendHeartbeat("", path: path);
        Check(denied.StatusCode == HttpStatusCode.Unauthorized, "心跳路由必须鉴权: " + path);
        using var permitted = await host.SendHeartbeat(reportKey, path: path);
        Check(permitted.StatusCode == HttpStatusCode.NoContent, "正确上报密钥兼容路由: " + path);
        using var wrongKey = await host.SendHeartbeat(adminKey, path: path);
        Check(wrongKey.StatusCode == HttpStatusCode.Unauthorized, "尾斜杠仍隔离两种密钥");
    }
    foreach (var path in new[] { "/api/usage/clients//", "/api/usage/clients///" })
    {
        using var denied = await host.GetSnapshot("", path: path);
        Check(denied.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, "多个末尾斜杠不能泄露管理数据");
    }
    foreach (var path in new[] { "/api/usage/heartbeat//", "/api/usage/heartbeat///" })
    {
        using var denied = await host.SendHeartbeat("", path: path);
        Check(denied.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized, "多个末尾斜杠不能未授权写入");
    }
}
));

tests.Add(("尾斜杠路由的未授权请求也计入来源IP限流", async () =>
{
    await using var host = await TestHost.Start(reportKey, adminKey, 2);
    using var first = await host.GetSnapshot("", path: "/api/usage/clients/");
    using var second = await host.SendHeartbeat("", path: "/api/usage/heartbeat/");
    using var third = await host.GetSnapshot(adminKey, path: "/API/USAGE/CLIENTS/");
    Check(first.StatusCode == HttpStatusCode.Unauthorized && second.StatusCode == HttpStatusCode.Unauthorized,
        $"尾斜杠未授权请求必须被拒绝（管理：{(int)first.StatusCode}，心跳：{(int)second.StatusCode}）");
    Check(third.StatusCode == HttpStatusCode.TooManyRequests, "尾斜杠和大小写变体共享IP限流");
}
));

var failures = 0;
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + exception);
    }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class TestHost : IAsyncDisposable
{
    private TestHost(WebApplication app, HttpClient client)
    {
        this.app = app;
        Client = client;
    }

    private readonly WebApplication app;
    public HttpClient Client { get; }

    public static async Task<TestHost> Start(string reportKey, string adminKey, int requestsPerMinute = 600, RecordingLogs? logs = null)
    {
        var app = UsageServer.Create(["--urls=http://127.0.0.1:0", "--Usage:ReportKey=" + reportKey, "--Usage:AdminKey=" + adminKey, "--Usage:RequestsPerMinute=" + requestsPerMinute, "--Logging:LogLevel:Default=" + (logs is null ? "Warning" : "Information")]);
        if (logs is not null) app.Services.GetRequiredService<ILoggerFactory>().AddProvider(logs);
        await app.StartAsync();
        return new TestHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.Single()), Timeout = TimeSpan.FromSeconds(10) });
    }

    public Task<HttpResponseMessage> SendHeartbeat(string key, string? body = null, string? forwardedFor = null, bool chunked = false, string path = "/api/usage/heartbeat")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path);
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        var json = body ?? """{"installationId":"00000000-0000-0000-0000-000000000001","version":"1.0.0-test+local_1"}""";
        request.Content = chunked ? new ChunkedContent(json) : new StringContent(json, Encoding.UTF8, "application/json");
        return SendAndDispose(request);
    }

    public Task<HttpResponseMessage> GetSnapshot(string key, string query = "", string path = "/api/usage/clients")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path + query);
        if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return SendAndDispose(request);
    }

    private async Task<HttpResponseMessage> SendAndDispose(HttpRequestMessage request)
    {
        using (request)
        {
            return await Client.SendAsync(request);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }
}

internal sealed class ChunkedContent : HttpContent
{
    public ChunkedContent(string json)
    {
        body = Encoding.UTF8.GetBytes(json);
        Headers.ContentType = new MediaTypeHeaderValue("application/json");
    }

    private readonly byte[] body;

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(body).AsTask();
}

internal sealed class MemoryIdentityStore : IStateStore<Guid>
{
    public MemoryIdentityStore(Guid identity) => this.identity = identity;
    private readonly Guid identity;
    public Task<Guid> LoadAsync(CancellationToken ct = default) => Task.FromResult(identity);
    public Task SaveAsync(Guid state, CancellationToken ct = default) => Task.CompletedTask;
}

internal sealed class WaitingClock : TimeProvider
{
    public TestClock Inner { get; } = new();
    public TaskCompletionSource Waiting { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public override DateTimeOffset GetUtcNow() => Inner.GetUtcNow();
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = Inner.CreateTimer(callback, state, dueTime, period);
        Waiting.TrySetResult();
        return timer;
    }
}

internal sealed class RecordingLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();
    public ILogger CreateLogger(string categoryName) => new RecordingLogger(Messages);
    public void Dispose() { }

    private sealed class RecordingLogger : ILogger
    {
        public RecordingLogger(ConcurrentQueue<string> messages) => this.messages = messages;
        private readonly ConcurrentQueue<string> messages;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) => messages.Enqueue(formatter(state, exception));
    }
}
