using System.Text.Json.Serialization;
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
/// Why a request stands refused, by kind: a refusal no consent lifts and a person reconciles by hand told from every
/// other refusal, without reading a line.
/// </summary>
public enum RefusalKind
{
    /// <summary>
    /// A path both trees changed whose three-way merge conflicts - of a fold, a deletion's fold, or a move of an agent's
    /// base: no consent lifts it, and a person reconciles it.
    /// </summary>
    Conflict,

    /// <summary>
    /// Anything else that must be put right: a path, a row or an option refused for what it is, the agent's own state,
    /// or a consent nobody gave where the request is an <c>--apply</c>, which refuses it rather than asking.
    /// </summary>
    Other,
}

/// <summary>
/// What a fold, or the work a delete folds back, did: every path it touched and every row it applied, which nothing
/// else reports as one answer - what it did before it stopped, where it stopped part way.
/// </summary>
/// <param name="Written">The paths written into the main tree, in order.</param>
/// <param name="Deleted">The paths removed from it, in order.</param>
/// <param name="Merged">The paths merged into both trees, in order, as text: never the arm registry, which is <paramref name="ArmRegistry"/>.</param>
/// <param name="ArmRegistry">The arm registry, as git names it, where the fold merged it into both trees by arm; <see langword="null"/> where it merged none.</param>
/// <param name="Handed">The paths settled by hand handed to the agent, in order, each with the digest it was handed.</param>
/// <param name="Registries">The registry files its rows changed, as git names them, in order; empty where its rows were not applied.</param>
/// <param name="Rows">The ids of the rows it applied: the fold's output; empty where its rows were not applied.</param>
/// <param name="OverCells">The cells both changed that its <c>--accept-over</c> took, as cells are named; empty where its rows were not applied.</param>
public sealed record AgentFolded(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Merged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ArmRegistry,
    IReadOnlyList<HandedPath> Handed,
    IReadOnlyList<string> Registries,
    IReadOnlyList<string> Rows,
    IReadOnlyList<string> OverCells);

