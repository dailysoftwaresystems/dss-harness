namespace RepoHarness.Tests;

/// <summary>
/// A clock that moves only when told, whose every wait passes at once and moves it on by what was waited: so a test of
/// something that waits for the clock to pass a time takes none of its own, and the clock is past that time when the
/// wait ends, as a real one would be.
/// </summary>
/// <remarks>
/// Moved under a lock, since workers of a sweep wait on it together, and each wait's end is posted to the thread pool
/// rather than run where the wait began, as a timer's is.
/// </remarks>
internal sealed class JumpingClock : ManualClock
{
    private readonly Lock _gate = new();
    private readonly List<TimeSpan> _waits = [];

    /// <summary>Every wait asked of it, in order.</summary>
    public IReadOnlyList<TimeSpan> Waits
    {
        get
        {
            lock (_gate)
            {
                return [.. _waits];
            }
        }
    }

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return base.GetUtcNow();
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return base.GetTimestamp();
        }
    }

    public override void Advance(TimeSpan by)
    {
        lock (_gate)
        {
            base.Advance(by);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            lock (_gate)
            {
                _waits.Add(dueTime);
                base.Advance(dueTime);
            }

            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new Passed();
    }

    /// <summary>A timer whose one wait has passed already, which nothing can change.</summary>
    private sealed class Passed : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => false;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
