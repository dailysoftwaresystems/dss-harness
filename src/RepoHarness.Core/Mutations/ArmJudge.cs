using RepoHarness.Core.Execution;

namespace RepoHarness.Core.Mutations;

/// <summary>How many times one before-text occurs where an arm must replace it.</summary>
/// <param name="Text">The file holding the text, as the registry cites it.</param>
/// <param name="Site">The site, as the registry names it.</param>
/// <param name="Count">How many times it occurs there, overlapping occurrences counted.</param>
public sealed record TextCount(string Text, string Site, int Count);

/// <summary>A site a row spells otherwise than the tree spells the file it names.</summary>
/// <param name="Site">The site, as the registry names it.</param>
/// <param name="Tree">The file, as the sweep's reading of the tree spells it.</param>
public sealed record SiteSpelling(string Site, string Tree);

/// <summary>A replacement that leaves its site as it was: what replaces the text is that text again.</summary>
/// <param name="Before">The file holding the text replaced, as the registry cites it.</param>
/// <param name="After">The file holding what replaces it, as the registry cites it.</param>
/// <param name="Site">The site, as the registry names it.</param>
public sealed record UnchangedSite(string Before, string After, string Site);

/// <summary>What an arm's pre-flight read, before anything is built: the sites, the texts, the target and what depends on the sites.</summary>
public sealed record ArmPreflight
{
    /// <summary>
    /// Each site the sweep's reading of the tree holds under another spelling - its letters in another case - which a file
    /// system that folds case would find, and one that does not would not.
    /// </summary>
    public IReadOnlyList<SiteSpelling> MisspeltSites { get; init; } = [];

    /// <summary>Each site the worker's copy does not hold as a file.</summary>
    public IReadOnlyList<string> MissingSites { get; init; } = [];

    /// <summary>
    /// Each site the sweep's reading of the tree does not hold as its row spells it, which nothing could be checked
    /// against once it was put back: one of the <see cref="MisspeltSites"/> or the <see cref="MissingSites"/>, each said
    /// first, or a file the worker's copy holds that a build made there, or that sync leaves out.
    /// </summary>
    public IReadOnlyList<string> UnreadSites { get; init; } = [];

    /// <summary>
    /// What is wrong with each text the arm cites that cannot be used as the worker's copy holds it, as a line says it:
    /// one the copy does not hold as a file, or a before-text holding nothing, which occurs everywhere.
    /// </summary>
    public IReadOnlyList<string> TextProblems { get; init; } = [];

    /// <summary>
    /// How many times each before-text occurs: the arm's own and each M row's in its site, and a BUILD-RED arm's paired
    /// control's in its pristine site.
    /// </summary>
    public IReadOnlyList<TextCount> Counts { get; init; } = [];

    /// <summary>
    /// Each replacement that would leave its site as it was - the arm's own, an M row's, or a paired control's - its
    /// after-text being its before-text once both have the site's line endings.
    /// </summary>
    public IReadOnlyList<UnchangedSite> Unchanged { get; init; } = [];

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

/// <summary>How long a mutated run of a test binary may take before it is stopped, and what set that.</summary>
/// <param name="Limit">How long it may take.</param>
/// <param name="SetBy">
/// What set it, as the verdict of a run stopped at it says after "ran past": <c>mutations.runTimeFactor</c> times the
/// unmutated run, or whichever end of the bound held instead (<see cref="PristineJudge.Bound"/>).
/// </param>
public sealed record RunBound(TimeSpan Limit, string SetBy);

/// <summary>
/// What a run that exited left as its report: none, one this read, what is no report, or one whose file could not be
/// read. Exactly one of the four.
/// </summary>
public sealed record RunReport
{
    private RunReport(bool written, JUnitReport? read, string? problem, string? unread)
    {
        Written = written;
        Read = read;
        Problem = problem;
        Unread = unread;
    }

    /// <summary>The binary wrote no report.</summary>
    public static RunReport None { get; } = new(false, null, null, null);

    /// <summary>Whether the binary wrote its report at all.</summary>
    public bool Written { get; }

    /// <summary>The report, or <see langword="null"/> where it was not written, is no report, or could not be read from its file.</summary>
    public JUnitReport? Read { get; }

    /// <summary>
    /// Why what it wrote is no report this reads - no XML, no JUnit report, one declaring a document type - or
    /// <see langword="null"/> where it is one, none was read, or nothing says why.
    /// </summary>
    public string? Problem { get; }

    /// <summary>
    /// Why the report it wrote could not be read from its file, each time that was tried - held by another process, not
    /// this user's to read - or <see langword="null"/> where it was read, or none was written. The harness's own
    /// failure to read, and nothing the binary did.
    /// </summary>
    public string? Unread { get; }

