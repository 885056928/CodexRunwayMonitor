using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Monitor.App;
using Monitor.Core;

internal static class NotificationReminderTests
{
    public static async Task RunAsync(string directory, string screenshotPath, Action<bool, string> check)
    {
        var clock = new TestClock();
        var settings = new AppSettings { EnableSound = false };
        await using var log = new LoggingService(Path.Combine(directory, "Logs"));
        using var service = new NotificationService(Dispatcher.CurrentDispatcher, () => settings, log, clock);

        service.Enqueue(Event("repeat"));
        await FlushAsync();
        check(Windows().Length == 1, "New event opens exactly one notification");
        await Program.Render(Windows().Single(), screenshotPath);
        clock.Advance(TimeSpan.FromSeconds(9));
        await FlushAsync();
        check(Windows().Length == 1, "Notification remains visible before the ten-second deadline");
        clock.Advance(TimeSpan.FromSeconds(1));
        await FlushAsync();
        check(Windows().Length == 0, "Unacknowledged notification closes at ten seconds");

        clock.Advance(TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1));
        await FlushAsync();
        check(Windows().Length == 0, "Reminder does not appear before fifteen minutes after automatic close");
        clock.Advance(TimeSpan.FromSeconds(1));
        await FlushAsync();
        check(Windows().Length == 1 && EventId(Windows().Single()) == "事件 ID：repeat", "Unacknowledged event reappears at the fifteen-minute deadline");
        clock.Advance(TimeSpan.FromSeconds(10));
        await FlushAsync();
        clock.Advance(TimeSpan.FromMinutes(15));
        await FlushAsync();
        check(Windows().Length == 1, "Automatic dismissal continues the reminder cycle until acknowledgement");
        Acknowledge(Windows().Single());
        await FlushAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await FlushAsync();
        check(Windows().Length == 0 && clock.PendingTimers == 0, "Explicit acknowledgement stops further reminders and releases timers");

        settings = settings with { AutoCloseNotificationSeconds = 0 };
        service.Enqueue(Event("title-close"));
        await FlushAsync();
        Windows().Single().Close();
        await FlushAsync();
        service.Enqueue(Event("new-event"));
        await FlushAsync();
        var newer = Windows().Single();
        clock.Advance(TimeSpan.FromMinutes(15));
        await FlushAsync();
        check(Windows().Length == 1 && ReferenceEquals(Windows().Single(), newer), "Due reminder queues behind an already visible notification");
        Acknowledge(newer);
        await FlushAsync();
        check(Windows().Length == 1 && EventId(Windows().Single()) == "事件 ID：title-close", "Title-bar dismissal remains unacknowledged and is reminded after the active event");
        Acknowledge(Windows().Single());
        await FlushAsync();

        service.Enqueue(Event("escape"));
        await FlushAsync();
        var escapeWindow = Windows().Single();
        escapeWindow.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(escapeWindow), 0, Key.Escape)
        {
            RoutedEvent = Keyboard.PreviewKeyDownEvent
        });
        await FlushAsync();
        clock.Advance(TimeSpan.FromMinutes(15));
        await FlushAsync();
        check(Windows().Length == 1 && EventId(Windows().Single()) == "事件 ID：escape", "Escape dismisses without acknowledging and still schedules a reminder");
        Acknowledge(Windows().Single());
        await FlushAsync();

        settings = settings with { AutoCloseNotificationSeconds = 10 };
        service.Enqueue(Event("disabled-active"));
        service.Enqueue(Event("disabled-queued"));
        await FlushAsync();
        settings = settings with { EnableNotification = false };
        service.ApplySettings();
        settings = settings with { EnableNotification = true };
        service.ApplySettings();
        Windows().Single().Close();
        await FlushAsync();
        clock.Advance(TimeSpan.FromHours(1));
        await FlushAsync();
        check(Windows().Length == 0 && clock.PendingTimers == 0, "Disabling notifications drops active and queued unacknowledged reminders without replay");

        service.Enqueue(Event("disabled-timer"));
        await FlushAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await FlushAsync();
        // 先让定时回调排入 Dispatcher，再禁用通知，覆盖已派发回调的取消边界。
        clock.Advance(TimeSpan.FromMinutes(15));
        settings = settings with { EnableNotification = false };
        service.ApplySettings();
        settings = settings with { EnableNotification = true };
        service.ApplySettings();
        clock.Advance(TimeSpan.FromMinutes(15));
        await FlushAsync();
        check(Windows().Length == 0 && clock.PendingTimers == 0, "Disabling notifications also cancels an already scheduled reminder timer");

        service.Enqueue(Event("exit"));
        await FlushAsync();
        clock.Advance(TimeSpan.FromSeconds(10));
        await FlushAsync();
        check(clock.PendingTimers > 0, "Automatic close owns a pending reminder timer");
        service.Enqueue(Event("exit-active"));
        await FlushAsync();
        clock.Advance(TimeSpan.FromMinutes(15));
        service.Dispose();
        clock.Advance(TimeSpan.FromHours(1));
        await FlushAsync();
        check(Windows().Length == 0 && clock.PendingTimers == 0, "Disposal cancels pending reminders without reopening windows");
    }

    private static ResetEvent Event(string id)
        => new(id, "reset_completed", "global", null, null, null, JsonSerializer.SerializeToElement(new { id }));

    private static NotificationWindow[] Windows() => Application.Current.Windows.OfType<NotificationWindow>().ToArray();
    private static string EventId(NotificationWindow window) => ((TextBlock)window.FindName("EventId")).Text;
    private static async Task FlushAsync() => await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);

    private static void Acknowledge(NotificationWindow window)
    {
        Program.InvokeButton(Program.Descendants<Button>(window).Single());
    }
}
