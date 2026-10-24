namespace DssHarness.Core.Execution;

/// <summary>
/// One leg a run placed: its name, and whether it works in a copy - its own under <c>.legs</c>, or the one its host's
/// sync carries there - which reads the tree only to be made, or in its tree itself.
/// </summary>
/// <param name="Leg">The leg's name, as the configuration names it.</param>
/// <param name="InACopy">Whether the leg works in a copy of the tree.</param>
public sealed record LegPlace(string Leg, bool InACopy);

/// <summary>
/// What the caller of one leg run is told of its legs as the run goes, beside the ledger it ends with: every leg the run
/// placed and where each works, each copy once it is made, and each leg once it has ended. A step of an orchestrator run
/// lets its tree go by it: a leg in a copy reads the tree only until its copy is made, and one in the tree itself until
/// it ends.
/// </summary>
/// <remarks>Said from the run's own tasks, several legs at once: an implementation takes calls from any thread.</remarks>
public interface ILegRunWatch
{
    /// <summary>
    /// The legs the run places, once, before any of them reads the tree: every one that will work, and where. A leg the
    /// run turns away before placing it - skipped, unavailable - is never placed, and is told of only as it ends.
    /// </summary>
    /// <param name="legs">Every leg placed, in the order the run placed them.</param>
    void Placed(IReadOnlyList<LegPlace> legs);

    /// <summary>
    /// The copy <paramref name="leg"/> works in is made: its sync has ended, before the leg's first phase. Never said of
    /// a leg whose copy was not made - its sync refused or failed - which is told of only as it ends.
    /// </summary>
    /// <param name="leg">The leg's name.</param>
    void CopyMade(string leg);

    /// <summary><paramref name="leg"/> has ended, its line recorded: whatever of the tree it was to read, it has read.</summary>
    /// <param name="leg">The leg's name.</param>
    void Ended(string leg);
}
