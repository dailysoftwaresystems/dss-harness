using System.Collections.Concurrent;
using System.Globalization;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The lookups a <see cref="CodebaseGraphRead"/> is asked by, indexed once from it: each node by its id, handle, qualified
/// and simple name and the line it is on, each file's nodes, each node's edges in and out by kind, the words its names split
/// into weighed by how rare they are, and the 99th percentile of each kind's out-degree. One owner of what the <c>graph</c>
/// tool and the <c>query-codebase-graph</c> verb answer from - the same view and query answer alike - over the neutral model,
/// no language of its own.
/// </summary>
public sealed class CodebaseGraphQueries
{
    /// <summary>What a card counts as its callers, and as its callees: the nodes the view holds one call reaches, each once.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> CallersOnly = [(GraphSide.In, CodeEdgeKinds.Calls)];

    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> CalleesOnly = [(GraphSide.Out, CodeEdgeKinds.Calls)];

    private readonly CodebaseGraphRead _read;
    private readonly IReadOnlyDictionary<string, CodeGraphSymbolEntry> _symbols;
    private readonly IReadOnlyDictionary<string, string> _handles;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<NodeAt>> _nodes;
    private readonly ILookup<string, string> _byQualified;
    private readonly ILookup<string, string> _bySimple;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _inFile;
    private readonly ILookup<string, CodeEdge> _out;
    private readonly ILookup<string, CodeEdge> _in;
    private readonly IReadOnlyDictionary<string, double> _idf;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _words;
    private readonly IReadOnlyDictionary<(string Kind, GraphSide Side), int> _percentile;
    private readonly IReadOnlyDictionary<string, FileExtract?> _extracts;
    private readonly ConcurrentDictionary<string, GraphCard> _cards = new(StringComparer.Ordinal);

    /// <summary>Indexes <paramref name="read"/>: every lookup is built once, so the first answer pays for them and the rest read them.</summary>
    /// <param name="read">The view to answer from, as a sync or a check or the read-only open left it.</param>
    public CodebaseGraphQueries(CodebaseGraphRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _read = read;
        var symbols = read.Symbols;

        var nodes = new Dictionary<string, IReadOnlyList<NodeAt>>(StringComparer.Ordinal);
        var inFile = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var byQualified = new List<(string Name, string Id)>();
        var bySimple = new List<(string Name, string Id)>();
        var words = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        // Each file's extract is read once: a view read from disk reads it from its record at each ask. A file whose entry
        // names an extract that no longer reads - every file read is a node of the link - has its declarations in no
        // answer, and is said.
        var extracts = new Dictionary<string, FileExtract?>(StringComparer.Ordinal);
        var unheld = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var path in symbols.Nodes.SelectMany(symbol => symbol.Paths))
        {
            if (!extracts.ContainsKey(path))
            {
                extracts[path] = read.Extract(path);

                if (extracts[path] is null && read.Files.TryGetValue(path, out var entry) && entry.Extract is not null)
                {
                    unheld.Add(path);
                }
            }
        }

        _extracts = extracts;

        // Each file's nodes by their ids, made once a file rather than looked for once a node.
        var declared = extracts.ToDictionary(file => file.Key, file => (file.Value?.Nodes ?? []).ToLookup(node => node.Id, StringComparer.Ordinal), StringComparer.Ordinal);
        var kept = new List<CodeGraphSymbolEntry>();

        foreach (var symbol in symbols.Nodes)
        {
            List<NodeAt> at = [.. symbol.Paths.SelectMany(path => declared[path][symbol.Id].Select(node => new NodeAt(path, node)))];

            // A declaration whose extract the graph no longer holds has no lines to name: it is left out, and its file said.
            if (at.Count == 0 && !string.Equals(symbol.Kind, CodeNodeKinds.File, StringComparison.Ordinal))
            {
                unheld.UnionWith(symbol.Paths);
                continue;
            }

            kept.Add(symbol);

            foreach (var path in symbol.Paths)
            {
                (inFile.TryGetValue(path, out var ids) ? ids : inFile[path] = []).Add(symbol.Id);
            }

            nodes[symbol.Id] = at;

            var representative = Representative(symbol, at).Node;
            byQualified.Add((representative.Qualified, symbol.Id));
            bySimple.Add((representative.Name, symbol.Id));
            words[symbol.Id] = [.. Split(representative.Qualified)];
        }

