using System.Net.Http.Headers;
using System.Text.Json;

namespace Monitor.Core;

public sealed class UsageAdminClient
{
    public UsageAdminClient(HttpClient client) => this.client = client;

    private readonly HttpClient client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int MaximumResponseBytes = 1024 * 1024;

    public async Task<UsageSnapshot> GetSnapshotAsync(Uri endpoint, string adminKey, int page, int pageSize, CancellationToken ct = default)
    {
        UsageEndpoint.ValidateServerUrl(endpoint.AbsoluteUri);
        UsageEndpoint.ValidateKey(adminKey);
        if (page < 1 || pageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(page));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint.Resource(endpoint, $"api/usage/clients?page={page}&pageSize={pageSize}"));
        // 令牌只属于此次请求，不能污染同一个HttpClient的其他调用。
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminKey);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, deadline.Token).ConfigureAwait(false);
        var snapshot = JsonSerializer.Deserialize<UsageSnapshot>(await response.Content.ReadAsByteArrayAsync(deadline.Token).ConfigureAwait(false), JsonOptions);
        if (snapshot is null || snapshot.Clients is null || snapshot.OnlineClients < 0 || snapshot.OnlineIps < 0
            || snapshot.OnlineIps > snapshot.OnlineClients || snapshot.Page != page || snapshot.PageSize != pageSize || snapshot.Clients.Length > pageSize
            || snapshot.Clients.Any(item => item is null || item.InstallationId == Guid.Empty || item.IpAddress is null || item.Version is null))
            throw new JsonException("统计服务器返回了无效快照。");
        return snapshot;
    }
}