    /// <summary>The report the binary wrote, read.</summary>
    /// <param name="report">The report.</param>
    public static RunReport Of(JUnitReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new(true, report, null, null);
    }

    /// <summary>What the binary wrote is no report this reads.</summary>
    /// <param name="problem">Why it is none, or <see langword="null"/> where nothing says.</param>
    public static RunReport NoReport(string? problem) => new(true, null, problem, null);

    /// <summary>The report the binary wrote could not be read from its file.</summary>
    /// <param name="why">Why, as the last attempt to read it said.</param>
    public static RunReport NotRead(string why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        return new(true, null, null, why);
    }
}

/// <summary>
/// One whole run of an arm's test binary: it exited, it ran past its bound and was stopped, or it was stopped as hung.
/// Exactly one of the three, and only a run that exited left a report anybody reads.
/// </summary>
public sealed record ArmRun
{
    private ArmRun(int? exitCode, RunBound? pastBound, int? stalledAfterSeconds, RunReport report, bool diagnosticSaid)
    {
        ExitCode = exitCode;
        PastBound = pastBound;
        StalledAfterSeconds = stalledAfterSeconds;
        Report = report;
        DiagnosticSaid = diagnosticSaid;
    }

    /// <summary>What the binary exited with, or <see langword="null"/> where it was stopped: at its bound, or as hung.</summary>
    public int? ExitCode { get; }

    /// <summary>The bound it ran past, and was stopped at, or <see langword="null"/> where it was not.</summary>
    public RunBound? PastBound { get; }

    /// <summary>
    /// How long it went without printing a line before it was stopped as hung, at <c>defaults.stallSeconds</c>, as every
    /// phase is; <see langword="null"/> where it was not.
    /// </summary>
    public int? StalledAfterSeconds { get; }

    /// <summary>What it left as its report: none where it was stopped, which nothing reads a report of.</summary>
    public RunReport Report { get; }

    /// <summary>Whether the run's output said the arm's diagnostic.</summary>
    public bool DiagnosticSaid { get; }

    /// <summary>A run that ended of its own accord.</summary>
    /// <param name="exitCode">What the binary exited with.</param>
    /// <param name="report">What it left as its report.</param>
    /// <param name="diagnosticSaid">Whether its output said the arm's diagnostic.</param>
    public static ArmRun Exited(int exitCode, RunReport report, bool diagnosticSaid = false)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new(exitCode, null, null, report, diagnosticSaid);
    }

    /// <summary>A run that ran past its bound, and was stopped.</summary>
    /// <param name="bound">The bound it had.</param>
    public static ArmRun StoppedAt(RunBound bound)
    {
        ArgumentNullException.ThrowIfNull(bound);

        return new(null, bound, null, RunReport.None, false);
    }

