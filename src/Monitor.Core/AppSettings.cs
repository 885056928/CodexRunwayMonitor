namespace Monitor.Core;

public sealed record AppSettings
{
    // 界面输入与持久化校验共用边界，避免修改设置范围时两处规则不一致。
    public const int MinimumCheckIntervalMinutes = 3;
    public const int MaximumCheckIntervalMinutes = 1440;
    public const int MinimumRequestTimeoutSeconds = 5;
    public const int MaximumRequestTimeoutSeconds = 120;
    public const int MinimumAutoCloseNotificationSeconds = 0;
    public const int MaximumAutoCloseNotificationSeconds = 3600;

    public string ApiBaseUrl { get; init; } = "https://www.codexrunway.com/openapi/v1";
    public int CheckIntervalMinutes { get; init; } = 5;
    public int RequestTimeoutSeconds { get; init; } = 30;
    public bool AutoStartMonitoring { get; init; } = true;
    public bool SilentStart { get; init; }
    public bool StartWithWindows { get; init; }
    public bool MinimizeToTray { get; init; } = true;
    public bool EnableNotification { get; init; } = true;
    public bool EnableSound { get; init; } = true;
    public bool EnableLogging { get; init; } = true;
    public bool EnableUsageReporting { get; init; } = true;
    public int AutoCloseNotificationSeconds { get; init; } = 10;
    public bool FirstRunSilent { get; init; } = true;

    public void Validate()
    {
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("API 地址须为 HTTPS，且不包含用户名、密码、查询参数或片段。");
        if (CheckIntervalMinutes is < MinimumCheckIntervalMinutes or > MaximumCheckIntervalMinutes)
            throw new ArgumentException($"检查间隔须为 {MinimumCheckIntervalMinutes}–{MaximumCheckIntervalMinutes} 分钟。");
        if (RequestTimeoutSeconds is < MinimumRequestTimeoutSeconds or > MaximumRequestTimeoutSeconds)
            throw new ArgumentException($"请求超时须为 {MinimumRequestTimeoutSeconds}–{MaximumRequestTimeoutSeconds} 秒。");
        if (AutoCloseNotificationSeconds is < MinimumAutoCloseNotificationSeconds or > MaximumAutoCloseNotificationSeconds)
            throw new ArgumentException($"自动关闭须为 {MinimumAutoCloseNotificationSeconds}–{MaximumAutoCloseNotificationSeconds} 秒，0 表示手动关闭。");
    }
}
