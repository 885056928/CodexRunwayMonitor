using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

internal sealed class TestClock : TimeProvider
{
    private readonly object sync = new();
    private DateTimeOffset now = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);
    private readonly List<ClockTimer> timers = [];
    public override DateTimeOffset GetUtcNow() { lock (sync) return now; }
    public int PendingTimers { get { lock (sync) return timers.Count(t => t.Due.HasValue); } }
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (sync) { var timer = new ClockTimer(this, callback, state); timers.Add(timer); timer.Change(dueTime, period); return timer; }
    }
    public void Advance(TimeSpan delta)
    {
        List<ClockTimer> ready;
        lock (sync) { now += delta; ready = timers.Where(t => t.Due <= now).ToList(); foreach (var t in ready) t.Due = t.Period > TimeSpan.Zero ? now + t.Period : null; }
        foreach (var t in ready) t.Callback(t.State);
    }
    private sealed class ClockTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; set; }
        public TimeSpan Period { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period) { lock (owner.sync) { Due = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime; Period = period; return true; } }
        public void Dispose() { lock (owner.sync) { Due = null; owner.timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
