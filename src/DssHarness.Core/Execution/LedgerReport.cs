using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using DssHarness.Core.Testing;

namespace DssHarness.Core.Execution;

/// <summary>One row of the per-leg table.</summary>
/// <param name="Leg">The leg, as the configuration names it.</param>
/// <param name="Verdict">The one verdict it reached.</param>
/// <param name="Duration">Its wall time, or <see cref="TimeSpan.Zero"/> when it never ran.</param>
/// <param name="Detail">
/// What produced the verdict, and nothing else. Deliberately without the timing mark: the mark is
/// composed where the table is rendered, so a ledger carried from one machine to another is not
/// marked again by each of them.
/// </param>
/// <param name="TimingsSuspect">
/// Whether its durations mean anything. Never part of the verdict: a host that slept, or a clock
/// that stepped, makes a duration meaningless, and whether the code passed is a separate fact.
/// </param>
/// <param name="TimingNotes">Why the timings are suspect, one reason per note.</param>
/// <param name="CommandTime">The time the leg's own commands took.</param>
/// <param name="Overhead">What the harness spent on syncing, fingerprinting and sampling.</param>
/// <param name="TestCount">How many tests it reported running, where a count pattern extracted one.</param>
/// <param name="Timings">What its phases reported about their own timing under <c>--time</c>.</param>
public sealed record LedgerLine(
    string Leg,
    LegVerdict Verdict,
    TimeSpan Duration,
    string Detail,
    bool TimingsSuspect,
    IReadOnlyList<string> TimingNotes,
    TimeSpan CommandTime,
    TimeSpan Overhead,
    int? TestCount,
    IReadOnlyList<TimingMark> Timings)
{
    /// <summary>
    /// Where the leg's records are when another host ran it, or <see langword="null"/> when they are
    /// in this run's own directory.
    /// </summary>
    public string? RunDirectory { get; init; }

    /// <summary>The run a host ran the leg under; <see langword="null"/> for a leg this machine ran.</summary>
    public RunId? HostRunId { get; init; }

    /// <summary>
    /// The directory the run kept this leg's own records in, wherever the leg ran; <see langword="null"/> for a leg that
    /// kept none.
    /// </summary>
    public string? Records { get; init; }

    /// <summary>Each phase log in <see cref="Records"/>, by phase, in the order of their names; empty where none are.</summary>
    public IReadOnlyList<PhaseLogFile> Logs { get; init; } = [];

    /// <summary>The steps the leg's operating system does not run, by name.</summary>
    public IReadOnlyList<string> SkippedSteps { get; init; } = [];

    /// <summary>The steps the leg ran - its runner's action's, or the runner's own phases - by name, in the order they ran.</summary>
    public IReadOnlyList<string> RanSteps { get; init; } = [];

    /// <summary>The manual steps the leg ran, by name.</summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];

    /// <summary>The steps of the runner's action the run did not select, by name.</summary>
    public IReadOnlyList<string> UnselectedSteps { get; init; } = [];

    /// <summary>The last lines the leg's last phase that did not pass printed, as <see cref="LegEntry.LogTail"/> says.</summary>
    public IReadOnlyList<string> LogTail { get; init; } = [];

    /// <summary>The phase <see cref="LogTail"/>'s lines are of, as <see cref="LegEntry.TailPhase"/> says.</summary>
    public string? TailPhase { get; init; }

    /// <summary>The compilers CMake configured the leg's build with.</summary>
    public IReadOnlyList<Build.CompilerFact> Compilers { get; init; } = [];

    /// <summary>The developer environment the leg's processes started in, where it was set up.</summary>
    public Hosts.DeveloperEnvironmentFact? DeveloperEnvironment { get; init; }

    /// <summary>What the leg did with each set-up its work named, as <see cref="LegEntry.SetUps"/> says.</summary>
    public IReadOnlyList<SetUps.SetUpFact> SetUps { get; init; } = [];

    /// <summary>How the leg's machine took it, as <see cref="LegEntry.Admission"/> says.</summary>
    public AdmissionFact? Admission { get; init; }

    /// <summary>What the leg's build directory held and the room on its filesystem, as <see cref="LegEntry.Space"/> says.</summary>
    public BuildSpace? Space { get; init; }

    /// <summary>Each file the leg's steps kept, as <see cref="LegEntry.KeptOutputs"/> says.</summary>
    public IReadOnlyList<string> KeptOutputs { get; init; } = [];

    /// <summary>What the leg's test invocation kept as failure evidence, as <see cref="LegEntry.KeptOnFailure"/> says.</summary>
    public KeptOnFailure? KeptOnFailure { get; init; }

    /// <summary>Each mutation arm the leg was asked about, as <see cref="LegEntry.Arms"/> says.</summary>
    public IReadOnlyList<ArmEntry> Arms { get; init; } = [];

    /// <summary>The tree the leg worked in, as <see cref="LegEntry.Tree"/> says.</summary>
    public WorkedTree? Tree { get; init; }

    /// <summary>What <c>check-mutations --pending</c> made of the leg's arms, as <see cref="LegEntry.PendingSweep"/> says.</summary>
    public PendingSweep? PendingSweep { get; init; }

    /// <summary>Each control of the leg's sweep that did not pass, as <see cref="LegEntry.Controls"/> says.</summary>
    public IReadOnlyList<ControlEntry> Controls { get; init; } = [];

    /// <summary>
    /// The project whose tests the leg counted, as the test that reached its runner recorded it;
    /// <see langword="null"/> where no test counted any, or the leg resolves no project.
    /// </summary>
    public string? Project { get; init; }

    /// <summary>Which of <see cref="Project"/>'s test sets the count belongs to; <see langword="null"/> for its shared set.</summary>
    public string? TestSet { get; init; }

    /// <summary>
    /// Why the leg's test count stands apart from the others of its project and test set - how many it
    /// ran, and how many the rest ran - or <see langword="null"/> where it does not. Never part of the
    /// verdict, and never a timing mark: a count says nothing about the clock.
    /// </summary>
    public string? TestCountNote { get; init; }

    /// <summary>Whether the leg's test count stands apart from the others of its project and test set.</summary>
    public bool TestCountDiffers => TestCountNote is not null;
}

