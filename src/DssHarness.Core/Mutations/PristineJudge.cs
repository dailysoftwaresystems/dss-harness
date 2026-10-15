using System.Globalization;
using DssHarness.Core.Execution;

namespace DssHarness.Core.Mutations;

/// <summary>
/// What the unmutated run of one test binary on a leg decided: it passed, and bounds each mutated run; it did not, and
/// stops each arm of the binary; or there was nothing to control. Exactly one of the three.
/// </summary>
public sealed record PristineOutcome
{
    private PristineOutcome(ReachedVerdict? leg, string? stops, RunBound? bound, IReadOnlyList<string>? reds)
    {
        Leg = leg;
        Stops = stops;
        Bound = bound;
        Reds = reds;
    }

    /// <summary>
    /// Nothing to control: the runner builds no program, which each of its arms' pre-flight says, so none of them
    /// reaches a run to bound.
    /// </summary>
    public static PristineOutcome NothingToControl { get; } = new(null, null, null, null);

    /// <summary>
    /// The leg's own verdict where the control did not pass - its build's verdict, failed, unattributed, or unmeasured -
    /// or <see langword="null"/> where it passed, or there was nothing to control.
    /// </summary>
    public ReachedVerdict? Leg { get; }

    /// <summary>Why each arm running the binary is stopped rather than driven, or <see langword="null"/> where they are driven.</summary>
    public string? Stops { get; }

    /// <summary>
    /// How long a mutated run of the binary may take before it is stopped, where the control passed;
    /// <see langword="null"/> where it did not, or there was nothing to control.
    /// </summary>
    public RunBound? Bound { get; }

    /// <summary>
    /// The cases the control's binary reddened, where it reddened any; <see langword="null"/> where it did not, or there
    /// was no run. What a reader of what ctest registers for them is matched against.
    /// </summary>
    public IReadOnlyList<string>? Reds { get; }

    /// <summary>The control passed: each arm of the binary is driven, its run bounded.</summary>
    /// <param name="bound">How long a mutated run of the binary may take.</param>
    public static PristineOutcome Passed(RunBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(bound.Limit, TimeSpan.Zero);

        return new(null, null, bound, null);
    }

    /// <summary>The control did not pass: each arm of the binary is stopped.</summary>
    /// <param name="leg">The leg's own verdict for it.</param>
    /// <param name="why">Why each arm running the binary is stopped rather than driven.</param>
    /// <param name="reds">The cases the binary reddened, where it reddened any.</param>
    public static PristineOutcome Stopped(ReachedVerdict leg, string why, IReadOnlyList<string>? reds = null)
    {
        ArgumentNullException.ThrowIfNull(leg);
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(leg, why, null, reds);
    }

    /// <summary>
    /// This outcome with its lines said in full: <paramref name="note"/> - what ctest registers for the red cases that a
    /// sweep does not apply, where it could be read - added to the leg's, and the run's log on the arms' reason, so each
    /// arm the control stops names where the run that reddened is kept. A control that passed, or nothing to control, is
    /// said by nothing more.
    /// </summary>
    /// <param name="note">What ctest registers for the red cases, or <see langword="null"/> where nothing was asked or read.</param>
    /// <param name="log">The control's run log, as a line names a path; <see langword="null"/> where the control never ran.</param>
    public PristineOutcome Said(string? note, string? log)
        => Stops is null || Leg is null
            ? this
            : new(
                Leg with { Detail = note is null ? Leg.Detail : $"{Leg.Detail}; {note}" },
                log is null ? Stops : $"{Stops}; its run's log: '{log}'",
                null,
                Reds);
}

/// <summary>
/// The judge of a pristine control: one build and whole run of a test binary in a worker's unmutated copy, before any of
/// its arms is driven, so every arm is judged against a binary that passes, and every mutated run is bounded by how long
/// the binary takes when nothing is wrong.
/// </summary>
/// <remarks>
/// A control that did not pass decides the leg's own verdict - its build's, <c>failed</c> where cases reddened unmutated,
/// <c>unattributed</c> where it failed or hung and nothing ties that to a case, <c>unmeasured</c> where the report it
/// wrote could not be read from its file - and stops every arm of the binary: a mutation whose cases were red before
/// it is proof of nothing, and nor is one of a binary nothing says is green.
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
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(build.Verdict.Verdict, $"the unmutated build of {runner}: {build.Verdict.Detail}"),
                $"the unmutated build of {runner} did not pass");
        }

        ArgumentNullException.ThrowIfNull(run);

        // Unbounded, the unmutated run either hung or exited: none is stopped at a bound.
        if (run.StalledAfterSeconds is { } quiet)
        {
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(LegVerdict.Unattributed, $"the unmutated {runner} printed nothing for {quiet}s, and was stopped as hung"),
                $"the unmutated {runner} hangs, so no run of a mutation of it can be told from a hang the mutation caused");
        }

        if (run.Report.Unread is { } unread)
        {
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(
                    LegVerdict.Unmeasured,
                    $"the report of the unmutated {runner} was written and could not be read, after it exited {run.ExitCode}: {unread}"),
                $"the report of the unmutated {runner} could not be read, so nothing says its cases pass unmutated");
        }

        if (run.Report.Read is not { } report)
        {
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(
                    LegVerdict.Unattributed,
                    run.Report.Written
                        ? $"the report of the unmutated {runner} is no JUnit report this reads, after it exited {run.ExitCode}{ArmJudge.Why(run.Report.Problem)}"
                        : $"the unmutated {runner} exited {run.ExitCode} and wrote no report"),
                $"the unmutated {runner} left no report to judge its arms' runs against");
        }

        if (report.Reds is { Count: > 0 } reds)
        {
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(LegVerdict.Failed, $"the unmutated {runner} has {reds.Count} red: {string.Join(", ", reds)}"),
                $"the unmutated {runner} has {reds.Count} red, so no mutation of it proves anything",
                [.. reds]);
        }

        if (run.ExitCode != 0)
        {
            return PristineOutcome.Stopped(
                ReachedVerdict.Of(LegVerdict.Unattributed, $"the unmutated {runner} exited {run.ExitCode}, and its report names no failing case"),
                $"the unmutated {runner} fails with no case to show for it");
        }

        return PristineOutcome.Passed(Bound(duration, factor));
    }

    /// <summary>
    /// How long a mutated run may take: <paramref name="factor"/> times the unmutated run, never less than the unmutated
    /// run and <see cref="Headroom"/>, and never more than <see cref="LongestBound"/> - saying which of the three set
    /// it, so a run stopped under the least a bound can be is never said to have run past the factor.
    /// </summary>
    /// <param name="duration">How long the unmutated run took.</param>
    /// <param name="factor">How many times that a mutated run may take.</param>
    public static RunBound Bound(TimeSpan duration, double factor)
    {
        var least = (duration + Headroom).Ticks;
        var scaled = duration.Ticks * factor;

        // Written so that a factor that is no number is the longest a bound can be, as it has always been.
        return !(Math.Max(scaled, least) < LongestBound.Ticks) ? new RunBound(LongestBound, "the longest a run is waited for")
            : scaled >= least ? new RunBound(TimeSpan.FromTicks((long)scaled), $"{factor.ToString(CultureInfo.InvariantCulture)}x the unmutated run")
            : new RunBound(TimeSpan.FromTicks(least), $"the unmutated run and {LedgerReport.FormatDuration(Headroom)} more");
    }
}
