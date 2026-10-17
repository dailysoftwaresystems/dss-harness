namespace DssHarness.Core.Orchestration;

/// <summary>What creating an agent was asked: its orchestrator, its name, its model, and what its making hands it.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Model">The model the agent works as, as a definition names one.</param>
/// <param name="Empty">Whether its making hands it nothing, rather than the main tree's uncommitted state.</param>
/// <param name="Session">The session to record for it, where one is given.</param>
public sealed record AgentCreateRequest(string StartDirectory, string Orchestrator, string Agent, string Model, bool Empty, string? Session);

/// <summary>What seeding an agent again was asked: what its seeding hands it, and whether it may overwrite its own work.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Empty">Whether it is handed nothing more, keeping what it was handed before.</param>
/// <param name="Force">Whether it is seeded over changes of its own.</param>
public sealed record AgentSeedRequest(string StartDirectory, string Orchestrator, string Agent, bool Empty, bool Force);

/// <summary>What an agent's making answered: where its worktree is, and what its seed recorded.</summary>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Name">The agent's name.</param>
/// <param name="Worktree">Its worktree's path.</param>
/// <param name="Base">The commit its worktree was made from; <see langword="null"/> where making it stopped before recording one.</param>
/// <param name="Empty">Whether it was handed nothing, as asked.</param>
/// <param name="Session">The session recorded for it, where one was; <see langword="null"/> where none.</param>
public sealed record AgentCreated(string Orchestrator, string Name, string Worktree, string? Base, bool Empty, string? Session);

/// <summary>What seeding an agent again answered: what it was handed.</summary>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Name">The agent's name.</param>
/// <param name="Empty">Whether it was handed nothing more, keeping what it was handed before.</param>
/// <param name="Handed">How many paths it was handed.</param>
public sealed record AgentSeeded(string Orchestrator, string Name, bool Empty, int Handed);

/// <summary>What refreshing an agent was asked, beside the request that says which paths: the agent, and whether it applies.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Request">What the refresh weighs: the paths, the exceptions, the stale copies and the merges.</param>
/// <param name="Apply">Whether to do the work rather than weigh it.</param>
/// <param name="Json">Whether to answer with one JSON document.</param>
public sealed record RefreshAgentRequest(string StartDirectory, string Orchestrator, string Agent, RefreshRequest Request, bool Apply, bool Json = false);

/// <summary>What moving an agent's base was asked, beside the request that says which paths: the agent, and whether it applies.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Request">What the move weighs: the paths to leave, to merge and to declare settled.</param>
/// <param name="Apply">Whether to do the work rather than weigh it.</param>
/// <param name="Json">Whether to answer with one JSON document.</param>
public sealed record RebaseAgentRequest(string StartDirectory, string Orchestrator, string Agent, RebaseRequest Request, bool Apply, bool Json = false);

/// <summary>What folding an agent was asked, beside the allowances its person lets it through with: the agent, and whether it applies.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Allowances">What the person folding it lets through that the fold otherwise refuses.</param>
/// <param name="Apply">Whether to do the work rather than weigh it.</param>
/// <param name="Json">Whether to answer with one JSON document.</param>
public sealed record FoldAgentRequest(string StartDirectory, string Orchestrator, string Agent, FoldAllowances Allowances, bool Apply, bool Json = false);

/// <summary>What deleting an agent was asked, beside the allowances its last fold is let through with.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Orchestrator">The orchestrator the agent belongs to.</param>
/// <param name="Agent">The agent's name.</param>
/// <param name="Allowances">What the person deleting it lets its fold through that it otherwise refuses.</param>
/// <param name="Apply">Whether to do the work rather than weigh it.</param>
/// <param name="DiscardUncommitted">Whether to abandon its work with nothing folded.</param>
/// <param name="Json">Whether to answer with one JSON document.</param>
public sealed record DeleteAgentRequest(string StartDirectory, string Orchestrator, string Agent, FoldAllowances Allowances, bool Apply, bool DiscardUncommitted, bool Json = false);
