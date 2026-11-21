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
    /// <summary>
    /// What a remote use is, how its ref is resolved and proved the repository's own, what refuses its directory, how the
    /// cache keeps it, and what workflowSources says of it - every bound and every variable named from the code that holds
    /// it, so the paragraph cannot promise what the fetch does not do.
    /// </summary>
    internal static string RemoteWorkflowsHelp
        => "A use may name a workflow beyond the repository: owner/repo/path/workflow.yml@ref, as GitHub Actions spells uses - a "
            + "repository's name may be dotted, as an organisation's .github is - or the same under a GitHub Enterprise host the "
            + "configuration names. It is fetched by git itself. The ref is held to git's own rules for a ref's name and resolved at the "
            + "run's start through the repository's own refs, every one of them listed in one call (git ls-remote): its tag, an annotated "
            + "one peeled through each tag object it names, however many, to the commit, or its branch where the pin allows one. A full "
            + "commit SHA is its own commit, and is fetched only once git's own walk over every branch and tag of the repository "
            + "(for-each-ref --contains) finds one that reaches it: GitHub serves a commit of any fork under the repository's own name, "
            + "and one that is not the repository's own is refused. The repository is fetched as a partial clone of commits and tag "
            + "objects alone, in a directory of the fetch's own - the one ref a tag or a branch names, at its tip, or every branch and "
            + "tag with their history for the walk that proves a SHA - and the commit's tree, asked for by its id with no blob, read "
            + "whole at the workflow's directory; the whole is refused with nothing cached where the directory holds "
            + "a symbolic link or a submodule, or a path that is rooted, holds '..', '\\', ':' or a control character, or is another's "
            + "where case is folded, or where the file the use names is not in it; every blob of the directory comes in one transfer, "
            + "asked for by its id with nothing checked out, and is checked against the hash the tree gives of it, of any number of "
            + "files and any size, each transfer said while it lasts. github.com is reached with the helpers git's configuration names; "
            + "a host beyond it with the one credential its entry names, handed to git in its environment with every helper cleared, "
            + "and none of the machine's. It is kept in the "
            + $"workflow cache, {WorkflowCache.DirectoryName}/ under this user's own directory for this tool, named the first time a "
            + "remote use needs it: each blob as its bytes at blobs/<first 2 hex>/<remaining 38 hex>, one entry per repository, commit "
            + "and directory, held to its key as it is read, and each ref's newest resolution with its time; every blob is hashed "
            + "again as it is read, so a blob changed on disk is refused and its directory fetched again, and a use is recorded "
            + "with the look that its entry is still there, so one a pruning removed as it was read is fetched again. A second run "
            + "of a pinned SHA reads the cache with no network, a tag needs one call at each run's start, and a run records each "
            + "use's resolution: a resume uses the recorded commit and never resolves again, so a tag or branch that moved since "
            + "changes no run already started. A run keeps every file of the remote workflows it began with beside its frozen "
            + $"definition, in {OrchestratorLayout.RemoteWorkflowsDirectoryName}/ of its own directory, as the check that let it start "
            + "proved, read and hashed them, and its steps read them there as it runs, as the check a resume goes through reads "
            + "them there first - the cache, and then the forge, asked only for a use the copy does not hold whole: neither a "
            + "pruning of the cache, the network not being there, nor a tag deleted or a branch pushed over since takes a file "
            + "from a run that started or refuses it, and each session's start keeps again what the run's copy does not hold whole. A remote workflow is "
            + "trusted by configuration alone - it directs agents and machine steps in "
            + "this repository - so workflowSources in config.json allows its owner (empty by default, and a remote use is refused "
            + "until then); names each GitHub Enterprise host with its one credential, \"credential\": {\"from\": \"env\", \"name\": "
            + "\"<VARIABLE>\"}, a host with none or whose variable is not set refused before anything is asked of it; pins the refs it takes "
            + $"({WorkflowSourceRules.PinTagOrCommit} by default, {WorkflowSourceRules.PinCommit}, or {WorkflowSourceRules.PinAny}, which allows a branch, "
            + $"warned of wherever one is resolved); and says what stands in where the network keeps the forge from being reached "
            + $"({WorkflowSourceRules.OfflineRefuse}, or {WorkflowSourceRules.OfflineLastResolved}, the cache's newest resolution of the ref, held to the pin "
            + "as the forge's own answer is - a ref the cache last resolved as a branch refused where the pin is not any - and "
            + "warned of where it stands in) - a forge that answered, refusing the credential or naming a thing not there, is "
            + "refused with what git said, and nothing stands in for it. A refusal of what the source is - not allowed, a host or "
            + "credential not named, a ref the pin refuses or that is not the repository's own, a file the directory does not "
            + "hold, a forge that answered and refused - is the definition's problem, said with the use; a fetch that could not run "
            + $"keeps its own code: the forge not reached with nothing standing in ({HarnessExit.CommandFailed}, git saying it made no "
            + $"connection), git failing otherwise or answering what does not read ({HarnessExit.CommandFailed}), and the workflow cache that could not be named, "
            + $"written or read, or whose lock could not be taken ({WorkflowCache.FailedExit}). Its files resolve against "
            + "its cache entry as a local workflow's do against its directory, a relative use inside it resolves inside the same "
            + "entry - one that leaves the entry refused - and no local use resolves under remote/, the cache's own reserved place "
            + "in the workflows folder, as no workflow is named remote. A remote workflow's schemas are held to the subset of JSON "
            + "Schema the engine reads as a local one's are. A command that runs under an agent - told by the one rule "
            + $"{Agents.SideTasks.SideTaskService.RunCommand} is told by, the agent's mark or a process of this tool's above it in the "
            + "process tree, one whose tree cannot be read taken as no person's, so a command any process of this tool's starts (a "
            + "leg's, a check's or a machine step's as much) is taken for an agent's - never reaches the forge nor writes the cache: "
            + $"{WorkflowValidationService.CommandName} and {WorkflowExportService.CommandName} there read the cache alone, and a use the cache does "
            + "not hold whole is refused saying what made the command an agent's - the mark, the process of this tool's by its name "
            + "and id, or why the tree could not be read - and that a person fills the cache from "
            + $"{Agents.AgentAncestryAnswer.APersonsShell}, with '{ToolPackage.Command} {WorkflowValidationService.CommandName}' or "
            + $"'{ToolPackage.Command} {WorkflowExportService.CommandName} --from'; a record or a blob of the cache's that could not be read "
            + $"is the cache's own failure there too ({WorkflowCache.FailedExit}). "
            + $"{WorkflowValidationService.CommandName} names the workflow cache's directory on the line it ends on wherever it went to "
            + $"resolve a remote use, whether the workflows hold or not, and {WorkflowExportService.CommandName} --from on its own; "
            + "both name it in --json, as cache, on every exit.";

    /// <summary>What the workflow-cache verbs do, every bound named from the code that holds it.</summary>
    internal static string WorkflowCacheHelp
        => $"{ToolPackage.Command} {WorkflowCacheService.CommandName} list says each entry the cache holds - where it came from, its commit, its "
            + "files and bytes, when it was fetched and last used, and the refs whose newest resolution it is - and every file of "
            + $"the cache's own that could not be read. {ToolPackage.Command} {WorkflowCacheService.CommandName} prune --unused-days N, the days "
            + "required, 0 or more - any number, those reaching back past the clock's start putting the cut-off there - removes the "
            + "entries no run used and none was fetched for N days, the resolutions that named them, and the blobs no entry left "
            + "holds - every blob an entry it keeps holds kept, and no blob removed at all while a file of the cache's own could not "
            + $"be read; a file it could not remove or read is said, and the pruning ends incomplete ({HarnessExit.Incomplete}). Both name "
            + "the cache's directory, in text and in --json. Every record of an entry, a use or a resolution, and every removal a "
            + "pruning makes, is made under one lock, held for one record or one batch of blobs at a time - a fetch's blobs are "
            + "written before its entry, outside it, and the entry only once every blob it names is there - so a pruning never "
            + "takes a blob an entry holds and never holds a fetch off for longer than a moment; a wait for the lock lasts while "
            + "the process holding it lives, told every five minutes, and a cache that could not be named, written or read, or "
            + $"whose lock could not be taken, ends incomplete ({WorkflowCache.FailedExit}).";
}