        Unheld = [.. unheld];
        _symbols = kept.ToDictionary(node => node.Id, StringComparer.Ordinal);
        _handles = kept.ToDictionary(node => node.Handle, node => node.Id, StringComparer.Ordinal);
        _nodes = nodes;
        _inFile = inFile.ToDictionary(file => file.Key, file => (IReadOnlyList<string>)file.Value, StringComparer.Ordinal);
        _byQualified = byQualified.ToLookup(name => name.Name, name => name.Id, StringComparer.Ordinal);
        _bySimple = bySimple.ToLookup(name => name.Name, name => name.Id, StringComparer.Ordinal);
        _words = words;

        // A word's weight is how rare it is among the view's names: a common word (Async, Service) parts few nodes, a rare one many.
        var documents = Math.Max(1, words.Count);
        var frequency = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var word in words.Values.SelectMany(list => list.Distinct(StringComparer.Ordinal)))
        {
            frequency[word] = frequency.GetValueOrDefault(word) + 1;
        }

        _idf = frequency.ToDictionary(word => word.Key, word => Math.Log((documents + 1.0) / (word.Value + 1.0)) + 1.0, StringComparer.Ordinal);

        _out = symbols.Edges.ToLookup(edge => edge.From, StringComparer.Ordinal);
        _in = symbols.Edges.ToLookup(edge => edge.To, StringComparer.Ordinal);
        _percentile = Percentiles(symbols.Edges, kept.Count);
    }

    /// <summary>The view answered from: <c>base</c>, or an agent's name.</summary>
    public string View => _read.View;

    /// <summary>The commit the tree's HEAD named when it was listed, where it named one.</summary>
    public string? Head => _read.Head;

    /// <summary>The tree, absolute.</summary>
    public string Tree => _read.Tree;

    /// <summary>How many files the view holds.</summary>
    public int FileCount => _read.Files.Count;

    /// <summary>The files, by path, whose extract the view no longer holds - one that does not read, or no longer declares what the link names - their declarations in no answer.</summary>
    public IReadOnlyList<string> Unheld { get; }

    /// <summary>
    /// The path the view holds <paramref name="path"/> at - as given, or with its backslashes read as separators, as a path
    /// typed on Windows is - or null where it holds neither.
    /// </summary>
    /// <param name="path">A path an agent or a person wrote, relative to the tree.</param>
    public string? HeldPath(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return _read.Files.ContainsKey(path) ? path
            : path.Replace('\\', '/') is var slashed && _read.Files.ContainsKey(slashed) ? slashed
            : null;
    }

    /// <summary>
    /// Resolves <paramref name="symbol"/> - a handle, an id, a <c>path:line</c>, a qualified name (or one written with some
    /// of its scopes, its last parts) or a simple name - to the node it names, its candidates, or none.
    /// </summary>
    /// <param name="symbol">What an agent wrote: a handle, an id, a <c>path:line</c>, a qualified name or a simple name.</param>
    public GraphResolve Resolve(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        if (_handles.TryGetValue(symbol, out var byHandle))
        {
            return GraphResolve.One(Card(byHandle));
        }

        if (_symbols.ContainsKey(symbol))
        {
            return GraphResolve.One(Card(symbol));
        }

        // A file named with backslashes is the file the view holds, as a path typed on Windows is.
        if (HeldPath(symbol) is { } held && held != symbol)
        {
            return Resolve(held);
        }

        if (AtLine(symbol) is { } atLine)
        {
            return GraphResolve.One(atLine);
        }

        var qualified = _byQualified[symbol].ToList();
        var ids = qualified.Count > 0 ? qualified : _bySimple[symbol].ToList();

        if (ids.Count == 0 && Dotted(symbol) is var dotted && dotted != symbol)
        {
            ids = [.. _byQualified[dotted]];
        }

        // A name written with some of its scopes (Order.Total) names each node whose qualified name ends with it, at a dot.
        if (ids.Count == 0 && Dotted(symbol) is var scoped && scoped.LastIndexOf('.') is > 0 and var dot && dot < scoped.Length - 1)
        {
            ids = [.. _bySimple[scoped[(dot + 1)..]].Where(id => Card(id).Qualified.EndsWith("." + scoped, StringComparison.Ordinal))];
        }

        return ids.Count switch
        {
            0 => GraphResolve.None,
            1 => GraphResolve.One(Card(ids[0])),
            _ => GraphResolve.Many([.. ids.Select(Card).OrderBy(card => card.Id, StringComparer.Ordinal)]),
        };
    }

    /// <summary>
    /// The nodes that match <paramref name="query"/>, best first, bounded by <paramref name="limit"/> after
    /// <paramref name="offset"/>: generated code after every other, then by score, then by how near <paramref name="near"/>
    /// says each is to the step's own work, then by id.
    /// </summary>
    /// <param name="query">The words to match, split and folded as a name is.</param>
    /// <param name="kind">The one kind to keep, where it is given.</param>
    /// <param name="path">The one file to keep the nodes of, where it is given.</param>
    /// <param name="limit">The most to give.</param>
    /// <param name="offset">How many of the best to pass over first.</param>
    /// <param name="near">How near each node is to the step's own work, which breaks a tie; none where it is not given.</param>
    public GraphMatches Find(string query, string? kind, string? path, int limit, int offset, GraphNearness? near = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);

        var terms = new Terms(Split(query), _idf);
        var ranked = _symbols.Values
            .Where(symbol => kind is null || string.Equals(symbol.Kind, kind, StringComparison.Ordinal))
            .Select(symbol => Card(symbol.Id))
            .Where(card => path is null || string.Equals(card.Path, path, StringComparison.Ordinal))
            .Select(card => (Card: card, Score: Score(card, query, terms)))
            .Where(scored => scored.Score > 0)
            .OrderByDescending(scored => scored.Card.Generated ? 0 : 1)
            .ThenByDescending(scored => scored.Score)
            .ThenBy(scored => near?.Of(scored.Card) ?? GraphNearness.Far)
            .ThenBy(scored => scored.Card.Id, StringComparer.Ordinal)
            .Select(scored => scored.Card)
            .ToList();

        return new GraphMatches([.. ranked.Skip(offset).Take(limit)], ranked.Count);
    }

    /// <summary>The files the view holds, by path.</summary>
    public IEnumerable<string> Paths => _read.Files.Keys;

    /// <summary>Whether the view holds the node <paramref name="id"/> names - one whose extract it still holds, or a file.</summary>
    /// <param name="id">A node's id.</param>
    public bool Holds(string id) => _symbols.ContainsKey(id);

    /// <summary>The card of the node <paramref name="id"/> names; it is a node the view holds.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public GraphCard Card(string id) => _cards.GetOrAdd(id, key => Card(key, null));

    /// <summary>
    /// The card of the node <paramref name="id"/> names as the file at <paramref name="path"/> declares it - its part there,
    /// for a namespace or a partial type spread over files - or null where the view holds no such node in that file.
    /// </summary>
    /// <param name="id">A node's id.</param>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public GraphCard? Part(string id, string path)
        => _symbols.TryGetValue(id, out var symbol) && symbol.Paths.Contains(path, StringComparer.Ordinal)
            && (_nodes[id].Count == 0 || _nodes[id].Any(at => at.Path == path))
            ? Card(id, path)
            : null;

    /// <summary>The cards of the node <paramref name="id"/> names, one for each file that declares a part of it, by path.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public IReadOnlyList<GraphCard> Parts(string id)
        => [.. _symbols[id].Paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(path => Part(id, path)).OfType<GraphCard>()];

    /// <summary>
    /// The card of the node <paramref name="id"/> names as the file at <paramref name="path"/> declares it - a namespace or
    /// a partial type spread over several, by its lines in that file - or as it is declared at all where that file does not.
    /// </summary>
    private GraphCard Card(string id, string? path)
    {
        var symbol = _symbols[id];
        var at = _nodes[id];
        var (where, node) = at.Where(one => one.Path == path).OrderBy(one => one.Node.Start).FirstOrDefault() ?? Representative(symbol, at);
        path = where;
        var callers = Steps(id, CallersOnly).Count;
        var callees = Steps(id, CalleesOnly).Count;

        return new GraphCard(
            id,
            symbol.Handle,
            symbol.Kind,
            node.SubKind,
            node.Signature,
            node.Name,
            node.Qualified,
            path,
            node.Start,
            node.End,
            node.BodyStart,
            node.Digest is { } digest ? digest[..8] : null,
            node.Summary,
            Generated(path),
            Dynamic(path),
            callers,
            callees);
    }

    /// <summary>The edges of the node <paramref name="id"/> names, grouped by kind as the <c>node</c> operation gives them, each a card.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public GraphEdges Edges(string id)
    {
        IReadOnlyList<GraphEdge> Group(GraphSide side, string kind)
            => [.. Steps(id, [(side, kind)]).Select(step => new GraphEdge(Card(step.To), step.Confidence))];

        return new GraphEdges(
            Group(GraphSide.In, CodeEdgeKinds.Calls),
            Group(GraphSide.Out, CodeEdgeKinds.Calls),
            [.. Group(GraphSide.Out, CodeEdgeKinds.Inherits), .. Group(GraphSide.Out, CodeEdgeKinds.Implements), .. Group(GraphSide.Out, CodeEdgeKinds.MixesIn)],
            [.. Group(GraphSide.In, CodeEdgeKinds.Inherits), .. Group(GraphSide.In, CodeEdgeKinds.MixesIn)],
            [.. Group(GraphSide.In, CodeEdgeKinds.Implements), .. Group(GraphSide.Out, CodeEdgeKinds.Dispatches)],
            [.. Group(GraphSide.Out, CodeEdgeKinds.Overrides), .. Group(GraphSide.In, CodeEdgeKinds.Overrides)],
            [.. Group(GraphSide.Out, CodeEdgeKinds.References), .. Group(GraphSide.Out, CodeEdgeKinds.Instantiates), .. Group(GraphSide.In, CodeEdgeKinds.References), .. Group(GraphSide.In, CodeEdgeKinds.Instantiates)],
            Group(GraphSide.In, CodeEdgeKinds.Tests),
            [.. Group(GraphSide.Out, CodeEdgeKinds.Imports), .. Group(GraphSide.Out, CodeEdgeKinds.ProjectReference), .. Group(GraphSide.In, CodeEdgeKinds.Imports), .. Group(GraphSide.In, CodeEdgeKinds.ProjectReference)]);
    }

    /// <summary>
    /// The nodes reached from <paramref name="id"/> over <paramref name="relation"/> to <paramref name="depth"/>, by distance.
    /// A node with more edges of the relation's kinds than the view's 99th percentile of any of them (and at least
    /// <see cref="CrowdFloor"/>) is given and not gone through.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="relation">What to follow: one of <see cref="GraphRelations.All"/>.</param>
    /// <param name="depth">How many hops, 1 to 3.</param>
    public IReadOnlyList<GraphReached> Related(string id, string relation, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(depth, 3);

        var edges = GraphRelations.Edges(relation);
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        var reached = new List<GraphReached>();
        var frontier = new List<string> { id };

        for (var hop = 1; hop <= depth && frontier.Count > 0; hop++)
        {
            var next = new List<string>();

            foreach (var from in frontier)
            {
                var unsure = ByCandidates(from, edges);

                foreach (var to in Neighbours(from, edges).OrderBy(to => to, StringComparer.Ordinal))
                {
                    if (!seen.Add(to))
                    {
                        continue;
                    }

                    var crowded = Crowd(to, edges);
                    reached.Add(new GraphReached(Card(to), hop) { Crowded = crowded, Candidates = unsure.Contains(to) });

                    if (crowded is null)
                    {
                        next.Add(to);
                    }
                }
            }

            frontier = next;
        }

        return reached;
    }

    /// <summary>
    /// How many nodes the view holds that <paramref name="edges"/> reach from <paramref name="id"/>, where that makes it a
    /// hub - at least <see cref="CrowdFloor"/>, and more than the view's 99th percentile of any of those kinds on its side -
    /// which a walk gives and does not go through; null where it goes through. The one rule of a hub, for <c>related</c> and
    /// the context pack.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="edges">The kinds walked, each with the side it is followed from.</param>
    public int? Crowd(string id, IReadOnlyList<(GraphSide Side, string Kind)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        var bound = edges.Max(edge => _percentile.GetValueOrDefault((edge.Kind, edge.Side)));
        var degree = Neighbours(id, edges).Count;
        return degree >= CrowdFloor && degree > bound ? degree : null;
    }

    /// <summary>
    /// Each node the view holds that one edge of <paramref name="edges"/> reaches from <paramref name="id"/>, once for each
    /// kind and side it is reached by, with the surest confidence of the edges that reach it so - by id, then by the order
    /// <paramref name="edges"/> names them.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="edges">The kinds to follow, each with the side it is followed from.</param>
    public IReadOnlyList<GraphStep> Steps(string id, IReadOnlyList<(GraphSide Side, string Kind)> edges)
    {
        ArgumentNullException.ThrowIfNull(edges);

        return [.. edges
            .SelectMany((edge, order) => (edge.Side == GraphSide.Out ? _out[id] : _in[id])
                .Where(found => string.Equals(found.Kind, edge.Kind, StringComparison.Ordinal))
                .Select(found => (To: edge.Side == GraphSide.Out ? found.To : found.From, edge.Side, edge.Kind, found.Confidence, Order: order)))
            .Where(step => _symbols.ContainsKey(step.To))
            .GroupBy(step => (step.To, step.Side, step.Kind, step.Order))
            .Select(group => new GraphStep(group.Key.To, group.Key.Side, group.Key.Kind, group.Select(step => step.Confidence).Aggregate(CodeConfidences.Surer)) { Order = group.Key.Order })
            .OrderBy(step => step.To, StringComparer.Ordinal)
            .ThenBy(step => step.Order)];
    }

    /// <summary>The declarations of the file at <paramref name="path"/>, each with its span and hash, bodies left out; null where the view holds no such file.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public IReadOnlyList<GraphCard>? OutlineFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (!_read.Files.ContainsKey(path))
        {
            return null;
        }

        return [.. (_inFile.TryGetValue(path, out var ids) ? ids : [])
            .Select(id => Card(id, path))
            .Where(card => card.Path == path)
            .Where(card => CodeNodeKinds.Extracted.Contains(card.Kind, StringComparer.Ordinal))
            .OrderBy(card => card.Start)
            .ThenBy(card => card.Id, StringComparer.Ordinal)];
    }

    /// <summary>The declarations inside the type <paramref name="id"/> names, each with its span and hash; <paramref name="id"/> is a node the view holds.</summary>
    /// <param name="id">A type node's id, the view's.</param>
    public IReadOnlyList<GraphCard> OutlineType(string id)
        => [.. _out[id]
            .Where(edge => string.Equals(edge.Kind, CodeEdgeKinds.Contains, StringComparison.Ordinal))
            .Select(edge => edge.To)
            .Where(_symbols.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .Select(Card)
            .OrderBy(card => card.Start)
            .ThenBy(card => card.Id, StringComparer.Ordinal)];

    /// <summary>
    /// The repository at a glance, as an <c>outline</c> asked of neither a file nor a symbol gives it: its projects, each
    /// top directory with how many files it holds, and each namespace with how many declarations it holds.
    /// </summary>
    public GraphOverview Overview()
    {
        var projects = _symbols.Keys.Select(Card).Where(card => string.Equals(card.Kind, CodeNodeKinds.Project, StringComparison.Ordinal))
            .OrderBy(card => card.Path, StringComparer.Ordinal).ThenBy(card => card.Id, StringComparer.Ordinal).ToList();
        var directories = _read.Files.Keys
            .GroupBy(path => path.IndexOf('/') is var slash and > 0 ? path[..slash] + "/" : ".", StringComparer.Ordinal)
            .Select(group => (Directory: group.Key, Files: (IReadOnlyList<string>)[.. group.Order(StringComparer.Ordinal)]))
            .OrderBy(directory => directory.Directory, StringComparer.Ordinal)
            .ToList();
        var namespaces = _symbols.Keys.Select(Card).Where(card => string.Equals(card.Kind, CodeNodeKinds.Namespace, StringComparison.Ordinal))
            .Select(card => (Card: card, Declarations: OutlineType(card.Id), Files: (IReadOnlyList<string>)[.. Parts(card.Id).Select(part => part.Path)]))
            .OrderBy(one => one.Card.Qualified, StringComparer.Ordinal).ThenBy(one => one.Card.Id, StringComparer.Ordinal)
            .ToList();

        return new GraphOverview(projects, directories, namespaces);
    }

    /// <summary>The file's lines <paramref name="card"/> spans, read fresh from <paramref name="lines"/>; the caller has checked the hash.</summary>
    /// <param name="card">The node whose span to read.</param>
    /// <param name="lines">The file's lines as they stand now.</param>
    public static IReadOnlyList<string> Body(GraphCard card, IReadOnlyList<string> lines)
    {
        ArgumentNullException.ThrowIfNull(card);
        ArgumentNullException.ThrowIfNull(lines);

        var start = Math.Clamp(card.Start, 1, Math.Max(1, lines.Count));
        var end = Math.Clamp(card.End, start, lines.Count);
        return [.. Enumerable.Range(start - 1, Math.Max(0, end - start + 1)).Select(at => lines[at])];
    }

    /// <summary>The node <paramref name="id"/> names across its files, so <c>body</c> and <c>edit</c> read each span and recompute its hash.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public IReadOnlyList<NodeAt> At(string id) => _nodes.TryGetValue(id, out var at) ? at : [];

    /// <summary>
    /// Whether a node an answer names makes the answer not sure, so it adds a text search: its file carries a
    /// <c>generated</c> or a <c>dynamic</c> mark, or it has external or unresolved callees. An edge of <c>candidates</c> an
    /// answer gives makes it not sure too, judged where the answer gives it.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    public bool Unsure(string id)
        => (_nodes.TryGetValue(id, out var at) && at.Count > 0 && (Generated(at[0].Path) || Dynamic(at[0].Path)))
            || _read.Symbols.External.GetValueOrDefault(id) > 0
            || _read.Symbols.Unresolved.GetValueOrDefault(id) > 0;

    /// <summary>Whether the file at <paramref name="path"/> is generated code: its entry says so, or holds the mark a later sync keeps it generated by.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public bool Generated(string path)
        => _read.Files.TryGetValue(path, out var entry)
            && (string.Equals(entry.State, CodeFileStates.Generated, StringComparison.Ordinal)
                || (CodeFileStates.Extracted.Contains(entry.State, StringComparer.Ordinal) && entry.Why is not null));

    /// <summary>Whether the file at <paramref name="path"/> holds a place its implementation could not bind: a text search where its nodes are named.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public bool Dynamic(string path) => (_extracts.TryGetValue(path, out var extract) ? extract : _read.Extract(path)) is { Dynamic.Count: > 0 };

    /// <summary>The nodes in the file at <paramref name="path"/>, as a text hit is turned to a card where one covers its line.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    /// <param name="line">A line of that file, from 1.</param>
    public GraphCard? AtLine(string path, int line)
        => (_inFile.TryGetValue(path, out var ids) ? ids : [])
            .Select(id => Card(id, path))
            .Where(card => card.Path == path && card.Start <= line && line <= card.End)
            .OrderByDescending(card => card.Start)
            .ThenBy(card => card.Id, StringComparer.Ordinal)
            .Cast<GraphCard?>()
            .FirstOrDefault();

    /// <summary>Whether <paramref name="symbol"/> is written as a file is - with a separator, or as a <c>path:line</c> - rather than as a name.</summary>
    /// <param name="symbol">What an agent or a person wrote.</param>
    public static bool NamesAFile(string symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);

        var colon = symbol.LastIndexOf(':');
        var backslash = symbol.LastIndexOf('\\');
        return symbol.Contains('/', StringComparison.Ordinal) || (backslash >= 0 && symbol.IndexOf('.', backslash) > backslash)
            || (colon > 0 && int.TryParse(symbol.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }

    /// <summary>At least this many edges of a kind, whatever the percentile, mark a node too crowded to go through.</summary>
    public const int CrowdFloor = 50;

    private GraphCard? AtLine(string symbol)
    {
        var colon = symbol.LastIndexOf(':');

        if (colon <= 0 || !int.TryParse(symbol.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line))
        {
            return null;
        }

        return AtLine(HeldPath(symbol[..colon]) ?? symbol[..colon], line);
    }

    /// <summary>The nodes an edge of <paramref name="edges"/>' kinds of <c>candidates</c> leads to from <paramref name="from"/> in one hop.</summary>
    private HashSet<string> ByCandidates(string from, IReadOnlyList<(GraphSide Side, string Kind)> edges)
        => [.. Steps(from, edges).Where(step => string.Equals(step.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal)).Select(step => step.To)];

    /// <summary>The nodes the view holds that <paramref name="edges"/> reach from <paramref name="id"/> in one hop, each once.</summary>
    private HashSet<string> Neighbours(string id, IReadOnlyList<(GraphSide Side, string Kind)> edges)
        => [.. Steps(id, edges).Select(step => step.To)];

    private double Score(GraphCard card, string query, Terms terms)
    {
        if (string.Equals(card.Qualified, query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000;
        }

        if (string.Equals(card.Name, query, StringComparison.OrdinalIgnoreCase))
        {
            return 500;
        }

        if (card.Qualified.StartsWith(query, StringComparison.OrdinalIgnoreCase) || card.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 250;
        }

        if (terms.Whole <= 0)
        {
            return 0;
        }

        // Each of the query's words the name holds, as often as the query says it, weighed by how rare it is - summed in one
        // order, so names that hold the same words score alike.
        var matched = _words.GetValueOrDefault(card.Id, []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Sum(word => terms.Weight.GetValueOrDefault(word));

        // A node's summary and its path count far less than its name: a word there is a twentieth of one in the name.
        matched += (Hits(card.Summary, terms) + Hits(card.Path, terms)) * 0.05;

        return matched / terms.Whole * 100;
    }

    /// <summary>How many of the query's words, each as often as it says it, <paramref name="text"/> holds.</summary>
    private static double Hits(string? text, Terms terms)
        => text is null ? 0 : Split(text).Distinct(StringComparer.Ordinal).Sum(word => terms.Count.GetValueOrDefault(word));

    private NodeAt Representative(CodeGraphSymbolEntry symbol, IReadOnlyList<NodeAt> at)
    {
        if (at.Count == 0)
        {
            // A file node, or a node whose extract the view no longer holds: its card is what the symbol alone says, a
            // file's span its lines.
            var path = symbol.Paths.Count > 0 ? symbol.Paths[0] : string.Empty;
            var lines = _read.Files.TryGetValue(path, out var entry) && entry.Lines is { } count ? Math.Max(1, count) : 1;
            return new NodeAt(path, new ExtractNode(symbol.Id, symbol.Kind, CodeGraphIds.FileName(path), path, 1, lines, string.Empty));
        }

        // The declaration with a body speaks for a symbol spread over partials and prototypes; else the first by path.
        var ordered = at.OrderBy(one => one.Path, StringComparer.Ordinal).ThenBy(one => one.Node.Start).ToList();
        return ordered.FirstOrDefault(one => one.Node.BodyStart is not null) ?? ordered[0];
    }

    private static IReadOnlyDictionary<(string Kind, GraphSide Side), int> Percentiles(IReadOnlyList<CodeEdge> edges, int nodes)
    {
        var percentile = new Dictionary<(string Kind, GraphSide Side), int>();

        foreach (var kind in edges.GroupBy(edge => edge.Kind, StringComparer.Ordinal))
        {
            foreach (var side in new[] { GraphSide.Out, GraphSide.In })
            {
                var degrees = kind
                    .GroupBy(edge => side == GraphSide.Out ? edge.From : edge.To, StringComparer.Ordinal)
                    .Select(node => node.Select(edge => side == GraphSide.Out ? edge.To : edge.From).Distinct(StringComparer.Ordinal).Count())
                    .Order()
                    .ToList();

                // The view's percentile: a node with no edge of the kind counts as none, else a view's busiest node is its own bound.
                var none = Math.Max(0, nodes - degrees.Count);
                var rank = (int)Math.Ceiling((none + degrees.Count) * 0.99) - 1;
                percentile[(kind.Key, side)] = rank < none ? 0 : degrees[rank - none];
            }
        }

        return percentile;
    }

    /// <summary>
    /// <paramref name="symbol"/> with its language's own separators - C++'s and PHP's <c>::</c>, PHP's <c>\</c> - written as
    /// the model's <c>.</c>, as every qualified name is held, a leading separator dropped.
    /// </summary>
    internal static string Dotted(string symbol) => symbol.Replace("::", ".", StringComparison.Ordinal).Replace('\\', '.').TrimStart('.');

    /// <summary>The words of <paramref name="name"/>: its parts at case changes, underscores, digits and dots, folded to lower case.</summary>
    /// <param name="name">A name, a query or a summary.</param>
    internal static IEnumerable<string> Split(string name)
    {
        var word = new System.Text.StringBuilder();

        for (var at = 0; at < name.Length; at++)
        {
            var ch = name[at];

            if (char.IsLetter(ch))
            {
                // A word begins at a capital after a small letter (FoldAsync), and at the last capital of a run that a small
                // letter follows (XMLParser, IShape).
                var begins = word.Length > 0 && char.IsUpper(ch)
                    && (!char.IsUpper(word[^1]) || (at + 1 < name.Length && char.IsLower(name[at + 1])));

                if (begins)
                {
                    yield return word.ToString().ToLowerInvariant();
                    word.Clear();
                }

                word.Append(ch);
            }
            else
            {
                if (word.Length > 0)
                {
                    yield return word.ToString().ToLowerInvariant();
                    word.Clear();
                }
            }
        }

        if (word.Length > 0)
        {
            yield return word.ToString().ToLowerInvariant();
        }
    }

    /// <summary>
    /// A query's words: how often it says each, each weighed by how rare it is among the view's names (one where no name
    /// holds it) as often as it is said, and the whole of those weights - so a long query, a step's prompt, is scored against
    /// each node in the time its name's words take.
    /// </summary>
    private sealed class Terms
    {
        public Terms(IEnumerable<string> words, IReadOnlyDictionary<string, double> idf)
        {
            var count = new Dictionary<string, int>(StringComparer.Ordinal);

            foreach (var word in words)
            {
                count[word] = count.GetValueOrDefault(word) + 1;
            }

            Count = count;
            Weight = count.ToDictionary(word => word.Key, word => word.Value * idf.GetValueOrDefault(word.Key, 1.0), StringComparer.Ordinal);
            Whole = Weight.Values.Sum();
        }

        public IReadOnlyDictionary<string, int> Count { get; }

        public IReadOnlyDictionary<string, double> Weight { get; }

        public double Whole { get; }
    }

    /// <summary>One place a node is declared: the file, and its extract there.</summary>
    /// <param name="Path">The file's path, relative to the tree with forward separators.</param>
    /// <param name="Node">The node as that file's extract holds it.</param>
    public sealed record NodeAt(string Path, ExtractNode Node);
}
