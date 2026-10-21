using System.Net.Http;
using System.Windows;

namespace Monitor.UsageAdmin;

public partial class App : Application
{
    private HttpClient? httpClient;
    private AdminViewModel? viewModel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // 管理密钥只发往用户选择的地址；每次请求的总时限由 Core 统一控制。
        httpClient = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(5)
        })
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        viewModel = new AdminViewModel(httpClient);
        MainWindow = new MainWindow(viewModel);
        MainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 窗口关闭时已经等待会话退出，HttpClient 最后释放，避免后台请求访问已释放连接。
        httpClient?.Dispose();
        base.OnExit(e);
    }
}
