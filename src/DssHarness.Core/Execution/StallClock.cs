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
    /// <summary>What <see cref="_lastOutputMs"/> holds before the child has started.</summary>
    private const long NotYet = -1;

    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private long _lastOutputMs = NotYet;

    /// <summary>How long the child has run.</summary>
    public TimeSpan Elapsed => _elapsed.Elapsed;

    /// <summary>
    /// How long since the last line on either stream, and nothing at all until the child is
    /// running. A process that has not started yet has not been quiet: the time spent starting
    /// one belongs to this tool, and counting it against the child makes a machine under load
    /// look like a hung command. That window has a bound of its own, in
    /// <see cref="Overdue(TimeSpan, TimeSpan)"/>.
    /// </summary>
    public TimeSpan Quiet
    {
        get
        {
            var last = Interlocked.Read(ref _lastOutputMs);

            return last == NotYet
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(_elapsed.ElapsedMilliseconds - last);
        }
    }

    /// <summary>
    /// Whether the child is past whichever bound is in force: how long it may take to
    /// start before it has started, how long it may stay silent after.
    /// </summary>
    /// <param name="quiet">How long the child may say nothing.</param>
    /// <param name="starting">How long the child may take to start.</param>
    /// <remarks>
    /// Two bounds rather than one because they measure different things. Counting the launch as
    /// silence makes a slow machine read as a hung command, which is the defect this clock was
    /// written for; leaving the launch unmeasured makes an unreachable PATH entry a child that
    /// never ends and never reports.
    /// </remarks>
    public bool Overdue(TimeSpan quiet, TimeSpan starting)
        => Interlocked.Read(ref _lastOutputMs) == NotYet
            ? _elapsed.Elapsed >= starting
            : Quiet >= quiet;

    /// <summary>
    /// Records that the child started, or said something. Called from the thread that starts the
    /// child and from the reader threads of both streams, so it is interlocked.
    /// </summary>
    public void Saw() => Interlocked.Exchange(ref _lastOutputMs, _elapsed.ElapsedMilliseconds);
}
