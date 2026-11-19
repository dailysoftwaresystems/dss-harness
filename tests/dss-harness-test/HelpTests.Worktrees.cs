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
    [Fact]
    public async Task WorktreesTopic_QuotesTheLimitsFromTheCode()
    {
        var result = HelpText.Of("worktrees");

        Assert.Contains(
            WorktreeSettings.DefaultMaxNameLength.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);
        Assert.Contains(
            HostPlatform.WindowsMaxPath.ToString(CultureInfo.InvariantCulture),
            result.StandardOutput,
            StringComparison.Ordinal);

        foreach (var setting in new[] { "worktrees.maxNameLength", "worktrees.pathBudgetReserve", "worktrees.pathLimit" })
        {
            Assert.Contains(setting, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task WorktreesTopic_SaysWhenDeleteWorktreeRefuses_AndHowToForceIt()
    {
        var result = HelpText.Of("worktrees");

        foreach (var text in new[] { "delete-worktree", "uncommitted changes", "skip-worktree", "locked", "--force", "--delete-evidence", "--discard-uncommitted", $"exits {HarnessExit.Refused}", $"exits {HarnessExit.CommandFailed}" })
        {
            Assert.Contains(text, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says what becomes of the mutation workers kept in a worktree that is deleted: asked about
    /// before anything of it is removed, so a worktree kept for one removes nothing; which code each one kept leaves it
    /// with; what an interruption says; that --force deletes every copy with the worktree; and that a host's copy is
    /// treated as the worktree is - the leg copies of the worktree among the families that go with it, never sent to a
    /// host.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysWhatBecomesOfTheCopiesKeptInAWorktree()
    {
        var result = HelpText.Of("worktrees");

        Assert.Contains(
            "The copies kept in a worktree go before it, of its two families: the mutation workers its sweeps made "
            + "('help mutations'), in .m - and in .mutations, where an earlier version kept them - and the copies of it its legs "
            + "work in ('help legs'), in .legs - never "
            + "sent to any host, so only this machine holds them. They are asked about before anything of it is removed or "
            + $"looked through: one a sweep still running holds - or a run holds a leg copy - keeps the worktree ({HarnessExit.Refused}), and one that cannot "
            + $"be removed, or told for the harness's, keeps it as a failure ({HarnessExit.CommandFailed}), as workers that cannot be looked "
            + "for do, with nothing removed. A run holds a leg copy however either spells it: the run lock tells a tree on this "
            + "machine by where it leads, every link followed, whichever way the run holding it spelled it - a build before this one's "
            + "included - so a run in a worktree kept on another disk through a linked worktrees root, or in a copy kept through a "
            + ".legs that is a link, keeps it from a deletion that reaches it the other way. A sweep that takes one meanwhile keeps the worktree as "
            + "well, and so does one whose removal "
            + "then fails, the others gone by then, which is said. An interruption as they go says which went, and that nothing else of "
            + $"the worktree was removed ({HarnessExit.Cancelled}). --force deletes the worktree with every copy in it, one held or one "
            + "that could not be removed among them, saying which went with it. A worktree whose own removal then fails, or is stopped, "
            + "says which copies went before it. In a host's copy the workers are asked about first as well, and a copy kept once some had "
            + "gone says which went.",
            Prose.Words(result.StandardOutput),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says a Ninja build leaves its dead outputs out of the path budget's warning, and names
    /// the ninja command that removes them, removing nothing itself.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysDeadOutputsAreLeftOutOfTheWarning_AndWhatRemovesThem()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains("A Ninja build leaves out of that warning the outputs ninja says no target of it produces any more", text, StringComparison.Ordinal);
        Assert.Contains("naming 'ninja -t cleandead', which removes them. The harness removes nothing.", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says list-worktree shows the copies hosts keep and those a gone worktree left, what --hosts
    /// asks the hosts, and what a host that cannot be asked does to the command.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysListWorktreeFindsTheCopiesAGoneWorktreeLeft()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "list-worktree lists each worktree with the copies this machine records hosts keeping of it, and the copies "
                + "left by worktrees that are gone - removed by hand, or by another tool - with the clean --gone-copies that "
                + "removes them by their tree, never a delete-worktree of their name, which a worktree here may bear now: "
                + "where one does, a live agent's among them, the line says so, and that deleting it would take them too.",
            "one the record does not hold - made from another checkout or machine, or forgotten here, which nothing here "
                + "reaches, said with the tree its marker says it was made from and whether that tree is on this machine - "
                + "and one recorded that is not there: a gone tree's, which the clean forgets, or a tree's here, whose next "
                + "sync makes it again.",
            "The copies a gone worktree left are removed alone, told by their tree, never by the name, by clean "
                + "--gone-copies ('help space').",
            "For a name whose worktree is gone, it removes what any worktree of that name left, and never the copies of one "
                + "that still exists, or of one that cannot be looked at here, which stay recorded and are said, refused "
                + $"({HarnessExit.Refused}).",
            "Each recorded copy is looked for where it was kept, as its removal acts on it: one kept beside an earlier "
                + "repositoryPath, before the host's item or config.json named another, is asked about there, and listed beside "
                + "it. A tree that cannot be looked at here - a path spelt for another system, a drive or a share that is not "
                + "mounted, a directory this user may not look in - is never listed as gone: it is said with why, and nothing "
                + "here removes or forgets its copies.",
            "With --hosts it also asks each declared host which worktree copies it keeps beside its repositoryPath, and "
                + "how large each is, and sets them against that record",
            $"{HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the listing as one JSON document.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says where an orchestrator's agents' worktrees sit, that they are named by their address, that
    /// a directory holding worktrees is never deleted as one, and how their copies are named apart.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAgentsWorktreesAreNamedByTheirAddress()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        Assert.Contains(
            "An orchestrator's agents' worktrees sit below the directory named for it, <worktrees.root>/<orchestrator>/<agent>, made by "
            + "create-agent with its records, never by create-worktree. list-worktree lists each as orchestrator/agent, and delete-worktree "
            + "takes that address. An agent's worktree is a repository of its own, not one of git's linked worktrees: it borrows this "
            + "checkout's objects through its alternates and writes only its own, holds no ref but its detached HEAD, and includes this "
            + "checkout's configuration before its own keys, so git worktree list here does not show it and nothing done in it reaches "
            + "this checkout's refs or objects. Its own .git/config carries the mark naming this main checkout and its address: a command "
            + $"run inside it finds this main checkout, one whose mark names a checkout that does not lend it its objects is refused ({HarnessExit.Refused}), "
            + "and list-worktree --json says \"kind\": \"repository\" of it and \"kind\": \"worktree\" of a linked one. The commit it "
            + "stands on is kept alive here under refs/harness/worktree-base/<address>, written before it is made, and the commit a move of "
            + "its base goes to under refs/harness/worktree-moving/<address> until the move ends; one removed by hand refuses every verb on "
            + "the agent, naming the git update-ref that writes it again. An agent an earlier version made keeps its linked worktree, its "
            + "record naming no tree, and is still read, folded, rebased and deleted as before, its isolated work writing its "
            + "own git directory alone - so git add and fetch fail inside it there, said at each launch over it - until "
            + "delete-agent and a new agent give it a repository of its own. "
            + "The directory named for an orchestrator is never deleted as one while worktrees are below it, or a "
            + "directory holding a .git of its own that git records no worktree at - what a removal left, a person's to look in and "
            + "delete, or a worker an earlier version of this tool kept beside an agent's worktree, which clean --former-workers "
            + "removes - --force "
            + $"or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git cannot list its worktrees "
            + $"({HarnessExit.CommandFailed}); a worktree's own submodules are never taken for worktrees. A worktree git records at another "
            + "place that holds none now - as after the checkout, or the worktrees in it, were moved by hand, its .git file and git's record "
            + "still naming where they were - is a worktree with its work in it, never a directory to delete: list-worktree, delete-worktree, "
            + "delete-orchestrator and every agent verb name 'git -C <main checkout> worktree repair <path>', which points the record and the "
            + ".git file back at each other, and list-orchestrator says of a live agent whose worktree git cannot open, or holds as another "
            + "kind than its record names, that it cannot be worked on and why, never that the agent stands live at it (\"worktreeProblem\" in "
            + "--json). A plain worktree cannot take an "
            + "orchestrator's name. An agent's copies on hosts are kept under orchestrator--agent, so two orchestrators' agents of one name "
            + "keep theirs apart.",
            text,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The worktrees topic says that on Windows each junction is removed as a link before git runs, what stops that,
    /// and that git worktree repair is named only while git still records the worktree.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysJunctionsAreRemovedAsLinks_AndWhenRepairIsNamed()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "On Windows, each directory junction in the worktree is removed first, as a link, never what it leads to: git leaves "
                + "every junction, and the directories above it, while reporting the worktree removed.",
            $"One that cannot be removed stops the deletion before any file of it goes ({HarnessExit.CommandFailed}), and a volume "
                + "mounted on a directory is never unmounted. On Linux and macOS a volume mounted on a directory in it - on Linux "
                + "a bind mount of a directory among them - is never gone into: the deletion stops there "
                + $"({HarnessExit.CommandFailed}), what went before it gone, still a worktree. Every git command the harness runs "
                + "on Windows reaches a path past 260 characters as any other (core.longpaths).",
            "A directory under the root with no .git and no record of git's - what a removal that stopped part way leaves - is "
                + $"refused ({HarnessExit.Refused}), naming --force; git worktree repair is named only while git still records a worktree there - "
                + "at that path, or, after the checkout was moved by hand, at another place that holds none now.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says a checked removal is the harness's own and reversible until the worktree's files are
    /// gone, what a removal that stops part way leaves and names, and that a live agent's worktree is refused, naming
    /// delete-agent, unless --force.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysACheckedRemovalIsReversible_AndALiveAgentsWorktreeIsRefused()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            "Once every check has passed, the removal of one of git's linked worktrees is the harness's own, every step of it "
                + "reversible until the worktree's files are gone: git is asked once more what changed in it and whether it is "
                + "locked, and a file changed or added since the checks, beyond the changes being discarded - told by its path, "
                + $"as git's status gives it - or a lock, refuses it ({HarnessExit.Refused}) with "
                + "nothing removed; then everything in it but its .git file is deleted, by a deletion that reaches any path, "
                + "however long, and only then is git asked to clear its record of it, the .git file going with the directory.",
            $"A removal that stops before git clears that record - a file another program holds ({HarnessExit.CommandFailed}), "
                + $"or an interruption ({HarnessExit.Cancelled}) - leaves a worktree git still knows, its .git file and git's "
                + "record of it there, and says so, naming 'git -C <path> checkout -- .', which puts back the files its index "
                + "holds, and, where it holds submodules, 'git -C <path> submodule update --init --recursive', which puts back "
                + "theirs, the directory junctions it had removed, which nothing puts back, and the delete-worktree --force that "
                + "finishes it. --force has git remove the worktree, and deletes whatever git left.",
            "It has no lock and no record of git's. Once its checks pass, git is asked once more what changed in it and what "
                + "was committed in it, and a file changed or added since, beyond the changes being discarded, or a commit made "
                + $"since, refuses it ({HarnessExit.Refused}) with nothing removed; then everything in it but its own .git directory "
                + "is deleted, and that last, its read-only object files with it, so one stopped part way is still a repository "
                + "holding every commit made in it;",
            "The worktree of an orchestrator's live agent - one whose record says it is live and names that worktree - is "
                + $"refused ({HarnessExit.Refused}), naming the delete-agent that deletes it with the agent, its evidence and "
                + "transcripts kept; one whose record cannot be read is refused too, since whether the agent is live cannot be "
                + "told. --force deletes the worktree anyway, leaving the agent's record naming a worktree that is gone. A closed "
                + "or deleted agent's worktree is deleted as any other.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The worktrees topic says that on Windows a worktree something holds part of is refused whole, with nothing
    /// removed, what holds one and what does not, what to do where it is the command's own current directory, and
    /// that --force does not look.
    /// </summary>
    [Fact]
    public async Task WorktreesTopic_SaysAWorktreeSomethingHoldsIsRefusedWholeOnWindows()
    {
        var result = HelpText.Of("worktrees");
        var text = Prose.Words(result.StandardOutput);

        foreach (var said in new[]
        {
            $"On Windows, without --force, a worktree something holds part of is refused ({HarnessExit.Refused}) whole once "
                + "every check has passed, with nothing removed, naming what is held: a directory that is a process's current "
                + "directory, this command's own among them, a file open without sharing its deletion, or one a program is "
                + "running from.",
            "The removal would stop there part way, with some of the worktree's files already gone.",
            "Close what holds it and run delete-worktree again, from outside the worktree where it is this command's own.",
            "A watcher on a directory, as an editor keeps, holds nothing; --force looks for nothing, and goes as far as it can.",
            "or, on Windows, a directory in it cannot be looked through for what holds it, nothing is deleted and it exits "
                + $"{HarnessExit.CommandFailed}.",
        })
        {
            Assert.Contains(said, text, StringComparison.Ordinal);
        }
    }
}
