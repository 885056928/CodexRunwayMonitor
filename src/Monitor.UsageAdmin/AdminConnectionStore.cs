using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Monitor.Core;

namespace Monitor.UsageAdmin;

public sealed record AdminConnection(string ServerAddress, string AdminKey);

public sealed class AdminConnectionStore : IStateStore<AdminConnection?>
{
    public AdminConnectionStore(string path)
    {
        this.path = path;
        encryptedStore = new JsonStore<string?>(path, () => null);
    }

    private readonly string path;
    private readonly JsonStore<string?> encryptedStore;

    public async Task<AdminConnection?> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return null;
        var encrypted = await encryptedStore.LoadAsync(ct);
        if (encrypted is null) throw new InvalidDataException("连接配置为空。");
        var plaintext = ProtectedData.Unprotect(Convert.FromBase64String(encrypted), null, DataProtectionScope.CurrentUser);
        try
        {
            var connection = JsonSerializer.Deserialize<AdminConnection>(plaintext)
                ?? throw new InvalidDataException("连接配置为空。");
            Validate(connection);
            return connection;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public async Task SaveAsync(AdminConnection? value, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        Validate(value);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(value);
        try
        {
            // 成对加密地址与密钥，避免配置被修改后将已记住的密钥发送到另一个地址。
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            await encryptedStore.SaveAsync(Convert.ToBase64String(encrypted), ct);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void Validate(AdminConnection connection)
    {
        UsageEndpoint.ValidateServerUrl(connection.ServerAddress);
        UsageEndpoint.ValidateKey(connection.AdminKey);
    }
}
