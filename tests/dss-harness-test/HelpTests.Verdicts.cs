using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DssHarness.Core.Anchors;
using DssHarness.Core.Ci;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Help;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

public sealed partial class HelpTests
{
    /// <summary>
    /// The verdicts topic says what becomes of a build stopped from outside, which is stopped rather than failed - but for
    /// one the harness stopped for hanging, or under another build tool - and of a run killed before it finished, which
    /// reaches no verdict and says nothing itself: the next run in its tree on that machine says it was abandoned, and
    /// releases it. Each verdict a leg reaches without one of its own shows the code its run exits
    /// with, and its remedy.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysAKilledRunIsReportedAbandoned_ByTheNextRun()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains(
            "A leg that never ran proves nothing about the code, and nor does a build something stopped from outside before it "
            + "finished, so counting either among the legs that passed reports evidence nobody gathered. A CMake build under "
            + "ninja that fails with ninja saying nothing of why - which it says whenever it ends a build itself - or saying it "
            + "was interrupted is stopped, not failed: run again, it finishes, where a failed one fails again. Read under the "
            + "name it says it under, which for samurai, run by CMake for ninja where it is installed, is its file's. A build "
            + "the harness stopped for hanging stays failed, saying it hung, and so does one under any other build tool, since "
            + "only ninja's lines are read.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  stopped                    Find out what stopped its work, then run again", result.StandardOutput, StringComparison.Ordinal);

        // Each verdict a leg reaches without one of its own shows the code its run exits with, and a remedy.
        Assert.All(
            ["stopped", "skipped-unavailable", "skipped-tool-missing"],
            verdict => Assert.Contains($"  {verdict,-22} not a failure      exit {HarnessExit.Incomplete}", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-unavailable        Make what its line names available, then run again", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($" {HarnessExit.Incomplete}  skipped-tool-missing       Install or update the tool its line names,", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(
            "A run killed, or stopped with its machine, before it finished says nothing more: the next build, test, run or "
            + "check-mutations in its tree on that machine to own its run directory says it was abandoned - its run id, process "
            + "and start, and where its records are - and releases its claim on them.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says how a run is stopped from another terminal from the code's own names and bounds: how often
    /// its dispatcher looks for the stop, how long stop-run waits, the codes it ends with, and what a run's legs are bound to
    /// on Windows.
    /// </summary>
    [Fact]
    public void VerdictsTopic_SaysHowStopRunStopsARun_FromItsOwnBounds()
    {
        var prose = Prose.Words(HelpText.Of("verdicts").StandardOutput);

        Assert.Contains(
            $"A run is stopped with Ctrl+C where it was typed, and from another terminal with '{RunStopService.CommandName} <run id>' typed in the "
                + $"tree that ran it: its dispatcher looks for the stop every {StopCadence.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s "
                + $"and stops as an interrupt stops it - each leg stopped where it stands, its ledger written with what was reached, exit {HarnessExit.Cancelled} "
                + $"- and {RunStopService.CommandName} waits {RunStopService.StopWait.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s for it, saying "
                + $"the run stopped only where its dispatcher took the stop: one that ended on its own first is nothing it stopped ({HarnessExit.Refused}). "
                + "--now ends the dispatcher's process where it stands, with all it started, and its ledger is never written. On Windows every phase a "
                + "leg runs on its own machine, its keepAwake command, and the ssh or wsl.exe that carries a leg to a host, is bound to its dispatcher - "
                + "a set-up's up line apart - and ends with it however "
                + $"it ends, a kill among them. On Linux and macOS a kill of the dispatcher alone does not reach its legs, and {RunStopService.CommandName} "
                + "--now does.",
            prose,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the three verdicts only a mutation arm reaches, each with the code its run exits with and
    /// its remedy, and says an arm that was due and never driven is stopped, saying why, rather than any of them.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesWhatAMutationArmReaches_WithItsCodeAndRemedy()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains($"{LegExit.Violated,3}  violated                   Fix the arm's declaration, or the code it guards", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Survived,3}  survived                   Strengthen the test that should have failed", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"{LegExit.Unattributed,3}  unattributed               Contain the crash or hang in the case, or make", result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(new string(' ', 32) + "the runner write its report", result.StandardOutput, StringComparison.Ordinal);
        Assert.All(
            ["violated", "survived", "unattributed"],
            verdict => Assert.Contains($"  {verdict,-22} counts as failure ", result.StandardOutput, StringComparison.Ordinal));
        Assert.Contains(
            "An arm not driven to a verdict - its sweep stopped, or ended by a refusal of the run, while it was driven or before; its "
            + "own build, or its paired control's, stopped from outside; no worker left to run it; or the unmutated run of its test "
            + "binary not passing - is stopped, saying why ('help mutations').",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says a heavy leg's build stopped for room is stopped, and that a shared tool working in a build
    /// directory of another tree of the repository is named as that tree's leg's, only what no tree accounts for being
    /// nobody's known.
    /// </summary>
    [Fact]
    public async Task TheVerdictsTopic_SaysABuildStoppedForRoomIsStopped_AndWhoseAToolInAnotherTreeIs()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "A heavy leg's build stopped for leaving less room than its machine's minFreeGiB is stopped too ('help admission').",
            said,
            StringComparison.Ordinal);
        Assert.Contains(
            "and one working in a build directory of another tree of the repository on that machine, a worktree's, an agent's or the main "
            + "checkout's, or a copy one of them keeps in its .legs, as that tree's leg's: 'worktree o1/xa's leg 'linux-debug'', or "
            + "'worktree o1/xa's leg copy's leg 'linux-debug''. Only what no tree there accounts for is nobody's known, and where the "
            + "other trees could not be listed the line says so.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says a leg whose inputs cannot be watched is unmeasured with none of its work run, in the words
    /// its line opens with, read from the code; and that a watch lost while the work ran ends it unmeasured as it ends.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysALegWhoseInputsCannotBeWatched_IsUnmeasuredWithNoneOfItsWorkRun()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "A leg whose inputs cannot be watched - the machine refusing the watch as its work was to begin - is unmeasured with no "
                + $"phase of it run: '{InputsUnwatchedException.Opening.TrimEnd()}' and the machine's reason. Run first and called unmeasured "
                + "after, the whole of the work was spent to learn what was known before it began. A watch that started and was lost "
                + "while the work ran still leaves it unmeasured, as the work ends.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The remedy for a leg whose inputs could not be watched is the machine's, never the tree's settling: the verdicts
    /// topic says so where it says why such a leg ran nothing, and again among the verdicts' remedies, and the exit codes
    /// topic beside the code it exits with.
    /// </summary>
    [Fact]
    public async Task TheRemedyForInputsThatCouldNotBeWatched_IsTheMachines_WhereverExitThreesRemedyIsSaid()
    {
        var verdicts = (HelpText.Of("verdicts")).StandardOutput;
        var codes = (HelpText.Of("exit-codes")).StandardOutput;

        Assert.Contains(
            "Its remedy is the machine's, not the tree's: free or raise what the reason names - end a program holding this user's "
                + "inotify instances or watches, or raise the limit - or put back the tree the watch was to be put on, then run again.",
            Prose.Words(verdicts),
            StringComparison.Ordinal);
        Assert.Contains(
            "  3  inputs-moved, unmeasured   Let the tree settle, then run again; where its" + Environment.NewLine
                + "                                inputs could not be watched, free or raise what" + Environment.NewLine
                + "                                the machine's reason names, or restore the tree" + Environment.NewLine,
            verdicts,
            StringComparison.Ordinal);
        Assert.Contains(
            $"    {LegExit.InputsMoved,3}  inputs-moved or unmeasured: let the tree settle, then run again; where the inputs" + Environment.NewLine
                + "         could not be watched, free or raise what the machine's reason names, or restore the tree" + Environment.NewLine,
            codes,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic names the two inotify limits a watch on Linux can meet, and says that the leg is said with the
    /// processes holding them and waits for neither.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_NamesTheTwoInotifyLimits_AndThatALegWaitsForNeither()
    {
        var said = Prose.Words((HelpText.Of("verdicts")).StandardOutput);

        Assert.Contains(
            "On Linux the machine's reason can be this user's inotify instances all taken (fs.inotify.max_user_instances) - "
                + "a process of this tool holds one, however many legs it runs - or its inotify watches run out "
                + "(fs.inotify.max_user_watches), one for each directory the work watches - those its inputs lie in and those "
                + "above them; each is said with the limit and the "
                + "processes /proc shows holding it, the most first. The leg does not wait for either: what holds them is "
                + "another program, whose end nothing here can read.",
            said,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The verdicts topic says when a leg on a copy is inputs-moved with nothing run - the tree moving while it was read,
    /// a file to carry changing after, another command's sync of the copy in between or the copy gone or part made by
    /// then, and under --use-staged a copy part made, absent or unmarked - what its line and its --json say of the tree it
    /// tested, and the remedy a leg that worked in its tree itself is named, and where it is not.
    /// </summary>
    [Fact]
    public async Task VerdictsTopic_SaysWhenALegOnACopyIsInputsMoved_AndWhichTreeItTested()
    {
        var result = HelpText.Of("verdicts");

        Assert.Contains(
            "A leg on a copy of its tree - a host's, or one of its own in the tree's .legs - is inputs-moved with nothing of it "
                + "run where the copy cannot be the tree the run read: a file written, appearing or going while the tree was read, "
                + "named; a file to carry changed after it was read; another command synced the copy again between this run's sync "
                + "and the leg taking it, naming that run, or by then the copy is not there, is part made, or holds no copy this "
                + "tool finished; or, under --use-staged, the copy is part made, is not there, or holds no copy this tool made. Each "
                + "leg on a copy says which tree it tested with its verdict - 'tree read at <commit>', with the uncommitted changes "
                + "the reading carried, or under --use-staged 'tree staged at <commit> by run <id>' as the copy records it - and "
                + "--json's 'tree' holds its path, the tree it is a copy of, and the commit, the count and the run. A leg that "
                + "worked in its tree itself and is inputs-moved names the remedy after its detail, where its command could have run "
                + "it on a copy: the leg's localCopy, defaults.localCopy, or --local-copy ('help legs'). A leg another machine sent "
                + "here, or one typed in a copy a sync made, is named none: it works in a copy already, and never in one of its own.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }
}
