using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Monitor.App;
using Monitor.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/CodexRunwayMonitor;component/Resources/Theme.xaml") });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame(); int result = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(args.FirstOrDefault(a => !a.StartsWith("--")) ?? Path.GetFullPath("artifacts/ui-smoke"), args.Contains("--extended"), args.Contains("--startup-registry"));
                result = 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("FAIL " + ex);
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return result;
    }
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static async Task Until(Func<bool> predicate) { using var ct = new CancellationTokenSource(5000); while (!predicate()) await Task.Delay(15, ct.Token); }
    internal static async Task Render(Window window, string path)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path); encoder.Save(file);
    }
    private static async Task RunAsync(string output, bool extended, bool startupRegistry)
    {
        Directory.CreateDirectory(output);
        var temp = Path.Combine(Path.GetTempPath(), "MonitorUiSmoke-" + Guid.NewGuid());
        string id = "smoke-baseline"; HttpStatusCode status = HttpStatusCode.OK;
        var client = new HttpClient(new Handler(() => new HttpResponseMessage(status) { Content = new StringContent("{\"ok\":true,\"data\":{\"id\":\"" + id + "\",\"kind\":\"reset_completed\",\"resetType\":\"global\",\"effectiveAt\":null,\"announcedAt\":\"2026-09-12T08:09:17.000Z\",\"text\":\"测试来源说明：实际重置时间为空时，不使用公告时间代替。\"}}") }));
        await using var controller = await AppController.CreateAsync(temp, client);
        try
        {
            await controller.SaveSettingsAsync(controller.Settings with { EnableSound = false, AutoCloseNotificationSeconds = 1 });
            var main = new MainWindow(controller);
            controller.Attach(main);
            main.Show();
            Check(main.FontSize == 14 && main.FontFamily.Source == "Microsoft YaHei UI", "Derived WPF window uses shared theme and typography");
            await controller.Monitor.StartAsync();
            await Until(() => controller.Monitor.Status.LastSuccess.HasValue);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(controller.ViewModel.State == "监控中" && controller.ViewModel.LatestTime == "实际重置时间未知", "Main window shows monitoring and honest unknown time");
            Check(!Application.Current.Windows.OfType<NotificationWindow>().Any(), "First successful event is silent");
            await Render(main, Path.Combine(output, "main.png"));
            var settings = new SettingsWindow(controller)
            {
                Owner = main
            };
            var settingsActions = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var interval = (TextBox)settings.FindName("Interval");
                    var save = (Button)settings.FindName("SaveButton");
                    interval.Text = "0";
                    InvokeButton(save);
                    await Until(() => ((TextBlock)settings.FindName("ErrorText")).Text.Contains("3–1440"));
                    Check(((TextBlock)settings.FindName("ErrorText")).Text.Contains("3–1440"), "Settings validation preserves invalid input and displays correction");
                    await Render(settings, Path.Combine(output, "settings-validation.png"));
                    interval.Text = "7";
                    ((CheckBox)settings.FindName("SilentStart")).IsChecked = true;
                    InvokeButton(save);
                    settingsActions.SetResult();
                }
                catch (Exception error)
                {
                    settingsActions.SetException(error);
                    settings.Close();
                }
            });
            var saved = settings.ShowDialog();
            await settingsActions.Task;
            Check(saved == true && controller.Settings.CheckIntervalMinutes == 7, "Valid settings persist and close the modal");
            Check(controller.Settings.SilentStart, "Saving settings checkbox enables silent startup");
            await controller.Monitor.PauseAsync();
            id = "smoke-new";
            await controller.Monitor.CheckAsync();
            await Until(() => Application.Current.Windows.OfType<NotificationWindow>().Any());
            var notification = Application.Current.Windows.OfType<NotificationWindow>().Single();
            Check(notification.Topmost && notification.IsVisible, "New event opens visible topmost WPF notification");
            var bounds = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            var dpi = VisualTreeHelper.GetDpi(notification);
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(notification).Handle, out var rectangle);
            var center = new Point((rectangle.Left + rectangle.Right) / 2.0, (rectangle.Top + rectangle.Bottom) / 2.0);
            Check(Math.Abs(center.X - (bounds.Left + bounds.Width / 2.0)) < 4 * dpi.DpiScaleX && Math.Abs(center.Y - (bounds.Top + bounds.Height / 2.0)) < 4 * dpi.DpiScaleY, "Notification is centered in primary screen work area");
            await Render(notification, Path.Combine(output, "notification.png"));
            await Until(() => !Application.Current.Windows.OfType<NotificationWindow>().Any());
            Check(true, "Notification automatically closes");
            await controller.Monitor.CheckAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!Application.Current.Windows.OfType<NotificationWindow>().Any(), "Repeated event does not open another notification");
            status = HttpStatusCode.BadGateway;
            await controller.Monitor.CheckAsync();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(controller.ViewModel.HasError && controller.ViewModel.EventId == "smoke-new", "HTTP failure remains visible and preserves last valid event");
            main.Width = 640;
            main.Height = 520;
            await Render(main, Path.Combine(output, "main-narrow-error.png"));
            main.Close();
            Check(!main.IsVisible && !controller.Exiting, "Main close hides to tray without stopping application");
            controller.ShowMain();
            Check(main.IsVisible, "Main window restores from tray action");
            await controller.DisposeAsync();
            Check(!main.IsVisible && !controller.Monitor.Status.Running, "Exit closes windows, stops monitor and disposes tray");
            await using var closeController = await AppController.CreateAsync(Path.Combine(temp, "close-test"), new HttpClient(new Handler(() => new HttpResponseMessage())));
            await closeController.SaveSettingsAsync(closeController.Settings with { MinimizeToTray = false });
            var closeWindow = new MainWindow(closeController);
            closeController.Attach(closeWindow, false);
            closeWindow.Show();
            closeWindow.Close();
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            Check(!closeWindow.IsVisible, "Title-bar close while paused and tray disabled completes without recursive close");
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelController = await AppController.CreateAsync(Path.Combine(temp, "cancel-test"), new HttpClient(new AsyncHandler(async ct =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    await released.Task;
                    throw;
                }

                return new HttpResponseMessage();
            })));
            await cancelController.Monitor.StartAsync();
            await entered.Task;
            var firstExit = cancelController.DisposeAsync().AsTask();
            var secondExit = cancelController.DisposeAsync().AsTask();
            var bothWait = !firstExit.IsCompleted && !secondExit.IsCompleted;
            released.SetResult();
            await Task.WhenAll(firstExit, secondExit);
            Check(bothWait, "Repeated disposal waits for the same in-flight cancellation and log flush");
            await using var silentController = await AppController.CreateAsync(Path.Combine(temp, "silent-start"), new HttpClient(new Handler(() => new HttpResponseMessage { Content = new StringContent("{\"ok\":true,\"data\":null}") })));
            var silentSettings = new SettingsWindow(silentController);
            var silentOption = silentSettings.FindName("SilentStart") as CheckBox;
            Check(silentOption is not null, "Settings exposes silent startup option");
            var startupOption = (CheckBox)silentSettings.FindName("WindowsStartup");
            var optionsPanel = (StackPanel)startupOption.Parent;
            Check(optionsPanel.Children.IndexOf(silentOption!) + 1 == optionsPanel.Children.IndexOf(startupOption), "Silent startup appears immediately above Windows startup");
            silentSettings.Close();
            await silentController.SaveSettingsAsync(silentController.Settings with { SilentStart = true, AutoStartMonitoring = false });
            var savedSilent = await new JsonStore<AppSettings>(Path.Combine(temp, "silent-start", "settings.json"), () => new()).LoadAsync();
            Check(savedSilent.SilentStart, "Silent startup setting persists to JSON");
            var silentMain = new MainWindow(silentController);
            silentController.Attach(silentMain, false);
            await silentController.StartAsync();
            Check(!silentMain.IsVisible && silentController.Monitor.Status.Running, "Silent startup hides main window and starts monitoring even when auto-monitor is off");
            await Until(() => silentController.Monitor.Status.LastSuccess.HasValue);
            silentController.ShowMain();
            Check(silentMain.IsVisible, "Silent startup still allows opening main window from tray");
            await silentController.DisposeAsync();
            var legacy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AutoStartMonitoring\":false}")!;
            Check(!legacy.SilentStart, "Existing settings without SilentStart keep normal window startup");
            await using var normalController = await AppController.CreateAsync(Path.Combine(temp, "normal-start"), new HttpClient(new Handler(() => new HttpResponseMessage())));
            await normalController.SaveSettingsAsync(legacy);
            var normalMain = new MainWindow(normalController);
            normalController.Attach(normalMain, false);
            await normalController.StartAsync();
            Check(normalMain.IsVisible && !normalController.Monitor.Status.Running, "Disabled silent startup preserves visible paused startup preference");
            await normalController.DisposeAsync();
            await VerifyOperationDiagnosticsAsync(Path.Combine(temp, "operation-error"));
            await VerifyCheckCommandAsync(Path.Combine(temp, "command-test"));
            await VerifySettingsFailureAsync(Path.Combine(temp, "settings-failure"));
            await VerifyAsyncCommandErrorAsync();
            await VerifySettingsBusyAsync(Path.Combine(temp, "settings-busy"));
            await VerifyInitializationDiagnosticsAsync(Path.Combine(temp, "initialization-error"));
            await VerifyRecoveryFeedbackAsync(Path.Combine(temp, "recovery-feedback"));
            await NotificationReminderTests.RunAsync(Path.Combine(temp, "notification-reminder"), Path.Combine(output, "notification-reminder.png"), Check);
            await UsageSettingsTests.RunAsync();
            if (extended)
                await ExtendedAsync(output, temp);
            if (startupRegistry)
                TestStartupRegistry(output);
            Console.WriteLine("WPF smoke tests completed; snapshots: " + output);
        }
        finally
        {
            await controller.DisposeAsync();
            if (Directory.Exists(temp))
                Directory.Delete(temp, true);
        }
    }
    private sealed class Handler(Func<HttpResponseMessage> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromResult(response()); }
    private sealed class AsyncHandler(Func<CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => response(ct); }

    private static async Task VerifyOperationDiagnosticsAsync(string directory)
    {
        var controller = await AppController.CreateAsync(directory, new HttpClient(new Handler(() => new HttpResponseMessage())));
        try
        {
            await controller.RunAsync(() => throw new IOException("测试操作故障", new InvalidOperationException("测试内部原因")));
            Check(controller.ViewModel.Feedback.Contains("操作失败"), "Operation failure remains visible in the main view model");
        }
        finally
        {
            await controller.DisposeAsync();
        }
        var log = await File.ReadAllTextAsync(Directory.GetFiles(directory, "error.log", SearchOption.AllDirectories).Single());
        Check(log.Contains("测试操作故障") && log.Contains("测试内部原因") && log.Contains("RunAsync"), "Operation error log retains the cause and stack");
    }

    private static async Task VerifyCheckCommandAsync(string directory)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = new HttpClient(new AsyncHandler(async ct =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new HttpResponseMessage { Content = new StringContent("{\"ok\":true,\"data\":null}") };
        }));
        await using var controller = await AppController.CreateAsync(directory, client);
        var main = new MainWindow(controller);
        controller.Attach(main, false);
        main.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        try
        {
            var button = Descendants<Button>(main).Single(item => Equals(item.Content, "立即检查"));
            Check(button.Command is not null, "Main check action is available through a bound command");
            var command = button.Command!;
            command.Execute(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            command.Execute(null);
            Check(calls == 1 && !command.CanExecute(null), "Busy command rejects duplicate checks");
            release.TrySetResult();
            await Until(() => controller.Monitor.Status.LastSuccess.HasValue && command.CanExecute(null));
            Check(calls == 1, "Check command becomes available again after completion");
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static async Task VerifySettingsFailureAsync(string directory)
    {
        await using var controller = await AppController.CreateAsync(directory, new HttpClient(new Handler(() => new HttpResponseMessage())));
        var window = new SettingsWindow(controller);
        window.Show();
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var save = (Button)window.FindName("SaveButton");
        try
        {
            Check(save.Command is not null, "Settings save is available through a bound command");
            var fields = new[]
            {
                ("Interval", "1441", "3–1440"),
                ("TimeoutSeconds", "121", "5–120"),
                ("AutoClose", "3601", "0–3600")
            };
            foreach (var (name, invalid, range) in fields)
            {
                var field = (TextBox)window.FindName(name);
                var previous = field.Text;
                field.Text = invalid;
                InvokeButton(save);
                await Until(() => ((TextBlock)window.FindName("ErrorText")).Text.Contains(range));
                Check(field.IsKeyboardFocused && field.Text == invalid, "Invalid " + name + " is retained and focused");
                field.Text = previous;
            }

            var settingsPath = Path.Combine(directory, "settings.json");
            var preserved = settingsPath + ".preserved";
            File.Move(settingsPath, preserved);
            Directory.CreateDirectory(settingsPath);
            try
            {
                ((TextBox)window.FindName("Interval")).Text = "9";
                InvokeButton(save);
                await Until(() => ((TextBlock)window.FindName("ErrorText")).Text.Contains("保存失败"));
                Check(window.IsVisible && controller.Settings.CheckIntervalMinutes == 5 && ((TextBox)window.FindName("Interval")).Text == "9" && save.IsEnabled, "Failed settings save retains input and prior settings and allows retry");
            }
            finally
            {
                Directory.Delete(settingsPath);
                File.Move(preserved, settingsPath);
            }
        }
        finally
        {
            window.Close();
        }
    }

    internal static void InvokeButton(Button button)
    {
        var peer = new System.Windows.Automation.Peers.ButtonAutomationPeer(button);
        ((System.Windows.Automation.Provider.IInvokeProvider)peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
    }

    private static async Task VerifyAsyncCommandErrorAsync()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? reported = null;
        async Task FailAsync()
        {
            await release.Task;
            throw new IOException("测试命令故障", new InvalidOperationException("测试命令内部原因"));
        }
        void ReportError(Exception error) => reported = error;

        var command = new AsyncRelayCommand(FailAsync, ReportError);
        command.Execute(null);
        Check(!command.CanExecute(null), "Async command is disabled until the awaited operation ends");
        release.TrySetResult();
        await Until(() => reported is not null && command.CanExecute(null));
        Check(reported is IOException { InnerException: InvalidOperationException } && reported.StackTrace?.Contains("FailAsync") == true,
            "ICommand entry reports the original exception and restores availability");
    }

    private static async Task VerifyInitializationDiagnosticsAsync(string directory)
    {
        foreach (var enabled in new[] { false, true })
        {
            var data = Path.Combine(directory, enabled ? "enabled" : "disabled");
            await new JsonStore<AppSettings>(Path.Combine(data, "settings.json"), () => new()).SaveAsync(new AppSettings { EnableLogging = enabled });
            Directory.CreateDirectory(Path.Combine(data, "events.json"));
            Exception? failure = null;
            try
            {
                await using var unexpected = await AppController.CreateAsync(data, new HttpClient(new Handler(() => new HttpResponseMessage())));
            }
            catch (IOException error)
            {
                failure = error;
            }
            Check(failure is not null, "Initialization failure propagates without returning a controller");
            var logs = Path.Combine(data, "Logs");
            if (enabled)
            {
                var files = Directory.Exists(logs) ? Directory.GetFiles(logs, "error.log", SearchOption.AllDirectories) : [];
                Check(files.Length == 1 && (await File.ReadAllTextAsync(files[0])).Contains("JsonStore"), "Initialization error is logged before the logger is disposed");
            }
            else
            {
                Check(!Directory.Exists(logs) || Directory.GetFiles(logs, "*.log", SearchOption.AllDirectories).Length == 0,
                    "Initialization failure honors the loaded logging-off setting");
            }
        }
    }

    private static async Task VerifyRecoveryFeedbackAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "settings.json"), "{\"CheckIntervalMinutes\":0}");
        var controller = await AppController.CreateAsync(directory, new HttpClient(new Handler(() => new HttpResponseMessage())));
        try
        {
            Check(controller.ViewModel.Feedback.Contains("settings.json") && !controller.ViewModel.Feedback.Contains("ArgumentException"),
                "Recovery feedback names the file without exposing exception details");
        }
        finally
        {
            await controller.DisposeAsync();
        }
        var log = await File.ReadAllTextAsync(Directory.GetFiles(directory, "error.log", SearchOption.AllDirectories).Single());
        Check(log.Contains("检查间隔须为") && log.Contains("Validate"), "Recovery diagnostics retain the validation reason and stack");
    }

    private static async Task VerifySettingsBusyAsync(string directory)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        using var client = new HttpClient(new AsyncHandler(async ct =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    await release.Task;
                    throw;
                }
            }
            return new HttpResponseMessage { Content = new StringContent("{\"ok\":true,\"data\":null}") };
        }));
        await using var controller = await AppController.CreateAsync(directory, client);
        try
        {
            await controller.Monitor.StartAsync();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var viewModel = new SettingsViewModel(controller) { IntervalText = "9" };
            var save = viewModel.SaveCommand.ExecuteAsync();
            await Until(() => viewModel.IsSaving);
            Check(!viewModel.CanClose && !viewModel.SaveCommand.CanExecute(null), "Settings cannot close or submit again while cancellation is pending");
            release.TrySetResult();
            await save.WaitAsync(TimeSpan.FromSeconds(3));
            Check(!viewModel.IsSaving && viewModel.CanClose && viewModel.SaveCommand.CanExecute(null) && controller.Settings.CheckIntervalMinutes == 9,
                "Settings finish saving after monitor cancellation and restore controls");
        }
        finally
        {
            release.TrySetResult();
        }
    }
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out Rect rect);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr handle, IntPtr after, int x, int y, int width, int height, uint flags);

    private static async Task ExtendedAsync(string output, string temp)
    {
        await using var controller = await AppController.CreateAsync(Path.Combine(temp, "extended"), new HttpClient(new Handler(() => new HttpResponseMessage())));
        var main = new MainWindow(controller); controller.Attach(main, false); main.Show();
        var screens = System.Windows.Forms.Screen.AllScreens;
        Check(screens.Length >= 2, "Dual-monitor acceptance requires at least two connected displays");
        var topology = new System.Collections.Generic.List<object>();
        foreach (var screen in screens)
        {
            SetWindowPos(new System.Windows.Interop.WindowInteropHelper(main).Handle, IntPtr.Zero, screen.WorkingArea.Left + 40, screen.WorkingArea.Top + 40, 0, 0, 0x0001 | 0x0004);
            await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            var dpi = VisualTreeHelper.GetDpi(main);
            topology.Add(new { screen.DeviceName, screen.Primary, X = screen.Bounds.X, Y = screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height, Scale = dpi.DpiScaleX });
            Check(System.Windows.Forms.Screen.FromHandle(new System.Windows.Interop.WindowInteropHelper(main).Handle).DeviceName == screen.DeviceName, "Main window moved to " + screen.DeviceName);
            var value = new ResetEvent("dual-screen-" + screen.DeviceName, "reset_completed", "global", DateTimeOffset.UtcNow, null, null, System.Text.Json.JsonSerializer.SerializeToElement(new { test = true }));
            var notice = new NotificationWindow(value, 0, _ => { }) { Owner = main };
            notice.Show(); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
            GetWindowRect(new System.Windows.Interop.WindowInteropHelper(notice).Handle, out var r);
            var primary = System.Windows.Forms.Screen.PrimaryScreen!.WorkingArea;
            Check(Math.Abs((r.Left + r.Right) / 2.0 - (primary.Left + primary.Width / 2.0)) < 4 && Math.Abs((r.Top + r.Bottom) / 2.0 - (primary.Top + primary.Height / 2.0)) < 4,
                "Notification triggered from " + screen.DeviceName + " stays centered on primary screen");
            var dismiss = Descendants<Button>(notice).Single();
            Check(new System.Windows.Automation.Peers.ButtonAutomationPeer(dismiss).GetName() == "知道了", "Notification button exposes localized accessibility name");
            notice.Activate(); dismiss.Focus();
            Check(dismiss.IsKeyboardFocused, "Notification dismiss button accepts keyboard focus");
            ((System.Windows.Automation.Provider.IInvokeProvider)new System.Windows.Automation.Peers.ButtonAutomationPeer(dismiss).GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!).Invoke();
            await Until(() => !notice.IsVisible);
            Check(true, "Accessibility Invoke dismisses notification");
        }
        await File.WriteAllTextAsync(Path.Combine(output, "monitor-topology.json"), System.Text.Json.JsonSerializer.Serialize(topology, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        var settings = new SettingsWindow(controller) { Owner = main }; settings.Show();
        foreach (var field in Descendants<TextBox>(settings))
            Check(!string.IsNullOrWhiteSpace(new System.Windows.Automation.Peers.TextBoxAutomationPeer(field).GetName()), "Settings field has accessibility name: " + field.Name);
        var interval = (TextBox)settings.FindName("Interval"); settings.Activate(); interval.Focus();
        Check(interval.IsKeyboardFocused, "Settings input accepts keyboard focus");
        interval.MoveFocus(new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.Next));
        Check(((TextBox)settings.FindName("TimeoutSeconds")).IsKeyboardFocused, "Tab order moves from interval to timeout");
        await Render(settings, Path.Combine(output, "settings-accessibility.png")); settings.Close();
        await controller.DisposeAsync();
    }

    internal static System.Collections.Generic.IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T value) yield return value; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }

    private static void TestStartupRegistry(string output)
    {
        const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "CodexRunwayMonitor";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, true);
        var exists = key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        var previous = key.GetValue(valueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
        var kind = exists ? key.GetValueKind(valueName) : Microsoft.Win32.RegistryValueKind.None;
        File.WriteAllText(Path.Combine(output, "startup-registry-backup.json"), System.Text.Json.JsonSerializer.Serialize(new { Exists = exists, RawValue = previous, Kind = kind.ToString() }));
        try
        {
            StartupService.Set(true);
            Check(StartupService.Read() == "\"" + Environment.ProcessPath + "\" --tray", "Startup registration quotes executable and passes --tray");
            StartupService.Set(false);
            Check(StartupService.Read() is null, "Disabling startup removes only application Run value");
        }
        finally
        {
            if (exists)
                key.SetValue(valueName, previous!, kind);
            else
                key.DeleteValue(valueName, false);
        }
        var restoredExists = key.GetValueNames().Contains(valueName, StringComparer.OrdinalIgnoreCase);
        var restoredValue = key.GetValue(valueName, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames);
        Check(restoredExists == exists && (!exists || key.GetValueKind(valueName) == kind) &&
            System.Text.Json.JsonSerializer.Serialize(restoredValue) == System.Text.Json.JsonSerializer.Serialize(previous),
            "Original startup registration raw value and kind restored after test");
    }
}
