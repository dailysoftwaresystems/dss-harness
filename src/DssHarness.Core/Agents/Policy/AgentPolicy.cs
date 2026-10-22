using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// The one evaluator of what an agent may do, the same on every client: everything is allowed
/// except the root actions, the places no agent reaches and the restrictions - which only ever
/// refuse, from every source at once, and never remove one another's.
/// </summary>
/// <remarks>
/// <para>
/// Asked of every action an agent asks for - a read, a write, a command - and answered
/// <see cref="AgentPolicyAnswer.Allow"/> with the input as asked or a denial with the rule and the
/// reason, which the model reads. Where the evaluator cannot decide, it denies: a call this tool
/// cannot decide never runs, so nothing runs while the host that would answer is silent or gone -
/// the client that waits for this answer holds every action behind it.
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
    /// agent whose HEAD left its base, and these are the verbs that move it. Reading git - status,
    /// diff, log, show, blame, grep - stays allowed.
    /// </summary>
    public static IReadOnlyList<string> GitStateVerbs { get; } =
        ["commit", "stash", "reset", "clean", "checkout", "switch", "restore", "rebase", "merge", "cherry-pick", "revert", "pull", "push", "am", "worktree", "branch", "tag", "update-ref", "config", "gc"];

    /// <summary>
    /// The <c>gh</c> commands that merge or start CI, refused by the worktree rules: never merges,
    /// never starts CI binds the agents too, not only the engine's own steps.
    /// </summary>
    public static IReadOnlyList<string> NeverCiCommands { get; } = ["gh pr merge", "gh workflow run", "gh run rerun"];

    /// <summary>
    /// This tool's own orchestrator and agent verbs, and the stop of a run of legs, refused whatever
    /// <c>worktreeRules</c> says: an agent does not move its own run, nor stop one.
    /// </summary>
    public static IReadOnlyList<string> ToolVerbs { get; } =
        ["create-orchestrator", "delete-orchestrator", "run-orchestrator", "resume-orchestrator", "answer-orchestrator", "pause-orchestrator", "stop-orchestrator", "create-agent", "seed-agent", "refresh-agent", "rebase-agent", "fold-agent", "delete-agent", "stop-run"];

    /// <summary>
    /// The files that hold the tool's own policy and what the next launch loads, refused as writes
    /// by the worktree rules: an agent does not edit its own policy or what the next launch loads.
    /// The skills directory's exception - a step whose <c>writes</c> names it - is read where the
    /// rule is matched, not written here.
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

    private readonly IFileSystem _fileSystem;
    private readonly IHostPlatform _platform;
    private readonly AgentPolicyGround _ground;
    private readonly IReadOnlyList<string> _readRoots;
    private readonly IReadOnlyList<string> _writableRoots;
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
        _allRoots = [.. _readRoots.Concat(_writableRoots).Distinct(StringComparer.FromComparison(_platform.PathComparison))];
        _slots = Resolve(ground.SlotDirectories);
        _claudeDirectories = Resolve(ground.ClaudeCodeDirectories);
        _state = Resolve(ground.StateDirectories ?? []);
        _rules = [.. ground.Restrictions.Select(declared => (AgentRule.Parse(declared.Text), declared))];
        _webDomains = ground.WebDomains ?? [];
        _writes = ground.Writes is { } writes
            ? [.. writes.Select(pattern => (GlobPattern.Parse(pattern), pattern))]
            : [];
        _worktreeRunRules =
            [.. GitStateVerbs.Select(verb => AgentRule.Parse($"Run(git {verb} *)")),
               .. NeverCiCommands.Select(command => AgentRule.Parse($"Run({command} *)"))];
        _worktreePolicyWrites = [.. WorktreePolicyWrites.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _protectedPaths = [.. ProtectedPathRules.Select(pattern => (GlobPattern.Parse(pattern), pattern))];
        _workingRoot = _fileSystem.ResolveLinks(Path.GetFullPath(ground.WorkingDirectory));
        _reporterRoot = ground.ReporterRoot is { } root ? _fileSystem.ResolveLinks(Path.GetFullPath(root)) : null;
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
                ? (IReadOnlyList<string>)["agentPolicy.webDomains is given empty: leave the key out to allow any public address, or name the domains an agent may reach"]
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
        ArgumentException.ThrowIfNullOrWhiteSpace(address);

        try
        {
            return WebDecided(address, address);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return Undecided(address, exception);
        }
    }

    /// <summary>
    /// Whether a path an agent would read may be read: a credential slot never, a <c>Read</c> rule
    /// where one matches; everything else is allowed, for reading is what an agent is for.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Decide(path, ReadDecision);
    }

    /// <summary>
    /// Whether a path an agent would write may be written: a credential slot never, a path under no
    /// writable root never, a <c>Write</c> rule where one matches.
    /// </summary>
    /// <param name="path">The path as the agent's tool asks it, relative to its working directory or absolute.</param>
    public AgentPolicyAnswer Write(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        return Decide(path, WriteDecision);
    }

    /// <summary>
    /// Whether a command an agent would run may run: flattened whole by
    /// <see cref="CommandAnalysis"/>, refused where any command it runs names a root action, a
    /// credential slot or a Claude Code directory, or where a <c>Run</c> rule matches one.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    public AgentPolicyAnswer Run(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        try
        {
            return RunDecided(CommandAnalysis.Commands(argv));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return Undecided(string.Join(' ', argv), exception);
        }
    }

    /// <summary>
    /// Whether a command an agent would run may run: the shell string is flattened whole by
    /// <see cref="CommandAnalysis"/> - its separators, substitutions, subshells, wrappers and the
    /// scripts it goes into - and each command it runs is refused where it names a root action, a
    /// credential slot or a Claude Code directory, or where a <c>Run</c> rule matches it.
    /// </summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    public AgentPolicyAnswer Run(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        try
        {
            return RunDecided(CommandAnalysis.Commands(shell));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return Undecided(shell, exception);
        }
    }

    /// <summary>A read, decided: a slot never, then the reporter's one root, then the <c>Read</c> rules on the path as asked and as resolved.</summary>
    private AgentPolicyAnswer ReadDecision(string asked, string resolved)
    {
        if (Slot(asked, resolved, "no file tool of an agent reads or writes one") is AgentPolicyAnswer.Denied slot)
        {
            return slot;
        }

        if (OutsideReporterRoot(asked, resolved) is AgentPolicyAnswer.Denied outside)
        {
            return outside;
        }

        return Matched(AgentRuleVerb.Read, asked, resolved);
    }

    /// <summary>A write, decided: a slot never, then the worktree rules, protected paths and the tool's own state, then the step's <c>writes</c>, the writable roots and the <c>Write</c> rules.</summary>
    private AgentPolicyAnswer WriteDecision(string asked, string resolved)
    {
        if (Slot(asked, resolved, "no file tool of an agent reads or writes one") is AgentPolicyAnswer.Denied slot)
        {
            return slot;
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
    /// The built-in write refusals: git's own files and the tool's policy and state under the
    /// worktree rules, the protected paths under their own switch, and the tool's own state in any
    /// root whatever the switches say - the agent's own work directory excepted, for it is a
    /// writable root of its own and the one exception the rule makes.
    /// </summary>
    private AgentPolicyAnswer? BuiltinWriteRefusal(string asked, string resolved)
    {
        var comparison = _platform.PathComparison;

        foreach (var root in _allRoots)
        {
            foreach (var form in new[] { asked, resolved })
            {
                if (Relative(root, form) is not { } relative)
                {
                    continue;
                }

                if (_ground.WorktreeRules)
                {
                    if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
                    {
                        return new AgentPolicyAnswer.Denied(
                            "Write(.git/**)",
                            AgentPolicySource.Builtin,
                            $"'{form}' is git's own: written by git's verbs and this tool's, never by an agent's file tool, whatever protectedPaths says");
                    }

                    foreach (var (pattern, text) in _worktreePolicyWrites)
                    {
                        if (pattern.Matches(relative, comparison) && !SkillsException(relative, comparison))
                        {
                            return new AgentPolicyAnswer.Denied(
                                $"Write({text})",
                                AgentPolicySource.Builtin,
                                $"'{form}' is denied by Write({text}), a worktree rule of this tool: an agent does not edit its own policy or what the next launch loads");
                        }
                    }
                }

                if (_ground.ProtectedPaths && _protectedPaths.Any(pair => pair.Pattern.Matches(relative, comparison)))
                {
                    var text = _protectedPaths.First(pair => pair.Pattern.Matches(relative, comparison)).Text;

                    return new AgentPolicyAnswer.Denied(
                        $"Write({text})",
                        AgentPolicySource.Builtin,
                        $"'{form}' is denied by Write({text}), a protected path of this tool: the writes Claude Code guards, refused on every client, so a role is not stopped on one where it passes on another");
                }
            }
        }

        // The tool's own state, in any root, whatever the switches say: written by its verbs alone.
        // What is under one of the agent's writable roots is not refused - its worktree under the
        // worktrees root, its own work directory under the run's - for those are the roots it was
        // given to write: asked of the path as it resolves, so no link inside a writable root
        // reaches the state by its other name.
        if (_state.Any(state => new[] { asked, resolved }.Any(form => PathContainment.IsInsideOrSame(state, form, comparison)))
            && !_writableRoots.Any(root => PathContainment.IsInsideOrSame(root, resolved, comparison)))
        {
            return new AgentPolicyAnswer.Denied(
                "the tool's own state",
                AgentPolicySource.Builtin,
                $"'{resolved}' is the tool's own state: written by its verbs alone, never by an agent's - its own work directory alone excepted");
        }

        return null;
    }

    /// <summary>
    /// Whether the path, as asked or as resolved, falls under the worktree rules' skills directory
    /// through a step whose <c>writes</c> names that directory itself: the one thing that opens it.
    /// A broader pattern that merely covers it - <c>**</c>, <c>.harness-config/**</c> - does not, for
    /// who may write a skill is something a person reads in the definition, not something an
    /// output's pattern decides.
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

        var comparison = _platform.PathComparison;

        foreach (var form in new[] { asked, resolved })
        {
            if (!PathContainment.IsInsideOrSame(_workingRoot, form, comparison))
            {
                continue;
            }

            if (Relative(_workingRoot, form) is { } relative && !_writes.Any(write => write.Pattern.Matches(relative, comparison)))
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
        // The root actions are this tool's own names, compared without regard to case on every
        // system; everything else of a command compares as this platform compares names.
        var roots = StringComparer.OrdinalIgnoreCase;
        var comparison = _platform.PathComparison;

        if (_reporterRoot is not null)
        {
            return new AgentPolicyAnswer.Denied(
                "the reporter's reads",
                AgentPolicySource.Builtin,
                "this agent reads and nothing else: it is the run's reporter, offered the tools that read and no other");
        }

        foreach (var command in commands)
        {
            // The tool's own verbs and run-side-task are refused whatever worktreeRules says: an
            // agent does not move its own run, nor leave its step's limits.
            if (command.Count >= 2
                && roots.Equals(CommandAnalysis.Name(command[0]), "dssharness")
                && ToolVerbs.Contains(command[1], StringComparer.OrdinalIgnoreCase))
            {
                return new AgentPolicyAnswer.Denied(
                    "the orchestrator and agent verbs",
                    AgentPolicySource.Builtin,
                    $"'dssharness {command[1]}' is refused: an agent does not move its own run");
            }

            if (command.Count >= 1 && roots.Equals(CommandAnalysis.Name(command[0]), "run-side-task"))
            {
                return new AgentPolicyAnswer.Denied(
                    "run-side-task",
                    AgentPolicySource.Builtin,
                    "'run-side-task' is refused: an agent does not leave its step's limits");
            }

            foreach (var word in command)
            {
                var name = CommandAnalysis.Name(word);

                // The sentence says the action it matched, in the one spelling the list holds, so a
                // word named in either case is refused in the same words.
                if (RootActions.FirstOrDefault(root => roots.Equals(root, name)) is { } action)
                {
                    return new AgentPolicyAnswer.Denied(
                        "the root actions",
                        AgentPolicySource.Builtin,
                        $"'{action}' is a root action: no agent of this tool acts as root");
                }

                if (Named(word) is AgentPolicyAnswer.Denied named)
                {
                    return named;
                }
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

            // The Run rules, against the command's own words: git's options dropped before its
            // subcommand, and a git -c that names an alias or a program git runs the git config it is.
            var forRules = CommandAnalysis.ForRules(command, comparison);

            foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb is AgentRuleVerb.Run))
            {
                if (rule.Matches(forRules, comparison))
                {
                    return DeniedBy(declared, string.Join(' ', command));
                }
            }

            // The worktree rules' commands: git's state-writing verbs and the merges and CI starts
            // of gh, which keep an agent's worktree a thing the engine can measure.
            if (_ground.WorktreeRules)
            {
                foreach (var rule in _worktreeRunRules)
                {
                    if (rule.Matches(forRules, comparison))
                    {
                        return new AgentPolicyAnswer.Denied(
                            rule.Text,
                            AgentPolicySource.Builtin,
                            $"'{string.Join(' ', command)}' is denied by {rule.Text}, a worktree rule of this tool");
                    }
                }
            }

            // A command that runs a program which fetches from the network is decided as a web
            // request too, by each address it names.
            foreach (var address in CommandAnalysis.WebAddresses(command))
            {
                if (WebDecided(address, string.Join(' ', command)) is AgentPolicyAnswer.Denied denied)
                {
                    return denied;
                }
            }
        }

        return AgentPolicyAnswer.Allow;
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

        var host = HostOf(address);

        if (!_ground.Web)
        {
            return new AgentPolicyAnswer.Denied(
                "the web capability",
                AgentPolicySource.Builtin,
                $"'{noWebSubject}' fetches '{address}' and this step has no web");
        }

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
    /// The host an address names: a URL's own host, or the first stretch of a bare
    /// <c>host[:port][/path]</c>, as a fetcher command names it. An address that names no host at
    /// all cannot be decided, so it is denied by the caller that catches.
    /// </summary>
    private static string HostOf(string address)
    {
        var text = address.Trim();

        foreach (var candidate in new[] { text, "https://" + text })
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                && uri.Host.Length > 0)
            {
                // A host may be spelled with its root dot ('pastebin.com.'), which is the same
                // host: trimmed here, so a rule written without it meets it.
                return uri.Host.TrimEnd('.');
            }
        }

        throw new ArgumentException($"'{address}' names no host", nameof(address));
    }

    /// <summary>Whether the path, as asked or as resolved, is a credential slot of this tool.</summary>
    private AgentPolicyAnswer? Slot(string asked, string resolved, string rule)
        => new[] { asked, resolved }
            .Where(form => _slots.Any(slot => PathContainment.IsInsideOrSame(slot, form, _platform.PathComparison)))
            .Select(form => new AgentPolicyAnswer.Denied("the credential slots", AgentPolicySource.Builtin, $"'{form}' is a credential slot of this tool: {rule}"))
            .Cast<AgentPolicyAnswer?>()
            .FirstOrDefault();

    /// <summary>Whether <paramref name="word"/> names a path into a credential slot or a Claude Code directory, resolved as a tool's path resolves.</summary>
    private AgentPolicyAnswer? Named(string word)
    {
        try
        {
            var asked = Path.GetFullPath(word, _ground.WorkingDirectory);

            foreach (var form in new[] { asked, _fileSystem.ResolveLinks(asked) })
            {
                if (_slots.Any(slot => PathContainment.IsInsideOrSame(slot, form, _platform.PathComparison)))
                {
                    return new AgentPolicyAnswer.Denied(
                        "the credential slots",
                        AgentPolicySource.Builtin,
                        $"'{word}' names a credential slot of this tool: no command of an agent touches one");
                }

                if (_claudeDirectories.Any(directory => PathContainment.IsInsideOrSame(directory, form, _platform.PathComparison)))
                {
                    return new AgentPolicyAnswer.Denied(
                        "the Claude Code directories",
                        AgentPolicySource.Builtin,
                        $"'{word}' lies in a Claude Code directory this run reads: no command of an agent touches one");
                }
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // A word that is no path at all ('--porcelain') is every command's ordinary argument.
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
    /// One path decision, made on the path as asked and as resolved: a restriction is tried on both,
    /// so no path reaches through a link past a rule that was written for where it lands.
    /// </summary>
    private AgentPolicyAnswer Decide(string path, Func<string, string, AgentPolicyAnswer> decide)
    {
        try
        {
            var asked = Path.GetFullPath(path, _ground.WorkingDirectory);

            return decide(asked, _fileSystem.ResolveLinks(asked));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or IOException or System.Security.SecurityException)
        {
            return Undecided(path, exception);
        }
    }

    /// <summary>
    /// The answer where this tool cannot decide: denied, never allowed, for nothing runs while the
    /// tool that would answer cannot - with why, so the fault is not lost in the refusal.
    /// </summary>
    private static AgentPolicyAnswer Undecided(string subject, Exception exception)
        => new AgentPolicyAnswer.Denied(
            "undecided",
            AgentPolicySource.Builtin,
            $"this tool could not decide '{subject}' ({exception.Message}): it denies what it cannot decide");

    /// <summary>
    /// The first path rule of <paramref name="verb"/> that matches the path, as asked or as
    /// resolved, against any of the agent's roots - the read roots, and for a write the writable
    /// roots beside them, for a write may fall in a root no read names - or allow where none does.
    /// </summary>
    private AgentPolicyAnswer Matched(AgentRuleVerb verb, string asked, string resolved)
    {
        var comparison = _platform.PathComparison;
        var roots = verb is AgentRuleVerb.Write
            ? _readRoots.Concat(_writableRoots).Distinct(StringComparer.FromComparison(comparison)).ToList()
            : _readRoots;

        foreach (var (rule, declared) in _rules.Where(pair => pair.Rule.Verb == verb))
        {
            foreach (var root in roots)
            {
                foreach (var form in new[] { asked, resolved })
                {
                    if (Relative(root, form) is { } relative && rule.Matches(relative, comparison))
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

    /// <summary>The path relative to <paramref name="root"/>, with forward separators, or null where it is not under it.</summary>
    private static string? Relative(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);

        // Only a whole '..' segment climbs out: a name that begins '..' and goes on is a name.
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || relative.StartsWith("..\\", StringComparison.Ordinal)
            || Path.IsPathRooted(relative) ? null
            : relative.Replace('\\', '/');
    }

    /// <summary>Every directory of the ground, resolved once, its duplicates gone.</summary>
    private IReadOnlyList<string> Resolve(IReadOnlyList<string> directories)
        => [.. directories
            .Select(directory => _fileSystem.ResolveLinks(Path.GetFullPath(directory)))
            .Distinct(StringComparer.FromComparison(_platform.PathComparison))];
}
