namespace RepoHarness.Tests;

/// <summary>A clock that moves only when told, its timestamps with it.</summary>
/// <param name="start">The time it starts at.</param>
internal class ManualClock(DateTimeOffset start) : TimeProvider
{
    private readonly DateTimeOffset _start = start;
    private DateTimeOffset _now = start;

    /// <summary>A clock started at an arbitrary fixed time.</summary>
    public ManualClock()
        : this(new DateTimeOffset(2026, 9, 30, 16, 32, 14, TimeSpan.Zero))
    {
    }

    /// <summary>How far the clock has been moved.</summary>
    public TimeSpan Moved => _now - _start;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long GetTimestamp() => (_now - _start).Ticks;

    public virtual void Advance(TimeSpan by) => _now += by;
}
