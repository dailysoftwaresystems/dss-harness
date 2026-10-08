using RepoHarness.Core.Results;

namespace RepoHarness.Core.Execution;

/// <summary>
/// The closed vocabulary a leg's result is reported in. Every declared leg reaches exactly one of
/// these, and nothing else: a report whose words are invented per command cannot be compared across
/// commands, and a leg with no word for what happened to it silently vanishes from the table.
/// </summary>
public enum LegVerdict
{
    /// <summary>Ran to completion, succeeded, and its success pattern matched.</summary>
    Passed,

    /// <summary>
    /// Ran to completion and reported failure. The code is broken. Of a mutation arm: its build failed at a step that
    /// is no object depending on its site - a link, another object - or named no step that failed.
    /// </summary>
    Failed,

    /// <summary>
    /// Exited zero, but its success pattern never matched its own output. Measured three ways: a
    /// suite that printed <c>failed=0</c> while exiting 2, an exit code read after a pipe, and a
    /// test command that exited zero having run no tests at all. Of a mutation arm: its build, or its
    /// paired control's, passed with an object that depends on a site not rebuilt.
    /// </summary>
    Unwitnessed,

    /// <summary>
    /// Files the tests read changed while they ran, so some tests saw the old files and some the
    /// new. Measured: eight failures, all passing seconds later on the unchanged tree. For a leg on
    /// another machine, also: a file its copy needed changed, or was removed, after the run read
    /// the tree as it began and before the file was carried there - or went while the tree was
    /// being read - so the copy could not be made the tree the run began with, and nothing of the
    /// leg ran.
    /// </summary>
    InputsMoved,

    /// <summary>
    /// Whether those files held still could not be established. Never reported as passed: an
    /// unreadable snapshot is not evidence that nothing moved. Nor is anything decided on a phase's
    /// output that could not be read back whole from its log. Of a mutation arm: ninja's log could
    /// not be read around its build, so nothing witnessed what it rebuilt; or the report its run wrote
    /// could not be read from its file, so nothing says which cases failed.
    /// </summary>
    Unmeasured,

    /// <summary>Another process used the leg's build directory while it ran.</summary>
    Contended,

    /// <summary>
    /// Filtered out by <c>--legs</c>. Not a failure: nobody asked for it. Of a mutation arm: left out of
    /// the leg by <c>--arms</c> or by its S row - and of a leg whose sweep drives no arm.
    /// </summary>
    SkippedNotSelected,

    /// <summary>
    /// No host can take the leg, its host or its tree could not be reached, whether a program it
    /// starts is there could not be established, or git could not answer in its tree. A warning: a
    /// switched-off machine is normal. Of a sweep: no worker fits the room left or the path limit, and
    /// every arm of the leg is skipped with it.
    /// </summary>
    SkippedUnavailable,

    /// <summary>
    /// A required tool is not installed. A warning, and named: executables are resolved before a
    /// leg starts so this is never a failure halfway through a build.
    /// </summary>
    SkippedToolMissing,

    /// <summary>Another run holds the lock for this leg, so nothing ran.</summary>
    RefusedLocked,

    /// <summary>
    /// A heavy leg waited as long as its machine allows for a heavy-leg slot, or holding one for the memory in use to
    /// fall below the machine's limit, or for room for its build beside what the other admitted legs claim, and nothing
    /// of it ran. Never a failure of the code: the machine could not take it. Of a sweep: a unit of it - a worker, an
    /// arm - waited so, and each arm the sweep had left once its machine refused one.
    /// </summary>
    NotAdmitted,

    /// <summary>
    /// Another live run owns this run's log path. Distinct from <see cref="RefusedLocked"/> because
    /// the remedies differ: one waits for the other run, the other points this run elsewhere.
    /// </summary>
    LogHeld,

    /// <summary>
    /// The harness could not produce a verdict. Deliberately distinct from <see cref="Failed"/>:
    /// "your code is broken" and "the harness broke" call for different responses. Of a mutation arm:
    /// a site could not be put back as it was, or a failure nobody named ended its driving.
    /// </summary>
    Poisoned,

