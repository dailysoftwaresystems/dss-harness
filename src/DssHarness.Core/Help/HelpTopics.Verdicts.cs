using System.Globalization;
using System.Text;
using DssHarness.Core.Ci;
using DssHarness.Core.Anchors;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.Codebase;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
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
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Help;

public static partial class HelpTopics
{
    /// <summary>What to do about each verdict that has a remedy worth saying, in the order help lists them, one line apiece.</summary>
    private static readonly (LegVerdict[] Verdicts, string[] Remedy)[] Remedies =
    [
        ([LegVerdict.Violated], ["Fix the arm's declaration, or the code it guards"]),
        ([LegVerdict.Survived], ["Strengthen the test that should have failed"]),
        (
            [LegVerdict.InputsMoved, LegVerdict.Unmeasured],
            [
                "Let the tree settle, then run again; where its",
                "inputs could not be watched, free or raise what",
                "the machine's reason names, or restore the tree",
            ]),
        ([LegVerdict.Contended], ["Wait for the other run"]),
        ([LegVerdict.Unwitnessed], ["Find out what actually ran"]),
        ([LegVerdict.LogHeld], ["Find out which run still owns the logs"]),
        (
            [LegVerdict.NotAdmitted],
            [
                "Wait for the heavy legs it names, free memory",
                "or room on the filesystem it names, or raise the",
                "machine's limits ('help admission')",
            ]),
        (
            [LegVerdict.Unattributed],
            [
                "Contain the crash or hang in the case, or make",
                "the runner write its report",
            ]),
        ([LegVerdict.Stopped], ["Find out what stopped its work, then run again"]),
        ([LegVerdict.SkippedUnavailable], ["Make what its line names available, then run again"]),
        ([LegVerdict.SkippedToolMissing], ["Install or update the tool its line names,", "then run again"]),
    ];

