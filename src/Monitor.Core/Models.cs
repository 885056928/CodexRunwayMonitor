using System.Text.Json;

namespace Monitor.Core;

public sealed record ResetEvent(string Id, string Kind, string ResetType, DateTimeOffset? EffectiveAt,
    DateTimeOffset? AnnouncedAt, string? Text, JsonElement Raw);

public sealed class ApiException : Exception
{
    public ApiException(string message, int? statusCode = null, TimeSpan? retryAfter = null)
        : this(message, statusCode, retryAfter, null)
    {
    }

    public ApiException(string message, int? statusCode, TimeSpan? retryAfter, Exception? innerException)
        : base(message, innerException)
    {
        StatusCode = statusCode;
        RetryAfter = retryAfter;
    }

    public int? StatusCode { get; }
    public TimeSpan? RetryAfter { get; }
}
