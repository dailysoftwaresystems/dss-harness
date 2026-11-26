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
    private static string RenderWorktrees()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Worktrees");
        builder.AppendLine();
        builder.AppendLine("Names are lowercase letters and digits joined by single hyphens. Their length is");
        builder.AppendLine("the path budget's to decide, measured against the real path of each tree, unless");
        builder.AppendLine("worktrees.maxNameLength sets a longest name of the repository's own.");
        builder.AppendLine($"Use --random to have one of {WorktreeName.RandomLength} characters generated - fewer where");
        builder.AppendLine("worktrees.maxNameLength is shorter - drawn from outside the names worktrees and");
        builder.AppendLine("orchestrators already have, so it is never one of them.");
        builder.AppendLine();
        builder.AppendLine("The path budget is not cosmetic. A worktree's build tree sits below");
        builder.AppendLine("  <worktrees.root>/<name>");
        builder.AppendLine($"which is '{WorktreeSettings.SeededRoot}' in a configuration init writes, and");
        builder.AppendLine($"'{WorktreeSettings.DefaultRoot}' in one that names none. That root is configurable");
        builder.AppendLine("precisely because it is spent before a worktree's own name: a shorter one buys");
        builder.AppendLine("those characters back. Ask list-worktree where a worktree actually is rather than");
        builder.AppendLine("assuming either.");
        builder.AppendLine("On Windows that path, plus build/<variant> for the longest variant this machine");
        builder.AppendLine("builds, plus the longest path the build system generates below that, must stay");
        builder.AppendLine($"under {HostPlatform.WindowsMaxPath} characters - and so must that path plus the longest file the");
        builder.AppendLine("repository tracks, which is all that counts where this machine builds no leg.");
        builder.AppendLine("A build past the limit does not fail as a path error: it appears as compile");
        builder.AppendLine("errors in files the worktree never touched.");
        builder.AppendLine();
        builder.AppendLine("create-worktree refuses up front when the budget cannot be met, and says how");
        builder.AppendLine("long a name would still fit. Both sides of the budget are in config.json:");
        builder.AppendLine("  worktrees.pathBudgetReserve   the longest path your build generates below its");
        builder.AppendLine("                                own build directory, relative to it; the check adds");
        builder.AppendLine("                                build/<variant> and every separator itself, and");
        builder.AppendLine("                                every build warns when it went deeper");
        builder.AppendLine("  worktrees.pathLimit           replaces the platform limit; set it only when");
        builder.AppendLine("                                every tool in the build handles long paths");
        builder.AppendLine();
        builder.AppendLine("A Ninja build leaves out of that warning the outputs ninja says no target of it");
        builder.AppendLine("produces any more - those of a target renamed or removed - since a new worktree's");
        builder.AppendLine("build, starting from clean, never holds them: it notes them instead, where one is");
        builder.AppendLine("deeper than the reserve, naming 'ninja -t cleandead', which removes them. The");
        builder.AppendLine("harness removes nothing. Another generator, or a ninja before 1.10, is measured as");
        builder.AppendLine("before.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An orchestrator's agents' worktrees sit below the directory named for it, <worktrees.root>/<orchestrator>/<agent>, "
            + "made by create-agent with its records, never by create-worktree. list-worktree lists each as orchestrator/agent, "
            + "and delete-worktree takes that address; one a deletion takes away while it is listed - the engine deletes an agent "
            + "that only read once its work is over, beside the run's next steps - is not listed, never a failure of the listing. "
            + "An agent's worktree is a repository of its own, not one of git's linked "
            + "worktrees: it borrows this checkout's objects through its alternates and writes only its own, holds no ref but its "
            + "detached HEAD, and includes this checkout's configuration before its own keys, so git worktree list here does not "
            + "show it and nothing done in it reaches this checkout's refs or objects. Its own .git/config carries the mark naming "
            + "this main checkout and its address: a command run inside it finds this main checkout, one whose mark names a "
            + $"checkout that does not lend it its objects is refused ({HarnessExit.Refused}), and list-worktree --json says "
            + "\"kind\": \"repository\" of it and \"kind\": \"worktree\" of a linked one. The commit it stands on is kept alive here "
            + "under refs/harness/worktree-base/<address>, written before it is made, and the commit a move of its base goes to under "
            + "refs/harness/worktree-moving/<address> until the move ends; one removed by hand refuses every verb on the agent, "
            + "naming the git update-ref that writes it again. An agent an earlier version made keeps its linked worktree, its "
            + "record naming no tree, and is still read, folded, rebased and deleted as before, its isolated work writing its "
            + "own git directory alone - so git add and fetch fail inside it there, said at each launch over it - until "
            + "delete-agent and a new agent give it a repository of its own. The directory named for an orchestrator is never deleted as one while "
            + "worktrees are below it, or a directory holding a .git of its own that git records no worktree at - what a "
            + "removal left, a person's to look in and delete, or a worker an earlier version of this tool kept beside an "
            + $"agent's worktree, which clean --former-workers removes - --force or not ({HarnessExit.Refused}), nor any directory with no .git of its own while git "
            + $"cannot list its worktrees ({HarnessExit.CommandFailed}); a worktree's own submodules are never taken for worktrees. "
            + "A worktree git records at another place that holds none now - as after the checkout, or the worktrees in it, were "
            + "moved by hand, its .git file and git's record still naming where they were - is a worktree with its work in it, "
            + "never a directory to delete: list-worktree, delete-worktree, delete-orchestrator and every agent verb name "
            + "'git -C <main checkout> worktree repair <path>', which points the record and the .git file back at each other, and "
            + "list-orchestrator says of a live agent whose worktree git cannot open, or holds as another kind than its record "
            + "names, that it cannot be worked on and why, never that the agent stands live at it (\"worktreeProblem\" in --json). "
            + "A plain worktree cannot take an orchestrator's name. An agent's copies on hosts are kept under "
            + "orchestrator--agent, so two orchestrators' agents of one name keep theirs apart.");
        builder.AppendLine();
        builder.AppendLine("Worktrees always belong to the main checkout, so running create-worktree from");
        builder.AppendLine("inside a worktree adds a sibling rather than nesting one.");
        builder.AppendLine();
        builder.AppendLine("delete-worktree removes a worktree, everything under it, and git's record of it.");
        builder.AppendLine($"Without --force it checks first, and deletes nothing and exits {HarnessExit.Refused} when the");
        builder.AppendLine("worktree has uncommitted changes: a modified, staged or untracked file git does");
        builder.AppendLine("not ignore, a changed submodule, or an edit hidden by assume-unchanged or");
        builder.AppendLine("skip-worktree. It refuses too when commits on its HEAD are on no branch, tag,");
        builder.AppendLine("remote-tracking ref, newest stash or other worktree's HEAD; when a submodule");
        builder.AppendLine("repository deleted with it, checked out or not, holds a commit no");
        builder.AppendLine("remote-tracking ref or tag contains, or a stash; when it is locked; when git's");
        builder.AppendLine("record of it names another directory or none, as after moving it by hand; and");
        builder.AppendLine("when git does not see it as a worktree of this repository. The refusal names");
        builder.AppendLine("everything it found, on one line. A tag made inside a submodule counts as kept,");
        builder.AppendLine("and is lost with the submodule's repository. A checked-out submodule's own");
        builder.AppendLine("changes are asked of it only where its repository is among its superproject's");
        builder.AppendLine("modules: its own git runs its own configuration, which no other place is kept");
        builder.AppendLine("from an agent, so one whose repository is anywhere else - a .git of its own in");
        builder.AppendLine("the work tree - is named a change never looked into.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "An agent's repository of its own is weighed the same way, but for its commits: those made in it that no ref of "
            + "the main checkout's reaches refuse its deletion, since its own store, deleted with it, is the only place they "
            + "are, and the refusal names fold-agent for its changes and a fetch into the main checkout for its commits - a "
            + "branch or a stash made in it would go with it. It has no lock and no record of git's. Once its checks pass, git "
            + "is asked once more what changed in it and what was committed in it, and a file changed or added since, beyond "
            + $"the changes being discarded, or a commit made since, refuses it ({HarnessExit.Refused}) with nothing removed; "
            + "then everything in it but its own .git directory is deleted, and that last, its read-only object files with "
            + "it, so one stopped part way is still a repository holding every commit made in it; and then the records of "
            + "the commit it stood on, "
            + "refs/harness/worktree-base/<address> and refs/harness/worktree-moving/<address>, which are forgotten too "
            + "whenever its directory is already gone.");
        builder.AppendLine();
        builder.AppendLine("Without --force, when the check cannot be finished, because git cannot answer, a");
        builder.AppendLine("record cannot be read or its directory found, or, on Windows, a directory in it");
        builder.AppendLine($"cannot be looked through for what holds it, nothing is deleted and it exits {HarnessExit.CommandFailed}.");
        builder.AppendLine("Ignored files outside a declared evidenceRoots directory are deleted unchecked,");
        builder.AppendLine("even ones no build makes again, such as .env, and so are ignored directories");
        builder.AppendLine("with everything in them, the history of a repository nested inside one and the");
        builder.AppendLine("records of every run started in the worktree included.");
        builder.AppendLine("A worktree whose evidenceRoots directory holds anything is refused;");
        builder.AppendLine("--delete-evidence waives that one check and nothing else, and so does");
        builder.AppendLine("--discard-uncommitted for uncommitted changes, which it deletes with the");
        builder.AppendLine("worktree, saying how many and naming a few. --force skips every check and");
        builder.AppendLine("overrides a lock; whatever the worktree held is lost.");
        builder.AppendLine();
        builder.AppendLine($"On Windows, without --force, a worktree something holds part of is refused ({HarnessExit.Refused})");
        builder.AppendLine("whole once every check has passed, with nothing removed, naming what is held: a");
        builder.AppendLine("directory that is a process's current directory, this command's own among them,");
        builder.AppendLine("a file open without sharing its deletion, or one a program is running from. The");
        builder.AppendLine("removal would stop there part way, with some of the worktree's files already gone.");
        builder.AppendLine("Close what holds it and run delete-worktree again, from outside the worktree where");
        builder.AppendLine("it is this command's own. A watcher on a directory, as an editor keeps, holds");
        builder.AppendLine("nothing; --force looks for nothing, and goes as far as it can.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The copies kept in a worktree go before it, of its two families: the mutation workers its sweeps made "
            + $"('help mutations'), in {HarnessLayout.MutationsDirectoryName} - and in {HarnessLayout.FormerMutationsDirectoryName}, "
            + "where an earlier version kept them - and the copies of it its legs work in "
            + $"('help legs'), in {HarnessLayout.LegsDirectoryName} - never sent to any host, so only this machine holds "
            + "them. They are asked about before anything "
            + $"of it is removed or looked through: one a sweep still running holds - or a run holds a leg copy - keeps the "
            + $"worktree ({HarnessExit.Refused}), "
            + $"and one that cannot be removed, or told for the harness's, keeps it as a failure ({HarnessExit.CommandFailed}), as "
            + "workers that cannot be looked for do, with nothing removed. A run holds a leg copy however either spells it: "
            + "the run lock tells a tree on this machine by where it leads, every link followed, whichever way the run holding "
            + "it spelled it - a build before this one's included - so a run in a worktree kept on another disk through a "
            + $"linked worktrees root, or in a copy kept through a {HarnessLayout.LegsDirectoryName} that is a link, keeps it "
            + "from a deletion that reaches it the other way. A sweep "
            + "that takes one meanwhile keeps the worktree as "
            + "well, and so does one whose removal then fails, the others gone by then, which is said. An interruption as they go "
            + $"says which went, and that nothing else of the worktree was removed ({HarnessExit.Cancelled}). --force deletes the "
            + "worktree with every copy in it, one held or one that could not be removed among them, saying which went with "
            + "it. A worktree whose own removal then fails, or is stopped, says which copies went before it. In a host's copy "
            + "the workers are asked about first as well, and a copy kept once some had gone says which went.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "Once every check has passed, the removal of one of git's linked worktrees is the harness's own, every step of "
            + "it reversible until the worktree's files are gone: git is asked once more what changed in it and whether it "
            + "is locked, and a file changed or added since the checks, beyond the changes being discarded - told by its "
            + "path, as git's status gives it - or a lock, refuses it "
            + $"({HarnessExit.Refused}) with nothing removed; then everything in it but its .git file is deleted, by a deletion "
            + "that reaches any path, however long, and only then is git asked to clear its record of it, the .git file "
            + "going with the directory. A removal that stops before git clears that record - a file another program "
            + $"holds ({HarnessExit.CommandFailed}), or an interruption ({HarnessExit.Cancelled}) - leaves a worktree git still "
            + "knows, its .git file and git's record of it there, and says so, naming 'git -C <path> checkout -- .', which "
            + "puts back the files its index holds, and, where it holds submodules, 'git -C <path> submodule update --init "
            + "--recursive', which puts back theirs, the directory junctions it had removed, which nothing puts back, and "
            + "the delete-worktree --force that finishes it. --force has git remove the worktree, and deletes whatever git left. "
            + "On Windows, each directory junction in the worktree is removed first, as a link, never what it leads to: "
            + "git leaves every junction, and the directories above it, while reporting the worktree removed. One that "
            + $"cannot be removed stops the deletion before any file of it goes ({HarnessExit.CommandFailed}), and a volume mounted "
            + "on a directory is never unmounted. On Linux and macOS a volume mounted on a directory in it - on Linux a bind "
            + "mount of a directory among them - is never gone into: the deletion stops there "
            + $"({HarnessExit.CommandFailed}), what went before it gone, still a worktree. Every git command the harness runs on "
            + "Windows reaches a path past 260 characters as any other (core.longpaths). A directory under the root with no "
            + ".git and no record of git's - "
            + $"what a removal that stopped part way leaves - is refused ({HarnessExit.Refused}), naming --force; git "
            + "worktree repair is named only while git still records a worktree there - at that path, or, after the checkout was "
            + "moved by hand, at another place that holds none now.");
        builder.AppendLine();
        builder.AppendLine("Once every check has passed, the deletion is past its point of no return: an");
        builder.AppendLine("interruption from then on is said, and the deletion goes on to its end, told every");
        builder.AppendLine($"{ReportText.Limit(Waiting.Waits.SaidEvery)} while it lasts, git never stopped part way. A second interruption, the hard");
        builder.AppendLine("stop, ends it at once and can leave it partly done, on any platform; running");
        builder.AppendLine("delete-worktree again with --force finishes it.");
        builder.AppendLine();
        AppendWrapped(
            builder,
            "The worktree of an orchestrator's live agent - one whose record says it is live and names that worktree - is "
            + $"refused ({HarnessExit.Refused}), naming the delete-agent that deletes it with the agent, its evidence and "
            + "transcripts kept; one whose record cannot be read is refused too, since whether the agent is live cannot be "
            + "told. --force deletes the worktree anyway, leaving the agent's record naming a worktree that is gone. A closed "
            + "or deleted agent's worktree is deleted as any other.");
        builder.AppendLine();
        builder.AppendLine("A worktree synced to a host has a copy there of its own, beside the main checkout's,");
        builder.AppendLine("and deleting the worktree asks each host that holds one to remove it, where the");
        builder.AppendLine("harness made it: a copy it took over with --adopt, or a directory with no mark of");
        builder.AppendLine("the harness's, is left where it is, and said to be. A copy is removed under the lock");
        builder.AppendLine("a leg this machine runs there takes, taken with everything kept inside the copy, so");
        builder.AppendLine("a sweep this machine runs there keeps it as a run does, none starts there while it");
        builder.AppendLine("goes, and its marker goes last, so a removal that stops part way leaves the rest");
        builder.AppendLine("still the harness's to remove. A host is reached through the worktree's own");
        builder.AppendLine("configuration, read before it goes, or else through the one the command runs in; one");
        builder.AppendLine("neither declares is not asked, and its copy is forgotten, and named.");
        builder.AppendLine("A copy that cannot be removed now stays recorded, and the worktree is deleted all the");
        builder.AppendLine("same, but the command fails, naming each, with the highest code among them:");
        builder.AppendLine($"{HarnessExit.Refused} where a run or a sweep holds one or it was refused, {HarnessExit.HostUnavailable} where its host cannot");
        builder.AppendLine($"be reached, {HarnessExit.CommandFailed} where removing it failed there; or {HarnessExit.Cancelled} when interrupted. Running");
        builder.AppendLine("delete-worktree again removes what it left. For a name whose worktree is gone, it");
        builder.AppendLine("removes what any worktree of that name left, and never the copies of one that still");
        builder.AppendLine("exists, or of one that cannot be looked at here, which stay recorded and are said,");
        builder.AppendLine($"refused ({HarnessExit.Refused}). The copies a gone worktree left are removed alone, told by their");
        builder.AppendLine($"tree, never by the name, by clean {CleanService.GoneCopiesOption} ('help space').");
        builder.AppendLine();
        AppendWrapped(
            builder,
            $"{WorktreeService.ListCommand} lists each worktree with the copies this machine records hosts keeping of it, and the "
            + "copies left by worktrees that are gone - removed by hand, or by another tool - with the "
            + $"clean {CleanService.GoneCopiesOption} that removes them by their tree, never a {WorktreeService.DeleteCommand} of "
            + "their name, which a worktree here may bear now: where one does, a live agent's among them, the line says so, "
            + "and that deleting it would take them too. With --hosts it also asks each declared host which worktree "
            + "copies it keeps beside its repositoryPath, and how large each is, and sets them against that record: "
            + "a copy of a worktree here, one left by a worktree that is gone, one the record does not hold - made "
            + "from another checkout or machine, or forgotten here, which nothing here reaches, said with the tree its "
            + "marker says it was made from and whether that tree is on this machine - and one recorded that is not "
            + "there: a gone tree's, which the clean forgets, or a tree's here, whose next sync makes it again. Each "
            + "recorded copy is looked for where it was kept, as its removal acts on it: one kept beside an earlier "
            + "repositoryPath, before the host's item or config.json named another, is asked about there, and listed "
            + "beside it. A tree that cannot be looked at here - a path spelt for another system, a drive or a share that "
            + "is not mounted, a directory this user may not look in - is never listed as gone: it is said with why, and "
            + "nothing here removes or forgets its copies. A host that cannot be asked is named, and the command fails with the "
            + $"highest code among them, {HarnessExit.HostUnavailable} where a host cannot be reached. --json prints the "
            + $"listing as one JSON document. {StoppedDocument}");

        return builder.ToString();
    }
}