/// <summary>
/// The per-leg ledger a run ends with: the table a reader sees, and the same facts as data.
/// </summary>
/// <remarks>
/// One table, whatever command produced it, because a gate is read by whoever is on call and not
/// by the person who wrote the command. <c>--json</c> emits the same ledger, so a leg that
/// silently skipped its emulated arm is as visible to a script as to a reader.
/// </remarks>
public sealed class LedgerReport
{
    /// <summary>The table's column headings, in order.</summary>
    public static IReadOnlyList<string> Headings { get; } = ["LEG", "VERDICT", "DURATION", "DETAIL"];

    private static readonly JsonSerializerOptions JsonOptions = ReportJson.Options;

    /// <summary>Tells each text as it was written: a document read on the machine that wrote it.</summary>
    private static readonly Func<string, string> AsWritten = text => text;

    private LedgerReport(IReadOnlyList<LedgerLine> lines, IReadOnlyList<(string Leg, SetUps.SetUpFact SetUp)> lineless)
    {
        Lines = lines;
        Lineless = lineless;
        Verdict = Verdicts.Worst(lines.Select(line => line.Verdict));
    }

    /// <summary>What each leg that ended with no line - stopped before its work reached a verdict - did with its set-ups.</summary>
    public IReadOnlyList<(string Leg, SetUps.SetUpFact SetUp)> Lineless { get; }

    /// <summary>One row per leg, in the order the legs were recorded.</summary>
    public IReadOnlyList<LedgerLine> Lines { get; }

    /// <summary>
    /// The verdict that decides the run: the most fundamental one any leg reached. A leg whose
    /// inputs moved is not reported as failed even if its tests failed, because what failed was a
    /// tree that never existed.
    /// </summary>
    public LegVerdict Verdict { get; }

    /// <summary>The exit code the run reports.</summary>
    /// <remarks>
    /// Computed here rather than at each caller so that the table, the JSON and the process agree.
    /// A run where nothing failed but a leg reached no verdict of its own is <see cref="HarnessExit.Incomplete"/>,
    /// read from which legs reached none (<see cref="Complete"/>) rather than from the worst verdict alone: a skip
    /// once mapped to success, and reading the worst verdict's code alone is how a leg nobody could reach came to
    /// print as a pass.
    /// </remarks>
    public int ExitCode => ExitCodeGiven(cancelled: false, unfinished: []);

    /// <summary>The exit code this run reports, given what only its caller knows.</summary>
    /// <remarks>
    /// The rows cannot show a run that stopped early or left a leg running, so the caller that can
    /// see those supplies them here rather than deciding a code of its own. One derivation, read by
    /// the summary and by the JSON alike, so the two cannot drift apart.
    /// </remarks>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs still running when it stopped.</param>
    public int ExitCodeGiven(bool cancelled, IReadOnlyList<string> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);

