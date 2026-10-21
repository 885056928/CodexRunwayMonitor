namespace Monitor.Core;

public sealed record EventState
{
    public HashSet<string> ProcessedIds { get; init; } = new(StringComparer.Ordinal);
    public ResetEvent? Latest { get; init; }
    public DateTimeOffset? TimeHighWatermark { get; init; }
    public void Validate()
    {
        if (ProcessedIds is null || ProcessedIds.Any(string.IsNullOrWhiteSpace) ||
            (Latest is not null && (!ProcessedIds.Contains(Latest.Id) || Latest.Kind != "reset_completed")))
            throw new ArgumentException("事件状态无效");
    }
}
public sealed class EventProcessor(IStateStore<EventState> store, EventState state)
{
    public EventState State { get; private set; } = state;
    public async Task<bool> ProcessAsync(ResetEvent value, bool firstRunSilent, CancellationToken ct = default)
    {
        var known = State.ProcessedIds.Contains(value.Id);
        var first = State.ProcessedIds.Count == 0;
        // 未见过的 ID 也可能是历史补录；仅用真实重置时间判断回退，不能用公告时间替代。
        var older = value.EffectiveAt.HasValue && State.TimeHighWatermark.HasValue && value.EffectiveAt < State.TimeHighWatermark;
        var notify = !known && !older && (!first || !firstRunSilent);
        var high = State.TimeHighWatermark;
        if (value.EffectiveAt is { } t && (high is null || t > high)) high = t;
        var latest = State.Latest;
        if (latest is null || latest.Id == value.Id || (!known && !older)) latest = value;
        var next = new EventState { ProcessedIds = new HashSet<string>(State.ProcessedIds, StringComparer.Ordinal) { value.Id }, Latest = latest, TimeHighWatermark = high };
        // 保存成功后才推进内存状态并允许通知，写入失败时保留原基线以便下次重试。
        await store.SaveAsync(next, ct).ConfigureAwait(false);
        State = next;
        return notify;
    }
}
