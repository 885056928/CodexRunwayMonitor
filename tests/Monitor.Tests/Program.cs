using System.Net;
using Monitor.Core;

var tests = new List<(string Name, Func<Task> Run)>();
UsageReportingTests.AddTo(tests);
const string valid = """{"ok":true,"data":{"id":"2098685367058612394","kind":"reset_completed","resetType":"global","effectiveAt":null,"announcedAt":"2026-09-12T08:09:17.000Z","text":"Reset all propagated. Sweet dreams."},"meta":{}}""";
ApiService Api(string json, HttpStatusCode status = HttpStatusCode.OK) => new(new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json) }))), new Uri("https://www.codexrunway.com/openapi/v1"), TimeSpan.FromSeconds(2));
tests.Add(("Real fields: string ID and explicit null effectiveAt", async () => { var e = await Api(valid).GetLatestAsync(); Check(e?.Id == "2098685367058612394" && e.EffectiveAt is null && e.AnnouncedAt.HasValue); }));
tests.Add(("Empty data is not an event", async () => Check(await Api("""{"ok":true,"data":null}""").GetLatestAsync() is null)));
foreach (var bad in new[] { "not JSON", "{}", valid.Replace("\"effectiveAt\":null,", ""), valid.Replace("\"id\":\"2098685367058612394\"", "\"id\":\"\""), valid.Replace("reset_completed", "reset_scheduled"), valid.Replace("\"effectiveAt\":null", "\"effectiveAt\":\"yesterday\"") })
    tests.Add(("Reject invalid payload: " + bad[..Math.Min(bad.Length, 35)], async () => await Throws<ApiException>(() => Api(bad).GetLatestAsync())));
