namespace DssHarness.Tests;

/// <summary>
/// The machine's own clock, whose timers never fire: a busy machine's timer callbacks, as late as they come. What holds
/// a time to such a clock must read the clock itself to know the time is spent.
/// </summary>
internal sealed class LateTimers : TimeProvider
{
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) => new Dormant();

    private sealed class Dormant : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
