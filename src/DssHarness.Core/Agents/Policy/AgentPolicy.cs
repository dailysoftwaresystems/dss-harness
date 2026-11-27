using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Runs;
using DssHarness.Core.Text;
using DssHarness.Core.Walks;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The one evaluator of what an agent may do, the same on every client: everything is allowed
/// except the root actions, the places no agent reaches and the restrictions - which only ever
/// refuse, from every source at once, and never remove one another's.
/// </summary>
/// <remarks>
/// <para>
/// Asked of every action an agent asks for - a read, a search, a write, a command - and answered
/// <see cref="AgentPolicyAnswer.Allow"/> with the input as asked or a denial with the rule and the
/// reason, which the model reads. Where the evaluator cannot decide, it denies: a call this tool
/// cannot decide never runs, so nothing runs while the host that would answer is silent or gone -
/// the client that waits for this answer holds every action behind it.
/// </para>
/// <para>
/// A path is decided as asked and as resolved, each spelled whole first - a home (<c>~</c>,
/// <c>$HOME</c>) expanded, and on Windows a device prefix (<c>\\?\</c>) dropped and a shell's
/// spelling of a drive (<c>/c/x</c>) read as the drive and as the directory it also names, a
/// decision holding for both - and a refusal's places are
/// compared without regard to case on every system, each spelled whole as the runtime spells a
/// full path - on Windows each short name (<c>HARNES~1</c>) given as the long name it stands for -
/// so no spelling a file system finds the place by reaches past one. What a refusal allows - a writable root, the step's <c>writes</c> -
/// compares as the platform compares names.
/// </para>
/// <para>
/// Said in help, and true: this is policy, not a sandbox. A program an agent may run opens files as
/// the user it runs as, a file of the credential slot among them; what its commands are never given
/// is a key in their environment, and what no file tool of an agent reads or writes is the slot
/// itself, in any root.
/// </para>
/// </remarks>
public sealed partial class AgentPolicy
{
    /// <summary>
    /// The programs that would act as root, refused wherever a command names one, at any position
    /// and in whatever case: a command that reaches for one is refused whole, before any rule is
    /// read. Compared without regard to case on every system, for these are this tool's own names
    /// for the actions it refuses, not programs it runs.
    /// </summary>
    public static IReadOnlyList<string> RootActions { get; } =
        ["sudo", "su", "doas", "pkexec", "run0", "gsudo", "sudoedit", "runas", "psexec", "systemd-run"];

    /// <summary>
    /// Git's state-writing verbs, refused as commands by the worktree rules: a fold refuses an
    /// agent whose HEAD left its base, and these are the verbs that move it or write what the
    /// repository holds beside it - its refs and the store that holds them, objects, configuration, remotes and worktrees,
    /// its experimental history rewriters (<c>history</c>, <c>replay</c>) among them. Reading git - status, diff, log, show,
    /// blame, grep - stays allowed, and so does the rest of git but the forms of it that write the same
    /// (<see cref="GitStateForms"/>). Held off a side-task's agent too while a run works over the tree
    /// (<see cref="LiveRunHeld"/>).
    /// </summary>
    public static IReadOnlyList<string> GitStateVerbs { get; } =
        ["commit", "stash", "reset", "clean", "checkout", "switch", "restore", "rebase", "merge", "cherry-pick", "revert", "pull", "push", "am", "worktree", "branch", "tag", "update-ref", "config", "gc",
         "bisect", "symbolic-ref", "filter-branch", "fast-import", "replace", "notes", "reflog", "remote", "submodule", "sparse-checkout", "read-tree", "prune", "repack", "pack-refs", "maintenance",
         "history", "replay", "refs"];

    /// <summary>
    /// The forms of git's other commands that write what <see cref="GitStateVerbs"/> do, each its program, its command and
    /// an option of it met wherever it stands among the command's words after its command (<see cref="Meets"/>) - a short
    /// option by any cluster that holds its letter, a long one by any start of its name, as git takes one: fetch's
    /// <c>--update-head-ok</c>, with which a fetch writes the branch a worktree has checked out, moving its HEAD under its
    /// index and its files, as git refuses a fetch without it. Refused by the worktree rules beside those verbs, and held off
    /// a side-task's agent while a run works over the tree (<see cref="LiveRunHeld"/>).
    /// </summary>
    public static IReadOnlyList<string> GitStateForms { get; } = ["git fetch --update-head-ok", "git fetch -u"];

    /// <summary>
    /// gh's own commands and help topics, as gh 2.89 lists them and gh's manual names them (<c>discussion</c>,
    /// <c>skill</c>, <c>telemetry</c>), each read by its own name where gh gives it another
    /// (<see cref="CommandAnalysis.GhAliases"/>): the first word of a gh command that is none of them is an alias - the
    /// <c>co</c> gh's configuration holds for <c>pr checkout</c> among them - or an extension, which gh runs as a command
    /// no rule here reads, and the worktree rules refuse it as they refuse <c>gh alias set</c>. A command a later gh adds
    /// is refused until it is named here.
    /// </summary>
    public static IReadOnlyList<string> GhCommands { get; } =
    [
        "agent-task", "alias", "api", "attestation", "auth", "browse", "cache", "codespace", "completion", "config", "copilot",
        "extension", "gist", "gpg-key", "help", "issue", "label", "licenses", "org", "pr", "preview", "project", "release",
        "repo", "ruleset", "run", "search", "secret", "ssh-key", "status", "variable", "version", "workflow",
        "discussion", "skill", "accessibility", "actions", "environment", "exit-codes", "formatting", "mintty", "reference", "telemetry",
    ];

    /// <summary>
    /// git's own commands, as git 2.55 lists its builtins and its main commands (<c>git --list-cmds=builtins,main</c>) -
    /// its porcelain, its ancillary and interacting commands, its plumbing, its internal helpers and its experimental
    /// commands - with the helpers git runs under its own name: a name git runs as itself, which no alias can stand for
    /// (git passes over an alias of one of its own names). A first word that is none of them is a program
    /// <c>git-&lt;name&gt;</c> this machine has - <c>git lfs</c> - or an alias, which git runs from a configuration file no
    /// rule reads: the policy tells the two apart by looking for the program (<see cref="GitProgram"/>), and refuses the
    /// alias on every ground. The list is this tool's own rather than asked of the git the agent runs: git's listing of
    /// its commands is internal to git and no stable interface, and the policy decides a command by reading its words
    /// alone, starting no program to do it. A command a later git adds is refused until it is named here.
    /// </summary>
    public static IReadOnlyList<string> GitCommands { get; } =
    [
        "add", "am", "annotate", "apply", "archimport", "archive", "backfill", "bisect", "blame", "branch", "bugreport", "bundle",
        "cat-file", "check-attr", "check-ignore", "check-mailmap", "check-ref-format", "checkout", "checkout--worker", "checkout-index",
        "cherry", "cherry-pick", "citool", "clean", "clone", "column", "commit", "commit-graph", "commit-tree", "config",
        "count-objects", "credential", "credential-cache", "credential-cache--daemon", "credential-store", "cvsexportcommit",
        "credential-wincred", "cvsimport", "cvsserver", "daemon", "describe", "diagnose", "diff", "diff-files", "diff-index", "diff-pairs", "diff-tree",
        "difftool", "difftool--helper", "fast-export", "fast-import", "fetch", "fetch-pack", "filter-branch", "fmt-merge-msg",
        "for-each-ref", "for-each-repo", "format-patch", "format-rev", "fsck", "fsck-objects", "fsmonitor--daemon", "gc", "get-tar-commit-id",
        "gitk", "gitweb", "grep", "gui", "gui--askpass", "gui--askyesno", "hash-object", "help", "history", "hook", "http-backend", "http-fetch", "http-push",
        "imap-send", "index-pack", "init", "init-db", "instaweb", "interpret-trailers", "last-modified", "log", "ls-files",
        "ls-remote", "ls-tree", "mailinfo", "mailsplit", "maintenance", "merge", "merge-base", "merge-file", "merge-index",
        "merge-octopus", "merge-one-file", "merge-ours", "merge-recursive", "merge-recursive-ours", "merge-recursive-theirs",
        "merge-resolve", "merge-subtree", "merge-tree", "mergetool", "mergetool--lib", "mktag", "mktree", "multi-pack-index", "mv",
        "name-rev", "notes", "p4", "pack-objects", "pack-redundant", "pack-refs", "patch-id", "pickaxe", "prune", "prune-packed", "pull",
        "push", "quiltimport", "range-diff", "read-tree", "rebase", "receive-pack", "reflog", "refs", "remote", "remote-ext",
        "remote-fd", "remote-ftp", "remote-ftps", "remote-http", "remote-https", "repack", "replace", "replay", "repo",
        "request-pull", "rerere", "reset", "restore", "rev-list", "rev-parse", "revert", "rm", "scalar", "send-email", "send-pack",
        "sh-i18n", "sh-i18n--envsubst", "sh-setup", "shell", "shortlog", "show", "show-branch", "show-index", "show-ref",
        "sparse-checkout", "stage", "stash", "status", "stripspace", "submodule", "submodule--helper", "subtree", "survey", "svn",
        "switch", "symbolic-ref", "tag", "unpack-file", "unpack-objects", "update-index", "update-ref", "update-server-info",
        "upload-archive", "upload-archive--writer", "upload-pack", "url-parse", "var", "verify-commit", "verify-pack", "verify-tag", "version",
        "web--browse", "whatchanged", "worktree", "write-tree",
    ];

    /// <summary>
    /// The <c>gh</c> commands that write the state the worktree rules keep, refused by them beside git's state-writing
    /// verbs (<see cref="GitStateVerbs"/>): git's, run on the repository they stand in - a pull request's or an issue's
    /// branch fetched and checked out, a pull request closed with its branch deleted (checked out of first), a default
    /// repository or a remote written into git's configuration, a fork or a new repository made with its remote, its push
    /// or its clone, a repository renamed with its remote, gh made git's credential helper, a pull request opened with its
    /// branch pushed and its upstream set, one merged with its branch deleted - its base checked out and pulled first - and
    /// the local repository synced from its source, its branch merged or reset to the source's - and gh's own
    /// configuration, which names the editor, pager and browser every gh command runs. Each is held whole, as the worktree
    /// rules refuse it, whatever its options: the three that open, merge and sync start CI as well
    /// (<see cref="NeverCiCommands"/>). Held off a side-task's agent too while a run works over the tree
    /// (<see cref="LiveRunHeld"/>).
    /// </summary>
    public static IReadOnlyList<string> GhStateCommands { get; } =
    [
        "gh pr checkout", "gh pr close", "gh issue develop", "gh repo set-default", "gh repo fork", "gh repo create",
        "gh repo rename", "gh auth setup-git", "gh pr create", "gh pr merge", "gh repo sync", "gh config set",
    ];

    /// <summary>
    /// The <c>gh</c> commands that leave a step's limits, refused whatever <c>worktreeRules</c> says, as
    /// <c>run-side-task</c> is: the work handed to another agent - the Copilot CLI, which runs commands no rule here reads,
    /// and an agent task, which works and opens a pull request where no rule reaches - and skills installed, updated or
    /// fixed where the next launch loads them.
    /// </summary>
    public static IReadOnlyList<string> GhOutOfStepCommands { get; } =
        ["gh copilot", "gh agent-task create", "gh skill install", "gh skill update", "gh skill publish"];

    /// <summary>
    /// The commands that print a credential - gh's own token, by itself, by its status's <c>--show-token</c>, by gh as
    /// git's credential helper and by gh's configuration where it holds one, and git's credential helpers' <c>get</c> and
    /// <c>fill</c>, which write the credential they find to what reads them - refused whatever <c>worktreeRules</c> says,
    /// as <c>run-side-task</c> is: a credential leaves its slot only by the key store that holds it, never through an
    /// agent's words. Each is its program and its command - a <c>*</c> at the command's end any helper's name - then the
    /// words that make it print, met anywhere among the command's words after its command, whatever options stand
    /// before or between them (<c>git credential-store --file f get</c>), a short option by any cluster that holds its
    /// letter (<see cref="Meets"/>). Printed by <c>help agent-clients</c> and pinned by a test, so a change
    /// in it is a change someone made.
    /// </summary>
    public static IReadOnlyList<string> CredentialPrintingCommands { get; } =
        ["gh auth token", "gh auth status --show-token", "gh auth status -t", "gh auth git-credential get", "gh config get oauth_token", "git credential fill", "git credential-* get"];

    /// <summary>
    /// Every way <c>gh</c> merges or starts CI, refused by the worktree rules - never merges, never
    /// starts CI binds the agents too, not only the engine's own steps: each REST endpoint and GraphQL
    /// mutation that does it, with the <c>gh</c> commands that reach it. The one list the four below
    /// are read from, so a command that reaches a refused endpoint is refused with it: a pull request's
    /// merge, its auto-merge and its queue, the update of its branch, a branch's merge, a fork's sync, a
    /// workflow's dispatch, approval and reruns, a check asked again - and, where the request writes, a
    /// push through the API (a ref made, moved or deleted, a file committed, a linked branch made), a pull
    /// request opened, and a release made or published, its tag with it.
    /// </summary>
    public static IReadOnlyList<GhCiReach> GhCiReaches { get; } =
    [
        new("pulls/<n>/merge", Mutation: false, ["gh pr merge"]),
        new("pulls/<n>/update-branch", Mutation: false, ["gh pr update-branch"]),
        new("merges", Mutation: false, []),
        new("merge-upstream", Mutation: false, ["gh repo sync"]),
        new("dispatches", Mutation: false, ["gh workflow run"]),
        new("approve", Mutation: false, []),
        new("rerun", Mutation: false, ["gh run rerun"]),
        new("rerun-failed-jobs", Mutation: false, ["gh run rerun"]),
        new("rerequest", Mutation: false, []),
        new("mergePullRequest", Mutation: true, ["gh pr merge"]),
        new("enablePullRequestAutoMerge", Mutation: true, ["gh pr merge"]),
        new("enqueuePullRequest", Mutation: true, ["gh pr merge"]),
        new("mergeBranch", Mutation: true, []),
        new("updatePullRequestBranch", Mutation: true, ["gh pr update-branch"]),
        new("rerequestCheckSuite", Mutation: true, []),
        new("git/refs/**", Mutation: false, [], Writes: true),
        new("contents/**", Mutation: false, [], Writes: true),
        new("pulls", Mutation: false, ["gh pr create"], Writes: true),
        new("releases", Mutation: false, ["gh release create"], Writes: true),
        new("releases/<n>", Mutation: false, ["gh release edit"], Writes: true),
        new("createRef", Mutation: true, []),
        new("updateRef", Mutation: true, []),
        new("updateRefs", Mutation: true, []),
        new("deleteRef", Mutation: true, []),
        new("createCommitOnBranch", Mutation: true, []),
        new("createLinkedBranch", Mutation: true, []),
        new("createPullRequest", Mutation: true, ["gh pr create"]),
    ];

    /// <summary>
    /// The <c>gh</c> commands that merge or start CI (<see cref="GhCiReaches"/>), each once, refused by
    /// the worktree rules. <c>gh api</c> reaching the same endpoints, and <c>gh alias</c> making a name
    /// for any command, are refused beside them (<see cref="GhApiEndpoints"/>, <see cref="GhApiMutations"/>).
    /// </summary>
    public static IReadOnlyList<string> NeverCiCommands { get; } = [.. GhCiReaches.SelectMany(reach => reach.Commands).Distinct(StringComparer.Ordinal)];

    /// <summary>The REST endpoints through which <c>gh api</c> merges or starts CI (<see cref="GhCiReaches"/>), refused by the worktree rules at a path's end.</summary>
    public static IReadOnlyList<string> GhApiEndpoints { get; } = [.. GhCiReaches.Where(reach => !reach.Mutation && !reach.Writes).Select(reach => reach.Reached)];

    /// <summary>
    /// The REST endpoints through which <c>gh api</c> pushes, opens a pull request or makes a release
    /// (<see cref="GhCiReaches"/>), refused by the worktree rules at a path's end - or past it, where one ends in
    /// <c>/**</c> - where the request writes (<see cref="CommandAnalysis.GhApiWrites"/>): reading them stays allowed.
    /// </summary>
    public static IReadOnlyList<string> GhApiWriteEndpoints { get; } = [.. GhCiReaches.Where(reach => !reach.Mutation && reach.Writes).Select(reach => reach.Reached)];

    /// <summary>The GraphQL mutations through which <c>gh api graphql</c> merges or starts CI (<see cref="GhCiReaches"/>), refused by the worktree rules wherever a word names one.</summary>
    public static IReadOnlyList<string> GhApiMutations { get; } = [.. GhCiReaches.Where(reach => reach.Mutation).Select(reach => reach.Reached)];

    /// <summary>
    /// This tool's own orchestrator and agent verbs, and the stop of a run of legs, refused whatever
    /// <c>worktreeRules</c> says: an agent does not move its own run, nor stop one. Each is the
    /// command's own name, as the service that serves it declares it.
    /// </summary>
    public static IReadOnlyList<string> ToolVerbs { get; } =
    [
        OrchestratorService.CreateCommand, OrchestratorService.DeleteCommand, FlowService.RunCommand, FlowService.ResumeCommand,
        FlowService.AnswerCommand, FlowService.PauseCommand, FlowService.StopCommand, AgentService.CreateCommand,
        AgentService.SeedCommand, AgentService.RefreshCommand, AgentService.RebaseCommand, AgentService.FoldCommand,
        AgentService.DeleteCommand, RunStopService.CommandName,
    ];

    /// <summary>
    /// This tool's verbs that read a credential slot or write the tool's own state or install, refused to every agent by
    /// name - a side-task's as well as a step's: each would read what no agent may, or change what the tool runs by, and
    /// inside the operating system's isolation it would fail part way or answer from what it could not read. The verbs
    /// that only read, and those that write only the tree's own files, stay open (<see cref="OpenVerbs"/>); every verb of
    /// the tool's is one or the other, its tests holding the two lists to the tool's own commands. Each is the command's
    /// own name, as the service that serves it declares it.
    /// </summary>
    public static IReadOnlyList<string> SlotOrStateVerbs { get; } =
    [
        Commands.InitService.CommandName, Worktrees.WorktreeService.CreateCommand, Worktrees.WorktreeService.DeleteCommand,
        OrchestratorService.CreateCommand, OrchestratorService.DeleteCommand, Agents.AgentProvidersService.ListCommand,
        FlowService.RunCommand, FlowService.ResumeCommand, FlowService.AnswerCommand, FlowService.AmendCommand,
        FlowService.PauseCommand, FlowService.StopCommand, FlowService.AskStatusCommand, FlowService.AskCommand,
        FlowService.AnalyzeReportCommand,
        AgentService.CreateCommand, AgentService.SeedCommand, AgentService.RefreshCommand, AgentService.RebaseCommand,
        AgentService.FoldCommand, AgentService.DeleteCommand, Legs.LegsService.CommandName, Tools.ToolProvisionService.CommandName,
        Hosts.ToolUpdateService.CommandName, Sync.SyncService.CommandName, Build.BuildService.CommandName, Testing.TestService.CommandName,
        Runners.RunnerRunService.CommandName, Mutations.MutationService.CommandName, RunStopService.CommandName,
        CleanService.CommandName, HostExecService.CommandName, SetUps.SetUpService.UpCommand, SetUps.SetUpService.DownCommand,
        Hosts.HostAgentProtocol.CommandName, Sync.SyncServe.CommandName, Execution.HoldAwakeService.CommandName, Processes.ContainExec.CommandName,
        Processes.MxcProcessContainer.CommandName, WorkflowCacheService.CommandName,
    ];

    /// <summary>
    /// This tool's verbs open to an agent: each only reads, or writes only the tree's own files - never a slot, the tool's
    /// state or its install. With <see cref="SlotOrStateVerbs"/> and <c>run-side-task</c>, every verb the tool has.
    /// <c>validate-workflow</c> and <c>export-workflow</c> resolve a remote workflow a person's command would fetch into
    /// the workflow cache; run by an agent - its every child carries <see cref="AgentChildEnvironment.AgentMarker"/> - they
    /// read the cache alone and write nothing to it (<see cref="Orchestration.Flow.WorkflowRemoteFetcher"/>), refused
    /// naming the person's command where it does not hold what they need.
    /// </summary>
    public static IReadOnlyList<string> OpenVerbs { get; } =
    [
        "help", "verify-git", Worktrees.WorktreeService.ListCommand, OrchestratorService.ListCommand, SideTasks.SideTaskService.ListCommand,
        WorkflowValidationService.CommandName, WorkflowExportService.CommandName, "write-anchor", "set-anchor",
        "read-anchor", "read-anchors", "check-anchor-balance", "check-anchor-arms", "check-anchor-citations", Worktrees.RootLitterService.CommandName,
        "fix-line-endings", "check-ci-legs", Codebase.CodebaseGraphQueryService.CommandName, Orchestration.Flow.FlowService.ReadReportCommand,
        Execution.QueueReports.CommandName,
    ];

    /// <summary>
    /// The git verbs that write its index beside its state-writing verbs (<see cref="GitStateVerbs"/>): staging, taking
    /// out, moving and the index's own plumbing - and <c>apply</c>, whose <c>--index</c> and <c>--cached</c> write it
    /// wherever they stand among its words, so the verb is held whole. Open to a step's agent in its own worktree, and held
    /// off a side-task's while a run works over the tree (<see cref="LiveRunHeld"/>), for the run's commit step stages its
    /// own set and refuses an index that holds anything else.
    /// </summary>
    public static IReadOnlyList<string> GitIndexVerbs { get; } = ["add", "stage", "rm", "mv", "apply", "update-index"];

    /// <summary>
    /// The commands held off a side-task's agent while a run works over the tree (<see cref="AgentPolicyGround.LiveRun"/>),
    /// each with any words after it: git's state-writing verbs (<see cref="GitStateVerbs"/>), the verbs that write its
    /// index (<see cref="GitIndexVerbs"/>) and gh's that write git's state (<see cref="GhStateCommands"/>) - and, beside
    /// them, the forms of git's other commands that write it (<see cref="GitStateForms"/>): every way a command writes the
    /// HEAD, index, refs, stash, configuration or worktrees the run works over. The run moves HEAD and stages and commits
    /// its own work, and an agent's moving any of it beside the run would weigh the run's work against a tree the run never
    /// saw. Each is met as the worktree rules' commands are, by every reading of the command
    /// (<see cref="CommandAnalysis.ForRules(IReadOnlyList{string}, bool, IReadOnlyList{string}?)"/>) - git's options
    /// before its verb dropped, <c>git-commit</c> read as <c>git commit</c>, a configuration set where it runs read as the
    /// <c>git config</c> it is - and a word the shell expands where a held word would stand is held as one that may be it.
    /// Their forms that only read stay open (<see cref="LiveRunOpen"/>, <see cref="CommandAnalysis.GitReadsOnly"/>), as
    /// does the git that writes none of it.
    /// </summary>
    public static IReadOnlyList<string> LiveRunHeld { get; } =
        [.. GitStateVerbs.Select(verb => $"git {verb}"), .. GitIndexVerbs.Select(verb => $"git {verb}"), .. GhStateCommands];

    /// <summary>
    /// The forms of the held commands (<see cref="LiveRunHeld"/>) that only list or read by their verb alone, open to a
    /// side-task's agent while a run works over the tree, each a <c>Run</c> pattern - a trailing <c>*</c> any words after
    /// it, every other form exactly as written: the listings an agent runs to see where it stands. Each is a verb of the
    /// command's that only reads, so no word after it makes the command one that writes. The held verbs whose reading is a
    /// mode their options choose - git branch, tag, config, symbolic-ref, reflog and notes - are read as git reads those
    /// options, wherever they stand (<see cref="CommandAnalysis.GitReadsOnly"/>). A form is open only where the command is
    /// it for certain: one a word the shell expands may make another is held.
    /// </summary>
    public static IReadOnlyList<string> LiveRunOpen { get; } =
    [
        "git stash list *", "git stash show *",
        "git worktree list *",
        "git remote", "git remote -v", "git remote --verbose", "git remote show *", "git remote get-url *",
        "git submodule", "git submodule status *",
        "git sparse-checkout list *",
        "git bisect log *", "git bisect visualize *", "git bisect view *",
        "git refs verify *", "git refs list *", "git refs exists *",
    ];

    /// <summary>
    /// The rule git's own files are refused by as writes under the worktree rules, whatever
    /// <c>protectedPaths</c> says: a <c>.git</c>, file or directory, at any depth beneath a root and
    /// in any case - a nested repository's and a submodule's as well as the root's own.
    /// </summary>
    public const string GitsOwnRule = "Write(**/.git/**)";

    /// <summary>
    /// The files that hold the tool's own policy and what the next launch loads, refused as writes
    /// by the worktree rules: an agent does not edit its own policy or what the next launch loads.
    /// The skills directory's exception - a step whose <c>writes</c> names one skill's own directory
    /// by name - is read where the rule is matched, not written here.
    /// </summary>
    public static IReadOnlyList<string> WorktreePolicyWrites { get; } =
        [".harness-config/config.json", ".harness-config/orchestrators/**", ".harness-config/skills/**", ".harness-config/runner/**", ".claude/settings*.json", ".mcp.json"];

    /// <summary>
    /// Claude Code's protected paths as its documentation gives them for the oldest version this
    /// tool takes, written out once as rules: the directories and files it guards, refused on every
    /// client, so a role is not stopped on one where it passes on another. Printed by
    /// <c>help agent-clients</c> and pinned by a test, so a change in it is a change someone made.
    /// <c>agentPolicy.protectedPaths: false</c> takes this tool's refusal away; Claude Code's own
    /// guard stays Claude Code's.
    /// </summary>
    public static IReadOnlyList<string> ProtectedPathRules { get; } =
        [
            ".git/**", ".config/git/**", ".claude/**", ".vscode/**", ".idea/**", ".husky/**", ".cargo/**",
            ".devcontainer/**", ".yarn/**", ".mvn/**", ".ssh/**", ".mcp.json", ".claude.json",
            ".bashrc", ".bash_profile", ".profile", ".zshrc", ".zprofile", ".zshenv", ".config/fish/config.fish",
            "package.json", "package-lock.json", "yarn.lock", "pnpm-lock.yaml", "bun.lock", "bun.lockb",
            "Cargo.toml", "Cargo.lock", "go.mod", "go.sum", "pom.xml", "requirements.txt", "pyproject.toml",
            "Gemfile", "Gemfile.lock", "composer.json", "composer.lock",
        ];

    /// <summary>
    /// How a refusal's places compare: without regard to case on every system, for a file system
    /// that folds case - Windows', macOS's by default, a folded directory on Linux - finds the place
    /// by any spelling, and a refusal that misses one is no refusal.
    /// </summary>
    private const StringComparison Refusals = StringComparison.OrdinalIgnoreCase;

    private readonly IFileSystem _fileSystem;
    private readonly IHostPlatform _platform;
    private readonly AgentPolicyGround _ground;
    private readonly IReadOnlyList<string> _readRoots;
    private readonly IReadOnlyList<string> _writableRoots;
    private readonly IReadOnlyList<string> _refusalReadRoots;
    private readonly IReadOnlyList<string> _allRoots;
    private readonly IReadOnlyList<Unreachable> _unreachable;
    private readonly IReadOnlyList<string> _state;
    private readonly IReadOnlyList<(AgentRule Rule, AgentRestriction Declared)> _rules;
    private readonly IReadOnlyList<string> _webDomains;
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _writes;
    private readonly IReadOnlyList<AgentRule> _worktreeRunRules;
    private readonly IReadOnlyList<AgentRule> _outOfStepRules = [.. GhOutOfStepCommands.Select(command => AgentRule.Parse($"Run({command} *)"))];
    private readonly IReadOnlyList<AgentRule> _liveRunHeldRules = [.. LiveRunHeld.Select(command => AgentRule.Parse($"Run({command} *)"))];
    private readonly IReadOnlyList<AgentRule> _liveRunOpenRules = [.. LiveRunOpen.Select(form => AgentRule.Parse($"Run({form})"))];
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _worktreePolicyWrites;
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _protectedPaths;
    private readonly string _workingRoot;
    private readonly string? _reporterRoot;
    private readonly string? _resolvedReporterRoot;
    private readonly string? _programPath;
    private readonly IReadOnlyList<string> _gitTrees;
    private readonly string? _cdPath;
    private readonly IReadOnlyCollection<string> _guardedNames;

    /// <summary>
    /// Reads the ground once: its roots and the places no action reaches resolved, its restrictions
    /// parsed - a restriction that does not read refuses the agent the policy is for, where it is
    /// built, not the action that first met it, and so does a root or a place that cannot be
    /// resolved.
    /// </summary>
    public AgentPolicy(IFileSystem fileSystem, IHostPlatform platform, AgentPolicyGround ground)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(ground);

        _fileSystem = fileSystem;
        _platform = platform;
        _ground = ground;
        _readRoots = Resolve([ground.WorkingDirectory, .. ground.ReadRoots]);
        _writableRoots = Resolve(ground.WritableRoots);

        // The places a refusal names, one by any case of its spelling, as every path asked is compared.
        _refusalReadRoots = [.. _readRoots.Distinct(StringComparer.OrdinalIgnoreCase)];
        _allRoots = [.. Resolve(ground.Roots(platform.PathComparison)).Distinct(StringComparer.OrdinalIgnoreCase)];
        _unreachable =
        [
            new(Resolve(ground.SlotDirectories), "the credential slots", "a credential slot of this tool", "is a credential slot of this tool", "names a credential slot of this tool"),
            new(Resolve(ground.ClaudeCodeDirectories), "the Claude Code directories", "a Claude Code directory this run reads", "lies in a Claude Code directory this run reads", "lies in a Claude Code directory this run reads"),
            new(Resolve([.. ground.CredentialPlaces.Select(place => place.Path)]), "the user's credentials", "a place the user keeps credentials in", "lies in a place the user keeps credentials in", "lies in a place the user keeps credentials in"),
        ];
        _state = Resolve(ground.StateDirectories);
        _rules = [.. ground.Restrictions.Select(declared => (AgentRule.Parse(declared.Text), declared))];

