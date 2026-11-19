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
    private static string RenderSpace()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Disk space");
        builder.AppendLine();
        builder.AppendLine("clean removes each selected leg's build directory where the leg runs: in this");
        builder.AppendLine("machine's tree, or in a WSL distribution's or an ssh host's copy of the tree it is");
        builder.AppendLine("typed in. The mutation workers a sweep of the leg keeps in that tree go with");
        builder.AppendLine("it - its self-test's too, and those of a variant no leg there builds any more -");
        builder.AppendLine("under the lock a sweep takes: one a live sweep claims is kept, one that cannot be");
        builder.AppendLine("removed fails the leg, which still says what went, and a directory under a worker's");
        builder.AppendLine("name that no sync made is said and left ('help mutations'). The copies of the tree");
        builder.AppendLine($"its legs work in, in the tree's {HarnessLayout.LegsDirectoryName} slot, go the same way - the leg's own variant's and");
        builder.AppendLine("those of a variant no leg builds any more, whatever the configuration says now -");
        builder.AppendLine("each under the lock a run takes on it: one a run is building in, or replacing, is");
        builder.AppendLine("kept and said - on a host, the copy a sweep works in there among them, with its");
        builder.AppendLine("workers. A host holding no copy of the tree holds nothing of it to remove.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"clean {CleanService.FormerWorkersOption} removes only the workers an earlier version of this tool kept beside the "
            + "repository's trees, as <tree>.mutation-<key><mark>-<n>, or in them, as "
            + $"<tree>/{HarnessLayout.FormerMutationsDirectoryName}/<key><mark>-<n>, once a machine: here beside and in the main "
            + "checkout and every worktree, and beside each worktree deleted since in the worktrees root; on each host a "
            + "selected leg runs on, asked of it from its home, beside and in its copy of the main checkout and each "
            + "worktree's copy beside it, and beside each removed since, whatever copy the host holds - and in the copies "
            + $"each of those trees keeps in its {HarnessLayout.LegsDirectoryName}. With --dry-run it lists each with its size. "
            + "One a sweep of that version still claims, by the claim file beside it, is kept, refused-locked; one no sync "
            + "made is said and left. Where the worktrees, or those deleted since, cannot be listed, the leg fails, saying "
            + "so, once the workers of the trees it knows are dealt with.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"clean {CleanService.GoneCopiesOption} removes only the copies hosts keep of worktrees gone from this machine, "
            + "told by the tree this machine's record of host copies names - never by the name a copy is kept under, so a "
            + "worktree here that bears a gone one's name, a live agent's among them, keeps its own copies. It is driven by "
            + "that record, not by the legs: every host the record names such a copy on is asked, one no declared leg runs "
            + "on any more among them, each on a line of its own named as the record spells the host, in place of the legs' "
            + "lines; --legs narrows it to the hosts the selected legs can run on. A tree is gone only where that is seen: "
            + "its path is whole on this machine, nothing is at it, and the directory it was in - for an orchestrator's "
            + "agent, the one its orchestrator's was in, once that is gone too - is there to look in. One that cannot be "
            + "looked at - a path spelt for another system, a drive or a share that is not mounted, a directory this user "
            + "may not look in - is never taken for gone: its copies stay recorded, each kept, refused-locked "
            + $"({HarnessExit.Refused}), its line saying why. Each copy goes as deleting its worktree would remove it, at the "
            + "path the record names, under the same lock: a copy the harness took over, or one nothing marks as its own, is "
            + "forgotten and left where it is, one the host no longer keeps is forgotten, and one a run or a sweep holds is "
            + $"kept, refused-locked ({HarnessExit.Refused}), as is one whose marker cannot be read. A host that cannot be "
            + "reached is said on its line, unavailable, its copies kept recorded for a later clean, and the next host is "
            + $"asked all the same ({HarnessExit.Incomplete}); one no configuration here declares any more can never be "
            + "asked, so its copies are forgotten and left on it for you to remove, as deleting a worktree forgets them. "
            + "With --dry-run it says what each holds and what removing it would do, measured at that same path - beside "
            + "the repositoryPath it was kept beside, whatever the host's item or config.json name now - and removes and "
            + "forgets nothing. Every "
            + "line names the record, and so does the line the clean ends on - one saying it records none where it does. "
            + $"It cannot be given with {CleanService.FormerWorkersOption} ({HarnessExit.UsageError}).");
        builder.AppendLine();
        builder.AppendLine($"  {ToolPackage.Command} clean --legs linux-arm64-debug,linux-arm64-release");
        builder.AppendLine($"  {ToolPackage.Command} clean --legs linux-arm64-debug --dry-run");
        builder.AppendLine();
        builder.AppendLine("It writes nothing where it removes before it has removed - no sync, no lock entry,");
        builder.AppendLine("no run records - so it frees a disk a build filled. A leg a run is building is");
        builder.AppendLine("refused-locked, naming the run: the lock file is read, never written. The directory");
        builder.AppendLine("is renamed aside, then removed, so a build started meanwhile starts in a new one; what");
        builder.AppendLine("an interrupted removal left aside, the next clean of that leg removes. A build");
        builder.AppendLine("directory that is a link is left alone: what it holds is wherever it points. Each");
        builder.AppendLine("leg's line says what was removed and the room left on its filesystem; --dry-run says");
        builder.AppendLine("what each holds and removes nothing, and ends as the clean would: a leg a run is");
        builder.AppendLine("building, or whose workers a sweep holds, or whose copy a run holds, is");
        builder.AppendLine($"refused-locked in the dry run too ({HarnessExit.Refused}), with what each holds still said. --json carries");
        builder.AppendLine("all of it as each leg's 'space', with its mutation workers' as 'workerBytes' and its");
        builder.AppendLine("copies' as 'copyBytes': what was removed of them - 0 where every one there was kept -");
        builder.AppendLine("or, in a dry run, what is there. A leg whose build directory was not measured - a");
        builder.AppendLine("link, locked or not removable - carries a space saying its workers and copies alone,");
        builder.AppendLine("where any is there.");
        builder.AppendLine();
        builder.AppendLine("A host whose DssHarness is older than this machine's is updated first, as for any");
        builder.AppendLine("command, and the update needs room: a host that is both full and behind has to be");
        builder.AppendLine("freed by hand once.");
        builder.AppendLine();
        builder.AppendLine("A leg is placed only where its host has the room its build still needs, as it is");
        builder.AppendLine("only where the programs it starts are: a build that fills a disk dies half way, and");
        builder.AppendLine("takes any other leg building there with it. What a build needs is what its");
        builder.AppendLine("directory comes to once built - the leg's buildSpaceGiB, or, left out, what a build");
        builder.AppendLine("of its variant recorded as it finished: in this tree's copy, or else the most any");
        builder.AppendLine("other copy of the repository on that host recorded - the main checkout's, a");
        builder.AppendLine("worktree's, an agent's or a plain one, or a leg copy of any of them, named in the");
        builder.AppendLine("line - less what the directory already holds. Legs building on one filesystem of a");
        builder.AppendLine("host are counted together, in the order they were selected, since every build");
        builder.AppendLine("directory stays once built; one that does not fit beside those before it is");
        builder.AppendLine("skipped-unavailable:");
        builder.AppendLine();
        builder.AppendLine("  ssh vps: this leg needs ~11.4 GiB, what worktree o1/xa's copy of the same");
        builder.AppendLine("  variant came to there; 3.2 GiB free on '/'");
        builder.AppendLine();
        builder.AppendLine("A leg nothing has measured that declares no buildSpaceGiB is placed as it always");
        builder.AppendLine("was, and so is one whose directory no build of this version recorded. Nothing is");
        builder.AppendLine("walked to decide: the room is the filesystem's own count, and what a directory holds");
        builder.AppendLine("is what its build recorded. Commands that build nothing - sync, clean - need no room.");
        builder.AppendLine("A sweep of a leg's mutation arms builds in its first worker, never in the leg's own");
        builder.AppendLine("directory, and is placed by the room that worker's build still needs; a self-test");
        builder.AppendLine("builds no tree of the leg's, and is placed by none.");
        builder.AppendLine();
        builder.AppendLine("One command counts only its own legs. Where a machine declares admission ('help");
        builder.AppendLine("admission'), each heavy leg whose need is known also claims the room its build");
        builder.AppendLine("needs as it is let start, and any later heavy leg - of this command or another -");
        builder.AppendLine("waits until its own need fits beside every claim on that filesystem.");
        builder.AppendLine();
        builder.AppendLine("A WSL distribution's disk is a file that grows on a drive of this machine, whatever");
        builder.AppendLine("room the distribution measures for itself - a terabyte, by default. A WSL leg needs");
        builder.AppendLine("its room on that drive too, which this machine's own legs on the drive fill as well.");
        builder.AppendLine();
        builder.AppendLine($"'{ToolPackage.Command} legs -v' says the room on each host it measured - where its copies are");
        builder.AppendLine("kept, and the main checkout for this machine, with the drive a WSL distribution's");
        builder.AppendLine("disk grows on - so a host that is nearly full shows before a run fills it; --json");
        builder.AppendLine("always carries it, as each host's 'space' and a WSL distribution's 'diskImageSpace'.");

        return builder.ToString();
    }
}
