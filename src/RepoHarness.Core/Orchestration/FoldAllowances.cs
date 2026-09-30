using RepoHarness.Core.Anchors;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// What the person folding an agent lets its fold do that it otherwise refuses, each said by name: nothing is let
/// through that nobody named, and a name that lets nothing through is refused as the typo it usually is.
/// </summary>
public sealed record FoldAllowances
{
    /// <summary>Nothing let through.</summary>
    public static FoldAllowances None { get; } = new();

    /// <summary>Paths reconciled in the main tree by hand, left out of the fold: <c>--settled</c>.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>The ids of rows the agent filed that its fold may make: <c>--new</c>.</summary>
    public IReadOnlyList<string> New { get; init; } = [];

    /// <summary>The cells of existing rows its fold may write though their stored text does not survive, as <c>&lt;ID&gt;:&lt;cell&gt;</c>: <c>--accept-lost</c>.</summary>
    public IReadOnlyList<string> AcceptLost { get; init; } = [];

    /// <summary>The options given, as the command line names them, for a refusal that none of them may be given.</summary>
    public IReadOnlyList<string> Given =>
        [.. new[] { ("--settled", Settled.Count), ("--new", New.Count), ("--accept-lost", AcceptLost.Count) }
            .Where(option => option.Item2 > 0)
            .Select(option => option.Item1)];

    /// <summary>What is wrong with how they are spelt, before anything is looked at; <see langword="null"/> where nothing is.</summary>
    public string? Problem()
    {
        if (New.FirstOrDefault(id => id.Length == 0 || id.Any(char.IsWhiteSpace)) is { } id)
        {
            return $"--new '{id}' is not an anchor id: name the id of a row the agent filed.";
        }

        if (AcceptLost.FirstOrDefault(cell => AnchorRowCell.Parse(cell) is null) is { } cell)
        {
            return $"--accept-lost '{cell}' is not <ID>:<cell>, where the cell is {string.Join(", ", AnchorCellNames.Text)}.";
        }

        return null;
    }

    /// <summary>The cells <see cref="AcceptLost"/> names, each once.</summary>
    public IReadOnlyList<AnchorRowCell> LostCells => [.. AcceptLost.Select(AnchorRowCell.Parse).OfType<AnchorRowCell>().Distinct()];

    /// <summary>The arguments that give them again, for a command line a report names.</summary>
    public IEnumerable<string> Arguments() =>
        Settled.SelectMany(path => new[] { "--settled", path })
            .Concat(New.Distinct(StringComparer.Ordinal).SelectMany(id => new[] { "--new", id }))
            .Concat(LostCells.SelectMany(cell => new[] { "--accept-lost", cell.ToString() }));
}
