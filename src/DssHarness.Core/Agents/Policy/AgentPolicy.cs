using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Runs;

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
public sealed class AgentPolicy
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
    /// repository holds beside it - its refs, objects, configuration, remotes and worktrees.
    /// Reading git - status, diff, log, show, blame, grep - stays allowed.
    /// </summary>
    public static IReadOnlyList<string> GitStateVerbs { get; } =
        ["commit", "stash", "reset", "clean", "checkout", "switch", "restore", "rebase", "merge", "cherry-pick", "revert", "pull", "push", "am", "worktree", "branch", "tag", "update-ref", "config", "gc",
         "bisect", "symbolic-ref", "filter-branch", "fast-import", "replace", "notes", "reflog", "remote", "submodule", "sparse-checkout", "read-tree", "prune", "repack", "pack-refs", "maintenance"];

    /// <summary>
    /// gh's own commands and help topics, as gh 2.89 lists them and gh's manual of 2026-10-26 (<c>discussion</c>,
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
    /// The <c>gh</c> commands that write the state the worktree rules keep, refused by them beside git's state-writing
    /// verbs (<see cref="GitStateVerbs"/>): git's, run on the repository they stand in - a pull request's or an issue's
    /// branch fetched and checked out, a pull request closed with its branch deleted (checked out of first), a default
    /// repository or a remote written into git's configuration, a fork or a new repository made with its remote, its push
    /// or its clone, a repository renamed with its remote, and gh made git's credential helper - and gh's own
    /// configuration, which names the editor, pager and browser every gh command runs. Opening a pull request, which may
    /// push its branch, is a CI start (<see cref="GhCiReaches"/>).
    /// </summary>
    public static IReadOnlyList<string> GhStateCommands { get; } =
    [
        "gh pr checkout", "gh pr close", "gh issue develop", "gh repo set-default", "gh repo fork", "gh repo create",
        "gh repo rename", "gh auth setup-git", "gh config set",
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
    private readonly IReadOnlyList<string> _slots;
    private readonly IReadOnlyList<string> _claudeDirectories;
    private readonly IReadOnlyList<string> _state;
    private readonly IReadOnlyList<(AgentRule Rule, AgentRestriction Declared)> _rules;
    private readonly IReadOnlyList<string> _webDomains;
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _writes;
    private readonly IReadOnlyList<AgentRule> _worktreeRunRules;
    private readonly IReadOnlyList<AgentRule> _outOfStepRules = [.. GhOutOfStepCommands.Select(command => AgentRule.Parse($"Run({command} *)"))];
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _worktreePolicyWrites;
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _protectedPaths;
    private readonly string _workingRoot;
    private readonly string? _reporterRoot;
    private readonly string? _resolvedReporterRoot;

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
        _slots = Resolve(ground.SlotDirectories);
        _claudeDirectories = Resolve(ground.ClaudeCodeDirectories);
        _state = Resolve(ground.StateDirectories);
        _rules = [.. ground.Restrictions.Select(declared => (AgentRule.Parse(declared.Text), declared))];

        // A domain is held as the configuration's check reads it: trimmed, its root dot gone, as a
        // host is.
        _webDomains = [.. (ground.WebDomains ?? []).Select(domain => domain.Trim().TrimEnd('.'))];
        _writes = ground.Writes is { } writes
            ? [.. writes.Select(pattern => (GlobPattern.Parse(pattern), pattern))]
            : [];
        _worktreeRunRules =
            [.. GitStateVerbs.Select(verb => AgentRule.Parse($"Run(git {verb} *)")),
               .. GhStateCommands.Select(command => AgentRule.Parse($"Run({command} *)")),
               .. NeverCiCommands.Select(command => AgentRule.Parse($"Run({command} *)"))];
        _worktreePolicyWrites = [.. WorktreePolicyWrites.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _protectedPaths = [.. ProtectedPathRules.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _workingRoot = Resolved(Whole(ground.WorkingDirectory));

        // The reporter's root as it was given, and where it resolves: a path asked is asked by one of
        // its names, and lands where the other says.
        _reporterRoot = ground.ReporterRoot is { } root ? Whole(Plain(root, ground.WorkingDirectory)) : null;
        _resolvedReporterRoot = ground.ReporterRoot is { } given ? Resolved(Whole(given)) : null;
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
    /// Whether a path an agent would read may be read: a credential slot and a Claude Code
    /// directory never, a <c>Read</c> rule where one matches; everything else is allowed, for
    /// reading is what an agent is for.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Read(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, ReadDecision);
    }

    /// <summary>
    /// Whether a directory an agent would search may be searched: as a read of it, and never where a
    /// credential slot or a Claude Code directory lies beneath it, for a search reads what is
    /// beneath. What a <c>Read</c> rule refuses beneath it is left out by the client's own deny rules,
    /// which this tool writes for each launch.
    /// </summary>
    /// <param name="path">The directory as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Search(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, (asked, resolved) => ReadDecision(asked, resolved).Refusal() is { } denied ? denied : SearchedPast(asked, resolved));
    }

    /// <summary>
    /// Whether a path an agent would write may be written: a credential slot and a Claude Code
    /// directory never, a path under no writable root never, a <c>Write</c> rule where one matches.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Write(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return Decide(path, WriteDecision);
    }

    /// <summary>
    /// Whether a command an agent would run may run: flattened whole by
    /// <see cref="CommandAnalysis"/>, refused where any command it runs names a root action, a
    /// credential slot or a Claude Code directory, or where a <c>Run</c> rule matches one - and
    /// denied where it cannot be read whole.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv) => Run(argv, directory: null);

    /// <summary>
    /// Whether a command an agent would run in <paramref name="directory"/> may run: decided as <see cref="Run(IReadOnlyList{string})"/>
    /// decides it, each path it names read against that directory - where the command starts - rather than the working
    /// directory.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    /// <param name="directory">Where it starts, absolute or relative to the working directory; null for the working directory.</param>
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv, string? directory)
    {
        ArgumentNullException.ThrowIfNull(argv);

        try
        {
            return RunDecided(CommandAnalysis.Commands(argv), directory is null ? null : Plain(directory, _ground.WorkingDirectory));
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
    /// credential slot or a Claude Code directory, or where a <c>Run</c> rule matches it; a string
    /// it cannot read whole is denied.
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

    /// <summary>A read, decided: a slot and a Claude Code directory never, then the reporter's one root, then the <c>Read</c> rules on the path as asked and as resolved.</summary>
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

    /// <summary>A search past its read: no credential slot and no Claude Code directory beneath what it searches.</summary>
    private AgentPolicyAnswer SearchedPast(string asked, string resolved)
    {
        foreach (var form in new[] { asked, resolved })
        {
            if (_slots.FirstOrDefault(slot => PathContainment.IsStrictlyInside(form, slot, Refusals)) is { } slot)
            {
                return new AgentPolicyAnswer.Denied(
                    "the credential slots",
                    AgentPolicySource.Builtin,
                    $"'{form}' holds a credential slot of this tool, '{slot}': a search of it would read one, and no file tool of an agent reads one");
            }

            if (_claudeDirectories.FirstOrDefault(directory => PathContainment.IsStrictlyInside(form, directory, Refusals)) is { } directory)
            {
                return new AgentPolicyAnswer.Denied(
                    "the Claude Code directories",
                    AgentPolicySource.Builtin,
                    $"'{form}' holds a Claude Code directory this run reads, '{directory}': a search of it would read one, and no file tool of an agent reads one");
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>A write, decided: a slot and a Claude Code directory never, then the worktree rules, protected paths and the tool's own state, then the step's <c>writes</c>, the writable roots and the <c>Write</c> rules.</summary>
    private AgentPolicyAnswer WriteDecision(string asked, string resolved)
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

        if (OutsideStepWrites(asked, resolved) is { } outside)
        {
            return outside;
        }

        if (!_writableRoots.Any(root => PathContainment.IsInsideOrSame(root, resolved, _platform.PathComparison)))
        {
            return new AgentPolicyAnswer.Denied(
                "the writable roots",
                AgentPolicySource.Builtin,
                $"'{resolved}' is outside the writable roots of this agent: {string.Join(", ", _writableRoots)}");
        }

        return Matched(AgentRuleVerb.Write, asked, resolved);
    }

    /// <summary>
    /// The built-in write refusals: git's own files and the tool's policy under the worktree rules,
    /// the protected paths under their own switch, and the tool's own state in any root whatever the
    /// switches say - a place of the agent's own within it excepted, a writable root that stands
    /// inside the state, as its work directory does inside the run's.
    /// </summary>
    private AgentPolicyAnswer.Denied? BuiltinWriteRefusal(string asked, string resolved)
    {
        foreach (var root in _allRoots)
        {
            foreach (var form in new[] { asked, resolved })
            {
                if (Beneath(root, form, Refusals) is not { } relative)
                {
                    continue;
                }

                if (_ground.WorktreeRules)
                {
                    // Git's own at any depth, as Claude Code's own guard holds it: a nested repository's
                    // or a submodule's .git runs its hooks as surely as the root's.
                    if (relative.Split('/').Any(segment => segment.Equals(".git", Refusals)))
                    {
                        return new AgentPolicyAnswer.Denied(
                            GitsOwnRule,
                            AgentPolicySource.Builtin,
                            $"'{form}' is git's own: written by git's verbs and this tool's, never by an agent's file tool, whatever protectedPaths says");
                    }

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

        // The tool's own state, in any root, whatever the switches say: written by its verbs alone.
        // A writable root that stands inside the state is the agent's own place there - its
        // worktree under the worktrees root, its work directory under the run's - and is not
        // refused; state that stands inside a writable root is, for the root was given to write
        // the agent's work, not the tool's records. Asked of the path as it resolves, so no link
        // inside a writable root reaches the state by its other name.
        foreach (var state in _state)
        {
            if (!new[] { asked, resolved }.Any(form => PathContainment.IsInsideOrSame(state, form, Refusals)))
            {
                continue;
            }

            if (_writableRoots.Any(root => PathContainment.IsInsideOrSame(root, resolved, _platform.PathComparison)
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
    /// its worktree that matches none of their patterns is denied. What a command the agent ran
    /// writes is not seen here, and is measured where it cannot be missed: at the fold.
    /// </summary>
    private AgentPolicyAnswer.Denied? OutsideStepWrites(string asked, string resolved)
    {
        if (_writes.Count == 0)
        {
            return null;
        }

        foreach (var form in new[] { asked, resolved })
        {
            // Beneath the worktree by any spelling its file system folds to it: the narrowing holds
            // for each, and a pattern allows only the spelling it names as the platform compares.
            if (Beneath(_workingRoot, form, Refusals) is { } relative && !Covers(relative))
            {
                return new AgentPolicyAnswer.Denied(
                    "the step's writes",
                    AgentPolicySource.Builtin,
                    $"'{resolved}' is outside the paths this step may write: {string.Join(", ", _writes.Select(write => write.Text))}");
            }
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
        // Everything of a command compares as this platform compares names, but for the root
        // actions and the places no agent reaches, which fold case everywhere.
        var comparison = _platform.PathComparison;

        if (_reporterRoot is not null)
        {
            return new AgentPolicyAnswer.Denied(
                "the reporter's reads",
                AgentPolicySource.Builtin,
                "this agent reads and nothing else: it is the run's reporter, offered the tools that read and no other");
        }

        // Every directory a path may be read against: the working directory, and each a cd of the
        // string moved to - a path is tried against all of them, for a subshell's cd ends with it.
        var places = new List<string> { startsIn ?? _ground.WorkingDirectory };

        foreach (var analyzed in commands)
        {
            var command = analyzed.Words;

            // Each word, and the file each redirect names or the text it gives: a redirect is decided by its file, and a
            // here-document's or here-string's text is none the shell expands braces in.
            foreach (var (word, braces) in command.Select(word => (word, true)).Concat(analyzed.Redirects.Select(redirect => (redirect.Target, redirect.Operator != "<<<"))))
            {
                // The sentence says the action it matched, in the one spelling the list holds, so a
                // word named in either case is refused in the same words.
                if (RootActionOf(word, braces) is { } action)
                {
                    return new AgentPolicyAnswer.Denied(
                        "the root actions",
                        AgentPolicySource.Builtin,
                        $"'{action}' is a root action: no agent of this tool acts as root");
                }

                if (Named(word, places, braces) is { } named)
                {
                    return named;
                }
            }

            places.AddRange(MovedTo(command, places));

            // wsl starting a root user, and schtasks running a task as another account: neither is
            // a word the list above holds, and both are as root.
            if (NamesRootUser(command, comparison) is { } asRoot)
            {
                return new AgentPolicyAnswer.Denied(
                    "the root actions",
                    AgentPolicySource.Builtin,
                    $"'{asRoot}' would run as the root user: no agent of this tool acts as root");
            }

            var environment = CommandAnalysis.EnvironmentOf(command);

            // Every word is tried as the program of the words after it, the command's environment
            // before them: a command another program runs - find -exec, ssh, a wrapper this does
            // not strip - meets the rules on its own program; but a program that runs none of its
            // words, whose words are its data. Assignments alone run no program, yet set git's
            // configuration for what runs after them (PowerShell's $env:NAME = value): they meet
            // the rules too.
            var starts = environment == command.Count || CommandAnalysis.RunsNoWords(command[environment]) ? environment + 1 : command.Count;

            for (var start = environment; start < starts; start++)
            {
                IReadOnlyList<string> words = [.. command.Take(environment), .. command.Skip(start)];

                if (Decided(analyzed, start, words, comparison).Refusal() is { } denied)
                {
                    return denied;
                }
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>
    /// Whether cmd.exe, starting a batch file with <paramref name="word"/> among its arguments, would read in it what was
    /// not read here: a character of its own (<see cref="BatchFiles"/>), an expansion the shell fills in first, or the
    /// word another program fills in as it starts it (find's <c>-exec</c>, fd's <c>-x</c>, parallel), an expansion as
    /// <see cref="CommandAnalysis"/> reads it - with a file's name, which may hold anything. A redirect is the shell's
    /// own, never among the arguments cmd is handed.
    /// </summary>
    private static bool CmdReadsAgain(string word)
        => BatchFiles.Reread([word]) is not null || CommandAnalysis.Expansion(word) is not null;

    /// <summary>
    /// The rules on one command's words from <paramref name="start"/>, as the program there would run them: the words it
    /// is handed, its redirects the shell's own - with no program where the command is assignments alone.
    /// </summary>
    private AgentPolicyAnswer Decided(AnalyzedCommand analyzed, int start, IReadOnlyList<string> words, StringComparison comparison)
    {
        var roots = StringComparer.OrdinalIgnoreCase;
        var command = analyzed.Words;
        var program = start < command.Count ? CommandAnalysis.Name(command[start]) : string.Empty;
        var whole = string.Join(' ', analyzed.Whole);

        // A program that runs a command line it composes - Start-Process's arguments, WMI's, the task scheduler's, code
        // Add-Type compiles - runs what no word here spells: the command's own program, past the root actions.
        if (start == CommandAnalysis.EnvironmentOf(command) && CommandAnalysis.ComposesCommandLine([.. command.Skip(start)]) is { } composed)
        {
            return Undecided(whole, new NotSupportedException($"'{command[start]}' {composed}, which this does not read"));
        }

        // A batch file is started on Windows by cmd.exe, which reads its arguments a second time: one holding what cmd
        // reads as its own, or what the shell expands into it, would run another command than the one read here - wherever
        // it stands, for a wrapper this does not strip starts it as well.
        if (_platform.Current is PlatformId.Windows && start < command.Count && BatchFiles.Is(command[start]) && command.Skip(start + 1).FirstOrDefault(CmdReadsAgain) is { } reread)
        {
            return Undecided(whole, new NotSupportedException($"'{command[start]}' is a batch file, whose arguments cmd.exe reads again, and '{reread}' holds what cmd reads as its own or what the shell expands"));
        }

        // The tool's own verbs and run-side-task are refused whatever worktreeRules says: an agent
        // does not move its own run, nor leave its step's limits. A verb read from a response file
        // is one this cannot see.
        if (roots.Equals(program, ToolPackage.Command) && start + 1 < command.Count
            && (ToolVerbs.Contains(command[start + 1], roots) || command[start + 1].StartsWith('@')))
        {
            return new AgentPolicyAnswer.Denied(
                "the orchestrator and agent verbs",
                AgentPolicySource.Builtin,
                command[start + 1].StartsWith('@')
                    ? $"'{ToolPackage.Command} {command[start + 1]}' is refused: its verb is in a file this does not read, and an agent does not move its own run"
                    : $"'{ToolPackage.Command} {command[start + 1]}' is refused: an agent does not move its own run");
        }

        if (roots.Equals(program, "run-side-task"))
        {
            return new AgentPolicyAnswer.Denied(
                "run-side-task",
                AgentPolicySource.Builtin,
                "'run-side-task' is refused: an agent does not leave its step's limits");
        }

        // The Run rules, against each reading of the command's own words in turn: git configured
        // where it runs the git config it is first, then the command with git's options dropped
        // before its subcommand.
        var environment = CommandAnalysis.EnvironmentOf(command);
        var runs = start == environment;
        var readings = CommandAnalysis.ForRules(words, program: runs);

        // The gh commands that hand the work to another agent, or install what the next launch loads, are refused whatever
        // worktreeRules says: an agent does not leave its step's limits.
        foreach (var (reading, rule) in readings.SelectMany(reading => _outOfStepRules.Select(rule => (reading, rule))))
        {
            switch (rule.Match(reading, comparison))
            {
                case AgentRuleMatch.Yes:
                    return new AgentPolicyAnswer.Denied(
                        rule.Text,
                        AgentPolicySource.Builtin,
                        $"'{whole}' is denied by {rule.Text}, a rule of this tool: an agent does not leave its step's limits");
                case AgentRuleMatch.Expanded:
                    return MayBe(rule.Text, AgentPolicySource.Builtin, whole);
            }
        }

        foreach (var reading in readings)
        {
            foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Run))
            {
                switch (rule.Match(reading, comparison))
                {
                    case AgentRuleMatch.Yes:
                        return DeniedBy(declared, whole);
                    case AgentRuleMatch.Expanded:
                        return MayBe(rule.Text, declared.Source, whole);
                }
            }
        }

        // The worktree rules' commands: git's state-writing verbs and the merges and CI starts of
        // gh, which keep an agent's worktree a thing the engine can measure.
        if (_ground.WorktreeRules)
        {
            foreach (var (reading, rule) in readings.SelectMany(reading => _worktreeRunRules.Select(rule => (reading, rule))))
            {
                switch (rule.Match(reading, comparison))
                {
                    case AgentRuleMatch.Yes:
                        return new AgentPolicyAnswer.Denied(
                            rule.Text,
                            AgentPolicySource.Builtin,
                            $"'{whole}' is denied by {rule.Text}, a worktree rule of this tool");
                    case AgentRuleMatch.Expanded:
                        return MayBe(rule.Text, AgentPolicySource.Builtin, whole);
                }
            }

            // A gh met past the command's program runs where that program runs the words after it (setsid gh co); among any
            // other program's words it is that program's data (Get-Command gh -ErrorAction SilentlyContinue).
            var ghRuns = runs || CommandAnalysis.RunsItsWords(command[environment]);

            if (readings.Select(reading => MergesOrStartsCi(reading, ghRuns)).OfType<string>().FirstOrDefault() is { } reached)
            {
                return new AgentPolicyAnswer.Denied(
                    "the merges and CI starts of gh",
                    AgentPolicySource.Builtin,
                    $"'{whole}' is denied by a worktree rule of this tool: it reaches {reached}, and never merges, never starts CI binds the agents too");
            }
        }

        // A command that runs a program which fetches from the network is decided as a web request
        // too, by each address it names - and where the web is held to rules, one whose addresses
        // are in a file this does not read is denied.
        foreach (var address in CommandAnalysis.WebAddresses(words))
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
    /// Whether a path the shell would expand from <paramref name="pattern"/> - its <c>*</c>, its
    /// <c>?</c>, its <c>[...]</c> and a <c>**</c> of many directories, a dot matched as any
    /// character - may be <paramref name="place"/> or lie beneath it, compared in any case. A
    /// pattern that ends above the place names its parents only, which no refusal reaches.
    /// </summary>
    private static bool GlobReaches(string pattern, string place)
    {
        var patterned = pattern.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var placed = place.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var segments = patterned.Select(segment => segment == "**" ? null : SegmentPattern(segment)).ToList();

        // Each pattern segment against each place segment, from the ends back: whether the pattern
        // from p on reaches the place from q on.
        var reaches = new bool[patterned.Length + 1, placed.Length + 1];

        for (var p = patterned.Length; p >= 0; p--)
        {
            for (var q = placed.Length; q >= 0; q--)
            {
                reaches[p, q] = q == placed.Length
                    || (p < patterned.Length && (segments[p] is not { } segment
                        ? reaches[p + 1, q] || reaches[p, q + 1]
                        : segment.IsMatch(placed[q]) && reaches[p + 1, q + 1]));
            }
        }

        return reaches[0, 0];
    }

    /// <summary>Whether <paramref name="word"/>'s last name is one a Windows may read as its device's, with any extension (<see cref="Runtime.ConfinedPaths.DeviceNames"/>).</summary>
    private static bool IsDevice(string word)
        => Runtime.ConfinedPaths.DeviceNames.Contains(
            Path.GetFileName(word.TrimEnd(':', ' ', '.').Replace('\\', '/')).Split('.')[0].TrimEnd(' '),
            StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// One segment of a shell pattern as the expression it matches a whole name by, in any case: a bracket's members
    /// each the character it is, a range kept; one the shell would not read as a bracket - a <c>]</c> right after its
    /// <c>[</c> or <c>[!</c> is a member, and one that does not close or holds a reversed range is the characters it
    /// is - read as those characters.
    /// </summary>
    private static System.Text.RegularExpressions.Regex SegmentPattern(string segment)
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

        // Matched in time linear in the name, however its stars and classes nest: no pattern backtracks past its bound.
        return new System.Text.RegularExpressions.Regex(
            expression.Append('$').ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking,
            TimeSpan.FromSeconds(1));
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


    /// <summary>
    /// What a <c>gh</c> command reaches that merges or starts CI, as the worktree rules refuse it:
    /// an <c>api</c> endpoint of <see cref="GhApiEndpoints"/> or a mutation of
    /// <see cref="GhApiMutations"/>, a GraphQL query read from a file, an alias made, an extension
    /// run, or - where gh is the command's program - a command named by none of <see cref="GhCommands"/>;
    /// null where it reaches none.
    /// </summary>
    /// <param name="words">The command as its rules read it.</param>
    /// <param name="runs">
    /// Whether the gh the words begin with runs: the command's own program, or a word past a program that runs the words
    /// after it (<see cref="CommandAnalysis.CommandRunners"/>); among any other program's words (<c>git grep gh src</c>,
    /// <c>Get-Command gh -ErrorAction SilentlyContinue</c>) a gh is that program's data, and its next word no command of gh's.
    /// </param>
    private static string? MergesOrStartsCi(IReadOnlyList<string> words, bool runs)
    {
        if (words.Count < 2 || !CommandAnalysis.Name(words[0]).Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // A word that is none of gh's own commands is an alias or an extension: a name for any command, a refused one
        // among them, that no rule knows.
        if (runs && !GhCommands.Contains(words[1], StringComparer.Ordinal))
        {
            return $"'gh {words[1]}', an alias or an extension, a name for any command";
        }

        // An alias stands for any command, a refused one among them, under a name no rule knows; an extension run by
        // exec is a command no rule here reads.
        if (words[1].Equals("alias", StringComparison.OrdinalIgnoreCase) && words.Count > 2 && words[2] is "set" or "import")
        {
            return $"gh alias {words[2]}, a name for any command";
        }

        if (words[1].Equals("extension", StringComparison.OrdinalIgnoreCase) && words.Count > 2 && words[2] is "exec")
        {
            return "gh extension exec, an extension run: a command no rule here reads";
        }

        if (!words[1].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var arguments = words.Skip(1).ToList();
        var writes = CommandAnalysis.GhApiWrites(arguments);

        foreach (var word in words.Skip(2))
        {
            var path = word.Split('?', 2)[0].TrimEnd('/');

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

        // An endpoint the shell expands may be any endpoint, a merge's among them.
        if (CommandAnalysis.GhApiEndpoint(arguments) is { } endpoint && CommandAnalysis.Expansion(endpoint) is { } expansion)
        {
            return $"an endpoint named by {expansion} ('{endpoint}'), which this does not read";
        }

        // A GraphQL query this does not read may be any mutation: one read from a file, or one the
        // shell expands.
        if (words.Any(word => word.Equals("graphql", StringComparison.OrdinalIgnoreCase)))
        {
            if (words.Any(word => word == "--input" || word.StartsWith("--input=", StringComparison.Ordinal) || word.Contains("=@", StringComparison.Ordinal)))
            {
                return "a GraphQL query read from a file, which this does not read";
            }

            if (CommandAnalysis.GhApiFields(arguments).FirstOrDefault(field => field.StartsWith("query=", StringComparison.OrdinalIgnoreCase) && CommandAnalysis.Expansion(field) is not null) is { } query)
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
        => new(
            $"(^|/)({string.Join('|', endpoints.Select(endpoint => endpoint.EndsWith("/**", StringComparison.Ordinal)
                ? System.Text.RegularExpressions.Regex.Escape(endpoint[..^3]).Replace("<n>", "[^/]+", StringComparison.Ordinal) + "(/.*)?"
                : System.Text.RegularExpressions.Regex.Escape(endpoint).Replace("<n>", "[^/]+", StringComparison.Ordinal)))})$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.NonBacktracking);

    /// <summary>
    /// The root action <paramref name="word"/> names, in the list's own spelling: the word itself,
    /// each word its braces spell, and each word a script inside it would run - a command another
    /// program runs from one argument (<c>ssh host 'sudo x'</c>) is read so too; null where it names
    /// none. <paramref name="braces"/> says whether the shell expands its braces: a here-document's text it never does.
    /// </summary>
    private static string? RootActionOf(string word, bool braces)
    {
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

    /// <summary>Whether the path, as asked or as resolved, is a credential slot of this tool or lies in a Claude Code directory: no file tool of an agent reads or writes either.</summary>
    private AgentPolicyAnswer.Denied? Unreached(string asked, string resolved)
    {
        foreach (var form in new[] { asked, resolved })
        {
            if (_slots.Any(slot => PathContainment.IsInsideOrSame(slot, form, Refusals)))
            {
                return new AgentPolicyAnswer.Denied("the credential slots", AgentPolicySource.Builtin, $"'{form}' is a credential slot of this tool: no file tool of an agent reads or writes one");
            }

            if (_claudeDirectories.Any(directory => PathContainment.IsInsideOrSame(directory, form, Refusals)))
            {
                return new AgentPolicyAnswer.Denied("the Claude Code directories", AgentPolicySource.Builtin, $"'{form}' lies in a Claude Code directory this run reads: no file tool of an agent reads or writes one");
            }
        }

        return null;
    }

    /// <summary>
    /// The commands that move the commands after them to another directory, by their names in any case: the shell's
    /// <c>cd</c>, <c>pushd</c> and <c>chdir</c>, and PowerShell's <c>Set-Location</c> and <c>Push-Location</c> with their
    /// aliases - <c>sl</c>, and the shell's three names, which PowerShell gives them too. A program that starts what it
    /// runs elsewhere - env's <c>-C</c>, pwsh's <c>-WorkingDirectory</c> - is read as a <c>cd</c> before it by
    /// <see cref="CommandAnalysis"/>, so this is the one reader of every move.
    /// </summary>
    public static IReadOnlyList<string> Movers { get; } = ["cd", "pushd", "chdir", "Set-Location", "sl", "Push-Location"];

    /// <summary>
    /// Every directory a command moves the commands after it to (<see cref="Movers"/>), spelled as a path asked is, against
    /// every place already reached, for a subshell's <c>cd</c> ends with it and a drive's two readings are both places a
    /// <c>cd</c> may have gone: each place a spelling of each word it may go to may reach - every word no option, every
    /// word after a <c>--</c>, and a PowerShell parameter's value glued after its <c>:</c> (<c>-Path:x</c>), so a
    /// <c>-StackName</c>'s value is one more place rather than its target none - the home where it names none, and none
    /// where it is no such command, goes back (<c>-</c>), or names a place this cannot tell.
    /// </summary>
    /// <exception cref="NotSupportedException">The command's moves reach more places than <see cref="MaxPlaces"/>, which this does not follow, so no decision is made on it.</exception>
    private IReadOnlyList<string> MovedTo(IReadOnlyList<string> command, IReadOnlyList<string> places)
    {
        var environment = CommandAnalysis.EnvironmentOf(command);

        if (environment == command.Count || !Movers.Contains(CommandAnalysis.Name(command[environment]), StringComparer.OrdinalIgnoreCase))
        {
            return [];
        }

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

        var moved = new List<string>();

        // '-' goes back to a place reached before, which is one already.
        IEnumerable<string> going = targets.Count == 0 ? ["~"] : targets.Where(target => target != "-");

        foreach (var target in going)
        {
            try
            {
                moved.AddRange(places.SelectMany(place => Spelled(target, place)));
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                // A word no place can be told by moves nowhere this reads.
            }
        }

        IReadOnlyList<string> reached = [.. moved.Except(places, StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase)];

        return places.Count + reached.Count <= MaxPlaces
            ? reached
            : throw new NotSupportedException($"its moves reach more than {MaxPlaces} places, which this does not follow");
    }

    /// <summary>The most places a command's moves are followed to; past them it is not decided, and so denied.</summary>
    public const int MaxPlaces = 64;

    /// <summary>
    /// Whether <paramref name="word"/> names a path into a credential slot or a Claude Code
    /// directory, resolved as a tool's path resolves against each place the command may stand in:
    /// the word itself - a redirect's file or text among them - the value after an <c>=</c>
    /// (<c>--file=...</c>, an assignment's) and a PowerShell parameter's after its <c>:</c>
    /// (<c>-Path:...</c>), and each word a script inside it would name, a home expanded in each; its
    /// braces' words where <paramref name="braces"/> says the shell expands them.
    /// </summary>
    private AgentPolicyAnswer.Denied? Named(string word, IReadOnlyList<string> places, bool braces)
    {
        var candidates = new List<string> { word };

        if (word.IndexOf('=', StringComparison.Ordinal) is var equals and >= 0 && equals + 1 < word.Length)
        {
            candidates.Add(word[(equals + 1)..]);
        }

        // A PowerShell parameter's value glued after its ':' (-Path:~/x), as an option's after its '='.
        if (word.StartsWith('-') && word.IndexOf(':', StringComparison.Ordinal) is var colon and > 1 && colon + 1 < word.Length)
        {
            candidates.Add(word[(colon + 1)..]);
        }

        if (word.Any(char.IsWhiteSpace))
        {
            candidates.AddRange(word.Split([' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '`', '\'', '"'], StringSplitOptions.RemoveEmptyEntries));
        }

        // Each word its braces spell, and the path a file URL names: curl and its kind read a file so.
        if (braces && word.Contains('{', StringComparison.Ordinal))
        {
            candidates.AddRange(CommandAnalysis.BraceExpansions(word));
        }

        candidates.AddRange([.. candidates
            .Where(candidate => candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
            .OfType<string>()]);

        foreach (var (candidate, place) in candidates
            .Where(candidate => candidate.Length > 0)
            .Distinct()
            .SelectMany(candidate => places.Distinct().Select(place => (candidate, place))))
        {
            IReadOnlyList<string> readings;

            try
            {
                readings = Spelled(candidate, place);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                // A word that is no path at all ('--porcelain') is every command's ordinary argument.
                continue;
            }

            // A pattern the shell expands names every path it may match: refused where one of them
            // may be a slot or a Claude Code directory, or lie beneath one.
            foreach (var asked in readings.Where(reading => reading.IndexOfAny(['*', '?', '[']) >= 0))
            {
                if (_slots.FirstOrDefault(slot => GlobReaches(asked, slot)) is not null)
                {
                    return new AgentPolicyAnswer.Denied(
                        "the credential slots",
                        AgentPolicySource.Builtin,
                        $"'{word}' may name a credential slot of this tool by its pattern: no command of an agent touches one");
                }

                if (_claudeDirectories.FirstOrDefault(directory => GlobReaches(asked, directory)) is not null)
                {
                    return new AgentPolicyAnswer.Denied(
                        "the Claude Code directories",
                        AgentPolicySource.Builtin,
                        $"'{word}' may name a Claude Code directory this run reads by its pattern: no command of an agent touches one");
                }
            }

            foreach (var form in readings.SelectMany(asked => new[] { asked, Resolved(asked) }))
            {
                if (_slots.Any(slot => PathContainment.IsInsideOrSame(slot, form, Refusals)))
                {
                    return new AgentPolicyAnswer.Denied(
                        "the credential slots",
                        AgentPolicySource.Builtin,
                        $"'{word}' names a credential slot of this tool: no command of an agent touches one");
                }

                if (_claudeDirectories.Any(directory => PathContainment.IsInsideOrSame(directory, form, Refusals)))
                {
                    return new AgentPolicyAnswer.Denied(
                        "the Claude Code directories",
                        AgentPolicySource.Builtin,
                        $"'{word}' lies in a Claude Code directory this run reads: no command of an agent touches one");
                }
            }
        }

        return null;
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
    private AgentPolicyAnswer Decide(string path, Func<string, string, AgentPolicyAnswer> decide)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("it names no path");
            }

            foreach (var asked in Spelled(path, _ground.WorkingDirectory))
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

    /// <summary>
    /// Every place <paramref name="path"/> may reach, each spelled whole as the system would reach
    /// it: a home expanded (<c>~</c>, <c>$HOME</c>, <c>$USERPROFILE</c>, <c>%USERPROFILE%</c>), made
    /// absolute against <paramref name="directory"/> as <see cref="Plain"/> makes one. On Windows a
    /// shell's spelling of a drive - <c>/c/x</c> as Git Bash spells <c>C:\x</c>, <c>/cygdrive/c/x</c>,
    /// <c>/mnt/c/x</c> - reaches the drive in the shell that spells it so and <c>\c\x</c> on the
    /// current drive in one that does not: both places are given, the drive's first, and a refusal
    /// holds where it holds for either.
    /// </summary>
    private IReadOnlyList<string> Spelled(string path, string directory)
    {
        var text = HomeExpanded(path.Trim());
        IReadOnlyList<string> readings = OnWindows && DriveSpelled(text) is { } drive ? [drive, text] : [text];

        return [.. readings.Select(reading => Plain(reading, directory)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// <paramref name="path"/> made absolute against <paramref name="directory"/> as the runtime makes
    /// it - on Windows each short name along it given as its long name, as far as it exists - and on
    /// Windows with no device prefix left (<see cref="Undeviced"/>): dropped from the text, and from
    /// what the runtime makes of it, which turns a prefix spelled with mixed separators
    /// (<c>\/?/C:/x</c>) into one of its own (<c>\\?\C:\x</c>). The one spelling of every place this
    /// compares: a path asked, where it resolves, where a command moves to, and the ground's own.
    /// </summary>
    private string Plain(string path, string directory)
    {
        // A name one Windows reads as its device (NUL, COM1.txt) is to another Windows, and to the shells on it, the
        // file of that name: read as that file - the device reaches nothing to decide, the file may lie anywhere.
        var prefixless = Undeviced(path);
        var whole = OnWindows && ReferenceEquals(prefixless, path) && IsDevice(path)
            ? Path.Join(Path.GetFullPath(Path.GetDirectoryName(path) is { Length: > 0 } parent ? parent : ".", directory), Path.GetFileName(path))
            : Path.GetFullPath(prefixless, directory);

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
    private string Resolved(string path)
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

    /// <summary>The path with a home at its start expanded to the user's own: <c>~</c>, <c>$HOME</c>, <c>${HOME}</c>, <c>$env:HOME</c>, <c>$USERPROFILE</c>, <c>${USERPROFILE}</c>, <c>$env:USERPROFILE</c> and <c>%USERPROFILE%</c>.</summary>
    private string HomeExpanded(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return _platform.HomeDirectory + path[1..];
        }

        foreach (var home in new[] { "${HOME}", "$HOME", "$env:HOME", "${USERPROFILE}", "$USERPROFILE", "$env:USERPROFILE", "%USERPROFILE%" })
        {
            if (path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                && (path.Length == home.Length || path[home.Length] is '/' or '\\'))
            {
                return _platform.HomeDirectory + path[home.Length..];
            }
        }

        return path;
    }

    /// <summary>The rule a call is denied by where it cannot be decided: by this policy, or by the engine whose deciding of it failed.</summary>
    public const string UndecidedRule = "undecided";

    /// <summary>Whether <paramref name="exception"/> is one a decision this cannot make throws: the path or the command is one it cannot read, or the file system would not answer.</summary>
    private static bool Undecidable(Exception exception)
        => exception is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException
            or System.Text.RegularExpressions.RegexMatchTimeoutException;

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
                    if (Beneath(root, form, Refusals) is { } relative && rule.Matches(relative, Refusals))
                    {
                        return DeniedBy(declared, form);
                    }
                }
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>The denial a restriction refuses with: its rule, and where the rule stands, and the action's own path.</summary>
    private static AgentPolicyAnswer DeniedBy(AgentRestriction declared, string subject)
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
