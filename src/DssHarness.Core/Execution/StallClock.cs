using System.Diagnostics;

namespace DssHarness.Core.Execution;

/// <summary>
/// How long a child has run and how long it has been silent, both from the monotonic clock.
/// A wall clock cannot answer either question on a host whose clock steps forward by 25 seconds
/// every few seconds: the child would look hung the moment the clock moved.
/// </summary>
/// <remarks>
/// The phase runner bounds a child by it. A reader that has to bound a silent exchange of its own
/// bounds it by this same clock rather than writing a second one.
/// </remarks>
internal sealed class StallClock
{
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();

    /// <summary>When the child was last seen: its launch, its start, or a line on either stream.</summary>
    private long _lastOutputMs;

    /// <summary>How long the child has run.</summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// How long since the child was last seen - its launch, its start, or the last line on either stream: a child still
    /// starting is as quiet as one that says nothing, under the one bound its user set.
    /// </summary>
    public TimeSpan Quiet => TimeSpan.FromMilliseconds(_elapsed.ElapsedMilliseconds - Interlocked.Read(ref _lastOutputMs));

    /// <summary>
    /// Whether the child has been quiet for <paramref name="quiet"/>: from its launch until it starts, and from its last
    /// line after - the user's own stall bound for both, never a multiple of it of the code's own for the start.
    /// </summary>
    /// <param name="quiet">How long the child may say nothing, starting among it.</param>
    /// <remarks>
    /// A start that takes the whole bound - a PATH entry on an unreachable share, a working directory on a mount that has
    /// gone - is a child that said nothing for as long as the user allows, and is stopped as one.
    /// </remarks>
    public bool Overdue(TimeSpan quiet) => Quiet >= quiet;

    /// <summary>
    /// Records that the child started, or said something. Called from the thread that starts the
    /// child and from the reader threads of both streams, so it is interlocked.
    /// </summary>
    public void Saw() => Interlocked.Exchange(ref _lastOutputMs, _elapsed.ElapsedMilliseconds);
}
