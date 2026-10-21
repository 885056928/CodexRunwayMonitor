using System.Windows.Threading;

namespace Monitor.App;

public partial class App : Application
{
    private Mutex? mutex;
    private EventWaitHandle? activation;
    private RegisteredWaitHandle? activationWait;
    private AppController? controller;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            // 单实例资源创建也可能失败，必须与其余启动步骤共用异常边界。
            mutex = new Mutex(true, @"Local\CodexRunwayMonitor.Instance", out var first);
            if (!first)
            {
                using var signal = EventWaitHandle.OpenExisting(@"Local\CodexRunwayMonitor.Activate");
                signal.Set();
                Shutdown();
                return;
            }

            activation = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CodexRunwayMonitor.Activate");
            activationWait = ThreadPool.RegisterWaitForSingleObject(activation, (_, _) => Dispatcher.BeginInvoke(() => controller?.ShowMain()), null, Timeout.Infinite, false);
            if (SystemParameters.HighContrast)
            {
                Resources["BackgroundBrush"] = SystemColors.WindowBrush;
                Resources["SurfaceBrush"] = SystemColors.WindowBrush;
                Resources["InkBrush"] = SystemColors.WindowTextBrush;
                Resources["MutedBrush"] = SystemColors.WindowTextBrush;
                Resources["AccentBrush"] = SystemColors.HighlightBrush;
                Resources["BorderBrush"] = SystemColors.WindowTextBrush;
                Resources["DangerBrush"] = SystemColors.WindowTextBrush;
            }

            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexRunwayMonitor");
            controller = await AppController.CreateAsync(folder);
            controller.ExitRequested += () => Shutdown();
            var main = new MainWindow(controller);
            MainWindow = main;
            controller.Attach(main);
            await controller.StartAsync(e.Args.Contains("--tray", StringComparer.OrdinalIgnoreCase));
            DispatcherUnhandledException += OnDispatcherError;
        }
        catch (Exception ex)
        {
            await HandleStartupFailureAsync(ex);
        }
    }

    private async Task HandleStartupFailureAsync(Exception exception)
    {
        // 控制器创建失败已由 CreateAsync 在原日志器释放前记录；更早的资源故障使用系统诊断。
        controller?.Log.WriteError("程序启动失败", exception);
        System.Diagnostics.Trace.TraceError("程序启动或激活失败：{0}", exception);
        try
        {
            if (controller is not null)
                await controller.DisposeAsync();
        }
        catch (Exception cleanupError)
        {
            controller?.Log.WriteError("启动失败后的清理失败", cleanupError);
            System.Diagnostics.Trace.TraceError("启动失败后的清理失败：{0}", cleanupError);
        }

        var message = exception is WaitHandleCannotBeOpenedException
            ? "无法激活已运行的程序，请稍后从系统托盘打开。"
            : "程序无法启动，请检查本地应用数据目录的权限和可用空间。";
        var window = new Window
        {
            Title = "CodexRunway Monitor 启动失败",
            Width = 520,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new TextBlock { Text = message + "\n\n错误类型：" + exception.GetType().Name, Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap }
        };
        window.Closed += (_, _) => Shutdown(1);
        window.Show();
    }
    private void OnDispatcherError(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        controller?.Log.WriteError("界面异常", e.Exception);
        controller?.ViewModel.SetFeedback("界面操作失败，请查看日志后重试。");
        e.Handled = true;
    }
    protected override void OnExit(ExitEventArgs e)
    {
        activationWait?.Unregister(null); activation?.Dispose(); mutex?.Dispose(); base.OnExit(e);
    }
}
