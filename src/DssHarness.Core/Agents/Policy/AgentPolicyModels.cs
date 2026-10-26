namespace DssHarness.Core.Agents.Policy;

/// <summary>Where a rule that refused an action comes from, as its denial is said.</summary>
public enum AgentPolicySource
{
    /// <summary>A rule this tool itself holds, no configuration's and no agent's: the root actions, the credential
    /// slots, an agent's writable roots.</summary>
    Builtin,

    /// <summary><c>agentPolicy.restrictions</c> in config.json.</summary>
    Config,

    /// <summary>An agent's own list: a definition's <c>restrictions</c> or a step's.</summary>
    Agent,

    /// <summary>A refusal Claude Code itself made, not this tool: read later, by the client.</summary>
    ClaudeCode,
}

/// <summary>
/// What <see cref="AgentPolicy"/> answers of one action an agent asked for: allow, with the input as
/// asked, or deny, with the rule that refused it and the reason, which the model reads.
/// </summary>
/// <remarks>
/// There is no third answer, and no answer but deny where the evaluator cannot decide: a call the
/// engine cannot decide never runs, so the client that waits for this answer - Claude Code's host
/// ask, the tool runtime before a tool runs - holds every action behind it.
/// </remarks>
public abstract record AgentPolicyAnswer
{
    /// <summary>The action runs, with the input as asked.</summary>
    public sealed record Allowed : AgentPolicyAnswer;

    /// <summary>The action does not run, refused by <see cref="Rule"/> of <see cref="Source"/>, for <see cref="Reason"/>.</summary>
    /// <param name="Rule">The rule as it is written: a restriction's whole string, or a built-in's own name.</param>
    /// <param name="Source">Where <paramref name="Rule"/> comes from.</param>
    /// <param name="Reason">The sentence the model reads: what was refused, and by what.</param>
    public sealed record Denied(string Rule, AgentPolicySource Source, string Reason) : AgentPolicyAnswer;

    /// <summary>The one allowed answer, shared: it carries nothing to say.</summary>
    public static readonly AgentPolicyAnswer Allow = new Allowed();
}

/// <summary>One restriction, as the policy holds it: the rule's whole string, and where it comes from.</summary>
/// <param name="Text">The rule, one string: <c>Run(git push *)</c>, <c>Write(.github/**)</c>, <c>Read(secrets/**)</c>.</param>
/// <param name="Source">Where <paramref name="Text"/> is declared.</param>
public sealed record AgentRestriction(string Text, AgentPolicySource Source);