        // A domain is held as the configuration's check reads it: trimmed, its root dot gone, as a
        // host is.
        _webDomains = [.. (ground.WebDomains ?? []).Select(domain => domain.Trim().TrimEnd('.'))];
        // The step's writes are a list of its tree's own paths - a work item's owned paths flow into them - so each is
        // read from the worktree's root, as the plan's check read it: a path names a directory's contents only where the
        // worktree holds that directory.
        _writes = ground.Writes is { } writes
            ? [.. writes.Select(pattern => (GlobPattern.FromRoot(pattern, GlobPattern.DirectoriesIn(fileSystem, ground.WorkingDirectory)), pattern))]
            : [];
        _worktreeRunRules =
            [.. GitStateVerbs.Select(verb => AgentRule.Parse($"Run(git {verb} *)")),
               .. GhStateCommands.Concat(NeverCiCommands).Distinct(StringComparer.Ordinal).Select(command => AgentRule.Parse($"Run({command} *)"))];
        _worktreePolicyWrites = [.. WorktreePolicyWrites.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _protectedPaths = [.. ProtectedPathRules.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _workingRoot = Resolved(Whole(ground.WorkingDirectory));

        // The record of the step's start names its paths relative to the worktree it was taken of, which is where the
        // agent works: read against any other tree, its paths would name other places.
        if (ground.StepStart is { } start && !PathContainment.AreSame(Resolved(Whole(start.Worktree)), _workingRoot, Refusals))
        {
            throw new ArgumentException($"the record of the step's start is of '{start.Worktree}', and the agent works in '{ground.WorkingDirectory}'", nameof(ground));
        }

        // The reporter's root as it was given, and where it resolves: a path asked is asked by one of
        // its names, and lands where the other says.
        _reporterRoot = ground.ReporterRoot is { } root ? Whole(Plain(root, ground.WorkingDirectory)) : null;
        _resolvedReporterRoot = ground.ReporterRoot is { } given ? Resolved(Whole(given)) : null;

        // The PATH the agent's commands find their programs on: the ground's, or this process's own, which every agent's
        // child inherits.
        _programPath = ground.ProgramPath ?? Environment.GetEnvironmentVariable("PATH");

        // Every tree whose git's own files no write reaches: the agent's roots, and the trees it touches beyond them.
        _gitTrees = [.. _allRoots.Concat(Resolve(ground.Trees ?? [])).Distinct(StringComparer.OrdinalIgnoreCase)];

        // The directories a cd of the agent's shell looks a relative name up in, which every agent's child inherits.
        _cdPath = Environment.GetEnvironmentVariable("CDPATH");

        // Every name a guard holds - each segment of a place no command reaches and of git's own directory of each tree,
        // and each literal segment of a path rule - which a short name may stand for on Windows.
        _guardedNames = [.. _unreachable.SelectMany(kind => kind.Places).Concat(_state).Concat(_gitTrees.Select(tree => Path.Combine(tree, ".git")))
            .Concat(_rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Read or AgentRuleVerb.Write).Select(pair => pair.Rule.Pattern))
            .Concat(_protectedPaths.Select(pair => pair.Text))
            .Concat(_worktreePolicyWrites.Select(pair => pair.Text))
            .SelectMany(place => place.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
            .Where(name => !name.EndsWith(':') && name.IndexOfAny(['*', '?', '[']) < 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The answer where the tool itself runs elevated (as root or an administrator): no agent starts
    /// while it does, whatever the configuration says, for every rule here assumes a user's powers.
    /// </summary>
    public static AgentPolicyAnswer RefusedWhileElevated()
        => new AgentPolicyAnswer.Denied(
            "the root actions",
            AgentPolicySource.Builtin,
            "this tool itself is running elevated, as root or an administrator: no agent starts while it does");

    /// <summary>
    /// Every problem the configuration's <c>agentPolicy</c> section holds, said with each rule's own
    /// words: a rule that loads is a rule this tool reads.
    /// </summary>
    /// <param name="config">The configuration, as it was read.</param>
    public static IReadOnlyList<string> RuleProblems(HarnessConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        return
        [
            .. (config.AgentPolicy?.Restrictions ?? []).Select((rule, at) => AgentRule.Problem(rule) is { } problem
                ? $"agentPolicy.restrictions[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}] {problem}"
                : null)
                .OfType<string>(),
            .. (config.AgentPolicy?.WebDomains ?? []).Select((domain, at) => AgentRule.DomainProblem(domain) is { } wrong
                ? $"agentPolicy.webDomains[{at.ToString(System.Globalization.CultureInfo.InvariantCulture)}] {wrong}"
                : null)
                .OfType<string>(),
            .. config.AgentPolicy?.WebDomains is { Count: 0 }
                ? (IReadOnlyList<string>)["agentPolicy.webDomains is given empty: leave the key out to allow any address, or name the domains an agent may reach"]
                : [],
            .. AgentChildEnvironment.Problems(
                config.AgentPolicy,
                AgentProviders.Resolve(config).Providers?.Appointed().Select(key => key.Name).ToList() ?? []),
            .. Isolation.AgentIsolationMode.Problem(config.AgentPolicy) is { } isolation ? (IReadOnlyList<string>)[isolation] : [],
            .. Isolation.UserCredentials.Problems(config.AgentPolicy),
        ];
    }

    /// <summary>
    /// Whether an address an agent would ask the web for may be asked: no web at all where the step
    /// has none, then each <c>Web(...)</c> rule against the address's host, then
    /// <c>agentPolicy.webDomains</c>. A page, a redirect and a result a search gives are each decided
    /// here, one address at a time.
    /// </summary>
    /// <param name="address">The address as the agent's tool asks it, or as a fetcher command names it.</param>
    public AgentPolicyAnswer Web(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            return WebDecided(address, address, domains: true, agentRules: true);
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(address, exception);
        }
    }

    /// <summary>
    /// Whether an address a search's source asks for this step may be asked - its endpoint, a lookup, the site of a sitemap
    /// source's index, each redirect of them: no web where the step has none, then each <c>Web(...)</c> rule against the
    /// address's host. <c>agentPolicy.webDomains</c>, the list of the pages an agent may read, does not hold one back: the
    /// source is the configuration's, asked by the harness, and the results it gives are held to the list.
    /// </summary>
    /// <param name="address">The address the source asks.</param>
    public AgentPolicyAnswer WebSource(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            return WebDecided(address, address, domains: false, agentRules: true);
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(address, exception);
        }
    }

    /// <summary>
    /// Whether a page a sitemap source's index reads may be asked, and each redirect of it: no web where the step that
    /// began the fill has none, then each <c>Web(...)</c> rule of the configuration's - the run's, which every step of it
    /// holds - against the address's host; never an agent's own list, nor <c>webDomains</c>. The index is the repository's,
    /// filled for every run that searches it long after the step that began it, so no one step's rules stop it short: a
    /// step searches only the index of a site its own rules let it ask (<see cref="WebSource"/>).
    /// </summary>
    /// <param name="address">The address the fill asks.</param>
    public AgentPolicyAnswer WebFill(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            return WebDecided(address, address, domains: false, agentRules: false);
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(address, exception);
        }
    }

    /// <summary>
    /// Whether a path an agent would read may be read: a credential slot, a Claude Code directory
    /// and a place the user keeps credentials in never, a <c>Read</c> rule where one matches;
    /// everything else is allowed, for reading is what an agent is for.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, ReadMatchers, ReadDecision);
    }

