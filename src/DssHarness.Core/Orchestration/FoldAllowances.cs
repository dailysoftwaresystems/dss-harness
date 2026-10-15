using DssHarness.Core.Anchors;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// What the person folding an agent lets its fold do that it otherwise refuses, each said by name. Nothing is let through
/// that nobody named, and a name for a row the agent never filed is refused as the typo it usually is; a name for a row an
/// earlier fold of the agent applied, which the agent declares as it did then, is let stand, so the command line of that
/// fold runs again.
/// </summary>
public sealed record FoldAllowances
{
    /// <summary>The option that takes a clean three-way merge of a path both trees changed.</summary>
    public const string MergeOption = "--merge";

    /// <summary>The option that leaves out of the fold a path reconciled by hand.</summary>
    public const string SettledOption = "--settled";

    /// <summary>What <see cref="MergeOption"/> does to a path, as a refusal of a path named twice says it.</summary>
    public const string MergeDoes = "merged for you";

    /// <summary>What <see cref="SettledOption"/> does to a path.</summary>
    public const string SettledDoes = "reconciled by hand";

    /// <summary>
    /// Paths both trees changed whose three-way merge, where it is clean, the fold may write into both:
    /// <see cref="MergeOption"/>.
    /// </summary>
    public IReadOnlyList<string> Merge { get; init; } = [];

    /// <summary>Paths reconciled in the main tree by hand, whose state there the fold hands the agent: <see cref="SettledOption"/>.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>The ids of rows the agent filed that its fold may make: <see cref="AnchorBatchRequest.NewOption"/>.</summary>
    public IReadOnlyList<string> New { get; init; } = [];

    /// <summary>
    /// The cells of existing rows its fold may write though their stored text does not survive, each as
    /// <see cref="AnchorRowCell.Form"/>: <see cref="AnchorBatchRequest.AcceptLostOption"/>.
    /// </summary>
    public IReadOnlyList<string> AcceptLost { get; init; } = [];

    /// <summary>
    /// The cells of rows an earlier fold of the agent applied that the registries and the agent both changed since, each
    /// otherwise, whose agent's text its fold may write over the registries', each as <see cref="AnchorRowCell.Form"/>:
    /// <see cref="AnchorBatchRequest.AcceptOverOption"/>. It accepts, for a prose cell, the loss of what the registries hold.
    /// </summary>
    public IReadOnlyList<string> AcceptOver { get; init; } = [];

    /// <summary>The options given, as the command line names them, for a refusal that none of them may be given.</summary>
    public IReadOnlyList<string> Given =>
        [.. new (string Option, int Count)[]
            {
                (MergeOption, Merge.Count),
                (SettledOption, Settled.Count),
                (AnchorBatchRequest.NewOption, New.Count),
                (AnchorBatchRequest.AcceptLostOption, AcceptLost.Count),
                (AnchorBatchRequest.AcceptOverOption, AcceptOver.Count),
            }
            .Where(option => option.Count > 0)
            .Select(option => option.Option)];

    /// <summary>
    /// What is wrong with how they are spelt, before anything is read: a path that could name nothing in the tree, one
    /// named both to be merged and as settled by hand, an id no anchor could have, a cell that is not
    /// <see cref="AnchorRowCell.Form"/> - for <see cref="AnchorBatchRequest.AcceptLostOption"/>, a prose cell; for
    /// <see cref="AnchorBatchRequest.AcceptOverOption"/>, any of a row's five; <see langword="null"/> where nothing is.
    /// </summary>
    public string? Problem()
    {
        if (PathsProblem(Merge, Settled) is { } paths)
        {
            return paths;
        }

        if (New.Select(AnchorRowCell.IdProblem).FirstOrDefault(problem => problem is not null) is { } id)
        {
            return $"{AnchorBatchRequest.NewOption} {id}.";
        }

        if (AcceptLost.FirstOrDefault(cell => AnchorRowCell.Parse(cell) is not { IsProse: true }) is { } cell)
        {
            return $"{AnchorBatchRequest.AcceptLostOption} '{cell}' is not {AnchorRowCell.Form}, where the id is an anchor's and the cell is "
                + $"{string.Join(", ", AnchorCellNames.Text)}.";
        }

        if (AcceptOver.FirstOrDefault(cell => AnchorRowCell.Parse(cell) is null) is { } over)
        {
            return $"{AnchorBatchRequest.AcceptOverOption} '{over}' is not {AnchorRowCell.Form}, where the id is an anchor's and the cell is "
                + $"{string.Join(", ", AnchorCellNames.All)}.";
        }

        return null;
    }

    /// <summary>The cells <see cref="AcceptLost"/> names, each once.</summary>
    /// <exception cref="InvalidOperationException">One is not <see cref="AnchorRowCell.Form"/>: <see cref="Problem"/> was not asked first.</exception>
    public IReadOnlyList<AnchorRowCell> LostCells => Cells(AcceptLost);

    /// <summary>The cells <see cref="AcceptOver"/> names, each once.</summary>
    /// <exception cref="InvalidOperationException">One is not <see cref="AnchorRowCell.Form"/>: <see cref="Problem"/> was not asked first.</exception>
    public IReadOnlyList<AnchorRowCell> OverCells => Cells(AcceptOver);

    /// <summary>
    /// What is wrong with the paths named with <see cref="MergeOption"/> and with <see cref="SettledOption"/>, whichever
    /// command takes the two, before anything is read: a path that could name nothing in the tree, or one named with both.
    /// Null where nothing is.
    /// </summary>
    /// <param name="merge">The paths named to be merged.</param>
    /// <param name="settled">The paths named as settled by hand.</param>
    public static string? PathsProblem(IReadOnlyList<string> merge, IReadOnlyList<string> settled)
        => OrchestrationRules.PathsProblem(merge, MergeOption)
            ?? OrchestrationRules.PathsProblem(settled, SettledOption)
            ?? OrchestrationRules.NamedTwiceProblem((MergeOption, merge, MergeDoes), (SettledOption, settled, SettledDoes));

    /// <summary>The arguments that name <paramref name="merge"/> and <paramref name="settled"/> again, each path once, as a command line takes them.</summary>
    /// <param name="merge">The paths named to be merged.</param>
    /// <param name="settled">The paths named as settled by hand.</param>
    public static IEnumerable<string> PathArguments(IReadOnlyList<string> merge, IReadOnlyList<string> settled)
        => OrchestrationReports.PathArguments(MergeOption, merge).Concat(OrchestrationReports.PathArguments(SettledOption, settled));

    /// <summary>The arguments that give them again, each as a command line takes it, for one a report names.</summary>
    public IEnumerable<string> Arguments() =>
        PathArguments(Merge, Settled)
            .Concat(New.Distinct(StringComparer.Ordinal).SelectMany(id => new[] { AnchorBatchRequest.NewOption, id }))
            .Concat(LostCells.SelectMany(cell => new[] { AnchorBatchRequest.AcceptLostOption, cell.ToString() }))
            .Concat(OverCells.SelectMany(cell => new[] { AnchorBatchRequest.AcceptOverOption, cell.ToString() }));

    /// <summary>The cells <paramref name="named"/> names, each once.</summary>
    private static IReadOnlyList<AnchorRowCell> Cells(IReadOnlyList<string> named)
        => [.. named.Select(cell => AnchorRowCell.Parse(cell) ?? throw new InvalidOperationException($"'{cell}' is not {AnchorRowCell.Form}; its problem was not asked first.")).Distinct()];
}
