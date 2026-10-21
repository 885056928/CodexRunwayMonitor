using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Threading;

namespace Monitor.App;

public sealed class AppController : IAsyncDisposable
{
    private AppController(HttpClient client, JsonStore<AppSettings> settingsStore, LoggingService log, AppSettings settings, MonitoringService monitor, Dispatcher dispatcher,
        HttpClient usageClient, UsageReporter usageReporter, bool usageEndpointReady)
    {
        this.client = client;
        this.settingsStore = settingsStore;
        Log = log;
        Settings = settings;
        Monitor = monitor;
        this.dispatcher = dispatcher;
        this.usageClient = usageClient;
        this.usageReporter = usageReporter;
        UsageEndpointReady = usageEndpointReady;
        ViewModel = new MainViewModel(this);
        ViewModel.Apply(monitor.Status, settings);
    }

    private readonly HttpClient client;
    private readonly HttpClient usageClient;
    private readonly UsageReporter usageReporter;
    private readonly JsonStore<AppSettings> settingsStore;
    private readonly Dispatcher dispatcher;
    private NotificationService? notifications;
    private TrayService? tray;
    private MainWindow? main;
    private SettingsWindow? settingsWindow;
    private Task? disposalTask;
    private Task? exitTask;
    public LoggingService Log { get; }
    public AppSettings Settings { get; private set; }
    public MonitoringService Monitor { get; }
    public MainViewModel ViewModel { get; }
    public bool Exiting { get; private set; }
    public bool UsageEndpointReady { get; }
    public event Action? ExitRequested;
    public static async Task<AppController> CreateAsync(string dataDirectory, HttpClient? client = null,
        HttpClient? usageClient = null, UsageEndpoint? usageEndpoint = null)
    {
        var dispatcher = Dispatcher.CurrentDispatcher;
        var log = new LoggingService(Path.Combine(dataDirectory, "Logs"));
        var warnings = new System.Collections.Generic.List<string>();
        void Warning(string text) { log.Write(text, true); warnings.Add(text); }
        void InvalidContent(string text, Exception error)
        {
            log.WriteError(text, error);
            warnings.Add(text);
        }
        client ??= new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(10), ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = Timeout.InfiniteTimeSpan };
        usageClient ??= new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(5) }) { Timeout = Timeout.InfiniteTimeSpan };
        try
        {
            var store = new JsonStore<AppSettings>(Path.Combine(dataDirectory, "settings.json"), () => new(), s => s.Validate(), Warning, InvalidContent);
            var settings = await store.LoadAsync();
            log.Enabled = settings.EnableLogging;
            var events = new JsonStore<EventState>(Path.Combine(dataDirectory, "events.json"), () => new(), s => s.Validate(), Warning, InvalidContent);
            var ledgerStore = new JsonStore<RequestLedger>(Path.Combine(dataDirectory, "requests.json"), () => new(), s => s.Validate(), Warning, InvalidContent);
            var state = await events.LoadAsync();
            var ledger = await ledgerStore.LoadAsync();
            var monitor = new MonitoringService(client, settings, new EventProcessor(events, state), ledgerStore, ledger, log.Write);
            usageEndpoint ??= await LoadUsageEndpointAsync(Warning);
            var identityStore = new JsonStore<Guid>(Path.Combine(dataDirectory, "usage-client.json"), Guid.NewGuid,
                id => { if (id == Guid.Empty) throw new ArgumentException("统计设备标识不能为空。"); }, Warning, InvalidContent);
            var reporter = new UsageReporter(usageClient, identityStore, usageEndpoint,
                typeof(AppController).Assembly.GetName().Version?.ToString() ?? "1.0.0", log.Write);
            var controller = new AppController(client, store, log, settings, monitor, dispatcher, usageClient, reporter, usageEndpoint.IsConfigured);
            if (warnings.Count > 0)
                controller.ViewModel.SetFeedback(string.Join("；", warnings));
            monitor.StatusChanged += controller.OnStatusChanged;
            log.Write("程序启动；独立 HttpClient 监控");
            return controller;
        }
        catch (Exception error)
        {
            // 此处已知真实日志开关；必须在释放前记录，启动层不另建默认开启的日志器。
            log.WriteError("控制器初始化失败", error);
            client.Dispose();
            usageClient.Dispose();
            await log.DisposeAsync();
            throw;
        }
    }
    private static async Task<UsageEndpoint> LoadUsageEndpointAsync(Action<string> warning)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "usage-endpoint.json");
        if (!File.Exists(path)) return new UsageEndpoint();
        try
        {
            await using var stream = File.OpenRead(path);
            var endpoint = await JsonSerializer.DeserializeAsync<UsageEndpoint>(stream) ?? throw new JsonException("空统计配置");
            endpoint.Validate();
            return endpoint;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // 发布配置可能包含上报密钥，不能将原始内容或异常参数写进日志。
            warning("统计服务器配置无法使用，当前不发送统计数据。请检查 usage-endpoint.json。");
            return new UsageEndpoint();
        }
    }
    public void Attach(MainWindow window, bool showTray = true)
    {
        main = window;
        notifications = new NotificationService(dispatcher, () => Settings, Log);
        Monitor.EventDetected += notifications.Enqueue;
        if (showTray) tray = new TrayService(this);
    }
    private void OnStatusChanged(MonitorStatus value)
    {
        if (Exiting || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (Exiting) return;
            ViewModel.Apply(Monitor.Status, Settings); tray?.Update(Monitor.Status);
            if (Log.LastWriteError is { } error) ViewModel.SetFeedback(error);
        });
    }
    public async Task RunAsync(Func<Task> action)
    {
        if (Exiting) return;
        try
        {
            await action();
        }
        catch (Exception e)
        {
            ReportOperationError(e);
        }
    }
    internal void ReportOperationError(Exception error)
    {
        Log.WriteError("操作失败", error);
        ViewModel.SetFeedback("操作失败，请查看日志后重试。");
    }
    public async Task SaveSettingsAsync(AppSettings settings)
    {
        settings.Validate();
        var oldStartup = StartupService.Read();
        var changedStartup = settings.StartWithWindows != Settings.StartWithWindows;
        try
        {
            if (changedStartup)
                StartupService.Set(settings.StartWithWindows);
            await settingsStore.SaveAsync(settings);
        }
        catch
        {
            if (changedStartup)
            {
                try
                {
                    StartupService.Restore(oldStartup);
                }
                catch (Exception e)
                {
                    Log.WriteError("开机启动设置回滚失败", e);
                }
            }

            throw;
        }
        Settings = settings; Log.Enabled = settings.EnableLogging;
        notifications?.ApplySettings();
        await Monitor.UpdateSettingsAsync(settings);
        await usageReporter.ApplyAsync(settings.EnableUsageReporting);
        ViewModel.Apply(Monitor.Status, settings); ViewModel.SetFeedback("设置已保存并生效。"); Log.Write("设置保存成功");
    }
    public void ShowMain()
    {
        if (Exiting || main is null) return;
        main.Show(); main.WindowState = WindowState.Normal; main.Activate();
    }
    public async Task StartAsync(bool forceTray = false)
    {
        if (!Settings.SilentStart && !forceTray) ShowMain();
        await usageReporter.ApplyAsync(Settings.EnableUsageReporting);
        if (Settings.SilentStart || Settings.AutoStartMonitoring) await Monitor.StartAsync();
    }
    public void OpenSettings()
    {
        if (Exiting) return;
        try
        {
            ShowMain();
            if (settingsWindow is not null)
            {
                settingsWindow.Activate();
                return;
            }

            settingsWindow = new SettingsWindow(this)
            {
                Owner = main
            };
            settingsWindow.ShowDialog();
        }
        catch (Exception e)
        {
            Log.WriteError("设置窗口失败", e);
            ViewModel.SetFeedback("设置窗口无法打开，请查看日志。");
        }
        finally
        {
            settingsWindow = null;
        }
    }
    public void OpenLogs()
    {
        try
        {
            Directory.CreateDirectory(Log.DirectoryPath);
            Process.Start(new ProcessStartInfo(Log.DirectoryPath) { UseShellExecute = true });
        }
        catch (Exception e)
        {
            Log.WriteError("日志目录打开失败", e);
            ViewModel.SetFeedback("无法打开日志目录：" + Log.DirectoryPath);
        }
    }
    public Task ExitAsync() => exitTask ??= ExitCoreAsync();
    private async Task ExitCoreAsync() { await DisposeAsync(); ExitRequested?.Invoke(); }
    public ValueTask DisposeAsync() => new(disposalTask ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        Exiting = true;
        ViewModel.Apply(Monitor.Status, Settings);
        await Monitor.DisposeAsync(); Monitor.StatusChanged -= OnStatusChanged;
        await usageReporter.DisposeAsync();
        if (notifications is not null) Monitor.EventDetected -= notifications.Enqueue;
        // 监控取消完成后才释放窗口、连接和日志，保持退出时的资源归属与顺序。
        notifications?.Dispose(); tray?.Dispose(); client.Dispose(); usageClient.Dispose();
        settingsWindow?.Close(); main?.Close(); Log.Write("程序退出"); await Log.DisposeAsync();
    }
}