    /// <summary>
    /// Whether a directory a client's own search tool would search may be searched - Claude Code's Grep and Glob, which
    /// read what is beneath it unseen by this tool: as a read of it, and never where a credential slot, a Claude Code
    /// directory or a place the user keeps credentials in lies beneath it. What a <c>Read</c> rule refuses beneath it is
    /// left out by the client's own deny rules, which this tool writes for each launch. This tool's own search tools ask
    /// a read of the directory instead, for they decide each file beneath it as its own read.
    /// </summary>
    /// <param name="path">The directory as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Search(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, ReadMatchers, (asked, resolved) => ReadDecision(asked, resolved).Refusal() is { } denied ? denied : SearchedPast(asked, resolved));
    }

    /// <summary>
    /// Whether a path an agent would write may be written: a credential slot, a Claude Code
    /// directory and a place the user keeps credentials in never, a path under no writable root
    /// never, a <c>Write</c> rule where one matches.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, WriteMatchers, (asked, resolved) => WriteDecision(asked, resolved, untracked: null));
    }

    /// <summary>
    /// What a refusal of a delete outside the step's <c>writes</c> says a step may take back there - the rule
    /// <see cref="Delete"/> holds, said in the denial's own words so the agent reading it knows what it may delete.
    /// </summary>
    public const string OwnMakingSaid = "outside them a step takes back only what it made itself - a path of its worktree git does not track that was not there when the step's run began";

    /// <summary>
    /// Whether a path an agent would take away - a file deleted, a directory deleted with what it holds, the source of a
    /// move - may be: as a write of it is (<see cref="Write"/>), save that the step's <c>writes</c> hold back no path the
    /// step made itself - one of its worktree git does not track (<paramref name="untracked"/>) that its worktree did not
    /// hold, nor hold anything beneath, as the step's run began (<see cref="AgentPolicyGround.StepStart"/>): what the step's
    /// own commands made it may take back, as they may write anywhere in its worktree. Every other refusal of a write holds
    /// a delete as it holds the write - the places no action reaches, git's own files, the tool's state and policy, the
    /// protected paths, the writable roots and every <c>Write</c> rule - and so do the step's writes for a ground that holds
    /// no record of the step's start, and for a path the record holds or git tracks or ignores. A write that would replace
    /// such a path is no taking back: it leaves a path standing outside the writes, and stays narrowed.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    /// <param name="untracked">
    /// Whether git tracks nothing at the path as the caller asked git just before: a file git lists untracked - never one
    /// it tracks or ignores - or a directory, every path of which the caller takes away and decides as a delete of its own,
    /// for git records no directory and a directory holds only what the step made where each path in it is that.
    /// </param>
    public AgentPolicyAnswer Delete(string path, bool untracked)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, WriteMatchers, (asked, resolved) => WriteDecision(asked, resolved, untracked));
    }

    /// <summary>
    /// Whether a command an agent would run may run: flattened whole by
    /// <see cref="CommandAnalysis"/>, refused where any command it runs names a root action, a
    /// credential slot, a Claude Code directory or a place the user keeps credentials in, or where
    /// a <c>Run</c> rule matches one - and denied where it cannot be read whole.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv) => Run(argv, directory: null);

    /// <summary>
    /// Whether a command an agent would run in <paramref name="directory"/> may run: decided as <see cref="Run(IReadOnlyList{string})"/>
    /// decides it, each path it names read against that directory - where the command starts - rather than the working
    /// directory. On Windows a program splits its command line itself, and a Cygwin or MSYS program expands a pattern or
    /// a brace among its words, which is read so (<see cref="ExpandsItsOwnWords"/>,
    /// <see cref="CommandAnalysis.Commands(IReadOnlyList{string}, bool, bool)"/>); every other program's words are literal.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    /// <param name="directory">Where it starts, absolute or relative to the working directory; null for the working directory.</param>
    /// <param name="inputGiven">
    /// Whether the command is given an input this does not see - the text the tool runtime writes to it - which a program
    /// that reads its script from its input would run; read as given where nothing says it is not.
    /// </param>
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv, string? directory, bool inputGiven = true)
    {
        ArgumentNullException.ThrowIfNull(argv);

        try
        {
            var startsIn = directory is null ? null : Plain(directory, _ground.WorkingDirectory);

            return RunDecided(
                CommandAnalysis.Commands(argv, splitByItsProgram: _platform.Current is PlatformId.Windows && ExpandsItsOwnWords(argv, startsIn ?? _ground.WorkingDirectory), inputGiven),
                startsIn);
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(string.Join(' ', argv), exception);
        }
    }

    /// <summary>
    /// Whether a command an agent would run may run: the shell string is flattened whole by
    /// <see cref="CommandAnalysis"/> - its separators, substitutions, subshells, wrappers and the
    /// scripts it goes into - and each command it runs is refused where it names a root action, a
    /// credential slot, a Claude Code directory or a place the user keeps credentials in, or where
    /// a <c>Run</c> rule matches it; a string it cannot read whole is denied.
    /// </summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    public AgentPolicyAnswer Run(string shell)
    {
        ArgumentNullException.ThrowIfNull(shell);

        try
        {
            return RunDecided(CommandAnalysis.Commands(shell), startsIn: null);
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(shell, exception);
        }
    }

    /// <summary>
    /// The runtime libraries a Cygwin and an MSYS program load from their own directory, which tell such a program from
    /// every other on Windows: the runtime that expands the patterns and braces of the command line it is started with.
    /// </summary>
    private static readonly string[] ExpandingRuntimes = ["cygwin1.dll", "msys-2.0.dll"];

    /// <summary>
    /// Whether the program an argument list starts on Windows expands the patterns and braces among its words itself, as
    /// a Cygwin or an MSYS program does - Git for Windows' <c>ls</c>, <c>rm</c> and <c>find</c> among them - told by the
    /// runtime library its directory holds (<see cref="ExpandingRuntimes"/>); <c>gh</c>, <c>git</c>, <c>dotnet</c> and
    /// every other program are handed their words as they are. A program a wrapper among the words starts (<c>env</c>,
    /// <c>xargs</c>) is asked too, and a first program this cannot find is read as one that may expand them.
    /// </summary>
    /// <param name="argv">The argument list.</param>
    /// <param name="directory">Where it starts, which a program named by its path is read against.</param>
    private bool ExpandsItsOwnWords(IReadOnlyList<string> argv, string directory)
    {
        var environment = CommandAnalysis.EnvironmentOf(argv);

        if (environment >= argv.Count)
        {
            return false;
        }

        if (ProgramFile(argv[environment], directory) is not { } first)
        {
            return true;
        }

        IEnumerable<string> started = CommandAnalysis.RunsItsWords(argv[environment])
            ? argv.Skip(environment + 1).Where(word => !word.StartsWith('-')).Select(word => ProgramFile(word, directory)).OfType<string>()
            : [];

        return new[] { first }.Concat(started).Any(file => Path.GetDirectoryName(file) is { } beside
            && ExpandingRuntimes.Any(runtime => _fileSystem.FileExists(Path.Combine(beside, runtime))));
    }

    /// <summary>
    /// The file a command of the agent's starts as <paramref name="program"/>: the file its path names, read against
    /// <paramref name="directory"/>, with Windows's <c>.exe</c> where it names none - or the first the PATH its commands
    /// are started with (<see cref="AgentPolicyGround.ProgramPath"/>) holds, as a process looks one up. Null where there is
    /// none, and for a word that is no program's name at all.
    /// </summary>
    private string? ProgramFile(string program, string directory)
    {
        var windows = _platform.Current is PlatformId.Windows;

        try
        {
            if (!ProcessRunner.IsPath(program))
            {
                return ProcessRunner.ProgramOnPath([program], _programPath, windows, _fileSystem.FileExists);
            }

            var full = Path.GetFullPath(Path.Combine(directory, program));

            return new[] { full, windows && !Path.HasExtension(full) ? full + ".exe" : null }.OfType<string>().FirstOrDefault(_fileSystem.FileExists);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether git finds a program of its own for <paramref name="command"/> - <c>git-&lt;command&gt;</c>, which it runs in
    /// place of an alias of that name - on the PATH the agent's commands are started with, or where git keeps its own
    /// programs beside the git that PATH finds (its directory; Git for Windows' <c>mingw64\bin</c>,
    /// <c>mingw64\libexec\git-core</c> and <c>usr\bin</c>; elsewhere <c>lib/git-core</c> and <c>libexec/git-core</c> under
    /// its prefix), as <c>git lfs</c> is found.
    /// </summary>
    /// <param name="command">The first word git is handed after its options.</param>
    private bool GitProgram(string command)
    {
        if (command.Length == 0 || command.IndexOfAny(['/', '\\', ':']) >= 0)
        {
            return false;
        }

        var windows = _platform.Current is PlatformId.Windows;
        var program = "git-" + command;

        try
        {
            if (ProcessRunner.ProgramOnPath([program], _programPath, windows, _fileSystem.FileExists) is not null)
            {
                return true;
            }

            if (ProcessRunner.ProgramOnPath(["git"], _programPath, windows, _fileSystem.FileExists) is not { } git
                || Path.GetDirectoryName(Resolved(git)) is not { } bin)
            {
                return false;
            }

            var prefix = Path.GetDirectoryName(bin) ?? bin;
            string[] kept =
            [
                bin,
                Path.Combine(prefix, "mingw64", "bin"), Path.Combine(prefix, "mingw64", "libexec", "git-core"), Path.Combine(prefix, "usr", "bin"),
                Path.Combine(prefix, "lib", "git-core"), Path.Combine(prefix, "libexec", "git-core"),
            ];

            return kept.Any(directory => ProcessRunner.ProgramInDirectory(program, directory, windows, _fileSystem.FileExists) is not null);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>
    /// The path of <paramref name="path"/> beneath <paramref name="root"/>, with forward separators;
    /// null where it is not beneath it, and for the root itself, which no pattern of a path beneath
    /// it names.
    /// </summary>
    /// <param name="root">The directory.</param>
    /// <param name="path">The path.</param>
    /// <param name="comparison">How the two compare.</param>
    internal static string? Beneath(string root, string path, StringComparison comparison)
    {
        var whole = Whole(root);
        var candidate = Whole(path);

        if (!PathContainment.IsStrictlyInside(whole, candidate, comparison))
        {
            return null;
        }

        return candidate[whole.Length..].TrimStart('/', '\\').Replace('\\', '/');
    }

    /// <summary>
    /// <see cref="Beneath"/> as the policy reads it within a decision: each path's whole - made as the runtime makes one, on
    /// Windows each short name along it given as its long name, a read of the disk - kept for the decision, so a path asked
    /// of each rule and root is made whole once, however many ask.
    /// </summary>
    private string? BeneathOf(string root, string path, StringComparison comparison)
    {
        var whole = WholeOf(root);
        var candidate = WholeOf(path);

        if (!StrictlyInside(whole, candidate, comparison))
        {
            return null;
        }

        return candidate[whole.Length..].TrimStart('/', '\\').Replace('\\', '/');
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> lies strictly beneath <paramref name="whole"/>, as
    /// <see cref="PathContainment.IsStrictlyInside"/> decides it, asked here of paths the decision already made whole
    /// (<see cref="WholeOf"/>) so neither is read on the disk again: the containment of a path the policy asks at each
    /// place, rule and root of a decision is decided once, on the wholes it holds.
    /// </summary>
    private static bool StrictlyInside(string whole, string candidate, StringComparison comparison)
        => candidate.Length > whole.Length
            && candidate.StartsWith(whole, comparison)
            && (IsSeparator(whole[^1]) || IsSeparator(candidate[whole.Length]));

    /// <summary>
    /// Whether <paramref name="candidate"/> is <paramref name="root"/> itself or lies beneath it, as
    /// <see cref="PathContainment.IsInsideOrSame"/> decides it (<see cref="StrictlyInside"/>'s question with the same
    /// path), asked of the wholes this decision made.
    /// </summary>
    private bool InsideOrSameOf(string root, string candidate, StringComparison comparison)
        => string.Equals(WholeOf(root), WholeOf(candidate), comparison) || StrictlyInside(WholeOf(root), WholeOf(candidate), comparison);

    /// <summary><paramref name="path"/> made whole (<see cref="Whole"/>), kept for the decision being made: one disk, read once a path.</summary>
    private string WholeOf(string path)
    {
        var wholes = _wholes.Value;

        if (wholes is not null && wholes.TryGetValue(path, out var whole))
        {
            return whole;
        }

        // A path already spelled as the runtime spells one, beneath a directory this decision found nothing at, is its own
        // whole: no short name below a directory that is not on the disk expands, so the runtime's reading of it changes
        // nothing. Each directory along it is asked of the disk once a decision (KindAt), shared by every path through it.
        var made = wholes is not null && SpelledWhole(path) ? path : Whole(path);

        wholes?.Add(path, made);

        return made;
    }

    /// <summary>
    /// Whether <paramref name="path"/> is its own whole (<see cref="Whole"/>): already spelled as the runtime spells one -
    /// rooted, each separator single, no segment <c>.</c> or <c>..</c> or empty, none at its end - no short name along the
    /// part of it that is on the disk (no <c>~</c> before the first directory this decision found nothing at), and nothing
    /// at some directory along it (<see cref="KindAt"/>), so no segment the runtime would expand or fold stands in it.
    /// </summary>
    private bool SpelledWhole(string path)
    {
        if (!Path.IsPathRooted(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var root = Path.GetPathRoot(path)!.Length;

        for (var at = root; at < path.Length;)
        {
            var end = path.IndexOfAny(['/', '\\'], at);

            if (end < 0)
            {
                end = path.Length;
            }

            var segment = path[at..end];

            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }

            var kind = KindAt(path[..end]);

            // Nothing at the segment: no name it may shorten to, and none beneath it either.
            if (kind is PathKind.None)
            {
                return true;
            }

            // A short name the disk holds: only the runtime's own reading of the path says the name it expands to.
            if (segment.Contains('~'))
            {
                return false;
            }

            // Nothing below a file: no name beneath it is found to expand.
            if (kind is PathKind.File)
            {
                return true;
            }

            at = end + 1;
        }

        // Every directory along it on the disk: the runtime may still expand a short name at any of them.
        return false;
    }

    /// <summary>
    /// Each path made whole this decision, by its spelling: the short names a whole expands are the disk's, read once a
    /// path a decision. Held per decision by the decision's own asynchronous flow, for the calls of a turn are decided
    /// at once, each its own decision: as a field of the policy itself they would be one dictionary shared by decisions
    /// running together.
    /// </summary>
    private readonly AsyncLocal<Dictionary<string, string>?> _wholes = new();

    /// <summary>A read, decided: a place no action reaches never (<see cref="Unreached"/>), then the reporter's one root, then the <c>Read</c> rules on the path as asked and as resolved.</summary>
    private AgentPolicyAnswer ReadDecision(string asked, string resolved)
    {
        if (Unreached(asked, resolved) is { } unreached)
        {
            return unreached;
        }

        if (OutsideReporterRoot(asked, resolved) is { } outside)
        {
            return outside;
        }

        return Matched(AgentRuleVerb.Read, asked, resolved);
    }

    /// <summary>A search past its read: no place no action reaches (<see cref="Unreachable"/>) beneath what it searches.</summary>
    private AgentPolicyAnswer SearchedPast(string asked, string resolved)
    {
        foreach (var form in new[] { asked, resolved })
        {
            foreach (var kind in _unreachable)
            {
                if (kind.Places.FirstOrDefault(place => PathContainment.IsStrictlyInside(form, place, Refusals)) is { } held)
                {
                    return new AgentPolicyAnswer.Denied(
                        kind.Rule,
                        AgentPolicySource.Builtin,
                        $"'{form}' holds {kind.What}, '{held}': a search of it would read one, and no file tool of an agent reads one");
                }
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>
    /// A write, decided: a place no action reaches never (<see cref="Unreached"/>), then the worktree rules, protected paths
    /// and the tool's own state, then the step's <c>writes</c> - which a delete of what the step made itself passes
    /// (<see cref="Delete"/>) - the writable roots and the <c>Write</c> rules.
    /// </summary>
    /// <param name="asked">The path as asked, spelled whole.</param>
    /// <param name="resolved">Where it resolves.</param>
    /// <param name="untracked">For a delete, whether git tracks nothing at the path (<see cref="Delete"/>); null for a write.</param>
    private AgentPolicyAnswer WriteDecision(string asked, string resolved, bool? untracked)
    {
        if (Unreached(asked, resolved) is { } unreached)
        {
            return unreached;
        }

        if (_reporterRoot is not null)
        {
            return new AgentPolicyAnswer.Denied(
                "the reporter's reads",
                AgentPolicySource.Builtin,
                "this agent reads and nothing else: it is the run's reporter, offered the tools that read and no other");
        }

        if (BuiltinWriteRefusal(asked, resolved) is { } refused)
        {
            return refused;
        }

        if (OutsideStepWrites(asked, resolved, untracked) is { } outside)
        {
            return outside;
        }

        if (!_writableRoots.Any(root => InsideOrSameOf(root, resolved, _platform.PathComparison)))
        {
            return new AgentPolicyAnswer.Denied(
                "the writable roots",
                AgentPolicySource.Builtin,
                $"'{resolved}' is outside the writable roots of this agent: {string.Join(", ", _writableRoots)}");
        }

        return Matched(AgentRuleVerb.Write, asked, resolved);
    }

    /// <summary>
    /// The built-in write refusals: git's own files and the tool's own state (<see cref="KeptWriteRefusal"/>), then the
    /// tool's policy under the worktree rules and the protected paths under their own switch.
    /// </summary>
    private AgentPolicyAnswer.Denied? BuiltinWriteRefusal(string asked, string resolved)
    {
        if (KeptWriteRefusal(asked, resolved) is { } kept)
        {
            return kept;
        }

        foreach (var root in _allRoots)
        {
            foreach (var form in new[] { asked, resolved })
            {
                if (BeneathOf(root, form, Refusals) is not { } relative)
                {
                    continue;
                }

                if (_ground.WorktreeRules)
                {
                    foreach (var (pattern, policyText) in _worktreePolicyWrites)
                    {
                        if (pattern.Matches(relative, Refusals) && !SkillsException(relative, _platform.PathComparison))
                        {
                            return new AgentPolicyAnswer.Denied(
                                $"Write({policyText})",
                                AgentPolicySource.Builtin,
                                $"'{form}' is denied by Write({policyText}), a worktree rule of this tool: an agent does not edit its own policy or what the next launch loads");
                        }
                    }
                }

                if (_ground.ProtectedPaths && _protectedPaths.FirstOrDefault(pair => pair.Pattern.Matches(relative, Refusals)) is { Text: { } text })
                {
                    return new AgentPolicyAnswer.Denied(
                        $"Write({text})",
                        AgentPolicySource.Builtin,
                        $"'{form}' is denied by Write({text}), a protected path of this tool: the writes Claude Code guards, refused on every client, so a role is not stopped on one where it passes on another");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The write refusals every ground keeps, a file tool's write and a command's alike (<see cref="CommandWriteRefusal"/>):
    /// git's own files beneath every tree the agent touches - its roots and the main checkout (<see cref="AgentPolicyGround.Trees"/>)
    /// - for a step's ground under the worktree rules, and for a side-task's, which holds them off; and the tool's own state
    /// wherever the ground names it, whatever the switches say, a place of the agent's own within it excepted: a root of
    /// its own that stands inside the state, as its worktree does inside the worktrees root and its work directory inside
    /// the run's.
    /// </summary>
    private AgentPolicyAnswer.Denied? KeptWriteRefusal(string asked, string resolved)
    {
        if (_ground.WorktreeRules || _ground.SideTask)
        {
            foreach (var root in _gitTrees)
            {
                foreach (var form in new[] { asked, resolved })
                {
                    // Git's own at any depth, as Claude Code's own guard holds it: a nested repository's or a
                    // submodule's .git runs its hooks as surely as the root's. Whatever protectedPaths says, and for a
                    // step's ground under the worktree rules - which a side-task's ground holds off, keeping this one
                    // alone of them, for git's own files are written by git's verbs and this tool's.
                    if (BeneathOf(root, form, Refusals) is { } relative && relative.Split('/').Any(segment => segment.Equals(".git", Refusals)))
                    {
                        return new AgentPolicyAnswer.Denied(
                            GitsOwnRule,
                            AgentPolicySource.Builtin,
                            $"'{form}' is git's own: written by git's verbs and this tool's, never by an agent's file tool, whatever protectedPaths says");
                    }
                }
            }
        }

        // The tool's own state, wherever the ground names it, whatever the switches say: written by its verbs alone.
        // A root of the agent's that stands inside the state is its own place there - its worktree under the worktrees
        // root, its work directory under the run's - and is not refused; state that stands inside a root is, for the root
        // was given for the agent's work, not the tool's records. Asked of the path as it resolves, so no link inside a
        // root reaches the state by its other name.
        foreach (var state in _state)
        {
            if (!new[] { asked, resolved }.Any(form => PathContainment.IsInsideOrSame(state, form, Refusals)))
            {
                continue;
            }

            if (_allRoots.Any(root => PathContainment.IsInsideOrSame(root, resolved, _platform.PathComparison)
                && PathContainment.IsStrictlyInside(state, root, _platform.PathComparison)))
            {
                continue;
            }

            return new AgentPolicyAnswer.Denied(
                "the tool's own state",
                AgentPolicySource.Builtin,
                $"'{resolved}' is the tool's own state: written by its verbs alone, never by an agent's - its own place within it alone excepted");
        }

        return null;
    }

    /// <summary>
    /// The refusals every ground keeps on a write (<see cref="KeptWriteRefusal"/>), held to what a command writes
    /// (<see cref="CommandAnalysis.WriteTargets"/>): each place its output redirects name and each a file-writing program's
    /// words name, spelled against every place the command may stand in - a pattern the shell expands refused where it may
    /// reach git's own files or the tool's state. A command that writes no file this reads meets the fold, as before.
    /// </summary>
    /// <param name="targets">What the command writes, as it spells each.</param>
    /// <param name="places">Every directory a path of the command may be read against.</param>
    /// <param name="whole">The command whole, as the denial names it.</param>
    /// <param name="dotNames">Whether the command's patterns match a dot-name (<see cref="PatternsMatchDotNames"/>).</param>
    private AgentPolicyAnswer.Denied? CommandWriteRefusal(IReadOnlyList<string> targets, IReadOnlyList<string> places, string whole, bool dotNames)
    {
        foreach (var (target, place) in targets
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(target => places.Distinct(StringComparer.Ordinal).Select(place => (target, place))))
        {
            // A word whose braces spell several places writes one of them per reading of the command: the spellings that
            // decide it - the names that matter, a witness per guarded place where it may spell a pattern.
            foreach (var spelled in SpelledAgainst(target, place, _state, dotNames))
            {
                IReadOnlyList<string> readings;

                try
                {
                    readings = CommandSpelled(spelled, place, pattern: true);
                }
                catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
                {
                    // A word that is no path names no place a refusal holds.
                    continue;
                }

                foreach (var reading in readings)
                {
                    AgentPolicyAnswer.Denied? refused;

                    if (reading.IndexOfAny(['*', '?', '[']) >= 0)
                    {
                        var asked = reading;

                        refused = (_ground.WorktreeRules || _ground.SideTask) && MayMatchGitsOwn(asked, dotNames)
                            ? new AgentPolicyAnswer.Denied(GitsOwnRule, AgentPolicySource.Builtin, string.Empty)
                            : _state.Any(state => GlobReaches(asked, state, dotNames) && !OwnPlaceInState(asked, state))
                                ? new AgentPolicyAnswer.Denied("the tool's own state", AgentPolicySource.Builtin, string.Empty)
                                : null;
                    }
                    else
                    {
                        refused = KeptWriteRefusal(reading, Resolved(reading));
                    }

                    if (refused is { } denied)
                    {
                        return denied with
                        {
                            Reason = denied.Rule == GitsOwnRule
                                ? $"'{whole}' writes '{spelled}', git's own: written by git's verbs and this tool's, never by an agent's command"
                                : $"'{whole}' writes '{spelled}', the tool's own state: written by its verbs alone, never by an agent's",
                        };
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The refusal of a git pathspec's pattern read as git reads one by default - a star and a question mark matching a
    /// separator too, a directory it matches reaching all it holds - where, read from a place, it may match a place no
    /// action reaches (<see cref="Unreachable"/>), a directory between the place and one, or a path beneath one.
    /// </summary>
    private AgentPolicyAnswer.Denied? GitPatternRefusal(string pattern, IReadOnlyList<string> places)
    {
        if (pattern.IndexOfAny(['*', '?', '[']) < 0)
        {
            return null;
        }

        var expression = SegmentPattern(pattern.Replace('\\', '/'));

        foreach (var (unreachable, rule, what, _, _) in _unreachable)
        {
            foreach (var (place, guarded) in places.SelectMany(place => unreachable.Select(guarded => (place, guarded))))
            {
                if (!PathContainment.IsStrictlyInside(place, guarded, Refusals))
                {
                    continue;
                }

                var segments = Path.GetRelativePath(place, guarded).Replace('\\', '/').Split('/');
                var reaches = Enumerable.Range(1, segments.Length).Any(count => expression.IsMatch(string.Join('/', segments[..count])))
                    || expression.IsMatch(string.Join('/', segments) + "/");

                if (reaches)
                {
                    return new AgentPolicyAnswer.Denied(rule, AgentPolicySource.Builtin, $"'{pattern}' may name {what}, '{guarded}', by git's own reading of its pattern: no command of an agent touches one");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The root of the repository each place lies in, as git finds it from there: the deepest tree the ground names that
    /// holds it, else the nearest directory above it that holds a <c>.git</c>, else the place itself.
    /// </summary>
    private IReadOnlyList<string> GitRoots(IReadOnlyList<string> places)
        => [.. places.Select(place => _gitTrees.Where(tree => PathContainment.IsInsideOrSame(tree, place, Refusals)).MaxBy(tree => tree.Length)
                ?? Ancestry(place).FirstOrDefault(directory => KindAt(Path.Join(directory, ".git")) is not PathKind.None)
                ?? place)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Whether a segment of the pattern <paramref name="asked"/> may match <c>.git</c> where one may be: after a part that
    /// holds no pattern of its own, where that directory holds a <c>.git</c> as the disk stands - the shell, or a cmdlet,
    /// expands a pattern against what is there - and after one that holds a pattern, wherever.
    /// </summary>
    private bool MayMatchGitsOwn(string asked, bool dotNames)
    {
        var start = 0;

        while (start < asked.Length)
        {
            var end = asked.IndexOfAny(['/', '\\'], start);
            var segment = end < 0 ? asked[start..] : asked[start..end];

            if (segment.Length > 0 && DotNamed(segment, ".git", dotNames) && SegmentPattern(segment).IsMatch(".git"))
            {
                var parent = asked[..start];

                if (parent.IndexOfAny(['*', '?', '[']) >= 0 || KindAt(Path.Join(parent, ".git")) is not PathKind.None)
                {
                    return true;
                }
            }

            if (end < 0)
            {
                break;
            }

            start = end + 1;
        }

        return false;
    }

    /// <summary>
    /// The refusal of a command that removes or moves whole (<see cref="CommandAnalysis.RemovalTargets"/>) a directory that
    /// holds a place no command reaches or writes - git's own directory of every tree the agent touches where the worktree
    /// rules or a side-task's ground keep it, the tool's own state, a credential slot, a Claude Code directory - or is one:
    /// each target spelled against every place the command may stand in, a pattern by every path it may match, the
    /// agent's working directory among them, for removing it removes all it holds.
    /// </summary>
    /// <param name="targets">What the command removes or moves, as it spells each.</param>
    /// <param name="places">Every directory a path of the command may be read against.</param>
    /// <param name="whole">The command whole, as the denial names it.</param>
    /// <param name="dotNames">Whether the command's patterns match a dot-name.</param>
    /// <param name="braces">
    /// Whether the shell expands a target's braces: each word they spell is a target too (<c>rm -rf {.git,x}</c> removes
    /// <c>.git</c>), read by the spellings that decide it where it is plainly spelled (<see cref="SpelledAgainst"/>).
    /// </param>
    private AgentPolicyAnswer.Denied? RemovalRefusal(IReadOnlyList<string> targets, IReadOnlyList<string> places, string whole, bool dotNames, bool braces = true)
    {
        var kept = KeptFromRemoval().Select(kept => kept.Place).ToList();

        foreach (var (target, place, spelled) in targets
            .Where(target => target.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .SelectMany(target => places.Distinct(StringComparer.Ordinal).Select(place => (target, place)))
            .SelectMany(each => (braces ? SpelledAgainst(each.target, each.place, kept, dotNames) : [each.target]).Select(spelled => (each.target, each.place, spelled))))
        {
            IReadOnlyList<string> readings;

            try
            {
                readings = CommandSpelled(spelled, place, pattern: true);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                continue;
            }

            foreach (var reading in readings)
            {
                foreach (var (guarded, rule, what) in KeptFromRemoval())
                {
                    var holds = reading.IndexOfAny(['*', '?', '[']) >= 0
                        ? Ancestry(guarded).Any(directory => GlobReaches(reading, directory, dotNames, exactly: true))
                        : new[] { reading, Resolved(reading) }.Any(form => PathContainment.IsInsideOrSame(form, guarded, Refusals));

                    if (holds)
                    {
                        return new AgentPolicyAnswer.Denied(
                            rule,
                            AgentPolicySource.Builtin,
                            $"'{whole}' removes or moves '{spelled}', which holds {what}, '{guarded}': no command of an agent removes or moves one");
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The places no command of an agent removes or moves, nor removes what they hold, each with the rule that keeps it
    /// and what it is: git's own directory of every tree the agent touches where the worktree rules or a side-task's
    /// ground keep it, the tool's own state, and each place no action reaches (<see cref="Unreachable"/>).
    /// </summary>
    private IEnumerable<(string Place, string Rule, string What)> KeptFromRemoval()
    {
        if (_ground.WorktreeRules || _ground.SideTask)
        {
            foreach (var tree in _gitTrees)
            {
                yield return (Path.Combine(tree, ".git"), GitsOwnRule, "git's own");
            }
        }

        foreach (var state in _state)
        {
            yield return (state, "the tool's own state", "the tool's own state");
        }

        foreach (var kind in _unreachable)
        {
            foreach (var place in kind.Places)
            {
                yield return (place, kind.Rule, kind.What);
            }
        }
    }

    /// <summary><paramref name="place"/> and every directory above it, to its root.</summary>
    private static IEnumerable<string> Ancestry(string place)
    {
        for (string? directory = place; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            yield return directory;
        }
    }

    /// <summary>
    /// The refusal of a walk that removes what it matches (<see cref="CommandAnalysis.WalkOf"/>) where it may remove a
    /// place <see cref="KeptFromRemoval"/> keeps, a directory that holds one, or an entry one holds: each start point
    /// spelled against every place the command may stand in - a pattern by every directory above a kept place it may
    /// match - and the walk's expression asked of each entry on the way from the start to the kept place, the place
    /// itself and every entry it holds as the disk holds them (<see cref="HeldRemoved"/>), as far as the walk reaches - its
    /// depths, its prunes where it is not depth-first. A directory
    /// on the way is removed only by a remover of it whole (<c>rm -r</c>, <c>mv</c>); an entry by any remover of its kind.
    /// </summary>
    /// <param name="walk">The walk.</param>
    /// <param name="places">Every directory a path of the command may be read against.</param>
    /// <param name="whole">The command whole, as the denial names it.</param>
    /// <param name="dotNames">Whether the command's patterns match a dot-name.</param>
    private AgentPolicyAnswer.Denied? WalkRefusal(FileWalk walk, IReadOnlyList<string> places, string whole, bool dotNames)
    {
        foreach (var (point, place) in walk.Points.Distinct(StringComparer.Ordinal).SelectMany(point => places.Distinct(StringComparer.Ordinal).Select(place => (point, place))))
        {
            IReadOnlyList<string> readings;

            try
            {
                readings = CommandSpelled(point, place, pattern: true);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                continue;
            }

            foreach (var reading in readings)
            {
                foreach (var (guarded, rule, what) in KeptFromRemoval())
                {
                    // A start the shell expands is each directory above the kept place it may match.
                    IEnumerable<string> starts = reading.IndexOfAny(['*', '?', '[']) >= 0
                        ? Ancestry(guarded).Where(directory => GlobReaches(reading, directory, dotNames, exactly: true))
                        : [reading];

                    foreach (var start in starts)
                    {
                        if (WalkRemoves(walk, point, start, guarded) is { } removed)
                        {
                            return new AgentPolicyAnswer.Denied(
                                rule,
                                AgentPolicySource.Builtin,
                                $"'{whole}' may remove '{removed.Entry}' by its walk of '{point}', and '{removed.Entry}' {(removed.Holds ? "is or holds" : "lies in")} {what}, '{guarded}': no command of an agent removes or moves one");
                        }
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The entry a walk from <paramref name="start"/> may remove that is <paramref name="guarded"/> or holds it, or lies in
    /// it - with which - as the walk prints it; null where it removes none.
    /// </summary>
    private (string Entry, bool Holds)? WalkRemoves(FileWalk walk, string point, string start, string guarded)
    {
        if (PathContainment.IsInsideOrSame(start, guarded, Refusals))
        {
            var below = Path.GetRelativePath(start, guarded);
            IReadOnlyList<string> segments = below == "." ? [] : below.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
            var kind = KindAt(guarded);

            if (kind is PathKind.None)
            {
                return null;
            }

            // Each entry on the way, the start first and the kept place last: a directory that holds it, then itself.
            for (var depth = 0; depth <= segments.Count; depth++)
            {
                if (depth > walk.MaxDepth)
                {
                    return null;
                }

                var reached = depth == segments.Count;
                var entry = Entry(point, start, segments.Take(depth).ToList(), reached ? kind : PathKind.Directory, holds: !reached);

                if (depth < walk.MinDepth)
                {
                    continue;
                }

                var asked = Asked(walk.Expression, entry);

                if (asked.Removes is not Truth.False)
                {
                    return (entry.Printed, true);
                }

                if (!walk.DepthFirst && asked.Prunes is Truth.True)
                {
                    return null;
                }
            }

            return kind is PathKind.Directory && HeldRemoved(walk, point, start, segments) is { } held ? (held, false) : null;
        }

        if (PathContainment.IsInsideOrSame(guarded, start, Refusals))
        {
            // Every entry the walk reaches lies in the kept place: the start, and all it holds.
            var kind = KindAt(start);

            if (kind is PathKind.None)
            {
                return null;
            }

            var entry = Entry(point, start, [], kind, holds: false);

            if (walk.MinDepth == 0)
            {
                var asked = Asked(walk.Expression, entry);

                if (asked.Removes is not Truth.False)
                {
                    return (entry.Printed, false);
                }

                if (!walk.DepthFirst && asked.Prunes is Truth.True)
                {
                    return null;
                }
            }

            return kind is PathKind.Directory && HeldRemoved(walk, point, start, []) is { } held ? (held, false) : null;
        }

        return null;
    }

    /// <summary>
    /// The first entry a walk may remove of those the directory <paramref name="above"/> names below
    /// <paramref name="start"/> holds, read from the disk a level at a time as the walk reaches each: every entry, each
    /// asked once, however many there are - each directory gone into once, known by where it finally lies, so a mount or a
    /// link the disk does not show as one leads into no directory twice. Where a directory cannot be read, an entry whose
    /// name and kind this does not know stands for all it holds; null where it removes none.
    /// </summary>
    private string? HeldRemoved(FileWalk walk, string point, string start, IReadOnlyList<string> above)
    {
        var waiting = new Queue<IReadOnlyList<string>>([above]);
        var entered = new HashSet<string>(StringComparer.FromComparison(_platform.PathComparison));

        while (waiting.Count > 0)
        {
            var segments = waiting.Dequeue();
            var depth = segments.Count + 1;

            if (depth > walk.MaxDepth)
            {
                continue;
            }

            var directory = segments.Count == 0 ? start : Path.Join([start, .. segments]);
            List<(string Name, PathKind Kind)> held;

            try
            {
                if (!entered.Add(_fileSystem.FinalPathOfDirectory(directory)))
                {
                    continue;
                }

                held = [.. _fileSystem.EnumerateDirectories(directory).Select(path => (Path.GetFileName(path), _fileSystem.IsLink(path) ? PathKind.Link : PathKind.Directory)),
                    .. _fileSystem.EnumerateFiles(directory, recursive: false).Select(path => (Path.GetFileName(path), PathKind.File))];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // What a directory that cannot be read holds is told by an entry whose name and kind this does not know,
                // standing for each one the directory the walk was asked of holds.
                var unread = Unread(point, start, above);

                return Asked(walk.Expression, unread).Removes is not Truth.False ? unread.Printed : null;
            }

            foreach (var (name, kind) in held)
            {
                var entry = Entry(point, start, [.. segments, name], kind, holds: false);

                if (depth >= walk.MinDepth)
                {
                    var asked = Asked(walk.Expression, entry);

                    if (asked.Removes is not Truth.False)
                    {
                        return entry.Printed;
                    }

                    if (!walk.DepthFirst && asked.Prunes is Truth.True)
                    {
                        continue;
                    }
                }

                if (kind is PathKind.Directory)
                {
                    waiting.Enqueue([.. segments, name]);
                }
            }
        }

        return null;
    }

    /// <summary>What is at <paramref name="path"/>, as the walk would find it; nothing where that cannot be asked.</summary>
    /// <remarks>What the decision being made found at each path, it keeps (<see cref="_kinds"/>): one decision reads one disk, so a path asked again is given the kind it was found to have.</remarks>
    private PathKind KindAt(string path)
    {
        var kinds = _kinds.Value;

        if (kinds is not null && kinds.TryGetValue(path, out var kind))
        {
            return kind;
        }

        var found = KindOnDisk(path);
        kinds?.Add(path, found);

        return found;
    }

    /// <summary><see cref="KindAt"/> asked of the disk itself.</summary>
    private PathKind KindOnDisk(string path)
    {
        try
        {
            return _fileSystem.KindOf(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return PathKind.None;
        }
    }

    /// <summary>
    /// What the decision being made found at each path it asked, by its spelling: one decision reads one disk, so its
    /// paths' kinds are its own to keep. Null outside a decision, where every ask reads the disk. Held per decision by
    /// the decision's own asynchronous flow, as <see cref="_wholes"/> is.
    /// </summary>
    private readonly AsyncLocal<Dictionary<string, PathKind>?> _kinds = new();

    /// <summary>Three answers a walk's test may give an entry: it does not hold, it may, it does.</summary>
    private enum Truth
    {
        False,
        Maybe,
        True,
    }

    /// <summary>
    /// One entry a walk reaches, as its tests read it: its name, its path as find prints it, below the start and whole,
    /// its kind and whether it is empty - each unknown where the entry stands for those past what was read
    /// (<paramref name="Unread"/>), whose paths are then the directory that holds them, a separator after it - and whether
    /// it holds a kept place, which no remover but of it whole removes.
    /// </summary>
    private sealed record WalkEntry(string? Name, string Printed, string Relative, string Absolute, PathKind? Kind, Lazy<Truth> Empty, bool Holds, bool Unread);

    /// <summary>The entry the segments below <paramref name="start"/> name, of the kind given.</summary>
    private WalkEntry Entry(string point, string start, IReadOnlyList<string> segments, PathKind kind, bool holds)
    {
        var absolute = segments.Count == 0 ? start : Path.Join([start, .. segments]);
        var name = segments.Count == 0 ? PointName(point) : segments[^1];
        var empty = new Lazy<Truth>(() => kind switch
        {
            PathKind.Directory => Listed(absolute) is { } any ? (any ? Truth.False : Truth.True) : Truth.Maybe,
            PathKind.File => _fileSystem.StandingOf(absolute) is { } standing ? (standing.Length == 0 ? Truth.True : Truth.False) : Truth.Maybe,
            _ => Truth.False,
        });

        return new WalkEntry(name, Printed(point, segments), string.Join('/', segments), absolute, kind, empty, holds, Unread: false);
    }

    /// <summary>An entry that stands for each one the directory the segments name holds past what was read: its name, kind and emptiness unknown.</summary>
    private static WalkEntry Unread(string point, string start, IReadOnlyList<string> segments)
        => new(
            null,
            Printed(point, segments).TrimEnd('/') + "/",
            segments.Count == 0 ? string.Empty : string.Join('/', segments) + "/",
            (segments.Count == 0 ? start : Path.Join([start, .. segments])) + Path.DirectorySeparatorChar,
            null,
            new Lazy<Truth>(() => Truth.Maybe),
            Holds: false,
            Unread: true);

    /// <summary>Whether the directory holds anything; null where it cannot be read.</summary>
    private bool? Listed(string directory)
    {
        try
        {
            return _fileSystem.EnumerateDirectories(directory).Any() || _fileSystem.EnumerateFiles(directory, recursive: false).Any();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The name find gives a start point: its last segment as spelled, its trailing separators gone - the point itself where it has none.</summary>
    private static string PointName(string point)
    {
        var trimmed = point.TrimEnd('/', '\\');

        return trimmed.Length == 0 ? point : trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
    }

    /// <summary>An entry's path as find prints it: the start point as spelled, then each segment below it, joined by '/'.</summary>
    private static string Printed(string point, IReadOnlyList<string> segments)
        => segments.Count == 0 ? point : (point.EndsWith('/') ? point : point + "/") + string.Join('/', segments);

    /// <summary>
    /// What <paramref name="expression"/> answers of <paramref name="entry"/>, as find evaluates an expression - the right of
    /// an <c>-a</c> asked only where the left may hold, of an <c>-o</c> only where it may not - with whether an action it
    /// may run removes the entry, and whether one prunes it. Asked on a stack of its own, each operator's left answered
    /// before its right is asked, so an expression of any depth is answered whole: never a recursion, whose depth the
    /// expression's own nesting would set.
    /// </summary>
    private (Truth Value, Truth Removes, Truth Prunes) Asked(WalkNode expression, WalkEntry entry)
    {
        // Each operator waiting on its operands: its left's answer once it is given (Left), else none yet.
        var waiting = new Stack<(WalkNode Node, (Truth Value, Truth Removes, Truth Prunes)? Left)>();
        var node = expression;

        while (true)
        {
            // Down the left of each operator to a test or an action, each operator met waiting on its operands.
            (Truth Value, Truth Removes, Truth Prunes) answer;

            switch (node)
            {
                case WalkNode.Test test:
                    answer = (Tested(test.Asked, entry), Truth.False, Truth.False);
                    break;
                case WalkNode.Act act:
                    answer = (act.Holds ? Truth.True : Truth.Maybe, RemovedBy(act.Action, entry), act.Action is WalkAction.Prune ? Truth.True : Truth.False);
                    break;
                case WalkNode.Not not:
                    waiting.Push((not, null));
                    node = not.Operand;
                    continue;
                case WalkNode.And or WalkNode.Or or WalkNode.Both:
                    waiting.Push((node, null));
                    node = node switch { WalkNode.And all => all.Left, WalkNode.Or either => either.Left, _ => ((WalkNode.Both)node).Left };
                    continue;
                default:
                    throw new NotSupportedException($"a walk's expression holds a {node.GetType().Name}, which this does not read");
            }

            // Up through every operator the answer completes; an operator whose right is still to be asked sends the walk there.
            WalkNode? right = null;

            while (right is null && waiting.TryPop(out var top))
            {
                switch (top.Node)
                {
                    case WalkNode.Not:
                        answer = (Negated(answer.Value), answer.Removes, answer.Prunes);
                        break;
                    case WalkNode.And all when top.Left is null:
                        // The right of an '-a' is asked only where its left may hold.
                        if (answer.Value is not Truth.False)
                        {
                            waiting.Push((all, answer));
                            right = all.Right;
                        }

                        break;
                    case WalkNode.And when top.Left is { } left:
                        answer = (Both(left.Value, answer.Value), Either(left.Removes, Both(left.Value, answer.Removes)), Either(left.Prunes, Both(left.Value, answer.Prunes)));
                        break;
                    case WalkNode.Or either when top.Left is null:
                        // The right of an '-o' is asked only where its left may not hold.
                        if (answer.Value is not Truth.True)
                        {
                            waiting.Push((either, answer));
                            right = either.Right;
                        }

                        break;
                    case WalkNode.Or when top.Left is { } left:
                        var otherwise = Negated(left.Value);
                        answer = (Either(left.Value, answer.Value), Either(left.Removes, Both(otherwise, answer.Removes)), Either(left.Prunes, Both(otherwise, answer.Prunes)));
                        break;
                    case WalkNode.Both both when top.Left is null:
                        waiting.Push((both, answer));
                        right = both.Right;
                        break;
                    case WalkNode.Both when top.Left is { } left:
                        answer = (answer.Value, Either(left.Removes, answer.Removes), Either(left.Prunes, answer.Prunes));
                        break;
                }
            }

            if (right is null)
            {
                return answer;
            }

            node = right;
        }

        static Truth Negated(Truth truth) => truth switch { Truth.True => Truth.False, Truth.False => Truth.True, _ => Truth.Maybe };

        static Truth Both(Truth left, Truth right) => left is Truth.False || right is Truth.False ? Truth.False : left is Truth.True && right is Truth.True ? Truth.True : Truth.Maybe;

        static Truth Either(Truth left, Truth right) => left is Truth.True || right is Truth.True ? Truth.True : left is Truth.False && right is Truth.False ? Truth.False : Truth.Maybe;
    }

    /// <summary>Whether <paramref name="action"/> removes <paramref name="entry"/>: whole, any entry; one of a kind, an entry of that kind that holds no kept place.</summary>
    private static Truth RemovedBy(WalkAction action, WalkEntry entry)
    {
        if (action is WalkAction.RemovesWhole)
        {
            return Truth.True;
        }

        if (action is WalkAction.None or WalkAction.Prune || entry.Holds)
        {
            return Truth.False;
        }

        return (action, entry.Kind) switch
        {
            (_, null) => Truth.Maybe,
            (WalkAction.RemovesFiles or WalkAction.RemovesFilesAndEmpty, PathKind.File or PathKind.Link) => Truth.True,
            (WalkAction.RemovesEmpty or WalkAction.RemovesFilesAndEmpty, PathKind.Directory) => entry.Empty.Value,
            _ => Truth.False,
        };
    }

    /// <summary>What <paramref name="test"/> answers of <paramref name="entry"/>; for one that stands for entries this did not read, what it answers of every one of them.</summary>
    private Truth Tested(WalkTest test, WalkEntry entry)
    {
        switch (test)
        {
            case WalkTest.Named named:
                var text = named.Of switch
                {
                    WalkName.Name => entry.Name,
                    WalkName.Printed => entry.Printed,
                    WalkName.Relative => entry.Relative,
                    _ => entry.Absolute,
                };

                return Matched(named, text, entry.Unread);
            case WalkTest.Kind kind:
                return entry.Kind switch
                {
                    null => Truth.Maybe,
                    var known => kind.Letters.Contains(known switch { PathKind.Directory => 'd', PathKind.File => 'f', _ => 'l' }, StringComparison.Ordinal) ? Truth.True : Truth.False,
                };
            case WalkTest.Empty:
                return entry.Kind is null ? Truth.Maybe : entry.Empty.Value;
            case WalkTest.Constant constant:
                return constant.Value ? Truth.True : Truth.False;
            default:
                return Truth.Maybe;
        }
    }

    /// <summary>
    /// Whether the pattern of the named test matches its text: a shell pattern whole, a regular
    /// expression found in it. Where the text stands for entries this did not read - a name unknown, or a directory's path
    /// with a separator after it that any entry it holds continues - it matches where it matches each such text, it
    /// does not where no text that begins so could match, and may otherwise.
    /// </summary>
    /// <summary>The matchers of a walk's named tests, each compiled once: a test asked of every entry of a walk asks what it compiled the first time.</summary>
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<WalkTest.Named, System.Text.RegularExpressions.Regex> WalkMatchers = new();
    private static Truth Matched(WalkTest.Named named, string? text, bool unread)
    {
        System.Text.RegularExpressions.Regex? pattern;

        // A regular expression the linear matcher does not read - no expression at all, or one of a construct it lacks -
        // may match: what it would remove is not told by this. Compiled once for the test, asked of every entry.
        try
        {
            if (!named.Regular)
            {
                pattern = WalkMatchers.GetValue(named, test => Segmented(test.Pattern, test.IgnoreCase));
            }
            else if (!LinearRegex.TryCompile(named.Pattern, named.IgnoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None, out pattern, out _))
            {
                return Truth.Maybe;
            }
        }
        catch (ArgumentException)
        {
            return Truth.Maybe;
        }

        if (!unread)
        {
            return pattern.IsMatch(text ?? string.Empty) ? Truth.True : Truth.False;
        }

        var start = text ?? string.Empty;

        if (new[] { start + "", start + ".", start + "/" }.All(pattern.IsMatch))
        {
            return Truth.True;
        }

        // A shell pattern's text before its first wildcard begins every text it matches.
        var fixedStart = named.Regular ? null : named.Pattern[..(named.Pattern.IndexOfAny(['*', '?', '[']) is var wild and >= 0 ? wild : named.Pattern.Length)];
        var comparison = named.IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        return fixedStart is not null && text is not null && !fixedStart.StartsWith(start, comparison) && !start.StartsWith(fixedStart, comparison)
            ? Truth.False
            : Truth.Maybe;
    }

    /// <summary>
    /// Whether the path, as asked or as resolved, falls under the worktree rules' skills directory
    /// through a step whose <c>writes</c> names one skill's own directory by name
    /// (<c>.harness-config/skills/&lt;name&gt;/...</c>): the one thing that opens it, for that skill
    /// alone. A broader pattern that merely covers it - <c>**</c>, <c>.harness-config/**</c>,
    /// <c>.harness-config/skills/**</c> - does not, for who may write a skill is something a person
    /// reads in the definition, not something an output's pattern decides.
    /// </summary>
    private bool SkillsException(string relative, StringComparison comparison)
        => _writes.Any(write
            => write.Text.Replace('\\', '/').StartsWith(".harness-config/skills/", StringComparison.Ordinal)
                && write.Text.Replace('\\', '/').Split('/') is { Length: > 2 } segments
                && segments[2].Length > 0
                && !segments[2].Contains('*', StringComparison.Ordinal)
                && !segments[2].Contains('?', StringComparison.Ordinal)
                && write.Pattern.Matches(relative, comparison));

    /// <summary>
    /// Whether the step's <c>writes</c> names <paramref name="relative"/> - a path relative to its worktree - as the platform
    /// compares names; false where the step names no writes. What a step's context pack seeds the declarations of.
    /// </summary>
    /// <param name="relative">A path relative to the step's worktree, with forward separators.</param>
    public bool StepWrites(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        return Covers(relative);
    }

    /// <summary>Whether one of the step's <c>writes</c> patterns names <paramref name="relative"/>, as the platform compares names.</summary>
    private bool Covers(string relative) => _writes.Any(write => write.Pattern.Matches(relative, _platform.PathComparison));

    /// <summary>
    /// The step's <c>writes</c>, the one source that narrows: where a step has them, a write under
    /// its worktree that matches none of their patterns is denied - and so is a delete, but of what
    /// the step made itself (<see cref="Delete"/>). What a command the agent ran writes is not seen
    /// here, and is measured where it cannot be missed: at the fold; what it made, the step may take
    /// back.
    /// </summary>
    /// <param name="asked">The path as asked, spelled whole.</param>
    /// <param name="resolved">Where it resolves.</param>
    /// <param name="untracked">For a delete, whether git tracks nothing at the path (<see cref="Delete"/>); null for a write.</param>
    private AgentPolicyAnswer.Denied? OutsideStepWrites(string asked, string resolved, bool? untracked)
    {
        if (_writes.Count == 0)
        {
            return null;
        }

        foreach (var form in new[] { asked, resolved })
        {
            // Beneath the worktree by any spelling its file system folds to it: the narrowing holds
            // for each, and a pattern allows only the spelling it names as the platform compares.
            if (BeneathOf(_workingRoot, form, Refusals) is not { } relative || Covers(relative))
            {
                continue;
            }

            var said = $"'{resolved}' is outside the paths this step may write: {string.Join(", ", _writes.Select(write => write.Text))}";

            // A delete of what the step made itself passes, by every spelling of it: a path git does not track that the
            // record of the step's start holds by none of them - which it holds in any case, so no spelling a file
            // system folds to a path that was there makes it one the step made.
            if (untracked is { } taken && _ground.StepStart is { } start)
            {
                if (taken && !start.Holds(relative))
                {
                    continue;
                }

                said += $"; {OwnMakingSaid} - and {(taken ? "this one, or a path in it, was there untracked when the step's run began" : "git tracks this one, or ignores it")}";
            }

            return new AgentPolicyAnswer.Denied("the step's writes", AgentPolicySource.Builtin, said);
        }

        return null;
    }

    /// <summary>Whether the path stands inside the reporter's one root, where this agent is the run's reporter.</summary>
    private AgentPolicyAnswer.Denied? OutsideReporterRoot(string asked, string resolved)
    {
        if (_reporterRoot is not { } root || _resolvedReporterRoot is not { } resolvedRoot)
        {
            return null;
        }

        var comparison = _platform.PathComparison;

        // Both of its forms must stand inside, each in the root as it is spelled: the path as asked
        // inside the root by either of its names - as given, a link it is reached through kept, or
        // where that resolves - and where it resolves inside where the root resolves, for a link
        // inside the root that resolves outside it is a read of what it points at, which is outside
        // what the reporter may read.
        if ((PathContainment.IsInsideOrSame(root, asked, comparison) || PathContainment.IsInsideOrSame(resolvedRoot, asked, comparison))
            && PathContainment.IsInsideOrSame(resolvedRoot, resolved, comparison))
        {
            return null;
        }

        return new AgentPolicyAnswer.Denied(
            "the reporter's reads",
            AgentPolicySource.Builtin,
            $"'{asked}' is outside what this agent may read: {root.Replace('\\', '/')}/**");
    }

    /// <summary>Each flattened command, refused by the first thing it runs that may not run.</summary>
    /// <param name="commands">The commands, flattened.</param>
    /// <param name="startsIn">Where the first starts, spelled whole; null for the working directory.</param>
    private AgentPolicyAnswer RunDecided(IReadOnlyList<AnalyzedCommand> commands, string? startsIn)
    {
        if (_reporterRoot is not null)
        {
            return new AgentPolicyAnswer.Denied(
                "the reporter's reads",
                AgentPolicySource.Builtin,
                "this agent reads and nothing else: it is the run's reporter, offered the tools that read and no other");
        }

        // Every directory a path may be read against: the working directory, and each a cd of the
        // string moved to - a path is tried against all of them, for a subshell's cd ends with it.
        return RunDecided(commands, [PlaceForm.Of(startsIn ?? _ground.WorkingDirectory)], running: new HashSet<string>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Each flattened command, refused by the first thing it runs that may not run, read against the places
    /// <paramref name="forms"/> hold and every place a move of theirs reaches - at one place of each way they stand to the
    /// decisions (<see cref="Representatives"/>), however many they hold.
    /// </summary>
    /// <param name="commands">The commands, flattened.</param>
    /// <param name="forms">Every directory a path of the first may be read against, as forms of places, added to as the commands move.</param>
    /// <param name="running">
    /// The texts of the script files this string's text stands in, each one its string wrote and then ran
    /// (<see cref="WrittenScripts"/>), outermost first: one met again runs itself, and is decided by the reading in progress.
    /// </param>
    /// <param name="probes">The cores of this decision of the string.</param>
    private AgentPolicyAnswer RunDecided(IReadOnlyList<AnalyzedCommand> commands, List<PlaceForm> forms, IReadOnlySet<string> running, Probes? probes = null)
    {
        // Everything of a command compares as this platform compares names, but for the root
        // actions and the places no agent reaches, which fold case everywhere.
        var comparison = _platform.PathComparison;

        // Whether a setting of the string's makes the shell's patterns match dot-names, read once of every command of it.
        var dotSettings = PatternsMatchDotNames(commands);

        // Whether a word of the string names PowerShell's env: drive, whose items a cmdlet given no path may be piped.
        var environmentDrive = commands.Any(command => command.Wrappers.Append(command.Words).Any(words => words.Any(CommandAnalysis.NamesEnvironmentDrive)));

        // What the string's own assignments set, and what its commands write, as each command after them reads it.
        var state = new StringState(OnWindows, Meet, MayEndIn);

        // The cores of this decision of the string: a script it runs is decided as a decision of its own, with cores of
        // its own, and what lies outside this decision's readings is left as it was.
        var outer = _probes.Value;
        _probes.Value = probes ?? new Probes();

        // What each path this decision asks resolves to, kept for the decision: one disk, read once a path.
        var outerResolutions = _resolutions.Value;
        _resolutions.Value = [];

        // What each path this decision asks is, kept for the decision as well.
        var outerKinds = _kinds.Value;
        _kinds.Value = [];

        // Each path this decision makes whole, kept for the decision as well.
        var outerWholes = _wholes.Value;
        _wholes.Value = [];

        try
        {

        // Where each command may stand, as the moves before it at its level left it.
        var standing = new Standing(forms);

        // The forms of places reached so far, each once.
        var placesReached = new HashSet<string>(forms.Select(form => form.Key), StringComparer.Ordinal);

        // One place of each way the places reached stand to the decisions, read on where a move reached more.
        var placeWays = new HashSet<string>(StringComparer.Ordinal);
        var places = new List<string>();
        Represent(forms, 0, placeWays, places);
        var placesRead = forms.Count;

        // Every place a path a command names may be, read from each place the command may stand in, as forms of places;
        // null where the path names one this cannot tell. Its short names kept: what a file written and one read share is
        // asked of the forms (Meet, MayEndIn). A word held as a core names the places its choice may be
        // (<see cref="FormsOf"/>), never the core's own text.
        IReadOnlyList<PlaceForm>? Wholes(string path)
        {
            if (Templated(path) is { } pattern)
            {
                return FormsOf(pattern, forms);
            }

            try
            {
                return Reached(forms, path, pattern: false);
            }
            catch (Exception exception) when (exception is NotSupportedException or ArgumentException)
            {
                return null;
            }
        }

        foreach (var analyzed in commands)
        {
            var command = analyzed.Words;
            var environment = CommandAnalysis.EnvironmentOf(command);
            var powerShell = CommandAnalysis.ReadBy(analyzed) is CommandAnalysis.Reader.PowerShell;

            // A pattern of this command's matches dot-names where a setting of the string's makes it, or where the shell
            // that expands it does: PowerShell's, cmd.exe's, or a -Force of the command's own.
            var dotNames = dotSettings || MatchesDotNames(analyzed);

            // The command as it is spelled, and with the variables the string set before it given each value they may hold
            // together - every value kept, one value of a variable wherever it stands.
            var readings = Readings(analyzed, state, forms, _probes.Value);

            // Each word a reading spells is decided once, however many readings spell it alike: a word no variable reaches
            // is the same in every reading.
            var wordsDecided = new HashSet<(string Word, string? Quoted, bool Braces, bool Holds, bool Pattern)>();

            foreach (var reading in readings)
            {
                var exempt = HoldsExempt(reading, environment);

                // Each word, and the file each redirect names or the text it gives: a redirect is decided by its file, and a
                // here-document's or here-string's text is none the shell expands braces in. A pattern is read where the
                // shell expands it: a quoted star is a star - but where PowerShell, whose cmdlets expand their own, reads it.
                foreach (var (word, quoted, braces, holds, pattern) in reading.Words.Select((word, at) => (word, (string?)reading.QuotedWord(at), true, !exempt.Contains(at), powerShell || CommandAnalysis.HasOpenPattern(word, reading.QuotedWord(at))))
                    .Concat(reading.Redirects.Select(redirect => (redirect.Target, (string?)null, redirect.Operator != "<<<", true, true))))
                {
                    if (!wordsDecided.Add((word, quoted, braces, holds, pattern)))
                    {
                        continue;
                    }

                    // The sentence says the action it matched, in the one spelling the list holds, so a
                    // word named in either case is refused in the same words.
                    if (RootActionOf(word, braces) is { } action)
                    {
                        return new AgentPolicyAnswer.Denied(
                            "the root actions",
                            AgentPolicySource.Builtin,
                            $"'{action}' is a root action: no agent of this tool acts as root");
                    }

                    if (Named(word, quoted, places, braces, dotNames, holds, pattern) is { } named)
                    {
                        return named;
                    }
                }

                // A pathspec of a git command that reaches what git does not track is git's own pattern, quoted or not, its
                // wildcards matching dot-names, read from the repository's root where its magic says so: text git reads,
                // whose tilde no shell expands. A pathspec held as a core is spelled by the spellings that decide it.
                foreach (var pathspec in CommandAnalysis.UntrackedPathspecs(reading.Words, environment))
                {
                    var from = pathspec.Top ? GitRoots(places) : places;

                    if (Named(pathspec.Path, new string(WordQuoting.Literal, pathspec.Path.Length), from, braces: false, dotNames: true, holds: true, pattern: !pathspec.Literal) is { } named)
                    {
                        return named;
                    }

                    if (pathspec.Literal || pathspec.Glob)
                    {
                        continue;
                    }

                    foreach (var spelled in Templated(pathspec.Path) is { } pattern
                        ? Decisive(pattern, places.FirstOrDefault() ?? _ground.WorkingDirectory, [], dotNames: true).Select(spelling => spelling.Word)
                        : [pathspec.Path])
                    {
                        if (GitPatternRefusal(spelled, from) is { } reached)
                        {
                            return reached;
                        }
                    }
                }
            }

            // A move goes from where the command may stand; one back to a place the string left goes to any it reached.
            var frontier = standing.Enter(analyzed);
            var moves = readings.Select(reading => MovedTo(reading.Words, frontier, state)).ToList();
            var program = environment < command.Count ? CommandAnalysis.Name(command[environment]) : string.Empty;
            List<PlaceForm> moved = Returners.Contains(program, StringComparer.OrdinalIgnoreCase) || moves.Any(move => move is { Back: true })
                ? [.. forms, .. moves.SelectMany(move => move?.To ?? [])]
                : [.. moves.SelectMany(move => move?.To ?? [])];

            // Every place reached is kept, each once, however many the moves reach: one more place is one more each path is
            // read against, never a reason to stop reading - and a move after a ';' one step more of a form, which may be
            // passed, never twice the places.
            forms.AddRange(moved.Where(form => placesReached.Add(form.Key)));

            Represent(forms, placesRead, placeWays, places);
            placesRead = forms.Count;

            standing.Left(frontier, moves.Any(move => move is not null) || Returners.Contains(program, StringComparer.OrdinalIgnoreCase) ? PlaceForm.Union(moved) : frontier);
            state.Moved |= Movers.Contains(program, StringComparer.OrdinalIgnoreCase);

            // Where the shell stands may have changed: what PWD holds is read anew by the commands after this one.
            if (moves.Any(move => move is not null) || Returners.Contains(program, StringComparer.OrdinalIgnoreCase))
            {
                state.Stood();
            }

            // wsl starting a root user, and schtasks running a task as another account: neither is
            // a word the list above holds, and both are as root.
            if (NamesRootUser(command, comparison) is { } asRoot)
            {
                return new AgentPolicyAnswer.Denied(
                    "the root actions",
                    AgentPolicySource.Builtin,
                    $"'{asRoot}' would run as the root user: no agent of this tool acts as root");
            }

            // The mark run-side-task refuses itself under goes with every program an agent starts: a command that sets it or
            // takes it away, by any form this reads, is refused.
            if (MarkerTouched(analyzed, readings, powerShell, environmentDrive) is { } marker)
            {
                return marker;
            }

            // A script file the string wrote and now runs holds what it wrote: read where its text is known, refused
            // where not. Every one the command's readings run is decided - a later reading's script is never left
            // undecided by an earlier one's.
            var ran = WrittenScripts(readings, environment, state, Wholes);

            if (ran.Any(each => each.Text is null))
            {
                var unread = ran.First(each => each.Text is null);

                // The file is named by a spelling that decides it, never a core's own sentinel text.
                return Undecided(string.Join(' ', analyzed.Whole), new NotSupportedException(
                    $"'{SpelledThatDecide(unread.File, places)}' is a script file this command wrote before it runs it, whose text this does not read"));
            }

            foreach (var text in ran.Select(each => each.Text!).Where(text => !running.Contains(text)).Distinct(StringComparer.Ordinal))
            {
                // However deep scripts that write and run scripts nest, each is decided whole, a level deeper through
                // DeepCalls; a script that runs one of the texts in progress runs itself, whose every command is being decided.
                if (DeepCalls.Run(() => RunDecided(CommandAnalysis.Commands(text), [.. forms], new HashSet<string>(running, StringComparer.Ordinal) { text })).Refusal() is { } inScript)
                {
                    return inScript;
                }
            }

            // Every word is tried as the program of the words after it, the command's environment
            // before them: a command another program runs - find -exec, ssh, a wrapper this does
            // not strip - meets the rules on its own program; but a program that runs none of its
            // words, whose words are its data. Assignments alone run no program, yet set git's
            // configuration for what runs after them (PowerShell's $env:NAME = value): they meet
            // the rules too. The words are a view, never a copy: a program that runs its words is
            // read at each of them without a list as long as the command made for each.
            var whole = string.Join(' ', analyzed.Whole);
            var starts = environment == command.Count || CommandAnalysis.RunsNoWords(command[environment]) ? environment + 1 : command.Count;

            for (var start = environment; start < starts; start++)
            {
                foreach (var reading in readings)
                {
                    // What the program there writes - its output redirects, and the files a writer's words name - meets the
                    // refusals every ground keeps on a write, as a file tool's write does: a side-task has no fold after it.
                    // A brace word is kept whole and decided by its pattern.
                    if (CommandWriteRefusal(CommandAnalysis.WriteTargets(reading, start, braces: false), places, whole, dotNames) is { } written)
                    {
                        return written;
                    }

                    // What it removes or moves whole holds no place a refusal keeps: git's own, the tool's state, a slot - a git
                    // clean's pathspecs read as git reads them, its wildcards matching dot-names.
                    var removals = CommandAnalysis.RemovalTargets(reading, start, powerShell);
                    var pathspecs = removals.Count > 0 ? CommandAnalysis.UntrackedPathspecs(reading.Words, start) : [];

                    if (pathspecs.Count == 0 && RemovalRefusal(removals, places, whole, dotNames, braces: !powerShell) is { } removed)
                    {
                        return removed;
                    }

                    foreach (var pathspec in pathspecs)
                    {
                        if (RemovalRefusal([pathspec.Path], pathspec.Top ? GitRoots(places) : places, whole, dotNames: true, braces: false) is { } cleaned)
                        {
                            return cleaned;
                        }
                    }

                    // What a walk of it removes of what it matches is none of those places, nor holds nor lies in one. A
                    // point held as a core is spelled by the spellings that decide it, each a walk of its own.
                    if (CommandAnalysis.WalkOf(reading, start) is { } walk)
                    {
                        foreach (var spelled in WalksSpelled(walk, places))
                        {
                            if (WalkRefusal(spelled, places, whole, dotNames) is { } walked)
                            {
                                return walked;
                            }
                        }
                    }
                }

                IReadOnlyList<string> words = new WordsOf(command, environment, start);

                if (Decided(analyzed, start, words, whole, comparison).Refusal() is { } denied)
                {
                    return denied;
                }
            }

            state.Record(analyzed, readings, environment, forms, Wholes);
        }

        // A side-task's command holds the main tree's lock for its duration, which its end ends: one that leaves a process
        // running after it - in the background, or by a program that keeps what it starts apart - would write the tree
        // outside the lock, on every client. Asked of every command a string runs - one a shell it runs reads, and one of a
        // script it wrote and then runs - once what each runs is allowed, so a refusal of that is said first; named as it was
        // written, its outermost wrapper with it (nohup, sh -c).
        return _ground.SideTask && commands.FirstOrDefault(CommandAnalysis.LeavesAProcessBehind) is { } left
            ? MainTreeWriteLock.LeftBehind(string.Join(' ', left.Wrappers.Count > 0 ? left.Wrappers[0] : left.Whole))
            : AgentPolicyAnswer.Allow;
        }
        finally
        {
            _probes.Value = outer;
            _resolutions.Value = outerResolutions;
            _kinds.Value = outerKinds;
            _wholes.Value = outerWholes;
        }
    }

    /// <summary>
    /// Where each command of a string may stand, level by level (<see cref="AnalyzedCommand.Level"/>): at each level,
    /// where the and-or list read so far leaves the commands after it once it held and once it did not - a command joined
    /// by <c>&amp;&amp;</c> standing where the one before held, by <c>||</c> where it did not, after any other separator
    /// where either; a level entered standing where its parent's next command would, and left as the parent stood, so a
    /// subshell's move ends with it. A move holds at the places it goes to and fails where it stood. The places are forms
    /// (<see cref="PlaceForm"/>), joined as one where they can be: where a move after a <c>;</c> held and where it failed
    /// are one form whose last step may be passed.
    /// </summary>
    private sealed class Standing(IReadOnlyList<PlaceForm> start)
    {
        private readonly List<(IReadOnlyList<PlaceForm> Held, IReadOnlyList<PlaceForm> Failed)> _levels = [([.. start], [])];

        private int _level;

        private string? _joined;

        /// <summary>Where <paramref name="command"/> may stand, the levels it enters or leaves entered or left.</summary>
        public IReadOnlyList<PlaceForm> Enter(AnalyzedCommand command)
        {
            var level = Math.Max(0, command.Level);

            if (_levels.Count > level + 1)
            {
                _levels.RemoveRange(level + 1, _levels.Count - level - 1);
            }

            var entered = false;

            while (_levels.Count < level + 1)
            {
                var parent = _levels.Count - 1;
                var entry = Joined(_levels[parent], command.Joined);

                _levels[parent] = Outcome(_levels[parent], command.Joined, entry, entry);
                _levels.Add((entry, []));
                entered = true;
            }

            _level = level;
            _joined = entered ? null : command.Joined;

            return Joined(_levels[level], _joined);
        }

        /// <summary>The command <see cref="Enter"/> placed at <paramref name="frontier"/> held at <paramref name="held"/>, or failed where it stood.</summary>
        public void Left(IReadOnlyList<PlaceForm> frontier, IReadOnlyList<PlaceForm> held)
            => _levels[_level] = Outcome(_levels[_level], _joined, held, frontier);

        private static IReadOnlyList<PlaceForm> Joined((IReadOnlyList<PlaceForm> Held, IReadOnlyList<PlaceForm> Failed) before, string? joined)
            => joined switch
            {
                "&&" => before.Held,
                "||" => before.Failed,
                _ => PlaceForm.Union(before.Held.Concat(before.Failed)),
            };

        private static (IReadOnlyList<PlaceForm> Held, IReadOnlyList<PlaceForm> Failed) Outcome((IReadOnlyList<PlaceForm> Held, IReadOnlyList<PlaceForm> Failed) before, string? joined, IReadOnlyList<PlaceForm> held, IReadOnlyList<PlaceForm> failed)
            => joined switch
            {
                "&&" => (held, PlaceForm.Union(before.Failed.Concat(failed))),
                "||" => (PlaceForm.Union(before.Held.Concat(held)), failed),
                _ => (held, failed),
            };
    }

    /// <summary>
    /// What a string's commands set and write, as each command after them reads it: the values its own assignments give
    /// each variable, the variables it sets to a value it computes, the files it writes and the text a here-document gives
    /// one, and whether a command of it moved.
    /// </summary>
    /// <param name="windows">Whether the string runs on Windows, whose device names reach no file where cmd or PowerShell writes them (<see cref="WritesNoFile"/>).</param>
    /// <param name="meet">
    /// Whether some place of one form is some place of another (<see cref="AgentPolicy.Meet"/>): a file written and a file
    /// read are the same where some place each may be is, asked of their forms, never of every place spelled out.
    /// </param>
    /// <param name="mayEndIn">Whether the text of some place of a form ends in an end (<see cref="AgentPolicy.MayEndIn"/>).</param>
    private sealed class StringState(bool windows, Func<PlaceForm, PlaceForm, bool> meet, Func<PlaceForm, string, bool> mayEndIn)
    {
        /// <summary>
        /// The text the string wrote into the file <paramref name="whole"/> names: the file it wrote by that form, or every
        /// one it wrote that some place of the form is (<see cref="AgentPolicy.Meet"/>) - the one text they all hold, null
        /// where one holds none known or they hold more than one. Not found where it wrote none.
        /// </summary>
        public bool WroteInto(PlaceForm whole, out string? text)
        {
            if (Written.FirstOrDefault(written => written.Whole.Key == whole.Key) is { Whole: not null } same)
            {
                text = same.Text;
                return true;
            }

            var met = Written.Where(written => meet(written.Whole, whole)).Select(written => written.Text).ToList();
            text = met.Count > 0 && met.All(each => each is not null && each == met[0]) ? met[0] : null;

            return met.Count > 0;
        }

        /// <summary>
        /// The pattern of the values each variable the string set to text it spells may hold, by its name: every value
        /// kept, however many, and a value read from another variable the same choice as that variable's own
        /// (<see cref="WordPattern"/>).
        /// </summary>
        public Dictionary<string, WordPattern> Values { get; } = new(StringComparer.Ordinal);

        /// <summary>The choice of where the shell may stand, as <c>PWD</c> holds it, while no command has moved since it was read.</summary>
        private (WordChoice Choice, int Places)? _standing;

        /// <summary>A command may have moved the shell: <c>PWD</c> is read anew, a choice of its own, by the commands after it.</summary>
        public void Stood() => _standing = null;

        /// <summary>The spellings each of <paramref name="name"/>'s values has, in order; null where none is known.</summary>
        public IReadOnlyList<string>? Spelled(string name)
            => Values.TryGetValue(name, out var values) ? [.. values.Spellings().Select(spelled => spelled.Word).Distinct(StringComparer.Ordinal)] : null;

        /// <summary>The variables the string set to a value it computes, which this cannot know.</summary>
        public HashSet<string> Unknown { get; } = new(StringComparer.Ordinal);

        /// <summary>
        /// The files the string wrote content into, each the form of every place it may be from each place its command may
        /// stand in, with the text a here-document or here-string gave it where one did - a later write to the same form
        /// in its place.
        /// </summary>
        public List<(PlaceForm Whole, string? Text)> Written { get; } = [];

        /// <summary>
        /// What follows the last expansion of the name of each file the string wrote content into that it named by what
        /// the shell expands into a name this does not read (<see cref="Unread"/>, <see cref="LiteralEnd"/>): the end every
        /// place that file may be shares.
        /// </summary>
        public List<string> UnreadEnds { get; } = [];

        /// <summary>Whether a command of the string moved, so a <c>cd -</c> goes back to a place already reached.</summary>
        public bool Moved { get; set; }

        /// <summary>
        /// The pattern of the values <paramref name="name"/> may hold: the string's own; for <c>PWD</c> one choice of every
        /// place the command may stand in - each form's places as its pattern (<see cref="PlaceForm.Pattern"/>) - and every
        /// value the string gave it - a cd after the assignment sets it to the place, which is among them - the same choice
        /// for every command that reads it while none moves between, as the shell's own directory is one there; none where
        /// the string gave it a value it computes; null where none is known.
        /// </summary>
        public WordPattern? ValuesOf(string name, IReadOnlyList<PlaceForm> places)
        {
            if (name != "PWD")
            {
                return Values.TryGetValue(name, out var values) ? values : null;
            }

            if (Unknown.Contains(name))
            {
                return null;
            }

            if (_standing is not { } standing || standing.Places != places.Count)
            {
                IReadOnlyList<WordPattern> each = [.. places.Select(place => place.Pattern())];
                standing = (new WordChoice(Values.TryGetValue(name, out var own) ? [.. each, own] : each), places.Count);
                _standing = standing;
            }

            return new WordPattern([new WordPart.Choice(standing.Choice)]);
        }

        /// <summary>
        /// What <paramref name="analyzed"/> leaves for the commands after it: each variable it sets - assignments alone,
        /// or a declaration's - to the text it spells, every variable the string knows given its values in it, or to a
        /// value it computes; and each file it writes content into - an output redirect's, tee's, a download's, sed's, a
        /// PowerShell content cmdlet's - in each of <paramref name="readings"/>, by every place <paramref name="wholes"/>
        /// says it may be, with the text a here-document or here-string gives cat or tee for it; or by the end its name
        /// holds past the last expansion the shell makes of it, where it is named by one this does not read - a tilde of a
        /// variable the string set to a value it computes among them (<see cref="Unread"/>). A device it writes to is no
        /// file (<see cref="WritesNoFile"/>), a Windows device's name only where cmd or PowerShell writes it.
        /// </summary>
        public void Record(AnalyzedCommand analyzed, CommandReadings readings, int environment, IReadOnlyList<PlaceForm> places, Func<string, IReadOnlyList<PlaceForm>?> wholes)
        {
            var words = analyzed.Words;
            var program = environment < words.Count ? CommandAnalysis.Name(words[environment]) : string.Empty;
            var declares = program is "export" or "declare" or "typeset" or "local" or "readonly";
            var settings = environment == words.Count ? Enumerable.Range(0, environment)
                : declares ? Enumerable.Range(environment + 1, words.Count - environment - 1).Where(at => CommandAnalysis.IsAssignment(words[at]))
                : [];

            foreach (var at in settings)
            {
                var name = CommandAnalysis.AssignedName(words[at]);
                var equals = words[at].IndexOf('=', StringComparison.Ordinal);
                var mask = analyzed.QuotedWord(at);
                var value = words[at][(equals + 1)..];

                // The value as the pattern of its spellings: each known variable it reads standing for every value it may
                // hold. One whose spelling may hold what the shell expands - an unknown variable, a substitution - is one the
                // string computes. A value whose braces the shell expands - none of what they spell expanded further - is the
                // pattern of what they spell: the variable holds one of them, whichever it is.
                var given = CommandAnalysis.Patterned(value, mask.Length == words[at].Length ? mask[(equals + 1)..] : null, known => ValuesOf(known, places));

                if (!given.Chooses
                    && value.Contains('{', StringComparison.Ordinal)
                    && WordPattern.Braces(value, mask.Length == words[at].Length ? mask[(equals + 1)..] : null) is { } braces
                    && !CommandAnalysis.MayExpand(braces))
                {
                    given = braces;
                }

                if (CommandAnalysis.MayExpand(given))
                {
                    Values.Remove(name);
                    Unknown.Add(name);
                    continue;
                }

                // A += adds to every value the name held: the pattern of each, then of what is added.
                Values[name] = words[at][..equals].EndsWith('+') && Values.TryGetValue(name, out var before) ? WordPattern.Joined(before, given) : given;
                Unknown.Remove(name);

                if (name == "PWD")
                {
                    Stood();
                }
            }

            var contentWriters = new[] { "tee", "dd", "curl", "wget", "sed", "gsed", "Out-File", "Set-Content", "Add-Content", "sc", "ac", "Invoke-WebRequest", "iwr", "Invoke-RestMethod", "irm" };

            // A reading that gives a known variable its values names the file as the shell would; the spelling alone
            // stands where no variable of it is known. What the readings of this one command write is collected before
            // it is kept: the readings are the ways one command may be spelled, and where they write one form with
            // texts that differ, or one of them appends, what the file holds is not known - never the last reading's.
            var wrote = new Dictionary<string, (PlaceForm Whole, List<string?> Texts)>(StringComparer.Ordinal);

            foreach (var reading in readings.Given)
            {
                var targets = reading.Redirects.Where(redirect => redirect.Operator is ">" or ">>" or "<>").Select(redirect => redirect.Target)
                    .Concat(contentWriters.Contains(program, StringComparer.OrdinalIgnoreCase) ? CommandAnalysis.WriteTargets(reading with { Redirects = [] }, environment) : []);
                var text = reading.Redirects.LastOrDefault(redirect => redirect.Descriptor == 0) is { Operator: "<<<" } input
                    && (program == "cat" || program == "tee")
                    && reading.Words.Skip(environment + 1).All(word => word.StartsWith('-') || program == "tee")
                        ? input.Target
                        : null;

                // A device is no file: nothing written there is run after.
                foreach (var target in targets.Where(target => !WritesNoFile(target, windows && CommandAnalysis.ReadBy(analyzed) is CommandAnalysis.Reader.Cmd or CommandAnalysis.Reader.PowerShell)))
                {
                    if (Unread(target, quoted: null) || wholes(target) is not { } spelled)
                    {
                        UnreadEnds.Add(LiteralEnd(target));
                        continue;
                    }

                    var appended = reading.Redirects.Any(redirect => redirect.Operator == ">>" && redirect.Target == target) || reading.Words.Skip(environment + 1).Any(word => word is "-a" or "--append");

                    // A later command's write to the same form is what it holds now; the readings of this one are
                    // collected and kept as one.
                    foreach (var whole in spelled)
                    {
                        if (!wrote.TryGetValue(whole.Key, out var held))
                        {
                            wrote[whole.Key] = (whole, [appended ? null : text]);
                        }
                        else
                        {
                            held.Texts.Add(appended ? null : text);
                        }
                    }
                }
            }

            foreach (var (whole, texts) in wrote.Values)
            {
                Written.RemoveAll(written => written.Whole.Key == whole.Key);
                Written.Add((whole, texts.Distinct().Count() == 1 ? texts[0] : null));
            }
        }

        /// <summary>
        /// Whether <paramref name="word"/> - spelled with <paramref name="quoted"/>, reaching <paramref name="wholes"/> -
        /// may name a file the string wrote that this cannot tell by its path: a word the shell expands into a name this
        /// does not read (<see cref="Unread"/>), and one that reaches a place this cannot tell, may name any file the string
        /// wrote where it is read as a program the command runs or a script file (<paramref name="runs"/>); and any word a
        /// file named by what the shell expands where the word's place ends in that name's end (<see cref="UnreadEnds"/>) -
        /// its path, where the end holds a separator, its name where not. An end that is empty is a name the shell gives
        /// whole, which no word this reads is taken to be.
        /// </summary>
        /// <param name="word">The word.</param>
        /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
        /// <param name="wholes">Every place the word may reach, or null where this cannot tell one.</param>
        /// <param name="runs">
        /// Whether the word is read as a program the command runs (<see cref="CommandAnalysis.ProgramsAt"/>) or as a script
        /// file one reads, rather than another word of a program that runs its words (<see cref="CommandAnalysis.RunsAt"/>) -
        /// an option's value, a remote command, a command line's text, an argument.
        /// </param>
        public bool MayBeUnread(string word, string? quoted, IReadOnlyList<PlaceForm>? wholes, bool runs)
        {
            if (runs && (wholes is null || Unread(word, quoted)))
            {
                return true;
            }

            // An end with no separator lies within the last name, so a reading ending in it is one whose last name does.
            return wholes is not null && UnreadEnds.Any(end => end.Length > 0 && wholes.Any(whole => mayEndIn(whole, end)));
        }

        /// <summary>
        /// Whether the shell expands <paramref name="word"/>, spelled with <paramref name="quoted"/>, into a name this does
        /// not read: by what it expands (<see cref="CommandAnalysis.Expansion"/>), or by a tilde at its start whose
        /// variable the string set to a value it computes - <c>~</c> where it so set <c>HOME</c> - which the readings leave
        /// as it is spelled (<see cref="UnreadTilde"/>).
        /// </summary>
        private bool Unread(string word, string? quoted) => CommandAnalysis.Expansion(word, quoted) is not null || UnreadTilde(word, quoted) > 0;

        /// <summary>
        /// How long the tilde at <paramref name="word"/>'s start is that the shell takes from a variable this does not
        /// read - <c>~+</c>, <c>~-</c> and the stack's (<see cref="CommandAnalysis.VariableTilde"/>), and <c>~</c> where the
        /// string set <c>HOME</c> to a value it computes - or 0 where none is.
        /// </summary>
        private int UnreadTilde(string word, string? quoted)
            => CommandAnalysis.VariableTilde(word, quoted) is var variable and > 0 ? variable
                : Unknown.Contains("HOME") && CommandAnalysis.TildeExpands(word, quoted) && (word == "~" || word.StartsWith("~/", StringComparison.Ordinal) || word.StartsWith(@"~\", StringComparison.Ordinal)) ? 1
                : 0;

        /// <summary>
        /// What follows the last expansion the shell makes of <paramref name="target"/> - a variable with its name, a
        /// substitution, a pattern, a tilde whose variable this does not read (<see cref="UnreadTilde"/>) - its separators
        /// spelled '/': the end every file it may name shares. Past a <c>.</c> or <c>..</c> segment, only what follows it,
        /// which the system walks to whole.
        /// </summary>
        private string LiteralEnd(string target)
        {
            var last = Math.Max(target.LastIndexOfAny(['$', '%', ')', '}', '`', '*', '?', ']']), UnreadTilde(target, quoted: null) - 1);
            var end = target[(last + 1)..];

            // A variable's name runs on past its dollar: a name's letters, digits and underscores, or one character.
            if (last >= 0 && target[last] == '$' && end.Length > 0)
            {
                end = char.IsAsciiLetter(end[0]) || end[0] == '_'
                    ? new string([.. end.SkipWhile(character => char.IsAsciiLetterOrDigit(character) || character == '_')])
                    : end[1..];
            }

            var segments = end.Replace('\\', '/').Split('/');
            var dots = Array.FindLastIndex(segments, segment => segment is "." or "..");

            return dots < 0 ? string.Join('/', segments) : "/" + string.Join('/', segments[(dots + 1)..]);
        }
    }

    /// <summary>
    /// The readings of <paramref name="analyzed"/> a path is decided on: the command as it is spelled, then - where a word
    /// or a redirect's file names a variable the string set to text it spells, or <c>$PWD</c> - the command with those
    /// variables given their values (<see cref="CommandAnalysis.Patterned"/>, <see cref="WordPattern.Spellings(IReadOnlyList{WordPattern})"/>):
    /// every value each may hold, however many, one value of a variable in every word and file that reads it, as the
    /// shell substitutes one, a leading <c>~</c> among them where the string, or a command that runs this one, sets
    /// <c>HOME</c>. A variable the string does not set, or sets to a value it computes, is read as the text it spells. The
    /// ways the variables' choices may be made are given as probes (<see cref="ProbesCore"/>): a choice whose
    /// alternatives name nothing the analysis reads stands as one core, and only the rest are spelled one alternative at
    /// a time. Given lazily: a decision that refuses a reading reads no more of them.
    /// </summary>
    private static CommandReadings Readings(AnalyzedCommand analyzed, StringState state, IReadOnlyList<PlaceForm> places, Probes probes)
    {
        // A shell a command runs with HOME set reads its tilde as that HOME: the command's own environment carries the HOME
        // of each command that runs it (HOME=x bash -c '...'), read beside the string's own - one choice for the command.
        var carriedHomes = analyzed.Words.Take(CommandAnalysis.EnvironmentOf(analyzed.Words))
            .Where(word => CommandAnalysis.IsAssignment(word) && CommandAnalysis.AssignedName(word) == "HOME")
            .Select(word => word.Split('=', 2)[1])
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var stateHome = state.ValuesOf("HOME", places);
        WordPattern? carried = carriedHomes.Count == 0 ? null : new WordPattern([new WordPart.Choice(new WordChoice(
            [.. stateHome is null ? [] : new[] { stateHome }, .. carriedHomes.Select(home => WordPattern.Of(home, new string(WordQuoting.Open, home.Length)))]))]);

        WordPattern? Values(string name) => name == "HOME" && carried is not null ? carried : state.ValuesOf(name, places);

        IReadOnlyList<WordPattern> words = [.. analyzed.Words.Select((word, at) => CommandAnalysis.Patterned(word, analyzed.QuotedWord(at), Values))];
        IReadOnlyList<WordPattern> targets = [.. analyzed.Redirects.Select(redirect => redirect.Operator == "<<<"
            ? WordPattern.Of(redirect.Target, new string(WordQuoting.Literal, redirect.Target.Length))
            : CommandAnalysis.Patterned(redirect.Target, null, Values))];

        // A command none of whose words or files reads a variable the string set is its one reading.
        return words.Concat(targets).All(pattern => pattern.Parts is [WordPart.Text])
            ? new CommandReadings(analyzed, [])
            : new CommandReadings(analyzed, EveryReading
                ? Substituted(analyzed, words, targets)
                : ProbesCore(analyzed, words, targets, probes));

        static IEnumerable<AnalyzedCommand> Substituted(AnalyzedCommand analyzed, IReadOnlyList<WordPattern> words, IReadOnlyList<WordPattern> targets)
        {
            foreach (var spelled in WordPattern.Spellings([.. words, .. targets]))
            {
                yield return new AnalyzedCommand(
                    [.. spelled.Take(words.Count).Select(each => each.Word)],
                    [.. analyzed.Redirects.Zip(spelled.Skip(words.Count), (redirect, target) => redirect.Operator == "<<<" ? redirect : redirect with { Target = target.Word })],
                    [.. spelled.Take(words.Count).Select(each => each.Quoted)])
                {
                    Wrappers = analyzed.Wrappers,
                };
            }
        }
    }

    /// <summary>
    /// The readings of one command (<see cref="Readings"/>): as it is spelled first, then each the variables the string
    /// set give it (<see cref="Substituted"/>), given lazily - and kept as they are read, so a decision that asks for them
    /// again (a decision of a string asks of every start of a program) reads what it read, never spelling the ways the
    /// variables' choices may be made a second time.
    /// </summary>
    /// <param name="Spelled">The command as it is spelled.</param>
    /// <param name="Substituted">The command with the variables the string set given their values, one reading each way; none where it reads none.</param>
    private sealed record CommandReadings(AnalyzedCommand Spelled, IEnumerable<AnalyzedCommand> Substituted) : IEnumerable<AnalyzedCommand>
    {
        /// <summary>The readings read so far, in the order they were spelled: what an enumeration that asks again is given first.</summary>
        private readonly List<AnalyzedCommand> _read = [];

        /// <summary>The reading of <see cref="Substituted"/> still being spelled, kept until it ends; null before the first is asked for.</summary>
        private IEnumerator<AnalyzedCommand>? _reading;

        /// <summary>The readings the variables give where there are any, else the command as it is spelled: what a file it names is read by.</summary>
        public IEnumerable<AnalyzedCommand> Given => Read().Any() ? Read() : [Spelled];

        /// <inheritdoc/>
        public IEnumerator<AnalyzedCommand> GetEnumerator() => Read().Prepend(Spelled).GetEnumerator();

        /// <inheritdoc/>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();

        /// <summary>
        /// The substituted readings as they are spelled, kept as they are read: an enumeration replays what it holds and spells on
        /// from where the reading stands, so the ways the variables' choices may be made are spelled once, however often they are asked for.
        /// </summary>
        private IEnumerable<AnalyzedCommand> Read()
        {
            for (var at = 0; ; at++)
            {
                while (at >= _read.Count)
                {
                    _reading ??= Substituted.GetEnumerator();

                    if (!_reading.MoveNext())
                    {
                        _reading.Dispose();
                        yield break;
                    }

                    _read.Add(_reading.Current);
                }

                yield return _read[at];
            }
        }
    }

    /// <summary>
    /// The words of a command, by where each stands, that are never refused for what a directory they name holds: a word
    /// that reads nothing there - a move's own targets (<see cref="Movers"/>) and the words a program moves by
    /// (<see cref="CommandAnalysis.MoveWords"/>), every word of a program that names, tests or lists what it is given
    /// (<see cref="CommandAnalysis.NamesOnly"/>), every word of a git command that reads only what git tracks
    /// (<see cref="CommandAnalysis.ReadsTrackedOnly"/>), which no file git ignores is, each word that gives where the
    /// program writes (<see cref="CommandAnalysis.WriteTargets"/>), and each that names what a program leaves out
    /// (<see cref="CommandAnalysis.ExcludeWords"/>). The command's environment is never one: its values are read.
    /// </summary>
    private static HashSet<int> HoldsExempt(AnalyzedCommand reading, int environment)
    {
        var words = reading.Words;
        var exempt = new HashSet<int>(CommandAnalysis.MoveWords(words));

        if (environment >= words.Count)
        {
            return exempt;
        }

        exempt.UnionWith(CommandAnalysis.ExcludeWords([.. words.Skip(environment)]).Select(at => at + environment));

        if (Movers.Contains(CommandAnalysis.Name(words[environment]), StringComparer.OrdinalIgnoreCase) || CommandAnalysis.NamesOnly(words, environment)
            || CommandAnalysis.ReadsTrackedOnly(words, environment))
        {
            exempt.UnionWith(Enumerable.Range(environment + 1, words.Count - environment - 1));
            return exempt;
        }

        var written = CommandAnalysis.WriteTargets(reading with { Redirects = [] }, environment);

        // A word that names what the program writes by a path it reads - a value after its '=', a word its braces spell -
        // is one too. Where the word's braces spell plain paths, each its own reading alone, the pattern is asked for each
        // target written, never spelled out.
        for (var at = environment + 1; at < words.Count && written.Count > 0; at++)
        {
            var word = words[at];
            var plain = word.Contains('{', StringComparison.Ordinal) && WordPattern.Braces(word, reading.QuotedWord(at)) is { } pattern
                && PlainlySpelled(word, reading.QuotedWord(at), pattern) is not null
                    ? pattern.Matcher()
                    : null;

            if (written.Contains(word, StringComparer.Ordinal)
                || (plain is not null
                    ? CommandAnalysis.PathsNamed(word, braces: false).Any(path => path.Whole && path.Path != word && written.Contains(path.Path, StringComparer.Ordinal))
                        || written.Any(target => target != word && plain.Spells(target, exactly: true))
                    : CommandAnalysis.PathsNamed(word).Any(path => path.Whole && path.Path != word && written.Contains(path.Path, StringComparer.Ordinal))))
            {
                exempt.Add(at);
            }
        }

        return exempt;
    }

    /// <summary>
    /// Every script file a reading of <paramref name="readings"/> runs that a command of its string wrote before it -
    /// each decided, never the first alone - with the text a here-document or here-string gave it where one did: the
    /// program itself, or the script file a shell, <c>source</c>, PowerShell or sed reads
    /// (<see cref="CommandAnalysis.ScriptFilesOf"/>) - named by a word or given as its input - each where a program runs
    /// (<see cref="CommandAnalysis.RunsAt"/>), by every place it may be (<paramref name="wholes"/>), a word whose braces
    /// spell several files read at each of them (<see cref="WordPattern.Spellings()"/>); or one that may be a file the
    /// string wrote this cannot tell by its path (<see cref="StringState.MayBeUnread"/>) - named by what the shell
    /// expands, or run as the program or a script file by a word the shell expands - whose text this does not know. Each
    /// file run once, in the order the readings and their words name them; empty where none runs one.
    /// </summary>
    private static IReadOnlyList<(string File, string? Text)> WrittenScripts(CommandReadings readings, int environment, StringState state, Func<string, IReadOnlyList<PlaceForm>?> wholes)
    {
        if (state.Written.Count == 0 && state.UnreadEnds.Count == 0)
        {
            return [];
        }

        var run = new List<(string File, string? Text)>();

        foreach (var reading in readings.Given)
        {
            var words = reading.Words;

            // The programs the command runs: its own, and the one each runner among its words runs past its own options.
            var programs = CommandAnalysis.ProgramsAt(words);

            for (var at = environment; at < words.Count && CommandAnalysis.RunsAt(words, at); at++)
            {
                var name = CommandAnalysis.Name(words[at]);
                var posix = CommandAnalysis.IsShell(name) || name is "source" or ".";
                var program = programs.Contains(at);
                List<string> masks = [.. Enumerable.Range(at, words.Count - at).Select(reading.QuotedWord)];

                // The program itself, its text read as it runs it - a program the command runs, or any other word a
                // program that runs its words is given; and each script file it reads, named or its input, its text read
                // where a POSIX shell reads it.
                IEnumerable<(string Word, string? Quoted, bool Read, bool Runs)> runs =
                [
                    (words[at], masks[0], true, program),
                    .. CommandAnalysis.ScriptFilesOf([.. words.Skip(at)], masks, reading.Redirects).Select(file => (file, (string?)null, posix, program)),
                ];

                foreach (var (word, quoted, read, runsIt) in runs)
                {
                    // A word whose braces spell several files runs one of them per reading of the command: each is read,
                    // each its own text.
                    foreach (var (spelled, spelledQuoted) in RunWords(word, quoted))
                    {
                        if (run.Any(each => each.File == spelled))
                        {
                            continue;
                        }

                        var places = wholes(spelled);

                        string? text = null;

                        if (places?.FirstOrDefault(whole => state.WroteInto(whole, out text)) is not null)
                        {
                            run.Add((spelled, read ? text : null));
                        }
                        else if (state.MayBeUnread(spelled, spelledQuoted, places, runsIt))
                        {
                            run.Add((spelled, null));
                        }
                    }
                }
            }
        }

        return run;
    }

    /// <summary>
    /// A spelling that decides <paramref name="word"/> at <paramref name="places"/>, for a refusal that names it: a word
    /// held as a core names the path by a spelling that decides it, never the core's own sentinel text.
    /// </summary>
    private string SpelledThatDecide(string word, IReadOnlyList<string> places)
        => Templated(word) is null
            ? word
            : SpelledAgainst(word, places.FirstOrDefault() ?? _ground.WorkingDirectory, _state, dotNames: true).FirstOrDefault() ?? word;

    /// <summary>
    /// The files <paramref name="word"/> names as a program or a script file it runs (<see cref="WrittenScripts"/>): the
    /// word itself, or each word its braces spell with its characters' classes where they pair, given one at a time.
    /// </summary>
    /// <param name="word">The word.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    private static IEnumerable<(string Spelled, string? Quoted)> RunWords(string word, string? quoted)
        => word.Contains('{', StringComparison.Ordinal) && CommandAnalysis.BraceGroup(word) is not null
            ? WordPattern.Braces(word, quoted) is { } pattern
                ? pattern.Spellings().Select(each => (each.Word, (string?)each.Quoted))
                : CommandAnalysis.BraceExpansions(word).Select(each => (each, (string?)null))
            : [(word, quoted)];

    /// <summary>
    /// The walk as its points are spelled: a point held as a core by the spellings that decide it
    /// (<see cref="Decisive"/>), each a walk of its own - a point no core holds is the walk itself, all its points together.
    /// </summary>
    private IEnumerable<FileWalk> WalksSpelled(FileWalk walk, IReadOnlyList<string> places)
    {
        var spelled = new List<IReadOnlyList<string>>();

        foreach (var point in walk.Points)
        {
            if (Templated(point) is { } pattern)
            {
                spelled.Add([.. Decisive(pattern, places.FirstOrDefault() ?? _ground.WorkingDirectory, [], dotNames: true).Select(each => each.Word)]);
            }
        }

        if (spelled.Count == 0)
        {
            yield return walk;
            yield break;
        }

        // Each cored point a walk of its own beside the others: what one removes of what it matches is asked of it alone.
        var others = 0;

        foreach (var point in walk.Points)
        {
            if (Templated(point) is null)
            {
                others++;
            }
        }

        foreach (var each in spelled.SelectMany(points => points))
        {
            yield return walk with { Points = others > 0 ? [.. walk.Points.Where(point => Templated(point) is null), each] : [each] };
        }
    }

    /// <summary>
    /// Why <c>run-side-task</c> is refused, where the program at <paramref name="start"/> starts it - by its own name, or
    /// as this tool's verb, the tool started by its name, its path or its assembly under <c>dotnet</c>, as the command's
    /// reading names every program - to every agent alike, a side-task's own included: a side-task is a person's own
    /// conversation in the person's terminal, with no limit of a run's, and no agent starts one. The first line of the
    /// rule: the command refuses itself as well where it is started under an agent
    /// (<see cref="AgentChildEnvironment.AgentMarker"/>), so a copy of the program under another name starts none either.
    /// Null where the command starts none.
    /// </summary>
    private static AgentPolicyAnswer.Denied? SideTaskRefusal(string program, AnalyzedCommand analyzed, int start, string whole)    {
        var roots = StringComparer.OrdinalIgnoreCase;
        var refused = new AgentPolicyAnswer.Denied(
            SideTasks.SideTaskService.RunCommand,
            AgentPolicySource.Builtin,
            $"'{SideTasks.SideTaskService.RunCommand}' is refused: {SideTasks.SideTaskService.NoAgentStartsOne}");

        if (roots.Equals(program, SideTasks.SideTaskService.RunCommand))
        {
            return refused;
        }

        // The verb by its spelling, or by an expansion the shell makes of it, which may be this one (run-side-$T).
        return ToolVerbOf(program, analyzed, start) switch
        {
            (var verb, null) when roots.Equals(verb, SideTasks.SideTaskService.RunCommand) => refused,
            (_, { }) => MayBe(SideTasks.SideTaskService.RunCommand, AgentPolicySource.Builtin, whole) with
            {
                Reason = $"'{whole}' may start {SideTasks.SideTaskService.RunCommand}: its verb is one the shell expands, which this does not read, and {SideTasks.SideTaskService.NoAgentStartsOne}",
            },
            _ => null,
        };
    }

    /// <summary>The refusal of a verb that reads a credential slot or writes the tool's own state (<see cref="SlotOrStateVerbs"/>), after <paramref name="opening"/>.</summary>
    /// <param name="opening">What is refused, as the sentence opens.</param>
    private static AgentPolicyAnswer.Denied SlotOrStateRefusal(string opening)
        => new(
            "the verbs that read a slot or write state",
            AgentPolicySource.Builtin,
            $"{opening}: it reads a credential slot or writes this tool's own state or install, which no command of an agent may - and where the agent's commands run inside the operating system's isolation, every slot is hidden there and the state read-only - so a person runs it");

    /// <summary>
    /// The verb this tool is handed where <paramref name="program"/>, at <paramref name="start"/>, is this tool - its first
    /// word, for the tool takes no option before its verb (<c>--help</c> and <c>--version</c> alone stand there, and run
    /// none) - with the expansion the shell makes of it where it makes one; null where the program is not this tool, or
    /// names no verb.
    /// </summary>
    private static (string Verb, string? Expansion)? ToolVerbOf(string program, AnalyzedCommand analyzed, int start)
        => program.Equals(ToolPackage.Command, StringComparison.OrdinalIgnoreCase) && start + 1 < analyzed.Words.Count
            ? (analyzed.Words[start + 1], CommandAnalysis.Expansion(analyzed.Words[start + 1], analyzed.QuotedWord(start + 1)))
            : null;

    /// <summary>
    /// The refusal of a command that sets or takes away <see cref="AgentChildEnvironment.AgentMarker"/>, the mark every
    /// program an agent of this tool starts carries and <c>run-side-task</c> refuses itself under, by any form
    /// <see cref="CommandAnalysis.VariableTouched"/> reads - an assignment, an unset, env's <c>-u</c>, a declaration that
    /// takes its export away, a set, read's, mapfile's and their kind's name, an arithmetic's, PowerShell's <c>env:</c>
    /// item - or may take it away by one of those forms given what the shell expands, or starts what it runs with no
    /// environment at all; and of one any other word of names it (<see cref="CommandAnalysis.NameSpelled"/>), but where
    /// the word is only data - a search's, a message's, its value read. Read in the command's words and in each wrapper's
    /// stripped before its program (<see cref="AnalyzedCommand.Wrappers"/>). Null where the command does none of these.
    /// </summary>
    /// <param name="analyzed">The command.</param>
    /// <param name="readings">Its readings (<see cref="Readings"/>): a variable the string set to text it spells is read as that text, never as one that may be the mark.</param>
    /// <param name="powerShell">Whether PowerShell reads it, whose <c>rm</c> and its kind are its item cmdlets.</param>
    /// <param name="environmentDrive">Whether a word of the string names PowerShell's <c>env:</c> drive.</param>
    private static AgentPolicyAnswer.Denied? MarkerTouched(AnalyzedCommand analyzed, CommandReadings readings, bool powerShell, bool environmentDrive)
    {
        var marker = AgentChildEnvironment.AgentMarker;
        var read = readings.Given;

        foreach (var (words, quoted) in analyzed.Wrappers.Select(wrapper => (wrapper, (IReadOnlyList<string>?)null)).Concat(read.Select(reading => (reading.Words, reading.Quoted))))
        {
            if (CommandAnalysis.VariableTouched(words, quoted, marker, powerShell, environmentDrive) is not var (word, touch))
            {
                // Past the forms read, the name anywhere a command may set or take it away - every word but one that is
                // only data.
                if (CommandAnalysis.NameSpelled(words, marker) is { } spelled)
                {
                    return new AgentPolicyAnswer.Denied(
                        marker,
                        AgentPolicySource.Builtin,
                        $"'{string.Join(' ', analyzed.Wrappers.Count > 0 ? analyzed.Wrappers[0] : analyzed.Whole)}' is refused: '{spelled}' names {marker}, which no command but one that only reads its words names, for it marks every program an agent of this tool starts");
                }

                continue;
            }

            var how = touch switch
            {
                VariableTouch.Named => $"'{word}' sets or takes away {marker}",
                VariableTouch.MayBe => $"'{word}' may take away {marker}: it names a variable by what the shell expands, which this does not read",
                _ => $"'{word}' starts what it runs with no environment, {marker} gone with the rest",
            };

            return new AgentPolicyAnswer.Denied(
                marker,
                AgentPolicySource.Builtin,
                $"'{string.Join(' ', analyzed.Wrappers.Count > 0 ? analyzed.Wrappers[0] : analyzed.Whole)}' is refused: {how}, which marks every program an agent of this tool starts");
        }

        return null;
    }

    /// <summary>
    /// Whether cmd.exe, starting a batch file with <paramref name="word"/> among its arguments, would read in it what was
    /// not read here: a character of its own (<see cref="BatchFiles"/>), an expansion the shell fills in first, or the
    /// word another program fills in as it starts it (find's <c>-exec</c>, fd's <c>-x</c>, parallel), an expansion as
    /// <see cref="CommandAnalysis"/> reads it - with a file's name, which may hold anything. A redirect is the shell's
    /// own, never among the arguments cmd is handed. The expansion is read only where the shell would make it: a word in
    /// quotes is that word.
    /// </summary>
    private static bool CmdReadsAgain(string word, string? quoted)
        => BatchFiles.Reread([word]) is not null || CommandAnalysis.Expansion(word, quoted) is not null;

    /// <summary>
    /// A command's words as one program of it is handed them: the environment's words, then the words from
    /// <paramref name="start"/> on - a view over the command's own, nothing copied, so a program that runs its words is
    /// read at each of them without a list as long as the command made for each.
    /// </summary>
    private readonly struct WordsOf(IReadOnlyList<string> command, int environment, int start) : IReadOnlyList<string>
    {
        /// <inheritdoc/>
        public int Count => environment + command.Count - start;

        /// <inheritdoc/>
        public string this[int at] => at < environment ? command[at] : command[start + at - environment];

        /// <inheritdoc/>
        public IEnumerator<string> GetEnumerator()
        {
            for (var at = 0; at < Count; at++)
            {
                yield return this[at];
            }
        }

        /// <inheritdoc/>
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>
    /// The rules on one command's words from <paramref name="start"/>, as the program there would run them: the words it
    /// is handed, its redirects the shell's own - with no program where the command is assignments alone.
    /// </summary>
    private AgentPolicyAnswer Decided(AnalyzedCommand analyzed, int start, IReadOnlyList<string> words, string whole, StringComparison comparison)
    {
        var roots = StringComparer.OrdinalIgnoreCase;
        var command = analyzed.Words;
        var program = start < command.Count ? CommandAnalysis.Name(command[start]) : string.Empty;

        // A program that runs a command line it composes - Start-Process's arguments, WMI's, the task scheduler's, code
        // Add-Type compiles, a terminal session's, cron's, launchd's and systemd's - runs what no word here spells: the
        // command's own program, or one a program that runs its words starts, past the root actions.
        if (CommandAnalysis.RunsAt(command, start) && CommandAnalysis.ComposesCommandLine(new WordsOf(command, 0, start)) is { } composed)
        {
            return Undecided(whole, new NotSupportedException($"'{command[start]}' {composed}, which this does not read"));
        }

        // A batch file is started on Windows by cmd.exe, which reads its arguments a second time: one holding what cmd
        // reads as its own, or what the shell expands into it, would run another command than the one read here - wherever
        // it stands, for a wrapper this does not strip starts it as well.
        if (_platform.Current is PlatformId.Windows && start < command.Count && BatchFiles.Is(command[start]))
        {
            string? reread = null;

            for (var at = start + 1; at < command.Count; at++)
            {
                if (CmdReadsAgain(command[at], analyzed.QuotedWord(at)))
                {
                    reread = command[at];
                    break;
                }
            }

            if (reread is not null)
            {
                return Undecided(whole, new NotSupportedException($"'{command[start]}' is a batch file, whose arguments cmd.exe reads again, and '{reread}' holds what cmd reads as its own or what the shell expands"));
            }
        }

        // The tool's own verbs are refused whatever worktreeRules says: an agent does not move its own run. A verb read
        // from a response file is one this cannot see, and is refused alike. A side-task's agent is the one exception on
        // the verbs themselves: it works as the person's own session does, over the main tree, and the tool's verbs are
        // open to it - run-side-task alone stays refused (SideTaskRefusal).
        if (roots.Equals(program, ToolPackage.Command) && start + 1 < command.Count && command[start + 1].StartsWith('@'))
        {
            return new AgentPolicyAnswer.Denied(
                "the orchestrator and agent verbs",
                AgentPolicySource.Builtin,
                $"'{ToolPackage.Command} {command[start + 1]}' is refused: its verb is in a file this does not read, and an agent does not move its own run");
        }

        if (SideTaskRefusal(program, analyzed, start, whole) is { } sideTask)
        {
            return sideTask;
        }

        // A verb that reads a credential slot or writes the tool's own state is refused to every agent, a side-task's too,
        // after a step's own verbs are: inside the operating system's isolation it would fail part way, and the words say
        // so first. One the shell expands is refused to a side-task above, for it may be run-side-task.
        if (_ground.SideTask && ToolVerbOf(program, analyzed, start) is var (sideVerb, _) && SlotOrStateVerbs.Contains(sideVerb, roots))
        {
            return SlotOrStateRefusal($"'{ToolPackage.Command} {sideVerb}' is refused");
        }

        if (!_ground.SideTask && ToolVerbOf(program, analyzed, start) is var (verb, expansion))
        {
            if (ToolVerbs.Contains(verb, roots))
            {
                return new AgentPolicyAnswer.Denied(
                    "the orchestrator and agent verbs",
                    AgentPolicySource.Builtin,
                    $"'{ToolPackage.Command} {verb}' is refused: an agent does not move its own run");
            }

            if (SlotOrStateVerbs.Contains(verb, roots))
            {
                return SlotOrStateRefusal($"'{ToolPackage.Command} {verb}' is refused");
            }

            // A verb the shell expands may be any of them (dssharness $V).
            if (expansion is not null)
            {
                return new AgentPolicyAnswer.Denied(
                    "the orchestrator and agent verbs",
                    AgentPolicySource.Builtin,
                    $"'{whole}' may move its own run: its verb is one the shell expands, which this does not read, and an agent does not move its own run");
            }
        }

        // The Run rules, against each reading of the command's own words in turn: git configured
        // where it runs the git config it is first, then the command with git's options dropped
        // before its subcommand.
        var environment = CommandAnalysis.EnvironmentOf(command);
        var runs = start == environment;

        // How each word stands to the shell, where the command's own spelling was kept: the readings keep step with
        // it, so every expansion is read where the shell would make it.
        var spelled = CommandAnalysis.QuotedOf(analyzed, start);

        var readings = CommandAnalysis.ForRules(words, runs, spelled);

        // A rule the command is decides it first, group by group (RunRules), so a command that prints a credential is
        // named as one whatever else it meets.
        foreach (var group in (RunRules[])[RunRules.Credentials, RunRules.OutOfStep, RunRules.Restrictions, RunRules.Worktree, RunRules.LiveRun])
        {
            if (readings.Select(reading => Met(group, reading, AgentRuleMatch.Yes, whole, comparison)).OfType<AgentPolicyAnswer.Denied>().FirstOrDefault() is { } certain)
            {
                return certain;
            }
        }

        // Only then a rule the command may be, where a word the rule reads is one the shell expands: the person's own
        // named first, so 'git $VERB' under their Run(git push *) is said as theirs, then the worktree rules', then a run
        // working over the tree, which names the run, then the tool's own refusals - a credential's only where nothing
        // before it may be the command.
        foreach (var group in (RunRules[])[RunRules.Restrictions, RunRules.Worktree, RunRules.LiveRun, RunRules.Credentials, RunRules.OutOfStep])
        {
            if (readings.Select(reading => Met(group, reading, AgentRuleMatch.Expanded, whole, comparison)).OfType<AgentPolicyAnswer.Denied>().FirstOrDefault() is { } possible)
            {
                return possible;
            }
        }

        // A git or gh command called by a name none of the program's own commands is: an alias, which the program runs from
        // a configuration file no rule reads, or gh's extension - refused on every ground, a program git finds of that name
        // (git lfs) apart.
        if (readings.Select(reading => CalledByAnotherName(reading, runs || CommandAnalysis.RunsItsWords(command[environment]), command)).OfType<string>().FirstOrDefault() is { } called)
        {
            return new AgentPolicyAnswer.Denied(
                "git's and gh's own commands",
                AgentPolicySource.Builtin,
                $"'{whole}' calls {called}, which runs a command line from a file no rule here reads: an agent calls git and gh by their own commands");
        }

        // The merges and CI starts of gh the worktree rules refuse. A gh met past the command's program runs where that
        // program runs the words after it (setsid gh co); among any other program's words it is that program's data
        // (Get-Command gh -ErrorAction SilentlyContinue).
        if (_ground.WorktreeRules
            && readings.Select(reading => MergesOrStartsCi(reading, runs || CommandAnalysis.RunsItsWords(command[environment]))).OfType<string>().FirstOrDefault() is { } reached)
        {
            return new AgentPolicyAnswer.Denied(
                "the merges and CI starts of gh",
                AgentPolicySource.Builtin,
                $"'{whole}' is denied by a worktree rule of this tool: it reaches {reached}, and never merges, never starts CI binds the agents too");
        }

        // A command that runs a program which fetches from the network is decided as a web request
        // too, by each address it names - and where the web is held to rules, one whose addresses
        // are in a file this does not read is denied.
        foreach (var address in CommandAnalysis.WebAddresses(words, spelled))
        {
            if (WebDecided(address, whole, domains: true, agentRules: true).Refusal() is { } denied)
            {
                return denied;
            }
        }

        if (CommandAnalysis.WebAddressesUnread(words) is { } unread && (!_ground.Web || _webDomains.Count > 0 || _rules.Any(pair => pair.Rule.Verb is AgentRuleVerb.Web)))
        {
            return new AgentPolicyAnswer.Denied(
                _ground.Web ? "the web rules" : "the web capability",
                AgentPolicySource.Builtin,
                _ground.Web
                    ? $"'{whole}' reads what it reaches from a file ({unread}) this does not read, so its hosts cannot be held to the web rules"
                    : $"'{whole}' fetches from the network and this step has no web");
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>
    /// Whether a path the shell would expand from <paramref name="pattern"/> - its <c>*</c>, its <c>?</c>, its
    /// <c>[...]</c> and a <c>**</c> of many directories, a dot matched as any character but at a name's start - may be
    /// <paramref name="place"/> or lie beneath it, compared in any case; with <paramref name="exactly"/>, whether it may
    /// be the place itself. A pattern that ends above the place names its parents only, which reach it only where
    /// <see cref="GlobHolds"/> asks. A name that begins with a dot is matched only by a segment that begins with one,
    /// as the shells match one (<see cref="DotNamed"/>) - but where <paramref name="dotNames"/> says the command's
    /// patterns match dot-names too.
    /// </summary>
    private bool GlobReaches(string pattern, string place, bool dotNames, bool exactly = false)
    {
        var (reaches, prefixes) = GlobAgainst(GlobSegments(pattern), place, dotNames);

        return exactly ? prefixes[^1] : reaches;
    }

    /// <summary>A shell pattern's segments, each with the expression it matches a name by - null for a <c>**</c> - read once for every place it is held against.</summary>
    private (string[] Segments, System.Text.RegularExpressions.Regex?[] Matchers) GlobSegments(string pattern)
    {
        var segments = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        return (segments, [.. segments.Select(segment => segment == "**" ? null : SegmentPattern(segment))]);
    }

    /// <summary>
    /// How the pattern <paramref name="pattern"/> stands to <paramref name="place"/> (<see cref="GlobReaches"/>): whether a
    /// path it may name is the place or lies beneath it, and, for each count of the place's leading segments, whether it
    /// may be exactly those - the place's own count last. Read from the start, each pattern segment against each place
    /// segment once: whether the pattern's first p segments may be the place's first q, a <c>**</c> taking none of them or
    /// any run of them, crossing a dot-named directory where a star matches one.
    /// </summary>
    private static (bool Reaches, bool[] Prefixes) GlobAgainst((string[] Segments, System.Text.RegularExpressions.Regex?[] Matchers) pattern, string place, bool dotNames)    {
        var (patterned, segments) = pattern;
        var placed = place.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var met = new bool[patterned.Length + 1, placed.Length + 1];
        met[0, 0] = true;

        for (var q = 0; q <= placed.Length; q++)
        {
            for (var p = 0; p <= patterned.Length; p++)
            {
                if (!met[p, q] || p == patterned.Length)
                {
                    continue;
                }

                if (segments[p] is not { } segment)
                {
                    met[p + 1, q] = true;

                    if (q < placed.Length && (dotNames || !placed[q].StartsWith('.')))
                    {
                        met[p, q + 1] = true;
                    }
                }
                else if (q < placed.Length && DotNamed(patterned[p], placed[q], dotNames) && segment.IsMatch(placed[q]))
                {
                    met[p + 1, q + 1] = true;
                }
            }
        }

        // A path the pattern names reaches the place where the place is met with the pattern's rest still to go; it is
        // exactly a count of the place's segments where they are met with nothing left of the pattern but its '**'s.
        var reaches = Enumerable.Range(0, patterned.Length + 1).Any(p => met[p, placed.Length]);
        var prefixes = new bool[placed.Length + 1];

        for (var q = 0; q <= placed.Length; q++)
        {
            prefixes[q] = Enumerable.Range(0, patterned.Length + 1).Any(p => met[p, q] && patterned[p..].All(rest => rest == "**"));
        }

        return (reaches, prefixes);
    }

    /// <summary>
    /// Whether every path <paramref name="pattern"/> may name lies in a root of the agent's own that stands inside
    /// <paramref name="state"/> - its worktree under the worktrees root, its work directory under the run's - as
    /// <see cref="KeptWriteRefusal"/> excepts a path there: the part of the pattern before its first wildcard lies in such
    /// a root, so no match leaves it.
    /// </summary>
    private bool OwnPlaceInState(string pattern, string state)
    {
        var wildcard = pattern.IndexOfAny(['*', '?', '[']);
        var separator = pattern.LastIndexOfAny(['/', '\\'], wildcard < 0 ? pattern.Length - 1 : wildcard);

        if (separator <= 0)
        {
            return false;
        }

        var literal = pattern[..separator];

        return _allRoots.Any(root => PathContainment.IsStrictlyInside(state, root, _platform.PathComparison)
            && PathContainment.IsInsideOrSame(root, literal, _platform.PathComparison));
    }

    /// <summary>
    /// Whether the pattern's segment <paramref name="segment"/> may match <paramref name="name"/> for what each begins
    /// with: a name beginning with a dot is matched only by a segment beginning with a dot itself - bash, zsh and sh never
    /// match one by a star, a question mark or a bracket - but where <paramref name="dotNames"/> says the command's
    /// patterns match dot-names too (<see cref="PatternsMatchDotNames"/>).
    /// </summary>
    private static bool DotNamed(string segment, string name, bool dotNames)
        => dotNames || segment.StartsWith('.') || !name.StartsWith('.');

    /// <summary>
    /// Whether a setting of <paramref name="commands"/>' makes a shell's patterns match a name beginning with a dot by a
    /// star, a question mark or a bracket: a word that names bash's <c>dotglob</c> or zsh's <c>globdots</c> - as a token of
    /// it, split at a blank, an <c>=</c>, a <c>:</c> or a <c>,</c>, read in any case, its underscores and dashes aside and
    /// a <c>no</c> before it read as the setting (<c>shopt -s dotglob</c>, <c>BASHOPTS=dotglob</c>, <c>unsetopt
    /// NO_GLOB_DOTS</c>, <c>zsh -o globdots</c>) - or that names <c>GLOBIGNORE</c> or <c>BASHOPTS</c> at all, which a
    /// command may set by any means (<c>read</c>, <c>printf -v</c>). Read of the whole string, wherever the setting stands
    /// - in a command's own words or those of a command that runs it (<see cref="AnalyzedCommand.Wrappers"/>) - and whether
    /// it turns the setting on or off: a pattern this cannot tell from one that matches is read as one. The shells that
    /// match dot-names of their own are read per command (<see cref="MatchesDotNames"/>); a zsh glob qualifier
    /// (<c>*(D)</c>) is refused where it is read, as every parenthesis after a command's word is.
    /// </summary>
    /// <param name="commands">The commands of one string or one argument list, flattened.</param>
    private static bool PatternsMatchDotNames(IReadOnlyList<AnalyzedCommand> commands)
        => commands.SelectMany(command => command.Wrappers.Append(command.Words)).SelectMany(words => words).Any(word =>
            word.Contains("GLOBIGNORE", StringComparison.OrdinalIgnoreCase)
            || word.Contains("BASHOPTS", StringComparison.OrdinalIgnoreCase)
            || word.Split([' ', '\t', '\n', '=', ':', ','], StringSplitOptions.RemoveEmptyEntries).Any(token =>
                token.Replace("_", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).TrimStart('+').ToLowerInvariant() is var read
                && (read.StartsWith("no", StringComparison.Ordinal) ? read[2..] : read) is "dotglob" or "globdots"));

    /// <summary>
    /// Whether the shell that expands <paramref name="analyzed"/>'s patterns matches a name beginning with a dot by a star,
    /// a question mark or a bracket of its own: PowerShell and cmd.exe, whose wildcards match every name a directory lists
    /// that it does not hide (on Windows a dot-name is hidden only by its attribute) - read by the shell that reads this
    /// command (<see cref="CommandAnalysis.ReadBy"/>) - or a command given PowerShell's <c>-Force</c>, which matches a hidden name too.
    /// Read of this command alone: another command of the string is read by its own shell.
    /// </summary>
    private static bool MatchesDotNames(AnalyzedCommand analyzed)
        => CommandAnalysis.ReadBy(analyzed) is CommandAnalysis.Reader.PowerShell or CommandAnalysis.Reader.Cmd
            || analyzed.Words.Any(word => word.Length >= 3 && word.StartsWith("-fo", StringComparison.OrdinalIgnoreCase) && "-force".StartsWith(word.Split(':', 2)[0], StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Whether a write to <paramref name="target"/> reaches a device and no file: the null device the shells name
    /// (<c>/dev/null</c>), and where cmd or PowerShell writes it on Windows (<paramref name="windowsShell"/>) a name every
    /// Windows reads as its device in any directory, bare (<c>NUL</c>, <c>\\.\NUL</c>,
    /// <see cref="Runtime.ConfinedPaths.DeviceNames"/>). One with an extension (<c>NUL.txt</c>) is a file to a Windows that
    /// reads it so; a POSIX shell - Git Bash's on Windows among them - writes a file of any such name, as does every shell
    /// on another system.
    /// </summary>
    private static bool WritesNoFile(string target, bool windowsShell)
        => target.Replace('\\', '/') is var path
            && (path == "/dev/null"
                || (windowsShell && Runtime.ConfinedPaths.DeviceNames.Contains(Path.GetFileName(path).TrimEnd(':', ' ', '.'), StringComparer.OrdinalIgnoreCase)));

    /// <summary>Whether <paramref name="word"/>'s last name is one a Windows may read as its device's, with any extension (<see cref="Runtime.ConfinedPaths.DeviceNames"/>).</summary>
    private static bool IsDevice(string word)
        => Runtime.ConfinedPaths.DeviceNames.Contains(
            Path.GetFileName(word.TrimEnd(':', ' ', '.').Replace('\\', '/')).Split('.')[0].TrimEnd(' '),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One segment of a shell pattern as the expression it matches a whole name by, in any case where
    /// <paramref name="ignoreCase"/> says so: a bracket's members each the character it is, a range kept; one the shell
    /// would not read as a bracket - a <c>]</c> right after its <c>[</c> or <c>[!</c> is a member, and one that does not
    /// close or holds a reversed range is the characters it is - read as those characters.
    /// </summary>
    /// <remarks>
    /// The expression a segment spells is a fact of its text: kept by the policy, so a segment - a name asked of each
    /// reading it stands in, a pattern asked of each place it is held against - is compiled once, however often it is read.
    /// </remarks>
    private System.Text.RegularExpressions.Regex SegmentPattern(string segment, bool ignoreCase = true)
        => _segments.TryGetValue((segment, ignoreCase), out var spelled) ? spelled : _segments[(segment, ignoreCase)] = Segmented(segment, ignoreCase);

    /// <summary>The expression each segment spells, by its text and case (<see cref="SegmentPattern"/>): each compiled once.</summary>
    private readonly Dictionary<(string Segment, bool IgnoreCase), System.Text.RegularExpressions.Regex> _segments = [];

    /// <summary><see cref="SegmentPattern"/> asked of the segment's text itself.</summary>
    private static System.Text.RegularExpressions.Regex Segmented(string segment, bool ignoreCase)
    {
        var expression = new System.Text.StringBuilder("^");

        for (var at = 0; at < segment.Length; at++)
        {
            var character = segment[at];

            if (character == '*')
            {
                expression.Append(".*");
            }
            else if (character == '?')
            {
                expression.Append('.');
            }
            else if (character == '[' && Bracket(segment, at) is var (closing, members) && closing > 0)
            {
                expression.Append(members);
                at = closing;
            }
            else
            {
                expression.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
            }
        }

        // Matched in time linear in the name, however its stars and classes nest (LinearRegex): no match is cut short.
        return LinearRegex.Compile(expression.Append('$').ToString(), ignoreCase ? System.Text.RegularExpressions.RegexOptions.IgnoreCase : System.Text.RegularExpressions.RegexOptions.None);
    }

    /// <summary>
    /// The bracket a pattern opens at <paramref name="at"/>, as the shell reads one: where it closes and the class it is,
    /// each member escaped, a range kept where it runs forward - or -1 where it is no bracket, its '[' a character.
    /// </summary>
    private static (int Closing, string Members) Bracket(string segment, int at)
    {
        var negated = at + 1 < segment.Length && segment[at + 1] is '!' or '^';
        var first = at + (negated ? 2 : 1);

        // A ']' right after the opening is a member, never the close.
        var closing = first < segment.Length ? segment.IndexOf(']', first + 1) : -1;

        if (closing < 0)
        {
            return (-1, string.Empty);
        }

        var members = new System.Text.StringBuilder(negated ? "[^" : "[");

        for (var member = first; member < closing; member++)
        {
            if (member + 2 < closing && segment[member + 1] == '-')
            {
                if (segment[member] > segment[member + 2])
                {
                    return (-1, string.Empty);
                }

                members.Append(Member(segment[member])).Append('-').Append(Member(segment[member + 2]));
                member += 2;
            }
            else
            {
                members.Append(Member(segment[member]));
            }
        }

        return (closing, members.Append(']').ToString());

        static string Member(char character) => character is '\\' or ']' or '[' or '^' or '-' ? "\\" + character : character.ToString();
    }

    /// <summary>
    /// The denial of a command a rule may be what it is: a word the rule reads is one the shell
    /// expands, so what it runs cannot be told, and a call this cannot decide never runs.
    /// </summary>
    private static AgentPolicyAnswer.Denied MayBe(string rule, AgentPolicySource source, string whole)
        => new(rule, source, $"'{whole}' may be what {rule} denies: a word the rule reads is one the shell expands, which this does not read");

    /// <summary>The groups of Run rules a command's readings are held to, each read in its own way.</summary>
    private enum RunRules
    {
        /// <summary>The commands that print a credential (<see cref="CredentialPrintingCommands"/>), refused always.</summary>
        Credentials,

        /// <summary>The gh commands that leave a step's limits (<see cref="GhOutOfStepCommands"/>), refused always.</summary>
        OutOfStep,

        /// <summary>The person's own restrictions: the configuration's, a definition's and a step's.</summary>
        Restrictions,

        /// <summary>The worktree rules' commands, where the ground has them on.</summary>
        Worktree,

        /// <summary>
        /// The commands held off a side-task's agent while a run works over the tree (<see cref="LiveRunHeld"/>), their
        /// listing forms open (<see cref="LiveRunOpen"/>), asked of the run at the call.
        /// </summary>
        LiveRun,
    }

    /// <summary>
    /// The denial of the first rule of <paramref name="group"/> that <paramref name="reading"/> meets as
    /// <paramref name="wanted"/> says - the command is what the rule names, or may be - in that group's own words; null
    /// where none does.
    /// </summary>
    private AgentPolicyAnswer.Denied? Met(RunRules group, RuleReading reading, AgentRuleMatch wanted, string whole, StringComparison comparison)
    {
        if (group is RunRules.Credentials)
        {
            return CredentialPrintingCommands.FirstOrDefault(printing => Meets(printing, reading, everyExpansion: true).Match == wanted) is not { } printing ? null
                : wanted is AgentRuleMatch.Yes
                    ? new AgentPolicyAnswer.Denied(
                        $"Run({printing})",
                        AgentPolicySource.Builtin,
                        $"'{whole}' is denied by Run({printing}), a rule of this tool: it prints a credential, which an agent of this tool is never handed")
                    : MayBe($"Run({printing})", AgentPolicySource.Builtin, whole);
        }

        if (group is RunRules.LiveRun)
        {
            return LiveRunHold(reading, wanted, whole, comparison);
        }

        IEnumerable<(AgentRule Rule, AgentRestriction? Declared)> rules = group switch
        {
            RunRules.OutOfStep => _outOfStepRules.Select(rule => (rule, (AgentRestriction?)null)),
            RunRules.Restrictions => _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Run).Select(pair => (pair.Rule, (AgentRestriction?)pair.Declared)),
            _ => _ground.WorktreeRules ? _worktreeRunRules.Select(rule => (rule, (AgentRestriction?)null)) : [],
        };

        if (rules.FirstOrDefault(each => each.Rule.Match(reading.Words, comparison, reading.Quoted) == wanted) is not { Rule: { } rule } met)
        {
            // git's other commands, in the forms that write what its state-writing verbs do, wherever the option stands - a
            // form the command meets without an expansion the shell splits named first, so the refusal says quoting lets it
            // through only where quoting would.
            return group is RunRules.Worktree && _ground.WorktreeRules
                && GitStateForms.Select(form => (Form: form, Met: Meets(form, reading, everyExpansion: false))).Where(each => each.Met.Match == wanted).OrderBy(each => each.Met.Split).FirstOrDefault() is { Form: { } form } state
                ? wanted is AgentRuleMatch.Expanded
                    ? MayBe($"Run({form})", AgentPolicySource.Builtin, whole) is var maybe && state.Met.Split ? maybe with { Reason = $"{maybe.Reason}; {QuoteTheExpansion}" } : maybe
                    : new AgentPolicyAnswer.Denied($"Run({form})", AgentPolicySource.Builtin, $"'{whole}' is denied by Run({form}), a worktree rule of this tool")
                : null;
        }

        return wanted is AgentRuleMatch.Expanded ? MayBe(rule.Text, met.Declared?.Source ?? AgentPolicySource.Builtin, whole)
            : met.Declared is { } declared ? DeniedBy(declared, whole)
            : group is RunRules.OutOfStep
                ? new AgentPolicyAnswer.Denied(rule.Text, AgentPolicySource.Builtin, $"'{whole}' is denied by {rule.Text}, a rule of this tool: an agent does not leave its step's limits")
                : new AgentPolicyAnswer.Denied(rule.Text, AgentPolicySource.Builtin, $"'{whole}' is denied by {rule.Text}, a worktree rule of this tool");
    }

    /// <summary>
    /// The hold a run working over the tree puts on a side-task's agent: the refusal, naming the run, of a reading that
    /// meets one of the held commands (<see cref="LiveRunHeld"/>, <see cref="GitStateForms"/>) as <paramref name="wanted"/>
    /// says - and is not, for certain, one of their forms that only read (<see cref="LiveRunOpen"/>,
    /// <see cref="CommandAnalysis.GitReadsOnly"/>) - while a run's owner is live. Null where the
    /// ground is no side-task's, the reading meets none, or no run is live: the run is asked only where a command would
    /// be held, at each call, so a run that starts while the chat is open is held to from its first step and one that
    /// ends releases it. The person may do any of it by hand; the agent may not, so that a run nobody watches is never
    /// moved by an agent of a conversation it does not know of.
    /// </summary>
    private AgentPolicyAnswer.Denied? LiveRunHold(RuleReading reading, AgentRuleMatch wanted, string whole, StringComparison comparison)
    {
        var held = _liveRunHeldRules.Any(rule => rule.Match(reading.Words, comparison, reading.Quoted) == wanted);
        var forms = GitStateForms.Select(form => Meets(form, reading, everyExpansion: false)).Where(met => met.Match == wanted).ToList();

        if (_ground is not { SideTask: true, LiveRun: { } liveRun } || !(held || forms.Count > 0))
        {
            return null;
        }

        var open = _liveRunOpenRules.Select(rule => rule.Match(reading.Words, comparison, reading.Quoted)).Append(CommandAnalysis.GitReadsOnly(reading)).ToList();

        if (open.Contains(AgentRuleMatch.Yes) || liveRun() is not { } run)
        {
            return null;
        }

        // A word the shell expands may make it a listing, or a command that writes: what it is cannot be told.
        return new AgentPolicyAnswer.Denied(
            LiveRunRule,
            AgentPolicySource.Builtin,
            wanted is AgentRuleMatch.Yes && !open.Contains(AgentRuleMatch.Expanded)
                ? $"run '{run}' is working over this tree, and '{whole}' writes the git state it works over - its HEAD, index, refs, stash, configuration or worktrees; ask again when it ends, or pause it yourself"
                : $"run '{run}' is working over this tree, and '{whole}' may write the git state it works over: a word of it is one the shell expands, which this does not read; "
                    + (!held && forms.All(met => met.Split) ? $"{QuoteTheExpansion}; " : string.Empty) + "ask again when it ends, or pause it yourself");
    }

    /// <summary>The rule a refusal of a command held while a run works over the tree names (<see cref="LiveRunHeld"/>).</summary>
    public const string LiveRunRule = "a run working over the tree";

    /// <summary>
    /// How <paramref name="reading"/> meets <paramref name="entry"/>, one of <see cref="CredentialPrintingCommands"/> or of
    /// <see cref="GitStateForms"/>: its program by its name and its command word as the entry spells it, in any case - a
    /// <c>*</c> ending the entry's word any word that begins as it does, past that beginning - then each further word of
    /// the entry anywhere among the reading's words after its command, in any order, whatever options stand before or
    /// between them: a long option with its value glued after an <c>=</c> too - and, for git, by any start of its name, as
    /// git takes one - and a short one by any cluster of short options that holds its letter (<c>-at</c> holds
    /// <c>-t</c>). Where no word is a word of the entry's but a word the shell expands may give it, the command may be the
    /// entry: for <paramref name="everyExpansion"/>, any word the shell expands among the command's words after its
    /// command; otherwise only one whose expansion may give the entry's word - one that begins its word, quoted or not
    /// (<c>$OPT</c>, <c>"$O"</c>), one that follows a start the entry's word may have (<c>-f$X</c>, <c>--upd$X</c>), and one
    /// the shell may split into words of their own (<see cref="MaySplit"/>: <c>--depth=$n</c> holding <c>"1 -u"</c>) - never
    /// a quoted one inside a word that begins with what no word of the entry's begins with (<c>--depth="$n"</c>,
    /// <c>"refs/heads/$b:refs/remotes/o/$b"</c>), nor a pattern whose every match begins so (<c>refs/heads/*</c>).
    /// </summary>
    /// <param name="entry">The entry: its program, its command, then the words that make it what it is.</param>
    /// <param name="reading">The command as its rules read it, each word with how it stands to the shell.</param>
    /// <param name="everyExpansion">
    /// Whether every word the shell expands may be a word of the entry's, wherever the expansion stands in it: so for the
    /// commands that print a credential (<see cref="CredentialPrintingCommands"/>), for an unquoted expansion inside a word
    /// is split by the shell where its value holds a blank (<c>--hostname=$H</c> holding <c>" --show-token"</c>), and cmd
    /// reads a percent's value before it reads a quote - a credential printed is the one refusal no reading of where an
    /// expansion stands may narrow; not so for the forms of git's commands that write its state
    /// (<see cref="GitStateForms"/>), where the narrower reading keeps a fetch of a depth or a refspec a quoted variable
    /// names open.
    /// </param>
    /// <returns>
    /// How the reading meets the entry, and whether it may be the entry only by an expansion the shell splits - so quoting
    /// that expansion would let it through, which its refusal says (<see cref="QuoteTheExpansion"/>).
    /// </returns>
    private static (AgentRuleMatch Match, bool Split) Meets(string entry, RuleReading reading, bool everyExpansion)
    {
        var named = entry.Split(' ');
        var words = reading.Words;

        if (words.Count < 2 || !CommandAnalysis.Name(words[0]).Equals(named[0], StringComparison.OrdinalIgnoreCase))
        {
            return (AgentRuleMatch.No, false);
        }

        bool Expands(int at) => CommandAnalysis.Expansion(words[at], reading.QuotedAt(at)) is not null;

        // A command word the shell expands may be any words at all, the entry's whole among them.
        if (Expands(1))
        {
            return (AgentRuleMatch.Expanded, false);
        }

        if (!(named[1].EndsWith('*')
            ? words[1].Length >= named[1].Length && words[1].StartsWith(named[1][..^1], StringComparison.OrdinalIgnoreCase)
            : words[1].Equals(named[1], StringComparison.OrdinalIgnoreCase)))
        {
            return (AgentRuleMatch.No, false);
        }

        var (expanded, split) = (false, true);
        var after = Enumerable.Range(2, words.Count - 2).ToList();

        var git = named[0] == "git";

        foreach (var wanted in named.Skip(2))
        {
            if (after.Any(at => MeetsWord(words[at], wanted, git)))
            {
                continue;
            }

            var begun = everyExpansion ? after.Any(Expands) : after.Any(at => MayGiveWord(words[at], reading.QuotedAt(at), wanted, git));
            var splits = begun || everyExpansion ? [] : after.Select(at => MaySplit(words[at], reading.QuotedAt(at))).Where(kind => kind is not Splitting.None).ToList();

            if (!begun && splits.Count == 0)
            {
                return (AgentRuleMatch.No, false);
            }

            // Quoting lets the command through only where every expansion that may give the word is one quoting keeps whole.
            (expanded, split) = (true, split && !begun && splits.All(kind => kind is Splitting.Unquoted));
        }

        return expanded ? (AgentRuleMatch.Expanded, split) : (AgentRuleMatch.Yes, false);
    }

    /// <summary>
    /// What a refusal says where a command may be one of <see cref="GitStateForms"/> only by an expansion the shell splits
    /// into words of their own: quoting it lets the command through.
    /// </summary>
    public const string QuoteTheExpansion = "an unquoted expansion may be split into a word of its own - quoted (\"$x\"), inside a word that begins otherwise, it is let through";

    /// <summary>
    /// Whether the shell may split an expansion in <paramref name="word"/> into words of their own, any of which may be an
    /// option, and whether quoting it would keep it whole: a variable or a substitution that stands unquoted, which quoting
    /// keeps whole (<see cref="Splitting.Unquoted"/>); and, however it is quoted (<see cref="Splitting.Always"/>), every
    /// positional parameter and every element of an array, by any spelling bash or zsh takes - <c>$@</c>, <c>${@...}</c>,
    /// <c>${!name@}</c>, a <c>[@]</c> inside it, zsh's <c>$name[@]</c> and a <c>${(...)...}</c> flag group or its
    /// <c>${=...}</c>, <c>${^...}</c> and <c>${~...}</c>, each read of the expansion's own text - and cmd's percent and
    /// delayed names, which cmd reads before it reads a quote. A pattern or a brace is no such expansion: each word it gives
    /// begins with what stands before it in its word, so its start alone decides it (<see cref="MayGiveWord"/>); nor is a
    /// tilde, whose value the shell never splits. Each expansion is found as <see cref="CommandAnalysis.Expansion"/> reads
    /// it, the one reader of what the shell expands.
    /// </summary>
    /// <param name="word">The word, its quotes read as the shell reads them.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    private static Splitting MaySplit(string word, string? quoted)
    {
        string? Expands(int from, int to) => CommandAnalysis.Expansion(word[from..to], quoted?[from..to]);

        // A tilde that begins the word is read whole and is never split (TildePrefix).
        for (var from = TildePrefix(word); from < word.Length;)
        {
            // The next expansion: the shortest stretch from here that expands ends in it, the longest end of that stretch
            // that still expands begins it.
            if (Enumerable.Range(from + 1, word.Length - from).FirstOrDefault(to => Expands(from, to) is not null) is not (> 0 and var end))
            {
                return Splitting.None;
            }

            var start = Enumerable.Range(from, end - from).Last(at => Expands(at, end) is not null);

            if (Expands(start, end) is "a variable" or "a substitution" && word[start] != '~')
            {
                if (word[start] is '%' or '!' || (word[start] == '$' && Positional(Referenced(word, start))))
                {
                    return Splitting.Always;
                }

                if (quoted is null || quoted[start] == WordQuoting.Open)
                {
                    return Splitting.Unquoted;
                }
            }

            from = end;
        }

        return Splitting.None;

        // Every positional parameter and every element of an array, quoted or not, by bash's and zsh's spellings.
        static bool Positional(string text)
            => text == "$@" || text.StartsWith("${@", StringComparison.Ordinal) || text.Contains("[@]", StringComparison.Ordinal)
                || (text.StartsWith("${!", StringComparison.Ordinal) && text.EndsWith("@}", StringComparison.Ordinal))
                || text.StartsWith("${(", StringComparison.Ordinal) || text.StartsWith("${=", StringComparison.Ordinal)
                || text.StartsWith("${^", StringComparison.Ordinal) || text.StartsWith("${~", StringComparison.Ordinal)
                || text.StartsWith("$=", StringComparison.Ordinal) || text.StartsWith("$^", StringComparison.Ordinal);
    }

    /// <summary>
    /// How long the tilde that begins <paramref name="word"/> is - to its first separator, as the shell reads its prefix -
    /// or 0 where the word begins with none: read whole, for a shorter start of it would read <c>~+</c> inside
    /// <c>~+x</c>, which the shell reads as no variable's.
    /// </summary>
    private static int TildePrefix(string word)
        => !word.StartsWith('~') ? 0 : word.IndexOfAny(['/', '\\']) is var separator and >= 0 ? separator : word.Length;

    /// <summary>How an expansion in a word may be split into words of their own (<see cref="MaySplit"/>).</summary>
    private enum Splitting
    {
        /// <summary>None of the word's expansions is split.</summary>
        None,

        /// <summary>One is split where it stands unquoted, and quoting it keeps it whole.</summary>
        Unquoted,

        /// <summary>One is split however it is quoted.</summary>
        Always,
    }

    /// <summary>
    /// The text of the reference whose dollar stands at <paramref name="start"/> in <paramref name="word"/>: a braced one to
    /// its closing brace, its braces nested within it counted; <c>$</c> and the special parameter after it; or a name with a
    /// zsh subscript after it (<c>$name[@]</c>) - to the word's end where it is not closed.
    /// </summary>
    private static string Referenced(string word, int start)
    {
        if (start + 1 >= word.Length)
        {
            return word[start..];
        }

        if (word[start + 1] == '{')
        {
            var depth = 0;

            for (var at = start + 1; at < word.Length; at++)
            {
                depth += word[at] switch { '{' => 1, '}' => -1, _ => 0 };

                if (depth == 0)
                {
                    return word[start..(at + 1)];
                }
            }

            return word[start..];
        }

        var end = start + 1;

        if (!(char.IsAsciiLetterOrDigit(word[end]) || word[end] == '_'))
        {
            return word[start..(end + 1)];
        }

        while (end < word.Length && (char.IsAsciiLetterOrDigit(word[end]) || word[end] == '_'))
        {
            end++;
        }

        if (end < word.Length && word[end] == '[' && word.IndexOf(']', end) is > 0 and var close)
        {
            end = close + 1;
        }

        return word[start..end];
    }

    /// <summary>
    /// Whether the shell's expansion of <paramref name="word"/> may give it as a word that is <paramref name="wanted"/>
    /// (<see cref="MeetsWord"/>): where the word expands at all, the text before its first expansion is a start a word
    /// that is <paramref name="wanted"/> may have - none, where the expansion begins the word. Every word an expansion that
    /// is not split gives begins with that text - a quoted variable's, and every match of a pattern, which begins with what
    /// stands before it, so <c>refs/heads/*</c> matches no <c>-u</c> while <c>*</c> may match a file of that name; one the
    /// shell splits may give a word of its own wherever it stands (<see cref="MaySplit"/>). Where the first expansion
    /// begins is read of what <see cref="CommandAnalysis.Expansion"/> reads, the one reader of what the shell expands: the
    /// shortest start of the word that expands ends in that expansion, and the longest end of that start that still
    /// expands begins it.
    /// </summary>
    /// <param name="word">The word, its quotes read as the shell reads them.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    /// <param name="wanted">A word of an entry's after its command.</param>
    /// <param name="git">Whether the entry is git's, which takes a long option by any start of its name.</param>
    private static bool MayGiveWord(string word, string? quoted, string wanted, bool git)
    {
        string? Expands(int from, int to) => CommandAnalysis.Expansion(word[from..to], quoted?[from..to]);

        // A tilde that begins the word is read whole (TildePrefix): one the shell takes from a variable begins the word,
        // and the start of one it does not is the word's own text.
        var tilde = TildePrefix(word);

        if (tilde > 0 && Expands(0, tilde) is not null)
        {
            return true;
        }

        if (Enumerable.Range(tilde + 1, word.Length - tilde).FirstOrDefault(to => Expands(tilde, to) is not null) is not (> 0 and var end))
        {
            return false;
        }

        var before = word[..Enumerable.Range(tilde, end - tilde).Last(from => Expands(from, end) is not null)];

        // A long option: the start of it, or of its name's start before a value glued after an '='; a short one: a dash
        // alone, or a cluster of short options; any other word: the start of it.
        return wanted.StartsWith("--", StringComparison.Ordinal)
            ? before.Split('=', 2) is [var name, _]
                ? name == wanted || (git && name.Length > 2 && wanted.StartsWith(name, StringComparison.Ordinal))
                : wanted.StartsWith(before, StringComparison.Ordinal)
            : wanted.StartsWith('-')
                ? before.Length == 0 || before == "-" || (before[0] == '-' && before[1] != '-')
                : wanted.StartsWith(before, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Whether <paramref name="word"/> is <paramref name="wanted"/>, a word of an entry <see cref="Meets"/> reads after its
    /// command: a long option alike or with its value glued after an <c>=</c> - and, where <paramref name="git"/>, any
    /// start of its name, for git takes one for the option it begins - a short one by any cluster of short options that
    /// holds its letter, any other word alike in any case.
    /// </summary>
    private static bool MeetsWord(string word, string wanted, bool git)
        => wanted.StartsWith("--", StringComparison.Ordinal)
            ? word.Equals(wanted, StringComparison.Ordinal) || word.StartsWith(wanted + "=", StringComparison.Ordinal)
                || (git && word.StartsWith("--", StringComparison.Ordinal) && word.Split('=', 2)[0] is { Length: > 2 } begun && wanted.StartsWith(begun, StringComparison.Ordinal))
            : wanted.StartsWith('-')
                ? word.Length > 1 && word[0] == '-' && word[1] != '-' && word.Contains(wanted[1], StringComparison.Ordinal)
                : word.Equals(wanted, StringComparison.OrdinalIgnoreCase);


    /// <summary>
    /// What a <c>git</c> or <c>gh</c> command calls by a name that is none of the program's own commands, as its rules read
    /// it: gh's first word none of <see cref="GhCommands"/> - an alias or an extension - or <c>gh extension exec</c>; git's
    /// none of <see cref="GitCommands"/> and no program git finds as <c>git-&lt;name&gt;</c> (<see cref="GitProgram"/>) -
    /// an alias. Either runs a command line a configuration file holds - one an agent may have written with any program,
    /// a credential's printing among them - that no rule reads. Null where the command calls none: where its first word is
    /// one the shell expands, which the rules that word may be read as refuse; for git, where the word is no name a
    /// setting of git's may have - a letter, then letters, digits and dashes - so no alias of git's is called by it, or
    /// where the command itself gives the alias it calls (<c>git -c alias.x=... x</c>), read as the command it is where it
    /// is given (<see cref="CommandAnalysis"/>).
    /// </summary>
    /// <param name="reading">The command as its rules read it, each word with how it stands to the shell.</param>
    /// <param name="runs">Whether the program the words begin with runs: the command's own, or one a program that runs its words starts.</param>
    /// <param name="command">The command's own words, whose settings may give the alias it calls.</param>
    private string? CalledByAnotherName(RuleReading reading, bool runs, IReadOnlyList<string> command)
    {
        var words = reading.Words;

        if (!runs || words.Count < 2 || words[1].StartsWith('-') || CommandAnalysis.Expansion(words[1], reading.QuotedAt(1)) is not null)
        {
            return null;
        }

        var program = CommandAnalysis.Name(words[0]);
        var called = words[1];

        if (program.Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            return !GhCommands.Contains(called, StringComparer.Ordinal)
                ? $"'gh {called}', none of gh's own commands: an alias or an extension"
                : called == "extension" && words.Count > 2 && words[2] == "exec"
                    ? "gh extension exec, an extension run"
                    : null;
        }

        return program.Equals("git", StringComparison.OrdinalIgnoreCase)
            && GitSettingName.IsMatch(called)
            && !GitCommands.Contains(called, StringComparer.Ordinal)
            && !command.Any(word => GivesAlias(word, called))
            && !GitProgram(called)
                ? $"'git {called}', none of git's own commands and no program git-{called} git finds: an alias"
                : null;

        // A setting of the alias on the command's own line: -c's, --config-env's, GIT_CONFIG_KEY_n's or GIT_CONFIG_PARAMETERS'.
        static bool GivesAlias(string word, string called)
        {
            var at = word.IndexOf("alias." + called, StringComparison.OrdinalIgnoreCase);
            var end = at + "alias.".Length + called.Length;

            return at >= 0 && (end == word.Length || word[end] is '=' or '\'' or '"');
        }
    }

    /// <summary>A name a setting of git's may have, an alias's among them: a letter, then letters, digits and dashes.</summary>
    private static readonly System.Text.RegularExpressions.Regex GitSettingName = LinearRegex.Compile("^[A-Za-z][A-Za-z0-9-]*$");

    /// <summary>
    /// What a <c>gh</c> command reaches that merges or starts CI, as the worktree rules refuse it:
    /// an <c>api</c> endpoint of <see cref="GhApiEndpoints"/> or a mutation of
    /// <see cref="GhApiMutations"/>, a GraphQL query read from a file, or an alias made; null where it reaches none. A
    /// command named by none of <see cref="GhCommands"/>, and an extension run, are refused on every ground
    /// (<see cref="CalledByAnotherName"/>).
    /// </summary>
    /// <param name="reading">The command as its rules read it, each word with how it stands to the shell.</param>
    /// <param name="runs">
    /// Whether the gh the words begin with runs: the command's own program, or a word past a program that runs the words
    /// after it (<see cref="CommandAnalysis.CommandRunners"/>); among any other program's words (<c>git grep gh src</c>,
    /// <c>Get-Command gh -ErrorAction SilentlyContinue</c>) a gh is that program's data, and its next word no command of gh's.
    /// </param>
    private static string? MergesOrStartsCi(RuleReading reading, bool runs)
    {
        var words = reading.Words;

        if (words.Count < 2 || !CommandAnalysis.Name(words[0]).Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An alias stands for any command, a refused one among them, under a name no rule knows; an extension run by
        // exec is a command no rule here reads.
        if (words[1].Equals("alias", StringComparison.OrdinalIgnoreCase) && words.Count > 2 && words[2] is "set" or "import")
        {
            return $"gh alias {words[2]}, a name for any command";
        }

        if (!words[1].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arguments = words.Skip(1).ToList();
        var argumentQuoted = reading.Quoted.Skip(1).ToList();
        var writes = CommandAnalysis.GhApiWrites(arguments);

        foreach (var word in words.Skip(2))
        {
            // The path a request reaches ends at its query or its fragment, whichever comes first: gh's client sends no
            // fragment, so 'pulls/1/merge#x' is the merge.
            var path = word.Split(['?', '#'], 2)[0].TrimEnd('/');

            if (CiEndpoint.IsMatch(path))
            {
                return $"the endpoint '{word}'";
            }

            if (writes && WrittenEndpoint.IsMatch(path))
            {
                return $"the endpoint '{word}', written";
            }

            if (GhApiMutations.FirstOrDefault(mutation => word.Contains(mutation, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                return $"the mutation {named}";
            }
        }

        // An endpoint the shell expands may be any endpoint, a merge's among them - read so only where the shell would
        // expand it, by the word the endpoint stands in: an endpoint named in quotes is an endpoint of that spelling. Its
        // query string is never stripped to read it, for a quoted one is part of the path it names.
        if (CommandAnalysis.GhApiEndpoint(arguments, argumentQuoted) is (var endpoint, var endpointQuoted)
            && CommandAnalysis.Expansion(endpoint, endpointQuoted) is { } expansion)
        {
            return $"an endpoint named by {expansion} ('{endpoint}'), which this does not read";
        }

        // A GraphQL query this does not read may be any mutation: one read from a file, or one the
        // shell expands where its field stands.
        if (words.Any(word => word.Equals("graphql", StringComparison.OrdinalIgnoreCase)))
        {
            if (words.Any(word => word == "--input" || word.StartsWith("--input=", StringComparison.Ordinal) || word.Contains("=@", StringComparison.Ordinal)))
            {
                return "a GraphQL query read from a file, which this does not read";
            }

            if (CommandAnalysis.GhApiFields(arguments, argumentQuoted).FirstOrDefault(field => field.Field.StartsWith("query=", StringComparison.OrdinalIgnoreCase)
                    && CommandAnalysis.Expansion(field.Field, field.Quoted) is not null) is { Field: { } query })
            {
                return $"a GraphQL query the shell expands ('{query}'), which this does not read";
            }
        }

        return null;
    }

    /// <summary>A REST path that merges or starts CI: an endpoint of <see cref="GhApiEndpoints"/> at a path's end, its <c>&lt;n&gt;</c> any one segment.</summary>
    private static readonly System.Text.RegularExpressions.Regex CiEndpoint = EndpointsAtAPathsEnd(GhApiEndpoints);

    /// <summary>A REST path that pushes, opens a pull request or makes a release where the request writes: an endpoint of <see cref="GhApiWriteEndpoints"/>.</summary>
    private static readonly System.Text.RegularExpressions.Regex WrittenEndpoint = EndpointsAtAPathsEnd(GhApiWriteEndpoints);

    /// <summary>
    /// The paths that end in one of <paramref name="endpoints"/>, any case: an <c>&lt;n&gt;</c> any one segment, and an
    /// endpoint that ends in <c>/**</c> any path beneath it too.
    /// </summary>
    private static System.Text.RegularExpressions.Regex EndpointsAtAPathsEnd(IReadOnlyList<string> endpoints)
        => LinearRegex.Compile(
            $"(^|/)({string.Join('|', endpoints.Select(endpoint => endpoint.EndsWith("/**", StringComparison.Ordinal)
                ? System.Text.RegularExpressions.Regex.Escape(endpoint[..^3]).Replace("<n>", "[^/]+", StringComparison.Ordinal) + "(/.*)?"
                : System.Text.RegularExpressions.Regex.Escape(endpoint).Replace("<n>", "[^/]+", StringComparison.Ordinal)))})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// The root action <paramref name="word"/> names, in the list's own spelling: the word itself,
    /// each word its braces spell, and each word a script inside it would run - a command another
    /// program runs from one argument (<c>ssh host 'sudo x'</c>) is read so too; null where it names
    /// none. <paramref name="braces"/> says whether the shell expands its braces: a here-document's text it never does.
    /// </summary>
    private static string? RootActionOf(string word, bool braces)
    {
        // A word whose braces pair is asked as its pattern: whether any word it spells names a root action, in time
        // linear in the word however many it spells ({a,b} forty times is forty choices).
        if (braces && WordPattern.Braces(word) is { } pattern)
        {
            if (RootActions.FirstOrDefault(root => pattern.MayNameProgram(root, CommandAnalysis.ProgramEnds)) is { } spelled)
            {
                return spelled;
            }

            braces = false;
        }

        IEnumerable<string> candidates = braces ? CommandAnalysis.BraceExpansions(word) : [word];

        if (word.Any(char.IsWhiteSpace))
        {
            candidates = candidates.Concat(word.Split([' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '`', '\'', '"', '$', '{', '}'], StringSplitOptions.RemoveEmptyEntries));
        }

        foreach (var candidate in candidates)
        {
            var name = CommandAnalysis.Name(candidate);

            if (RootActions.FirstOrDefault(root => root.Equals(name, StringComparison.OrdinalIgnoreCase)) is { } action)
            {
                return action;
            }
        }

        return null;
    }

    /// <summary>One web request, decided: no web where the step has none, then the <c>Web</c> rules, then <c>webDomains</c> where <paramref name="domains"/>.</summary>
    /// <param name="address">The address as it was named.</param>
    /// <param name="noWebSubject">What the no-web sentence names beside the address: the address itself for a tool's ask, the command for a fetcher.</param>
    /// <param name="domains">Whether <c>webDomains</c> holds the address: a page's, never a search source's.</param>
    /// <param name="agentRules">Whether an agent's own <c>Web</c> rules hold it beside the configuration's: every request's but an index's fill.</param>
    private AgentPolicyAnswer WebDecided(string address, string noWebSubject, bool domains, bool agentRules)
    {
        if (_reporterRoot is not null)
        {
            return new AgentPolicyAnswer.Denied(
                "the reporter's reads",
                AgentPolicySource.Builtin,
                "this agent reads and nothing else: it is the run's reporter, offered the tools that read and no other");
        }

        if (!_ground.Web)
        {
            return new AgentPolicyAnswer.Denied(
                "the web capability",
                AgentPolicySource.Builtin,
                $"'{noWebSubject}' fetches '{address}' and this step has no web");
        }

        var host = HostOf(address);

        foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Web && (agentRules || pair.Declared.Source != AgentPolicySource.Agent)))
        {
            if (rule.MatchesHost(host))
            {
                return DeniedBy(declared, host);
            }
        }

        if (domains && _webDomains.Count > 0 && !_webDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
        {
            return new AgentPolicyAnswer.Denied(
                "agentPolicy.webDomains",
                AgentPolicySource.Config,
                $"'{host}' is outside webDomains of agentPolicy: {string.Join(", ", _webDomains)}");
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>
    /// The host an address names: a URL's own host, whatever its scheme, or the first stretch of a
    /// bare <c>host[:port][/path]</c>, as a fetcher command names it. An address that names no host
    /// at all cannot be decided, so it is denied by the caller that catches.
    /// </summary>
    private static string HostOf(string address)
    {
        var text = address.Trim();

        if (SlashedApart(text))
        {
            throw new ArgumentException($"'{address}' spells the slashes after its scheme as readers of an address read apart, so its host is not one this can name", nameof(address));
        }

        foreach (var candidate in text.Contains("://", StringComparison.Ordinal) ? [text] : new[] { "https://" + text })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Host.Length > 0)
            {
                // A host spelled beyond ASCII reaches the one IDNA maps it to, which readers map
                // each their own way - a browser's and curl's fold full-width letters and drop a
                // soft hyphen, this tool's .NET, reading without a culture, neither - so the host
                // one of them reaches is not one this can name. Its ASCII form ('xn--') is.
                if (!System.Text.Ascii.IsValid(uri.Host))
                {
                    throw new ArgumentException($"'{address}' spells its host beyond ASCII, which readers of an address map each their own way, so its host is not one this can name: spell it as its xn-- form", nameof(address));
                }

                // A host may be spelled with its root dot ('pastebin.com.'), which is the same
                // host: trimmed here, so a rule written without it meets it.
                return uri.Host.Trim('[', ']').TrimEnd('.');
            }
        }

        throw new ArgumentException($"'{address}' names no host", nameof(address));
    }

    /// <summary>
    /// Whether <paramref name="text"/> spells the slashes after its scheme other than as two and a host after them, where a
    /// reader of addresses finds a host all the same: any scheme followed by a slash or a backslash - <c>http:/\host</c>,
    /// <c>gopher:/host</c>, <c>http:///host</c>, for curl takes the host past one to three slashes whatever the scheme -
    /// and one of <see cref="SlashedSchemes"/> followed by none (<c>https:host</c>). A browser's reader, .NET's and curl's
    /// each skip such slashes their own way, so the host one of them reaches is not the one a reading of the text would
    /// name.
    /// </summary>
    private static bool SlashedApart(string text)
    {
        var colon = text.IndexOf(':', StringComparison.Ordinal);

        if (colon <= 0 || !char.IsAsciiLetter(text[0]) || !text[..colon].All(character => char.IsAsciiLetterOrDigit(character) || character is '+' or '.' or '-'))
        {
            return false;
        }

        var rest = text.AsSpan(colon + 1);

        if (rest.Length == 0 || rest[0] is not ('/' or '\\'))
        {
            return SlashedSchemes.Contains(text[..colon], StringComparer.OrdinalIgnoreCase);
        }

        return !(rest.Length > 2 && rest[0] == '/' && rest[1] == '/' && rest[2] is not ('/' or '\\'));
    }

    /// <summary>The schemes whose host a reader of addresses finds even with no slash after them (<c>https:host</c>).</summary>
    public static IReadOnlyList<string> SlashedSchemes { get; } = ["http", "https", "ws", "wss", "ftp"];

    /// <summary>
    /// Whether the path, as asked or as resolved, lies in a place no action reaches (<see cref="Unreachable"/>) - a
    /// credential slot of this tool, a Claude Code directory, a place the user keeps credentials in: no file tool of an
    /// agent reads or writes one.
    /// </summary>
    private AgentPolicyAnswer.Denied? Unreached(string asked, string resolved)
    {
        foreach (var form in new[] { asked, resolved })
        {
            foreach (var kind in _unreachable)
            {
                if (kind.Places.Any(place => InsideOrSameOf(place, form, Refusals)))
                {
                    return new AgentPolicyAnswer.Denied(kind.Rule, AgentPolicySource.Builtin, $"'{form}' {kind.Is}: no file tool of an agent reads or writes one");
                }
            }
        }

        return null;
    }

    /// <summary>
    /// One kind of place no action of any agent reaches, as every refusal of one names it - the one list each such refusal
    /// is read from: the credential slots of this tool, the Claude Code directories, the places the user keeps credentials
    /// in.
    /// </summary>
    /// <param name="Places">Its places, resolved.</param>
    /// <param name="Rule">The rule a refusal names.</param>
    /// <param name="What">One of them, as a refusal names it.</param>
    /// <param name="Is">What a path inside one is, as a file tool's refusal says it.</param>
    /// <param name="Named">What a word naming one does, as a command's refusal says it.</param>
    private sealed record Unreachable(IReadOnlyList<string> Places, string Rule, string What, string Is, string Named);

    /// <summary>
    /// The commands that move the commands after them to another directory, by their names in any case: the shell's
    /// <c>cd</c>, <c>pushd</c> and <c>chdir</c>, and PowerShell's <c>Set-Location</c> and <c>Push-Location</c> with their
    /// aliases - <c>sl</c>, and the shell's three names, which PowerShell gives them too. A program that starts what it
    /// runs elsewhere - env's <c>-C</c>, pwsh's <c>-WorkingDirectory</c> - is read as a <c>cd</c> before it by
    /// <see cref="CommandAnalysis"/>, so this is the one reader of every move.
    /// </summary>
    public static IReadOnlyList<string> Movers { get; } = ["cd", "pushd", "chdir", "Set-Location", "sl", "Push-Location"];

    /// <summary>The commands that move the commands after them back to a place a move of the string's left: the shell's <c>popd</c>, PowerShell's <c>Pop-Location</c>.</summary>
    private static readonly string[] Returners = ["popd", "Pop-Location"];

    /// <summary>
    /// Every directory a command moves the commands after it to (<see cref="Movers"/>), spelled as a path asked is, against
    /// every place the command may stand in (<see cref="Standing"/>), a drive's two readings both places a <c>cd</c> may
    /// have gone: each place a spelling of each word it may go to may reach - every word no option, every
    /// word after a <c>--</c>, and a PowerShell parameter's value glued after its <c>:</c> (<c>-Path:x</c>), so a
    /// <c>-StackName</c>'s value is one more place rather than its target none - the home where it names none, a shell's
    /// <c>cd</c> going to the <c>HOME</c> the command or the string sets where one does - and none where it is no such
    /// command. The shell's <c>cd</c> looks a relative name up in each directory <c>CDPATH</c> holds
    /// too - the string's own, the command's, or the one the agent inherits - and <c>cd -</c> goes back to
    /// <c>OLDPWD</c>'s: the string's own, or - <c>Back</c> - any place already reached where a move of the string's went
    /// before it; a move to a directory the shell expands - a variable it does not know, a substitution, a pattern - may
    /// go to any place reached as well (<c>Back</c>). Null where it is no move.
    /// </summary>
    /// <param name="command">The command's words, as one reading of it spells them.</param>
    /// <param name="places">Every place the command may stand in.</param>
    /// <param name="state">What the string set before this command.</param>
    /// <exception cref="NotSupportedException">
    /// Its <c>CDPATH</c>, <c>OLDPWD</c> or <c>HOME</c> is one the string sets to a value it computes, or <c>cd -</c> goes back to a place
    /// the shell stood in before the string, which this cannot tell.
    /// </exception>
    private (IReadOnlyList<PlaceForm> To, bool Back)? MovedTo(IReadOnlyList<string> command, IReadOnlyList<PlaceForm> places, StringState state)
    {
        var environment = CommandAnalysis.EnvironmentOf(command);

        if (environment == command.Count || !Movers.Contains(CommandAnalysis.Name(command[environment]), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var goesBack = false;

        var targets = new List<string>();
        var ended = false;

        foreach (var word in command.Skip(environment + 1))
        {
            if (!ended && word == "--")
            {
                ended = true;
            }
            else if (ended || word == "-" || !word.StartsWith('-'))
            {
                targets.Add(word);
            }
            else if (word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && colon + 1 < word.Length)
            {
                targets.Add(word[(colon + 1)..]);
            }
        }

        // A variable the move reads: the command's own assignment of it, else the string's.
        IReadOnlyList<string>? Value(string name)
        {
            if (command.Take(environment).LastOrDefault(word => CommandAnalysis.AssignedName(word) == name) is { } own)
            {
                var value = own.Split('=', 2)[1];

                return CommandAnalysis.Expansion(value) is null ? [value] : throw new NotSupportedException($"'{own}' sets {name} to a value it computes, which this does not read");
            }

            return state.Unknown.Contains(name)
                ? throw new NotSupportedException($"{name} is set by the string to a value it computes, which this does not read")
                : state.Spelled(name);
        }

        var going = new List<string>();
        var shells = CommandAnalysis.Name(command[environment]) is var mover && (mover.Equals("cd", StringComparison.OrdinalIgnoreCase) || mover.Equals("pushd", StringComparison.OrdinalIgnoreCase) || mover.Equals("chdir", StringComparison.OrdinalIgnoreCase));

        // A shell's cd given no directory goes to HOME: the command's own or the string's, where either sets it.
        foreach (var target in targets.Count == 0 ? (shells && Value("HOME") is { } home ? home : ["~"]) : targets)
        {
            if (target == "-")
            {
                // Back where the shell stood before: OLDPWD's, or a place a move of the string's already reached.
                if (Value("OLDPWD") is { } back)
                {
                    going.AddRange(back);
                }
                else if (!state.Moved)
                {
                    throw new NotSupportedException("'cd -' goes back to where the shell stood before this command, which this does not know");
                }
                else
                {
                    goesBack = true;
                }

                continue;
            }

            going.Add(target);

            // A directory the shell expands may be any: the move may go back to any place reached, as well as where it spells.
            goesBack |= CommandAnalysis.Expansion(HomeExpanded(target)) is not null;

            if (shells && LooksUp(target) && (Value("CDPATH") ?? (_cdPath is { Length: > 0 } inherited ? [inherited] : null)) is { } searched)
            {
                going.AddRange(searched.SelectMany(CdPathEntries).Select(entry => Path.Join(entry, target)));
            }
        }

        var moved = new List<PlaceForm>();

        foreach (var target in going)
        {
            // A move held as a core goes to the places its choice may be (<see cref="FormsOf"/>), never the core's text.
            if (Templated(target) is { } pattern)
            {
                moved.AddRange(FormsOf(pattern, places));
                continue;
            }

            try
            {
                moved.AddRange(Reached(places, target, pattern: true));
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                // A word no place can be told by moves nowhere this reads.
            }
        }

        return (PlaceForm.Union(moved), goesBack);

        // A name cd looks up in CDPATH: one that begins with no root, no home, no expansion and no '.' or '..' of its own.
        static bool LooksUp(string target)
            => target.Length > 0 && target[0] is not '/' and not '\\' and not '~' and not '$' && !(target.Length > 1 && target[1] == ':')
                && target.Split(['/', '\\'])[0] is not "." and not "..";

        // CDPATH's directories, split at its colons - a drive's letter kept with its path - or its semicolons.
        static IEnumerable<string> CdPathEntries(string value)
        {
            var parts = value.Split(value.Contains(';', StringComparison.Ordinal) ? ';' : ':');

            for (var at = 0; at < parts.Length; at++)
            {
                var part = parts[at].Length == 1 && char.IsAsciiLetter(parts[at][0]) && at + 1 < parts.Length ? parts[at] + ":" + parts[++at] : parts[at];

                if (part.Length > 0)
                {
                    yield return part;
                }
            }
        }
    }

    /// <summary>
    /// Whether <paramref name="word"/> names a place no action reaches (<see cref="Unreachable"/>) - a credential slot, a
    /// Claude Code directory, a place the user keeps credentials in - a path beneath one, or a directory that holds one, resolved as a tool's path resolves against each place the command may stand in: every
    /// path the word may name (<see cref="CommandAnalysis.PathsNamed"/>) - itself, a redirect's file or text among them,
    /// a value after an <c>=</c>, an <c>@</c> or a <c>&lt;</c>, a short option's glued value, a PowerShell parameter's
    /// after its <c>:</c>, each word of a script inside it, each word its braces spell where <paramref name="braces"/>
    /// says the shell expands them, a file URL's path - a home expanded in each, the shell's tildes of a variable only
    /// where it expands them (<see cref="NamedPath.TildeExpands"/>), and a pattern by every path it may match
    /// (<paramref name="dotNames"/> saying whether it matches a dot-name). A directory that holds one is refused
    /// where the word gives it whole and <paramref name="holds"/> says the command may read what it names, for a program
    /// handed a directory may walk it, the slot's files and all - an archiver, a copy, a search - but for what it holds
    /// through the agent's working directory, where it is that directory or one above it: every command over the tree
    /// names it (<c>.</c>) and every program given no path works over it (<see cref="ThroughWorkingDirectory"/>); a value
    /// glued to a short option or a word of a script's text is refused only where it is a slot or a path in one.
    /// </summary>
    /// <param name="word">The word, or a redirect's file or text.</param>
    /// <param name="quoted">
    /// How each of the word's characters stands to the shell (<see cref="WordQuoting"/>), or null where its spelling was
    /// not kept.
    /// </param>
    /// <param name="places">Every directory the word may be read against.</param>
    /// <param name="braces">Whether the shell expands the word's braces.</param>
    /// <param name="dotNames">Whether the command's patterns match a dot-name (<see cref="PatternsMatchDotNames"/>).</param>
    /// <param name="holds">
    /// Whether a directory the word names whole is refused for what it holds: false for a word that reads nothing there -
    /// a move's own (<see cref="Movers"/>, <see cref="CommandAnalysis.MoveWords"/>), each path after it read where it goes;
    /// a program's that names, tests or lists what it is given (<see cref="CommandAnalysis.NamesOnly"/>); where the program
    /// writes (<see cref="CommandAnalysis.WriteTargets"/>).
    /// </param>
    /// <param name="pattern">Whether the shell that reads the word expands its pattern: a quoted star is a star.</param>
    private AgentPolicyAnswer.Denied? Named(string word, string? quoted, IReadOnlyList<string> places, bool braces, bool dotNames, bool holds, bool pattern)
    {
        foreach (var (named, place, spelled) in NamedAgainst(word, quoted, places.Distinct().ToList(), braces, dotNames))
        {
            IReadOnlyList<string> readings;

            // A place a pattern of a move reached is a pattern itself, whatever the word's own quotes.
            var globbed = pattern || place.IndexOfAny(['*', '?', '[']) >= 0;

            try
            {
                readings = CommandSpelled(named.Path, place, globbed, named.TildeExpands);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                // A word that is no path at all ('--porcelain') is every command's ordinary argument.
                continue;
            }

            // A pattern the shell expands names every path it may match: refused where one of them may be a slot or a
            // Claude Code directory, lie beneath one, or - given whole - be a directory that holds one.
            foreach (var asked in readings.Where(reading => globbed && reading.IndexOfAny(['*', '?', '[']) >= 0))
            {
                foreach (var (unreachable, rule, what, _, _) in _unreachable)
                {
                    if (unreachable.FirstOrDefault(unreached => GlobReaches(asked, unreached, dotNames)) is not null)
                    {
                        return new AgentPolicyAnswer.Denied(rule, AgentPolicySource.Builtin, $"'{spelled}' may name {what} by its pattern: no command of an agent touches one");
                    }

                    if (holds && named.Whole && unreachable.FirstOrDefault(unreached => GlobHolds(asked, unreached, dotNames)) is { } held)
                    {
                        return new AgentPolicyAnswer.Denied(
                            rule,
                            AgentPolicySource.Builtin,
                            $"'{spelled}' may name a directory that holds {what}, '{held}', by its pattern: a command over it may read one, and no command of an agent touches one");
                    }
                }
            }

            foreach (var form in readings.SelectMany(asked => new[] { asked, Resolved(asked) }))
            {
                if (_unreachable.FirstOrDefault(kind => kind.Places.Any(place => PathContainment.IsInsideOrSame(place, form, Refusals))) is { } inside)
                {
                    return new AgentPolicyAnswer.Denied(
                        inside.Rule,
                        AgentPolicySource.Builtin,
                        $"'{spelled}' {inside.Named}: no command of an agent touches one");
                }

                if (!holds || !named.Whole)
                {
                    continue;
                }

                foreach (var (unreachable, rule, what, _, _) in _unreachable)
                {
                    if (unreachable.FirstOrDefault(unreached => PathContainment.IsStrictlyInside(form, unreached, Refusals) && !ThroughWorkingDirectory(form, unreached)) is { } held)
                    {
                        return new AgentPolicyAnswer.Denied(
                            rule,
                            AgentPolicySource.Builtin,
                            $"'{spelled}' names '{form}', which holds {what}, '{held}': a command over it may read one, and no command of an agent touches one");
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Each path <paramref name="word"/> names (<see cref="CommandAnalysis.PathsNamed"/>) with each place it is read
    /// against, and the word a refusal names the path by: the word itself, or - for a word held as a core - the decisive
    /// spelling that decided it, a real place never the core's text. A word whose braces pair is decided by its pattern
    /// (<see cref="Decisive"/>), never spelled out: its own readings, then against each place the spellings that decide
    /// it - <c>{a,b}</c> written forty times is decided by one spelling, not 2^40, and a word that may spell a shell
    /// pattern by a witness per guarded place. Every word whose braces do not pair has its spellings read each, one at a
    /// time.
    /// </summary>
    private IEnumerable<(NamedPath Named, string Place, string Spelled)> NamedAgainst(string word, string? quoted, IReadOnlyList<string> places, bool braces, bool dotNames)
    {
        // A word held as a core - one text standing for every alternative of a choice - is decided by its pattern, the
        // core mapped back to the choice (<see cref="Templated"/>): its spellings that decide it, never the core's text.
        if (Templated(word) is { } core)
        {
            var unreachable = _unreachable.SelectMany(kind => kind.Places).ToList();

            foreach (var place in places)
            {
                foreach (var (spelling, mask) in Decisive(core, place, unreachable, dotNames))
                {
                    foreach (var named in CommandAnalysis.PathsNamed(spelling, braces: false, quoted: mask))
                    {
                        yield return (named, place, spelling);
                    }
                }
            }

            yield break;
        }

        if (braces && word.Contains('{', StringComparison.Ordinal) && WordPattern.Braces(word, quoted) is { } spelled)
        {
            var unreachable = _unreachable.SelectMany(kind => kind.Places).ToList();

            foreach (var named in CommandAnalysis.PathsNamed(word, braces: false, quoted: quoted))
            {
                foreach (var place in places)
                {
                    yield return (named, place, word);
                }
            }

            foreach (var place in places)
            {
                foreach (var (spelling, mask) in Decisive(spelled, place, unreachable, dotNames))
                {
                    foreach (var named in CommandAnalysis.PathsNamed(spelling, braces: false, quoted: mask))
                    {
                        yield return (named, place, spelling);
                    }
                }
            }

            yield break;
        }

        foreach (var named in CommandAnalysis.PathsNamed(word, braces, quoted: quoted))
        {
            foreach (var place in places)
            {
                yield return (named, place, word);
            }
        }
    }

    /// <summary>
    /// The words <paramref name="word"/>'s braces spell, read against <paramref name="place"/> by a decision that compares
    /// what they name with <paramref name="matters"/>: the spellings that decide it (<see cref="Decisive"/>) - the names
    /// that matter where the word is plainly spelled, a witness per guarded place where it may spell a pattern - every
    /// spelling, one at a time, where neither reads it, and the word alone where its braces spell nothing.
    /// </summary>
    private IEnumerable<string> SpelledAgainst(string word, string place, IReadOnlyList<string> matters, bool dotNames)
    {
        // A word held as a core is decided by the pattern of the paths it may name, never the core's own text.
        if (Templated(word) is { } core)
        {
            return Decisive(core, place, matters, dotNames).Select(spelling => spelling.Word);
        }

        if (!word.Contains('{', StringComparison.Ordinal) || CommandAnalysis.BraceGroup(word) is null)
        {
            return [word];
        }

        return WordPattern.Braces(word) is { } pattern
            ? Decisive(pattern, place, matters, dotNames).Select(spelling => spelling.Word)
            : CommandAnalysis.BraceExpansions(word);
    }

    /// <summary>
    /// <paramref name="pattern"/>'s segments - each with the separators before it - where every spelling of the word is a
    /// plain path that names itself alone: no character of it quoted; its own text of names and separators, but a home's
    /// <c>~</c> leading it and a drive's colon after a first letter; its choices spelling nothing but a name's letters,
    /// digits, <c>_</c>, <c>-</c>, <c>+</c> and <c>.</c>, none of them able to begin the word with <c>-</c>, and every
    /// segment that holds one spelling neither nothing, <c>.</c>, <c>..</c> nor a name ending in a dot; and no <c>..</c>
    /// after the first such segment. Such a word is read by <see cref="CommandAnalysis.PathsNamed"/> as itself alone, and
    /// what decides each spelling is which names its segments spell (<see cref="SpellingsThatDecide"/>). Null for any other
    /// word.
    /// </summary>
    private static IReadOnlyList<(string Before, WordPattern Piece)>? PlainlySpelled(string word, string? quoted, WordPattern pattern)
    {
        static bool Named(char character) => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '+' or '.';
        static bool Separator(char character) => character is '/' or '\\';

        if (CommandAnalysis.ClassesOf(word, quoted).Any(character => character != WordQuoting.Open)
            || !pattern.ChoicesSpellOnly(Named)
            || pattern.Matcher().MayBeginWith(character => character == '-'))
        {
            return null;
        }

        var at = 0;

        foreach (var text in pattern.Parts.OfType<WordPart.Text>().Select(part => part.Spelled))
        {
            foreach (var character in text)
            {
                var allowed = Named(character) || Separator(character)
                    || (character == '~' && at == 0 && pattern.Parts[0] is WordPart.Text { Spelled: [_, '/' or '\\', ..] })
                    || (character == ':' && at == 1 && pattern.Parts[0] is WordPart.Text { Spelled.Length: > 1 } first && char.IsAsciiLetter(first.Spelled[0]));

                if (!allowed)
                {
                    return null;
                }

                at++;
            }
        }

        if (word.StartsWith('-'))
        {
            return null;
        }

        var segments = pattern.CutAt(Separator);
        var chosen = false;

        foreach (var (_, piece) in segments)
        {
            if (!piece.Chooses)
            {
                if (chosen && piece.Parts is [WordPart.Text { Spelled: ".." }])
                {
                    return null;
                }

                continue;
            }

            var matcher = piece.Matcher();

            if (matcher.Spells(string.Empty) || matcher.Spells(".") || matcher.Spells("..") || matcher.MayEndWith(character => character == '.'))
            {
                return null;
            }

            chosen = true;
        }

        return segments;
    }

    /// <summary>
    /// The spellings of a plainly spelled word (<see cref="PlainlySpelled"/>) that decide it against
    /// <paramref name="place"/>, for a decision that compares the paths it names with <paramref name="matters"/> - the
    /// places no command reaches, or those no command removes - and with what the disk holds: every other spelling is
    /// decided as one of them is. Built a segment at a time from the spelled ways so far, each taking at a segment that
    /// chooses: one spelling of it where the way stands nowhere a name could matter - within one of those places, where
    /// every way on is decided alike, or in a directory that is not there and above none of them, where none reaches one -
    /// and otherwise each name of the next segment of such a place below it, each name in the directory, that the segment
    /// spells, and one spelling that is none of them (<see cref="WordPattern.WordMatcher.SpellingBesides"/>): a name no place
    /// and no entry has is alike to the decision whichever it is, the decision comparing names and nothing else. The ways
    /// are as many as the names that matter, never the product of every choice.
    /// </summary>
    private IEnumerable<(string Word, string Quoted)> SpellingsThatDecide(IReadOnlyList<(string Before, WordPattern Piece)> segments, string place, IReadOnlyList<string> matters)
    {
        var ways = new List<(string Word, string Quoted)> { (string.Empty, string.Empty) };

        foreach (var (before, piece) in segments)
        {
            var mask = MaskOf(piece);

            if (!piece.Chooses)
            {
                var spelled = string.Concat(piece.Parts.OfType<WordPart.Text>().Select(part => part.Spelled));
                ways = [.. ways.Select(way => (
                    way.Word + before + spelled,
                    way.Quoted + new string(WordQuoting.Open, before.Length) + (mask.Length == spelled.Length ? mask : new string(WordQuoting.Open, spelled.Length))))];
                continue;
            }

            var matcher = piece.Matcher();
            var next = new HashSet<(string, string)>();

            foreach (var way in ways)
            {
                foreach (var name in NamesThatDecide(way.Word + before, piece, matcher, place, matters))
                {
                    next.Add((way.Word + before + name, way.Quoted + new string(WordQuoting.Open, before.Length + name.Length)));
                }
            }

            ways = [.. next];
        }

        return ways;
    }

    /// <summary>How each character of a segment's fixed text stands to the shell, where all of it stands one way; empty where it stands none.</summary>
    private static string MaskOf(WordPattern piece)
    {
        var mask = string.Empty;

        foreach (var part in piece.Parts.OfType<WordPart.Text>())
        {
            mask += part.Quoted.Length >= part.Spelled.Length ? part.Quoted : new string(WordQuoting.Open, part.Spelled.Length);
        }

        return mask;
    }

    /// <summary>
    /// The names a segment (<paramref name="piece"/>, asked through <paramref name="matcher"/>) is spelled as after
    /// <paramref name="directory"/>, read against <paramref name="place"/>, that decide the spellings through it
    /// (<see cref="SpellingsThatDecide"/>). A directory that cannot be listed hides which of its names matter, so there
    /// every spelling of the segment is one.
    /// </summary>
    private IEnumerable<string> NamesThatDecide(string directory, WordPattern piece, WordPattern.WordMatcher matcher, string place, IReadOnlyList<string> places)
    {
        var first = piece.Spellings().First().Word;
        IReadOnlyList<string> forms;

        try
        {
            forms = [.. CommandSpelled(directory.Length == 0 ? "." : directory, place, pattern: false)
                .SelectMany(reading => new[] { reading, Resolved(reading) })
                .Distinct(StringComparer.OrdinalIgnoreCase)];
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException or NotSupportedException)
        {
            // A way that is no path at all is no path whichever name follows.
            return [first];
        }

        if (forms.Any(form => places.Any(unreached => PathContainment.IsInsideOrSame(unreached, form, Refusals))))
        {
            return [first];
        }

        // Every name kept in its own case, two that differ in case alone both: on a system that tells them apart each is
        // an entry of its own.
        var names = new HashSet<string>(StringComparer.Ordinal);
        var matters = false;

        foreach (var form in forms)
        {
            foreach (var unreached in places.Where(unreached => PathContainment.IsStrictlyInside(form, unreached, Refusals)))
            {
                matters = true;

                if (BeneathOf(form, unreached, Refusals) is { } relative && relative.Split('/')[0] is { Length: > 0 } below && matcher.Spells(below))
                {
                    names.Add(below);
                }
            }

            if (_fileSystem.DirectoryExists(form))
            {
                matters = true;
                IReadOnlyList<string> entries;

                try
                {
                    entries = [.. _fileSystem.EnumerateDirectories(form).Concat(_fileSystem.EnumerateFiles(form, recursive: false)).Select(entry => Path.GetFileName(entry))];
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    return piece.Spellings().Select(spelling => spelling.Word);
                }

                names.UnionWith(entries.Where(entry => entry.Length > 0 && matcher.Spells(entry)));
            }
        }

        if (!matters)
        {
            return [first];
        }

        return matcher.SpellingBesides(names) is { } besides ? [.. names, besides] : [.. names];
    }

    /// <summary>
    /// Whether <paramref name="directory"/> holds <paramref name="held"/> only through the agent's working directory: it is
    /// that directory or one above it, as given or as it resolves, and <paramref name="held"/> lies within the working
    /// directory. Every command over the tree names the working directory (<c>.</c>) and every program given no path works
    /// over it, and a directory above it reaches what it holds through it alone - so naming one whole is never refused for
    /// that: a refusal of it would hold back no program that reads it by no word at all.
    /// </summary>
    private bool ThroughWorkingDirectory(string directory, string held)
        => new[] { Whole(_ground.WorkingDirectory), _workingRoot }.Any(root
            => PathContainment.IsInsideOrSame(directory, root, Refusals) && PathContainment.IsInsideOrSame(root, held, Refusals));

    /// <summary>
    /// Whether the pattern <paramref name="pattern"/> may match a directory that holds <paramref name="place"/> - one of its
    /// parents - but through the agent's working directory (<see cref="ThroughWorkingDirectory"/>).
    /// </summary>
    private bool GlobHolds(string pattern, string place, bool dotNames)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(place));

        for (; parent is not null && Path.GetDirectoryName(parent) is not null; parent = Path.GetDirectoryName(parent))
        {
            if (!ThroughWorkingDirectory(parent, place) && GlobReaches(pattern, parent, dotNames, exactly: true))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The word of <paramref name="command"/> that would start or schedule as the root user -
    /// <c>wsl -u root</c> and its long spelling, <c>schtasks</c> with <c>/ru</c> - or null.
    /// </summary>
    private static string? NamesRootUser(IReadOnlyList<string> command, StringComparison comparison)
    {
        for (var at = 0; at < command.Count; at++)
        {
            if (string.Equals("wsl", CommandAnalysis.Name(command[at]), comparison))
            {
                for (var option = at + 1; option < command.Count; option++)
                {
                    var word = command[option];

                    if (option + 1 < command.Count
                        && (string.Equals(word, "-u", comparison) || string.Equals(word, "--user", comparison))
                        && string.Equals(command[option + 1], "root", comparison))
                    {
                        return "wsl";
                    }

                    if (word.StartsWith("--user=", StringComparison.Ordinal)
                        && string.Equals(word["--user=".Length..], "root", comparison))
                    {
                        return "wsl";
                    }
                }
            }

            if (string.Equals("schtasks", CommandAnalysis.Name(command[at]), comparison)
                && command.Any(word => word.Equals("/ru", StringComparison.OrdinalIgnoreCase)))
            {
                return "schtasks";
            }
        }

        return null;
    }

    /// <summary>
    /// One path decision, made on the path as asked and as resolved, each spelled whole: a
    /// restriction is tried on both, so no path reaches through a link past a rule that was written
    /// for where it lands - and on each place a spelling of it may reach, allowed only where every
    /// one of them is. A path this cannot read, or one blank, is undecided.
    /// </summary>
    /// <param name="path">The path as the tool asks it.</param>
    /// <param name="matchers">The patterns the decision matches a path by, which tell its readings apart (<see cref="ReadMatchers"/>, <see cref="WriteMatchers"/>).</param>
    /// <param name="decide">The decision of one spelling of the path, asked and resolved.</param>
    private AgentPolicyAnswer Decide(string path, IReadOnlyList<(DssHarness.Core.Repository.GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers, Func<string, string, AgentPolicyAnswer> decide)
    {
        // What each path this decision asks resolves to, kept for the decision: one disk, read once a path.
        var outerResolutions = _resolutions.Value;
        _resolutions.Value = [];

        // What each path this decision asks is, kept for the decision as well.
        var outerKinds = _kinds.Value;
        _kinds.Value = [];

        // Each path this decision makes whole, kept for the decision as well.
        var outerWholes = _wholes.Value;
        _wholes.Value = [];

        try
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    throw new ArgumentException("it names no path");
                }

                foreach (var asked in Spelled(path, _ground.WorkingDirectory, matchers: matchers))
                {
                    if (decide(asked, Resolved(asked)).Refusal() is { } denied)
                    {
                        return denied;
                    }
                }

                return AgentPolicyAnswer.Allow;
            }
            catch (Exception exception) when (Undecidable(exception))
            {
                return Undecided(path, exception);
            }
        }
        finally
        {
            _resolutions.Value = outerResolutions;
            _kinds.Value = outerKinds;
            _wholes.Value = outerWholes;
        }
    }

    /// <summary>
    /// Every place <paramref name="path"/> may reach, each spelled whole as the system would reach
    /// it: a home expanded (<c>~</c>, <c>$HOME</c>, <c>$USERPROFILE</c>, <c>%USERPROFILE%</c>), made
    /// absolute against <paramref name="directory"/> as <see cref="Plain"/> makes one. On Windows a
    /// shell's spelling of a drive - <c>/c/x</c> as Git Bash spells <c>C:\x</c>, <c>/cygdrive/c/x</c>,
    /// <c>/mnt/c/x</c> - reaches the drive in the shell that spells it so and <c>\c\x</c> on the
    /// current drive in one that does not: both places are given, the drive's first, and a refusal
    /// holds where it holds for either. On Windows a segment spelled as a short name the system gave no
    /// long name - its path is not there when this reads it, and a command may make it before the word
    /// is read - is read as each name of a guarded place it could shorten as well
    /// (<see cref="WindowsNames.Lengthened"/>).
    /// </summary>
    /// <param name="path">The path, as a word of a command spells it.</param>
    /// <param name="directory">Where it is read from.</param>
    /// <param name="tildeExpands">
    /// Whether the shell expands a tilde at the path's start (<see cref="NamedPath.TildeExpands"/>): false where a program
    /// reads it out of its word or it is quoted, so its tilde of a variable is the text it spells.
    /// </param>
    /// <param name="lengthen">
    /// Whether each short name is read as each guarded name it could shorten: false where the path is a place a move
    /// reaches, which the shell's <c>PWD</c> holds as the move spells it, and where a file's reaching another is asked of
    /// the two as forms (<see cref="Meet"/>) - every path read from such a place, or asked of such a file, is lengthened
    /// where it is decided.
    /// </param>
    /// <param name="matchers">The patterns the decision asking matches a path by, each of which tells two readings apart (<see cref="Lengthenings"/>); null where none does.</param>
    private IReadOnlyList<string> Spelled(string path, string directory, bool tildeExpands = true, bool lengthen = true, IReadOnlyList<(DssHarness.Core.Repository.GlobPattern Pattern, string Root, StringComparison Comparison)>? matchers = null)
    {
        var text = HomeExpanded(path.Trim(), _platform.HomeDirectory, tildeExpands);
        IReadOnlyList<string> readings = OnWindows && DriveSpelled(text) is { } drive ? [drive, text] : [text];

        // A short name the system gave no long one - its path is not there yet - may be any guarded name it could shorten:
        // read one of each way its readings stand to the decisions (Lengthenings), never every one of them.
        return [.. readings.Select(reading => Plain(reading, directory))
            .SelectMany(whole => OnWindows && lengthen ? Lengthenings(whole, matchers) : [whole])
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Every place <paramref name="path"/> may reach as a word of a command (<see cref="Spelled"/>), and where
    /// <paramref name="pattern"/> says the shell expands its pattern, each place it reaches with a segment that begins with
    /// a dot and may match <c>..</c> or <c>.</c> (<c>.?</c>, <c>.[.]</c>, <c>.*</c>) read as that name - for a shell that
    /// does not skip them (sh, bash before 5.2 or with <c>globskipdots</c> off) matches them so - each folded away as the
    /// system walks it; and each short name read as <see cref="Spelled"/> reads it, where <paramref name="lengthen"/> says
    /// so - the two read together as the forms of the places they reach (<see cref="ReadingForms"/>), one place of each way
    /// those stand to the decisions given, never every combination.
    /// </summary>
    private IReadOnlyList<string> CommandSpelled(string path, string directory, bool pattern, bool tildeExpands = true, bool lengthen = true)
    {
        var spelled = Spelled(path, directory, tildeExpands, lengthen: false);
        var dots = pattern && spelled.Any(reading => reading.IndexOfAny(['*', '?', '[']) >= 0);
        var shorts = OnWindows && lengthen && spelled.Any(reading => reading.Contains('~', StringComparison.Ordinal));

        return dots || shorts ? [.. spelled.SelectMany(reading => ReadAsCommandsRead(reading, dots, shorts)).Distinct(StringComparer.OrdinalIgnoreCase)] : spelled;
    }

    /// <summary>
    /// <paramref name="path"/> made absolute against <paramref name="directory"/> as the runtime makes
    /// it - on Windows each segment read as Windows reads it, its trailing dots and spaces and an NTFS
    /// stream's or attribute's suffix gone (<see cref="WindowsNames.Trimmed"/>), and each short name along
    /// it given as its long name, as far as it exists - and on
    /// Windows with no device prefix left (<see cref="Undeviced"/>): dropped from the text, and from
    /// what the runtime makes of it, which turns a prefix spelled with mixed separators
    /// (<c>\/?/C:/x</c>) into one of its own (<c>\\?\C:\x</c>). The one spelling of every place this
    /// compares: a path asked, where it resolves, where a command moves to, and the ground's own.
    /// </summary>
    private string Plain(string path, string directory)
    {
        // A name one Windows reads as its device (NUL, COM1.txt) is to another Windows, and to the shells on it, the
        // file of that name: read as that file - the device reaches nothing to decide, the file may lie anywhere. Each
        // segment is read as Windows reads it, its trailing dots and spaces and a stream's suffix gone (WindowsNames).
        var prefixless = Undeviced(path);
        var device = OnWindows && ReferenceEquals(prefixless, path) && IsDevice(path);
        var named = OnWindows ? WindowsNames.Trimmed(prefixless) : prefixless;
        var whole = device
            ? Path.Join(Path.GetFullPath(Path.GetDirectoryName(named) is { Length: > 0 } parent ? parent : ".", directory), Path.GetFileName(named))
            : Path.GetFullPath(named, directory);

        // Each prefix dropped shortens the path, so the runtime's spelling of it is plain in a few turns.
        for (var undeviced = Undeviced(whole); !ReferenceEquals(undeviced, whole); undeviced = Undeviced(whole))
        {
            whole = Path.GetFullPath(undeviced, directory);
        }

        return whole;
    }

    /// <summary>
    /// A path as it resolves, its links followed, spelled as <see cref="Plain"/> spells it: a link's target may name its
    /// place with a device prefix. A device-named file this Windows resolves to the device is the file in its directory's
    /// place, that directory's links followed, as <see cref="Plain"/> reads it.
    /// </summary>
    /// <remarks>
    /// The one decision of a string or a path that asks holds what each path it asked resolved to
    /// (<see cref="_resolutions"/>): a decision reads one disk, so a path asked again - a word read at each place the shell
    /// may stand, a path read again at another - is given what it resolved to, never walked on the disk twice.
    /// </remarks>
    private string Resolved(string path)
    {
        var known = _resolutions.Value;

        if (known is not null && known.TryGetValue(path, out var resolved))
        {
            return resolved;
        }

        // A path no link stands along resolves to itself: read of the kinds this decision already found at each of its
        // directories, so a path beneath a directory the decision found empty on the disk is not walked again.
        var answer = known is not null && ResolvesToItself(path) ? path : ResolvedOnDisk(path);

        // Set by place, never added: the path may have been resolved at the boundary another resolution confirmed.
        if (known is not null)
        {
            known[path] = answer;
        }

        return answer;
    }

    /// <summary>
    /// Whether <paramref name="path"/> resolves to itself: every directory along it one the decision found on the disk
    /// and none a link, and no separator at its end - so the walk that follows links reads the path it is given. Each
    /// directory's kind is asked once a decision (<see cref="KindAt"/>), shared by every path that passes through it; and
    /// where the walk stops, at a name that is no directory, the disk's own resolver is asked once at the boundary it
    /// stops on (<see cref="ResolvedOnce"/>), for a file system may refuse to resolve a name whose kind it reads fine -
    /// a refusal is the caller's to see, as the walk's own would be.
    /// </summary>
    private bool ResolvesToItself(string path)    {
        if (!Path.IsPathRooted(path) || path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar))
        {
            return false;
        }

        var root = Path.GetPathRoot(path)!.Length;

        for (var at = root; at < path.Length;)
        {
            var end = path.IndexOfAny(['/', '\\'], at);

            if (end < 0)
            {
                end = path.Length;
            }

            if (end == at)
            {
                return false;
            }

            switch (KindAt(path[..end]))
            {
                case PathKind.Link:
                    return false;
                case PathKind.None or PathKind.File:
                    // The walk that follows links stops at this name, probing nothing beneath it: what it does probe - the
                    // directories above, and this name - the resolver is asked of at the first name past it, or the path
                    // itself where the name is the path's last.
                    return ResolvedOnce(Boundary(path, end + 1)) == Boundary(path, end + 1);
            }

            at = end + 1;
        }

        // Every directory along it is on the disk and none a link: the walk that follows them reads the path it is given.
        return true;
    }

    /// <summary><paramref name="path"/> to its first separator past <paramref name="from"/> - the whole of it where the separator does not stand: the first name past a missing directory, as the walk that follows links stops at it.</summary>
    private static string Boundary(string path, int from)
    {
        var end = from >= path.Length ? path.Length : path.IndexOfAny(['/', '\\'], from);

        return path[..(end < 0 ? path.Length : end)];
    }

    /// <summary>
    /// <see cref="ResolvedOnDisk"/> kept for the decision (<see cref="_resolutions"/>): a path the decision asks to resolve
    /// again is given what it resolved to, and a disk that refuses to resolve it refuses to the caller as it did before.
    /// </summary>
    private string ResolvedOnce(string path)
    {
        var known = _resolutions.Value;

        if (known is not null && known.TryGetValue(path, out var resolved))
        {
            return resolved;
        }

        var answer = ResolvedOnDisk(path);

        // Set by place, never added: the path may be the very boundary another resolution is confirming.
        if (known is not null)
        {
            known[path] = answer;
        }

        return answer;
    }

    /// <summary><see cref="Resolved"/> asked of the disk itself.</summary>
    private string ResolvedOnDisk(string path)
    {
        var resolved = _fileSystem.ResolveLinks(path);

        try
        {
            return Plain(resolved, _ground.WorkingDirectory);
        }
        catch (NotSupportedException) when (OnWindows && IsDevice(path) && Path.GetDirectoryName(path) is { Length: > 0 } parent)
        {
            return Path.Join(Resolved(parent), Path.GetFileName(path));
        }
    }

    /// <summary>
    /// Where each path the decision being made resolved to, by its spelling: one decision reads one disk, so its paths'
    /// resolutions are its own to keep. Null outside a decision, where every ask walks the disk. Held per decision by
    /// the decision's own asynchronous flow, as <see cref="_wholes"/> is.
    /// </summary>
    private readonly AsyncLocal<Dictionary<string, string>?> _resolutions = new();

    /// <summary>
    /// <paramref name="path"/> without the device prefix at its start, on Windows - <c>\\?\</c>,
    /// <c>\\.\</c> and <c>\??\</c>, in either separator, <c>UNC\</c> after one given as the
    /// <c>\\</c> of the share it names - and the path itself where it has none, or where the
    /// platform is another, whose system reads no prefix and where the runtime spells the path as
    /// that system reaches it.
    /// </summary>
    /// <exception cref="NotSupportedException">The prefix names a device that is no drive and no share - a volume by its id, a pipe - whose place this cannot tell, so no decision is made on it.</exception>
    private string Undeviced(string path)
    {
        if (!OnWindows
            || path.Length < 4
            || !IsSeparator(path[0])
            || !IsSeparator(path[3])
            || !((IsSeparator(path[1]) && path[2] is '?' or '.') || (path[1] == '?' && path[2] == '?')))
        {
            return path;
        }

        var rest = path[4..];

        if (rest.Length >= 4 && rest.StartsWith("UNC", StringComparison.OrdinalIgnoreCase) && IsSeparator(rest[3]))
        {
            return @"\\" + rest[4..];
        }

        return rest.Length >= 2 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && (rest.Length == 2 || IsSeparator(rest[2]))
            ? rest
            : throw new NotSupportedException($"'{path}' names a device that is no drive and no share, whose place this cannot tell");
    }

    /// <summary>
    /// The drive a shell's POSIX spelling of one names - <c>/c/x</c> as Git Bash and MSYS spell
    /// <c>C:\x</c>, <c>/cygdrive/c/x</c> as Cygwin does, <c>/mnt/c/x</c> as WSL does - as Windows
    /// spells it; null where the path spells no drive so.
    /// </summary>
    private static string? DriveSpelled(string path)
    {
        foreach (var mount in new[] { "/", "/cygdrive/", "/mnt/" })
        {
            if (path.StartsWith(mount, StringComparison.OrdinalIgnoreCase)
                && path.Length > mount.Length
                && char.IsAsciiLetter(path[mount.Length])
                && (path.Length == mount.Length + 1 || path[mount.Length + 1] == '/'))
            {
                return $"{char.ToUpperInvariant(path[mount.Length])}:\\{path[(mount.Length + 1)..].TrimStart('/')}";
            }
        }

        return null;
    }

    /// <summary>Whether the platform is Windows, whose paths take a device prefix and a shell's spelling of a drive.</summary>
    private bool OnWindows => _platform.Current is PlatformId.Windows;

    /// <summary>Whether <paramref name="character"/> separates a path on Windows, either way it is written.</summary>
    private static bool IsSeparator(char character) => character is '\\' or '/';

    /// <summary>
    /// A tilde's prefix the shell expands, before a separator or the path's end: <c>~+</c>, <c>~-</c>, the directory
    /// stack's <c>~N</c>, <c>~+N</c> and <c>~-N</c>, or <c>~name</c> - the separator after the name, where one stands,
    /// matched too, so what follows the prefix is read from the name's end.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TildeName = LinearRegex.Compile(@"^~(?<name>[+-]?[0-9]+|[+-]|[A-Za-z0-9._][A-Za-z0-9._-]*)(?:$|[/\\])");

    /// <summary>
    /// The path with a home at its start expanded to the user's own: <c>~</c>, <c>$HOME</c>, <c>${HOME}</c>,
    /// <c>$env:HOME</c>, <c>$USERPROFILE</c>, <c>${USERPROFILE}</c>, <c>$env:USERPROFILE</c> and <c>%USERPROFILE%</c> -
    /// and the shell's other tildes: <c>~+</c> where it stands, <c>~name</c> that user's home beside the user's own.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// It begins with <c>~-</c>, where the shell stood before, or with the directory stack's <c>~N</c>, <c>~+N</c> or
    /// <c>~-N</c>, which this does not know.
    /// </exception>
    private string HomeExpanded(string path) => HomeExpanded(path, _platform.HomeDirectory);

    /// <summary>
    /// <paramref name="path"/> with a home at its start expanded to <paramref name="home"/>: <c>~</c>, <c>$HOME</c>,
    /// <c>${HOME}</c>, <c>$env:HOME</c>, <c>$USERPROFILE</c>, <c>${USERPROFILE}</c>, <c>$env:USERPROFILE</c> and
    /// <c>%USERPROFILE%</c> - and the shell's other tildes: <c>~+</c> where it stands, <c>~name</c> that user's home beside
    /// <paramref name="home"/>. The one reading of a home a command's word names, for an agent's commands and for every
    /// line the tool reads as such.
    /// </summary>
    /// <param name="path">The path, as a word of a command spells it.</param>
    /// <param name="home">The user's home.</param>
    /// <param name="tildeExpands">
    /// Whether the shell expands a tilde at the path's start (<see cref="NamedPath.TildeExpands"/>): where it does not - a
    /// value a program reads out of its word (<c>git log @~1</c>), a quoted word - its tilde of a variable, <c>~+</c>,
    /// <c>~-</c> or the stack's, is the text it spells, read where the path is; a home is read as one still, as a program
    /// that reads one itself reads it.
    /// </param>
    /// <exception cref="NotSupportedException">
    /// It begins with <c>~-</c>, where the shell stood before, or with the directory stack's <c>~N</c>, <c>~+N</c> or
    /// <c>~-N</c>, which this does not know, and the shell expands it.
    /// </exception>
    internal static string HomeExpanded(string path, string home, bool tildeExpands = true)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(home);

        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return home + path[1..];
        }

        if (!tildeExpands && CommandAnalysis.VariableTilde(path, quoted: null) > 0)
        {
            return path;
        }

        // bash's ~+ is where the shell stands, the path after it read there - and every value the string gives PWD, read
        // beside it (CommandAnalysis.Substituted); its ~- is where the shell stood before, and ~N, ~+N and ~-N a directory of
        // the stack pushd keeps, which this does not know; and ~name is that user's home, read beside the user's own, where
        // homes stand together.
        if (TildeName.Match(path) is { Success: true } tilde)
        {
            var named = tilde.Groups["name"];
            var name = named.Value;
            var rest = path[(named.Index + named.Length)..];

            return name switch
            {
                "+" => rest.Length > 0 ? rest[1..] : ".",
                "-" => throw new NotSupportedException($"'{path}' names where the shell stood before this command, which this does not know"),
                _ when name.TrimStart('+', '-') is { Length: > 0 } stack && stack.All(char.IsAsciiDigit)
                    => throw new NotSupportedException($"'{path}' names a directory of the stack pushd keeps, which this does not know"),
                _ when Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(home)) is { } homes => Path.Join(homes, name) + rest,
                _ => path,
            };
        }

        foreach (var named in new[] { "${HOME}", "$HOME", "$env:HOME", "${USERPROFILE}", "$USERPROFILE", "$env:USERPROFILE", "%USERPROFILE%" })
        {
            if (path.StartsWith(named, StringComparison.OrdinalIgnoreCase)
                && (path.Length == named.Length || path[named.Length] is '/' or '\\'))
            {
                return home + path[named.Length..];
            }
        }

        return path;
    }

    /// <summary>The rule a call is denied by where it cannot be decided: by this policy, or by the engine whose deciding of it failed.</summary>
    public const string UndecidedRule = "undecided";

    /// <summary>Whether <paramref name="exception"/> is one a decision this cannot make throws: the path or the command is one it cannot read, or the file system would not answer.</summary>
    private static bool Undecidable(Exception exception)
        => exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException;

    /// <summary>
    /// The answer where this tool cannot decide: denied, never allowed, for nothing runs while the
    /// tool that would answer cannot - with why, so the fault is not lost in the refusal.
    /// </summary>
    private static AgentPolicyAnswer Undecided(string subject, Exception exception)
        => new AgentPolicyAnswer.Denied(
            UndecidedRule,
            AgentPolicySource.Builtin,
            $"this tool could not decide '{subject}' ({exception.Message}): it denies what it cannot decide");

    /// <summary>
    /// The first path rule of <paramref name="verb"/> that matches the path, as asked or as
    /// resolved, against any of the agent's roots - the read roots, and for a write the writable
    /// roots beside them, for a write may fall in a root no read names - or allow where none does.
    /// </summary>
    private AgentPolicyAnswer Matched(AgentRuleVerb verb, string asked, string resolved)
    {
        var roots = verb is AgentRuleVerb.Write ? _allRoots : _refusalReadRoots;

        foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb == verb))
        {
            foreach (var root in roots)
            {
                foreach (var form in new[] { asked, resolved })
                {
                    if (BeneathOf(root, form, Refusals) is { } relative && rule.Matches(relative, Refusals))
                    {
                        return DeniedBy(declared, form);
                    }
                }
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>The denial a restriction refuses with: its rule, and where the rule stands, and the action's own path.</summary>
    private static AgentPolicyAnswer.Denied DeniedBy(AgentRestriction declared, string subject)
    {
        var source = declared.Source is AgentPolicySource.Config ? "agentPolicy.restrictions" : "this agent";

        return new AgentPolicyAnswer.Denied(
            declared.Text,
            declared.Source,
            $"'{subject}' is denied by {declared.Text}, a restriction of {source}");
    }

    /// <summary>The path made absolute as the runtime makes it - on Windows each short name along it given as its long name, as far as it exists - its trailing separator gone.</summary>
    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Every directory of the ground, resolved once and spelled as every path asked is, its duplicates gone.</summary>
    private IReadOnlyList<string> Resolve(IReadOnlyList<string> directories)
        => [.. directories
            .Select(directory => Resolved(Whole(directory)))
            .Distinct(StringComparer.FromComparison(_platform.PathComparison))];
}
