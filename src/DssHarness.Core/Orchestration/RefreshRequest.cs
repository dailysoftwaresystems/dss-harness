namespace DssHarness.Core.Orchestration;

/// <summary>
/// What a refresh of an agent weighs, and what of the agent's own it leaves alone: the paths named, the directory the
/// anchor registries are kept in where none is, or every path the main tree moved; the paths the agent changed that stay
/// as it changed them, each said by name; the stale copies handed the main tree's; and the paths both changed whose clean
/// three-way merge is written into the agent's worktree. Nothing is left out that nobody named, and a name for a path the
/// refresh would not have refused is refused as the typo it usually is.
/// </summary>
public sealed record RefreshRequest
{
    /// <summary>The option that weighs every path the main tree moved.</summary>
    public const string AllOption = "--all";

    /// <summary>The option that leaves a path the agent changed as it changed it, and hands it the rest.</summary>
    public const string ExceptOption = "--except";

    /// <summary>
    /// The option that hands the main tree's copy of a path whose copy in the agent is not its work: the main tree's own at
    /// an earlier commit.
    /// </summary>
    public const string StaleOption = "--stale";

    /// <summary>What <see cref="ExceptOption"/> does to a path, as a refusal of a path named twice says it.</summary>
    public const string ExceptDoes = "left as the agent changed it";

    /// <summary>What <see cref="StaleOption"/> does to a path.</summary>
    public const string StaleDoes = "handed the main tree's copy";

    /// <summary>The paths to refresh, relative to the tree; none where <see cref="All"/> is asked, or for the registries' directory.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Whether every path the main tree moved is weighed, off the paths never handed to an agent: <see cref="AllOption"/>.</summary>
    public bool All { get; init; }

    /// <summary>Paths the agent changed, left as it changed them: <see cref="ExceptOption"/>.</summary>
    public IReadOnlyList<string> Except { get; init; } = [];

    /// <summary>
    /// Paths whose copy in the agent is not its work, handed the main tree's copy - only where the main tree's history holds
    /// those very bytes at that path: <see cref="StaleOption"/>.
    /// </summary>
    public IReadOnlyList<string> Stale { get; init; } = [];

    /// <summary>
    /// Paths both the agent and the main tree changed whose clean three-way merge is written into the agent's worktree as
    /// its change, the path recorded as shared at the main tree's bytes: <see cref="FoldAllowances.MergeOption"/>.
    /// </summary>
    public IReadOnlyList<string> Merge { get; init; } = [];

    /// <summary>A refresh of what is under <paramref name="paths"/>; of the registries' directory where there are none.</summary>
    /// <param name="paths">The paths, relative to the tree.</param>
    public static RefreshRequest Under(params string[] paths) => new() { Paths = paths };

    /// <summary>
    /// What is wrong with how it is spelt, before anything is read: a path that could name nothing in the tree, the whole
    /// tree named as a path, paths given beside <see cref="AllOption"/>, or one named by two options that say different
    /// things of it - left as the agent changed it, handed the main tree's copy, merged; <see langword="null"/> where
    /// nothing is.
    /// </summary>
    public string? Problem()
    {
        if (Paths.Any(path => path.TrimEnd('/', '\\') is "." or ""))
        {
            return $"'{Paths.First(path => path.TrimEnd('/', '\\') is "." or "")}' names the whole tree, not a path in it: pass {AllOption} to weigh every path the main tree moved";
        }

        if (All && Paths.Count > 0)
        {
            return $"{AllOption} weighs every path the main tree moved, so it takes no path beside it: give the paths, or {AllOption}";
        }

        return OrchestrationRules.PathsProblem(Paths, "a path to refresh")
            ?? OrchestrationRules.PathsProblem(Except, ExceptOption)
            ?? OrchestrationRules.PathsProblem(Stale, StaleOption)
            ?? OrchestrationRules.PathsProblem(Merge, FoldAllowances.MergeOption)
            ?? OrchestrationRules.NamedTwiceProblem(
                (ExceptOption, Except, ExceptDoes),
                (StaleOption, Stale, StaleDoes),
                (FoldAllowances.MergeOption, Merge, FoldAllowances.MergeDoes));
    }

    /// <summary>
    /// The arguments that ask for this refresh again, each as a command line takes it: its paths, or
    /// <see cref="AllOption"/>, then <see cref="ExceptOption"/> for each path left as the agent changed it,
    /// <see cref="StaleOption"/> for each handed the main tree's copy and <see cref="FoldAllowances.MergeOption"/> for each
    /// merged. Nothing where it names none, which is a refresh of the registries' directory.
    /// </summary>
    public string Arguments()
        => string.Join(
            ' ',
            new[] { All ? AllOption : string.Join(' ', Paths.Select(OrchestrationReports.Argument)) }
                .Concat(OrchestrationReports.PathArguments(ExceptOption, Except))
                .Concat(OrchestrationReports.PathArguments(StaleOption, Stale))
                .Concat(OrchestrationReports.PathArguments(FoldAllowances.MergeOption, Merge))
                .Where(part => part.Length > 0));
}
