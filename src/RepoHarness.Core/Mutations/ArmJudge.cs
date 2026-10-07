using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Mutations;

/// <summary>How many times one before-text occurs where an arm must replace it.</summary>
/// <param name="Text">The file holding the text, as the registry cites it.</param>
/// <param name="Site">The site, as the registry names it.</param>
/// <param name="Count">How many times it occurs there, overlapping occurrences counted.</param>
public sealed record TextCount(string Text, string Site, int Count);

/// <summary>What an arm's pre-flight read, before anything is built: the sites, the texts, the target and what depends on the sites.</summary>
public sealed record ArmPreflight
{
    /// <summary>Each site the worker's copy does not hold as a file.</summary>
    public IReadOnlyList<string> MissingSites { get; init; } = [];

    /// <summary>
    /// How many times each before-text occurs: the arm's own and each M row's in its site, and a BUILD-RED arm's paired
    /// control's in its pristine site.
    /// </summary>
    public IReadOnlyList<TextCount> Counts { get; init; } = [];

    /// <summary>Whether the leg's build manifest has a build line for the arm's target.</summary>
    public bool TargetBuilt { get; init; } = true;

    /// <summary>Why the arm's runner builds no program the sweep can run, or <see langword="null"/> where it does, or the arm runs nothing.</summary>
    public string? RunnerProblem { get; init; }

    /// <summary>The objects the target builds that depend on a site, as <see cref="RebuildWitness.DependentObjects"/> gives them.</summary>
    public IReadOnlyList<string> Dependents { get; init; } = [];
}

/// <summary>One build of an arm's target in its worker: the mutated build, or a BUILD-RED arm's paired control.</summary>
/// <param name="Verdict">The build's own verdict: passed, failed, stopped, or one a guard of the build reached.</param>
/// <param name="Failed">Every step the build said failed, by its first output, as <c>Ninja.FailedOutputs</c> names them.</param>
/// <param name="NotRebuilt">The dependent objects ninja's log shows no step run for, where the build passed.</param>
public sealed record ArmBuild(ReachedVerdict Verdict, IReadOnlyList<string> Failed, IReadOnlyList<string> NotRebuilt);

/// <summary>One whole run of an arm's test binary.</summary>
public sealed record ArmRun
{
    /// <summary>What the binary exited with, or <see langword="null"/> where it was stopped at its bound.</summary>
    public int? ExitCode { get; init; }

    /// <summary>Whether it ran past its bound, and was stopped.</summary>
    public bool StoppedAtBound { get; init; }

    /// <summary>The bound the run had: <c>mutations.runTimeFactor</c> times the unmutated run, never less than it and a minute.</summary>
    public TimeSpan Bound { get; init; }

    /// <summary>The factor the bound was set by.</summary>
    public double Factor { get; init; }

    /// <summary>Whether the binary wrote its report at all.</summary>
    public bool ReportWritten { get; init; }

    /// <summary>The report, or <see langword="null"/> where it was not written or could not be read.</summary>
    public JUnitReport? Report { get; init; }

    /// <summary>Whether the run's output said the arm's diagnostic.</summary>
    public bool DiagnosticSaid { get; init; }
}

/// <summary>Everything observed of one arm so far: what is not observed yet is <see langword="null"/>.</summary>
/// <param name="Preflight">What the pre-flight read.</param>
public sealed record ArmObservation(ArmPreflight Preflight)
{
    /// <summary>The mutated build.</summary>
    public ArmBuild? Build { get; init; }

    /// <summary>A BUILD-RED arm's paired control's build.</summary>
    public ArmBuild? Control { get; init; }

    /// <summary>A TEST-RED arm's run.</summary>
    public ArmRun? Run { get; init; }
}

/// <summary>What an arm's sweep does next, where its verdict is not reached yet.</summary>
public enum ArmStep
{
    /// <summary>Build the mutated target.</summary>
    Build,

    /// <summary>Put the site back, apply the paired control, and build again.</summary>
    Control,

    /// <summary>Run the test binary whole.</summary>
    Run,
}

