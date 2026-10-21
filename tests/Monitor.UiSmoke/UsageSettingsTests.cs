using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Threading;
using Monitor.App;
using Monitor.Core;

internal static class UsageSettingsTests
{
    public static async Task RunAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "MonitorUsageUi-" + Guid.NewGuid());
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = false;
        var calls = 0;
        using var originalHttp = new HttpClient(new RejectHandler());
        using var usageHttp = new HttpClient(new UsageHandler(async ct =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            finally
            {
                cancelled = ct.IsCancellationRequested;
            }
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        }));
        await using var controller = await AppController.CreateAsync(temp, originalHttp, usageHttp,
            new UsageEndpoint { ServerUrl = "https://usage.example.test", ReportKey = new string('r', 32) });
        try
        {
            await controller.SaveSettingsAsync(controller.Settings with { AutoStartMonitoring = false });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Check(calls == 1 && !controller.Monitor.Status.Running, "Usage lifecycle is independent of paused original monitoring");
            Check(controller.ViewModel.UsageNotice.Contains("公网 IP") && controller.ViewModel.UsageNotice.Contains("设置中关闭"), "Main notice discloses source IP and opt-out");
            var window = new SettingsWindow(controller);
            var editing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Dispatcher.CurrentDispatcher.BeginInvoke(async () =>
            {
                try
                {
                    var viewModel = (SettingsViewModel)window.DataContext;
                    var checkbox = (CheckBox)window.FindName("UsageReporting");
                    Check(checkbox.IsChecked == true, "Usage checkbox defaults enabled and binds the editable draft");
                    checkbox.IsChecked = false;
                    Check(controller.Settings.EnableUsageReporting, "Unsaved usage preference does not change active reporting");
                    await viewModel.SaveCommand.ExecuteAsync();
                    editing.SetResult();
                }
                catch (Exception error)
                {
                    editing.SetException(error);
                    window.Close();
                }
            });
            Check(window.ShowDialog() == true, "Usage opt-out saves through the actual modal settings window");
            await editing.Task;
            Check(cancelled && calls == 1 && !controller.Settings.EnableUsageReporting, "Saving opt-out cancels and awaits the active heartbeat");
            Check(controller.ViewModel.UsageNotice.Contains("已关闭"), "Main notice updates immediately after opt-out");
            var persisted = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(Path.Combine(temp, "settings.json")));
            Check(persisted?.EnableUsageReporting == false, "Opt-out is persisted for subsequent launches");
            await controller.DisposeAsync();
        }
        finally
        {
            await controller.DisposeAsync();
            Directory.Delete(temp, true);
        }
    }

    private static void Check(bool value, string message)
    {
        if (!value) throw new Exception(message);
        Console.WriteLine("PASS " + message);
    }
    private sealed class RejectHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Original API must remain idle in usage test");
    }
    private sealed class UsageHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(ct);
    }
}
