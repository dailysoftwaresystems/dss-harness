namespace RepoHarness.Core.Orchestration;

/// <summary>
/// What a refresh of an agent weighs, and what of the agent's own it leaves alone: the paths named, the directory the
/// anchor registries are kept in where none is, or every path the main tree moved; and the paths the agent changed that
/// stay as it changed them, each said by name. Nothing is left out that nobody named, and a name for a path the refresh
/// would not have refused is refused as the typo it usually is.
/// </summary>
public sealed record RefreshRequest
{
    /// <summary>The option that weighs every path the main tree moved.</summary>
    public const string AllOption = "--all";

    /// <summary>The option that leaves a path the agent changed as it changed it, and hands it the rest.</summary>
    public const string ExceptOption = "--except";

    /// <summary>The paths to refresh, relative to the tree; none where <see cref="All"/> is asked, or for the registries' directory.</summary>
    public IReadOnlyList<string> Paths { get; init; } = [];

    /// <summary>Whether every path the main tree moved is weighed, off the paths never handed to an agent: <see cref="AllOption"/>.</summary>
    public bool All { get; init; }

    /// <summary>Paths the agent changed, left as it changed them: <see cref="ExceptOption"/>.</summary>
    public IReadOnlyList<string> Except { get; init; } = [];

    /// <summary>A refresh of what is under <paramref name="paths"/>; of the registries' directory where there are none.</summary>
    /// <param name="paths">The paths, relative to the tree.</param>
    public static RefreshRequest Under(params string[] paths) => new() { Paths = paths };

    /// <summary>
    /// What is wrong with how it is spelt, before anything is read: a path that could name nothing in the tree, the whole
    /// tree named as a path, or paths given beside <see cref="AllOption"/>; <see langword="null"/> where nothing is.
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

        return OrchestrationRules.PathsProblem(Paths, "a path to refresh") ?? OrchestrationRules.PathsProblem(Except, ExceptOption);
    }

    /// <summary>
    /// The arguments that ask for this refresh again, each as a command line takes it: its paths, or
    /// <see cref="AllOption"/>, then <see cref="ExceptOption"/> for each path left as the agent changed it. Nothing where it
    /// names none, which is a refresh of the registries' directory.
    /// </summary>
    public string Arguments()
        => string.Join(
            ' ',
            new[] { All ? AllOption : string.Join(' ', Paths.Select(OrchestrationReports.Argument)), ExceptArguments(Except) }.Where(part => part.Length > 0));

    /// <summary>The arguments that leave <paramref name="paths"/> as the agent changed them, each as a command line takes it.</summary>
    /// <param name="paths">The paths, as git names them.</param>
    public static string ExceptArguments(IEnumerable<string> paths)
        => string.Join(' ', paths.SelectMany(path => new[] { ExceptOption, OrchestrationReports.Argument(path) }));
}