/// <summary>
/// What one agent's policy is decided against: where it works, what it may write, and the places no
/// action of any agent reaches. The engine is to hand each step's own once the agent step lands;
/// until then nothing builds one but a test.
/// </summary>
/// <param name="WorkingDirectory">
/// Where the agent's commands and relative paths resolve: its worktree, its work directory, or the
/// run's directory for the reporter.
/// </param>
/// <param name="ReadRoots">
/// Every root the agent reads from, its working directory among them: a <c>Read</c> rule's pattern is
/// matched against the path as it stands relative to each.
/// </param>
/// <param name="WritableRoots">
/// The roots a write may fall in, the agent's worktree and its own work directory among them: a write
/// under none of them is refused, whatever any rule says.
/// </param>
/// <param name="SlotDirectories">
/// The credential slots, as absolute directories of the main checkout: no file tool reads or writes
/// one, and no command names one. <c>HarnessLayout.SecretSlots</c> names them.
/// </param>
/// <param name="ClaudeCodeDirectories">
/// Every Claude Code directory a client of this run uses, and the tool's own process's: no command of
/// an agent names one.
/// </param>
/// <param name="Restrictions">
/// Every restriction that applies to this agent, all applied together: the configuration's, the
/// definition's and the step's. A restriction only ever refuses.
/// </param>
/// <param name="Capabilities">
/// What the agent may do: its definition's <c>capabilities</c>, less what its client cannot - the
/// web where the client has no key of its own for it. The one source both of the tools a launch
/// offers and of whether the agent has the web at all: without <c>web</c> every web request is
/// refused, a page and a command that runs a program which fetches alike.
/// </param>
/// <param name="WebDomains">
/// <c>agentPolicy.webDomains</c>: the allow list the host of every page an agent reads, and of every
/// result a search gives, must stand under - a search source's own requests are held to the
/// <c>Web(...)</c> rules alone. Null where every address is allowed, the machine's own and its
/// network's among them; never empty, for an empty list would read as no list - the configuration
/// refuses one, and so does the ground.
/// </param>
/// <param name="Writes">
/// The step's <c>writes</c>, its patterns relative to its worktree: the one source that narrows,
/// for where a step has it, a write under its worktree to the paths its patterns name. Null where
/// the step narrows nothing; never empty, for an empty list would read as no narrowing - a flow
/// refuses one, and so does the ground.
/// </param>
/// <param name="ReporterRoot">
/// The one root the run's reporter may read, where this agent is one: reads outside it are denied,
/// and with it no command, no write and no web runs at all. Null for every other agent.
/// </param>
/// <param name="WorktreeRules">
/// Whether the worktree rules are on (<c>agentPolicy.worktreeRules</c>, which the configuration
/// holds on where nothing is said). The tool's own verbs and <c>run-side-task</c> are refused
/// whatever this says.
/// </param>
/// <param name="ProtectedPaths">
/// Whether Claude Code's protected paths are refused on every client
/// (<c>agentPolicy.protectedPaths</c>, which the configuration holds on where nothing is said).
/// </param>
/// <param name="StateDirectories">
/// The tool's own state, as absolute paths under each tree this agent touches, from
/// <c>HarnessLayout.StatePaths</c>: written by the tool's verbs alone, never by an agent's - the
/// agent's own work directory excepted, for it is a writable root.
/// </param>
public sealed record AgentPolicyGround(
    string WorkingDirectory,
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<string> SlotDirectories,
    IReadOnlyList<string> ClaudeCodeDirectories,
    IReadOnlyList<AgentRestriction> Restrictions,
    IReadOnlyList<AgentCapability> Capabilities,
    IReadOnlyList<string>? WebDomains,
    IReadOnlyList<string>? Writes,
    string? ReporterRoot,
    bool WorktreeRules,
    bool ProtectedPaths,
    IReadOnlyList<string> StateDirectories)
{
    /// <inheritdoc cref="AgentPolicyGround" path="/param[@name='WebDomains']"/>
    public IReadOnlyList<string>? WebDomains { get; init => field = Listed(value, nameof(WebDomains)); } = Listed(WebDomains, nameof(WebDomains));

    /// <inheritdoc cref="AgentPolicyGround" path="/param[@name='Writes']"/>
    public IReadOnlyList<string>? Writes { get; init => field = Listed(value, nameof(Writes)); } = Listed(Writes, nameof(Writes));

    /// <summary>Every capability, in the order a definition names them.</summary>
    public static IReadOnlyList<AgentCapability> EveryCapability { get; } = [AgentCapability.Read, AgentCapability.Write, AgentCapability.Run, AgentCapability.Web];

    /// <summary>The capabilities the run's reporter acts by, whose reads are narrowed to its one root: reading, and no other.</summary>
    public static IReadOnlyList<AgentCapability> ReporterCapabilities { get; } = [AgentCapability.Read];

    /// <summary>
    /// The capabilities the agent's tools are given by, on every client: its own, or <see cref="ReporterCapabilities"/>
    /// where it is the run's reporter. The one rule of what the reporter is given.
    /// </summary>
    public IReadOnlyList<AgentCapability> Given => ReporterRoot is null ? Capabilities : ReporterCapabilities;

    /// <summary>Whether the agent has the web at all: the capability <c>web</c> among its <see cref="Capabilities"/>.</summary>
    public bool Web => Capabilities.Contains(AgentCapability.Web);

    /// <summary>
    /// Every root of the agent's, each once as <paramref name="comparison"/> compares names: its working
    /// directory, then each root it reads, then each it writes. The one list the policy decides over,
    /// the launch's settings write their rules for, and a client hands its program.
    /// </summary>
    /// <param name="comparison">How the platform compares names.</param>
    public IReadOnlyList<string> Roots(StringComparison comparison)
        => [.. new[] { WorkingDirectory }.Concat(ReadRoots).Concat(WritableRoots).Distinct(StringComparer.FromComparison(comparison))];

    /// <summary>A list that narrows, as given: null where it narrows nothing, and refused empty, which would read as no list.</summary>
    private static IReadOnlyList<string>? Listed(IReadOnlyList<string>? list, string name)
        => list is { Count: 0 }
            ? throw new ArgumentException($"{name} is given empty, which would read as no list: null where it narrows nothing, else the entries it allows", name)
            : list;
}
