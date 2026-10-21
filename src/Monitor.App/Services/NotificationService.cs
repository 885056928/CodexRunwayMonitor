using System.Collections.Generic;
using System.Media;
using System.Windows.Threading;

namespace Monitor.App;

public sealed class NotificationService : IDisposable
{
    public NotificationService(Dispatcher dispatcher, Func<AppSettings> settings, LoggingService log)
        : this(dispatcher, settings, log, TimeProvider.System)
    {
    }

    public NotificationService(Dispatcher dispatcher, Func<AppSettings> settings, LoggingService log, TimeProvider clock)
    {
        this.dispatcher = dispatcher;
        this.settings = settings;
        this.log = log;
        this.clock = clock;
    }

    public const int ReminderIntervalMinutes = 15;

    private readonly Dispatcher dispatcher;
    private readonly Func<AppSettings> settings;
    private readonly LoggingService log;
    private readonly TimeProvider clock;
    private readonly Queue<ResetEvent> queue = new();
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> unacknowledged = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ITimer> reminders = new(StringComparer.Ordinal);
    private NotificationWindow? active;
    private bool disposed;

    public void Enqueue(ResetEvent value)
    {
        if (disposed || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(() =>
        {
            if (disposed) return;
            if (!settings().EnableNotification)
            {
                ApplySettings();
                return;
            }
            if (!seen.Add(value.Id)) return;
            unacknowledged.Add(value.Id);
            queue.Enqueue(value);
            ShowNext();
        });
    }

    public void ApplySettings()
    {
        if (disposed || settings().EnableNotification) return;
        // 关闭通知视为放弃本轮待提醒；重新开启时不补发已处理的旧事件。
        ClearPending();
    }

    private void ShowNext()
    {
        if (disposed || active is not null || queue.Count == 0) return;
        if (!settings().EnableNotification)
        {
            ApplySettings();
            return;
        }
        var value = queue.Dequeue();
        try
        {
            var window = new NotificationWindow(value, settings().AutoCloseNotificationSeconds, message => log.Write(message), clock);
            active = window;
            window.Closed += (_, _) => OnWindowClosed(window, value);
            window.Show();
            log.Write("显示重置提醒");
            if (settings().EnableSound)
            {
                try
                {
                    SystemSounds.Asterisk.Play();
                }
                catch (Exception e)
                {
                    log.WriteError("系统声音失败", e);
                }
            }
        }
        catch (Exception e)
        {
            active = null;
            log.WriteError("提醒窗口创建失败", e);
            ScheduleReminder(value);
            dispatcher.BeginInvoke(ShowNext);
        }
    }

    private void OnWindowClosed(NotificationWindow window, ResetEvent value)
    {
        if (ReferenceEquals(active, window)) active = null;
        if (disposed) return;
        if (window.IsAcknowledged) unacknowledged.Remove(value.Id);
        else ScheduleReminder(value);
        dispatcher.BeginInvoke(ShowNext);
    }

    private void ScheduleReminder(ResetEvent value)
    {
        if (disposed) return;
        if (!settings().EnableNotification)
        {
            ApplySettings();
            return;
        }
        if (!unacknowledged.Contains(value.Id)) return;
        if (reminders.Remove(value.Id, out var previous)) previous.Dispose();
        // 每次未确认关闭后重新计时，而不是从首次弹窗起累积固定周期。
        reminders[value.Id] = clock.CreateTimer(OnReminderTimer, value, TimeSpan.FromMinutes(ReminderIntervalMinutes), Timeout.InfiniteTimeSpan);
        log.Write($"事件 {value.Id} 尚未确认，{ReminderIntervalMinutes} 分钟后再次提醒");
    }

    private void OnReminderTimer(object? state)
    {
        if (dispatcher.HasShutdownStarted) return;
        try
        {
            // 时钟回调可能运行在线程池；队列、窗口和待确认状态只在 UI 线程访问。
            dispatcher.BeginInvoke(() => Repeat((ResetEvent)state!));
        }
        catch (InvalidOperationException error)
        {
            log.WriteError("再次提醒派发失败", error);
        }
    }

    private void Repeat(ResetEvent value)
    {
        if (disposed || !reminders.Remove(value.Id, out var timer)) return;
        timer.Dispose();
        if (!settings().EnableNotification)
        {
            ApplySettings();
            return;
        }
        if (!unacknowledged.Contains(value.Id)) return;
        // 重提醒复用原事件，不重新请求 API，也不改变事件持久化去重状态。
        queue.Enqueue(value);
        ShowNext();
    }

    private void ClearPending()
    {
        queue.Clear();
        unacknowledged.Clear();
        foreach (var timer in reminders.Values) timer.Dispose();
        reminders.Clear();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ClearPending();
        try
        {
            active?.Close();
        }
        catch (Exception e)
        {
            log.WriteError("提醒关闭失败", e);
        }
    }
}
