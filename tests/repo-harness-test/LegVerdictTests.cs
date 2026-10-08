using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The verdict vocabulary is a promise to whoever reads a gate: one word per outcome, one exit code
/// per word, and a fixed order when legs disagree. All three are pinned here, because a change to
/// any of them changes what a red build means without changing a single test that runs a leg.
/// </summary>
public sealed class LegVerdictTests
{
    [Fact]
    public void EveryVerdict_HasAName_AFailureFlag_AndAnExitCode()
    {
        foreach (var verdict in Enum.GetValues<LegVerdict>())
        {
            var info = Verdicts.Describe(verdict);

            Assert.False(string.IsNullOrWhiteSpace(info.Display), $"{verdict} has no display name");
            Assert.Equal(info.Display, info.Display.ToLowerInvariant());
            Assert.True(info.ExitCode >= 0, $"{verdict} has no exit code");
        }
    }

    [Fact]
    public void DisplayNames_AreTheOnesTheDocumentSpells()
    {
        // The table in docs/architecture.md, word for word. A verdict spelled two ways cannot be
        // grepped out of a gate's output.
        Assert.Equal("passed", Verdicts.Display(LegVerdict.Passed));
        Assert.Equal("failed", Verdicts.Display(LegVerdict.Failed));
        Assert.Equal("unwitnessed", Verdicts.Display(LegVerdict.Unwitnessed));
        Assert.Equal("inputs-moved", Verdicts.Display(LegVerdict.InputsMoved));
        Assert.Equal("unmeasured", Verdicts.Display(LegVerdict.Unmeasured));
        Assert.Equal("contended", Verdicts.Display(LegVerdict.Contended));
        Assert.Equal("skipped-not-selected", Verdicts.Display(LegVerdict.SkippedNotSelected));
        Assert.Equal("skipped-unavailable", Verdicts.Display(LegVerdict.SkippedUnavailable));
        Assert.Equal("skipped-tool-missing", Verdicts.Display(LegVerdict.SkippedToolMissing));
        Assert.Equal("refused-locked", Verdicts.Display(LegVerdict.RefusedLocked));
        Assert.Equal("log-held", Verdicts.Display(LegVerdict.LogHeld));
        Assert.Equal("not-admitted", Verdicts.Display(LegVerdict.NotAdmitted));
        Assert.Equal("poisoned", Verdicts.Display(LegVerdict.Poisoned));
        Assert.Equal("stopped", Verdicts.Display(LegVerdict.Stopped));
        Assert.Equal("violated", Verdicts.Display(LegVerdict.Violated));
        Assert.Equal("survived", Verdicts.Display(LegVerdict.Survived));
        Assert.Equal("unattributed", Verdicts.Display(LegVerdict.Unattributed));
    }

    [Fact]
    public void Precedence_IsTheOrderTheDocumentGives()
    {
        LegVerdict[] fundamentalFirst =
        [
            LegVerdict.Poisoned,
            LegVerdict.Unmeasured,
            LegVerdict.InputsMoved,
            LegVerdict.Contended,
            LegVerdict.LogHeld,
            LegVerdict.RefusedLocked,
            LegVerdict.NotAdmitted,
            LegVerdict.Failed,
            LegVerdict.Violated,
            LegVerdict.Survived,
            LegVerdict.Unattributed,
            LegVerdict.Unwitnessed,
        ];

        for (var index = 1; index < fundamentalFirst.Length; index++)
        {
            Assert.True(
                Verdicts.Rank(fundamentalFirst[index - 1]) < Verdicts.Rank(fundamentalFirst[index]),
                $"{fundamentalFirst[index - 1]} must outrank {fundamentalFirst[index]}");
        }

        // A stopped build follows every failure, and comes before the legs that did no work, whose work never began, and
        // those that passed, since it reached no verdict of its own.
        Assert.All(fundamentalFirst, verdict => Assert.True(Verdicts.Rank(verdict) < Verdicts.Rank(LegVerdict.Stopped), $"{verdict} must outrank stopped"));
        Assert.All(
            [LegVerdict.SkippedUnavailable, LegVerdict.SkippedToolMissing, LegVerdict.Passed, LegVerdict.SkippedNotSelected],
            verdict => Assert.True(Verdicts.Rank(LegVerdict.Stopped) < Verdicts.Rank(verdict), $"stopped must outrank {verdict}"));
    }

    [Fact]
    public void Worst_ReportsTheMoreFundamentalVerdict()
    {
        // A leg whose inputs moved is not reported as failed even if its tests failed, because what
        // failed was a tree that never existed.
        Assert.Equal(LegVerdict.InputsMoved, Verdicts.Worst([LegVerdict.Passed, LegVerdict.Failed, LegVerdict.InputsMoved]));
        Assert.Equal(LegVerdict.Poisoned, Verdicts.Worst([LegVerdict.Unmeasured, LegVerdict.Poisoned]));
        Assert.Equal(LegVerdict.Failed, Verdicts.Worst([LegVerdict.Unwitnessed, LegVerdict.Failed, LegVerdict.Passed]));
        Assert.Equal(LegVerdict.Passed, Verdicts.Worst([LegVerdict.Passed, LegVerdict.SkippedNotSelected]));
    }