/// <summary>
/// The judge of one arm: its declaration and what was observed of it in, its verdict out, by the rows below, in order,
/// the first that applies deciding.
/// </summary>
/// <remarks>
/// <para>
/// Pre-flight, before anything is built: a site the copy does not hold; a before-text that does not occur exactly once
/// - the arm's, an M row's, or a paired control's in the pristine site; a target the leg's build does not build, or a
/// runner building no program; and no object the target builds depending on a site - each <c>violated</c>.
/// </para>
/// <para>
/// The mutated build: <c>stopped</c> where it was stopped, and the verdict of a guard of the build where one reached
/// one. Where it failed, a step that failed outside the objects depending on a site - or none named - is <c>failed</c>,
/// upstream of the mutation; one within them is the mutation not compiling, <c>violated</c> for a TEST-RED arm and as
/// declared for a BUILD-RED one. Where it built, an object depending on a site that ninja's log shows not rebuilt is
/// <c>unwitnessed</c>, and a BUILD-RED mutation that built is <c>violated</c>. A BUILD-RED arm's paired control is then
/// built: stopped or a guard's verdict as above, <c>violated</c> where it did not build, <c>unwitnessed</c> where an
/// object was not rebuilt, and <c>passed</c> otherwise.
/// </para>
/// <para>
/// A TEST-RED arm's run: past its bound, <c>unattributed</c>; no report, or one that cannot be read, <c>unattributed</c>;
/// a failing exit whose report names no failing case, <c>unattributed</c>; another number of cases run than declared,
/// <c>violated</c>; no case red, <c>survived</c>; the red cases not exactly the C rows', <c>violated</c>; a G row's
/// case not run, <c>violated</c>; its diagnostic not said, <c>violated</c>; and otherwise <c>passed</c>.
/// </para>
/// </remarks>
public static class ArmJudge
{
    /// <summary>
    /// The verdict <paramref name="observation"/> reaches for <paramref name="arm"/>, or <see langword="null"/> where it
    /// reaches none yet, and <see cref="Next"/> says what to observe.
    /// </summary>
    /// <param name="arm">The arm's declaration.</param>
    /// <param name="observation">What was observed of it so far.</param>
    public static ReachedVerdict? Judge(MutationArm arm, ArmObservation observation)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(observation);

        if (Preflight(arm, observation.Preflight) is { } refused)
        {
            return refused;
        }

        if (observation.Build is not { } build)
        {
            return null;
        }

        var dependents = observation.Preflight.Dependents;

        if (Unfinished(build, "the mutated build") is { } unfinished)
        {
            return unfinished;
        }

        if (build.Verdict.Verdict == LegVerdict.Failed)
        {
            if (build.Failed.FirstOrDefault(output => !dependents.Contains(output, StringComparer.Ordinal)) is { } upstream)
            {
                return ReachedVerdict.Of(LegVerdict.Failed, $"the mutated build failed at {upstream}, which is no object that depends on the site");
            }

            if (build.Failed.Count == 0)
            {
                return ReachedVerdict.Of(LegVerdict.Failed, $"the mutated build failed, and named no step that failed: {build.Verdict.Detail}");
            }

            if (arm.Kind == RedKind.TestRed)
            {
                return ReachedVerdict.Of(
                    LegVerdict.Violated,
                    $"declared {MutationRegistryParser.TestRed}, and the mutation does not compile: {string.Join(", ", build.Failed)}");
            }

            return observation.Control is { } control ? JudgeControl(control, build) : null;
        }

        if (build.NotRebuilt.Count > 0)
        {
            return NotRebuilt(build, "the mutated build");
        }

