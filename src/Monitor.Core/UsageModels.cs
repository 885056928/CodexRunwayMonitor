namespace Monitor.Core;

public sealed record UsageHeartbeat(Guid InstallationId, string Version);

public sealed record UsageClient(Guid InstallationId, string IpAddress, string Version, DateTimeOffset LastSeen);

public sealed record UsageSnapshot(DateTimeOffset ServerTime, int OnlineClients, int OnlineIps,
    int Page, int PageSize, UsageClient[] Clients);
