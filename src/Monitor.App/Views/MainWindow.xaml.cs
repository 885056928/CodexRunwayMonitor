using System.ComponentModel;

namespace Monitor.App;

public partial class MainWindow : Window
{
    public MainWindow(AppController controller)
    {
        InitializeComponent(); this.controller = controller; DataContext = controller.ViewModel;
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized && controller.Settings.MinimizeToTray) Hide(); };
        Loaded += (_, _) => ScreenPlacement.Clamp(this);
    }

    private readonly AppController controller;
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (controller.Exiting) return;
        e.Cancel = true;
        if (controller.Settings.MinimizeToTray) Hide();
        // Closing 回调内再次 Close 会重入；等本次关闭事件结束后再执行可处理异常的退出命令。
        else Dispatcher.BeginInvoke(ExecuteExitCommand);
    }

    private void ExecuteExitCommand() => controller.ViewModel.ExitCommand.Execute(null);
}
