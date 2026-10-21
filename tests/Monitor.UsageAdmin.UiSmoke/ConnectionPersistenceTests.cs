using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Controls;
using Monitor.UsageAdmin;

internal static class ConnectionPersistenceTests
{
    public static async Task RunAsync(string output)
    {
        var directory = Path.Combine(Path.GetTempPath(), "MonitorAdminPersistence-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "connection.json");
            var store = new AdminConnectionStore(path);
            var handler = new Program.SnapshotHandler { Status = HttpStatusCode.Unauthorized };
            using var http = new HttpClient(handler);
            var address = "https://usage.example.test";
            var key = new string('k', 40);
            await using (var first = new AdminViewModel(http, store))
            {
                await first.LoadConnectionAsync();
                Program.Check(first.ServerAddress == "" && first.AdminKey == "" && !File.Exists(path) && handler.RequestCount == 0,
                    "首次读取不创建连接配置或发出请求");
                first.ServerAddress = address;
                first.AdminKey = key;
                first.ConnectCommand.Execute(null);
                await Program.UntilAsync(() => !first.IsBusy && first.HasError);
                Program.Check(!File.Exists(path), "鉴权失败不保存地址和密钥");
                handler.Status = HttpStatusCode.OK;
                first.RefreshCommand.Execute(null);
                await Program.UntilAsync(() => !first.IsBusy);
                Program.Check(File.Exists(path) && !first.IsStale && !first.HasError, "首次有效快照后保存连接配置");
                var contents = await File.ReadAllTextAsync(path);
                Program.Check(!contents.Contains(key, StringComparison.Ordinal) && !contents.Contains(address, StringComparison.Ordinal),
                    "本地文件中没有明文密钥或服务器地址");
                var loaded = await new AdminConnectionStore(path).LoadAsync();
                Program.Check(loaded?.ServerAddress == address && loaded.AdminKey == key, "真实Windows当前用户加密可恢复完整连接信息");
                first.RefreshCommand.Execute(null);
                await Program.UntilAsync(() => !first.IsBusy);
                Program.Check(await File.ReadAllTextAsync(path) == contents, "正常快照刷新不会反复写入连接文件");
                await first.DisconnectAsync();
                Program.Check(first.AdminKey.Length == 0 && File.Exists(path), "断开清空当前内存密钥并保留加密配置");
            }

            var saved = await File.ReadAllBytesAsync(path);
            await using (var reopened = new AdminViewModel(http, new AdminConnectionStore(path)))
            {
                var previousRequests = handler.RequestCount;
                await reopened.LoadConnectionAsync();
                var window = new MainWindow(reopened);
                window.Show();
                try
                {
                    var password = (PasswordBox)window.FindName("AdminPassword");
                    Program.Check(reopened.ServerAddress == address && password.Password == key && reopened.AdminKey == key,
                        "重新打开自动填入地址和掩码密钥且PasswordBox不覆盖已载入值");
                    Program.Check(!reopened.IsConnected && !reopened.IsBusy && reopened.ConnectCommand.CanExecute(null) &&
                        handler.RequestCount == previousRequests,
                        "恢复配置后等待用户点击连接");
                    await Program.RenderAsync(window, Path.Combine(output, "remembered-connection.png"));
                    handler.Status = HttpStatusCode.Forbidden;
                    reopened.ServerAddress = "https://changed.example.test";
                    password.Password = new string('n', 40);
                    reopened.ConnectCommand.Execute(null);
                    await Program.UntilAsync(() => !reopened.IsBusy && reopened.HasError);
                    Program.Check((await File.ReadAllBytesAsync(path)).SequenceEqual(saved), "新地址或密钥连接失败不覆盖上次成功配置");
                    await reopened.DisconnectAsync();
                    Program.Check(password.Password.Length == 0 && reopened.AdminKey.Length == 0, "保存功能不改变断开时的界面密钥清理");
                    password.Password = key;
                    handler.HoldNext = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    reopened.ConnectCommand.Execute(null);
                    await Program.UntilAsync(() => reopened.IsBusy);
                    await reopened.DisconnectAsync();
                    Program.Check((await File.ReadAllBytesAsync(path)).SequenceEqual(saved), "连接中取消不会保存未经成功验证的配置");
                    handler.Status = HttpStatusCode.OK;
                    password.Password = new string('r', 40);
                    reopened.ConnectCommand.Execute(null);
                    await Program.UntilAsync(() => !reopened.IsBusy);
                    var replaced = await store.LoadAsync();
                    Program.Check(replaced?.ServerAddress == "https://changed.example.test" && replaced.AdminKey == new string('r', 40),
                        "新连接成功后地址和密钥成对更新");
                }
                finally
                {
                    window.Close();
                    await Program.UntilAsync(() => !window.IsVisible);
                }
            }

            await using (var saveFailure = new AdminViewModel(http, store))
            {
                await saveFailure.LoadConnectionAsync();
                var beforeFailure = await File.ReadAllBytesAsync(path);
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    saveFailure.ConnectCommand.Execute(null);
                    await Program.UntilAsync(() => !saveFailure.IsBusy);
                    Program.Check(saveFailure.IsConnected && !saveFailure.IsStale && !saveFailure.HasError &&
                        saveFailure.ConnectionNotice.Contains("保存失败", StringComparison.Ordinal),
                        "配置写入失败给出独立提示且不误报查询失败或快照过期");
                    Program.Check((await File.ReadAllBytesAsync(path)).SequenceEqual(beforeFailure), "写入失败时原有连接配置保持完整");
                }
                saveFailure.RefreshCommand.Execute(null);
                await Program.UntilAsync(() => !saveFailure.IsBusy);
                Program.Check(saveFailure.ConnectionNotice.Contains("已保存", StringComparison.Ordinal), "本地写入故障解除后刷新会重试保存");
            }
            Program.Check(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "保存失败清理临时文件");

            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                await using var readFailure = new AdminViewModel(http, store);
                await readFailure.LoadConnectionAsync();
                Program.Check(readFailure.ConnectionNotice.Contains("读取失败", StringComparison.Ordinal) &&
                    readFailure.CanEditConnection && readFailure.AdminKey.Length == 0,
                    "配置读取故障保留手动输入和连接能力");
            }

            await File.WriteAllTextAsync(path, "{");
            await using (var backup = new AdminViewModel(http, store))
            {
                await backup.LoadConnectionAsync();
                Program.Check(backup.AdminKey.Length == 40 && backup.ServerAddress.StartsWith("https://", StringComparison.Ordinal),
                    "JSON损坏时复用已有备份恢复机制");
            }

            // 使用有效JSON、无效DPAPI内容，确保实际走解密失败路径而非仅覆盖JSON解析错误。
            await File.WriteAllTextAsync(path, JsonSerializer.Serialize("AAAA"));
            await using (var corrupt = new AdminViewModel(http, store))
            {
                await corrupt.LoadConnectionAsync();
                Program.Check(corrupt.AdminKey.Length == 0 && corrupt.ServerAddress.Length == 0 &&
                    corrupt.ConnectionNotice.Contains("读取失败", StringComparison.Ordinal) && !corrupt.IsConnected,
                    "无法解密的本地文件不会恢复半份配置或自动连接");
            }
        }
        finally
        {
            // 目录由本测试创建，不接触用户真实配置。
            Directory.Delete(directory, true);
        }
    }
}