    /// <summary>
    /// Its work was begun or due, and was stopped before it reached a verdict of its own. Something stopped its build
    /// from outside before it finished: ninja, which says why whenever it ends a build itself, said nothing of why, or
    /// said it was interrupted. Or a mutation arm was not driven to a verdict: its sweep was stopped, or ended by a
    /// refusal of the run, while it was driven or before; its own build, or its paired control's, was stopped from
    /// outside; no worker was left to run it; or the unmutated run of its test binary did not pass, leaving nothing to
    /// judge its own run against. Says
    /// nothing about the code: distinct from <see cref="Failed"/>, which running again repeats, where running again
    /// finishes this one. Not a failure, and not a pass: a run whose legs include one, and nothing failed, is
    /// incomplete.
    /// </summary>
    Stopped,

    /// <summary>
    /// A mutation arm's declaration did not hold: a site or a cited text is not there, or a site is spelt otherwise than
    /// the tree spells it or is no file the sweep's reading of the tree holds; the text it mutates is not in its site exactly once, or is replaced by itself; its target or
    /// its runner is not built, or no object they build depends on a site; its mutation reddened other cases than it
    /// declares, ran another number of cases, left a neighbour declared green unrun or left out its diagnostic; a
    /// mutation declared to redden a test does not compile; or one declared to stop the build built, or its paired
    /// control did not. The registry's words, or the code the arm guards, are wrong - distinct from
    /// <see cref="Survived"/>, where the declaration held and the tests did not fail.
    /// </summary>
    Violated,

    /// <summary>
    /// A mutation arm's mutation built and ran, and no case reddened: the tests that guard the mutated code never
    /// failed, so they prove nothing about it. The finding mutation testing exists to make.
    /// </summary>
    Survived,

    /// <summary>
    /// A mutation arm's run failed, and nothing ties the failure to a case: the runner wrote no report, or one that
    /// is no JUnit report, or exited failing with a report naming no failing case - a crash after it was written, a leak
    /// checker at exit - or ran past its bound, or printed nothing for as long as a phase may, and was stopped. Something
    /// failed, and nothing says which case did.
    /// </summary>
    Unattributed,
}

/// <summary>Everything the report and the exit code need to know about one verdict.</summary>
/// <param name="Verdict">The verdict.</param>
/// <param name="Display">Its name as the ledger and docs/architecture.md spell it.</param>
/// <param name="IsFailure">Whether it makes the run red.</param>
/// <param name="Rank">
/// How fundamental it is; the smaller number wins when legs disagree. A leg whose inputs moved is
/// not reported as failed even if its tests failed, because what failed was a tree that never existed.
/// </param>
/// <param name="ExitCode">The process exit code a run reports when this verdict decides it.</param>
public sealed record VerdictInfo(LegVerdict Verdict, string Display, bool IsFailure, int Rank, int ExitCode)
{
    /// <summary>
    /// Whether only a sweep of mutation arms reaches it - what an arm's judge alone decides - so that no build, test or
    /// run ever exits with its code, that code is free to mean something else of theirs, and no refusal carrying it is
    /// read as it (<see cref="Verdicts.ForRefusal"/>).
    /// </summary>
    public bool OfASweep { get; init; }
}

/// <summary>
/// A verdict together with the sentence that explains it, produced so that a leg which reached no
/// verdict cannot be dropped: there is no way to build one of these without saying what happened.
/// </summary>
/// <param name="Verdict">The verdict the leg reached.</param>
/// <param name="Detail">What produced it, as the ledger's DETAIL column shows it.</param>
public sealed record ReachedVerdict(LegVerdict Verdict, string Detail)
{
    /// <summary>A leg that reached <paramref name="verdict"/>.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <param name="detail">What produced it.</param>
    public static ReachedVerdict Of(LegVerdict verdict, string detail = "") => new(verdict, detail);

