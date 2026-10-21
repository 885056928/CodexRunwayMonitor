using System.ComponentModel;

namespace Monitor.App;

public static class Display
{
    public static string Time(DateTimeOffset? value) => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz") ?? "—";
    public static string ResetTime(ResetEvent? value) => value is null ? "等待首次成功检查" : value.EffectiveAt is null ? "实际重置时间未知" : Time(value.EffectiveAt);
}
public sealed class MainViewModel : INotifyPropertyChanged
{
    public MainViewModel(AppController controller)
    {
        this.controller = controller;
        CheckCommand = new AsyncRelayCommand(ExecuteCheckAsync, controller.ReportOperationError, CanExecuteCheck);
        StartCommand = new AsyncRelayCommand(ExecuteStartAsync, controller.ReportOperationError, CanExecuteStart);
        PauseCommand = new AsyncRelayCommand(ExecutePauseAsync, controller.ReportOperationError, CanExecutePause);
        SettingsCommand = new RelayCommand(ExecuteOpenSettings, CanInteract);
        LogsCommand = new RelayCommand(ExecuteOpenLogs, CanInteract);
        ExitCommand = new AsyncRelayCommand(ExecuteExitAsync, controller.ReportOperationError, CanInteract);
    }

    #region 属性

    private readonly AppController controller;
    private MonitorStatus status = new();
    private AppSettings settings = new();
    public string State => status.Running ? "监控中" : "已暂停";
    public string Interval => $"每 {settings.CheckIntervalMinutes} 分钟检查";
    public string UsageNotice => !settings.EnableUsageReporting
        ? "使用统计已关闭，不发送统计数据。可在设置中重新开启。"
        : controller.UsageEndpointReady
            ? "使用统计已开启：向统计服务器发送随机设备标识和版本，服务器记录公网 IP；可在设置中关闭。"
            : "使用统计默认开启；尚未配置统计服务器，当前不发送数据。可在设置中关闭。";
    public string LastCheck => Display.Time(status.LastCheck);
    public string LastSuccess => Display.Time(status.LastSuccess);
    public string NextCheck => status.Running ? Display.Time(status.NextCheck) : "暂停期间不自动检查";
    public string Network => status.Message;
    public string LatestTime => Display.ResetTime(status.Latest);
    public string Announcement => Display.Time(status.Latest?.AnnouncedAt);
    public string EventId => status.Latest?.Id ?? "—";
    public string Kind => status.Latest?.Kind ?? "—";
    public string ResetType => status.Latest?.ResetType ?? "—";
    public string Summary => status.Latest?.Text ?? "暂无来源说明";
    public bool CanCheck => !status.Busy && CanInteract();
    public bool CanStart => !status.Running && CanInteract();
    public bool CanPause => (status.Running || status.Busy) && CanInteract();
    public bool HasError => status.Error;
    public string Feedback { get; private set; } = "关闭窗口后可在系统托盘继续运行。";
    public event PropertyChangedEventHandler? PropertyChanged;

    #endregion

    #region 命令

    public AsyncRelayCommand CheckCommand { get; }
    public AsyncRelayCommand StartCommand { get; }
    public AsyncRelayCommand PauseCommand { get; }
    public RelayCommand SettingsCommand { get; }
    public RelayCommand LogsCommand { get; }
    public AsyncRelayCommand ExitCommand { get; }

    #endregion

    #region 方法

    public void Apply(MonitorStatus value, AppSettings config)
    {
        status = value;
        settings = config;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
        CheckCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged();
        SettingsCommand.NotifyCanExecuteChanged();
        LogsCommand.NotifyCanExecuteChanged();
        ExitCommand.NotifyCanExecuteChanged();
    }

    public void SetFeedback(string text)
    {
        Feedback = text;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Feedback)));
    }

    private bool CanInteract() => !controller.Exiting;
    private bool CanExecuteCheck() => CanCheck;
    private bool CanExecuteStart() => CanStart;
    private bool CanExecutePause() => CanPause;
    private Task ExecuteCheckAsync() => controller.Monitor.CheckAsync();
    private Task ExecuteStartAsync() => controller.Monitor.StartAsync();
    private Task ExecutePauseAsync() => controller.Monitor.PauseAsync();
    private Task ExecuteExitAsync() => controller.ExitAsync();
    private void ExecuteOpenSettings() => controller.OpenSettings();
    private void ExecuteOpenLogs() => controller.OpenLogs();

    #endregion
}
