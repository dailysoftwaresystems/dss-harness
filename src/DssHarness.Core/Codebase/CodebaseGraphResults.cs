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
public sealed record GraphEdge(GraphCard Card, string Confidence)
{
    /// <summary>
    /// Whether the edge is recall, never a doubt (<see cref="CodeEdgeKinds.Recall"/>): neither how sure it is nor the node
    /// it reaches says how sure an answer that gives it is.
    /// </summary>
    public bool Recall { get; init; }
}

/// <summary>A node's edges, grouped by kind as the <c>node</c> operation gives them.</summary>
/// <param name="Callers">The members that call it.</param>
/// <param name="Callees">The members it calls.</param>
/// <param name="Bases">The types it is built on - inherited, implemented or mixed in.</param>
/// <param name="Derived">The types built on it.</param>
/// <param name="Implementations">The types that implement it, and the members that answer it for a type.</param>
/// <param name="Overrides">The members it overrides, and those that override it.</param>
/// <param name="References">The types it names or makes, and what names or makes it.</param>
/// <param name="Users">The members that read or write it without calling it.</param>
/// <param name="Uses">The members it reads or writes without calling them.</param>
/// <param name="Tests">The tests that exercise it.</param>
/// <param name="Includes">What it imports or names as a project, and what imports or names it.</param>
/// <param name="Parts">The other parts of its declaration: a view and the type its <c>x:Class</c> names, a component and its code-behind.</param>
public sealed record GraphEdges(
    IReadOnlyList<GraphEdge> Callers,
    IReadOnlyList<GraphEdge> Callees,
    IReadOnlyList<GraphEdge> Bases,
    IReadOnlyList<GraphEdge> Derived,
    IReadOnlyList<GraphEdge> Implementations,
    IReadOnlyList<GraphEdge> Overrides,
    IReadOnlyList<GraphEdge> References,
    IReadOnlyList<GraphEdge> Users,
    IReadOnlyList<GraphEdge> Uses,
    IReadOnlyList<GraphEdge> Tests,
    IReadOnlyList<GraphEdge> Includes,
    IReadOnlyList<GraphEdge> Parts)
{
    /// <summary>
    /// Each group with the name the <c>node</c> operation prints it under, in that order. Whether an edge of it is a doubt
    /// is the edge's own (<see cref="GraphEdge.Recall"/>, read by <see cref="CodebaseGraphQueries.Doubts(GraphEdge)"/>).
    /// </summary>
    public IReadOnlyList<(string Name, IReadOnlyList<GraphEdge> Edges)> Groups =>
    [
        ("callers", Callers),
        ("callees", Callees),
        ("bases", Bases),
        ("derived", Derived),
        ("implementations", Implementations),
        ("overrides", Overrides),
        ("references", References),
        ("users", Users),
        ("uses", Uses),
        ("tests", Tests),
        ("includes", Includes),
        ("parts", Parts),
    ];
}

/// <summary>One node a <c>related</c> walk reached, and how many hops away it is.</summary>
/// <param name="Card">The node.</param>
/// <param name="Distance">How many hops from the node asked about, from 1.</param>
public sealed record GraphReached(GraphCard Card, int Distance)
{
    /// <summary>Whether an edge it was reached by is of <c>candidates</c>, and no use: the walk is not sure it goes there.</summary>
    public bool Candidates { get; init; }

    /// <summary>
    /// Whether every edge it was reached by is recall, never a doubt (<see cref="CodeEdgeKinds.Recall"/>): neither how sure
    /// they are nor the node itself says how sure the walk's answer is.
    /// </summary>
    public bool Recall { get; init; }
}

/// <summary>
/// One place a string literal that names the data of a <c>sites</c> ask is written: the node whose code holds it
/// - the innermost declaration around it, or the file's own node for what its top holds outside its declarations - where
/// the view holds one, and the file, the line and the value whatever holds it.
/// </summary>
/// <param name="Node">The node whose code holds the literal; null where the view holds no node of it.</param>
/// <param name="Path">The file, relative to the tree with forward separators.</param>
/// <param name="Line">The line the literal's value begins on.</param>
/// <param name="Value">The literal's whole value, which names the data or holds it as a whole word.</param>
public sealed record GraphLiteralPlace(GraphCard? Node, string Path, int Line, string Value);

/// <summary>
/// How a place decides the data of a <c>sites</c> ask, as its card says it: the one list of the words, in the order a card
/// says them, each reached by its own edges (<see cref="CodebaseGraphQueries.SitesOf"/>) or by what the answer reads beside
/// them - a definer, a key, a literal, a line of text.
/// </summary>
public static class GraphSiteHows
{
    /// <summary>A declaration of the data: a node the data names.</summary>
    public const string Defines = "defines";

