namespace Monitor.Core;

public sealed record UsageEndpoint
{
    public string ServerUrl { get; init; } = "";
    public string ReportKey { get; init; } = "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(ReportKey);

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ServerUrl) && string.IsNullOrWhiteSpace(ReportKey)) return;
        ValidateServerUrl(ServerUrl);
        ValidateKey(ReportKey);
    }

    public static Uri ValidateServerUrl(string address)
    {
        // 仅本机联调允许明文，避免发布时将统计密钥和公网IP经HTTP传输。
        if (!Uri.TryCreate(address?.Trim(), UriKind.Absolute, out var uri)
            || !(uri.Scheme == "https" || uri.Scheme == "http" && uri.IsLoopback)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("统计服务器须使用 HTTPS（本机联调允许 HTTP），且不含账号、查询参数或片段。");
        return uri;
    }

    public static void ValidateKey(string key)
    {
        if (key is not { Length: >= 32 and <= 128 } || !key.All(character => character is >= '!' and <= '~'))
            throw new ArgumentException("统计密钥须为 32–128 位非空白 ASCII 字符。");
    }

    internal static Uri Resource(Uri endpoint, string path) => new(endpoint.AbsoluteUri.TrimEnd('/') + "/" + path);
}
