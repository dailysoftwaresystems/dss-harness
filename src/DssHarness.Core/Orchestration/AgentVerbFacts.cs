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
/// <param name="Registries">The registries its rows changed, by name; empty where its rows were not applied.</param>
/// <param name="Rows">The ids of the rows it applied: the fold's output; empty where its rows were not applied.</param>
/// <param name="OverCells">The cells both changed that its <c>--accept-over</c> took, as cells are named; empty where its rows were not applied.</param>
public sealed record AgentFolded(
    IReadOnlyList<string> Written,
    IReadOnlyList<string> Deleted,
    IReadOnlyList<string> Merged,
    string? ArmRegistry,
    IReadOnlyList<HandedPath> Handed,
    IReadOnlyList<string> Registries,
    IReadOnlyList<string> Rows,
    IReadOnlyList<string> OverCells);

/// <summary>
/// What a refresh's plan, or a move of an agent's base, names for each path it weighed: what a dry run would do and an
/// <c>--apply</c> set out to, as measured - which nothing else reports as one answer.
/// </summary>
/// <param name="Taken">The paths a move of its base takes into its worktree as the new base holds them, in order; empty for a refresh.</param>
/// <param name="Kept">The paths a move of its base keeps as they are, shared with the main tree, in order; empty for a refresh.</param>
/// <param name="Handed">The paths a refresh hands it from the main tree - each file copied and each deletion made - in order; empty for a move of its base.</param>
/// <param name="Merged">The paths both changed whose three-way merge is taken, in order, as text: never the arm registry, which is <paramref name="ArmRegistry"/>.</param>
/// <param name="ArmRegistry">The arm registry, as git names it, where it is merged by arm; <see langword="null"/> where it is not.</param>
public sealed record AgentPlanned(
    IReadOnlyList<string> Taken,
    IReadOnlyList<string> Kept,
    IReadOnlyList<string> Handed,
    IReadOnlyList<string> Merged,
    string? ArmRegistry);

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
/// What the plan of refresh-agent or rebase-agent names, wherever it was measured, its dry run's and its
/// <c>--apply</c>'s alike; null for fold-agent and delete-agent, and where no plan was measured.
/// </param>
/// <param name="Ended">
/// Each session a killed process left running of the orchestrator's contained children that finding the agent ended
/// (<see cref="ChildSessions.SweepAsync"/>), in the document as <c>ended</c> where there was one; null or empty where
/// there was none, and where the command stopped before its orchestrator was found.
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
    AgentPlanned? Planned = null,
    IReadOnlyList<EndedChildSession>? Ended = null);
