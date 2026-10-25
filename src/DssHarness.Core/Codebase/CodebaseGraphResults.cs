using System.Globalization;
using DssHarness.Core.Output;

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

/// <summary>One edge a walk follows from a node: the node it reaches, which way and by what kind, and how sure the surest such edge is.</summary>
/// <param name="To">The node reached, by id.</param>
/// <param name="Side">Which way the edge was followed from the node walked from.</param>
/// <param name="Kind">The edge's kind (<see cref="CodeEdgeKinds"/>).</param>
/// <param name="Confidence">How sure the surest edge of that kind and side to it is (<see cref="CodeConfidences"/>).</param>
public sealed record GraphStep(string To, GraphSide Side, string Kind, string Confidence)
{
    /// <summary>Where the walk named this kind and side among those it follows, from 0.</summary>
    public int Order { get; init; }
}

/// <summary>
/// How near a node is to a step's own work, which breaks a tie between nodes <c>find</c> scores alike (part 08): a node of
/// its context pack or of a working set it was given, then one in a file its <c>writes</c> covers, then one in a file its
/// agent changed, then one in a path or with a name its prompt holds - each nearer than the next, and every other node
/// <see cref="Far"/>.
/// </summary>
public sealed class GraphNearness
{
    /// <summary>How near a node with nothing to do with the step's work is: after every other.</summary>
    public const int Far = 4;

    private readonly IReadOnlySet<string> _ids;
    private readonly Func<string, bool> _written;
    private readonly IReadOnlySet<string> _changed;
    private readonly IReadOnlySet<string> _paths;
    private readonly IReadOnlySet<string> _names;

    /// <summary>The nearness of a step's work.</summary>
    /// <param name="ids">The nodes of its pack and of the working sets it was given.</param>
    /// <param name="written">Whether its <c>writes</c> covers a file, by its path relative to the tree.</param>
    /// <param name="changed">The files its agent changed against its base.</param>
    /// <param name="paths">The files its prompt names.</param>
    /// <param name="names">The words its prompt holds, each a name a node may carry.</param>
    public GraphNearness(IEnumerable<string> ids, Func<string, bool> written, IEnumerable<string> changed, IEnumerable<string> paths, IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(written);
        ArgumentNullException.ThrowIfNull(changed);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(names);

        _ids = ids.ToHashSet(StringComparer.Ordinal);
        _written = written;
        _changed = changed.ToHashSet(StringComparer.Ordinal);
        _paths = paths.ToHashSet(StringComparer.Ordinal);
        _names = names.ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>How near <paramref name="card"/>'s node is, from 0, the nearest, to <see cref="Far"/>.</summary>
    /// <param name="card">A node.</param>
    public int Of(GraphCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return _ids.Contains(card.Id) ? 0
            : _written(card.Path) ? 1
            : _changed.Contains(card.Path) ? 2
            : _paths.Contains(card.Path) || _names.Contains(card.Name) ? 3
            : Far;
    }
}

/// <summary>How a node is written in the lines the <c>graph</c> tool and the context pack give.</summary>
public static class GraphLines
{
    /// <summary>
    /// The one line a node is named by in every answer and in the context pack: its handle, kind and signature (its
    /// qualified name where it has none), <c>path:start-end #hash</c>, its summary, and its callers and callees and
    /// whether its file is generated or dynamic where they say anything.
    /// </summary>
    /// <param name="card">The node.</param>
    public static string Of(GraphCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var where = card.Hash is { } hash
            ? $"{card.Path}:{card.Start.ToString(CultureInfo.InvariantCulture)}-{card.End.ToString(CultureInfo.InvariantCulture)} #{hash}"
            : $"{card.Path}:{card.Start.ToString(CultureInfo.InvariantCulture)}-{card.End.ToString(CultureInfo.InvariantCulture)}";
        var line = $"{card.Handle} {card.Kind} {(card.Signature.Length > 0 ? card.Signature : card.Qualified)} | {where}";

        if (card.Summary is { } summary)
        {
            line += $" | {summary}";
        }

        var marks = new List<string>();

        if (card.Callers > 0 || card.Callees > 0)
        {
            marks.Add($"{ReportText.Counted(card.Callers, "caller")}, {ReportText.Counted(card.Callees, "callee")}");
        }

        if (card.Generated)
        {
            marks.Add("generated");
        }

        if (card.Dynamic)
        {
            marks.Add("dynamic");
        }

        return marks.Count > 0 ? $"{line} | {string.Join(", ", marks)}" : line;
    }
}
