namespace Monitor.Core;

public sealed record RequestLedger
{
    public List<DateTimeOffset> Requests { get; init; } = [];
    public DateTimeOffset? CooldownUntil { get; init; }
    public void Validate() { if (Requests is null || Requests.Count > 100) throw new ArgumentException("请求额度记录无效"); }
}
public sealed record MonitorStatus(bool Running = false, bool Busy = false, DateTimeOffset? LastCheck = null,
    DateTimeOffset? LastSuccess = null, DateTimeOffset? NextCheck = null, DateTimeOffset? BlockedUntil = null,
    string Message = "尚未检查", bool Error = false, ResetEvent? Latest = null);

public sealed class MonitoringService : IAsyncDisposable
{
    public MonitoringService(HttpClient client, AppSettings settings, EventProcessor processor, IStateStore<RequestLedger> ledgerStore,
        RequestLedger ledger, Action<string, bool>? log = null, TimeProvider? clock = null)
    {
        settings.Validate(); ledger.Validate();
        this.client = client; this.settings = settings; this.processor = processor;
        this.ledgerStore = ledgerStore; this.ledger = ledger; this.log = log ?? ((_, _) => { }); this.clock = clock ?? TimeProvider.System;
        status = new MonitorStatus(Latest: processor.State.Latest);
    }

    private const int RequestsPerWindow = 20;
    private static readonly TimeSpan RequestWindow = TimeSpan.FromHours(1);
    private static readonly TimeSpan CooldownBoundaryMargin = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ResumeCheckInterval = TimeSpan.FromSeconds(30);

    private readonly HttpClient client;
    private AppSettings settings;
    private readonly EventProcessor processor;
    private readonly IStateStore<RequestLedger> ledgerStore;
    private RequestLedger ledger;
    private readonly Action<string, bool> log;
    private readonly TimeProvider clock;
    private readonly SemaphoreSlim checkGate = new(1, 1);
    private readonly SemaphoreSlim lifecycle = new(1, 1);
    private readonly object sync = new();
    private readonly CancellationTokenSource shutdown = new();
    private CancellationTokenSource? loopCancellation;
    private CancellationTokenSource? activeRequest;
    private Task loop = Task.CompletedTask;
    private MonitorStatus status;
    private bool disposed;
    public MonitorStatus Status => Volatile.Read(ref status);
    public event Action<MonitorStatus>? StatusChanged;
    public event Action<ResetEvent>? EventDetected;

    private void Change(Func<MonitorStatus, MonitorStatus> update)
    {
        MonitorStatus snapshot;
        lock (sync) { status = update(status); snapshot = status; }
        try
        {
            StatusChanged?.Invoke(snapshot);
        }
        catch (Exception e)
        {
            log("状态展示失败：" + e, true);
        }
    }

