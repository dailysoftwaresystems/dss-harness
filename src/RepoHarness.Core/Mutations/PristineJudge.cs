using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Mutations;

/// <summary>What the unmutated run of one test binary on a leg decided.</summary>
/// <param name="Leg">
/// The leg's own verdict where the control did not pass - its build's verdict, failed, or unattributed - or
/// <see langword="null"/> where it passed.
/// </param>
/// <param name="Stops">Why each arm running the binary is stopped rather than driven, or <see langword="null"/> where they are driven.</param>
/// <param name="Bound">How long a mutated run of the binary may take before it is stopped as hung, where the control passed.</param>
public sealed record PristineOutcome(ReachedVerdict? Leg, string? Stops, TimeSpan Bound);

/// <summary>
/// The judge of a pristine control: one build and whole run of a test binary in a worker's unmutated copy, before any of
/// its arms is driven, so every arm is judged against a binary that passes, and every mutated run is bounded by how long
/// the binary takes when nothing is wrong.
/// </summary>
/// <remarks>
/// A control that did not pass decides the leg's own verdict - its build's, <c>failed</c> where cases reddened unmutated,
/// <c>unattributed</c> where it failed or hung and nothing ties that to a case - and stops every arm of the binary: a
/// mutation whose cases were red before it is proof of nothing.
/// </remarks>
public static class PristineJudge
{
    /// <summary>The least time a mutated run may take beyond the unmutated run's, however quick that was.</summary>
    public static readonly TimeSpan Headroom = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The longest a bound can be: the longest a timer can wait, some 49 days, which a factor however large never takes a
    /// bound past - one past it could not be waited for at all.
    /// </summary>
    public static readonly TimeSpan LongestBound = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// What the control of <paramref name="runner"/> decided: <paramref name="build"/>, and <paramref name="run"/> where
    /// the build passed, which took <paramref name="duration"/>.
    /// </summary>
    /// <param name="runner">The test target, as arms name it.</param>
    /// <param name="build">The build of the target in the unmutated worker.</param>
    /// <param name="run">The binary's whole run, or <see langword="null"/> where the build did not pass.</param>
    /// <param name="duration">How long the run took.</param>
    /// <param name="factor">How many times that a mutated run may take: <c>mutations.runTimeFactor</c>.</param>
    public static PristineOutcome Judge(string runner, ArmBuild build, ArmRun? run, TimeSpan duration, double factor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runner);
        ArgumentNullException.ThrowIfNull(build);

        if (build.Verdict.Verdict != LegVerdict.Passed)
        {
            return Stopped(
                ReachedVerdict.Of(build.Verdict.Verdict, $"the unmutated build of {runner}: {build.Verdict.Detail}"),
                $"the unmutated build of {runner} did not pass");
        }

        ArgumentNullException.ThrowIfNull(run);

        if (run.StalledAfterSeconds is { } quiet)
        {
            return Stopped(
                ReachedVerdict.Of(LegVerdict.Unattributed, $"the unmutated {runner} printed nothing for {quiet}s, and was stopped as hung"),
                $"the unmutated {runner} hangs, so no run of a mutation of it can be told from a hang the mutation caused");
        }

        if (run.Report is not { } report)
        {
            return Stopped(
                ReachedVerdict.Of(
                    LegVerdict.Unattributed,
                    run.ReportWritten
                        ? $"the report of the unmutated {runner} could not be read, after it exited {run.ExitCode}"
                        : $"the unmutated {runner} exited {run.ExitCode} and wrote no report"),
                $"the unmutated {runner} left no report to judge its arms' runs against");
        }

        if (report.Reds is { Count: > 0 } reds)
        {
            return Stopped(
                ReachedVerdict.Of(LegVerdict.Failed, $"the unmutated {runner} has {reds.Count} red: {string.Join(", ", reds)}"),
                $"the unmutated {runner} has {reds.Count} red, so no mutation of it proves anything");
        }

        if (run.ExitCode != 0)
        {
            return Stopped(
                ReachedVerdict.Of(LegVerdict.Unattributed, $"the unmutated {runner} exited {run.ExitCode}, and its report names no failing case"),
                $"the unmutated {runner} fails with no case to show for it");
        }

        return new PristineOutcome(null, null, Bound(duration, factor));
    }

    /// <summary>
    /// How long a mutated run may take: <paramref name="factor"/> times the unmutated run, never less than the unmutated
    /// run and <see cref="Headroom"/>, and never more than <see cref="LongestBound"/>.
    /// </summary>
    /// <param name="duration">How long the unmutated run took.</param>
    /// <param name="factor">How many times that a mutated run may take.</param>
    public static TimeSpan Bound(TimeSpan duration, double factor)
    {
        var least = (duration + Headroom).Ticks;
        var ticks = Math.Max(duration.Ticks * factor, least);

        return ticks < LongestBound.Ticks ? TimeSpan.FromTicks((long)ticks) : LongestBound;
    }

    private static PristineOutcome Stopped(ReachedVerdict leg, string why) => new(leg, why, TimeSpan.Zero);
}