foreach (var status in new[] { 422, 502, 503 }) tests.Add(($"HTTP {status} becomes typed error", async () => { var e = await Throws<ApiException>(() => Api("<html>error</html>", (HttpStatusCode)status).GetLatestAsync()); Check(e.StatusCode == status); }));
tests.Add(("429 JSON retryAfter preserved", async () => { var e = await Throws<ApiException>(() => Api("""{"ok":false,"error":"rate_limited","detail":"Request limit exceeded","retryAfter":143}""", HttpStatusCode.TooManyRequests).GetLatestAsync()); Check(e.RetryAfter == TimeSpan.FromSeconds(143)); }));
tests.Add(("Timeout interrupts request", async () => { using var c = new HttpClient(new FakeHandler(async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return new(); })); var api = new ApiService(c, new("https://example.com"), TimeSpan.FromMilliseconds(30)); await Throws<TimeoutException>(() => api.GetLatestAsync()); }));
tests.Add(("Caller cancellation preserved", async () => { using var cts = new CancellationTokenSource(); cts.Cancel(); await Throws<OperationCanceledException>(() => Api(valid).GetLatestAsync(cts.Token)); }));
ResetEvent Event(string id, int? day = null) => new(id, "reset_completed", "global", day is null ? null : new DateTimeOffset(2026, 9, day.Value, 0, 0, 0, TimeSpan.Zero), null, null, System.Text.Json.JsonSerializer.SerializeToElement(new { id }));
tests.Add(("First event silent, new ID notifies, restart and rollback dedupe", async () =>
{
    var store = new MemoryStore<EventState>(new()); var processor = new EventProcessor(store, new());
    Check(!await processor.ProcessAsync(Event("a", 10), true));
    Check(await processor.ProcessAsync(Event("b", 11), true));
    processor = new(store, await store.LoadAsync());
    Check(!await processor.ProcessAsync(Event("b", 11), true));
    Check(!await processor.ProcessAsync(Event("a", 10), true));
    Check(!await processor.ProcessAsync(Event("b", 11), true));
    Check(processor.State.Latest?.Id == "b");
}
));
tests.Add(("Historical unknown ID suppressed, equal-time new ID notified", async () =>
{
    var s = new MemoryStore<EventState>(new()); var p = new EventProcessor(s, new());
    await p.ProcessAsync(Event("a", 10), true);
    Check(!await p.ProcessAsync(Event("old", 9), true));
    Check(await p.ProcessAsync(Event("same-time", 10), true));
}
));
tests.Add(("Unknown actual time processed once; repaired time is not a new event", async () =>
{
    var s = new MemoryStore<EventState>(new()); var p = new EventProcessor(s, new());
    Check(!await p.ProcessAsync(Event("a"), true));
    Check(await p.ProcessAsync(Event("b"), true));
    Check(!await p.ProcessAsync(Event("b", 13), true)); Check(p.State.Latest?.EffectiveAt?.Day == 13);
}
));
tests.Add(("Save failure leaves baseline intact and permits later retry", async () =>
{
    var s = new MemoryStore<EventState>(new()); var p = new EventProcessor(s, new());
    await p.ProcessAsync(Event("a"), true); s.Fail = true;
    await Throws<IOException>(() => p.ProcessAsync(Event("b"), true));
    Check(p.State.Latest?.Id == "a" && !p.State.ProcessedIds.Contains("b"));
    s.Fail = false; Check(await p.ProcessAsync(Event("b"), true));
}
));
tests.Add(("FirstRunSilent false allows first notification", async () => { var s = new MemoryStore<EventState>(new()); Check(await new EventProcessor(s, new()).ProcessAsync(Event("a"), false)); }));
tests.Add(("Atomic JSON store restores valid backup after corruption", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorTests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "state.json");
        var store = new JsonStore<EventState>(path, () => new());
        await store.SaveAsync(new EventState { ProcessedIds = ["a"], Latest = Event("a") });
        await store.SaveAsync(new EventState { ProcessedIds = ["a", "b"], Latest = Event("b") });
        await File.WriteAllTextAsync(path, "broken");
        var loaded = await store.LoadAsync();
        Check(loaded.Latest?.Id == "a");
        await store.SaveAsync(loaded);
        Check((await store.LoadAsync()).Latest?.Id == "a");
    }
    finally
    {
        Directory.Delete(dir, true);
    }
}
));
tests.Add(("Invalid settings preserved and defaulted", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorTests-" + Guid.NewGuid()); Directory.CreateDirectory(dir);
    try
    {
        var path = Path.Combine(dir, "settings.json");
        await File.WriteAllTextAsync(path, "{\"CheckIntervalMinutes\":0}");
        var warnings = new List<string>();
        var store = new JsonStore<AppSettings>(path, () => new(), s => s.Validate(), warnings.Add);
        Check((await store.LoadAsync()).CheckIntervalMinutes == 5);
        Check(warnings.Count > 0);
        Check(warnings.Any(message => message.Contains("检查间隔须为") && message.Contains("Validate")));
        Check(Directory.GetFiles(dir, "*.corrupt-*").Length == 1);
    }
    finally
    {
        Directory.Delete(dir, true);
    }
}
));
MonitoringService Service(HttpClient client, MemoryStore<RequestLedger>? ledger = null, RequestLedger? initial = null) => new(client, new(), new EventProcessor(new MemoryStore<EventState>(new()), new()), ledger ?? new(new()), initial ?? new());
tests.Add(("Concurrent manual checks never overlap", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
    using var c = new HttpClient(new FakeHandler(async (_, ct) => { Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task.WaitAsync(ct); return new() { Content = new StringContent(valid) }; }));
    await using var s = Service(c); var first = s.CheckAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await s.CheckAsync(); Check(calls == 1); release.SetResult(); await first; Check(s.Status.LastSuccess.HasValue);
}
));
tests.Add(("Pause cancels active network and resume immediately checks", async () =>
{
    var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int calls = 0;
    using var c = new HttpClient(new FakeHandler(async (_, ct) => { if (Interlocked.Increment(ref calls) == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } return new() { Content = new StringContent(valid) }; }));
    await using var s = Service(c); await s.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(2)); await s.PauseAsync().WaitAsync(TimeSpan.FromSeconds(2)); Check(!s.Status.Running && !s.Status.Busy);
    await s.StartAsync(); await Until(() => s.Status.LastSuccess.HasValue); Check(calls == 2);
}
));
tests.Add(("502 leaves monitor alive and next check succeeds", async () =>
{
    int calls = 0; using var c = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent(valid) })));
    await using var s = Service(c); await s.CheckAsync(); Check(s.Status.Error); await s.CheckAsync(); Check(!s.Status.Error && s.Status.LastSuccess.HasValue);
}
));
tests.Add(("Persistent rolling 20-request limit prevents network calls", async () =>
{
    using var c = new HttpClient(new FakeHandler((_, _) => throw new Exception("Network must not run")));
    var initial = new RequestLedger { Requests = Enumerable.Repeat(DateTimeOffset.UtcNow, 20).ToList() };
    await using var s = Service(c, initial: initial); await s.CheckAsync(); Check(s.Status.BlockedUntil > DateTimeOffset.UtcNow && s.Status.LastCheck is null);
}
));
tests.Add(("429 cooldown persists across restart and blocks manual checks", async () =>
{
    int calls = 0; using var c = new HttpClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{\"retryAfter\":300}") }); }));
    var ledger = new MemoryStore<RequestLedger>(new()); await using (var s = Service(c, ledger)) { await s.CheckAsync(); Check(s.Status.Error); }
    await using (var s = Service(c, ledger, await ledger.LoadAsync())) { await s.CheckAsync(); Check(calls == 1 && s.Status.BlockedUntil.HasValue); }
}
));
tests.Add(("Failed request ledger write never starts HTTP", async () =>
{
    int calls = 0; using var c = new HttpClient(new FakeHandler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage()); }));
    await using var s = Service(c, new MemoryStore<RequestLedger>(new()) { Fail = true }); await s.CheckAsync(); Check(s.Status.Error && calls == 0);
}
));
tests.Add(("Monitoring errors retain the original cause and stack", async () =>
{
    var errors = new List<string>();
    using var client = new HttpClient(new FakeHandler((_, _) => throw new IOException("测试读取故障", new InvalidOperationException("测试底层原因"))));
    await using var monitor = new MonitoringService(client, new(), new EventProcessor(new MemoryStore<EventState>(new()), new()),
        new MemoryStore<RequestLedger>(new()), new(), (message, error) => { if (error) errors.Add(message); });
    await monitor.CheckAsync();
    Check(monitor.Status.Error && errors.Any(message => message.Contains("测试读取故障") && message.Contains("测试底层原因") && message.Contains("SendAsync")));
}
));
tests.Add(("Malformed API payload retains the parsing exception", async () =>
{
    var error = await Throws<ApiException>(() => Api("not JSON").GetLatestAsync());
    Check(error.InnerException is System.Text.Json.JsonException);
}
));
tests.Add(("Settings enforce inclusive numeric boundaries", () =>
{
    new AppSettings { CheckIntervalMinutes = 3, RequestTimeoutSeconds = 5, AutoCloseNotificationSeconds = 0 }.Validate();
    new AppSettings { CheckIntervalMinutes = 1440, RequestTimeoutSeconds = 120, AutoCloseNotificationSeconds = 3600 }.Validate();
    foreach (var settings in new[]
    {
        new AppSettings { CheckIntervalMinutes = 2 }, new AppSettings { CheckIntervalMinutes = 1441 },
        new AppSettings { RequestTimeoutSeconds = 4 }, new AppSettings { RequestTimeoutSeconds = 121 },
        new AppSettings { AutoCloseNotificationSeconds = -1 }, new AppSettings { AutoCloseNotificationSeconds = 3601 }
    })
    {
        bool rejected = false;
        try
        {
            settings.Validate();
        }
        catch (ArgumentException)
        {
            rejected = true;
        }
        Check(rejected);
    }
    return Task.CompletedTask;
}
));
var failures = 0;
if (args.Contains("--live")) tests.Add(("Live official API via C# HttpClient", async () =>
{
    using var client = new HttpClient();
    var api = new ApiService(client, new("https://www.codexrunway.com/openapi/v1"), TimeSpan.FromSeconds(30));
    var result = await api.GetLatestAsync(); Check(result is not null && result.Kind == "reset_completed");
    Console.WriteLine($"LIVE id={result!.Id}, effectiveAt={result.EffectiveAt?.ToString("O") ?? "null"}, announcedAt={result.AnnouncedAt:O}");
}
    ));