    /// <summary>A configuration key the data names, which is held and never defined.</summary>
    public const string HoldsTheKey = "holds the key";

    /// <summary>A member that calls a definer.</summary>
    public const string Calls = "calls";

    /// <summary>A node that reads or writes a definer without calling it.</summary>
    public const string Uses = "uses";

    /// <summary>A node that makes a definer: a call of a type, an element of a component.</summary>
    public const string Instantiates = "instantiates";

    /// <summary>A type that implements a definer, or a member that answers it for a type.</summary>
    public const string Implements = "implements";

    /// <summary>A member that overrides a definer, or one a definer overrides.</summary>
    public const string Overrides = "overrides";

    /// <summary>
    /// A node that names a definer by its kind without making it: a type mention - an annotation, a parameter's or a field's
    /// type - and a test of a value's kind, as <c>isinstance</c>, <c>instanceof</c> and a <c>catch</c> or an <c>except</c> make one.
    /// </summary>
    public const string References = "references";

    /// <summary>A type built on a definer: one inheriting it, or mixing it in.</summary>
    public const string Inherits = "inherits";

    /// <summary>A node whose code holds a string literal that is the data or holds it.</summary>
    public const string HoldsTheLiteral = "holds the literal";

    /// <summary>A node with a line of text that holds the data, found by the answer's text search.</summary>
    public const string Text = "a line holds it";

    /// <summary>Every way a place decides, in the order a card says them.</summary>
    public static IReadOnlyList<string> All { get; } = [Defines, HoldsTheKey, Calls, Uses, Instantiates, References, Inherits, Implements, Overrides, HoldsTheLiteral, Text];
}

/// <summary>What one way of deciding reaches off a <c>sites</c> ask's definers: the nodes, each with the surest confidence of the edges that reach it.</summary>
/// <param name="How">The way they decide: one of <see cref="GraphSiteHows"/>.</param>
/// <param name="Edges">The nodes it reaches, each once.</param>
public sealed record GraphSiteGroup(string How, IReadOnlyList<GraphEdge> Edges);

/// <summary>Why a <c>sites</c> answer searches a file by text: what the graph does not answer of it (<see cref="CodebaseGraphQueries.TextScopeOf"/>).</summary>
public enum GraphTextReason
{
    /// <summary>The view does not hold the file: it was written after the view's last sync, or the graph's settings keep it out.</summary>
    Unheld,

    /// <summary>The view holds the file in a state no implementation of a language read it in: listed alone, unreadable, ambiguous, and the rest.</summary>
    Unread,

    /// <summary>
    /// The view holds the file as an implementation of a language read it, but no longer the extract it read: one that does
    /// not read, or no longer declares what the link names (<see cref="CodebaseGraphQueries.Unheld"/>) - its declarations in
    /// no answer.
    /// </summary>
    Unextracted,

    /// <summary>The graph is not sure of the file (<see cref="CodebaseGraphQueries.UnsureOfFile"/>).</summary>
    Unsure,

    /// <summary>An implementation outlines the file: its values and its text are held by no literal of the graph.</summary>
    Outlined,

    /// <summary>A node of the file makes a reference named the data that the linker bound to nothing of that name (<see cref="CodebaseGraphQueries.UnboundNaming"/>).</summary>
    Unbound,

    /// <summary>The data is a quoted value, and the graph holds a value only where code writes it as a string literal: every other file is searched for it.</summary>
    Value,
}