        if (arm.Kind == RedKind.BuildRed)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"declared {MutationRegistryParser.BuildRed}, and the mutation built");
        }

        return observation.Run is { } run ? JudgeRun(arm, run) : null;
    }

    /// <summary>What to observe next where <see cref="Judge"/> reaches no verdict yet.</summary>
    /// <param name="arm">The arm's declaration.</param>
    /// <param name="observation">What was observed of it so far.</param>
    public static ArmStep Next(MutationArm arm, ArmObservation observation)
    {
        ArgumentNullException.ThrowIfNull(arm);
        ArgumentNullException.ThrowIfNull(observation);

        return observation.Build is null ? ArmStep.Build
            : arm.Kind == RedKind.BuildRed ? ArmStep.Control
            : ArmStep.Run;
    }

    private static ReachedVerdict? Preflight(MutationArm arm, ArmPreflight preflight)
    {
        if (preflight.MissingSites.FirstOrDefault() is { } missing)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"site '{missing}' is not a file in the worker's copy of the tree");
        }

        if (preflight.Counts.FirstOrDefault(count => count.Count != 1) is { } miscounted)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                $"the text in '{miscounted.Text}' occurs {miscounted.Count} time(s) in '{miscounted.Site}', where it must occur exactly once");
        }

        if (!preflight.TargetBuilt)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"target '{arm.Target}' is built by no line of the leg's build");
        }

        if (preflight.RunnerProblem is { } runner)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, runner);
        }

        return preflight.Dependents.Count == 0
            ? ReachedVerdict.Of(
                LegVerdict.Violated,
                $"no object target '{arm.Target}' builds depends on {string.Join(", ", arm.Sites.Select(site => $"'{site.Site}'"))}")
            : null;
    }

    /// <summary>
    /// The verdict of a build that neither passed nor failed: stopped, or one a guard of the build reached - inputs that
    /// moved, another process in the build directory - which says nothing about the mutation either way.
    /// </summary>
    private static ReachedVerdict? Unfinished(ArmBuild build, string which)
        => build.Verdict.Verdict is LegVerdict.Passed or LegVerdict.Failed
            ? null
            : ReachedVerdict.Of(build.Verdict.Verdict, $"{which}: {build.Verdict.Detail}");

    private static ReachedVerdict NotRebuilt(ArmBuild build, string which)
        => ReachedVerdict.Of(
            LegVerdict.Unwitnessed,
            $"{string.Join(", ", build.NotRebuilt)} {(build.NotRebuilt.Count == 1 ? "depends" : "depend")} on the site, and {which} did not rebuild "
            + (build.NotRebuilt.Count == 1 ? "it" : "them"));

    private static ReachedVerdict JudgeControl(ArmBuild control, ArmBuild negative)
    {
        if (Unfinished(control, "the paired control's build") is { } unfinished)
        {
            return unfinished;
        }

        if (control.Verdict.Verdict == LegVerdict.Failed)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                "the paired positive control did not build"
                + (control.Failed.Count > 0 ? $": {string.Join(", ", control.Failed)}" : string.Empty));
        }

        return control.NotRebuilt.Count > 0
            ? NotRebuilt(control, "the paired control's build")
            : ReachedVerdict.Of(LegVerdict.Passed, $"the mutation stops the build at {string.Join(", ", negative.Failed)}, and its paired control builds");
    }

    private static ReachedVerdict JudgeRun(MutationArm arm, ArmRun run)
    {
        if (run.StoppedAtBound)
        {
            return ReachedVerdict.Of(
                LegVerdict.Unattributed,
                $"ran past {run.Factor.ToString(System.Globalization.CultureInfo.InvariantCulture)}x the unmutated run, "
                + $"{LedgerReport.FormatDuration(run.Bound)}, and was stopped");
        }

        if (run.Report is not { } report)
        {
            return ReachedVerdict.Of(
                LegVerdict.Unattributed,
                run.ReportWritten ? $"its report could not be read, after it exited {run.ExitCode}" : $"exited {run.ExitCode} and wrote no report");
        }

        var reds = report.Reds;

        if (run.ExitCode != 0 && reds.Count == 0)
        {
            return ReachedVerdict.Of(LegVerdict.Unattributed, $"exited {run.ExitCode}, and its report names no failing case");
        }

        if (report.Ran != arm.Cases)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"ran {report.Ran} case(s), and the arm declares {arm.Cases}");
        }

        if (reds.Count == 0)
        {
            return ReachedVerdict.Of(LegVerdict.Survived, $"ran {report.Ran} case(s), and none failed");
        }

        var missing = arm.Reds.Except(reds, StringComparer.Ordinal).ToList();
        var unexpected = reds.Except(arm.Reds, StringComparer.Ordinal).ToList();

        if (missing.Count > 0 || unexpected.Count > 0)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                "the cases that failed are not those declared:"
                + (missing.Count > 0 ? $" declared red and not, {string.Join(", ", missing)};" : string.Empty)
                + (unexpected.Count > 0 ? $" red and not declared, {string.Join(", ", unexpected)};" : string.Empty)).TrimmedDetail();
        }

        var ran = report.RanCases;

        if (arm.Greens.FirstOrDefault(green => !ran.Contains(green)) is { } absent)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"neighbour '{absent}' did not run, so it is no neighbour that stayed green");
        }

        return run.DiagnosticSaid
            ? ReachedVerdict.Of(LegVerdict.Passed, $"ran {report.Ran} case(s), {reds.Count} red as declared, and said its diagnostic")
            : ReachedVerdict.Of(LegVerdict.Violated, $"the run did not say its diagnostic, the text in '{arm.Diagnostic}'");
    }

    /// <summary><paramref name="reached"/> with the separator its detail was built to end with taken off.</summary>
    private static ReachedVerdict TrimmedDetail(this ReachedVerdict reached) => reached with { Detail = reached.Detail.TrimEnd(';') };
}
