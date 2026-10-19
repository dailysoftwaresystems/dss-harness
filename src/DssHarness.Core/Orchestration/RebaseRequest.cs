namespace DssHarness.Core.Orchestration;

/// <summary>
/// What a move of an agent's base is let through that it otherwise refuses, each path said by name: a path the agent
/// changed that the main tree committed a change to, merged three ways where its merge is clean
/// (<see cref="FoldAllowances.MergeOption"/>), or kept as the agent's copy, reconciled by hand
/// (<see cref="FoldAllowances.SettledOption"/>). Nothing is let through that nobody named, and the two are spelt and
/// checked as a fold's are.
/// </summary>
public sealed record RebaseRequest
{
    /// <summary>The paths whose clean three-way merge is written into the agent's worktree: <see cref="FoldAllowances.MergeOption"/>.</summary>
    public IReadOnlyList<string> Merge { get; init; } = [];

    /// <summary>The paths whose copy the agent keeps, reconciled by hand: <see cref="FoldAllowances.SettledOption"/>.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>
    /// What is wrong with how it is spelt, before anything is read: a path that could name nothing in the tree, or one
    /// named both to be merged and as settled; <see langword="null"/> where nothing is.
    /// </summary>
    /// <param name="spelling">Whose words the refusal names each option in: the command line's flags, or a workflow step's keys.</param>
    public string? Problem(OptionSpelling spelling = OptionSpelling.Flag) => FoldAllowances.PathsProblem(Merge, Settled, spelling);

    /// <summary>The arguments that ask for this move again, each as a command line takes it: every path it names, once.</summary>
    public IEnumerable<string> Arguments() => FoldAllowances.PathArguments(Merge, Settled);
}