/// <summary>What a <c>sites</c> answer searches one file by text for (<see cref="CodebaseGraphQueries.TextScopeOf"/>).</summary>
/// <param name="Reason">Why it is searched.</param>
/// <param name="State">The state the view holds it in, where it is searched for that (<see cref="GraphTextReason.Unread"/>); null otherwise.</param>
public sealed record GraphTextScope(GraphTextReason Reason, string? State = null);

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

    /// <summary>The rules that style the node: the classes and ids it is told by, as a style sheet declares them - its uses of a rule, and of nothing else.</summary>
    public const string StyledBy = "styledBy";

    /// <summary>The elements and components a rule reaches: what is styled by it - the uses of the node where it is a rule, and nothing of any other node.</summary>
    public const string Styles = "styles";

    /// <summary>The types the node is built on: those it inherits, implements or mixes in.</summary>
    public const string Bases = "bases";

    /// <summary>The members of a base type the node overrides.</summary>
    public const string Overrides = "overrides";

    /// <summary>The members that override the node.</summary>
    public const string OverriddenBy = "overriddenBy";

    /// <summary>The interface members that dispatch to the node: those it answers for its type.</summary>
    public const string Dispatchers = "dispatchers";

    /// <summary>What reads or writes the node, or names it, without calling it.</summary>
    public const string Users = "users";

    /// <summary>What the node reads or writes, or names, without calling it.</summary>
    public const string Uses = "uses";

    /// <summary>
    /// The other parts of the node's declaration - a view and the type its <c>x:Class</c> names, a component and its
    /// code-behind - which every other relation walks as the node itself.
    /// </summary>
    public const string Parts = "parts";

    /// <summary>What the node, a test, exercises: each member it calls directly.</summary>
    public const string Tested = "tested";

    /// <summary>Every relation a <c>related</c> walk may follow, with what it reaches from a node, as the tool and its help say it: the one list of both.</summary>
    public static IReadOnlyList<(string Name, string Meaning)> Described { get; } =
    [
        (Callers, "what calls it"),
        (Callees, "what it calls"),
        (Derived, "the types built on it"),
        (Implementations, "what implements it or answers it for a type"),
        (References, "what names or makes it"),
        (Tests, "the tests that exercise it"),
        (IncludedBy, "what imports it"),
        (StyledBy, "the rules that style it"),
        (Styles, "what a rule styles"),
        (Bases, "the types it is built on"),
        (Overrides, "what it overrides"),
        (OverriddenBy, "what overrides it"),
        (Dispatchers, "the interface members it answers"),
        (Users, "what reads or writes it"),
        (Uses, "what it reads or writes"),
        (Parts, "the other parts of its declaration"),
        (Tested, "what a test exercises"),
    ];

    /// <summary>Every relation a <c>related</c> walk may follow.</summary>
    public static IReadOnlyList<string> All { get; } = [.. Described.Select(described => described.Name)];

    /// <summary>
    /// The relations a change of a node reaches what it reaches by, in the order its walk follows them
    /// (<see cref="CodebaseContextPack.Impact"/>): its tests, its callers, what it overrides and what overrides it, its
    /// implementations, the interface members that dispatch to it and the types derived from it - each from every node
    /// reached, to the end of the walk.
    /// </summary>
    public static IReadOnlyList<string> Impact { get; } = [Tests, Callers, Overrides, OverriddenBy, Implementations, Dispatchers, Derived];

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
        StyledBy => [(GraphSide.Out, CodeEdgeKinds.Uses)],
        Styles => [(GraphSide.In, CodeEdgeKinds.Uses)],
        Bases => [(GraphSide.Out, CodeEdgeKinds.Inherits), (GraphSide.Out, CodeEdgeKinds.Implements), (GraphSide.Out, CodeEdgeKinds.MixesIn)],
        Overrides => [(GraphSide.Out, CodeEdgeKinds.Overrides)],
        OverriddenBy => [(GraphSide.In, CodeEdgeKinds.Overrides)],
        Dispatchers => [(GraphSide.In, CodeEdgeKinds.Dispatches)],
        Users => [(GraphSide.In, CodeEdgeKinds.Uses)],
        Uses => [(GraphSide.Out, CodeEdgeKinds.Uses)],
        Parts => [(GraphSide.Out, CodeEdgeKinds.PartOf), (GraphSide.In, CodeEdgeKinds.PartOf)],
        Tested => [(GraphSide.Out, CodeEdgeKinds.Tests)],
        _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Not a relation a related walk follows."),
    };

    /// <summary>
    /// Whether a walk of <paramref name="relation"/> takes every other part of a node's declaration as the node itself, as
    /// every relation does but <see cref="Parts"/>, which walks to those parts.
    /// </summary>
    /// <param name="relation">One of <see cref="All"/>.</param>
    public static bool WalksPartsAsOne(string relation) => relation != Parts;

    /// <summary>
    /// The kind within a kind (<see cref="CodeSubKinds"/>) the node an edge <paramref name="relation"/> follows enters
    /// must be, whichever way the edge is followed: a rule for <see cref="StyledBy"/> and <see cref="Styles"/>, whose
    /// uses are those of a style rule alone - a selector the one reference that reaches one - and not a member's reads, a
    /// binding of markup or the keys' join; <see langword="null"/> where any node is.
    /// </summary>
    /// <param name="relation">One of <see cref="All"/>.</param>
    public static string? Into(string relation) => relation is StyledBy or Styles ? CodeSubKinds.Rule : null;
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
/// One node a best-first walk settled (<see cref="CodebaseContextPack.Impact"/>, and the context pack's walk of its task):
/// its best score, the source its best path began at, how many edges that path has, and the edge it ends with - so the
/// whole path is read back from the nodes settled before it.
/// </summary>
/// <param name="Id">The node, by id.</param>
/// <param name="Score">Its best score: its source's weight, carried on along the path.</param>
/// <param name="Order">The order of the source its best path began at, from 0: what breaks a tie of scores first.</param>
/// <param name="Hop">How many edges its best path has; 0 for a source.</param>
public sealed record GraphWalked(string Id, double Score, int Order, int Hop)
{
    /// <summary>Why a source is one, as the line that names it says; null for a node the walk reached.</summary>
    public string? Why { get; init; }

