namespace DssHarness.Core.Orchestration;

/// <summary>What creating an orchestrator was asked: its name, its model, and what it runs.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Name">The orchestrator's name.</param>
/// <param name="Model">The model its agents work as, as a definition names one.</param>
/// <param name="Parallel">At most how many of its agents hold a worktree at once; null for its default.</param>
/// <param name="Session">The session to record for its transcripts, where one is given.</param>
/// <param name="MustBeNew">
/// Whether a record of that name being taken refuses it, rather than being answered as asked: the mode a run's start
/// creates its own record in, which a name someone else took must not overwrite. Nothing of the command line sets it.
/// </param>
public sealed record OrchestratorCreateRequest(string StartDirectory, string Name, string Model, int? Parallel, string? Session, bool MustBeNew = false);

/// <summary>What deleting an orchestrator was asked.</summary>
/// <param name="StartDirectory">The directory the command was run from.</param>
/// <param name="Name">The orchestrator's name.</param>
/// <param name="DeleteEvidence">Whether to delete the evidence its agents kept, rather than only the orchestrator.</param>
public sealed record OrchestratorDeleteRequest(string StartDirectory, string Name, bool DeleteEvidence);

/// <summary>What creating an orchestrator answered.</summary>
/// <param name="Name">The orchestrator's name.</param>
/// <param name="Existed">Whether it existed already, and only a session was recorded.</param>
/// <param name="Parallel">At most how many of its agents hold a worktree at once, as it stands now.</param>
/// <param name="Session">The session recorded for it, where one was; <see langword="null"/> where none.</param>
public sealed record OrchestratorCreated(string Name, bool Existed, int Parallel, string? Session);

/// <summary>What deleting an orchestrator answered.</summary>
/// <param name="Name">The orchestrator's name.</param>
/// <param name="EvidenceDeleted">Whether the evidence its agents kept was deleted with it.</param>
public sealed record OrchestratorDeleted(string Name, bool EvidenceDeleted);
