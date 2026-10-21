using System.Text.Json;

namespace Monitor.Core;

public interface IStateStore<T> { Task<T> LoadAsync(CancellationToken ct = default); Task SaveAsync(T value, CancellationToken ct = default); }
public sealed class JsonStore<T>(string path, Func<T> defaults, Action<T>? validate = null, Action<string>? warning = null) : IStateStore<T>
{
    public JsonStore(string path, Func<T> defaults, Action<T>? validate, Action<string>? warning, Action<string, Exception> invalidContent)
        : this(path, defaults, validate, warning)
    {
        this.invalidContent = invalidContent;
    }

    private readonly Action<string, Exception>? invalidContent;
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };
    public async Task<T> LoadAsync(CancellationToken ct = default)
    {
        foreach (var candidate in new[] { path, path + ".bak" })
        {
            if (!File.Exists(candidate)) continue;
            try
            {
                await using var stream = new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
                var value = await JsonSerializer.DeserializeAsync<T>(stream, Options, ct).ConfigureAwait(false) ?? throw new JsonException("空 JSON");
                validate?.Invoke(value);
                if (candidate != path)
                    warning?.Invoke("已从备份恢复 " + Path.GetFileName(path));
                return value;
            }
            catch (Exception e) when (e is JsonException or ArgumentException)
            {
                // 仅内容损坏允许尝试备份；权限或 I/O 故障必须向上传播，不能当成空状态。
                var message = "文件内容无效，保留故障副本：" + Path.GetFileName(candidate);
                // UI 只展示恢复提示，诊断回调单独保留异常链，避免把堆栈放进状态栏。
                if (invalidContent is not null) invalidContent(message, e);
                else warning?.Invoke(message + "；原因：" + e);
                File.Move(candidate, candidate + ".corrupt-" + Guid.NewGuid().ToString("N"));
            }
        }
        var fallback = defaults(); validate?.Invoke(fallback);
        await SaveAsync(fallback, ct).ConfigureAwait(false);
        return fallback;
    }
    public async Task SaveAsync(T value, CancellationToken ct = default)
    {
        validate?.Invoke(value);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, Options, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(true);
            }

            ct.ThrowIfCancellationRequested();
            // 在同目录原子替换并保留上一版，避免中途退出留下半个 JSON 文件。
            if (File.Exists(path))
                File.Replace(temp, path, path + ".bak", true);
            else
                File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }
    }
}
