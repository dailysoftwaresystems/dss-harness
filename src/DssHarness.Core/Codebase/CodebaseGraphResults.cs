namespace DssHarness.Core.Codebase;

/// <summary>One node as an answer names it: its handle, kind, where it stands and how sure the view is of it.</summary>
/// <param name="Id">Its id, the view's.</param>
/// <param name="Handle">Its handle in the view (<see cref="CodeGraphIds.Handles"/>).</param>
/// <param name="Kind">Its kind (<see cref="CodeNodeKinds"/>).</param>
/// <param name="SubKind">The kind within its kind the implementation named, where it named one.</param>
/// <param name="Signature">Its one-line signature; empty for a file node.</param>
/// <param name="Name">Its simple name.</param>
/// <param name="Qualified">Its qualified name.</param>
/// <param name="Path">The file it is declared in - the one with a body where it is spread over several.</param>
/// <param name="Start">Its first line, from 1.</param>
/// <param name="End">Its last line, inclusive.</param>
/// <param name="BodyStart">The line its body begins on, after its signature, where it has a body; null otherwise.</param>
/// <param name="Hash">The first eight characters of its digest, where it has one; null for a file node.</param>
/// <param name="Summary">The first sentence of its documentation, where the code says one.</param>
/// <param name="Generated">Whether its file is generated code.</param>
/// <param name="Dynamic">Whether its file holds a place its implementation could not bind.</param>
/// <param name="Callers">How many distinct members call it.</param>
/// <param name="Callees">How many distinct members it calls.</param>
public sealed record GraphCard(
    string Id,
    string Handle,
    string Kind,
    string? SubKind,
    string Signature,
    string Name,
    string Qualified,
    string Path,
    int Start,
    int End,
    int? BodyStart,
    string? Hash,
    string? Summary,
    bool Generated,
    bool Dynamic,
    int Callers,
    int Callees);

/// <summary>What resolving a symbol found: the one node it names, the candidates where a name is not unique, or none.</summary>
public abstract record GraphResolve
{
    private GraphResolve()
    {
    }

    /// <summary>The symbol named one node.</summary>
    /// <param name="Card">The node.</param>
    public sealed record Found(GraphCard Card) : GraphResolve;

    /// <summary>The symbol named a name two or more nodes carry: each is given, none picked.</summary>
    /// <param name="Cards">The candidates, by id.</param>
    public sealed record Candidates(IReadOnlyList<GraphCard> Cards) : GraphResolve;

    /// <summary>The symbol named nothing the view holds.</summary>
    public sealed record NotFound : GraphResolve;

    /// <summary>The symbol named nothing the view holds.</summary>
    public static readonly GraphResolve None = new NotFound();

    /// <summary>The symbol named one node, <paramref name="card"/>.</summary>
    /// <param name="card">The node.</param>
    public static GraphResolve One(GraphCard card) => new Found(card);

    /// <summary>The symbol named <paramref name="cards"/>, a name two or more carry.</summary>
    /// <param name="cards">The candidates.</param>
    public static GraphResolve Many(IReadOnlyList<GraphCard> cards) => new Candidates(cards);
}

/// <summary>The nodes a <c>find</c> matched: the page asked for, and how many matched in all.</summary>
/// <param name="Cards">The page, best first.</param>
/// <param name="Total">How many matched.</param>
public sealed record GraphMatches(IReadOnlyList<GraphCard> Cards, int Total);

/// <summary>One edge of a node, as a group of the <c>node</c> operation gives it: the node at its other end, and how sure the edge is.</summary>
/// <param name="Card">The node at the edge's other end.</param>
/// <param name="Confidence">How sure the surest edge to it is (<see cref="CodeConfidences"/>).</param>
public sealed record GraphEdge(GraphCard Card, string Confidence);

/// <summary>A node's edges, grouped by kind as the <c>node</c> operation gives them.</summary>
/// <param name="Callers">The members that call it.</param>
/// <param name="Callees">The members it calls.</param>
/// <param name="Bases">The types it is built on - inherited, implemented or mixed in.</param>
/// <param name="Derived">The types built on it.</param>
/// <param name="Implementations">The types that implement it, and the members that answer it for a type.</param>
/// <param name="Overrides">The members it overrides, and those that override it.</param>
/// <param name="References">The types it names or makes, and what names or makes it.</param>
/// <param name="Tests">The tests that exercise it.</param>
/// <param name="Includes">What it imports or names as a project, and what imports or names it.</param>
public sealed record GraphEdges(
    IReadOnlyList<GraphEdge> Callers,
    IReadOnlyList<GraphEdge> Callees,
    IReadOnlyList<GraphEdge> Bases,
    IReadOnlyList<GraphEdge> Derived,
    IReadOnlyList<GraphEdge> Implementations,
    IReadOnlyList<GraphEdge> Overrides,
    IReadOnlyList<GraphEdge> References,
    IReadOnlyList<GraphEdge> Tests,
    IReadOnlyList<GraphEdge> Includes)
{
    /// <summary>Each group with the name the <c>node</c> operation prints it under, in that order.</summary>
    public IReadOnlyList<(string Name, IReadOnlyList<GraphEdge> Edges)> Groups =>
    [
        ("callers", Callers),
        ("callees", Callees),
        ("bases", Bases),
        ("derived", Derived),
        ("implementations", Implementations),
        ("overrides", Overrides),
        ("references", References),
        ("tests", Tests),
        ("includes", Includes),
    ];
}

