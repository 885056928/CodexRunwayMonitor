using System.Net;
using Monitor.Core;

namespace Monitor.UsageServer;

public sealed class UsageState
{
    public UsageState(TimeProvider timeProvider, int requestsPerMinute)
    {
        if (requestsPerMinute < 1) throw new ArgumentOutOfRangeException(nameof(requestsPerMinute));
        this.timeProvider = timeProvider;
        this.requestsPerMinute = requestsPerMinute;
    }

    private const int MaximumClients = 10000;
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan RequestWindow = TimeSpan.FromMinutes(1);
    private readonly object sync = new();
    private readonly TimeProvider timeProvider;
    private readonly int requestsPerMinute;
    private readonly Dictionary<Guid, UsageClient> clients = [];
    private readonly Dictionary<IPAddress, RequestCounter> requests = [];

    public bool TryRecord(UsageHeartbeat heartbeat, IPAddress address)
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            if (!clients.ContainsKey(heartbeat.InstallationId) && clients.Count >= MaximumClients)
            {
                RemoveExpiredClients(now);
                if (clients.Count >= MaximumClients) return false;
            }

            // 同一安装只保留最新地址，避免形成历史IP记录。
            clients[heartbeat.InstallationId] = new UsageClient(heartbeat.InstallationId, Normalize(address).ToString(), heartbeat.Version, now);
            return true;
        }
    }

    public bool TryAcquireRequest(IPAddress address)
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            address = Normalize(address);
            if (!requests.TryGetValue(address, out var counter) || now - counter.StartedAt >= RequestWindow)
            {
                // 限流状态也有容量边界，防止大量不同来源耗尽内存。
                if (counter is null && requests.Count >= MaximumClients)
                {
                    RemoveExpiredRequestCounters(now);
                    if (requests.Count >= MaximumClients) return false;
                }
                requests[address] = new RequestCounter(now, 1);
                return true;
            }
            if (counter.Count >= requestsPerMinute) return false;
            requests[address] = counter with { Count = counter.Count + 1 };
            return true;
        }
    }

    public UsageSnapshot Snapshot(int page, int pageSize)
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            RemoveExpiredClients(now);
            var offset = ((long)page - 1) * pageSize;
            UsageClient[] items = offset >= clients.Count
                ? []
                : clients.Values.OrderBy(client => client.InstallationId).Skip((int)offset).Take(pageSize).ToArray();
            return new UsageSnapshot(now, clients.Count, clients.Values.Select(client => client.IpAddress).Distinct(StringComparer.Ordinal).Count(), page, pageSize, items);
        }
    }

    public void RemoveExpired()
    {
        lock (sync)
        {
            var now = timeProvider.GetUtcNow();
            RemoveExpiredClients(now);
            RemoveExpiredRequestCounters(now);
        }
    }

    private void RemoveExpiredClients(DateTimeOffset now)
    {
        foreach (var id in clients.Where(pair => now - pair.Value.LastSeen >= OnlineWindow).Select(pair => pair.Key).ToArray())
            clients.Remove(id);
    }

    private void RemoveExpiredRequestCounters(DateTimeOffset now)
    {
        foreach (var address in requests.Where(pair => now - pair.Value.StartedAt >= RequestWindow).Select(pair => pair.Key).ToArray())
            requests.Remove(address);
    }

    private static IPAddress Normalize(IPAddress address) => address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

    private sealed record RequestCounter(DateTimeOffset StartedAt, int Count);
}

internal sealed class UsageCleanupService : BackgroundService
{
    public UsageCleanupService(UsageState state, TimeProvider timeProvider)
    {
        this.state = state;
        this.timeProvider = timeProvider;
    }

    private readonly UsageState state;
    private readonly TimeProvider timeProvider;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30), timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) state.RemoveExpired();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // 主机停止时取消等待是正常退出信号。
            return;
        }
    }
}