/// <summary>
/// What the plan of a fold, a deletion's fold, a refresh or a move of an agent's base names for each path and each row it
/// weighed: what a dry run would do and an <c>--apply</c> set out to, as measured, and what each consent it lacks and each
/// conflict it meets is about - which nothing else reports as one answer, and which a step of a run reads to ask, to give
/// a consent by name and to hold an <c>--apply</c> to what its dry run named.
/// </summary>
/// <param name="Taken">The paths a move of its base takes into its worktree as the new base holds them, in order; empty for the rest.</param>
/// <param name="Kept">The paths a move of its base keeps as they are, shared with the main tree, in order; empty for the rest.</param>
/// <param name="Handed">
/// The paths a refresh hands it from the main tree - each file copied and each deletion made - or a fold hands it, settled by
/// hand, in order; empty for a move of its base.
/// </param>
/// <param name="Merged">The paths both changed whose three-way merge is taken, in order, as text: never the arm registry, which is <paramref name="ArmRegistry"/>.</param>
/// <param name="ArmRegistry">The arm registry, as git names it, where it is merged by arm; <see langword="null"/> where it is not.</param>
public sealed record AgentPlanned(
    IReadOnlyList<string> Taken,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Handed,
    IReadOnlyList<string> Merged,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ArmRegistry)
{
    /// <summary>The paths a fold writes into the main tree, in order; empty for a refresh and a move of its base.</summary>
    public IReadOnlyList<string> Written { get; init; } = [];

    /// <summary>The paths a fold removes from the main tree, in order; empty for a refresh and a move of its base.</summary>
    public IReadOnlyList<string> Deleted { get; init; } = [];

    /// <summary>
    /// The registry files its rows change, as git names them - the one a row moves out of and the one it goes into - in
    /// order; empty for a refresh and a move of its base.
    /// </summary>
    public IReadOnlyList<string> Registries { get; init; } = [];

    /// <summary>The ids of the rows a fold applies, new or changed, in order; empty for a refresh and a move of its base.</summary>
    public IReadOnlyList<string> Rows { get; init; } = [];

    /// <summary>The paths both changed whose three-way merge is clean and that nobody named to be merged: what <see cref="ConsentKind.FoldTwoChanges"/> is about.</summary>
    public IReadOnlyList<string> Mergeable { get; init; } = [];

    /// <summary>The paths both changed whose three-way merge conflicts: what <see cref="RefusalKind.Conflict"/> is about, which no consent lifts.</summary>
    public IReadOnlyList<string> Conflicting { get; init; } = [];

    /// <summary>The ids of the rows no registry holds that nobody named new: what <see cref="ConsentKind.FoldNewRows"/> is about.</summary>
    public IReadOnlyList<string> NewRows { get; init; } = [];

    /// <summary>The cells of its rows that would lose stored text, nobody accepting it, as cells are named: what <see cref="ConsentKind.FoldLostRows"/> is about.</summary>
    public IReadOnlyList<string> LostCells { get; init; } = [];

    /// <summary>The cells the registries and the agent both changed that nobody named to be written over, as cells are named: what <see cref="ConsentKind.FoldOverCells"/> is about.</summary>
    public IReadOnlyList<string> OverCells { get; init; } = [];

    /// <summary>
    /// Every path a fold would write or remove in the main tree, those it would merge were it asked among them: what its
    /// <c>within</c> is held against - never the registry files its rows change, nor a path handed to the agent.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> WouldWrite
        => [.. Written.Concat(Deleted).Concat(Merged).Concat(ArmRegistry is { } arms ? [arms] : []).Concat(Mergeable).Distinct(StringComparer.Ordinal)];

    /// <summary>What a consent of <paramref name="kind"/> it lacks is about: the rows, the cells or the paths it names.</summary>
    /// <param name="kind">The consent.</param>
    public IReadOnlyList<string> Named(ConsentKind kind) => kind switch
    {
        ConsentKind.FoldNewRows => NewRows,
        ConsentKind.FoldLostRows => LostCells,
        ConsentKind.FoldOverCells => OverCells,
        ConsentKind.FoldTwoChanges => Mergeable,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a consent a request can lack."),
    };

    /// <summary>
    /// How what this plan writes differs from what <paramref name="named"/> - a dry run's - named: each member of what it
    /// writes, merges, hands, removes or applies, with what it adds and what it drops, compared as sets; or null where it
    /// writes exactly that.
    /// </summary>
    /// <param name="named">The plan its dry run named.</param>
    public string? DifferenceFrom(AgentPlanned named)
    {
        ArgumentNullException.ThrowIfNull(named);

        List<string> said = [];

        void Compare(string member, IReadOnlyList<string> was, IReadOnlyList<string> now)
        {
            var added = now.Except(was, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var dropped = was.Except(now, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            if (added.Count + dropped.Count > 0)
            {
                said.Add($"{member} {string.Join(" and ", new[] { added.Count > 0 ? $"adds {string.Join(", ", added.Select(name => $"'{name}'"))}" : null, dropped.Count > 0 ? $"drops {string.Join(", ", dropped.Select(name => $"'{name}'"))}" : null }.OfType<string>())}");
            }
        }

        Compare("written", named.Written, Written);
        Compare("removed", named.Deleted, Deleted);
        Compare("merged", [.. named.Merged, .. named.ArmRegistry is { } was ? [was] : Array.Empty<string>()], [.. Merged, .. ArmRegistry is { } now ? [now] : Array.Empty<string>()]);
        Compare("handed", named.Handed, Handed);
        Compare("registries", named.Registries, Registries);
        Compare("rows", named.Rows, Rows);

        return said.Count == 0 ? null : string.Join("; ", said);
    }
}

/// <summary>
/// What one answer of fold-agent, delete-agent, refresh-agent or rebase-agent knows beyond its lines: the document the
/// verbs answer with under <c>--json</c> is this record serialised, member by member
/// (<see cref="OrchestrationReports.Document(Results.CommandOutcome, AgentVerbFacts)"/>), and a step of an orchestrator
/// run will read it typed - one thing, so the two cannot drift. An answer stopped before anything was weighed - its
/// arguments a usage error, its agent not found - answers with the stopped form of the document
/// (<see cref="OrchestrationReports.Stopped"/>), which says what its facts say: nothing applied, no standing, nobody
/// asked, no lines, nothing needed and no reason typed.
/// </summary>
/// <param name="ExitCode">The code the command exits with.</param>
/// <param name="Summary">The line it ends on.</param>
/// <param name="Applied">Whether <c>--apply</c> was given; false where the command stopped before anything was weighed.</param>
/// <param name="Standing">
/// What is wanted of the caller: a dry run's as its one rule weighs it (<see cref="DryRuns.Answer"/>); an
/// <c>--apply</c>'s sound where it went through or was refused at its hold, and refused where it was refused or its
/// arguments are a usage error. Null where what the command weighs could not be read, where an <c>--apply</c> failed
/// (20) or stopped part way (21), and where the command stopped before anything was weighed.
/// </param>
/// <param name="Holders">Who holds what the <c>--apply</c> takes, where the command asked; null where it asked nobody.</param>
/// <param name="Lines">The lines under the summary.</param>
/// <param name="Needs">Each consent the request lacks, by kind, and nothing else; empty for an <c>--apply</c>, which asks for none.</param>
/// <param name="Refusals">
/// Each reason the request stands refused for, by kind, and nothing else: empty unless <paramref name="Standing"/> is
/// <see cref="RequestStanding.Refused"/>, and then <see cref="RefusalKind.Conflict"/> where a path's merge conflicts,
/// <see cref="RefusalKind.Other"/> where anything else refuses it - so a request refused for conflicting merges alone
/// is the one whose reasons are exactly <see cref="RefusalKind.Conflict"/>.
/// </param>
/// <param name="Folded">
/// What a fold wrote, removed, merged, handed and applied - fold-agent's <c>--apply</c>, and delete-agent's, which folds
/// back what is left of the agent first - whether it went through or stopped part way after writing some of it (21);
/// null for every other answer, and wherever it wrote, removed, merged, handed and applied nothing at all - a fold of
/// nothing and a deletion that folds nothing among them.
/// </param>
/// <param name="Planned">
/// What the plan of the verb names, wherever it was measured, its dry run's and its <c>--apply</c>'s alike - of delete-agent,
/// the fold it makes; null where no plan was measured, and for a deletion that folds nothing.
/// </param>
public sealed record AgentVerbFacts(
    int ExitCode,
    string Summary,
    bool Applied,
    RequestStanding? Standing,
    TreeHeld? Holders,
    IReadOnlyList<string> Lines,
    IReadOnlyList<ConsentKind> Needs,
    IReadOnlyList<RefusalKind> Refusals,
    AgentFolded? Folded = null,
    AgentPlanned? Planned = null);
