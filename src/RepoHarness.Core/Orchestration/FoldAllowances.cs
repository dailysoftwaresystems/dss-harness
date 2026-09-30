using RepoHarness.Core.Anchors;

namespace RepoHarness.Core.Orchestration;

/// <summary>
/// What the person folding an agent lets its fold do that it otherwise refuses, each said by name. Nothing is let through
/// that nobody named, and a name for a row the agent never filed is refused as the typo it usually is; a name for a row an
/// earlier fold of the agent applied, which the agent declares as it did then, is let stand, so the command line of that
/// fold runs again.
/// </summary>
public sealed record FoldAllowances
{
    /// <summary>The option that leaves out of the fold a path reconciled by hand.</summary>
    public const string SettledOption = "--settled";

    /// <summary>Paths reconciled in the main tree by hand, left out of the fold: <see cref="SettledOption"/>.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>The ids of rows the agent filed that its fold may make: <see cref="AnchorBatchRequest.NewOption"/>.</summary>
    public IReadOnlyList<string> New { get; init; } = [];

    /// <summary>
    /// The cells of existing rows its fold may write though their stored text does not survive, each as
    /// <see cref="AnchorRowCell.Form"/>: <see cref="AnchorBatchRequest.AcceptLostOption"/>.
    /// </summary>
    public IReadOnlyList<string> AcceptLost { get; init; } = [];

    /// <summary>The options given, as the command line names them, for a refusal that none of them may be given.</summary>
    public IReadOnlyList<string> Given =>
        [.. new (string Option, int Count)[] { (SettledOption, Settled.Count), (AnchorBatchRequest.NewOption, New.Count), (AnchorBatchRequest.AcceptLostOption, AcceptLost.Count) }
            .Where(option => option.Count > 0)
            .Select(option => option.Option)];

    /// <summary>
    /// What is wrong with how they are spelt, before anything is read: a path that could name nothing in the tree, an id no
    /// anchor could have, a cell that is not <see cref="AnchorRowCell.Form"/>; <see langword="null"/> where nothing is.
    /// </summary>
    public string? Problem()
    {
        if (OrchestrationRules.PathsProblem(Settled, SettledOption) is { } path)
        {
            return path;
        }

        if (New.Select(AnchorRowCell.IdProblem).FirstOrDefault(problem => problem is not null) is { } id)
        {
            return $"{AnchorBatchRequest.NewOption} {id}.";
        }

        if (AcceptLost.FirstOrDefault(cell => AnchorRowCell.Parse(cell) is null) is { } cell)
        {
            return $"{AnchorBatchRequest.AcceptLostOption} '{cell}' is not {AnchorRowCell.Form}, where the id is an anchor's and the cell is "
                + $"{string.Join(", ", AnchorCellNames.Text)}.";
        }

        return null;
    }

    /// <summary>The cells <see cref="AcceptLost"/> names, each once.</summary>
    /// <exception cref="InvalidOperationException">One is not <see cref="AnchorRowCell.Form"/>: <see cref="Problem"/> was not asked first.</exception>
    public IReadOnlyList<AnchorRowCell> LostCells =>
        [.. AcceptLost.Select(cell => AnchorRowCell.Parse(cell) ?? throw new InvalidOperationException($"'{cell}' is not {AnchorRowCell.Form}; its problem was not asked first.")).Distinct()];

    /// <summary>The arguments that give them again, each as a command line takes it, for one a report names.</summary>
    public IEnumerable<string> Arguments() =>
        Settled.SelectMany(path => new[] { SettledOption, OrchestrationReports.Argument(path) })
            .Concat(New.Distinct(StringComparer.Ordinal).SelectMany(id => new[] { AnchorBatchRequest.NewOption, id }))
            .Concat(LostCells.SelectMany(cell => new[] { AnchorBatchRequest.AcceptLostOption, cell.ToString() }));
}
