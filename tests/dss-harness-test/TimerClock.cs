namespace DssHarness.Tests;

/// <summary>
/// A clock that moves only when told, whose timers fire when it is moved past their time and never on their own: so a
/// test of something that waits - a retry's turn, a handoff's bound, a stop's drain, a budget's tick - decides when the
/// wait ends, and a wait the test never moves past never ends.
/// </summary>
/// <remarks>
/// Moved under a lock, since the code under test creates its timers on other threads, and each timer's callback is
/// posted to the thread pool rather than run where the clock was moved, as a real timer's is.
/// </remarks>
internal sealed class TimerClock : ManualClock
{
    private readonly Lock _gate = new();
    private readonly List<Pending> _timers = [];
    private readonly List<(int Count, TaskCompletionSource Armed)> _watchers = [];

    /// <summary>How many timers wait on the clock now: armed, and not yet fired or disposed.</summary>
    public int Waiting
    {
        get
        {
            lock (_gate)
            {
                return ArmedLocked();
            }
        }
    }

    /// <summary>
    /// Completes once at least <paramref name="count"/> timers wait on the clock at once: the signal that the code under
    /// test has begun its wait, so a test moves the clock past a wait that stands, never one not yet begun.
    /// </summary>
    public Task WaitingAsync(int count = 1)
    {
        lock (_gate)
        {
            if (ArmedLocked() >= count)
            {
                return Task.CompletedTask;
            }

            var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _watchers.Add((count, armed));
            return armed.Task;
        }
    }

    /// <summary>How many timers are armed, counted under the lock the caller holds.</summary>
    private int ArmedLocked()
        => _timers.Count(timer => timer.Due is not null);

    /// <summary>Completes every watcher whose count the armed timers now reach, under the lock the caller holds.</summary>
    private void SignalLocked()
    {
        var armed = ArmedLocked();

        foreach (var watcher in _watchers.Where(watcher => watcher.Count <= armed).ToList())
        {
            _ = _watchers.Remove(watcher);
            watcher.Armed.TrySetResult();
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
        List<Pending> due;

        lock (_gate)
        {
            base.Advance(by);
            var now = base.GetUtcNow();
            due = [.. _timers.Where(timer => timer.Due <= now)];

            foreach (var timer in due)
            {
                timer.Due = timer.Period > TimeSpan.Zero ? now + timer.Period : null;
            }
        }

        foreach (var timer in due)
        {
            ThreadPool.QueueUserWorkItem(_ => timer.Callback(timer.State));
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new Pending(this, callback, state);

        lock (_gate)
        {
            _timers.Add(timer);
        }

        _ = timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>One timer of the clock: when it fires next, if armed.</summary>
    private sealed class Pending(TimerClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;

        public object? State { get; } = state;

        /// <summary>When it fires next; null while disarmed.</summary>
        public DateTimeOffset? Due { get; set; }

        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            var fireNow = false;

            lock (clock._gate)
            {
                Period = period == Timeout.InfiniteTimeSpan ? TimeSpan.Zero : period;
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNowLocked() + dueTime;

                if (dueTime == TimeSpan.Zero)
                {
                    Due = Period > TimeSpan.Zero ? clock.GetUtcNowLocked() + Period : null;
                    fireNow = true;
                }

                clock.SignalLocked();
            }

            if (fireNow)
            {
                ThreadPool.QueueUserWorkItem(_ => Callback(State));
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._gate)
            {
                Due = null;
                _ = clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The time now, read under the lock the caller already holds.</summary>
    private DateTimeOffset GetUtcNowLocked() => base.GetUtcNow();
}