    /// <summary>The edge its best path reached it by, followed from <see cref="From"/>; null for a source.</summary>
    public GraphStep? By { get; init; }

    /// <summary>The node <see cref="By"/> was followed from, by id - settled before it; null for a source.</summary>
    public string? From { get; init; }
}

/// <summary>
/// One declaration at the top of a file (<see cref="CodebaseGraphQueries.TopDeclarations"/>): a type or a member no type or
/// member of the file holds, and what it and every declaration inside it exercise as tests.
/// </summary>
/// <param name="Card">The declaration, as its file declares it.</param>
/// <param name="Tests">What it and the declarations inside it test, by the <c>tests</c> edges the link gave, each once.</param>
public sealed record GraphTopDeclaration(GraphCard Card, IReadOnlyList<GraphCard> Tests);

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

/// <summary>How a node is written in the lines the <c>graph</c> tool and the context pack give, and read back.</summary>
public static partial class GraphLines
{
    /// <summary>
    /// The one line a node is named by in every answer and in the context pack: its handle, kind and signature (its
    /// qualified name where it has none), <c>path:start-end #hash</c>, its summary, and its callers and callees and
    /// whether its file is generated or dynamic where they say anything - each text of the node's own and its path
    /// printable (<see cref="ReportText.Printable"/>), so the line is one line whatever a file's name holds.
    /// </summary>
    /// <param name="card">The node.</param>
    public static string Of(GraphCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        var path = ReportText.Printable(card.Path);
        var where = card.Hash is { } hash
            ? $"{path}:{card.Start.ToString(CultureInfo.InvariantCulture)}-{card.End.ToString(CultureInfo.InvariantCulture)} #{hash}"
            : $"{path}:{card.Start.ToString(CultureInfo.InvariantCulture)}-{card.End.ToString(CultureInfo.InvariantCulture)}";
        var line = $"{card.Handle} {card.Kind} {ReportText.Printable(card.Signature.Length > 0 ? card.Signature : card.Qualified)} | {where}";

        if (card.Summary is { } summary)
        {
            line += $" | {ReportText.Printable(summary)}";
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

    /// <summary>The separator between the parts of a node's line (<see cref="Of"/>).</summary>
    private const string Part = " | ";

    /// <summary>
    /// Where a node line - one <see cref="Of"/> writes, with or without what a pack says after it - says its node stands:
    /// its handle, the line's first word, and the first part after its first that reads as a place, <c>path:start-end</c>
    /// with <c>#hash</c> where it has one, its path read back from the printable form <see cref="Of"/> wrote it in
    /// (<see cref="ReportText.Unprinted"/>). A signature that holds the separator itself (a default of <c>A | B</c>) is
    /// passed over to the place after it, and a place-like text a summary says is never reached, for the place comes
    /// first. Null where the line is no node's: a pack's first or last line, a declaration it says was taken out, a count.
    /// </summary>
    /// <param name="line">The line, whole.</param>
    public static GraphPlace? Placed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var parts = line.Split(Part);

        if (parts.Length < 2 || parts[0].Split(' ') is not [{ Length: > 0 } handle, ..])
        {
            return null;
        }

        foreach (var part in parts.Skip(1))
        {
            if (PlaceForm().Match(part) is { Success: true } place)
            {
                return new GraphPlace(
                    handle,
                    ReportText.Unprinted(place.Groups["path"].Value),
                    int.Parse(place.Groups["start"].Value, CultureInfo.InvariantCulture),
                    int.Parse(place.Groups["end"].Value, CultureInfo.InvariantCulture),
                    place.Groups["hash"].Success ? place.Groups["hash"].Value : null);
            }
        }

        return null;
    }

    /// <summary>A place as <see cref="Of"/> writes one: <c>path:start-end</c>, then <c>#hash</c> where the node has one - matched by the one linear engine.</summary>
    private static System.Text.RegularExpressions.Regex PlaceForm() => Place.Value;

    private static readonly Lazy<System.Text.RegularExpressions.Regex> Place = new(() => Text.LinearRegex.Compile(@"^(?<path>[^ ].*):(?<start>\d+)-(?<end>\d+)(?: #(?<hash>[0-9a-fA-F]+))?$"));
}

/// <summary>Where a node line says its node stands (<see cref="GraphLines.Placed"/>).</summary>
/// <param name="Handle">The node's handle, as every line of the graph names it.</param>
/// <param name="Path">The file it is declared in, relative to the tree with forward separators.</param>
/// <param name="Start">The line its own lines begin at.</param>
/// <param name="End">The line its own lines end at.</param>
/// <param name="Hash">The hash of what stands there; null where the line names none.</param>
public sealed record GraphPlace(string Handle, string Path, int Start, int End, string? Hash);