    public async Task CheckAsync(CancellationToken ct = default)
    {
        if (disposed || !await checkGate.WaitAsync(0, ct).ConfigureAwait(false)) return;
        using var request = CancellationTokenSource.CreateLinkedTokenSource(ct, shutdown.Token);
        lock (sync) activeRequest = request;
        try
        {
            request.Token.ThrowIfCancellationRequested();
            var now = clock.GetUtcNow();
            var recent = ledger.Requests.Where(t => t > now - RequestWindow).Order().ToList();
            var blocked = ledger.CooldownUntil;
            if (recent.Count >= RequestsPerWindow && (blocked is null || recent[0] + RequestWindow + CooldownBoundaryMargin > blocked))
                blocked = recent[0] + RequestWindow + CooldownBoundaryMargin;
            if (blocked > now)
            {
                Change(s => s with { BlockedUntil = blocked, Message = $"请求冷却中，可检查时间：{blocked.Value.ToLocalTime():HH:mm:ss}", Error = false });
                return;
            }

            Change(s => s with { Busy = true, BlockedUntil = null, Message = "正在检查…", Error = false });
            recent.Add(now);
            var reserved = new RequestLedger
            {
                Requests = recent
            };
            // 先持久化本次额度占用再请求网络；重启后也不能重复使用已消耗的额度。
            await ledgerStore.SaveAsync(reserved, request.Token).ConfigureAwait(false);
            ledger = reserved;
            Change(s => s with { LastCheck = now });
            log("发起 API 请求 GET /records/latest?kind=reset_completed", false);
            var api = new ApiService(client, new Uri(settings.ApiBaseUrl), TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
            var value = await api.GetLatestAsync(request.Token).ConfigureAwait(false);
            request.Token.ThrowIfCancellationRequested();
            bool notify = value is not null && await processor.ProcessAsync(value, settings.FirstRunSilent, request.Token).ConfigureAwait(false);
            log(value is null ? "HTTP 200，无匹配记录" : notify ? "HTTP 200，检测到新事件" : "HTTP 200，已建立基线或重复/历史事件", false);
            Change(s => s with { LastSuccess = clock.GetUtcNow(), Latest = processor.State.Latest, Error = false, Message = value is null ? "连接正常，暂无完成记录" : notify ? "检测到新的重置完成事件" : "连接正常，事件已处理" });
            if (notify && settings.EnableNotification)
            {
                try
                {
                    EventDetected?.Invoke(value!);
                }
                catch (Exception e)
                {
                    log("通知派发失败：" + e, true);
                }
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            Change(s => s with { Message = "检查已取消", Error = false });
            log("检查已取消", false);
        }
        catch (Exception e)
        {
            if (e is ApiException { StatusCode: 429 } limited)
            {
                ledger = ledger with
                {
                    CooldownUntil = clock.GetUtcNow() + (limited.RetryAfter ?? TimeSpan.FromMinutes(5))
                };
                Change(s => s with { BlockedUntil = ledger.CooldownUntil });
                try
                {
                    await ledgerStore.SaveAsync(ledger, shutdown.Token).ConfigureAwait(false);
                }
                catch (Exception saveError)
                {
                    log("限流状态保存失败：" + saveError, true);
                }
            }

            var message = e switch
            {
                ApiException => e.Message,
                TimeoutException => "请求超时，等待下次检查",
                HttpRequestException => "网络连接失败，等待下次检查",
                IOException or UnauthorizedAccessException => "本地状态保存失败，请检查磁盘与权限",
                _ => "检查失败：" + e.GetType().Name
            };
            Change(s => s with { Message = message, Error = true });
            log(message + " [" + e + "]", true);
        }
        finally
        {
            lock (sync)
                activeRequest = null;
            Change(s => s with { Busy = false });
            checkGate.Release();
        }
    }

    public async Task StartAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!disposed && !Status.Running)
                StartCore();
        }
        finally
        {
            lifecycle.Release();
        }
    }
    private void StartCore()
    {
        loopCancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token);
        var token = loopCancellation.Token;
        Change(s => s with { Running = true });
        log("开始监控", false);
        // 保留独立调度任务，避免启动时同步完成的阶段阻塞 UI；暂停与退出必须等待该任务结束。
        loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
    }
    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await CheckAsync(ct).ConfigureAwait(false);
                var next = clock.GetUtcNow().AddMinutes(settings.CheckIntervalMinutes);
                if (Status.BlockedUntil > next)
                    next = Status.BlockedUntil.Value;
                Change(s => s with { NextCheck = next });
                // 分段等待以便 Windows 从睡眠恢复后重新核对墙上时间，不补发睡眠期间的轮询。
                while (next > clock.GetUtcNow())
                {
                    var delay = next - clock.GetUtcNow();
                    if (delay <= TimeSpan.Zero)
                        break;
                    await Task.Delay(delay < ResumeCheckInterval ? delay : ResumeCheckInterval, clock, ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 暂停和退出主动取消调度，记录正常结束而不标记网络故障。
            log("监控调度已取消", false);
        }
        catch (Exception e)
        {
            Change(s => s with { Error = true, Message = "调度失败，请重新开始监控", Running = false });
            log("调度异常：" + e, true);
        }
    }
    public async Task PauseAsync()
    {
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            await PauseCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            lifecycle.Release();
        }
    }
    private async Task PauseCoreAsync()
    {
        loopCancellation?.Cancel();
        lock (sync) activeRequest?.Cancel();
        await loop.ConfigureAwait(false);
        await checkGate.WaitAsync().ConfigureAwait(false);
        checkGate.Release();
        loopCancellation?.Dispose(); loopCancellation = null;
        Change(s => s with { Running = false, NextCheck = null });
        log("暂停监控", false);
    }
    public async Task UpdateSettingsAsync(AppSettings value)
    {
        value.Validate();
        await lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            var running = Status.Running;
            await PauseCoreAsync().ConfigureAwait(false);
            settings = value;
            if (running && !disposed)
                StartCore();
        }
        finally
        {
            lifecycle.Release();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true; shutdown.Cancel();
        await PauseAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }
}
