using System.Windows.Input;

namespace Monitor.App;

public partial class NotificationWindow : Window
{
    public NotificationWindow(ResetEvent value, int autoCloseSeconds, Action<string> log)
        : this(value, autoCloseSeconds, log, TimeProvider.System)
    {
    }

    public NotificationWindow(ResetEvent value, int autoCloseSeconds, Action<string> log, TimeProvider clock)
    {
        InitializeComponent();
        this.log = log;
        this.clock = clock;
        autoCloseDelay = TimeSpan.FromSeconds(autoCloseSeconds);
        ResetTime.Text = Display.ResetTime(value); AnnouncedTime.Text = "公告时间：" + Display.Time(value.AnnouncedAt); EventId.Text = "事件 ID：" + value.Id;
        CloseHint.Text = autoCloseSeconds > 0
            ? $"{autoCloseSeconds} 秒后自动关闭；未确认则 {NotificationService.ReminderIntervalMinutes} 分钟后再次提醒。"
            : "请点击“知道了”确认；关闭窗口后仍会再次提醒。";
        Loaded += OnLoaded;
        Closed += OnClosed;
        PreviewKeyDown += OnPreviewKeyDown;
    }

    private readonly TimeProvider clock;
    private readonly TimeSpan autoCloseDelay;
    private ITimer? timer;
    private readonly Action<string> log;
    private bool automatic;
    public bool IsAcknowledged { get; private set; }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ScreenPlacement.CenterPrimary(this);
        if (timer is null && autoCloseDelay > TimeSpan.Zero)
            timer = clock.CreateTimer(OnAutoCloseTimer, null, autoCloseDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnAutoCloseTimer(object? state)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.BeginInvoke(AutomaticallyClose);
        }
        catch (InvalidOperationException error)
        {
            log("提醒自动关闭派发失败：" + error);
        }
    }

    private void AutomaticallyClose()
    {
        if (!IsVisible) return;
        automatic = true;
        SafeClose();
    }

    private void Dismiss_Click(object sender, RoutedEventArgs e)
    {
        // 只有明确操作确认按钮才算已读；超时、标题栏和 Esc 关闭均保留待确认状态。
        IsAcknowledged = true;
        SafeClose();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        SafeClose();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        timer?.Dispose();
        log(IsAcknowledged ? "用户确认提醒" : automatic ? "提醒自动关闭，尚未确认" : "用户关闭提醒，尚未确认");
    }

    private void SafeClose()
    {
        try
        {
            Close();
        }
        catch (InvalidOperationException error)
        {
            log("提醒窗口关闭失败：" + error);
        }
    }
}
