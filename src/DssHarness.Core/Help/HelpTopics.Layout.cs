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
    /// <summary>The column the layout topic writes what each place holds at.</summary>
    private const int LayoutColumn = 37;

    /// <summary>
    /// Every place init creates or the tool keeps in a tree, each with what it holds, in the order the layout topic lists
    /// them: the one list of them, which the README's Layout block is held to word for word.
    /// </summary>
    internal static IReadOnlyList<(string Path, string Text)> LayoutEntries { get; } =
    [
        (HarnessLayout.ConfigFileRelative, "tracked; the whole contract"),
        ($"{HarnessLayout.WorkflowsDirectoryRelative}/<name>/<name>{HarnessLayout.WorkflowFileExtension}",
            "tracked; one directory per workflow, holding its definition and every file that definition names - its agents, "
            + $"prompts and schemas ('{ToolPackage.Command} help workflows'); each missing file written by init where "
            + "config.json declares the implementation one"),
        ($"{HarnessLayout.WorkflowsEnvSlotRelative}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; the agent clients' keys, NAME=value files, every "
            + $"file merged, no key read from anywhere else; no workflow may be named {HarnessLayout.WorkflowsEnvSlotName}"),
        ($"{AgentSkills.HarnessPlace}/<name>/",
            "tracked; the tool's own place a step's prompt names a skill from; init creates nothing there "
            + $"('{ToolPackage.Command} help skills')"),
        ($"{HarnessLayout.RunnerActionsDirectoryRelative}/<name>/<name>.yml",
            "tracked; one directory per action, holding its steps and whatever those steps run"),
        ($"{HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerEnvDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; values actions read"),
        ($"{HarnessLayout.RunnerDirectoryRelative}/{HarnessLayout.RunnerSecretsDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; secret values actions read"),
        ($"{HarnessLayout.DirectoryName}/{HarnessLayout.SshItemsDirectoryName}/<name>/",
            $"ignored, and written by you, not init: {HarnessLayout.ItemEnvFileName} (address, user, port, copies' path), "
            + $"{HarnessLayout.ItemKeyFileName}, {HarnessLayout.ItemKnownHostsFileName}"),
        ($"{HarnessLayout.DirectoryName}/{HarnessLayout.WslDistrosDirectoryName}/<name>/",
            $"ignored; {HarnessLayout.ItemEnvFileName} (distribution, credential, copies' path)"),
        ($"{WorktreeSettings.SeededRoot}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; the worktrees, at worktrees.root, which init writes as this"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; what each orchestrator and its agents keep, in the "
            + $"main checkout, and never sent to a host by sync ('{ToolPackage.Command} help orchestrators')"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.RecordFileName}", "ignored; the orchestrator's record"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.LogsDirectoryName}/<name>{OrchestratorLayout.LogExtension}",
            "ignored; a line for each run that reached it or one of its agents, refusals included; a dry run writes none"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.PlansDirectoryName}/<name>/",
            "ignored; its plans, and each agent's"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.WorkDirectoryName}/<agent>/",
            "ignored; an agent's scratch and task files"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>/",
            "ignored; an agent's record, seed, the bytes it shares, rows, the rows applied, kept evidence"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.StepsDirectoryName}/<instance>/",
            "ignored; one step the agent runs, or that runs in or for its worktree: everything of the step, its "
            + $"{OrchestratorLayout.HandoffFileName} where one stopped it, and for a reason step the {OrchestratorLayout.PolicyFileName} it started under - the agents' "
            + "configuration read when each step starts, as its prompts are, so an edit of config.json reaches the steps "
            + "that start after it and never one under way - and, where the step has a codebase graph, its graph.jsonl (a "
            + $"line for each launch's first message, each call of the graph tool, each escape of its narrowed reads), its {OrchestratorLayout.StepPagesDirectoryName}/ (the whole result of each ask of more than one page its graph tool answered, and where each page of a call's whole result read_result reads begins, one file an ask, each kept packed for the conversation's life and put away when it ends, so a later page, a resumed conversation and a step that goes on with its work read pages of that one result) and its working-set.json, the "
            + "seeds of the packs of the steps after it"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AgentsDirectoryName}/<agent>/{OrchestratorLayout.SessionsDirectoryName}/<id>/",
            "ignored; API clients only: one conversation with the provider, in the provider's own form"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.StepsDirectoryName}/<instance>/",
            "ignored; one step of a run that no agent owns, the create-agent that makes an agent included"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{HarnessLayout.OrchestratorTempDirectoryName}/<launch>/",
            "ignored; every temporary directory a run's launch and its children keep, one directory a launch (its "
            + "children's and Claude Code's own process's beside it), made with the launch, held for the run's life "
            + "and never in the user's own places; taken away whole with the run's directory"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{HarnessLayout.OrchestratorArtifactsDirectoryName}/",
            "ignored; the run's artifacts, made with the run and kept for its life: read and copied wherever they "
            + "are asked for while it runs, and taken away with the run's directory"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/.../<instance>/{OrchestratorLayout.ChildrenDirectoryName}/<session>{OrchestratorLayout.ChildExtension}",
            "ignored; in either kind of step, on Linux and macOS: a session a contained child of the step leads while it "
            + "runs, or the process group a phase of its legs - a set-up's up line apart - its keepAwake command or what "
            + "carries a leg to a host leads while anything is in it, ended by the next command over the orchestrator - and "
            + "by stop-orchestrator --now - once its starter has gone"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.AsksDirectoryName}/",
            "ignored; one directory for each call of ask-orchestrator and ask-orchestrator-status: the facts it was given, "
            + $"the files of its reporter's attempt, the conversations its client stores ({OrchestratorLayout.SessionsDirectoryName}/) and ask.json; "
            + "the engine never reads it"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/<o>/{OrchestratorLayout.WebDirectoryName}/{OrchestratorLayout.WebSearchesFileName}",
            "ignored; a run's web search: what each source found once for the run, and each query it answered, which the "
            + "same query again is answered from; written by every client whose step searches"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/{OrchestratorLayout.WebIndexesDirectoryName}/<source>/",
            "ignored; the harness's own index of a sitemap search source's site: its state, its pages and the pages it "
            + "could not read, and its fill's record of each address it asked"),
        ($"{HarnessLayout.OrchestratorsDirectoryName}/{DssHarness.Core.Agents.AgentKeySpend.FileName}",
            "ignored; what each key spent, by its name and never its value: a line for each key and model of each entry of a step's order or of "
            + "a side-task's turn that ran, its lines older than seven days dropped when it is next written, read where "
            + $"balanceKeyUsage chooses a key ('{ToolPackage.Command} help agent-clients')"),
        ($"{HarnessLayout.SideTasksDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; the side-tasks' records, in the main checkout, "
            + $"and never sent to a host by sync ('{ToolPackage.Command} help side-tasks')"),
        ($"{HarnessLayout.SideTasksDirectoryName}/<task>/",
            "ignored; one side-task: its task.json, stored stream, transcript, denials and web, its conversation where "
            + "its client stores one, and its own codebase graph (graph/, its map and each call of the graph tool) where it "
            + $"was given one, and {OrchestratorLayout.StepPagesDirectoryName}/ (the whole result of each ask of more than one page its graph tool answered, and where each page of a call's whole result read_result reads begins, which their later pages read); <task>.owner.json beside it while a side-task holds it open"),
        ($"{HarnessLayout.MutationsDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; the tree's mutation workers, in every tree, never "
            + $"sent to a host by sync ('{ToolPackage.Command} help mutations')"),
        ($"{HarnessLayout.MutationsDirectoryName}/<key><mark>-<n>/",
            $"ignored; a worker, a repository of its own, building in {VariantKey.BuildRootName}/<key>, and its claim beside it"),
        ($"{HarnessLayout.FormerMutationsDirectoryName}/",
            "ignored whole, with no placeholder, an empty one left there removed by init; where an earlier version kept the "
            + "tree's mutation workers, never made again: "
            + "never sent to a host by sync nor deleted by one, and what is left there removed by clean --former-workers, or "
            + "with the tree"),
        ($"{HarnessLayout.LegsDirectoryName}/",
            $"contents ignored, {HarnessLayout.GitKeepFileName} tracked; the copies of the tree its legs work in, in every "
            + "tree, never sent to a host by sync"),
        ($"{HarnessLayout.RunsDirectoryRelative}/", "ignored; one directory of records per run, in the tree that ran it"),
        ($"{HarnessLayout.DirectoryName}/{HarnessLayout.LockFileName}", "ignored; records in-progress runs"),
        ($"{HarnessLayout.SyncPiecesDirectoryRelative}/",
            "ignored; the pieces of a file a sync is bringing in, kept until the whole of it has arrived and removed once it is "
            + "placed, never sent by a sync nor deleted by one"),
        ($"{HarnessLayout.DependencySourcesDirectoryRelative}/<name>/",
            "a mutation worker's alone, never sent by a sync nor deleted by one: its own copy of each dependency source its "
            + $"leg's build fetched ('{ToolPackage.Command} help mutations')"),
        ($"{HarnessLayout.DirectoryName}/{HarnessLayout.SyncedCopyMarkerName}",
            "a host's copy's alone: the mark the sync that made it leaves, which tells a synced copy from a tree of its own"),
        ($"{HarnessLayout.HostCopiesDirectoryRelative}/",
            "ignores itself; which hosts hold a copy of which worktree, in the main checkout"),
        ($"{HarnessLayout.MutationRecordDirectoryRelative}/",
            "ignores itself; each leg's verdicts of its mutation arms, with what each was judged on, in the main checkout "
            + "('help mutations')"),
        (AnchorSettings.DefaultPendingAnchorsPath, "tracked; live anchors (anchors.pendingAnchorsPath)"),
        (AnchorSettings.DefaultDoneAnchorsPath, "tracked; closed anchors (anchors.doneAnchorsPath)"),
    ];

    private static string RenderLayout()
    {
        var builder = new StringBuilder();

        builder.AppendLine("Layout created by init");
        builder.AppendLine();
        foreach (var (path, text) in LayoutEntries)
        {
            if (path.Length < LayoutColumn - 2)
            {
                AppendWrapped(builder, text, $"  {path}".PadRight(LayoutColumn));
            }
            else
            {
                builder.AppendLine($"  {path}");
                AppendWrapped(builder, text, new string(' ', LayoutColumn));
            }
        }
        builder.AppendLine();
        builder.AppendLine("init creates each anchor registry that is missing, from a skeleton holding an");
        builder.AppendLine("introduction and an empty table, and never touches one that exists. A registry");
        builder.AppendLine("git tracks belongs to the branch, so it is created in the tree init runs in.");
        builder.AppendLine();
        builder.AppendLine("init adds these rules to .gitignore inside a marked block, replacing that");
        builder.AppendLine("block on later runs and leaving every other rule untouched. It then asks git which");
        builder.AppendLine("rule decides each path the block rules on, and names in a note any rule that turns");
        builder.AppendLine("one the other way: one git follows undoes the block there, and one the block");
        builder.AppendLine("overrules does nothing there - named only where taking it out would take from git");
        builder.AppendLine("nothing the harness keeps: no path the block rules on, none of its own files - its");
        builder.AppendLine("configuration, a placeholder, an action's files, an anchor registry - and no");
        builder.AppendLine("directory those are in, as git reads every ignore file the tree has, its");
        builder.AppendLine("info/exclude and configured excludes file among them. An allowlist's re-include of");
        builder.AppendLine("the slot a placeholder is kept in is overruled for the slot's contents and needed");
        builder.AppendLine("for the placeholder, since git never looks inside an excluded directory, so it is");
        builder.AppendLine("not named. A rule agreeing with the block is not named, however it is spelled.");
        builder.AppendLine("'.env', which many repositories ignore, is named: it takes the whole runner/.env");
        builder.AppendLine("and workflows/.env directories, and git re-includes no placeholder from an");
        builder.AppendLine("excluded one. A rule re-including a directory the block ignores, such as each");
        builder.AppendLine("host's under sshItems, is named too, though git itself names none for the files");
        builder.AppendLine("below it. A path git will not answer about - one beyond a symbolic link - is named");
        builder.AppendLine("with git's reason, and the rest are still asked. runs is ignored by name, with no");
        builder.AppendLine("trailing slash, so one kept on another disk through a link is ignored as the link");
        builder.AppendLine($"it is. The worktrees root, the orchestrators directory, {HarnessLayout.MutationsDirectoryName}, {HarnessLayout.LegsDirectoryName}");
        builder.AppendLine($"and {HarnessLayout.SideTasksDirectoryName} are kept in git by their placeholders, so each reads as not ignored itself while");
        builder.AppendLine("everything made in it is, and none can be a link: init names one it finds, and");
        builder.AppendLine("writes nothing through it. Sync withholds each by name. Each copy in them is a");
        builder.AppendLine("whole copy of the tree, so a program of the repository's own that walks the tree");
        builder.AppendLine("from its root - a census, a guard over every file - must leave out what the block");
        builder.AppendLine("ignores, as git does, or it reads every file once more for each copy kept there.");
        AppendWrapped(
            builder,
            $"{HarnessLayout.FormerMutationsDirectoryName}, where an earlier version kept the tree's mutation workers before "
            + $"they moved to {HarnessLayout.MutationsDirectoryName}, keeps its rule in the block for ever - the directory ignored "
            + "whole, with no placeholder, since nothing is made there again - so what that version left there, each worker a "
            + "repository of its own, never reads as work to commit; sync withholds it by name too, and no agent writes "
            + "there. The placeholder that version's init wrote there, still empty, init removes, the directory with it where "
            + "nothing else is left, for the repository to commit its removal; one holding anything is left, and said. "
            + "clean --former-workers removes what is left there, as does whatever removes the tree.");
        builder.AppendLine();
        builder.AppendLine("A block an earlier build wrote under the markers of the tool's former name");
        builder.AppendLine($"('{GitIgnoreManager.RetiredBeginMarker}') is the managed block too: init");
        builder.AppendLine("rewrites it in place under the current markers and says so, and none of its lines");
        builder.AppendLine("is read as a rule of the repository's own.");
        builder.AppendLine();
        builder.AppendLine("init writes the tree it runs in, a worktree's own included: its configuration - a");
        builder.AppendLine("copy of the main checkout's, where the worktree was running on that one - its");
        builder.AppendLine(".gitignore and its placeholders. The main checkout is left as it is, but for an");
        builder.AppendLine("anchor registry git ignores, created there where it is missing.");
        builder.AppendLine();
        builder.AppendLine("Ignored state lives in the main checkout. A worktree receives the tracked part of");
        builder.AppendLine(".harness-config through git but never the ignored part, so connection data and the");
        builder.AppendLine("run lock resolve back to the originating checkout. A run's records are an");
        builder.AppendLine($"exception, and so are a tree's {HarnessLayout.MutationsDirectoryName} and {HarnessLayout.LegsDirectoryName}: each belongs to the tree that");
        builder.AppendLine("ran or made it, so a run started inside a worktree writes its records there, and");
        builder.AppendLine("build, test, run and check-mutations name the directory in");
        builder.AppendLine("their output and as runDirectory in --json. Each leg that kept records has a line of");
        builder.AppendLine("its own naming its directory and the logs in it - 'logs of <leg>[ on <host>]' - as its");
        builder.AppendLine("verdict is said and in the run's closing lines, and as records and the phase logs as");
        builder.AppendLine("logs in --json, a host's paths with its home written as ~ (see");
        builder.AppendLine($"'{ToolPackage.Command} help legs'). Each names its run");
        builder.AppendLine("too, in its first line, 'run <id>', and as runId in --json, however it ended: a run");
        builder.AppendLine("refused before it had a directory keeps no records, and is cited by that id alone.");
        builder.AppendLine("check-mutations --list begins no run, and names neither.");
        builder.AppendLine("A host runs a leg under a run of its own: the dispatcher names it once, '<leg>: run");
        builder.AppendLine("<id> on <host>', never relaying the host's own line, and the leg's --json holds it as");
        builder.AppendLine("hostRunId.");
        builder.AppendLine("Action files are tracked, so a worktree has its own and a runner acts on the tree it");
        builder.AppendLine("was asked about.");

        return builder.ToString();
    }
}
