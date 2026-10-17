using DssHarness.Core.Execution;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// A consent a dry run of the fold family lacks, by kind: what its question is named by, which a caller tells from one
/// another without reading a line.
/// </summary>
public enum ConsentKind
{
    /// <summary>A row no registry holds that nobody named new (<c>--new</c>).</summary>
    FoldNewRows,

    /// <summary>A cell of a row that does not keep its stored text word for word (<c>--accept-lost</c>).</summary>
    FoldLostRows,

    /// <summary>A cell the registries and the agent both changed (<c>--accept-over</c>).</summary>
    FoldOverCells,

    /// <summary>A path both trees changed whose three-way merge is clean (<c>--merge</c>).</summary>
    FoldTwoChanges,
}

/// <summary>
/// What a fold, or the work a delete folds back, did: every path it touched and every row it applied, which nothing
/// else reports as one answer.
/// </summary>
/// <param name="Written">The paths written into the main tree, in order.</param>
/// <param name="Deleted">The paths removed from it, in order.</param>
/// <param name="Merged">The paths merged into both trees, in order.</param>
/// <param name="Handed">The paths settled by hand handed to the agent, in order, each with the digest it was handed.</param>
/// <param name="Registries">The registries its rows changed, by name.</param>
/// <param name="Rows">The ids of the rows it applied: the fold's output.</param>
/// <param name="OverCells">The cells both changed that its <c>--accept-over</c> took, as cells are named.</param>
public sealed record AgentFolded(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Merged,
    IReadOnlyList<HandedPath> Handed,
    IReadOnlyList<string> Registries,
    IReadOnlyList<string> Rows,
    IReadOnlyList<string> OverCells);

/// <summary>
/// What one answer of fold-agent, delete-agent, refresh-agent or rebase-agent knows beyond its lines: the document the
/// verbs answer with under <c>--json</c> is this record serialised, and the engine reads it typed - one thing, so the
/// two cannot drift. What the lines still say, the record does not repeat.
/// </summary>
/// <param name="ExitCode">The code the command exits with.</param>
/// <param name="Summary">The line it ends on.</param>
/// <param name="Applied">Whether <c>--apply</c> was given.</param>
/// <param name="Standing">What is wanted of the caller; null where what the command weighs could not be read.</param>
/// <param name="Holders">Who holds what the <c>--apply</c> takes, where the command asked; null where it asked nobody.</param>
/// <param name="Lines">The lines under the summary.</param>
/// <param name="Needs">Each consent the request lacks, by kind, and nothing else.</param>
/// <param name="Folded">What a fold wrote, merged, handed and applied; null for every other answer and a fold that wrote nothing.</param>
public sealed record AgentVerbFacts(
    int ExitCode,
    string Summary,
    bool Applied,
    RequestStanding? Standing,
    TreeHeld? Holders,
    IReadOnlyList<string> Lines,
    IReadOnlyList<ConsentKind> Needs,
    AgentFolded? Folded = null);
