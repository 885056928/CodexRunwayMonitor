using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Monitor.Core;
using Monitor.UsageAdmin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/CodexUsageAdmin;component/Resources/Theme.xaml")
        });
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var frame = new DispatcherFrame();
        var result = 1;
        Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
        {
            try
            {
                await RunAsync(Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/usage-admin-ui-smoke"));
                result = 0;
            }
            catch (Exception error)
            {
                Console.WriteLine("FAIL " + error);
            }
            finally
            {
                frame.Continue = false;
            }
        });
        Dispatcher.PushFrame(frame);
        return result;
    }

    private static async Task RunAsync(string output)
    {
        Directory.CreateDirectory(output);
        await ConnectionPersistenceTests.RunAsync(output);
        var handler = new SnapshotHandler();
        using var http = new HttpClient(handler);
        await using var viewModel = new AdminViewModel(http);
        var window = new MainWindow(viewModel);
        window.Show();
        try
        {
            var address = (TextBox)window.FindName("ServerAddress");
            var key = (PasswordBox)window.FindName("AdminPassword");
            var table = (DataGrid)window.FindName("ClientsGrid");
            Check(window.FontSize == 14 && window.FontFamily.Source == "Microsoft YaHei UI", "管理端复用主题与中文字体");
            Check(window.MinWidth == 900 && window.MinHeight == 600, "窗口保留可用的最小尺寸");
            Check(!viewModel.IsConnected && table.Items.Count == 0, "首次打开未连接且不展示伪造数据");
            Check(table.IsReadOnly && table.Columns.Count == 4, "在线列表只读且展示四个必要字段");
            await RenderAsync(window, Path.Combine(output, "disconnected.png"));

            address.Text = "https://usage.example.test";
            key.Password = new string('k', 40);
            var releaseLoading = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.HoldNext = releaseLoading;
            Invoke(window, "ConnectButton");
            await UntilAsync(() => viewModel.IsBusy);
            Check(!((Button)window.FindName("RefreshButton")).IsEnabled, "连接中禁用重复刷新");
            await RenderAsync(window, Path.Combine(output, "loading.png"));
            releaseLoading.SetResult();
            await UntilAsync(() => !viewModel.IsBusy && table.Items.Count == 50);
            Check(viewModel.IsConnected && viewModel.OnlineClients == "51" && viewModel.OnlineIps == "2", "真实WPF绑定显示客户端数与不同IP数");
            Check(viewModel.Clients[0].IpAddress == "203.0.113.1" && viewModel.Clients[0].Version == "1.2.3", "表格显示服务返回的IP与版本");
            Check(!viewModel.IsStale && viewModel.Page == 1 && viewModel.HasNextPage, "首次快照有效并可前往下一页");
            Check(viewModel.ServerTime.Contains("2026-10-21", StringComparison.Ordinal) && viewModel.LastRefresh != "尚未刷新",
                "最后刷新时间与服务器时间已绑定");
            await RenderAsync(window, Path.Combine(output, "connected.png"));

            handler.Version = "2.0.0";
            await UntilAsync(() => !viewModel.IsBusy && viewModel.Clients[0].Version == "2.0.0", 15000);
            Check(viewModel.Clients[0].Version == "2.0.0", "连接后定时刷新更新可见数据");

            Invoke(window, "NextPageButton");
            await UntilAsync(() => !viewModel.IsBusy && viewModel.Page == 2);
            Check(table.Items.Count == 1 && !viewModel.HasNextPage, "下一页获取尾页且正确禁用翻页");
            Invoke(window, "PreviousPageButton");
            await UntilAsync(() => !viewModel.IsBusy && viewModel.Page == 1);
            Check(table.Items.Count == 50, "上一页恢复第一页");

            handler.Status = HttpStatusCode.BadGateway;
            Invoke(window, "RefreshButton");
            await UntilAsync(() => !viewModel.IsBusy && viewModel.IsStale);
            Check(table.Items.Count == 50 && viewModel.OnlineClients == "51" && viewModel.HasError, "刷新错误保留最后成功结果并明确过期");
            Check(!viewModel.Status.Contains(new string('k', 40), StringComparison.Ordinal), "错误文本不泄露管理密钥");
            window.Width = 900;
            window.Height = 600;
            await RenderAsync(window, Path.Combine(output, "error-minimum.png"));

            handler.Status = HttpStatusCode.OK;
            handler.Empty = true;
            Invoke(window, "RefreshButton");
            await UntilAsync(() => !viewModel.IsBusy && !viewModel.IsStale);
            Check(table.Items.Count == 0 && viewModel.OnlineClients == "0" && !viewModel.HasNextPage, "空快照清空列表并展示零在线");
            await RenderAsync(window, Path.Combine(output, "empty.png"));

            Invoke(window, "DisconnectButton");
            await UntilAsync(() => viewModel.CanEditConnection);
            Check(!viewModel.IsConnected && key.Password.Length == 0 && viewModel.AdminKey.Length == 0, "断开停止会话并清空界面和内存密钥");

            key.Password = new string('z', 40);
            handler.Empty = false;
            handler.HoldNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke(window, "ConnectButton");
            await UntilAsync(() => viewModel.IsBusy);
            Invoke(window, "DisconnectButton");
            await UntilAsync(() => viewModel.CanEditConnection);
            Check(!viewModel.IsConnected && viewModel.Clients.Count == 0 && key.Password.Length == 0,
                "请求进行中断开会取消请求且迟到响应不会覆盖结果");

            foreach (var invalidKey in new[] { new string('密', 40), new string(' ', 40) })
            {
                key.Password = invalidKey;
                Invoke(window, "ConnectButton");
                await UntilAsync(() => !viewModel.IsBusy && viewModel.HasError);
                Check(!viewModel.IsConnected && key.Password == invalidKey,
                    "密钥含非ASCII或空白时保留输入并阻止建立刷新会话");
            }

            address.Text = "http://public.example.test";
            key.Password = new string('v', 40);
            Invoke(window, "ConnectButton");
            await UntilAsync(() => !viewModel.IsBusy && viewModel.HasError);
            Check(!viewModel.IsConnected && key.Password.Length == 40, "拒绝公网HTTP地址并保留输入供修正");

            address.Text = "https://usage.example.test";
            handler.HoldNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Invoke(window, "ConnectButton");
            await UntilAsync(() => viewModel.IsBusy);
            window.Close();
            await UntilAsync(() => !window.IsVisible);
            Check(key.Password.Length == 0 && viewModel.AdminKey.Length == 0 && !viewModel.IsConnected,
                "关闭真实窗口会等待请求取消并清空密钥");
        }
        finally
        {
            if (window.IsVisible)
            {
                window.Close();
                await UntilAsync(() => !window.IsVisible);
            }
        }
    }

    private static void Invoke(Window window, string name)
    {
        var button = (Button)window.FindName(name);
        Check(button.IsEnabled, name + " 可执行");
        button.Command!.Execute(button.CommandParameter);
    }

    internal static async Task UntilAsync(Func<bool> predicate, int timeoutMilliseconds = 5000)
    {
        using var timeout = new CancellationTokenSource(timeoutMilliseconds);
        while (!predicate())
        {
            await Task.Delay(15, timeout.Token);
        }
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        Console.WriteLine("PASS " + message);
    }

    internal static async Task RenderAsync(Window window, string path)
    {
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
        var root = (FrameworkElement)VisualTreeHelper.GetChild(window, 0);
        var image = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
        image.Render(root);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    internal sealed class SnapshotHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Empty { get; set; }
        public string Version { get; set; } = "1.2.3";
        public TaskCompletionSource? HoldNext { get; set; }
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            // 仅替换网络边界；窗口、命令、HTTP客户端和绑定均执行生产实现。
            if (HoldNext is { } pending)
            {
                HoldNext = null;
                await pending.Task.WaitAsync(cancellationToken);
            }
            if (Status != HttpStatusCode.OK) return new HttpResponseMessage(Status);
            var page = request.RequestUri!.Query.TrimStart('?').Split('&').Contains("page=2") ? 2 : 1;
            var count = Empty ? 0 : page == 1 ? 50 : 1;
            var clients = Enumerable.Range((page - 1) * 50 + 1, count)
                .Select(index => new UsageClient(new Guid(index, 0, 0, new byte[8]),
                    index % 2 == 1 ? "203.0.113.1" : "203.0.113.2", Version, DateTimeOffset.Parse("2026-10-21T08:00:00Z")))
                .ToArray();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new UsageSnapshot(DateTimeOffset.Parse("2026-10-21T08:00:05Z"),
                    Empty ? 0 : 51, Empty ? 0 : 2, page, 50, clients))
            };
        }
    }
}
