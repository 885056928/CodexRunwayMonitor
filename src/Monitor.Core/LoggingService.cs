using System.Threading.Channels;

namespace Monitor.Core;

public sealed class LoggingService : IAsyncDisposable
{
    public LoggingService(string directory) { DirectoryPath = directory; writer = Task.Run(WriteLoopAsync); }

    private readonly Channel<(DateTimeOffset Time, string Message, bool Error)> queue = Channel.CreateBounded<(DateTimeOffset, string, bool)>(new BoundedChannelOptions(1024) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    private readonly Task writer;
    public bool Enabled { get; set; } = true;
    public string DirectoryPath { get; }
    public string? LastWriteError { get; private set; }
    public void Write(string message, bool error = false)
    { if (Enabled) queue.Writer.TryWrite((DateTimeOffset.Now, message.Replace('\r', ' ').Replace('\n', ' '), error)); }
    public void WriteError(string context, Exception exception)
    {
        // 保留异常链与堆栈；沿用单行日志清洗，防止换行破坏日志记录边界。
        Write(context + "：" + exception, true);
    }
    private async Task WriteLoopAsync()
    {
        await foreach (var entry in queue.Reader.ReadAllAsync())
        {
            try
            {
                var folder = Path.Combine(DirectoryPath, entry.Time.ToString("yyyy-MM-dd"));
                Directory.CreateDirectory(folder);
                var line = $"{entry.Time:O} [{(entry.Error ? "ERROR" : "INFO")}] {entry.Message}{Environment.NewLine}";
                await File.AppendAllTextAsync(Path.Combine(folder, "app.log"), line).ConfigureAwait(false);
                if (entry.Error)
                    await File.AppendAllTextAsync(Path.Combine(folder, "error.log"), line).ConfigureAwait(false);
                LastWriteError = null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                LastWriteError = "日志无法写入：" + e.Message;
                // 日志目录不可写时不能再向同一队列追加错误，改用系统诊断输出保留堆栈。
                System.Diagnostics.Trace.TraceError("日志写入失败（{0}）：{1}", DirectoryPath, e);
            }
        }
    }
    public async ValueTask DisposeAsync() { queue.Writer.TryComplete(); await writer.ConfigureAwait(false); }
}