    [Fact]
    public void ExitCodes_AreTheContractCommandsPromise()
    {
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(LegVerdict.Passed));
        Assert.Equal(HarnessExit.CommandFailed, Verdicts.ExitCodeFor(LegVerdict.Failed));
        Assert.Equal(HarnessExit.Refused, Verdicts.ExitCodeFor(LegVerdict.RefusedLocked));
        Assert.Equal(HarnessExit.InternalError, Verdicts.ExitCodeFor(LegVerdict.Poisoned));

        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.InputsMoved));
        Assert.Equal(LegExit.InputsMoved, Verdicts.ExitCodeFor(LegVerdict.Unmeasured));
        Assert.Equal(LegExit.Contended, Verdicts.ExitCodeFor(LegVerdict.Contended));
        Assert.Equal(LegExit.Unwitnessed, Verdicts.ExitCodeFor(LegVerdict.Unwitnessed));
        Assert.Equal(LegExit.LogHeld, Verdicts.ExitCodeFor(LegVerdict.LogHeld));
        Assert.Equal(LegExit.NotAdmitted, Verdicts.ExitCodeFor(LegVerdict.NotAdmitted));
        Assert.Equal(LegExit.Violated, Verdicts.ExitCodeFor(LegVerdict.Violated));
        Assert.Equal(LegExit.Survived, Verdicts.ExitCodeFor(LegVerdict.Survived));
        Assert.Equal(LegExit.Unattributed, Verdicts.ExitCodeFor(LegVerdict.Unattributed));

        // A warning is not a failure - a switched-off machine is normal - and nor is a stopped build,
        // but a run whose worst verdict is one of them is incomplete, never a pass. A leg nobody asked
        // for is an answer, not a gap.
        Assert.Equal(HarnessExit.Incomplete, Verdicts.ExitCodeFor(LegVerdict.SkippedUnavailable));
        Assert.Equal(HarnessExit.Incomplete, Verdicts.ExitCodeFor(LegVerdict.SkippedToolMissing));
        Assert.Equal(HarnessExit.Incomplete, Verdicts.ExitCodeFor(LegVerdict.Stopped));
        Assert.Equal(HarnessExit.Success, Verdicts.ExitCodeFor(LegVerdict.SkippedNotSelected));
    }

    /// <summary>
    /// The worst verdict decides a run's exit code where something failed, and names it in the run's closing line either
    /// way, so every failure outranks every verdict that is none, and every verdict a leg reaches without one of its own
    /// outranks every one it reaches that is neither: otherwise a run where something failed would exit as a pass, or
    /// close naming a pass when it is incomplete.
    /// </summary>
    [Fact]
    public void EveryFailure_OutranksTheRest_AndEveryLegWithoutAVerdict_OutranksAPass()
    {
        var verdicts = Enum.GetValues<LegVerdict>();
        var failures = verdicts.Where(Verdicts.IsFailure).ToList();
        var reachedNone = verdicts.Where(Verdicts.ReachedNone).ToList();
        var reached = verdicts.Where(verdict => !Verdicts.IsFailure(verdict) && !Verdicts.ReachedNone(verdict)).ToList();

        Assert.All(failures, failure => Assert.All(
            verdicts.Except(failures),
            other => Assert.True(Verdicts.Rank(failure) < Verdicts.Rank(other), $"{failure} must outrank {other}")));
        Assert.All(reachedNone, none => Assert.All(
            reached,
            other => Assert.True(Verdicts.Rank(none) < Verdicts.Rank(other), $"{none} must outrank {other}")));
    }

    /// <summary>
    /// The codes a mutation arm's verdicts exit with are the ones docs/architecture.md and help map a mutation
    /// harness's own exit codes onto, and a repository moving its sweep to check-mutations adapts its callers to them:
    /// renumbered here, every such caller would read another verdict than the one reported.
    /// </summary>
    [Fact]
    public void MutationCodes_AreTheOnesTheDocumentMapsOnto()
    {
        Assert.Equal(1, LegExit.Violated);
        Assert.Equal(2, LegExit.Survived);
        Assert.Equal(8, LegExit.Unattributed);
    }

    [Fact]
    public void LegCodes_StayInsideThePerCommandRange()
    {
        // 1-9 belongs to a command's own contract. A leg code outside it would collide with a
        // shared meaning, and a leg code shared with another command's would mean two things.
        Assert.InRange(LegExit.InputsMoved, 1, 9);
        Assert.InRange(LegExit.Contended, 1, 9);
        Assert.InRange(LegExit.Unwitnessed, 1, 9);
        Assert.InRange(LegExit.LogHeld, 1, 9);
        Assert.InRange(LegExit.NotAdmitted, 1, 9);
        Assert.InRange(LegExit.Violated, 1, 9);
        Assert.InRange(LegExit.Survived, 1, 9);
        Assert.InRange(LegExit.Unattributed, 1, 9);

        int[] codes =
        [
            LegExit.InputsMoved, LegExit.Contended, LegExit.Unwitnessed, LegExit.LogHeld, LegExit.NotAdmitted,
            LegExit.Violated, LegExit.Survived, LegExit.Unattributed,
        ];
        Assert.Equal(codes.Length, codes.Distinct().Count());

        foreach (var code in codes)
        {
            Assert.Null(HarnessExit.Describe(code));
        }
    }

    [Fact]
    public void FailureFlags_MatchTheDocument()
    {
        foreach (var verdict in new[]
                 {
                     LegVerdict.Failed, LegVerdict.Unwitnessed, LegVerdict.InputsMoved, LegVerdict.Unmeasured,
                     LegVerdict.Contended, LegVerdict.RefusedLocked, LegVerdict.NotAdmitted, LegVerdict.LogHeld, LegVerdict.Poisoned,
                     LegVerdict.Violated, LegVerdict.Survived, LegVerdict.Unattributed,
                 })
        {
            Assert.True(Verdicts.IsFailure(verdict), $"{verdict} counts as a failure");
        }

        foreach (var verdict in new[]
                 {
                     LegVerdict.Passed, LegVerdict.SkippedNotSelected, LegVerdict.SkippedUnavailable, LegVerdict.SkippedToolMissing,
                     LegVerdict.Stopped,
                 })
        {
            Assert.False(Verdicts.IsFailure(verdict), $"{verdict} does not count as a failure");
        }
    }

    /// <summary>
    /// A leg reached no verdict of its own where it did no work or its build was stopped from outside; every other
    /// verdict, a failure or a pass or a leg nobody asked for, is one.
    /// </summary>
    [Fact]
    public void ReachedNone_IsTheSkipsAndAStoppedBuild()
    {
        LegVerdict[] none = [LegVerdict.SkippedUnavailable, LegVerdict.SkippedToolMissing, LegVerdict.Stopped];

        Assert.All(Enum.GetValues<LegVerdict>(), verdict => Assert.Equal(none.Contains(verdict), Verdicts.ReachedNone(verdict)));
    }

    [Fact]
    public void ALegWithNoVerdict_IsPoisonedAndSaysWhich()
    {
        var reached = ReachedVerdict.OrPoisoned(null, "win-msvc-release");

        Assert.Equal(LegVerdict.Poisoned, reached.Verdict);
        Assert.Contains("win-msvc-release", reached.Detail, StringComparison.Ordinal);
        Assert.True(Verdicts.IsFailure(reached.Verdict), "a leg that vanishes must fail the run");
    }

    /// <summary>
    /// The verdict work that ended in a failure comes to is read in one place, for a leg and for every unit of a sweep:
    /// the verdict a refusal names, unmeasured where a phase's output could not be read back from its log, failed where
    /// the cause is one this build can name - and none where nobody named it, which is the caller's to say as poisoned.
    /// </summary>
    [Fact]
    public void AFailure_ComesToTheVerdictItsCauseNames_AndToNoneWhereNobodyNamedIt()
    {
        Assert.Equal(
            ReachedVerdict.Of(LegVerdict.SkippedToolMissing, "ninja was not found"),
            Verdicts.ForFailure(new HarnessException(HarnessExit.ToolMissing, "ninja was not found")));
        Assert.Equal(
            ReachedVerdict.Of(LegVerdict.Unmeasured, PhaseOutputTests.Unread.Said),
            Verdicts.ForFailure(new PhaseOutputUnreadException("build.log", "it was written again since")));
        Assert.Equal(
            ReachedVerdict.Of(LegVerdict.Failed, "'bench' could not be started: Exec format error"),
            Verdicts.ForFailure(new RepoHarness.Core.Processes.ProgramStartException("bench", "'bench' could not be started: Exec format error")));
        Assert.Null(Verdicts.ForFailure(new InvalidOperationException("the build directory vanished")));
        Assert.Null(Verdicts.ForFailure(new IOException("the disk went away")));
    }

    [Fact]
    public void ARefusal_KeepsTheMeaningItWasRaisedWith()
    {
        // A host that is switched off is not a defect in the harness, and reporting it as poisoned
        // would send the reader looking for one.
        Assert.Equal(LegVerdict.SkippedUnavailable, Verdicts.ForRefusal(HarnessExit.HostUnavailable));
        Assert.Equal(LegVerdict.SkippedToolMissing, Verdicts.ForRefusal(HarnessExit.ToolMissing));
        Assert.Equal(LegVerdict.LogHeld, Verdicts.ForRefusal(LegExit.LogHeld));
        Assert.Equal(LegVerdict.NotAdmitted, Verdicts.ForRefusal(LegExit.NotAdmitted));
        Assert.Equal(LegVerdict.Violated, Verdicts.ForRefusal(LegExit.Violated));
        Assert.Equal(LegVerdict.Survived, Verdicts.ForRefusal(LegExit.Survived));
        Assert.Equal(LegVerdict.Unattributed, Verdicts.ForRefusal(LegExit.Unattributed));
        Assert.Equal(LegVerdict.Poisoned, Verdicts.ForRefusal(HarnessExit.InternalError));
    }
}
