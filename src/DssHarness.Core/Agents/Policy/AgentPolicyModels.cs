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
/// action of any agent reaches. The engine hands each step's own; a test hands one directly.
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
public sealed record AgentPolicyGround(
    string WorkingDirectory,
    IReadOnlyList<string> ReadRoots,
    IReadOnlyList<string> WritableRoots,
    IReadOnlyList<string> SlotDirectories,
    IReadOnlyList<string> ClaudeCodeDirectories,
    IReadOnlyList<AgentRestriction> Restrictions);