    /// <summary>A run stopped as hung.</summary>
    /// <param name="seconds">How long it had printed nothing for.</param>
    public static ArmRun Hung(int seconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(seconds);

        return new(null, null, seconds, RunReport.None, false);
    }
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
/// Pre-flight, before anything is built or written: a site the tree spells otherwise, one the copy does not hold, or one
/// it holds that the sweep's reading of the tree does not; a text it cites that the copy does not hold, or a before-text
/// holding nothing; a before-text that does not occur exactly once - the arm's, an M row's, or a paired control's in the
/// pristine site - or a replacement that changes nothing; a target the leg's build does not build, or a runner building
/// no program; and no object the arm's build builds depending on a site - each <c>violated</c>.
/// </para>
/// <para>
/// The mutated build: <c>stopped</c> where it was stopped, and the verdict of a guard of the build where one reached
/// one. Where it failed, a step that failed outside the objects depending on a site - another object, or a link
/// downstream of them, as a test binary's is - or none named, is <c>failed</c>: nothing says the mutation did not
/// compile. One within them is the mutation not compiling, <c>violated</c> for a TEST-RED arm and as declared for a
/// BUILD-RED one. Where it built, an object depending on a site that ninja's log shows not rebuilt is
/// <c>unwitnessed</c>, and a BUILD-RED mutation that built is <c>violated</c>. A BUILD-RED arm's paired control is then
/// built: stopped or a guard's verdict as above, <c>violated</c> where it did not build, <c>unwitnessed</c> where an
/// object was not rebuilt, and <c>passed</c> otherwise.
/// </para>
/// <para>
/// A TEST-RED arm's run: past its bound, or stopped as hung for printing nothing, <c>unattributed</c>; a report written
/// that could not be read from its file, <c>unmeasured</c>, which is this tool's failure and no finding about the
/// binary; no report, or one that is no JUnit report, <c>unattributed</c>, saying why it is none;
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
            if (build.Failed.FirstOrDefault(output => !dependents.Contains(output, StringComparer.Ordinal)) is { } elsewhere)
            {
                return ReachedVerdict.Of(LegVerdict.Failed, $"the mutated build failed at {elsewhere}, which is no object that depends on the site");
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
        // Before a missing site: where names are compared exactly, a site spelt otherwise than the tree spells it is
        // missing too, and saying only that would send its reader looking for a file that is there.
        if (preflight.MisspeltSites.FirstOrDefault() is { } misspelt)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                $"site '{misspelt.Site}' is spelt '{misspelt.Tree}' in the tree, and a row names a file as the tree spells it");
        }

        if (preflight.MissingSites.FirstOrDefault() is { } missing)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, $"site '{missing}' is not a file in the worker's copy of the tree");
        }

        if (preflight.UnreadSites.FirstOrDefault() is { } unread)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                $"site '{unread}' is no file the sweep's reading of the tree holds - one a build makes there, or one sync leaves out - "
                + "so nothing vouches for what it holds");
        }

        if (preflight.TextProblems.FirstOrDefault() is { } text)
        {
            return ReachedVerdict.Of(LegVerdict.Violated, text);
        }

        if (preflight.Counts.FirstOrDefault(count => count.Count != 1) is { } miscounted)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                $"the text in '{miscounted.Text}' occurs {miscounted.Count} time(s) in '{miscounted.Site}', where it must occur exactly once");
        }

        if (preflight.Unchanged.FirstOrDefault() is { } unchanged)
        {
            return ReachedVerdict.Of(
                LegVerdict.Violated,
                $"the text in '{unchanged.After}' is the text in '{unchanged.Before}', so replacing one with the other changes nothing in '{unchanged.Site}'");
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
                $"no object {Built(arm)} builds depends on {string.Join(", ", arm.Sites.Select(site => $"'{site.Site}'"))}")
            : null;
    }

    /// <summary>
    /// What an arm's build builds, as a line names it: its target, and a TEST-RED arm's runner where that is another
    /// target, which the build makes too so the binary run is the mutated tree's.
    /// </summary>
    private static string Built(MutationArm arm)
        => Builds(arm).Count == 1 ? $"target '{arm.Target}'" : $"target '{arm.Target}' or runner '{arm.Runner}'";

    /// <summary>
    /// The targets an arm's build builds: its own, and a TEST-RED arm's runner where that is another target - a test
    /// binary that links what the target builds is linked again only where the build is asked for it, and one left as an
    /// earlier build linked it would run without the mutation.
    /// </summary>
    /// <param name="arm">The arm.</param>
    public static IReadOnlyList<string> Builds(MutationArm arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        return arm.Kind == RedKind.TestRed && !string.Equals(arm.Runner, arm.Target, StringComparison.Ordinal)
            ? [arm.Target, arm.Runner]
            : [arm.Target];
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
        if (run.PastBound is { } bound)
        {
            return ReachedVerdict.Of(LegVerdict.Unattributed, $"ran past {bound.SetBy}, {LedgerReport.FormatDuration(bound.Limit)}, and was stopped");
        }

        if (run.StalledAfterSeconds is { } quiet)
        {
            return ReachedVerdict.Of(LegVerdict.Unattributed, $"printed nothing for {quiet}s, and was stopped as hung");
        }

        // It exited, then: only a run stopped at its bound, or as hung, has no exit code.
        if (run.Report.Unread is { } unread)
        {
            return ReachedVerdict.Of(
                LegVerdict.Unmeasured,
                $"its report was written and could not be read, after it exited {run.ExitCode}, so nothing says which cases failed: {unread}");
        }

        if (run.Report.Read is not { } report)
        {
            return ReachedVerdict.Of(
                LegVerdict.Unattributed,
                run.Report.Written
                    ? $"its report is no JUnit report this reads, after it exited {run.ExitCode}{Why(run.Report.Problem)}"
                    : $"exited {run.ExitCode} and wrote no report");
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

    /// <summary>Why a report is none, as a line ends with it: <paramref name="problem"/> after a colon, or nothing where none was said.</summary>
    internal static string Why(string? problem) => problem is null ? string.Empty : $": {problem}";

    /// <summary><paramref name="reached"/> with the separator its detail was built to end with taken off.</summary>
    private static ReachedVerdict TrimmedDetail(this ReachedVerdict reached) => reached with { Detail = reached.Detail.TrimEnd(';') };
}
