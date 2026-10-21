using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Monitor.Core;

namespace Monitor.UsageServer;

internal sealed class UsageKeys
{
    public UsageKeys(string? reportKey, string? adminKey)
    {
        try
        {
            // 与普通客户端、管理客户端共享ASCII边界，防止服务接受客户端无法发送的密钥。
            UsageEndpoint.ValidateKey(reportKey!);
            UsageEndpoint.ValidateKey(adminKey!);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException("Usage:ReportKey 和 Usage:AdminKey 必须是32–128位非空白ASCII密钥。", exception);
        }
        if (reportKey!.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || adminKey!.Contains("CHANGE_ME", StringComparison.OrdinalIgnoreCase)
            || string.Equals(reportKey, adminKey, StringComparison.Ordinal))
            throw new InvalidOperationException("上报与管理密钥必须不同，且不能包含CHANGE_ME占位内容。");
        reportHash = SHA256.HashData(Encoding.UTF8.GetBytes(reportKey!));
        adminHash = SHA256.HashData(Encoding.UTF8.GetBytes(adminKey!));
    }

    private readonly byte[] reportHash;
    private readonly byte[] adminHash;

    public bool Authorize(HttpRequest request, bool admin)
    {
        var values = request.Headers.Authorization;
        if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var header)
            || !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrEmpty(header.Parameter) || header.Parameter.Length > 128)
            return false;

        // 先转为固定长度摘要，再常量时间比较，避免普通字符串比较泄漏前缀信息。
        var actual = SHA256.HashData(Encoding.UTF8.GetBytes(header.Parameter));
        return CryptographicOperations.FixedTimeEquals(actual, admin ? adminHash : reportHash);
    }

}
