using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Monitor.App;
using Monitor.Core;

namespace Monitor.UsageAdmin;

public sealed class AdminViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    public AdminViewModel(HttpClient httpClient)
    {
        client = new UsageAdminClient(httpClient);
        ConnectCommand = new AsyncRelayCommand(ExecuteConnectAsync, ReportOperationError, CanConnect);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, ReportOperationError, CanDisconnect);
        RefreshCommand = new AsyncRelayCommand(ExecuteRefreshAsync, ReportOperationError, CanRefresh);
        PreviousPageCommand = new AsyncRelayCommand(ExecutePreviousPageAsync, ReportOperationError, CanPreviousPage);
        NextPageCommand = new AsyncRelayCommand(ExecuteNextPageAsync, ReportOperationError, CanNextPage);
    }

    #region 属性

    private const int PageSize = 50;
    private readonly UsageAdminClient client;
    private string serverAddress = "";
    private string adminKey = "";
    private Uri? endpoint;
    private CancellationTokenSource? sessionCancellation;
    private Task refreshLoop = Task.CompletedTask;
    private Task activeRequest = Task.CompletedTask;
    private Task disconnection = Task.CompletedTask;
    private UsageSnapshot? snapshot;
    private bool disconnecting;
    private bool disposed;

    public string ServerAddress
    {
        get => serverAddress;
        set
        {
            serverAddress = value;
            NotifyChanged();
        }
    }

    // 密钥仅在当前进程和 PasswordBox 内保留；断开和退出都由同一清理路径清空。
    public string AdminKey
    {
        get => adminKey;
        set
        {
            adminKey = value;
            NotifyChanged();
        }
    }

    public ObservableCollection<UsageClient> Clients { get; } = [];
    public bool IsConnected { get; private set; }
    public bool IsBusy { get; private set; }
    public bool IsStale { get; private set; } = true;
    public bool HasError { get; private set; }
    public bool CanEditConnection => !IsConnected && !IsBusy && !disconnecting && !disposed;
    public string OnlineClients => snapshot?.OnlineClients.ToString() ?? "—";
    public string OnlineIps => snapshot?.OnlineIps.ToString() ?? "—";
    public string LastRefresh { get; private set; } = "尚未刷新";
    public string ServerTime => snapshot is null ? "—" : FormatTime(snapshot.ServerTime);
    public int Page => snapshot?.Page ?? 1;
    public int TotalPages => Math.Max(1, ((snapshot?.OnlineClients ?? 0) + PageSize - 1) / PageSize);
    public bool HasNextPage => Page < TotalPages;
    public string PageDescription => $"第 {Page} / {TotalPages} 页 · 每页 {PageSize} 条";
    public string State => IsBusy ? "正在刷新" : !IsConnected ? "未连接" : HasError ? "刷新失败 · 数据已过期" : "已连接 · 每 10 秒刷新";
    public string Status { get; private set; } = "填写服务器地址与管理密钥，然后连接。";
    public string DataNotice => snapshot is null ? "暂无快照" : IsStale ? "数据已过期，仅供参考" : "最后一次成功刷新结果";
    public string EmptyMessage => IsBusy ? "正在获取在线客户端…" : !IsConnected ? "连接服务器后显示在线客户端。" : HasError ? "暂时无法获取快照，请检查连接后刷新。" : "当前没有在线客户端。";
    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    #region 命令

    public AsyncRelayCommand ConnectCommand { get; }
    public AsyncRelayCommand DisconnectCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }

    #endregion

    #region 方法

    private bool CanConnect() => CanEditConnection;
    private bool CanDisconnect() => IsConnected && !disconnecting;
    private bool CanRefresh() => IsConnected && !IsBusy && !disconnecting;
    private bool CanPreviousPage() => CanRefresh() && Page > 1;
    private bool CanNextPage() => CanRefresh() && HasNextPage;

    private async Task ExecuteConnectAsync()
    {
        try
        {
            endpoint = UsageEndpoint.ValidateServerUrl(ServerAddress);
        }
        catch (ArgumentException)
        {
            SetValidationError("请输入有效的 HTTPS 服务器地址；HTTP 仅允许本机回环地址。");
            return;
        }

        try
        {
            UsageEndpoint.ValidateKey(AdminKey);
        }
        catch (ArgumentException)
        {
            SetValidationError("管理密钥须为 32–128 位非空白 ASCII 字符。");
            return;
        }

        sessionCancellation = new CancellationTokenSource();
        IsConnected = true;
        HasError = false;
        NotifyChanged();
        // 定时器与手动刷新共用忙碌状态，始终只允许一个管理请求在途。
        refreshLoop = RefreshLoopAsync(sessionCancellation.Token);
        await FetchPageAsync(1);
    }

    private Task ExecuteRefreshAsync() => FetchPageAsync(Page);
    private Task ExecutePreviousPageAsync() => FetchPageAsync(Page - 1);
    private Task ExecuteNextPageAsync() => FetchPageAsync(Page + 1);

    private Task FetchPageAsync(int page)
    {
        if (!CanRefresh() || sessionCancellation is null) return Task.CompletedTask;
        activeRequest = FetchPageCoreAsync(page, sessionCancellation.Token);
        return activeRequest;
    }

    private async Task FetchPageCoreAsync(int page, CancellationToken cancellationToken)
    {
        IsBusy = true;
        Status = "正在获取最新在线快照…";
        NotifyChanged();
        try
        {
            var result = await client.GetSnapshotAsync(endpoint!, AdminKey, page, PageSize, cancellationToken);
            var lastPage = Math.Max(1, (result.OnlineClients + PageSize - 1) / PageSize);
            if (page > lastPage)
            {
                // 在线数量会随时下降，自动回到仍存在的末页，避免停留在越界空页。
                result = await client.GetSnapshotAsync(endpoint!, AdminKey, lastPage, PageSize, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            snapshot = result;
            Clients.Clear();
            foreach (var item in result.Clients) Clients.Add(item);
            LastRefresh = FormatTime(DateTimeOffset.Now);
            IsStale = false;
            HasError = false;
            Status = "快照已更新。在线表示距服务器时间不足 180 秒收到心跳。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 断开是正常取消，保留断开状态，绝不让已取消的响应覆盖已有快照。
            return;
        }
        catch (Exception error)
        {
            ReportOperationError(error);
        }
        finally
        {
            IsBusy = false;
            NotifyChanged();
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                await FetchPageAsync(Page);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // 会话拥有定时器，断开时取消并等待循环结束。
            return;
        }
        catch (Exception error)
        {
            ReportOperationError(error);
        }
    }

    public Task DisconnectAsync()
    {
        if (disconnecting) return disconnection;
        disconnection = DisconnectCoreAsync();
        return disconnection;
    }

    private async Task DisconnectCoreAsync()
    {
        disconnecting = true;
        IsConnected = false;
        IsStale = true;
        HasError = false;
        AdminKey = "";
        Status = snapshot is null ? "已断开，管理密钥已清空。" : "已断开，管理密钥已清空；保留的快照已过期。";
        var cancellation = sessionCancellation;
        sessionCancellation = null;
        cancellation?.Cancel();
        NotifyChanged();
        try
        {
            await activeRequest;
            await refreshLoop;
        }
        finally
        {
            cancellation?.Dispose();
            disconnecting = false;
            NotifyChanged();
        }
    }

    public async ValueTask DisposeAsync()
    {
        disposed = true;
        await DisconnectAsync();
    }

    private void SetValidationError(string message)
    {
        HasError = true;
        IsStale = true;
        Status = message;
        NotifyChanged();
    }

    public void ReportOperationError(Exception error)
    {
        HasError = true;
        IsStale = true;
        // 仅使用固定提示和状态码，不展示异常消息、响应体或带凭据的请求信息。
        Status = error switch
        {
            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden }
                => "管理密钥无效或权限不足。请断开后核对密钥，再重新连接。",
            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }
                => "服务器请求过多，将按正常刷新间隔重试。已有数据已过期。",
            OperationCanceledException => "请求超时，将按正常刷新间隔重试。已有数据已过期。",
            JsonException or InvalidDataException => "服务器返回的快照格式无效。已有数据已过期。",
            _ => "刷新失败，请检查服务器地址与网络。已有数据已过期，可手动刷新或等待重试。"
        };
        NotifyChanged();
    }

    private static string FormatTime(DateTimeOffset value) => value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");

    private void NotifyChanged()
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        ConnectCommand.NotifyCanExecuteChanged();
        DisconnectCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
    }

    #endregion
}
