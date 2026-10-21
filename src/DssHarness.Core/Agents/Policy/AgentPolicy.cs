using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

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

    private readonly IFileSystem _fileSystem;
    private readonly IHostPlatform _platform;
    private readonly AgentPolicyGround _ground;
    private readonly IReadOnlyList<string> _readRoots;
    private readonly IReadOnlyList<string> _writableRoots;
    private readonly IReadOnlyList<string> _slots;
    private readonly IReadOnlyList<string> _claudeDirectories;
    private readonly IReadOnlyList<(AgentRule Rule, AgentRestriction Declared)> _rules;

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
        _slots = Resolve(ground.SlotDirectories);
        _claudeDirectories = Resolve(ground.ClaudeCodeDirectories);
        _rules = [.. ground.Restrictions.Select(declared => (AgentRule.Parse(declared.Text), declared))];
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
        ];
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

    /// <summary>A read, decided: a slot never, then the <c>Read</c> rules on the path as asked and as resolved.</summary>
    private AgentPolicyAnswer ReadDecision(string asked, string resolved)
    {
        if (Slot(asked, resolved, "no file tool of an agent reads or writes one") is AgentPolicyAnswer.Denied slot)
        {
            return slot;
        }

        return Matched(AgentRuleVerb.Read, asked, resolved);
    }

    /// <summary>A write, decided: a slot never, then the writable roots, then the <c>Write</c> rules.</summary>
    private AgentPolicyAnswer WriteDecision(string asked, string resolved)
    {
        if (Slot(asked, resolved, "no file tool of an agent reads or writes one") is AgentPolicyAnswer.Denied slot)
        {
            return slot;
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

    /// <summary>Each flattened command, refused by the first thing it runs that may not run.</summary>
    private AgentPolicyAnswer RunDecided(IReadOnlyList<IReadOnlyList<string>> commands)
    {
        // The root actions are this tool's own names, compared without regard to case on every
        // system; everything else of a command compares as this platform compares names.
        var roots = StringComparer.OrdinalIgnoreCase;
        var comparison = _platform.PathComparison;

        foreach (var command in commands)
        {
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
        }

        return AgentPolicyAnswer.Allow;
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
