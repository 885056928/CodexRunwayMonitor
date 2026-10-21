using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Monitor.Core;

public sealed class ApiService(HttpClient client, Uri baseUri, TimeSpan timeout)
{
    public async Task<ResetEvent?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri.AbsoluteUri.TrimEnd('/') + "/records/latest?kind=reset_completed"));
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            await response.Content.LoadIntoBufferAsync(1_048_576, deadline.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                TimeSpan? retry = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is { } date)
                    retry = date - DateTimeOffset.UtcNow;
                string code = "request_failed";
                Exception? detailsError = null;
                try
                {
                    using var error = JsonDocument.Parse(body);
                    if (error.RootElement.TryGetProperty("error", out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var s = value.GetString();
                        if (s is { Length: < 80 } && s.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                            code = s;
                    }

                    if (error.RootElement.TryGetProperty("retryAfter", out var seconds) && seconds.TryGetDouble(out var number) && double.IsFinite(number) && number >= 0)
                    {
                        var fromBody = TimeSpan.FromSeconds(Math.Min(number, 604800));
                        if (retry is null || fromBody > retry)
                            retry = fromBody;
                    }
                }
                catch (Exception e) when (e is JsonException or InvalidOperationException)
                {
                    // 网关可能返回非 JSON；仍保留 HTTP 状态和原始解析原因供日志定位。
                    detailsError = e;
                }

                if (response.StatusCode == HttpStatusCode.TooManyRequests && (retry is null || retry < TimeSpan.Zero))
                    retry = TimeSpan.FromMinutes(5);
                throw new ApiException($"HTTP {(int)response.StatusCode} / {code}", (int)response.StatusCode, retry, detailsError);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.GetProperty("ok").ValueKind != JsonValueKind.True)
                throw new ApiException("API 返回失败状态");
            var data = root.GetProperty("data");
            if (data.ValueKind == JsonValueKind.Null)
                return null;
            var id = data.GetProperty("id").GetString();
            var kind = data.GetProperty("kind").GetString();
            var resetType = data.GetProperty("resetType").GetString();
            if (string.IsNullOrWhiteSpace(id) || id.Length > 256 || kind != "reset_completed" || string.IsNullOrWhiteSpace(resetType))
                throw new ApiException("事件 ID、kind 或 resetType 无效");
            var effective = ReadTime(data.GetProperty("effectiveAt"));
            var announced = data.TryGetProperty("announcedAt", out var a) ? ReadTime(a) : null;
            var text = data.TryGetProperty("text", out var t) && t.ValueKind != JsonValueKind.Null ? t.GetString() : null;
            return new ResetEvent(id, kind, resetType, effective, announced, text, data.Clone());
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("API 请求超时", e);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new ApiException("API JSON 格式或字段无效（未更新事件状态）", null, null, e);
        }
    }

    private static DateTimeOffset? ReadTime(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        var s = value.GetString();
        if (s is null || !s.EndsWith('Z') || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
            throw new ApiException("API 时间字段不是有效 UTC 时间");
        return time;
    }
}