    /// <summary>
    /// <paramref name="verdict"/> when the leg reached one, and <see cref="LegVerdict.Poisoned"/>
    /// naming the leg when it did not. A leg that finishes with no verdict is a defect in the
    /// harness itself, and it fails the run rather than silently vanishing from the report.
    /// </summary>
    /// <param name="verdict">The verdict the leg reached, if it reached one.</param>
    /// <param name="leg">The leg's name, so the poisoned entry says which one it was.</param>
    /// <param name="detail">What produced the verdict, when there is one.</param>
    public static ReachedVerdict OrPoisoned(LegVerdict? verdict, string leg, string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(leg);

        return verdict is { } reached
            ? new ReachedVerdict(reached, detail ?? string.Empty)
            : new ReachedVerdict(
                LegVerdict.Poisoned,
                $"leg '{leg}' finished without reaching a verdict; this is a defect in the harness");
    }
}

/// <summary>
/// The facts about each verdict, in one place: what it is called, whether it is a failure, how it
/// ranks against the others, and what a run exits with when it decides the run.
/// </summary>
/// <remarks>
/// Kept here rather than spread over the commands that report verdicts, because three commands
/// reporting the same verdict under three spellings, or with three exit codes, is exactly what a
/// closed vocabulary exists to prevent.
/// </remarks>
public static class Verdicts
{
    private static readonly IReadOnlyDictionary<LegVerdict, VerdictInfo> Table = new Dictionary<LegVerdict, VerdictInfo>
    {
        // Ranks 0-11 are the failures, in the order docs/architecture.md gives for disagreeing legs, each
        // deciding the exit code of a run it is the worst verdict of; unmeasured shares inputs-moved's. A
        // finding about the code outranks the absence of evidence: failed, and the three a mutation arm
        // reaches, come before unwitnessed. Every failure outranks everything that is none, and every verdict
        // a leg reaches without one of its own - a stopped build and the two skips, whose code is incomplete -
        // outranks a pass, so the worst verdict of a run is a failure wherever one failed, and one of those
        // wherever it is incomplete: a run with an unavailable leg summarises as that, never as an unqualified
        // success. A leg nobody asked for outranks nothing at all.
        [LegVerdict.Poisoned] = new(LegVerdict.Poisoned, "poisoned", true, 0, HarnessExit.InternalError),
        [LegVerdict.Unmeasured] = new(LegVerdict.Unmeasured, "unmeasured", true, 1, LegExit.InputsMoved),
        [LegVerdict.InputsMoved] = new(LegVerdict.InputsMoved, "inputs-moved", true, 2, LegExit.InputsMoved),
        [LegVerdict.Contended] = new(LegVerdict.Contended, "contended", true, 3, LegExit.Contended),
        [LegVerdict.LogHeld] = new(LegVerdict.LogHeld, "log-held", true, 4, LegExit.LogHeld),
        [LegVerdict.RefusedLocked] = new(LegVerdict.RefusedLocked, "refused-locked", true, 5, HarnessExit.Refused),
        [LegVerdict.NotAdmitted] = new(LegVerdict.NotAdmitted, "not-admitted", true, 6, LegExit.NotAdmitted),
        [LegVerdict.Failed] = new(LegVerdict.Failed, "failed", true, 7, HarnessExit.CommandFailed),
        [LegVerdict.Violated] = new(LegVerdict.Violated, "violated", true, 8, LegExit.Violated) { OfASweep = true },
        [LegVerdict.Survived] = new(LegVerdict.Survived, "survived", true, 9, LegExit.Survived) { OfASweep = true },
        [LegVerdict.Unattributed] = new(LegVerdict.Unattributed, "unattributed", true, 10, LegExit.Unattributed) { OfASweep = true },
        [LegVerdict.Unwitnessed] = new(LegVerdict.Unwitnessed, "unwitnessed", true, 11, LegExit.Unwitnessed),

        // Above the skips, since its leg's work was begun or due and was stopped, where theirs never began.
        [LegVerdict.Stopped] = new(LegVerdict.Stopped, "stopped", false, 12, HarnessExit.Incomplete),
        [LegVerdict.SkippedUnavailable] = new(LegVerdict.SkippedUnavailable, "skipped-unavailable", false, 13, HarnessExit.Incomplete),
        [LegVerdict.SkippedToolMissing] = new(LegVerdict.SkippedToolMissing, "skipped-tool-missing", false, 14, HarnessExit.Incomplete),
        [LegVerdict.Passed] = new(LegVerdict.Passed, "passed", false, 15, HarnessExit.Success),
        [LegVerdict.SkippedNotSelected] = new(LegVerdict.SkippedNotSelected, "skipped-not-selected", false, 16, HarnessExit.Success),
    };