tests.Add(("Virtual clock: five-minute interval, sleep recovery, no backlog", async () =>
{
    var clock = new TestClock(); int calls = 0;
    using var client = new HttpClient(new FakeHandler((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage() { Content = new StringContent(valid) }); }));
    await using var s = new MonitoringService(client, new(), new EventProcessor(new MemoryStore<EventState>(new()), new()), new MemoryStore<RequestLedger>(new()), new(), clock: clock);
    await s.StartAsync(); await Until(() => clock.PendingTimers > 0); Check(calls == 1);
    clock.Advance(TimeSpan.FromMinutes(4)); await Until(() => clock.PendingTimers > 0); Check(calls == 1);
    clock.Advance(TimeSpan.FromMinutes(1)); await Until(() => calls == 2 && clock.PendingTimers > 0);
    clock.Advance(TimeSpan.FromHours(12)); await Until(() => calls == 3 && clock.PendingTimers > 0); Check(calls == 3);
    await s.PauseAsync(); clock.Advance(TimeSpan.FromDays(1)); Check(calls == 3);
}
));
tests.Add(("Virtual clock: automatic poll recovers after 502", async () =>
{
    var clock = new TestClock(); int calls = 0;
    using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(++calls == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK) { Content = new StringContent(valid) })));
    await using var s = new MonitoringService(client, new(), new EventProcessor(new MemoryStore<EventState>(new()), new()), new MemoryStore<RequestLedger>(new()), new(), clock: clock);
    await s.StartAsync(); await Until(() => clock.PendingTimers > 0); Check(s.Status.Error);
    clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => s.Status.LastSuccess.HasValue); Check(s.Status.Running && !s.Status.Error);
}
));
tests.Add(("Notification disabled still commits event and never replays it", async () =>
{
    int calls = 0, notices = 0; using var client = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage() { Content = new StringContent(valid.Replace("2098685367058612394", ++calls == 1 ? "baseline" : "new")) })));
    await using var s = new MonitoringService(client, new() { EnableNotification = false }, new EventProcessor(new MemoryStore<EventState>(new()), new()), new MemoryStore<RequestLedger>(new()), new());
    s.EventDetected += _ => notices++;
    await s.CheckAsync(); await s.CheckAsync(); await s.UpdateSettingsAsync(new() { EnableNotification = true }); await s.CheckAsync(); Check(notices == 0 && s.Status.Latest?.Id == "new");
}
));
tests.Add(("Log writer flushes app and error log without throwing", async () =>
{
    var dir = Path.Combine(Path.GetTempPath(), "MonitorLogTest-" + Guid.NewGuid());
    try
    {
        await using (var log = new LoggingService(dir))
        {
            log.Write("start");
            log.Write("failed", true);
            log.Enabled = false;
            log.Write("hidden");
        }

        var content = await File.ReadAllTextAsync(Directory.GetFiles(dir, "app.log", SearchOption.AllDirectories).Single());
        Check(content.Contains("start") && content.Contains("failed") && !content.Contains("hidden"));
        Check(File.ReadAllText(Directory.GetFiles(dir, "error.log", SearchOption.AllDirectories).Single()).Contains("failed"));
    }
    finally
    {
        if (Directory.Exists(dir))
            Directory.Delete(dir, true);
    }
}
));
tests.Add(("Three simulated days: 864 timer cycles without duplicate notification", async () =>
{
    var clock = new TestClock(); int calls = 0, notices = 0;
    using var client = new HttpClient(new FakeHandler((_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage() { Content = new StringContent(valid) }); }));
    await using var s = new MonitoringService(client, new(), new EventProcessor(new MemoryStore<EventState>(new()), new()), new MemoryStore<RequestLedger>(new()), new(), clock: clock);
    s.EventDetected += _ => notices++;
    await s.StartAsync(); await Until(() => clock.PendingTimers > 0);
    for (var i = 1; i <= 864; i++) { clock.Advance(TimeSpan.FromMinutes(5)); var expected = i + 1; await Until(() => calls == expected && clock.PendingTimers > 0); }
    Check(calls == 865 && notices == 0 && s.Status.Running && !s.Status.Error);
}
));
foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine("PASS " + test.Name);
    }
    catch (Exception e)
    {
        failures++;
        Console.WriteLine("FAIL " + test.Name + ": " + e);
    }
}
Console.WriteLine($"{tests.Count - failures}/{tests.Count} passed");
return failures == 0 ? 0 : 1;

static void Check(bool value) { if (!value) throw new Exception("Assertion failed"); }
static async Task Until(Func<bool> condition) { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3)); while (!condition()) await Task.Delay(10, timeout.Token); }
static async Task<T> Throws<T>(Func<Task> action)
    where T : Exception
{
    try
    {
        await action();
    }
    catch (T e)
    {
        return e;
    }

    throw new Exception("Expected " + typeof(T).Name);
}

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }

sealed class MemoryStore<T>(T value) : IStateStore<T>
{
    public bool Fail { get; set; }
    public Task<T> LoadAsync(CancellationToken ct = default) => Task.FromResult(value);
    public Task SaveAsync(T next, CancellationToken ct = default) { if (Fail) throw new IOException("disk failure"); value = next; return Task.CompletedTask; }
}
