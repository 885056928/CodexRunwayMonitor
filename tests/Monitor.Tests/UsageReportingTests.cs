using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Monitor.Core;

internal static class UsageReportingTests
{
    public static void AddTo(List<(string Name, Func<Task> Run)> tests)
    {
        tests.Add(("Usage defaults enabled and explicit opt-out survives JSON", () =>
        {
            Check(JsonSerializer.Deserialize<AppSettings>("{}")!.EnableUsageReporting);
            Check(!JsonSerializer.Deserialize<AppSettings>("{\"EnableUsageReporting\":false}")!.EnableUsageReporting);
            return Task.CompletedTask;
        }
        ));
        tests.Add(("Usage endpoints require HTTPS except loopback and reject credentials", () =>
        {
            foreach (var url in new[] { "https://example.com", "http://127.0.0.1:5088", "http://localhost:5088", "http://[::1]:5088" })
                Check(UsageEndpoint.ValidateServerUrl(url).IsAbsoluteUri);
            foreach (var url in new[] { "http://example.com", "https://user:secret@example.com", "https://example.com/?key=x", "https://example.com/#x", "ftp://localhost" })
                Throws<ArgumentException>(() => UsageEndpoint.ValidateServerUrl(url));
            new UsageEndpoint().Validate();
            Throws<ArgumentException>(() => new UsageEndpoint { ServerUrl = "https://example.com" }.Validate());
            return Task.CompletedTask;
        }
        ));
        tests.Add(("Unconfigured or disabled reporter performs no network or identity IO", async () =>
        {
            using var http = new HttpClient(new FakeHandler((_, _) => throw new Exception("Unexpected request")));
            var store = new IdentityStore();
            await using var reporter = new UsageReporter(http, store, new(), "1.0");
            await reporter.ApplyAsync(true);
            await reporter.ApplyAsync(false);
            Check(store.Loads == 0);
        }
        ));
        tests.Add(("Heartbeat uses random persisted identity, interval and disable cancellation", async () =>
        {
            var clock = new TestClock();
            var store = new IdentityStore();
            var calls = 0;
            using var http = new HttpClient(new FakeHandler(async (request, ct) =>
            {
                Check(request.RequestUri!.AbsolutePath == "/prefix/api/usage/heartbeat");
                Check(request.Headers.Authorization?.Parameter == new string('r', 32));
                var payload = await request.Content!.ReadFromJsonAsync<UsageHeartbeat>(ct);
                Check(payload!.InstallationId == store.Id && payload.Version == "1.2.3");
                Interlocked.Increment(ref calls);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            }));
            await using var reporter = new UsageReporter(http, store, Config(), "1.2.3", clock: clock);
            await reporter.ApplyAsync(true);
            await Until(() => calls == 1 && clock.PendingTimers == 1);
            await reporter.ApplyAsync(true);
            Check(calls == 1);
            clock.Advance(TimeSpan.FromSeconds(59));
            Check(calls == 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            await Until(() => calls == 2 && clock.PendingTimers == 1);
            await reporter.ApplyAsync(false);
            Check(clock.PendingTimers == 0 && store.Loads == 1);
            clock.Advance(TimeSpan.FromMinutes(2));
            Check(calls == 2);
            await reporter.ApplyAsync(true);
            await Until(() => calls == 3);
        }
        ));
        tests.Add(("Disable waits for in-flight heartbeat cancellation", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var cancelled = false;
            using var http = new HttpClient(new FakeHandler(async (_, ct) =>
            {
                entered.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
                finally
                {
                    cancelled = ct.IsCancellationRequested;
                }
                return new HttpResponseMessage();
            }));
            await using var reporter = new UsageReporter(http, new IdentityStore(), Config(), "1.0");
            await reporter.ApplyAsync(true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await reporter.ApplyAsync(false).WaitAsync(TimeSpan.FromSeconds(2));
            Check(cancelled);
        }
        ));
        tests.Add(("Failed heartbeat retries only on next interval and logs no secret", async () =>
        {
            var clock = new TestClock();
            var calls = 0;
            var logs = new List<string>();
            using var http = new HttpClient(new FakeHandler((_, _) =>
            {
                Interlocked.Increment(ref calls);
                throw new HttpRequestException("secret should not be copied");
            }));
            await using var reporter = new UsageReporter(http, new IdentityStore(), Config(), "1.0", (message, _) => logs.Add(message), clock);
            await reporter.ApplyAsync(true);
            await Until(() => clock.PendingTimers == 1);
            Check(calls == 1 && logs.Count == 1 && !logs[0].Contains("secret"));
            clock.Advance(TimeSpan.FromMinutes(1));
            await Until(() => calls == 2 && clock.PendingTimers == 1);
        }
        ));
        tests.Add(("Admin key stays per request and paging uses shared DTO", async () =>
        {
            var count = 0;
            using var http = new HttpClient(new FakeHandler((request, _) =>
            {
                Check(request.RequestUri!.PathAndQuery == "/prefix/api/usage/clients?page=2&pageSize=50");
                Check(request.Headers.Authorization?.Parameter == new string('a', 32));
                count++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new UsageSnapshot(DateTimeOffset.UtcNow, 51, 2, 2, 50, [])) });
            }));
            var snapshot = await new UsageAdminClient(http).GetSnapshotAsync(new Uri(Config().ServerUrl), new string('a', 32), 2, 50);
            Check(snapshot.OnlineClients == 51 && count == 1 && http.DefaultRequestHeaders.Authorization is null);
        }
        ));
        tests.Add(("Admin HTTP unauthorized remains typed error", async () =>
        {
            using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
            try
            {
                await new UsageAdminClient(http).GetSnapshotAsync(new Uri(Config().ServerUrl), new string('a', 32), 1, 50);
                throw new Exception("Expected unauthorized");
            }
            catch (HttpRequestException error)
            {
                Check(error.StatusCode == HttpStatusCode.Unauthorized);
            }
        }
        ));
    }

    private static UsageEndpoint Config() => new() { ServerUrl = "https://example.com/prefix", ReportKey = new string('r', 32) };
    private static void Check(bool condition) { if (!condition) throw new Exception("Usage assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private sealed class IdentityStore : IStateStore<Guid>
    {
        public Guid Id { get; } = Guid.NewGuid();
        public int Loads { get; private set; }
        public Task<Guid> LoadAsync(CancellationToken ct = default) { Loads++; return Task.FromResult(Id); }
        public Task SaveAsync(Guid value, CancellationToken ct = default) => Task.CompletedTask;
    }
}
