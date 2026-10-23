using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Platform;
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
/// A path is decided as asked and as resolved, each spelled whole first - a device prefix
/// (<c>\\?\</c>) dropped, a home (<c>~</c>, <c>$HOME</c>) expanded - and a refusal's places are
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
    /// repository holds beside it - its refs, objects, configuration, remotes and worktrees.
    /// Reading git - status, diff, log, show, blame, grep - stays allowed.
    /// </summary>
    public static IReadOnlyList<string> GitStateVerbs { get; } =
        ["commit", "stash", "reset", "clean", "checkout", "switch", "restore", "rebase", "merge", "cherry-pick", "revert", "pull", "push", "am", "worktree", "branch", "tag", "update-ref", "config", "gc",
         "bisect", "symbolic-ref", "filter-branch", "fast-import", "replace", "notes", "reflog", "remote", "submodule", "sparse-checkout", "read-tree", "prune", "repack", "pack-refs", "maintenance"];

    /// <summary>
    /// The <c>gh</c> commands that merge or start CI, refused by the worktree rules: never merges,
    /// never starts CI binds the agents too, not only the engine's own steps. <c>gh api</c> reaching
    /// the same endpoints, and <c>gh alias</c> making a name for any command, are refused beside
    /// them (<see cref="GhApiEndpoints"/>, <see cref="GhApiMutations"/>).
    /// </summary>
    public static IReadOnlyList<string> NeverCiCommands { get; } = ["gh pr merge", "gh workflow run", "gh run rerun"];

    /// <summary>
    /// The REST endpoints through which <c>gh api</c> merges or starts CI, refused by the worktree
    /// rules at a path's end: a pull request's merge and the update of its branch, a branch's merge,
    /// a fork's sync, a workflow's dispatch, approval and reruns, and a check asked again.
    /// </summary>
    public static IReadOnlyList<string> GhApiEndpoints { get; } =
        ["pulls/<n>/merge", "pulls/<n>/update-branch", "merges", "merge-upstream", "dispatches", "approve", "rerun", "rerun-failed-jobs", "rerequest"];

    /// <summary>The GraphQL mutations through which <c>gh api graphql</c> merges or starts CI, refused by the worktree rules wherever a word names one.</summary>
    public static IReadOnlyList<string> GhApiMutations { get; } =
        ["mergePullRequest", "enablePullRequestAutoMerge", "enqueuePullRequest", "mergeBranch", "updatePullRequestBranch", "rerequestCheckSuite"];

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
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _worktreePolicyWrites;
    private readonly IReadOnlyList<(GlobPattern Pattern, string Text)> _protectedPaths;
    private readonly string _workingRoot;
    private readonly string? _reporterRoot;

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
               .. NeverCiCommands.Select(command => AgentRule.Parse($"Run({command} *)"))];
        _worktreePolicyWrites = [.. WorktreePolicyWrites.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _protectedPaths = [.. ProtectedPathRules.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _workingRoot = _fileSystem.ResolveLinks(Whole(ground.WorkingDirectory));
        _reporterRoot = ground.ReporterRoot is { } root ? _fileSystem.ResolveLinks(Whole(root)) : null;
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
        ];
    }

    /// <summary>
    /// Whether an address an agent would ask the web for may be asked: no web at all where the step
    /// has none, then each <c>Web(...)</c> rule against the address's host, then
    /// <c>agentPolicy.webDomains</c>. A page, a redirect and a search's source are each decided
    /// here, one address at a time.
    /// </summary>
    /// <param name="address">The address as the agent's tool asks it, or as a fetcher command names it.</param>
    public AgentPolicyAnswer Web(string address)
    {
        ArgumentNullException.ThrowIfNull(address);

        try
        {
            return WebDecided(address, address);
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

        return Decide(path, (asked, resolved) => ReadDecision(asked, resolved) is AgentPolicyAnswer.Denied denied ? denied : SearchedPast(asked, resolved));
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
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        try
        {
            return RunDecided(CommandAnalysis.Commands(argv));
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
            return RunDecided(CommandAnalysis.Commands(shell));
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
        if (Unreached(asked, resolved) is AgentPolicyAnswer.Denied unreached)
        {
            return unreached;
        }

        if (OutsideReporterRoot(asked, resolved) is AgentPolicyAnswer.Denied outside)
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
        if (Unreached(asked, resolved) is AgentPolicyAnswer.Denied unreached)
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

        if (BuiltinWriteRefusal(asked, resolved) is AgentPolicyAnswer.Denied refused)
        {
            return refused;
        }

        if (OutsideStepWrites(asked, resolved) is AgentPolicyAnswer.Denied outside)
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
    private AgentPolicyAnswer? BuiltinWriteRefusal(string asked, string resolved)
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
                    if (relative.Equals(".git", Refusals) || relative.StartsWith(".git/", Refusals))
                    {
                        return new AgentPolicyAnswer.Denied(
                            "Write(.git/**)",
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
    /// The step's <c>writes</c>, the one source that narrows: where a step has them, a write under
    /// its worktree that matches none of their patterns is denied. What a command the agent ran
    /// writes is not seen here, and is measured where it cannot be missed: at the fold.
    /// </summary>
    private AgentPolicyAnswer? OutsideStepWrites(string asked, string resolved)
    {
        if (_writes.Count == 0)
        {
            return null;
        }

        foreach (var form in new[] { asked, resolved })
        {
            // Beneath the worktree by any spelling its file system folds to it: the narrowing holds
            // for each, and a pattern allows only the spelling it names as the platform compares.
            if (Beneath(_workingRoot, form, Refusals) is { } relative && !_writes.Any(write => write.Pattern.Matches(relative, _platform.PathComparison)))
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
    private AgentPolicyAnswer? OutsideReporterRoot(string asked, string resolved)
    {
        if (_reporterRoot is not { } root)
        {
            return null;
        }

        var comparison = _platform.PathComparison;

        // Both of its forms must stand inside: a link inside the root that resolves outside it is
        // a read of what it points at, which is outside what the reporter may read.
        if (new[] { asked, resolved }.All(form => PathContainment.IsInsideOrSame(root, form, comparison)))
        {
            return null;
        }

        return new AgentPolicyAnswer.Denied(
            "the reporter's reads",
            AgentPolicySource.Builtin,
            $"'{asked}' is outside what this agent may read: {root.Replace('\\', '/')}/**");
    }

    /// <summary>Each flattened command, refused by the first thing it runs that may not run.</summary>
    private AgentPolicyAnswer RunDecided(IReadOnlyList<IReadOnlyList<string>> commands)
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
        var places = new List<string> { _ground.WorkingDirectory };

        foreach (var command in commands)
        {
            foreach (var word in command)
            {
                // The sentence says the action it matched, in the one spelling the list holds, so a
                // word named in either case is refused in the same words.
                if (RootActionOf(word) is { } action)
                {
                    return new AgentPolicyAnswer.Denied(
                        "the root actions",
                        AgentPolicySource.Builtin,
                        $"'{action}' is a root action: no agent of this tool acts as root");
                }

                if (Named(word, places) is AgentPolicyAnswer.Denied named)
                {
                    return named;
                }
            }

            if (MovedTo(command, places) is { } moved)
            {
                places.Add(moved);
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

            var environment = CommandAnalysis.EnvironmentOf(command);

            // Every word is tried as the program of the words after it, the command's environment
            // before them: a command another program runs - find -exec, ssh, a wrapper this does
            // not strip - meets the rules on its own program.
            for (var start = environment; start < command.Count; start++)
            {
                IReadOnlyList<string> words = [.. command.Take(environment), .. command.Skip(start)];

                if (Decided(command, start, words, comparison) is AgentPolicyAnswer.Denied denied)
                {
                    return denied;
                }
            }
        }

        return AgentPolicyAnswer.Allow;
    }

    /// <summary>The rules on one command's words from <paramref name="start"/>, as the program there would run them.</summary>
    private AgentPolicyAnswer Decided(IReadOnlyList<string> command, int start, IReadOnlyList<string> words, StringComparison comparison)
    {
        var roots = StringComparer.OrdinalIgnoreCase;
        var program = CommandAnalysis.Name(command[start]);
        var whole = string.Join(' ', command);

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

        // The Run rules, against the command's own words: git's options dropped before its
        // subcommand, and git configured where it runs the git config it is.
        var forRules = CommandAnalysis.ForRules(words, comparison);

        foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Run))
        {
            switch (rule.Match(forRules, comparison))
            {
                case AgentRuleMatch.Yes:
                    return DeniedBy(declared, whole);
                case AgentRuleMatch.Expanded:
                    return MayBe(rule.Text, declared.Source, whole);
            }
        }

        // The worktree rules' commands: git's state-writing verbs and the merges and CI starts of
        // gh, which keep an agent's worktree a thing the engine can measure.
        if (_ground.WorktreeRules)
        {
            foreach (var rule in _worktreeRunRules)
            {
                switch (rule.Match(forRules, comparison))
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

            if (MergesOrStartsCi(forRules) is { } reached)
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
            if (WebDecided(address, whole) is AgentPolicyAnswer.Denied denied)
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

    /// <summary>One segment of a shell pattern as the expression it matches a whole name by, in any case.</summary>
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
            else if (character == '[' && segment.IndexOf(']', at + 2) is var closing and > 0)
            {
                var set = segment[(at + 1)..closing];
                expression.Append('[').Append(set.StartsWith('!') ? "^" + System.Text.RegularExpressions.Regex.Escape(set[1..]) : System.Text.RegularExpressions.Regex.Escape(set)).Append(']');
                at = closing;
            }
            else
            {
                expression.Append(System.Text.RegularExpressions.Regex.Escape(character.ToString()));
            }
        }

        return new System.Text.RegularExpressions.Regex(
            expression.Append('$').ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
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
    /// <see cref="GhApiMutations"/>, a GraphQL query read from a file, or an alias made; null where
    /// it reaches none.
    /// </summary>
    private static string? MergesOrStartsCi(IReadOnlyList<string> words)
    {
        if (words.Count < 2 || !CommandAnalysis.Name(words[0]).Equals("gh", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        // An alias stands for any command, a refused one among them, under a name no rule knows.
        if (words[1].Equals("alias", StringComparison.OrdinalIgnoreCase) && words.Count > 2 && words[2] is "set" or "import")
        {
            return $"gh alias {words[2]}, a name for any command";
        }

        if (!words[1].Equals("api", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        foreach (var word in words.Skip(2))
        {
            if (CiEndpoint().IsMatch(word.Split('?', 2)[0].TrimEnd('/')))
            {
                return $"the endpoint '{word}'";
            }

            if (GhApiMutations.FirstOrDefault(mutation => word.Contains(mutation, StringComparison.OrdinalIgnoreCase)) is { } named)
            {
                return $"the mutation {named}";
            }
        }

        // An endpoint the shell expands may be any endpoint, a merge's among them.
        var arguments = words.Skip(1).ToList();

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

    /// <summary>A REST path that merges or starts CI: the endpoints of <see cref="GhApiEndpoints"/>, at a path's end.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(^|/)(pulls/[^/]+/(merge|update-branch)|merges|merge-upstream|dispatches|rerun|rerun-failed-jobs|approve|rerequest)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CiEndpoint();

    /// <summary>
    /// The root action <paramref name="word"/> names, in the list's own spelling: the word itself,
    /// each word its braces spell, and each word a script inside it would run - a command another
    /// program runs from one argument (<c>ssh host 'sudo x'</c>) is read so too; null where it names
    /// none.
    /// </summary>
    private static string? RootActionOf(string word)
    {
        IEnumerable<string> candidates = CommandAnalysis.BraceExpansions(word);

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

    /// <summary>One web request, decided: no web where the step has none, then the <c>Web</c> rules, then <c>webDomains</c>.</summary>
    /// <param name="address">The address as it was named.</param>
    /// <param name="noWebSubject">What the no-web sentence names beside the address: the address itself for a tool's ask, the command for a fetcher.</param>
    private AgentPolicyAnswer WebDecided(string address, string noWebSubject)
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

        foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Web))
        {
            if (rule.MatchesHost(host))
            {
                return DeniedBy(declared, host);
            }
        }

        if (_webDomains.Count > 0 && !_webDomains.Any(domain => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase)))
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

        foreach (var candidate in text.Contains("://", StringComparison.Ordinal) ? [text] : new[] { "https://" + text })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && !uri.IsFile && uri.Host.Length > 0)
            {
                // A host may be spelled with its root dot ('pastebin.com.'), which is the same
                // host: trimmed here, so a rule written without it meets it.
                return uri.Host.Trim('[', ']').TrimEnd('.');
            }
        }

        throw new ArgumentException($"'{address}' names no host", nameof(address));
    }

    /// <summary>Whether the path, as asked or as resolved, is a credential slot of this tool or lies in a Claude Code directory: no file tool of an agent reads or writes either.</summary>
    private AgentPolicyAnswer? Unreached(string asked, string resolved)
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
    /// The directory a command moves the commands after it to - <c>cd</c>, <c>pushd</c>,
    /// <c>chdir</c>, PowerShell's <c>Set-Location</c> - as a path is resolved against the places
    /// already reached; the home where it names none, and null where it is no such command or names
    /// a place this cannot tell.
    /// </summary>
    private string? MovedTo(IReadOnlyList<string> command, IReadOnlyList<string> places)
    {
        var environment = CommandAnalysis.EnvironmentOf(command);

        if (environment == command.Count
            || !new[] { "cd", "pushd", "chdir", "Set-Location", "sl" }.Contains(CommandAnalysis.Name(command[environment]), StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = command.Skip(environment + 1).FirstOrDefault(word => !word.StartsWith('-') || word == "-");

        if (target == "-")
        {
            return null;
        }

        try
        {
            return target is null ? _platform.HomeDirectory : Path.GetFullPath(HomeExpanded(target), places[^1]);
        }
        catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="word"/> names a path into a credential slot or a Claude Code
    /// directory, resolved as a tool's path resolves against each place the command may stand in:
    /// the word itself, the file a redirect names, the value after an <c>=</c> (<c>--file=...</c>,
    /// an assignment's), and each word a script inside it would name, a home expanded in each.
    /// </summary>
    private AgentPolicyAnswer? Named(string word, IReadOnlyList<string> places)
    {
        var candidates = new List<string> { word };

        if (CommandAnalysis.IsRedirect(word))
        {
            candidates.Add(word.TrimStart("0123456789<>|".ToCharArray()));
        }

        if (word.IndexOf('=', StringComparison.Ordinal) is var equals and >= 0 && equals + 1 < word.Length)
        {
            candidates.Add(word[(equals + 1)..]);
        }

        if (word.Any(char.IsWhiteSpace))
        {
            candidates.AddRange(word.Split([' ', '\t', '\n', '\r', ';', '|', '&', '(', ')', '`', '\'', '"'], StringSplitOptions.RemoveEmptyEntries));
        }

        // Each word its braces spell, and the path a file URL names: curl and its kind read a file so.
        if (word.Contains('{', StringComparison.Ordinal))
        {
            candidates.AddRange(CommandAnalysis.BraceExpansions(word));
        }

        candidates.AddRange([.. candidates
            .Where(candidate => candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            .Select(candidate => Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : null)
            .OfType<string>()]);

        foreach (var (candidate, place) in candidates.Where(candidate => candidate.Length > 0).Distinct().SelectMany(candidate => places.Distinct().Select(place => (candidate, place))))
        {
            string asked;

            try
            {
                asked = Spelled(candidate, place);
            }
            catch (Exception exception) when (exception is ArgumentException or System.Security.SecurityException or PathTooLongException)
            {
                // A word that is no path at all ('--porcelain') is every command's ordinary argument.
                continue;
            }

            // A pattern the shell expands names every path it may match: refused where one of them
            // may be a slot or a Claude Code directory, or lie beneath one.
            if (asked.IndexOfAny(['*', '?', '[']) >= 0)
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

            foreach (var form in new[] { asked, _fileSystem.ResolveLinks(asked) })
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
    /// for where it lands. A path this cannot read, or one blank, is undecided.
    /// </summary>
    private AgentPolicyAnswer Decide(string path, Func<string, string, AgentPolicyAnswer> decide)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("it names no path");
            }

            var asked = Spelled(path, _ground.WorkingDirectory);

            return decide(asked, _fileSystem.ResolveLinks(asked));
        }
        catch (Exception exception) when (Undecidable(exception))
        {
            return Undecided(path, exception);
        }
    }

    /// <summary>
    /// <paramref name="path"/> spelled whole, as the system would reach it: a home expanded
    /// (<c>~</c>, <c>$HOME</c>, <c>%USERPROFILE%</c>), a device prefix dropped (<c>\\?\</c>,
    /// <c>\\.\</c>), made absolute against <paramref name="directory"/> - on Windows each short name
    /// along it given as its long name, as far as it exists, as the runtime makes a path absolute.
    /// </summary>
    private string Spelled(string path, string directory)
    {
        var text = HomeExpanded(path.Trim());

        foreach (var (prefix, replacement) in new[] { (@"\\?\UNC\", @"\\"), (@"\\?\", string.Empty), (@"\\.\", string.Empty), ("//?/UNC/", "//"), ("//?/", string.Empty), ("//./", string.Empty) })
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = replacement + text[prefix.Length..];
                break;
            }
        }

        return Path.GetFullPath(text, directory);
    }

    /// <summary>The path with a home at its start expanded to the user's own: <c>~</c>, <c>$HOME</c>, <c>${HOME}</c>, <c>$env:HOME</c>, <c>$env:USERPROFILE</c> and <c>%USERPROFILE%</c>.</summary>
    private string HomeExpanded(string path)
    {
        if (path == "~" || path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return _platform.HomeDirectory + path[1..];
        }

        foreach (var home in new[] { "${HOME}", "$HOME", "$env:HOME", "$env:USERPROFILE", "%USERPROFILE%" })
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

    /// <summary>Every directory of the ground, resolved once, its duplicates gone.</summary>
    private IReadOnlyList<string> Resolve(IReadOnlyList<string> directories)
        => [.. directories
            .Select(directory => _fileSystem.ResolveLinks(Whole(directory)))
            .Distinct(StringComparer.FromComparison(_platform.PathComparison))];
}
