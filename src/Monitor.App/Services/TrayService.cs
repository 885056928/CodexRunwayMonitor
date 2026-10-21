using System.Collections.Generic;
using System.Drawing;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace Monitor.App;

public sealed class TrayService : IDisposable
{
    public TrayService(AppController controller)
    {
        menu.Items.Add(new Forms.ToolStripMenuItem("CodexRunway Monitor", null, (_, _) => controller.ShowMain()));
        menu.Items.AddRange([state, checkTime, successTime, resetTime, new Forms.ToolStripSeparator()]);
        // 托盘与主窗口复用同一命令，忙碌状态、可执行条件和异常边界保持一致。
        Add("立即检查", controller.ViewModel.CheckCommand);
        Add("开始监控", controller.ViewModel.StartCommand);
        Add("暂停监控", controller.ViewModel.PauseCommand);
        Add("打开设置", controller.ViewModel.SettingsCommand);
        Add("打开日志目录", controller.ViewModel.LogsCommand);
        menu.Items.Add(new Forms.ToolStripSeparator()); Add("退出程序", controller.ViewModel.ExitCommand);
        icon = new Forms.NotifyIcon { Text = "CodexRunway Monitor", Icon = pausedIcon, ContextMenuStrip = menu, Visible = true };
        icon.DoubleClick += (_, _) => controller.ShowMain();
        Update(controller.Monitor.Status);
    }

    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ContextMenuStrip menu = new();
    private readonly Forms.ToolStripMenuItem state = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem checkTime = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem successTime = new() { Enabled = false };
    private readonly Forms.ToolStripMenuItem resetTime = new() { Enabled = false };
    private readonly List<(Forms.ToolStripMenuItem Item, ICommand Command)> commandItems = [];
    private readonly Icon runningIcon = LoadApplicationIcon();
    private readonly Icon pausedIcon = (Icon)SystemIcons.Application.Clone();
    private readonly Icon errorIcon = (Icon)SystemIcons.Warning.Clone();

    private static Icon LoadApplicationIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/CodexRunwayMonitor;component/Resources/AppIcon.ico"))
            ?? throw new FileNotFoundException("找不到程序图标资源。");
        using var stream = resource.Stream;
        using var source = new Icon(stream);
        // 克隆后再关闭资源流，使托盘图标的生命周期独立，并由 Dispose 统一释放。
        return (Icon)source.Clone();
    }

    private void Add(string label, ICommand command)
    {
        var item = new Forms.ToolStripMenuItem(label) { Enabled = command.CanExecute(null) };
        item.Click += (_, _) => command.Execute(null);
        command.CanExecuteChanged += OnCanExecuteChanged;
        commandItems.Add((item, command));
        menu.Items.Add(item);
    }

    private void OnCanExecuteChanged(object? sender, EventArgs e)
    {
        foreach (var (item, command) in commandItems) item.Enabled = command.CanExecute(null);
    }
    public void Update(MonitorStatus s)
    {
        state.Text = "当前状态：" + (s.Running ? "监控中" : "已暂停") + (s.Error ? " · 检查失败" : s.Busy ? " · 正在检查" : "");
        checkTime.Text = "上次检查：" + Display.Time(s.LastCheck); successTime.Text = "上次成功：" + Display.Time(s.LastSuccess);
        resetTime.Text = "最新重置：" + Display.ResetTime(s.Latest);
        icon.Icon = s.Error ? errorIcon : s.Running ? runningIcon : pausedIcon;
        icon.Text = "CodexRunway · " + (s.Error ? "检查失败" : s.Running ? "监控中" : "已暂停");
    }
    public void Dispose()
    {
        foreach (var (_, command) in commandItems) command.CanExecuteChanged -= OnCanExecuteChanged;
        commandItems.Clear();
        icon.Visible = false;
        icon.Dispose();
        menu.Dispose();
        runningIcon.Dispose();
        pausedIcon.Dispose();
        errorIcon.Dispose();
    }
}
