using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Monitor.Core;

public sealed class UsageReporter : IAsyncDisposable
{
    public UsageReporter(HttpClient client, IStateStore<Guid> identityStore, UsageEndpoint endpoint, string version,
        Action<string, bool>? log = null, TimeProvider? clock = null)
    {
        endpoint.Validate();
        this.client = client;
        this.identityStore = identityStore;
        this.endpoint = endpoint;
        this.version = version;
        this.log = log;
        this.clock = clock ?? TimeProvider.System;
    }

    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(60);
    private readonly HttpClient client;
    private readonly IStateStore<Guid> identityStore;
    private readonly UsageEndpoint endpoint;
    private readonly string version;
    private readonly Action<string, bool>? log;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private CancellationTokenSource? cancellation;
    private Task loop = Task.CompletedTask;
    private bool disposed;

    public async Task ApplyAsync(bool enabled)
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            if (enabled && endpoint.IsConfigured)
            {
                if (cancellation is not null) return;
                cancellation = new CancellationTokenSource();
                // 保存并等待同一个循环任务；关闭和退出都能确认请求已取消。
                loop = RunAsync(cancellation.Token);
            }
            else
            {
                await StopAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            lifecycle.Release();
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        Guid? identity = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    identity ??= await identityStore.LoadAsync(ct).ConfigureAwait(false);
                    if (identity == Guid.Empty) throw new InvalidDataException("统计设备标识无效。");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(TimeSpan.FromSeconds(5));
                    using var request = new HttpRequestMessage(HttpMethod.Post, UsageEndpoint.Resource(new Uri(endpoint.ServerUrl), "api/usage/heartbeat"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ReportKey);
                    request.Content = JsonContent.Create(new UsageHeartbeat(identity.Value, version));
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                catch (Exception error) when (!ct.IsCancellationRequested && error is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException)
                {
                    // 外部异常可能含地址或令牌，统计日志只保留安全的故障类别；失败不影响原有监控。
                    log?.Invoke("使用统计上报失败（" + error.GetType().Name + "），将在下一周期重试。", true);
                }
                await Task.Delay(HeartbeatInterval, clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 用户关闭统计或退出时的正常取消，不能记成网络故障。
            return;
        }
    }

    private async Task StopAsync()
    {
        if (cancellation is null) return;
        await cancellation.CancelAsync().ConfigureAwait(false);
        await loop.ConfigureAwait(false);
        cancellation.Dispose();
        cancellation = null;
    }

    public async ValueTask DisposeAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (disposed) return;
            disposed = true;
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }
}