    private static string RenderVerdicts()
    {
        var builder = new StringBuilder();

        builder.AppendLine("What each leg verdict means");
        builder.AppendLine();
        builder.AppendLine("Every declared leg reaches exactly one. A leg that reaches none is a defect in");
        builder.AppendLine("the harness and fails the run, rather than vanishing from the report.");
        builder.AppendLine();

        foreach (var info in Verdicts.All)
        {
            builder.AppendLine(
                $"  {info.Display,-22} {(info.IsFailure ? "counts as failure" : "not a failure"),-18} exit {info.ExitCode}");
        }

        builder.AppendLine();
        builder.AppendLine("A run where nothing failed but some leg reached no verdict, or left a set-up it brought up");
        builder.AppendLine($"running ('help setups'), exits {HarnessExit.Incomplete}, not 0, and names the legs that did not report. A leg that never ran proves nothing about the");
        builder.AppendLine("code, and nor does a build something stopped from outside before it finished, so");
        builder.AppendLine("counting either among the legs that passed reports evidence nobody gathered. A");
        builder.AppendLine("CMake build under ninja that fails with ninja saying nothing of why - which it says");
        builder.AppendLine("whenever it ends a build itself - or saying it was interrupted is stopped, not");
        builder.AppendLine("failed: run again, it finishes, where a failed one fails again. Read under the name");
        builder.AppendLine("it says it under, which for samurai, run by CMake for ninja where it is installed,");
        builder.AppendLine("is its file's. A build the harness stopped for hanging stays failed, saying it hung,");
        builder.AppendLine("and so does one under any other build tool, since only ninja's lines are read. A");
        builder.AppendLine("heavy leg's build stopped for leaving less room than its machine's minFreeGiB is");
        builder.AppendLine("stopped too ('help admission').");
        builder.AppendLine("A run killed, or stopped with its machine, before it finished says nothing more:");
        builder.AppendLine("the next build, test, run or check-mutations in its tree on that machine to own its");
        builder.AppendLine("run directory says it was abandoned - its run id, process and start, and where its");
        builder.AppendLine("records are - and releases its claim on them.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"A run is stopped with Ctrl+C where it was typed, and from another terminal with '{RunStopService.CommandName} <run id>' typed in "
            + $"the tree that ran it: its dispatcher looks for the stop every {StopCadence.PollEvery.TotalSeconds.ToString(CultureInfo.InvariantCulture)} s "
            + $"and stops as an interrupt stops it - each leg stopped where it stands, its ledger written with what was reached, exit {HarnessExit.Cancelled} "
            + $"- and {RunStopService.CommandName} waits for it while it lives, said every five minutes, saying "
            + $"the run stopped only where its dispatcher took the stop: one that ended on its own first is nothing it stopped ({HarnessExit.Refused}). "
            + "--now ends the dispatcher's process where it stands, with all it started, and its ledger is never written. A second Ctrl+C is the "
            + "hard stop: the dispatcher ends where it stands, as a kill ends it, and its ledger is never written. On Windows every phase "
            + "a leg runs on its own machine, its keepAwake command, and the ssh or wsl.exe that carries a leg to a host, is bound to its "
            + "dispatcher - a set-up's up line apart - and ends with it "
            + $"however it ends, a kill among them. On Linux and macOS a kill of the dispatcher alone does not reach its legs, and "
            + $"{RunStopService.CommandName} --now does.");
        builder.AppendLine();
        builder.AppendLine("check-mutations judges each arm of a repository's mutation registry, and a leg's");
        builder.AppendLine("verdict there is the worst of its own and its arms': violated where an arm's");
        builder.AppendLine("declaration did not hold, survived where its mutation built and ran and no case");
        builder.AppendLine("failed, and unattributed where its run failed and nothing ties that to a case. An");
        builder.AppendLine("arm not driven to a verdict - its sweep stopped, or ended by a refusal of the run,");
        builder.AppendLine("while it was driven or before; its own build, or its paired control's, stopped from");
        builder.AppendLine("outside; no worker left to run it; or the unmutated run of its test binary not");
        builder.AppendLine("passing - is stopped, saying why ('help mutations').");
        builder.AppendLine();
        builder.AppendLine("When several apply the more fundamental one is reported, in the order above.");
        builder.AppendLine("A leg whose inputs moved is not reported as failed even when its tests failed,");
        builder.AppendLine("because what failed was a tree that never existed.");
        builder.AppendLine();
        AppendWrapped(
            builder,
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
            + "here, or one typed in a copy a sync made, is named none: it works in a copy already, and never in one of its own.");
        builder.AppendLine();

        // The words such a leg's line opens with, read from where the line is made.
        AppendWrapped(
            builder,
            "A leg whose inputs cannot be watched - the machine refusing the watch as its work was to begin - is unmeasured "
            + $"with no phase of it run: '{InputsUnwatchedException.Opening.TrimEnd()}' and the machine's reason. Run first and "
            + "called unmeasured after, the whole of the work was spent to learn what was known before it began. A watch that "
            + "started and was lost while the work ran still leaves it unmeasured, as the work ends. On Linux the machine's "
            + "reason can be this user's inotify instances all taken (fs.inotify.max_user_instances) - a process of this tool "
            + "holds one, however many legs it runs - or its inotify watches run out (fs.inotify.max_user_watches), one for each "
            + "directory the work watches - those its inputs lie in and those above them; each is said with the limit and the "
            + "processes /proc shows holding it, the most "
            + "first. The leg does not wait for either: what holds them is another program, whose end nothing here can read. "
            + "Its remedy is the machine's, not the tree's: free or raise what the reason names - end a program holding this "
            + "user's inotify instances or watches, or raise the limit - or put back the tree the watch was to be put on, then "
            + "run again.");
        builder.AppendLine();
        builder.AppendLine("A run's closing line names its verdict with the count of legs that reached it, and");
        builder.AppendLine("then the rest, worst first - 'poisoned: 2 of 8 leg(s); 3 failed, 3 passed' - never");
        builder.AppendLine("the verdict beside the total, which reads as every leg having it.");
        builder.AppendLine();
        builder.AppendLine("A tool contention.sharedResourceTools lists, found running beside a leg, is a");
        builder.AppendLine("warning: one line per tool and per whose it was, with a count and the range of");
        builder.AppendLine("process ids. A process working in another declared leg's build directory is named");
        builder.AppendLine("as that leg's - two legs on one host share its load, which is maxParallelLegs at");
        builder.AppendLine("work, and yours to decide about - and one working in a build directory of another");
        builder.AppendLine("tree of the repository on that machine, a worktree's, an agent's or the main");
        builder.AppendLine("checkout's, or a copy one of them keeps in its .legs, as that tree's leg's:");
        builder.AppendLine("'worktree o1/xa's leg 'linux-debug'', or 'worktree o1/xa's leg copy's leg");
        builder.AppendLine("'linux-debug''. Only what no tree there accounts for is nobody's known, and where the");
        builder.AppendLine("other trees could not be listed the line says so. contention.sharedState says what");
        builder.AppendLine("each tool shares, in the words the warning uses:");
        builder.AppendLine();
        builder.AppendLine("  \"sharedState\": { \"ccache\": \"the per-user compiler cache\" }");
        builder.AppendLine();
        builder.AppendLine("Remedies");

        // Each verdict's name and exit code read from the verdict table, as the list above them is, so neither can
        // drift from what a run reports.
        foreach (var (verdicts, remedy) in Remedies)
        {
            var code = verdicts.Select(Verdicts.ExitCodeFor).Distinct().Single();
            var names = string.Join(", ", verdicts.Select(Verdicts.Display));

            builder.AppendLine(string.Create(CultureInfo.InvariantCulture, $"{code,3}  {names,-27}{remedy[0]}"));

            foreach (var more in remedy.Skip(1))
            {
                builder.AppendLine(new string(' ', 32) + more);
            }
        }

        return builder.ToString();
    }
}