        // A failure's own code where anything failed; otherwise incomplete wherever a leg reached no verdict of its own,
        // read from the same rows the summary names them from. A leg still running when the run stopped has no row, and
        // reached none either.
        return cancelled ? HarnessExit.Cancelled
            : !Passed ? Verdicts.ExitCodeFor(Verdict)
            : Complete && unfinished.Count == 0 ? HarnessExit.Success
            : HarnessExit.Incomplete;
    }

    /// <summary>
    /// Why this run ended as its code says, where the code alone cannot: <see cref="OutcomeCause.LockHeld"/> where that
    /// code is <see cref="HarnessExit.Refused"/> and every leg that did not pass was refused-locked - a lock another run
    /// holds turned each away, and nothing else went wrong, which a caller that can wait waits out - and
    /// <see cref="OutcomeCause.None"/> otherwise.
    /// </summary>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs still running when it stopped.</param>
    /// <remarks>
    /// Derived beside <see cref="ExitCodeGiven"/> and from it, so a run interrupted, or one a worse verdict decides,
    /// never reads as waiting on a lock its code does not say. Refused-locked ranks above a failure of the code, so a run
    /// where one leg failed and another met a held lock exits 13 as well: waited out, it would fail again, so it is no
    /// lock to wait on - nor is one with a leg left unreached, still running, or a set-up it left up.
    /// </remarks>
    public OutcomeCause CauseGiven(bool cancelled, IReadOnlyList<string> unfinished)
        => ExitCodeGiven(cancelled, unfinished) == HarnessExit.Refused
            && unfinished.Count == 0
            && LeftRunning.Count == 0
            && Lines.All(line => line.Verdict == LegVerdict.RefusedLocked || Verdicts.Describe(line.Verdict).ExitCode == HarnessExit.Success)
                ? OutcomeCause.LockHeld
                : OutcomeCause.None;

    /// <summary>
    /// The one line that says how this run ended: interrupted, a failing verdict, legs that did no
    /// work or were stopped before finishing, or a plain pass.
    /// </summary>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs still running when it stopped.</param>
    /// <remarks>
    /// Derived the way <see cref="ExitCodeGiven"/> is and beside it, so the line and the code can
    /// never describe two different runs. Composed here rather than by each caller because a
    /// caller that composed it on one branch only is exactly how a failing run came to print
    /// <c>FAIL - </c> with nothing after it: the table said why and the JSON branch said nothing,
    /// and a host is always asked for JSON, so every failure on another machine read that way.
    /// </remarks>
    public string Summarize(bool cancelled, IReadOnlyList<string> unfinished)
    {
        ArgumentNullException.ThrowIfNull(unfinished);

        if (cancelled)
        {
            // Not a red verdict. A caller reading a failure code for an interrupted run would
            // report the code as broken when nothing reached a verdict at all.
            return string.Join("; ", [$"interrupted after {ReportText.Counted(Lines.Count, "leg")}", .. LeftRunningSaid]);
        }

        if (!Passed)
        {
            // The run's verdict with the count that reached it, then the rest. The worst verdict
            // beside the total read as every leg having it: "poisoned: 8 leg(s) reported" was
            // taken for eight poisoned legs when two were, three had failed and three had passed.
            var worst = Lines.Count(line => line.Verdict == Verdict);
            var others = Lines
                .Where(line => line.Verdict != Verdict)
                .GroupBy(line => line.Verdict)
                .OrderBy(group => Verdicts.Rank(group.Key))
                .Select(group => $"{group.Count()} {Verdicts.Display(group.Key)}")
                .ToList();

            // What a leg left running is said whatever the verdicts: it runs on after the run, failed or not.
            return string.Join(
                "; ",
                [
                    $"{Verdicts.Display(Verdict)}: {worst} of {ReportText.Counted(Lines.Count, "leg")}",
                    .. others.Count == 0 ? Array.Empty<string>() : [string.Join(", ", others)],
                    .. LeftRunningSaid,
                ]);
        }

        if (ExitCodeGiven(cancelled, unfinished) == HarnessExit.Success)
        {
            return $"{ReportText.Counted(Lines.Count, "leg")} passed";
        }

        // A leg that did no work, or whose work was stopped, is not a leg that passed. Nothing failed, so this is not a
        // red run; but an unqualified success would put "OK - 8 leg(s) passed" in front of a reader when none of those
        // eight ran, which is the one thing a gate reads. The legs are named, because which of them went unreported is
        // the first thing to ask, and the two kinds apart, because what to do differs: every leg that reached no verdict
        // of its own, as the exit code reads them, and did work was stopped before finishing.
        var withoutWork = WithoutVerdict.Where(line => Verdicts.IsSkip(line.Verdict)).Select(line => line.Leg).Concat(unfinished).ToList();
        var stopped = WithoutVerdict.Where(line => !Verdicts.IsSkip(line.Verdict)).Select(line => line.Leg).ToList();

        IEnumerable<string> unreported =
        [
            .. withoutWork.Count > 0 ? [$"{withoutWork.Count} did no work: {string.Join(", ", withoutWork)}"] : Array.Empty<string>(),
            .. stopped.Count > 0 ? [$"{stopped.Count} stopped before finishing: {string.Join(", ", stopped)}"] : Array.Empty<string>(),
            .. LeftRunningSaid,
        ];

        return $"{Reported} of {ReportText.Counted(Lines.Count, "leg")} passed; {string.Join("; ", unreported)}";
    }

    /// <summary>Each set-up a leg brought up and did not bring down, as the run's last line says it, and how to.</summary>
    /// <remarks>
    /// Said wherever the run ends - its last line, or after the refusal that ended it - since a set-up left running runs
    /// on after the run whatever ended it.
    /// </remarks>
    public IReadOnlyList<string> LeftRunningSaid
        => [.. LeftRunning.Select(left => $"{left.Leg} did not bring set-up '{left.SetUp.Name}' down; {SetUps.SetUpFact.Remedy(left.SetUp.Name, left.SetUp.Inputs)}")];

    /// <summary>Whether every leg reached a verdict that is not a failure.</summary>
    public bool Passed => !Lines.Any(line => Verdicts.IsFailure(line.Verdict));

    /// <summary>The legs that reached no verdict of their own, each with why, in the order they were recorded.</summary>
    /// <remarks>
    /// A skip is not a failure — a switched-off machine is normal — and nor is a leg whose build something stopped from
    /// outside, but neither is a pass either, and the difference is the whole of what a gate reads. Kept apart from
    /// <see cref="Passed"/> rather than folded into it: a run where one leg passed and another was
    /// never reached is neither wholly green nor red, and saying so is the only honest summary.
    /// </remarks>
    public IReadOnlyList<LedgerLine> WithoutVerdict
        => [.. Lines.Where(line => Verdicts.ReachedNone(line.Verdict))];

    /// <summary>How many legs actually reached a verdict of their own.</summary>
    public int Reported => Lines.Count - WithoutVerdict.Count;

    /// <summary>
    /// Whether every leg with a row reached a verdict of its own, and brought down every set-up it brought up.
    /// </summary>
    /// <remarks>
    /// Orthogonal to <see cref="Passed"/> on purpose: "did everything report" and "did anything
    /// fail" are separate questions, and folding them into one boolean made a run where all eight
    /// legs ran and two failed report as incomplete — which reads as a run that did not finish,
    /// when it finished and found bugs. A set-up left up is a change begun and not finished, whatever
    /// the verdicts: what it started may still run, and bringing it down finishes it. Interruption is not visible from the rows at all, so the
    /// caller that can see it supplies it to <see cref="ExitCodeGiven"/> and <see cref="ToJson(bool, IReadOnlyList{string}, string, Func{string, string}, RunId, string)"/>.
    /// </remarks>
    public bool Complete => WithoutVerdict.Count == 0 && LeftRunning.Count == 0;

    /// <summary>
    /// Each set-up a leg brought up and did not bring down, with the leg - one with a line, or one stopped with none:
    /// what it started may still run, so the run did not finish what it began, whatever its legs' verdicts -
    /// <see cref="HarnessExit.Incomplete"/> where nothing failed.
    /// </summary>
    public IReadOnlyList<(string Leg, SetUps.SetUpFact SetUp)> LeftRunning
        => [.. Lines.SelectMany(line => line.SetUps.Select(setUp => (Leg: line.Leg, SetUp: setUp))).Concat(Lineless).Where(left => left.SetUp.LeftRunning)];

    /// <summary>
    /// Builds the report, marking a phase that took more than
    /// <paramref name="durationWarningFactor"/> times what its siblings took, and a test count that
    /// stands apart from its siblings'.
    /// </summary>
    /// <param name="entries">Each leg's line.</param>
    /// <param name="durationWarningFactor">The factor; zero or less disables the mark.</param>
    /// <param name="lineless">What each leg stopped with no line did with its set-ups; none where none was.</param>
    public static LedgerReport From(
        IReadOnlyList<LegEntry> entries,
        double durationWarningFactor,
        IReadOnlyList<(string Leg, SetUps.SetUpFact SetUp)>? lineless = null)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var suspect = Compare(entries, durationWarningFactor);
        var counts = CompareCounts(entries);

        return new LedgerReport(
        [
            .. entries.Select(entry =>
            {
                // A count that stands apart is its own note, never a timing one: a Windows-only test
                // made a Windows leg's timings read as suspect, which says nothing about its clock.
                var notes = entry.TimingNotes
                    .Concat(entry.Phases.Where(phase => phase.ClockStepped).Select(phase => $"the clock stepped during {phase.Phase}"))
                    .Concat(suspect.TryGetValue(entry.Leg, out var slow) ? slow : [])
                    .Distinct(StringComparer.Ordinal)
                    .ToList();

                return new LedgerLine(
                    entry.Leg,
                    entry.Verdict,
                    entry.Duration,
                    Detail(entry),
                    notes.Count > 0,
                    notes,
                    entry.CommandTime,
                    entry.Overhead,
                    entry.TestCount,
                    entry.Timings)
                {
                    RunDirectory = entry.RunDirectory,
                    HostRunId = entry.HostRunId,
                    Records = entry.Records,
                    Logs = entry.Logs ?? [],
                    SkippedSteps = entry.SkippedSteps,
                    RanSteps = entry.RanSteps,
                    ManualSteps = entry.ManualSteps,
                    UnselectedSteps = entry.UnselectedSteps,
                    LogTail = entry.LogTail,
                    TailPhase = entry.TailPhase,
                    Compilers = entry.Compilers,
                    DeveloperEnvironment = entry.DeveloperEnvironment,
                    SetUps = entry.SetUps,
                    Admission = entry.Admission,
                    Space = entry.Space,
                    KeptOutputs = entry.KeptOutputs,
                    KeptOnFailure = entry.KeptOnFailure,
                    Arms = entry.Arms,
                    Tree = entry.Tree,
                    PendingSweep = entry.PendingSweep,
                    Controls = entry.Controls,
                    Project = entry.Project,
                    TestSet = entry.TestSet,
                    TestCountNote = counts.GetValueOrDefault(entry.Leg),
                };
            }),
        ],
        lineless ?? []);
    }

    /// <summary>
    /// A duration as the table writes it: <c>2m14s</c>, <c>12m40s</c>, <c>1h04m</c>. Whole seconds,
    /// because a column that shows a build's milliseconds invites a reader to compare two numbers
    /// that a clock step can move by 25 seconds.
    /// </summary>
    /// <param name="duration">The duration.</param>
    public static string FormatDuration(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return string.Empty;
        }

        var rounded = TimeSpan.FromSeconds(Math.Round(duration.TotalSeconds));

        if (rounded.TotalHours >= 1)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalHours}h{rounded.Minutes:00}m");
        }

        return rounded.TotalMinutes >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalMinutes}m{rounded.Seconds:00}s")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)rounded.TotalSeconds}s");
    }

    /// <summary>
    /// The table, heading first: <c>LEG / VERDICT / DURATION / DETAIL</c>; nothing when no leg has a row.
    /// </summary>
    /// <remarks>
    /// Each column is as wide as what it holds, so a long verdict such as
    /// <c>skipped-unavailable</c> never pushes a detail into the duration column and nothing is
    /// truncated. The duration is right-aligned, so two legs' times can be compared down the column.
    /// <para>
    /// A command that stopped before any leg reached a verdict has no table: the heading alone, above
    /// the line saying why it stopped, read as a table whose rows had gone missing.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> Render()
    {
        if (Lines.Count == 0)
        {
            return [];
        }

        var leg = Width(Headings[0], Lines.Select(line => line.Leg));
        var verdict = Width(Headings[1], Lines.Select(line => Verdicts.Display(line.Verdict)));
        var duration = Width(Headings[2], Lines.Select(line => FormatDuration(line.Duration)));

        var rows = new List<string>
        {
            Row(Headings[0], Headings[1], Headings[2], Headings[3], leg, verdict, duration),
        };

        rows.AddRange(Lines.Select(line => Row(
            line.Leg,
            Verdicts.Display(line.Verdict),
            FormatDuration(line.Duration),
            Marked(line.Detail, line.Tree, line.TimingNotes, line.Compilers, line.DeveloperEnvironment, line.SetUps, line.Admission, line.TestCountNote),
            leg,
            verdict,
            duration)));

        rows.AddRange(RenderArms());
        rows.AddRange(RenderTimings());
        rows.AddRange(RenderTails());
        rows.AddRange(RenderKeptOnFailure());

        return rows;
    }

    /// <summary>
    /// What each leg's test invocation kept as failure evidence, as a block below the table: one line a leg, naming the
    /// directory, how many files were kept and their size, and how many were refused over the cap, could not be read, or
    /// named nothing - the counts a reader needs before opening anything, beside the verdict they belong to.
    /// </summary>
    private IReadOnlyList<string> RenderKeptOnFailure()
    {
        var rows = new List<string>();

        foreach (var line in Lines.Where(line => line.KeptOnFailure is not null))
        {
            var kept = line.KeptOnFailure!;
            var said = $"{line.Leg}: kept {ReportText.Counted(kept.Kept.Count, "file")} ({DiskSpace.Size(kept.Bytes)}) as "
                + $"failure evidence in '{kept.Directory}'";

            if (kept.RefusedOverCap.Count > 0)
            {
                said += $", {ReportText.Counted(kept.RefusedOverCap.Count, "file")} refused over the cap";
            }

            if (kept.Unreadable.Count > 0)
            {
                said += $", {ReportText.Counted(kept.Unreadable.Count, "file")} could not be read";
            }

            if (kept.NamedNothing.Count > 0)
            {
                said += $", {ReportText.Counted(kept.NamedNothing.Count, "path")} named nothing";
            }

            rows.Add(string.Empty);
            rows.Add(said);
        }

        return rows;
    }

    /// <summary>
    /// Each mutation arm that reached a verdict other than passed, as a block below the table: the arm and why, under the
    /// leg it ran on, so a sweep of a hundred arms shows the ones to act on. An arm the sweep did not select is left out,
    /// as it was asked; each arm is in the data form whatever it reached.
    /// </summary>
    /// <remarks>
    /// Below rather than in the table, as the timings are: a leg's line says the worst its arms reached and how many
    /// reached what, and the arms behind it would push every other leg's line apart.
    /// </remarks>
    private IReadOnlyList<string> RenderArms()
    {
        var arms = Lines
            .SelectMany(line => line.Arms
                .Where(arm => arm.Verdict is not (LegVerdict.Passed or LegVerdict.SkippedNotSelected))
                .Select(arm => (line.Leg, Arm: arm)))
            .ToList();

        if (arms.Count == 0)
        {
            return [];
        }

        var leg = Width(Headings[0], arms.Select(item => item.Leg));
        var arm = Width("ARM", arms.Select(item => item.Arm.Arm));
        var verdict = Width(Headings[1], arms.Select(item => Verdicts.Display(item.Arm.Verdict)));

        return
        [
            string.Empty,
            "ARMS",
            $"  {Headings[0].PadRight(leg)}  {"ARM".PadRight(arm)}  {Headings[1].PadRight(verdict)}  {Headings[3]}",
            .. arms.Select(item =>
                $"  {item.Leg.PadRight(leg)}  {item.Arm.Arm.PadRight(arm)}  {Verdicts.Display(item.Arm.Verdict).PadRight(verdict)}  {item.Arm.Detail}".TrimEnd()),
        ];
    }

    /// <summary>
    /// The last lines each leg's last phase that did not pass printed, as a block below the table: what a
    /// reader who cannot open the leg's log - another host's, which stays on that host - needs first.
    /// Below rather than in the table, as the timings are, so no row is pushed apart.
    /// </summary>
    private IReadOnlyList<string> RenderTails()
    {
        var rows = new List<string>();

        foreach (var line in Lines.Where(line => line.LogTail.Count > 0))
        {
            rows.Add(string.Empty);
            rows.Add($"{line.Leg}: the last {ReportText.Counted(line.LogTail.Count, "line")} its phase printed");
            rows.AddRange(line.LogTail.Select(QuotedLine.Of));
        }

        return rows;
    }

    /// <summary>
    /// What each leg's phases reported about their own timing, as a block below the table.
    /// </summary>
    /// <remarks>
    /// Below rather than in the table: a leg can report many marks, and a column wide enough for all
    /// of them would push the verdict a reader came for off the edge. Empty without <c>--time</c>,
    /// and empty with it where no pattern is configured or none matched, so the block appears only
    /// when there is something in it. Each row names the leg and the phase that printed it, because
    /// legs run at the same time and a number with neither is a measurement of nothing.
    /// </remarks>
    private IReadOnlyList<string> RenderTimings()
    {
        var marks = Lines
            .SelectMany(line => line.Timings.Select(timing => (line.Leg, timing.Phase, timing.Text, timing.Value)))
            .ToList();

        if (marks.Count == 0)
        {
            return [];
        }

        var leg = Width("LEG", marks.Select(mark => mark.Leg));
        var phase = Width("PHASE", marks.Select(mark => mark.Phase));
        var text = Width("REPORTED", marks.Select(mark => mark.Text));

        return
        [
            string.Empty,
            "TIMINGS",
            $"  {"LEG".PadRight(leg)}  {"PHASE".PadRight(phase)}  {"REPORTED".PadRight(text)}  VALUE",
            .. marks.Select(mark =>
                $"  {mark.Leg.PadRight(leg)}  {mark.Phase.PadRight(phase)}  {mark.Text.PadRight(text)}  {mark.Value}"),
        ];
    }

    /// <summary>The same ledger as data, for <c>--json</c>.</summary>
    /// <remarks>
    /// An interrupted run is told, not inferred. The report is built from the legs that finished,
    /// so on its own it would describe a run stopped after its first leg as a clean pass of one
    /// leg, with the others simply absent — and the process would meanwhile exit 130. A script
    /// reading this instead of the shell's status would take that for a green run, which is the
    /// failure the whole ledger exists to prevent.
    /// </remarks>
    /// <param name="cancelled">Whether the run was interrupted before it finished.</param>
    /// <param name="unfinished">The legs that were still running when it stopped.</param>
    /// <param name="runDirectory">
    /// Where this run keeps its records, so a caller reading the document never has to work it out;
    /// <see langword="null"/> for a command that keeps none.
    /// </param>
    /// <param name="shown">
    /// How the document's reader is told a path or a line of the harness's own, as
    /// <see cref="IHarnessOutput.Shown"/> tells it; <see langword="null"/> to write each as it is.
    /// </param>
    /// <param name="run">
    /// The run this is, for a command that keeps runs, which a caller can cite it by whatever ended it;
    /// <see langword="null"/> for a command that keeps none.
    /// </param>
    /// <param name="mutationRecord">
    /// The directory this run's verdicts of its mutation arms are recorded in, where it records them; <see langword="null"/>
    /// for a command that records none.
    /// </param>
    public string ToJson(bool cancelled, IReadOnlyList<string> unfinished, string? runDirectory = null, Func<string, string>? shown = null, RunId? run = null, string? mutationRecord = null)
        => Json(ExitCodeGiven(cancelled, unfinished), Summarize(cancelled, unfinished), cancelled, unfinished, stopped: false, run, runDirectory, shown ?? AsWritten, mutationRecord);

    /// <summary>
    /// The ledger as data for a run something other than its legs ended - a refusal of the whole run,
    /// a selection no host could take, or a log another run holds - with the code the process exits
    /// with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">What the process exits with.</param>
    /// <param name="stoppedBecause">The line the command ends on.</param>
    /// <param name="runDirectory">
    /// Where the run keeps its records, when it got as far as having a directory; otherwise
    /// <see langword="null"/>.
    /// </param>
    /// <param name="shown">
    /// How the document's reader is told a path or a line of the harness's own, as
    /// <see cref="IHarnessOutput.Shown"/> tells it; <see langword="null"/> to write each as it is.
    /// </param>
    /// <param name="run">
    /// The run this is, for a command that keeps runs, which a caller can cite it by whatever ended it;
    /// <see langword="null"/> for a command that keeps none.
    /// </param>
    /// <param name="mutationRecord">
    /// The directory this run's verdicts of its mutation arms are recorded in, where it records them; <see langword="null"/>
    /// for a command that records none.
    /// </param>
    /// <remarks>
    /// Whatever ended the run, a reader who asked for data is answered with data: the machine that
    /// dispatched a leg reads a host's standard output as this document, and text there - a table, a
    /// list of reasons - read as a host whose answer could not be read, where the host had said
    /// exactly what happened. The legs that had a line by then are in it, and nothing is claimed of
    /// the run itself: it neither passed nor completed, and reached no verdict of its own. An
    /// interruption is said as one, from the code it ends with, so a script never reads it as red.
    /// </remarks>
    public string ToJson(int exitCode, string stoppedBecause, string? runDirectory = null, Func<string, string>? shown = null, RunId? run = null, string? mutationRecord = null)
    {
        ArgumentNullException.ThrowIfNull(stoppedBecause);

        return Json(exitCode, stoppedBecause, cancelled: exitCode == HarnessExit.Cancelled, [], stopped: true, run, runDirectory, shown ?? AsWritten, mutationRecord);
    }

    /// <summary>
    /// The ledger as data for a command that stopped before any leg had a line - a leg nobody
    /// declared, a configuration that does not load, a runner nobody declared - with none, and the
    /// code the process exits with and the line it ends on.
    /// </summary>
    /// <param name="exitCode">What the process exits with.</param>
    /// <param name="stoppedBecause">The line the command ends on.</param>
    /// <param name="shown">
    /// How the document's reader is told a path or a line of the harness's own, as
    /// <see cref="IHarnessOutput.Shown"/> tells it; <see langword="null"/> to write each as it is.
    /// </param>
    /// <param name="run">
    /// The run this is, for a command that keeps runs, which a caller can cite it by whatever ended it;
    /// <see langword="null"/> for a command that keeps none.
    /// </param>
    public static string Stopped(int exitCode, string stoppedBecause, Func<string, string>? shown = null, RunId? run = null)
        => From([], new HarnessDefaults().DurationWarningFactor).ToJson(exitCode, stoppedBecause, shown: shown, run: run);

    /// <summary>The document, with every path and every line of the harness's own told as <paramref name="show"/> tells it.</summary>
    /// <remarks>
    /// A host answering another machine writes its home as <c>~</c> in what the harness wrote - where the
    /// records are, why a leg ended as it did, what a timing pattern matched, a build directory and its room,
    /// the developer environment it started in - as the same ledger's text form writes each of its rows. The
    /// last lines a phase printed are that program's own lines, beneath the table as here, and stay as it
    /// printed them.
    /// </remarks>
    private string Json(int exitCode, string summary, bool cancelled, IReadOnlyList<string> unfinished, bool stopped, RunId? run, string? runDirectory, Func<string, string> show, string? mutationRecord = null) => JsonSerializer.Serialize(
        new
        {
            Verdict = stopped ? null : Verdicts.Display(Verdict),
            ExitCode = exitCode,

            // The line the command ends on, beside the code it exits with, so a script reading the
            // document has what a reader of the terminal has.
            Summary = show(summary),

            // Which run this is, as the text form's first line says, whatever ended it: a run refused
            // before it had a directory keeps no records, and is cited by this alone.
            RunId = run?.Value,

            // Where the records are, as the text form's 'logs:' line says: a caller is told rather
            // than left to work out which tree a run wrote into.
            RunDirectory = runDirectory is null ? null : show(runDirectory),

            // Where the verdicts of a sweep's arms are kept, for the one command that records them: a
            // caller reading them back needs no rule to work out which tree they were kept in.
            MutationRecord = mutationRecord is null ? null : show(mutationRecord),
            Passed = !stopped && !cancelled && Passed,
            Cancelled = cancelled,
            Unfinished = unfinished,

            // Beside Passed rather than folded into it: a script comparing two runs has to be able
            // to tell "nothing failed" from "nothing failed and everything reported", and those are
            // the same boolean only when every leg ran. A run that was interrupted, or that left a
            // leg running, reported on fewer legs than it was asked about whatever its rows say.
            Complete = !stopped && !cancelled && Complete && unfinished.Count == 0,

            // Each set-up a leg left running - on its line, or stopped with none - and the command that brings it down:
            // a script told the run did not complete is told what it left behind.
            SetUpsLeftRunning = LeftRunning.Count == 0
                ? null
                : LeftRunning.Select(left => new
                {
                    left.Leg,
                    SetUp = left.SetUp.Name,
                    left.SetUp.Up,
                    left.SetUp.Inputs,
                    left.SetUp.Detail,
                    Remedy = SetUps.SetUpFact.Remedy(left.SetUp.Name, left.SetUp.Inputs),
                }),
            Legs = Lines.Select(line => new
            {
                line.Leg,
                Verdict = Verdicts.Display(line.Verdict),
                Failure = Verdicts.IsFailure(line.Verdict),
                DurationSeconds = Seconds(line.Duration),
                CommandSeconds = Seconds(line.CommandTime),
                OverheadSeconds = Seconds(line.Overhead),
                Detail = show(line.Detail),
                line.TimingsSuspect,
                TimingNotes = line.TimingNotes.Select(show),
                line.TestCount,

                // What the count belongs to, carried from where it was made, so a ledger read back
                // from another host groups it as it was counted; and whether it stands apart.
                line.Project,
                line.TestSet,
                line.TestCountDiffers,
                TestCountNote = line.TestCountNote is null ? null : show(line.TestCountNote),

                // Only for a leg another host ran, whose records are in that host's own run.
                RunDirectory = line.RunDirectory is null ? null : show(line.RunDirectory),

                // The run a host ran the leg under, where another did.
                HostRunId = line.HostRunId?.Value,

                // Where the leg's own records are, wherever it ran, and each phase log in it by name: a host's paths as
                // the host spelled them, its home as ~.
                Records = line.Records is null ? null : show(line.Records),
                Logs = line.Logs.Count > 0 ? line.Logs.Select(log => new { log.Phase, File = show(log.File) }) : null,

                // Only where a step was left out for the leg's operating system.
                SkippedSteps = line.SkippedSteps.Count > 0 ? line.SkippedSteps : null,

                // Each only where it names something: the steps the leg began, the manual ones among
                // them, and the steps the run left out - a plain run lists the manual steps it did not
                // run, so it is never read as having run them.
                RanSteps = line.RanSteps.Count > 0 ? line.RanSteps : null,
                ManualSteps = line.ManualSteps.Count > 0 ? line.ManualSteps : null,
                UnselectedSteps = line.UnselectedSteps.Count > 0 ? line.UnselectedSteps : null,

                // Only for a leg that has one: a phase of its own did not pass.
                LogTail = line.LogTail.Count > 0 ? line.LogTail : null,
                line.TailPhase,

                // Only where CMake named the compilers it configured the leg's build with.
                Compilers = line.Compilers.Count > 0
                    ? line.Compilers.Select(compiler => new { compiler.Language, compiler.Id, compiler.Version })
                    : null,

                // Only where the leg's toolchain names a developer environment and it was set up.
                DeveloperEnvironment = line.DeveloperEnvironment is { } environment
                    ? environment with { InstallationPath = show(environment.InstallationPath) }
                    : null,

                // Only where the run was asked to set up and the leg's work named a set-up: how each came up, and went.
                SetUps = line.SetUps.Count > 0 ? line.SetUps : null,

                // Only where the leg is heavy and its machine declares admission: taken or not, how long it waited,
                // the memory in use, the room it claimed, what held the slots or claimed the room where it was not taken
                // for want of either, and the record they are kept in.
                Admission = line.Admission is { } admission
                    ? admission with
                    {
                        Holders = admission.Holders?.Select(show).ToList(),
                        Record = admission.Record is { } record ? show(record) : null,
                        Room = admission.Room is { } room ? show(room) : null,
                    }
                    : null,

                // Only where the command measured the leg's build directory: clean.
                Space = line.Space is { } space
                    ? space with { Directory = show(space.Directory), Disk = space.Disk is { } disk ? disk with { Filesystem = show(disk.Filesystem) } : null }
                    : null,

                // Only where a step kept something, each as sync --pull takes it.
                KeptOutputs = line.KeptOutputs.Count > 0 ? line.KeptOutputs : null,

                // Only where the leg's test invocation did not pass and named paths to keep: where the evidence is, how
                // much was kept, and each file by name - kept, refused over the cap, unreadable, or a path that named
                // nothing. A host's directory is spelled as that host spells it, as its records are.
                KeptOnFailure = line.KeptOnFailure is { } keptEvidence ? new
                {
                    Directory = show(keptEvidence.Directory),
                    Files = keptEvidence.Kept.Count,
                    keptEvidence.Bytes,
                    keptEvidence.Kept,
                    keptEvidence.RefusedOverCap,
                    keptEvidence.Unreadable,
                    keptEvidence.NamedNothing,
                } : null,

                // Where the leg worked: its path alone for a leg in its tree, and for a copy the tree it is a copy of and the
                // reading it holds, where that is known - this run's, or what a run on what is staged found there.
                Tree = line.Tree is { } tree
                    ? new
                    {
                        Path = show(tree.Path),
                        CopyOf = tree.CopyOf is { } copyOf ? show(copyOf) : null,
                        tree.Reading?.Commit,
                        tree.Reading?.UncommittedChanges,
                        tree.Reading?.ReadBy,
                    }
                    : null,

                // Only for a leg a sweep asked about arms: each arm, whatever it reached, an arm not selected among them, with
                // what its run measured beside what it declares, and where its records are.
                Arms = line.Arms.Count > 0 ? line.Arms.Select(arm => ArmDocument(arm, show)) : null,

                // Only for a leg check-mutations --pending swept: how many of its arms held a passed, and how many it swept.
                line.PendingSweep,

                // Only for a leg a sweep stopped a control of: each control that did not pass, with what ctest registers for its red cases.
                Controls = line.Controls.Count > 0 ? line.Controls.Select(control => ControlDocument(control, show)) : null,
                Timings = line.Timings.Select(timing => new
                {
                    timing.Phase,
                    Text = show(timing.Text),
                    Value = show(timing.Value),
                }),
            }),
        },
        JsonOptions);

    /// <summary>
    /// A control's line as data: what it came to and why, the cases it reddened, where its records are, and what ctest
    /// registers for them - each path and line of the harness's own told as <paramref name="show"/> tells it.
    /// </summary>
    private static object ControlDocument(ControlEntry control, Func<string, string> show) => new
    {
        control.Runner,
        control.Verdict,
        Detail = show(control.Detail),
        control.Reds,
        Records = show(control.Records),
        Ctest = control.Ctest is { } ctest
            ? new
            {
                ctest.Matched,
                ctest.Unread,
                Tests = ctest.Tests.Select(test => new
                {
                    test.Test,
                    test.Cases,
                    NotApplied = test.NotApplied.Select(notApplied => new { notApplied.Name, notApplied.Value, notApplied.SetUpBy }),
                }),
            }
            : null,
    };

    /// <summary>
    /// An arm's line as the record it leaves among its records holds it: the very shape it has beneath its leg in the
    /// ledger's data, so one reader reads both.
    /// </summary>
    /// <param name="arm">The arm's line.</param>
    internal static string ArmJson(ArmEntry arm)
    {
        ArgumentNullException.ThrowIfNull(arm);

        return JsonSerializer.Serialize(ArmDocument(arm, AsWritten), JsonOptions);
    }

    /// <summary>
    /// An arm's line as data: what it reached and why, with what its run measured beside what it declares, and where its
    /// records are - each path and line of the harness's own told as <paramref name="show"/> tells it.
    /// </summary>
    private static object ArmDocument(ArmEntry arm, Func<string, string> show) => new
    {
        arm.Arm,
        Verdict = Verdicts.Display(arm.Verdict),
        Failure = Verdicts.IsFailure(arm.Verdict),
        Detail = show(arm.Detail),
        DurationSeconds = Seconds(arm.Duration),
        arm.Worker,
        arm.Cases,
        arm.DeclaredCases,
        arm.Reds,
        arm.DeclaredReds,
        Records = arm.Records is { } records ? show(records) : null,
        arm.Key,
        arm.Held,
        Pending = arm.Pending is { } pending ? new { pending.Code, Said = show(pending.Said) } : null,
    };

    /// <summary>
    /// Which legs ran a different number of tests from the rest of their project and test set, and
    /// what the rest ran.
    /// </summary>
    /// <remarks>
    /// Legs running the same suite are meant to run the same tests. A leg that reports three where
    /// its siblings report four hundred is green on both the exit code and the success pattern, and
    /// neither of those can see the difference — a filter that matched almost nothing, a discovery
    /// step that failed quietly, a test project excluded by a stale glob. The count is the only
    /// thing in the ledger that can, so a leg disagreeing with the others is marked.
    /// <para>
    /// The same suite is the same project and the same test set: a leg testing another project runs
    /// another suite, and so does one whose test settings name a set of its own - a platform's own
    /// tests, a sanitizer leg's subset - where a count that differs is expected. A leg that resolves no
    /// project is compared with nothing. Within one, compared only where at least three legs reported a
    /// count: with two, "which one is wrong" has no answer, and marking both says nothing a reader can
    /// act on. Emulated legs are compared with the rest here, because a count does not depend on how
    /// fast the machine is.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string> CompareCounts(IReadOnlyList<LegEntry> entries)
    {
        var marks = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // One project at a time, and one test set within it, each named as configuration keys are.
        var suites = entries
            .Where(entry => entry.TestCount is not null && entry.Project is not null && !Verdicts.IsFailure(entry.Verdict))
            .GroupBy(entry => entry.Project, StringComparer.OrdinalIgnoreCase)
            .SelectMany(project => project.GroupBy(entry => entry.TestSet, StringComparer.OrdinalIgnoreCase));

        foreach (var suite in suites)
        {
            var counted = suite.ToList();

            if (counted.Count < 3)
            {
                continue;
            }

            var agreed = counted
                .GroupBy(entry => entry.TestCount!.Value)
                .OrderByDescending(group => group.Count())
                .First();

            if (agreed.Count() == counted.Count)
            {
                continue;
            }

            foreach (var entry in counted.Where(entry => entry.TestCount != agreed.Key))
            {
                marks[entry.Leg] = $"it ran {ReportText.Counted(entry.TestCount!.Value, "test")}, where {ReportText.Counted(agreed.Count(), "other leg")} ran {agreed.Key}";
            }
        }

        return marks;
    }

    /// <summary>
    /// Which legs have a phase slower than its siblings, and by how much. Phases are compared only
    /// with the same phase on legs of the same kind: an emulated leg is never compared with a native
    /// one, because the two have no common scale and every emulated leg would be marked forever.
    /// </summary>
    private static Dictionary<string, List<string>> Compare(IReadOnlyList<LegEntry> entries, double factor)
    {
        var marks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        if (factor <= 0)
        {
            return marks;
        }

        // One kind at a time, and one phase within it, named as configuration keys are.
        var groups = entries
            .SelectMany(entry => entry.Phases.Select(phase => (entry.Leg, entry.Emulated, Phase: phase)))
            .GroupBy(item => item.Emulated)
            .SelectMany(kind => kind.GroupBy(item => item.Phase.Phase, StringComparer.OrdinalIgnoreCase));

        foreach (var group in groups)
        {
            var members = group.ToList();

            if (members.Count < 2)
            {
                continue;
            }

            for (var index = 0; index < members.Count; index++)
            {
                var member = members[index];

                // Measured against the middle of its siblings rather than the fastest: with three
                // legs where one is slow, the fastest alone would mark the two honest ones too.
                // Excluded by position, since two legs can legitimately record identical phases.
                var baseline = Median(members.Where((_, other) => other != index).Select(other => other.Phase.Duration));

                if (baseline <= TimeSpan.Zero || member.Phase.Duration <= baseline * factor)
                {
                    continue;
                }

                if (!marks.TryGetValue(member.Leg, out var notes))
                {
                    notes = [];
                    marks[member.Leg] = notes;
                }

                notes.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{member.Phase.Phase} took {FormatDuration(member.Phase.Duration)} against {FormatDuration(baseline)} on sibling legs"));
            }
        }

        return marks;
    }

    private static TimeSpan Median(IEnumerable<TimeSpan> durations)
    {
        var ordered = durations.Order().ToList();

        return ordered.Count switch
        {
            0 => TimeSpan.Zero,
            _ when ordered.Count % 2 == 1 => ordered[ordered.Count / 2],
            _ => (ordered[(ordered.Count / 2) - 1] + ordered[ordered.Count / 2]) / 2,
        };
    }

    /// <summary>
    /// What one leg has to say for itself, as the leg said it.
    /// </summary>
    /// <param name="entry">The leg's entry.</param>
    /// <remarks>
    /// The count where the leg has one and nothing more urgent to say. A passing leg's detail is
    /// the evidence it passed on, and two legs running the same tests that report different counts
    /// are only comparable if the table shows both: one of them quietly skipped a group, and a
    /// blank column beside a green verdict hides exactly that.
    /// <para>
    /// Deliberately without the timing mark. The notes travel beside the detail, not inside it, so
    /// a reader that receives this ledger and reports it again — which is every remote leg, whose
    /// host runs this same code and answers with <c>--json</c> — composes the mark once rather than
    /// once per machine the answer passed through. Measured on a consumer's two-leg run: the whole
    /// explanation appeared twice, concatenated, in the one column a reader actually looks at.
    /// </para>
    /// </remarks>
    private static string Detail(LegEntry entry)
        => entry.Detail.Length == 0 && entry.TestCount is { } count
            ? $"{count} test{(count == 1 ? string.Empty : "s")}"
            : entry.Detail;

    /// <summary>
    /// <paramref name="detail"/> with the compilers the leg built with, the developer environment it
    /// started in, how its machine admitted it, a test count that stands apart and the timing mark, for a
    /// line that shows one leg.
    /// </summary>
    /// <param name="detail">What the leg said.</param>
    /// <param name="tree">The tree it worked in, whose reading it says where it worked in a copy.</param>
    /// <param name="notes">Why its timings are suspect, if they are.</param>
    /// <param name="compilers">The compilers CMake configured its build with.</param>
    /// <param name="developerEnvironment">The developer environment its processes started in, if one was set up.</param>
    /// <param name="setUps">What it did with each set-up its work named.</param>
    /// <param name="admission">How its machine took it, where it is heavy and the machine declares admission.</param>
    /// <param name="testCountNote">Why its test count stands apart from its siblings', if it does.</param>
    /// <remarks>
    /// Composed here, from fields that travel beside the detail rather than inside it, so a ledger a
    /// host reported and this machine reports again names each once.
    /// </remarks>
    internal static string Marked(
        string detail,
        WorkedTree? tree,
        IReadOnlyList<string> notes,
        IReadOnlyList<Build.CompilerFact> compilers,
        Hosts.DeveloperEnvironmentFact? developerEnvironment,
        IReadOnlyList<SetUps.SetUpFact> setUps,
        AdmissionFact? admission,
        string? testCountNote)
    {
        var parts = new List<string>();

        if (detail.Length > 0)
        {
            parts.Add(detail);
        }

        // Which tree a leg on a copy tested, as its machine was told it: a run's legs here and on hosts can be read for
        // the same reading by this alone.
        if (tree?.Said is { } read)
        {
            parts.Add(read);
        }

        if (Build.CompilerFacts.Describe(compilers) is { } compiler)
        {
            parts.Add(compiler);
        }

        if (developerEnvironment is not null)
        {
            parts.Add(developerEnvironment.Describe());
        }

        parts.AddRange(setUps.Select(setUp => setUp.Describe()));

        // A leg not let start says why as its detail; one let start says how, beside it.
        if (admission?.Describe() is { } admitted)
        {
            parts.Add(admitted);
        }

        if (testCountNote is { Length: > 0 })
        {
            parts.Add("test count differs: " + testCountNote);
        }

        if (notes.Count > 0)
        {
            parts.Add("timings suspect: " + string.Join("; ", notes));
        }

        return string.Join("; ", parts);
    }

    private static int Width(string heading, IEnumerable<string> values)
        => Math.Max(heading.Length, values.Select(value => value.Length).DefaultIfEmpty(0).Max());

    private static string Row(string leg, string verdict, string duration, string detail, int legWidth, int verdictWidth, int durationWidth)
        => (leg.PadRight(legWidth) + "  " + verdict.PadRight(verdictWidth) + "  " + duration.PadLeft(durationWidth) + "  " + detail).TrimEnd();

    /// <summary>A duration as every seconds field of <c>--json</c> gives it: to the millisecond.</summary>
    /// <param name="duration">The duration.</param>
    internal static double Seconds(TimeSpan duration) => Math.Round(duration.TotalSeconds, 3);
}