    /// <summary>Every verdict, most fundamental first.</summary>
    public static IReadOnlyList<VerdictInfo> All { get; } = [.. Table.Values.OrderBy(info => info.Rank)];

    /// <summary>Everything known about <paramref name="verdict"/>.</summary>
    /// <param name="verdict">The verdict.</param>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a declared verdict.</exception>
    public static VerdictInfo Describe(LegVerdict verdict)
        => Table.TryGetValue(verdict, out var info)
            ? info
            : throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "Not a declared verdict.");

    /// <summary>The verdict's name as the ledger prints it and docs/architecture.md spells it.</summary>
    /// <param name="verdict">The verdict.</param>
    public static string Display(LegVerdict verdict) => Describe(verdict).Display;

    /// <summary>
    /// Whether the verdict makes the run red. <c>skipped-unavailable</c> and
    /// <c>skipped-tool-missing</c> are warnings rather than failures; a command asked for such a leg
    /// by name decides for itself what that means, as <c>legs</c> does.
    /// </summary>
    /// <param name="verdict">The verdict.</param>
    public static bool IsFailure(LegVerdict verdict) => Describe(verdict).IsFailure;

    /// <summary>
    /// Whether the leg was asked to run and did no work: no host could run it, or a tool it needs
    /// is missing.
    /// </summary>
    /// <remarks>
    /// Neither a failure nor a pass, and the run's summary needs all three: a leg that was asked
    /// for and could not run proves nothing about the code, so counting it as passed reports
    /// evidence nobody gathered. <c>skipped-not-selected</c> is deliberately not one of these —
    /// nobody asked for that leg, so its absence is an answer rather than a gap in one, and counting
    /// it would make every run narrowed by <c>--legs</c> report as incomplete. Listed by verdict
    /// rather than by a flag on the table, because these are exactly the verdicts a leg reaches
    /// after being asked to run and not running, and a new verdict should have to say which it is.
    /// </remarks>
    /// <param name="verdict">The verdict.</param>
    public static bool IsSkip(LegVerdict verdict) => verdict
        is LegVerdict.SkippedUnavailable
        or LegVerdict.SkippedToolMissing;

    /// <summary>
    /// Whether the leg reached no verdict of its own: it did no work (<see cref="IsSkip"/>), or something stopped its
    /// work from outside before it finished. A run whose legs include one, and nothing failed, is incomplete - read from
    /// the code the table gives the verdict, and read alike by the run's exit code and by the legs its summary names, so
    /// the two cannot disagree about which they are.
    /// </summary>
    /// <param name="verdict">The verdict.</param>
    public static bool ReachedNone(LegVerdict verdict) => Describe(verdict) is { IsFailure: false, ExitCode: HarnessExit.Incomplete };

    /// <summary>How fundamental the verdict is; the smaller number decides when legs disagree.</summary>
    /// <param name="verdict">The verdict.</param>
    public static int Rank(LegVerdict verdict) => Describe(verdict).Rank;

    /// <summary>The exit code a run reports when <paramref name="verdict"/> decides it.</summary>
    /// <param name="verdict">The verdict.</param>
    public static int ExitCodeFor(LegVerdict verdict) => Describe(verdict).ExitCode;

    /// <summary>
    /// The verdict a display name belongs to, or <see langword="null"/> when nothing is spelled that
    /// way.
    /// </summary>
    /// <param name="display">The name as a ledger writes it, such as <c>inputs-moved</c>.</param>
    /// <remarks>
    /// Read back from the same table that writes it, so a ledger this build produced is one this
    /// build can read — which is what lets a host report its leg's verdict to the machine that asked
    /// for it. A name this build does not know is not guessed at: the caller decides what an
    /// unreadable answer means, and every caller here treats it as no verdict at all.
    /// </remarks>
    public static LegVerdict? Parse(string? display)
        => display is null
            ? null
            : All
                .Where(info => string.Equals(info.Display, display, StringComparison.Ordinal))
                .Select(info => (LegVerdict?)info.Verdict)
                .FirstOrDefault();

    /// <summary>
    /// The verdict that decides a run over <paramref name="verdicts"/>: the most fundamental one
    /// present. An empty sequence is <see cref="LegVerdict.Passed"/>, because no leg failed; a run
    /// that selected no leg at all is refused where the legs are selected, not scored here.
    /// </summary>
    /// <param name="verdicts">Every selected leg's verdict.</param>
    public static LegVerdict Worst(IEnumerable<LegVerdict> verdicts)
    {
        ArgumentNullException.ThrowIfNull(verdicts);

        LegVerdict? worst = null;
        var rank = int.MaxValue;

        foreach (var verdict in verdicts)
        {
            var candidate = Rank(verdict);

            if (candidate < rank)
            {
                worst = verdict;
                rank = candidate;
            }
        }

        return worst ?? LegVerdict.Passed;
    }

    /// <summary>
    /// The verdict work that ended in <paramref name="exception"/> comes to - a leg's, or a unit's of a sweep - where the
    /// exception neither stopped it nor refuses the run: the verdict a refusal names; <c>unmeasured</c> where what a
    /// phase printed could not be read back from its log, since nothing is decided on what is left of one; and
    /// <c>failed</c> where its cause is one this build can name. <see langword="null"/> where nobody named it, which is
    /// <c>poisoned</c>, said by whoever caught it in its own words.
    /// </summary>
    /// <param name="exception">What the work raised.</param>
    public static ReachedVerdict? ForFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            HarnessException refused => ReachedVerdict.Of(ForRefusal(refused.ExitCode), refused.Message),
            PhaseOutputUnreadException unread => ReachedVerdict.Of(LegVerdict.Unmeasured, unread.Message),
            _ when KnownCauses.Names(exception) => ReachedVerdict.Of(LegVerdict.Failed, exception.Message),
            _ => null,
        };
    }

    /// <summary>
    /// The verdict a refusal carrying <paramref name="exitCode"/> gives the leg it stopped. A leg
    /// that refuses is still a leg with a verdict: reported as poisoned, a host that is merely
    /// switched off would read as a defect in the tool.
    /// </summary>
    /// <remarks>
    /// Never one only a sweep reaches (<see cref="VerdictInfo.OfASweep"/>): those are what an arm's judge alone decides,
    /// and a refusal may carry any code - what a program a host ran exited with, among them - so one carrying such a
    /// verdict's code names nothing a judge decided, and is poisoned as any other code nobody gave a meaning is.
    /// </remarks>
    /// <param name="exitCode">The code the refusal carried, as <see cref="HarnessException.ExitCode"/> reports it.</param>
    public static LegVerdict ForRefusal(int exitCode) => exitCode switch
    {
        HarnessExit.ToolMissing => LegVerdict.SkippedToolMissing,
        HarnessExit.HostUnavailable => LegVerdict.SkippedUnavailable,
        HarnessExit.CommandFailed => LegVerdict.Failed,
        LegExit.InputsMoved => LegVerdict.InputsMoved,
        LegExit.Contended => LegVerdict.Contended,
        LegExit.Unwitnessed => LegVerdict.Unwitnessed,
        LegExit.LogHeld => LegVerdict.LogHeld,
        LegExit.NotAdmitted => LegVerdict.NotAdmitted,
        _ => LegVerdict.Poisoned,
    };
}