/// <summary>One node a <c>related</c> walk reached, and how many hops away it is.</summary>
/// <param name="Card">The node.</param>
/// <param name="Distance">How many hops from the node asked about, from 1.</param>
public sealed record GraphReached(GraphCard Card, int Distance)
{
    /// <summary>
    /// How many edges of the kind walked the node has, where it has more than the view's 99th percentile (and at least
    /// <see cref="CodebaseGraphQueries.CrowdFloor"/>): it is given and not gone through. Null where it was gone through.
    /// </summary>
    public int? Crowded { get; init; }

    /// <summary>Whether an edge it was reached by is of <c>candidates</c>: the walk is not sure it goes there.</summary>
    public bool Candidates { get; init; }
}

/// <summary>The repository at a glance: its projects, its top directories with their files, its namespaces with what they declare.</summary>
/// <param name="Projects">Each project node.</param>
/// <param name="Directories">Each top directory - '.' for the files at the root - with the files the view holds in it.</param>
/// <param name="Namespaces">Each namespace node with the declarations it holds, and the files that declare a part of it.</param>
public sealed record GraphOverview(
    IReadOnlyList<GraphCard> Projects,
    IReadOnlyList<(string Directory, IReadOnlyList<string> Files)> Directories,
    IReadOnlyList<(GraphCard Card, IReadOnlyList<GraphCard> Declarations, IReadOnlyList<string> Files)> Namespaces);

/// <summary>Which way an edge is followed from a node.</summary>
public enum GraphSide
{
    /// <summary>Out of the node, to the edge's target.</summary>
    Out,

    /// <summary>Into the node, from the edge's source.</summary>
    In,
}

/// <summary>The relations a <c>related</c> walk may follow, each the edge kind and direction it means.</summary>
public static class GraphRelations
{
    /// <summary>The members that call the node.</summary>
    public const string Callers = "callers";

    /// <summary>The members the node calls.</summary>
    public const string Callees = "callees";

    /// <summary>The types built on the node: inheriting it, or mixing it in.</summary>
    public const string Derived = "derived";

    /// <summary>The types that implement the node, and the members that answer it for a type.</summary>
    public const string Implementations = "implementations";

    /// <summary>What names the node: a reference to it, or a call that makes it.</summary>
    public const string References = "references";

    /// <summary>The tests that exercise the node.</summary>
    public const string Tests = "tests";

    /// <summary>What imports the node: a file that imports it, or a project that names it.</summary>
    public const string IncludedBy = "includedBy";

    /// <summary>Every relation a <c>related</c> walk may follow.</summary>
    public static IReadOnlyList<string> All { get; } = [Callers, Callees, Derived, Implementations, References, Tests, IncludedBy];

    /// <summary>The edges <paramref name="relation"/> follows: each kind, and which way from the node.</summary>
    /// <param name="relation">One of <see cref="All"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="relation"/> is none of <see cref="All"/>.</exception>
    public static IReadOnlyList<(GraphSide Side, string Kind)> Edges(string relation) => relation switch
    {
        Callers => [(GraphSide.In, CodeEdgeKinds.Calls)],
        Callees => [(GraphSide.Out, CodeEdgeKinds.Calls)],
        Derived => [(GraphSide.In, CodeEdgeKinds.Inherits), (GraphSide.In, CodeEdgeKinds.MixesIn)],
        Implementations => [(GraphSide.In, CodeEdgeKinds.Implements), (GraphSide.Out, CodeEdgeKinds.Dispatches)],
        References => [(GraphSide.In, CodeEdgeKinds.References), (GraphSide.In, CodeEdgeKinds.Instantiates)],
        Tests => [(GraphSide.In, CodeEdgeKinds.Tests)],
        IncludedBy => [(GraphSide.In, CodeEdgeKinds.Imports), (GraphSide.In, CodeEdgeKinds.ProjectReference)],
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Not a relation a related walk follows."),
    };
}
