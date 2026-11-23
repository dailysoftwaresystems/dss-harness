using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.Output;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase;

/// <summary>
/// The lookups a <see cref="CodebaseGraphRead"/> is asked by, indexed once from it: each node by its id, handle, qualified
/// and simple name and the line it is on, each file's nodes, each node's edges in and out by kind, and the words its names
/// split into weighed by how rare they are. One owner of what the <c>graph</c>
/// tool and the <c>query-codebase-graph</c> verb answer from - the same view and query answer alike - over the neutral model,
/// no language of its own.
/// </summary>
public sealed class CodebaseGraphQueries
{
    /// <summary>What a card counts as its callers, and as its callees: the nodes the view holds one call reaches, each once.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> CallersOnly = GraphRelations.Edges(GraphRelations.Callers);

    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> CalleesOnly = GraphRelations.Edges(GraphRelations.Callees);

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
    private readonly IReadOnlyDictionary<string, FileExtract?> _extracts;
    private readonly IReadOnlySet<string> _testFiles;
    private readonly ConcurrentDictionary<string, GraphCard> _cards = new(StringComparer.Ordinal);

    /// <summary>Each node the view holds by each file that holds a part of it, by id and path: what its symbol's paths say, made once.</summary>
    private readonly HashSet<(string Id, string Path)> _partsIn = [];

    /// <summary>
    /// Each node's first declaration in each file that declares it - the one that begins first there - by id and path, made
    /// once from every declaration: what a card of the node as a file declares it, and the file's line index, read.
    /// </summary>
    private readonly Dictionary<(string Id, string Path), NodeAt> _declaredIn = [];

    /// <summary>The declaration that speaks for each node the view holds, by id (<see cref="Representative"/>), chosen once.</summary>
    private readonly Dictionary<string, NodeAt> _representatives = new(StringComparer.Ordinal);

    /// <summary>Every path the view holds, by its spelling in any case: made once, where a file system that finds a name in any case first asks.</summary>
    private readonly Lazy<ILookup<string, string>> _folded;

    /// <summary>Indexes <paramref name="read"/>: every lookup is built once, so the first answer pays for them and the rest read them.</summary>
    /// <param name="read">The view to answer from, as a sync or a check or the read-only open left it.</param>
    public CodebaseGraphQueries(CodebaseGraphRead read)
    {
        ArgumentNullException.ThrowIfNull(read);

        _read = read;
        _graph = new(() => GraphOf(read));
        _folded = new(() => read.Files.Keys.ToLookup(key => key, StringComparer.OrdinalIgnoreCase));
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
        _references = new(() => extracts
            .SelectMany(file => (file.Value?.References ?? []).Where(reference => !reference.Probe).Select(reference => (reference.Name, Place: (file.Key, reference.From.Whole(), reference.Line))))
            .ToLookup(reference => reference.Name, reference => reference.Place, StringComparer.Ordinal));

        // A file that declares a test - its implementation marks the test, or the link finds a declaration a test by a type another
        // file declares (CodeGraphLinker.TestsByType), never one its file only marks as one that may be, which no runner may
        // collect - is test code whole: its classes, fixtures and helpers beside the tests.
        var byType = CodeGraphLinker.TestsByType(extracts.Values.SelectMany(extract => extract?.Nodes ?? []), symbols.Edges).ToHashSet(StringComparer.Ordinal);
        _testFiles = extracts.Where(file => file.Value?.Nodes.Any(node => node.Test || (node.TestByType && byType.Contains(node.Id.Whole()))) == true).Select(file => file.Key).ToHashSet(StringComparer.Ordinal);

        // Each file's nodes by their ids, made once a file rather than looked for once a node.
        var declared = extracts.ToDictionary(file => file.Key, file => (file.Value?.Nodes ?? []).ToLookup(node => node.Id), StringComparer.Ordinal);
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
                if (_partsIn.Add((symbol.Id, path)))
                {
                    (inFile.TryGetValue(path, out var ids) ? ids : inFile[path] = []).Add(symbol.Id);
                }
            }

            // Each file's first declaration of the node, by the line it begins on, read once here: a node declared in many
            // files - a namespace, a partial type - is never looked through again for the one a file holds.
            foreach (var one in at)
            {
                if (!_declaredIn.TryGetValue((symbol.Id, one.Path), out var first) || one.Node.Start < first.Node.Start)
                {
                    _declaredIn[(symbol.Id, one.Path)] = one;
                }
            }

            nodes[symbol.Id] = at;

            var representative = (_representatives[symbol.Id] = Representative(symbol, at)).Node;
            byQualified.Add((representative.Qualified.Whole(), symbol.Id));
            bySimple.Add((representative.Name, symbol.Id));

            // A component is named by each of its element selectors too, as markup writes them: app-cart reaches its component.
            foreach (var selector in representative.Selectors ?? [])
            {
                bySimple.Add((selector, symbol.Id));
            }

            words[symbol.Id] = [.. Split(representative.Qualified.Whole())];
        }

        Unheld = [.. unheld];
        _unheld = unheld;
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

        _projects = new(() => _symbols.Values
            .Where(symbol => string.Equals(symbol.Kind, CodeNodeKinds.Project, StringComparison.Ordinal))
            .Select(symbol => Card(symbol.Id).Path)
            .Distinct(StringComparer.Ordinal)
            .GroupBy(DirectoryOf, StringComparer.Ordinal)
            .ToDictionary(directory => directory.Key, directory => (IReadOnlyList<string>)[.. directory.Order(StringComparer.Ordinal)], StringComparer.Ordinal));
        _out = symbols.Edges.ToLookup(edge => edge.From, StringComparer.Ordinal);
        _in = symbols.Edges.ToLookup(edge => edge.To, StringComparer.Ordinal);
        _parts = symbols.Edges.Where(edge => string.Equals(edge.Kind, CodeEdgeKinds.PartOf, StringComparison.Ordinal))
            .SelectMany(edge => new[] { (edge.From, edge.To), (edge.To, edge.From) })
            .GroupBy(pair => pair.Item1, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<string>)[.. group.Select(pair => pair.Item2).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)], StringComparer.Ordinal);
    }

    /// <summary>Each node's other parts, by id: what <see cref="CodeEdgeKinds.PartOf"/> joins it to, either way round.</summary>
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _parts;

    /// <summary>
    /// The node <paramref name="id"/> and every other part of its declaration: what the walks that read members - the
    /// <c>node</c> operation, a <c>related</c> walk, a type's outline, the spans a body is served from - walk as one.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    public IReadOnlyList<string> OneDeclaration(string id)
        => _parts.TryGetValue(id, out var parts) ? [id, .. parts] : [id];

    /// <summary>The view answered from: <c>base</c>, or an agent's name.</summary>
    public string View => _read.View;

    /// <summary>The commit the tree's HEAD named when it was listed, where it named one.</summary>
    public string? Head => _read.Head;

    /// <summary>
    /// The digest of the graph the view answers from: the digest of the manifest its link result was made from
    /// (<see cref="CodeGraphSymbolsFile.Graph"/>), which moves as the base the view stands on moves, with every file as the
    /// view holds it - its blob, its state, the implementation that read it and its extract, never when it was last looked
    /// at - so an agent's view moves with the agent's own writes too, where the base's digest alone does not. What tells a
    /// kept page the graph it was computed on is the graph it is asked of now: a step that wrote since page 1 recounts its
    /// later pages, and says so. Computed once a view, where first asked.
    /// </summary>
    public string Graph => _graph.Value;

    private readonly Lazy<string> _graph;

    /// <summary>The digest <see cref="Graph"/> answers, of the view <paramref name="read"/>.</summary>
    private static string GraphOf(CodebaseGraphRead read)
        => FileSystem.FileContentHash.Of(stream => System.Text.Json.JsonSerializer.Serialize(
            stream,
            new
            {
                graph = read.Symbols.Graph,
                files = read.Files.OrderBy(file => file.Key, StringComparer.Ordinal).Select(file => new { path = file.Key, file.Value.Blob, file.Value.State, file.Value.Language, file.Value.Extract }),
            },
            FileSystem.JsonStateFile.LineOptions));

    /// <summary>The tree, absolute.</summary>
    public string Tree => _read.Tree;

    /// <summary>How many files the view holds.</summary>
    public int FileCount => _read.Files.Count;

    /// <summary>How many declarations the view holds: its nodes that are code declarations, files and projects left out.</summary>
    public int DeclarationCount => _symbols.Values.Count(symbol => CodeNodeKinds.Extracted.Contains(symbol.Kind, StringComparer.Ordinal));

    /// <summary>
    /// The view's most central declarations, most first: each by how many files other than its own hold a reference that
    /// binds to it - over calls, references, instantiations, inheritance, implementations and dispatches, a test file's never -
    /// raised by how its name matches <paramref name="task"/> as <see cref="Find"/> weighs a name's words: the share of the
    /// task's words its name holds whole, each weighed by how often the task says it and how rare it is among the view's
    /// names, a name that holds them all raised by <see cref="TaskRaise"/>. Ties broken by id; no declaration of a file the
    /// view holds as generated, nor of one that declares a test.
    /// </summary>
    /// <param name="task">The task the centrality is raised for - a run's inputs, a side-task's message - or empty for none.</param>
    public IReadOnlyList<GraphCard> Central(string task)
    {
        ArgumentNullException.ThrowIfNull(task);

        var terms = new Terms(Split(task), _idf);

        return [.. _symbols.Keys
            .Where(id => !_symbols[id].Paths.Any(path => _testFiles.Contains(path)))
            .Select(Card)
            .Where(card => CodeNodeKinds.Extracted.Contains(card.Kind, StringComparer.Ordinal))
            .Where(card => !Generated(card.Path))
            .Select(card => (Card: card, Centrality: CentralityOf(card, terms)))
            .OrderByDescending(scored => scored.Centrality.Files + (scored.Centrality.Named * TaskRaise))
            .ThenBy(scored => scored.Card.Id, StringComparer.Ordinal)
            .Select(scored => scored.Card)];
    }

    /// <summary>
    /// What <see cref="Central"/> ranks the node <paramref name="id"/> names by: how many files other than its own, and
    /// other than a test's, hold a reference that binds to it, and the share of <paramref name="task"/>'s weight its name
    /// holds, from 0 to 1.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="task">The task the centrality is raised for, or empty for none.</param>
    internal (int Files, double Named) CentralityOf(string id, string task) => CentralityOf(Card(id), new Terms(Split(task), _idf));

    /// <inheritdoc cref="CentralityOf(string, string)"/>
    private (int Files, double Named) CentralityOf(GraphCard card, Terms terms)
    {
        IReadOnlyList<(GraphSide Side, string Kind)> binding =
        [
            (GraphSide.In, CodeEdgeKinds.Calls),
            (GraphSide.In, CodeEdgeKinds.References),
            (GraphSide.In, CodeEdgeKinds.Instantiates),
            (GraphSide.In, CodeEdgeKinds.Inherits),
            (GraphSide.In, CodeEdgeKinds.Implements),
            (GraphSide.In, CodeEdgeKinds.Dispatches),
        ];
        var own = _symbols[card.Id].Paths;

        // A test's reference is the test's, not the code's use of it: no test file counts.
        return (
            Steps(card.Id, binding).Select(step => Card(step.To).Path).Where(path => !own.Contains(path, StringComparer.Ordinal) && !_testFiles.Contains(path)).Distinct(StringComparer.Ordinal).Count(),
            terms.Whole > 0 ? NameWeight(card, terms) / terms.Whole : 0);
    }

    /// <summary>
    /// How far a name that holds every word of the task is raised among the view's most central declarations
    /// (<see cref="Central"/>): as far as ten other files' references bind it, a name holding part of the task's weight
    /// raised by that part.
    /// </summary>
    public const int TaskRaise = 10;

    /// <summary>The files, by path, whose extract the view no longer holds - one that does not read, or no longer declares what the link names - their declarations in no answer.</summary>
    public IReadOnlyList<string> Unheld { get; }

    /// <summary>The files of <see cref="Unheld"/>, looked up by path.</summary>
    private readonly IReadOnlySet<string> _unheld;

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
    /// The path the view holds <paramref name="path"/> at, as <see cref="HeldPath(string)"/> finds it - and, where the tree's
    /// file system finds a name in any case (<paramref name="comparison"/> ignores case), the one file the view holds that
    /// the path names in another case: never one of two the view holds that differ in their case alone.
    /// </summary>
    /// <param name="path">A path an agent or a person wrote, relative to the tree.</param>
    /// <param name="comparison">How the tree's file system compares names, as it answers for itself.</param>
    public string? HeldPath(string path, StringComparison comparison)
    {
        var held = HeldPath(path);

        if (held is not null || comparison != StringComparison.OrdinalIgnoreCase)
        {
            return held;
        }

        return _folded.Value[path.Replace('\\', '/')].ToList() is [var one] ? one : null;
    }

    /// <summary>
    /// The view's entry of the file it holds at <paramref name="held"/> - its state, its size and its lines as the graph read
    /// them - or null where it holds none there.
    /// </summary>
    /// <param name="held">The path the view holds the file at (<see cref="HeldPath(string, StringComparison)"/>).</param>
    public CodeGraphFileEntry? FileAt(string held)
    {
        ArgumentNullException.ThrowIfNull(held);

        return _read.Files.GetValueOrDefault(held);
    }

    /// <summary>
    /// Resolves <paramref name="symbol"/> - a handle, an id, a <c>path:line</c>, a qualified name (or one written with some
    /// of its scopes, its last parts) or a simple name - to the node it names, its candidates, or none.
    /// </summary>
    /// <param name="symbol">What an agent wrote: a handle, an id, a <c>path:line</c>, a qualified name or a simple name.</param>
    public GraphResolve Resolve(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);

        var (exact, named) = Naming(symbol);

        if (exact is not null)
        {
            return GraphResolve.One(exact);
        }

        // The first rule a name is read by that names any node decides: a qualified name before a simple one, and so on.
        var ids = named.FirstOrDefault(found => found.Count > 0) ?? [];

        return ids.Count switch
        {
            0 => GraphResolve.None,
            1 => GraphResolve.One(Card(ids[0])),
            _ => GraphResolve.Many([.. ids.Select(Card).OrderBy(card => card.Id, StringComparer.Ordinal)]),
        };
    }

    /// <summary>
    /// The node <paramref name="symbol"/> names by itself - by its handle, its id or a <c>path:line</c>, as <see cref="Resolve"/>
    /// reads them first - or null where it names none so and is read as a name.
    /// </summary>
    /// <param name="symbol">What an agent wrote.</param>
    public GraphCard? NamedExactly(string symbol)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(symbol);
        return Naming(symbol).Exact;
    }

    /// <summary>
    /// Every declaration the view holds that <paramref name="data"/> names: the one node it names by itself - its handle, its
    /// id, a <c>path:line</c> - or else every node any rule a name is read by finds, where <see cref="Resolve"/> takes the
    /// first rule that finds one - by its qualified name, by its simple name, by one written with its language's own
    /// separators (<see cref="Dotted"/>) or spelt as a key path is (<see cref="CodeKeyPaths.Dotted"/>), by a qualified name it
    /// ends with at a dot, or, of a key path, by a key whose own name spells it (<see cref="NamedBy"/>) - each once, ordered by
    /// id then by path: the definers of the data, whose calls, uses, references, inheritors, implementations and overrides the
    /// <c>sites</c> operation answers beside them. A data the view holds no declaration of answers empty, which that answer says - the graph's
    /// silence is never read as the data's absence, for the text search answers beside it.
    /// </summary>
    /// <param name="data">What an agent wrote: a symbol as <see cref="Resolve"/> reads one, a key path, a diagnostic id.</param>
    public IReadOnlyList<GraphCard> DefinersOf(string data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);

        var (exact, named) = Naming(data);

        if (exact is not null)
        {
            return [exact];
        }

        return [.. named.SelectMany(found => found).Distinct(StringComparer.Ordinal).Where(Holds).Select(Card)
            .OrderBy(card => card.Id, StringComparer.Ordinal).ThenBy(card => card.Path, StringComparer.Ordinal)];
    }

    /// <summary>
    /// What <paramref name="symbol"/> names, by the one reading of what an agent writes that <see cref="Resolve"/> and
    /// <see cref="DefinersOf"/> share: the node it names by itself - its handle, its id, or a <c>path:line</c>, a path written
    /// with backslashes read as the file the view holds, as a path typed on Windows is - or, where it names none so, the
    /// ids each rule a name is read by finds, in the rules' order (<see cref="NamedBy"/>).
    /// </summary>
    private (GraphCard? Exact, IEnumerable<IReadOnlyList<string>> Named) Naming(string symbol)
    {
        if (_handles.TryGetValue(symbol, out var byHandle))
        {
            return (Card(byHandle), []);
        }

        if (_symbols.ContainsKey(symbol))
        {
            return (Card(symbol), []);
        }

        if (HeldPath(symbol) is { } held && held != symbol)
        {
            return Naming(held);
        }

        return AtLine(symbol) is { } atLine ? (atLine, []) : (null, NamedBy(symbol));
    }

    /// <summary>
    /// The ids each rule a written name is read by finds, in the rules' order: its qualified name, its simple name, the
    /// qualified name it is written as with its language's own separators (<see cref="Dotted"/>) and as .NET's configuration
    /// spells a key path (<see cref="CodeKeyPaths.Dotted"/>: <c>Payments:TimeoutSeconds</c>, <c>Payments__TimeoutSeconds</c>),
    /// the qualified names either ends at a dot - a name written with some of its scopes (Order.Total) names each node whose
    /// qualified name ends with it - and, last, each key whose own name spells such a path as .NET's configuration reads a
    /// key's name - an environment variable's (<c>Payments__TimeoutSeconds</c> under a compose file's <c>environment</c>), a
    /// settings file's flat key - which names that path wherever it stands (<see cref="CodeKeyPaths.Spellings"/>). A key path
    /// in any spelling so finds the same keys, and a name the earlier rules find resolves as it did.
    /// </summary>
    private IEnumerable<IReadOnlyList<string>> NamedBy(string symbol)
    {
        yield return [.. _byQualified[symbol]];
        yield return [.. _bySimple[symbol]];

        var forms = Forms(symbol);

        foreach (var form in forms.Where(form => form != symbol))
        {
            yield return [.. _byQualified[form]];
        }

        // A name written with some of its scopes names each node named by its last place - as it is written, a data file's
        // item with its brackets as its key is named - whose qualified name ends with it at a dot.
        foreach (var form in forms.Where(form => SelectorName(form) is null))
        {
            if (CodeKeyPaths.Last(form) is var last && last != form)
            {
                yield return [.. _bySimple[last].Where(id => Card(id).Qualified.EndsWith("." + form, StringComparison.Ordinal))];
            }
        }

        foreach (var spelt in forms.SelectMany(CodeKeyPaths.Spellings).Distinct(StringComparer.Ordinal))
        {
            yield return [.. _bySimple[spelt].Where(id => string.Equals(Card(id).SubKind, CodeSubKinds.Key, StringComparison.Ordinal))];
        }
    }

    /// <summary>
    /// The qualified names <paramref name="symbol"/> may be written as, each once: with its language's own separators written
    /// as dots (<see cref="Dotted"/>), and, where it is spelt as .NET's configuration spells a key path
    /// (<see cref="SpeltAsAKeyPath"/>), that path, as the key's own reader writes it.
    /// </summary>
    private static IReadOnlyList<string> Forms(string symbol)
        => SpeltAsAKeyPath(symbol) is { } spelt && spelt != Dotted(symbol) ? [Dotted(symbol), spelt] : [Dotted(symbol)];

    /// <summary>
    /// The key path <paramref name="symbol"/> spells as .NET's configuration spells one - its places parted by colons or by
    /// double underscores (<see cref="CodeKeyPaths.Dotted"/>) - or null where it spells none so, or is a rule's selector
    /// (<see cref="SelectorName"/>), which is never a key path: <c>.card__title</c> is the class <c>card__title</c>, as BEM
    /// names an element of a block, never the path <c>.card.title</c>.
    /// </summary>
    private static string? SpeltAsAKeyPath(string symbol) => SelectorName(symbol) is null ? CodeKeyPaths.Dotted(symbol) : null;

    /// <summary>
    /// What a <c>sites</c> answer looks for <paramref name="data"/> by in the literals and in the text it searches
    /// (<see cref="GraphSitesSearch"/>), the data asked by a name, its definers <paramref name="definers"/>
    /// (<see cref="DefinersOf"/>): the word code and data write it by, and each key path it names as an environment variable
    /// writes one. The word is the name of the node the data names by itself - its handle, its id, a <c>path:line</c> - else
    /// the selector it is written as, a rule's (<c>.btn</c>, <c>.card__title</c>), whole; else its last name
    /// (<see cref="CodeKeyPaths.LastName"/>) - <c>Total</c> of <c>Order.Total</c> and of <c>Order::Total</c>, <c>this[]</c> of
    /// <c>Canvas.this[]</c> - read as a key path where it is spelt as one (<see cref="SpeltAsAKeyPath"/>): where it names a key;
    /// where its definers, none a key, are found by the path it spells and none is named by it as it is written - a member the
    /// options pattern binds a section to, <c>Api.JwtSettings.Secret</c> of <c>JwtSettings:Secret</c>; and where nothing names
    /// it and it is written as no node's id and no file's line (<see cref="WrittenAsAPlace"/>) - <c>TimeoutSeconds</c> of
    /// <c>Payments:TimeoutSeconds</c> and of <c>Payments__TimeoutSeconds</c>, <c>Secret</c> of <c>JwtSettings:Secret</c>. Where none
    /// of its definers is a key and one is named by it as it is written (<see cref="NamedAsWritten"/>) it is read as that name, as
    /// code that declares <c>RETRY__LIMIT</c> writes it. The key paths are those of the keys among its definers, and the path
    /// the data spells where it is read as one, each as an environment variable writes it (<see cref="CodeKeyPaths.Environment"/>):
    /// <c>Payments__TimeoutSeconds</c>, which no whole word of its last key is held by. A node the data names by itself, a
    /// selector, and a name read as the name it is written as spell no path of their own.
    /// </summary>
    /// <param name="data">The data as it is written, asked by a name.</param>
    /// <param name="definers">The data's definers.</param>
    public GraphSitesSearch SitesSearch(string data, IReadOnlyList<GraphCard> definers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(data);
        ArgumentNullException.ThrowIfNull(definers);

        var keys = definers.Where(card => string.Equals(card.SubKind, CodeSubKinds.Key, StringComparison.Ordinal)).ToList();
        var exact = NamedExactly(data);
        var spelt = exact is null && SpeltAsAKeyPath(data) is { } path
            && (keys.Count > 0 || (definers.Count > 0 ? !NamedAsWritten(data, definers) : !WrittenAsAPlace(data)))
            ? path
            : null;
        var word = exact?.Name ?? (SelectorName(data) is not null ? data : CodeKeyPaths.LastName(spelt ?? Dotted(data)));

        return GraphSitesSearch.Of(word, keys.Select(card => card.Qualified).Append(spelt).OfType<string>().Select(CodeKeyPaths.Environment).OfType<string>());
    }

    /// <summary>
    /// Whether one of <paramref name="definers"/> is named by <paramref name="data"/> as it is written - its name or its qualified
    /// name the data, or the data with its language's own separators written as dots (<see cref="Dotted"/>) - and not by the key
    /// path the data spells: a name code declares, <c>RETRY__LIMIT</c>, which the data is read as.
    /// </summary>
    private static bool NamedAsWritten(string data, IReadOnlyList<GraphCard> definers)
    {
        var dotted = Dotted(data);

        return definers.Any(card => card.Name == data || card.Qualified == data || card.Name == dotted || card.Qualified == dotted);
    }

    /// <summary>
    /// Whether <paramref name="symbol"/> is written as a node's id is - its part before its first colon a language of this
    /// build, or <c>file</c> - or as a file's line - its part after its last colon digits alone, and its part before it a
    /// file the view holds or a path a separator parts: what names a place by itself, and is never read as a key path,
    /// whether or not the view holds what it names.
    /// </summary>
    private bool WrittenAsAPlace(string symbol)
    {
        var first = symbol.IndexOf(':', StringComparison.Ordinal);

        if (first > 0 && (symbol[..first] == CodeNodeKinds.File || CodeGraphExtractors.OfThisBuild.Find(symbol[..first]) is not null))
        {
            return true;
        }

        var last = symbol.LastIndexOf(':');

        return last > 0 && last < symbol.Length - 1 && !symbol.AsSpan(last + 1).ContainsAnyExceptInRange('0', '9')
            && (HeldPath(symbol[..last]) is not null || symbol.AsSpan(0, last).ContainsAny('/', '\\'));
    }

    /// <summary>
    /// Every place the view holds a string literal whose value <paramref name="search"/> holds - a <c>sites</c> ask's
    /// (<see cref="GraphSitesSearch.Holds"/>) - each place among the literals of every extract the view holds
    /// (<see cref="FileExtract.Literals"/> - no node of the graph is a literal's, so this read of them is the one index of
    /// them), with the node whose code holds it - the file's own node for what its top holds outside its declarations - its
    /// file and its line. Ordered by path, then line. No literal is masked here: the answer that serves these places masks
    /// them as it computes, as it masks every line it shows.
    /// </summary>
    /// <param name="search">What a value is asked whether it holds, as the ask's text search asks each line.</param>
    public IReadOnlyList<GraphLiteralPlace> LiteralSites(GraphSitesSearch search)
    {
        ArgumentNullException.ThrowIfNull(search);

        var places = new List<GraphLiteralPlace>();

        foreach (var (path, extract) in _extracts.OrderBy(file => file.Key, StringComparer.Ordinal))
        {
            foreach (var literal in extract?.Literals ?? [])
            {
                if (!search.Holds(literal.Value))
                {
                    continue;
                }

                foreach (var place in literal.Places)
                {
                    var id = place.Node is { } node && Holds(node.Whole()) ? node.Whole() : CodeGraphIds.FileId(path);
                    places.Add(new(Holds(id) ? Part(id, path) : null, path, place.Line, literal.Value));
                }
            }
        }

        return [.. places.OrderBy(place => place.Path, StringComparer.Ordinal).ThenBy(place => place.Line)];
    }

    /// <summary>
    /// The bare name of the class's or the id's selector <paramref name="word"/> is written as - <c>.btn-primary</c> is the
    /// class <c>btn-primary</c>, <c>#main</c> the id <c>main</c>: a dot or a hash and then letters, digits, hyphens and
    /// underscores alone, as a rule names a class or an id - or null where it is no such selector.
    /// </summary>
    /// <param name="word">The data as it is asked.</param>
    public static string? SelectorName(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        if (word.Length < 2 || word[0] is not ('.' or '#'))
        {
            return null;
        }

        foreach (var character in word.AsSpan(1))
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return null;
            }
        }

        return word[1..];
    }

    /// <summary>Whether <paramref name="character"/> is one a word is made of, as a whole word's boundaries read it (<see cref="GraphWord.Whole"/>): a letter, a number or an underscore.</summary>
    internal static bool WordCharacter(char character) => char.IsLetter(character) || char.IsNumber(character) || character == '_';

    /// <summary>
    /// What a <c>sites</c> answer searches the file at <paramref name="path"/> by text for, beside what the graph answers of its
    /// data - each file for the first of these it is - or null where the graph answers what the file holds of the data, and
    /// the answer reads it from the graph alone: the view does not hold the file; holds it in a state no implementation of a
    /// language read it in (<see cref="ReadByALanguage"/>) - listed by the fallback alone, no extractor reading its language,
    /// or unreadable, ambiguous and the rest; holds it read, but no longer the extract it read (<see cref="Unheld"/>), its
    /// declarations in no answer; is not sure of it (<see cref="UnsureOfFile"/>); holds it outlined, its values
    /// and text held by no literal of the graph; holds a reference of it named the data that the linker bound to nothing of
    /// that name (<paramref name="unbound"/>, <see cref="UnboundNaming"/>); or, for a quoted value, holds it at all, for the
    /// graph holds a value only where code writes it as a string literal. The one rule of what the graph's silence is never
    /// taken for.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as the reader spells it.</param>
    /// <param name="unbound">The files, as the view holds them, that hold a reference named the data the linker bound to nothing of that name.</param>
    /// <param name="value">Whether the data is a quoted value, asked by its value alone.</param>
    public GraphTextScope? TextScopeOf(string path, IReadOnlySet<string> unbound, bool value)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(unbound);

        if (HeldPath(path) is not { } held)
        {
            return new(GraphTextReason.Unheld);
        }

        var entry = _read.Files[held];

        return !ReadByALanguage(held) ? new(GraphTextReason.Unread, entry.State)
            : _unheld.Contains(held) ? new(GraphTextReason.Unextracted)
            : UnsureOfFile(held) is not null ? new(GraphTextReason.Unsure)
            : string.Equals(entry.State, CodeFileStates.Outlined, StringComparison.Ordinal) ? new(GraphTextReason.Outlined)
            : unbound.Contains(held) ? new(GraphTextReason.Unbound)
            : value ? new(GraphTextReason.Value)
            : null;
    }

    /// <summary>
    /// Whether an implementation of a language read the file the view holds at <paramref name="path"/> - it is parsed,
    /// outlined or generated - so the graph answers what it holds; not where it is listed by the fallback alone, or held in
    /// a state none read it in.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as the reader spells it.</param>
    public bool ReadByALanguage(string path)
        => HeldPath(path) is { } held && _read.Files.TryGetValue(held, out var entry)
            && entry.State is CodeFileStates.Parsed or CodeFileStates.Outlined or CodeFileStates.Generated;

    /// <summary>
    /// The files, by the path the view holds them at, a node of which makes a reference named one of <paramref name="names"/>
    /// that the linker bound to nothing of that name at the reference's line - external, or bound by none of the ways it
    /// was looked for - and is no probe (<see cref="ExtractReference.Probe"/>), which asks whether a declaration is there:
    /// where the graph could not tell what a reference of the name is, a sign it may be silent of a place that decides it,
    /// read of one datum at a time. A reference bound to a declaration of another name - one an import renames - is said
    /// so too. Counting every external reference of a file toward its unsureness (<see cref="UnsureOfFile"/>) would be no
    /// sign of a datum: what the repository does not declare - a library's members, the language's own - is most of them.
    /// </summary>
    /// <param name="names">The names asked of, each a reference's simple name.</param>
    public IReadOnlySet<string> UnboundNaming(IEnumerable<string> names)
    {
        ArgumentNullException.ThrowIfNull(names);

        var files = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            // Where a reference of the name bound: each edge into a node of that name, by the node it leaves and its line.
            var bound = _bySimple[name].SelectMany(OneDeclaration).Distinct(StringComparer.Ordinal)
                .SelectMany(id => _in[id])
                .Select(edge => (edge.From, edge.Line))
                .ToHashSet();

            foreach (var (path, from, line) in _references.Value[name])
            {
                if (!bound.Contains((from, line)))
                {
                    files.Add(path);
                }
            }
        }

        return files;
    }

    /// <summary>Every reference the view's extracts make that is no probe, by the simple name it names: its file, the node it is made from and its line - made once a view, where first asked.</summary>
    private readonly Lazy<ILookup<string, (string Path, string From, int Line)>> _references;

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

        var ranked = Scored(query, kind, path, near);

        return new GraphMatches([.. ranked.Skip(offset).Take(limit).Select(scored => scored.Card)], ranked.Count);
    }

    /// <summary>
    /// Every node that matches <paramref name="query"/>, with its score, in the order <see cref="Find"/> gives them: the one
    /// ranking of a query's matches, which the context pack seeds from - every match, weighed by its score.
    /// </summary>
    /// <param name="query">The words to match, split and folded as a name is.</param>
    /// <param name="kind">The one kind to keep, where it is given.</param>
    /// <param name="path">The one file to keep the nodes of, where it is given.</param>
    /// <param name="near">How near each node is to the step's own work, which breaks a tie; none where it is not given.</param>
    public IReadOnlyList<(GraphCard Card, double Score)> Scored(string query, string? kind = null, string? path = null, GraphNearness? near = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var terms = new Terms(Split(query), _idf);

        return [.. _symbols.Values
            .Where(symbol => kind is null || string.Equals(symbol.Kind, kind, StringComparison.Ordinal))
            .Select(symbol => Card(symbol.Id))
            .Where(card => path is null || string.Equals(card.Path, path, StringComparison.Ordinal))
            .Select(card => (Card: card, Score: Score(card, query, terms)))
            .Where(scored => scored.Score > 0)
            .OrderByDescending(scored => scored.Card.Generated ? 0 : 1)
            .ThenByDescending(scored => scored.Score)
            .ThenBy(scored => near?.Of(scored.Card) ?? GraphNearness.Far)
            .ThenBy(scored => scored.Card.Id, StringComparer.Ordinal)];
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
        => _partsIn.Contains((id, path)) && (_nodes[id].Count == 0 || DeclaredIn(id, path) is not null)
            ? Card(id, path)
            : null;

    /// <summary>
    /// How many cards of a node as a file declares it this view has made, each walking the node's callers and callees: the
    /// work an answer is counted by, so a reading that makes a card of every node of a file to find one is seen.
    /// </summary>
    internal long CardsMade => Interlocked.Read(ref _cardsMade);

    private long _cardsMade;

    /// <summary>
    /// How many declarations this view has looked at to find a node's declaration in a file - for a card of it as the file
    /// declares it, for the file's line index - each lookup one: the work a node declared in many files costs each file, so a
    /// reading that looks through every file's declaration of the node for each of its files is seen.
    /// </summary>
    internal long DeclarationsRead => Interlocked.Read(ref _declarationsRead);

    private long _declarationsRead;

    /// <summary>
    /// The first declaration of the node <paramref name="id"/> names in the file at <paramref name="path"/> - the one that
    /// begins first there - or null where the file declares none of it: read from the index made once, one declaration
    /// looked at, however many files declare the node.
    /// </summary>
    private NodeAt? DeclaredIn(string id, string path)
    {
        Interlocked.Increment(ref _declarationsRead);
        return _declaredIn.GetValueOrDefault((id, path));
    }

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
        Interlocked.Increment(ref _cardsMade);

        var symbol = _symbols[id];
        var (where, node) = (path is null ? null : DeclaredIn(id, path)) ?? _representatives[id];
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
            node.Qualified.Whole(),
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

    /// <summary>The edges of the node <paramref name="id"/> names - and of every other part of its declaration - grouped by kind as the <c>node</c> operation gives them, each a card.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public GraphEdges Edges(string id)
    {
        var of = OneDeclaration(id);

        // Each kind and side a group of its own, those of one group one after another, as the node operation lists them.
        IReadOnlyList<GraphEdge> Group(IReadOnlyList<(GraphSide Side, string Kind)> edges) => [.. edges.SelectMany(edge => Reached(of, [edge]))];

        return new GraphEdges(
            Group(GraphRelations.Edges(GraphRelations.Callers)),
            Group(GraphRelations.Edges(GraphRelations.Callees)),
            Group([(GraphSide.Out, CodeEdgeKinds.Inherits), (GraphSide.Out, CodeEdgeKinds.Implements), (GraphSide.Out, CodeEdgeKinds.MixesIn)]),
            Group(GraphRelations.Edges(GraphRelations.Derived)),
            Group(GraphRelations.Edges(GraphRelations.Implementations)),
            Group(Overriding),
            Group([(GraphSide.Out, CodeEdgeKinds.References), (GraphSide.Out, CodeEdgeKinds.Instantiates), (GraphSide.In, CodeEdgeKinds.References), (GraphSide.In, CodeEdgeKinds.Instantiates)]),
            Group(Users),
            Group(Using),
            Group(GraphRelations.Edges(GraphRelations.Tests)),
            Group([(GraphSide.Out, CodeEdgeKinds.Imports), (GraphSide.Out, CodeEdgeKinds.ProjectReference), (GraphSide.In, CodeEdgeKinds.Imports), (GraphSide.In, CodeEdgeKinds.ProjectReference)]),
            [.. of.Where(part => part != id).Select(part => new GraphEdge(Card(part), CodeConfidences.Exact))]);
    }

    /// <summary>What a node's users are: the nodes that read or write it without calling it.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> Users = [(GraphSide.In, CodeEdgeKinds.Uses)];

    /// <summary>What a node uses: the nodes it reads or writes without calling them.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> Using = [(GraphSide.Out, CodeEdgeKinds.Uses)];

    /// <summary>What makes a node: a call of a type, an element of a component.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> Makers = [(GraphSide.In, CodeEdgeKinds.Instantiates)];

    /// <summary>A node's overrides both ways: what it overrides, and what overrides it.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> Overriding = [(GraphSide.Out, CodeEdgeKinds.Overrides), (GraphSide.In, CodeEdgeKinds.Overrides)];

    /// <summary>What names a node by its kind without making it: a type mention, a test of a value's kind.</summary>
    private static readonly IReadOnlyList<(GraphSide Side, string Kind)> Referrers = [(GraphSide.In, CodeEdgeKinds.References)];

    /// <summary>
    /// Each way a place decides that the definers' own edges reach (<see cref="SitesOf"/>), with the edges that reach it - a
    /// relation's own where one walks them (<see cref="GraphRelations.Edges"/>), the <c>node</c> operation's groups' else.
    /// </summary>
    private static readonly IReadOnlyList<(string How, IReadOnlyList<(GraphSide Side, string Kind)> Edges)> Deciding =
    [
        (GraphSiteHows.Calls, GraphRelations.Edges(GraphRelations.Callers)),
        (GraphSiteHows.Uses, Users),
        (GraphSiteHows.Instantiates, Makers),
        (GraphSiteHows.References, Referrers),
        (GraphSiteHows.Inherits, GraphRelations.Edges(GraphRelations.Derived)),
        (GraphSiteHows.Implements, GraphRelations.Edges(GraphRelations.Implementations)),
        (GraphSiteHows.Overrides, Overriding),
    ];

    /// <summary>
    /// What decides of a <c>sites</c> ask's data beside its definers: one hop off each definer's own edges - every other part
    /// of its declaration walked as the definer itself - each way of deciding a group (<see cref="GraphSiteHows"/>): what
    /// calls it, uses it, makes it, names it by its kind - a type mention, an <c>isinstance</c>, an <c>instanceof</c>, a
    /// <c>catch</c> - inherits it or mixes it in, implements it or answers it for a type through its dispatches, overrides it
    /// or is overridden by it; each node a group reaches once, with the surest confidence of the edges that reach it. No
    /// reached node's own edges are walked, so a hub is listed and never gone through.
    /// </summary>
    /// <param name="definers">The definers, by id, the view's.</param>
    public IReadOnlyList<GraphSiteGroup> SitesOf(IReadOnlyList<string> definers)
    {
        ArgumentNullException.ThrowIfNull(definers);

        var of = definers.SelectMany(OneDeclaration).Distinct(StringComparer.Ordinal).ToList();

        return [.. Deciding.Select(how => new GraphSiteGroup(how.How, Reached(of, how.Edges)))];
    }

    /// <summary>
    /// The nodes one edge of <paramref name="edges"/> reaches from any of <paramref name="of"/>, each once, with the surest
    /// confidence of the edges that reach it, and whether every such edge is recall (<see cref="CodeEdgeKinds.Recall"/>): the
    /// one grouping of what a walk of one hop reaches, the <c>node</c> operation's and a <c>sites</c> answer's.
    /// </summary>
    private IReadOnlyList<GraphEdge> Reached(IReadOnlyList<string> of, IReadOnlyList<(GraphSide Side, string Kind)> edges)
        => [.. of.SelectMany(one => Steps(one, edges))
            .GroupBy(step => step.To, StringComparer.Ordinal)
            .Select(alike => new GraphEdge(Card(alike.Key), alike.Select(step => step.Confidence).Aggregate(CodeConfidences.Surer)) { Recall = alike.All(step => CodeEdgeKinds.Recall(step.Kind)) })];

    /// <summary>
    /// The nodes reached from <paramref name="id"/> - and every other part of its declaration, a part walked as the node
    /// itself - over <paramref name="relation"/> to <paramref name="depth"/>, by distance, each edge into the kind of node
    /// the relation enters alone (<see cref="GraphRelations.Into"/>). Every node reached is gone through, whatever edges it
    /// has: the walk knows each node it reached and never reaches it again, so it is linear in the edges it follows, its
    /// cycles among them, and ends where a hop reaches nothing new, however deep it was asked to go.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="relation">What to follow: one of <see cref="GraphRelations.All"/>.</param>
    /// <param name="depth">How many hops, from 1.</param>
    public IReadOnlyList<GraphReached> Related(string id, string relation, int depth)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);

        var edges = GraphRelations.Edges(relation);
        var into = GraphRelations.Into(relation);
        var of = OneDeclaration(id);
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        seen.UnionWith(of);
        var reached = new List<GraphReached>();
        var frontier = new List<string>(of);

        for (var hop = 1; hop <= depth && frontier.Count > 0; hop++)
        {
            var next = new List<string>();

            foreach (var from in frontier)
            {
                foreach (var steps in Steps(from, edges, into).GroupBy(step => step.To, StringComparer.Ordinal).OrderBy(alike => alike.Key, StringComparer.Ordinal))
                {
                    var to = steps.Key;

                    if (!seen.Add(to))
                    {
                        continue;
                    }

                    // A use is recall, never a doubt: its candidates leave the walk as sure as it was.
                    reached.Add(new GraphReached(Card(to), hop)
                    {
                        Candidates = steps.Any(step => !CodeEdgeKinds.Recall(step.Kind) && string.Equals(step.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal)),
                        Recall = steps.All(step => CodeEdgeKinds.Recall(step.Kind)),
                    });

                    next.Add(to);
                }
            }

            frontier = next;
        }

        return reached;
    }

    /// <summary>
    /// Each node the view holds that one edge of <paramref name="edges"/> reaches from <paramref name="id"/>, once for each
    /// kind and side it is reached by, with the surest confidence of the edges that reach it so - by id, then by the order
    /// <paramref name="edges"/> names them; where <paramref name="into"/> is given, only by an edge whose target is of that
    /// kind within a kind.
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    /// <param name="edges">The kinds to follow, each with the side it is followed from.</param>
    /// <param name="into">The kind within a kind each edge's target must be (<see cref="GraphRelations.Into"/>); null for any.</param>
    public IReadOnlyList<GraphStep> Steps(string id, IReadOnlyList<(GraphSide Side, string Kind)> edges, string? into = null)
    {
        ArgumentNullException.ThrowIfNull(edges);

        return [.. edges
            .SelectMany((edge, order) => (edge.Side == GraphSide.Out ? _out[id] : _in[id])
                .Where(found => string.Equals(found.Kind, edge.Kind, StringComparison.Ordinal))
                .Where(found => into is null || string.Equals(SubKindOf(found.To), into, StringComparison.Ordinal))
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

    /// <summary>The declarations inside the type <paramref name="id"/> names - and every other part of its declaration - each with its span and hash; <paramref name="id"/> is a node the view holds.</summary>
    /// <param name="id">A type node's id, the view's.</param>
    public IReadOnlyList<GraphCard> OutlineType(string id)
        => [.. OneDeclaration(id).SelectMany(one => _out[one])
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
            .GroupBy(TopDirectory, StringComparer.Ordinal)
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

    /// <summary>The node <paramref name="id"/> names across its files - and every other part of its declaration across its - so <c>body</c> and <c>edit</c> read each span and recompute its hash.</summary>
    /// <param name="id">A node's id, the view's.</param>
    public IReadOnlyList<NodeAt> At(string id)
        => OneDeclaration(id).SelectMany(AtOne).ToList();

    /// <summary>The places the node <paramref name="id"/> itself is declared at, without its parts.</summary>
    private IReadOnlyList<NodeAt> AtOne(string id) => _nodes.TryGetValue(id, out var at) ? at : [];

    /// <summary>
    /// Whether a node an answer names makes the answer not sure, so it adds a text search: its file carries a
    /// <c>generated</c> or a <c>dynamic</c> mark, or it has external callees. An edge of <c>candidates</c> an answer gives
    /// makes it not sure too, judged where the answer gives it - but a use's, which is recall
    /// (<see cref="CodeEdgeKinds.Recall"/>).
    /// </summary>
    /// <param name="id">A node's id, the view's.</param>
    public bool Unsure(string id)
        => (_nodes.TryGetValue(id, out var at) && at.Count > 0 && (Generated(at[0].Path) || Dynamic(at[0].Path)))
            || _read.Symbols.External.GetValueOrDefault(id) > 0;

    /// <summary>
    /// Whether <paramref name="edge"/>, which an answer gives, makes the answer not sure: it is of <c>candidates</c>, or
    /// the node it reaches is <see cref="Unsure"/> - but a use's, which is recall and never a doubt
    /// (<see cref="CodeEdgeKinds.Recall"/>). The one rule of it, the <c>node</c> operation's and a walk's.
    /// </summary>
    /// <param name="edge">An edge of a node's group.</param>
    public bool Doubts(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        return !edge.Recall && (string.Equals(edge.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal) || Unsure(edge.Card.Id));
    }

    /// <summary>Whether <paramref name="reached"/>, which a walk gives, makes its answer not sure, as <see cref="Doubts(GraphEdge)"/> says of an edge.</summary>
    /// <param name="reached">A node a walk reached.</param>
    public bool Doubts(GraphReached reached)
    {
        ArgumentNullException.ThrowIfNull(reached);
        return reached.Candidates || (!reached.Recall && Unsure(reached.Card.Id));
    }

    /// <summary>Whether the file at <paramref name="path"/> is generated code: its entry says so, or holds the mark a later sync keeps it generated by.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public bool Generated(string path)
        => _read.Files.TryGetValue(path, out var entry)
            && (string.Equals(entry.State, CodeFileStates.Generated, StringComparison.Ordinal)
                || (CodeFileStates.Extracted.Contains(entry.State, StringComparer.Ordinal) && entry.Why is not null));

    /// <summary>Whether the file at <paramref name="path"/> holds a place its implementation could not bind: a text search where its nodes are named.</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public bool Dynamic(string path) => (_extracts.TryGetValue(path, out var extract) ? extract : _read.Extract(path)) is { Dynamic.Count: > 0 };

    /// <summary>
    /// Whether the view holds the file at <paramref name="path"/> as one it parsed - the state the narrowing of a whole
    /// read is held to, for a file the graph parsed is one it can serve an outline and bodies of instead.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as an agent or the tool spells it.</param>
    public bool Parsed(string path)
        => HeldPath(path) is { } held && _read.Files.TryGetValue(held, out var entry)
            && string.Equals(entry.State, CodeFileStates.Parsed, StringComparison.Ordinal);

    /// <summary>
    /// Why the graph is not sure of the file at <paramref name="path"/> - a whole read of it is let past the narrowing
    /// as an escape <c>lowConfidence</c> - or null where it is sure: the file carries a <c>dynamic</c> mark, is marked
    /// <c>generated</c>, or more than a quarter of the references its nodes make are edges of <c>candidates</c>.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as an agent or the tool spells it.</param>
    public string? UnsureOfFile(string path)
    {
        if (HeldPath(path) is not { } held)
        {
            return null;
        }

        if (Dynamic(held))
        {
            return "the graph cannot bind part of it";
        }

        if (Generated(held))
        {
            return "the graph holds it as generated code";
        }

        return MostlyCandidates(held) is { } edges
            ? $"more than a quarter of its {edges.ToString(CultureInfo.InvariantCulture)} references the linker left candidates"
            : null;
    }

    /// <summary>
    /// How many references the nodes of the file at <paramref name="held"/> make that say how sure the graph is, where more
    /// than a quarter of them the linker left candidates; null where no more than a quarter are, or it makes none. The one
    /// rule of a file the graph is unsure of for its candidates (<see cref="UnsureOfFile"/>).
    /// </summary>
    private int? MostlyCandidates(string held) => LeftCandidates(held) is { } left && left.Unsure * 4 > left.Edges ? left.Edges : null;

    /// <summary>
    /// The signs the file at <paramref name="path"/> gives of what the implementation of its language does not see
    /// (<see cref="CodeGraphLimit.Signs"/>): the kind of each dynamic mark its reading made, and each sign the linker found its nodes'
    /// references and its imports give (<see cref="CodeGraphSymbolsFile.Signs"/>) - why one counted external named nothing, why one was
    /// left candidates, an import taken as several places - however few, each such is the limit it names; each once, in order. Read
    /// once a file a view, where first asked.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as an agent or the tool spells it.</param>
    public IReadOnlyList<string> SignsOf(string path)
    {
        if (HeldPath(path) is not { } held)
        {
            return [];
        }

        return _signs.GetOrAdd(held, file =>
            [
                .. ((_extracts.TryGetValue(file, out var extract) ? extract : _read.Extract(file))?.Dynamic ?? []).Select(mark => mark.Kind)
                    .Concat((_inFile.TryGetValue(file, out var own) ? own : []).SelectMany(id => _read.Symbols.Signs.GetValueOrDefault(id) ?? []))
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal),
            ]);
    }

    /// <summary>The signs each file gives, by the path the view holds it at, as <see cref="SignsOf"/> first read them.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _signs = new(StringComparer.Ordinal);

    /// <summary>
    /// The kind of the file at <paramref name="path"/>, as a <c>sites</c> answer groups its places by: the language of the
    /// implementation that read it, where one of a language did (<see cref="ReadByALanguage"/>); its kind by its name else
    /// (<see cref="FileKind"/>).
    /// </summary>
    /// <param name="path">A file's path, relative to the tree, as the reader spells it.</param>
    public string KindOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        return ReadByALanguage(path) && _read.Files[HeldPath(path)!].Language is { } language ? language : FileKind(path);
    }

    /// <summary>The kind a file's name gives it: its extension with its dot, or its name where it has none (Makefile, Dockerfile).</summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public static string FileKind(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var name = CodeGraphIds.FileName(path);
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name[dot..] : name;
    }

    /// <summary>
    /// The tier the file at <paramref name="path"/> is of, as the repository's map names its parts: the project nearest
    /// above it - each project file of the nearest directory that holds one, by its path - or, where no project is above it,
    /// the top directory the outline names it in (<see cref="Overview"/>). Read by walking the file's directories up, each
    /// once.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public string TierOf(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        var projects = _projects.Value;

        for (var directory = DirectoryOf(path); ; directory = DirectoryOf(directory))
        {
            if (projects.TryGetValue(directory, out var files))
            {
                return $"project {string.Join(", ", files.Select(ReportText.Printable))}";
            }

            if (directory.Length == 0)
            {
                return $"directory {ReportText.Printable(TopDirectory(path))}";
            }
        }
    }

    /// <summary>The project files the view holds, by the directory each stands in - the tree's root as empty - each directory's in order.</summary>
    private readonly Lazy<IReadOnlyDictionary<string, IReadOnlyList<string>>> _projects;

    /// <summary>The directory <paramref name="path"/> stands in, relative to the tree - empty for the tree's root.</summary>
    private static string DirectoryOf(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : string.Empty;

    /// <summary>The top directory a file is in, as the outline lists the repository's: its first directory with a slash, or <c>.</c> for a file at the tree's root.</summary>
    private static string TopDirectory(string path) => path.IndexOf('/') is var slash and > 0 ? path[..slash] + "/" : ".";

    /// <summary>
    /// The references the nodes of the file at <paramref name="held"/> make that say how sure the graph is of what they name, and
    /// how many of them the linker left candidates: a use is recall and never a doubt, and no edge of the file's structure is one.
    /// None where the file makes none.
    /// </summary>
    private (int Edges, int Unsure)? LeftCandidates(string held)
    {
        var edges = 0;
        var unsure = 0;

        foreach (var id in _inFile.TryGetValue(held, out var own) ? own : [])
        {
            foreach (var edge in _out[id])
            {
                if (string.Equals(edge.Kind, CodeEdgeKinds.Contains, StringComparison.Ordinal)
                    || string.Equals(edge.Kind, CodeEdgeKinds.Declares, StringComparison.Ordinal)
                    || CodeEdgeKinds.Recall(edge.Kind))
                {
                    continue;
                }

                edges++;

                if (string.Equals(edge.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal))
                {
                    unsure++;
                }
            }
        }

        return edges > 0 ? (edges, unsure) : null;
    }

    /// <summary>
    /// What an answer about the file at <paramref name="path"/> says the graph does not hold of it, a line each: the state
    /// it is held in where no implementation read it, with why; where its implementation stopped reading it or could not
    /// bind it, how many places, then each of them by line and why, in the file's order - every one, however many - each one
    /// line, the file's path and each why printable (<see cref="ReportText.Printable"/>). None where the graph holds the
    /// whole of it, or where the view holds no such file.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    public IReadOnlyList<string> Unread(string path)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (!_read.Files.TryGetValue(path, out var entry))
        {
            return [];
        }

        if (!CodeFileStates.Extracted.Contains(entry.State, StringComparer.Ordinal))
        {
            return [entry.Why is { } why ? $"{ReportText.Printable(path)} is {entry.State}: {ReportText.Printable(why)}" : $"{ReportText.Printable(path)} is {entry.State}"];
        }

        var extract = _extracts.TryGetValue(path, out var held) ? held : _read.Extract(path);
        var lines = new List<string>();

        if (string.Equals(entry.State, CodeFileStates.Listed, StringComparison.Ordinal))
        {
            lines.Add($"{ReportText.Printable(path)} is {entry.State}: no implementation of the graph reads its language");
        }

        if (extract?.Dynamic is { Count: > 0 } dynamic)
        {
            if (dynamic.Count > 1)
            {
                lines.Add($"{ReportText.Counted(dynamic.Count, "place")} the graph cannot bind:");
            }

            lines.AddRange(dynamic
                .OrderBy(mark => mark.Line)
                .ThenBy(mark => mark.Why, StringComparer.Ordinal)
                .Select(mark => $"line {mark.Line.ToString(CultureInfo.InvariantCulture)}: {ReportText.Printable(mark.Why)}"));
        }

        return lines;
    }

    /// <summary>
    /// What an answer about the node <paramref name="card"/> says the graph does not hold of it: of a file or a
    /// configuration key, what <see cref="Unread"/> says of its file. None of any other node.
    /// </summary>
    /// <param name="card">A node the view holds.</param>
    public IReadOnlyList<string> UnreadOf(GraphCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        return string.Equals(card.SubKind, CodeSubKinds.Key, StringComparison.Ordinal) || string.Equals(card.Kind, CodeNodeKinds.File, StringComparison.Ordinal)
            ? Unread(card.Path)
            : [];
    }

    /// <summary>
    /// The node of the file at <paramref name="path"/> at <paramref name="line"/>, as a text hit is turned to a card where one
    /// covers its line and a <c>path:line</c> names one - the file's own node, which holds every node the file declares, only
    /// where no other holds the line. At a line nodes begin on, the outermost of them: of those that end last, the one no
    /// other of them is declared inside, whatever their count - a one-line type, not its one member; a positional record, not
    /// its one parameter or several; a one-line enum, not a member; a one-line object, not its one key or several; a method
    /// that begins on a field's line, not the field - where several are, the one of them that makes a reference at the line,
    /// else the nearest node they are all declared inside. At any other line, the innermost node that holds it: of the nodes
    /// whose lines hold it, those of the tightest lines - the ones that begin last, of those the ones that end first - and of
    /// them the innermost, one no other of them is declared inside: a landmark, not the body of a page with no body element
    /// around it; a Vue component that spans its file. Where several are innermost, the one of the tightest the line declares
    /// them all inside; else the one of them that makes a reference at the line - a Razor page's markup, not the class it
    /// compiles to, whose lines are its page's too - and else the nearest node they are all declared inside, the file's own
    /// where they share none. Never one of them by the order of their ids. Read from the file's line index, made once a file a
    /// view (<see cref="LineIndex"/>), and one card made for the answer: a search of a file's every line asks it in time
    /// linear in the file's nodes and its references.
    /// </summary>
    /// <param name="path">A file's path, relative to the tree with forward separators.</param>
    /// <param name="line">A line of that file, from 1.</param>
    public GraphCard? AtLine(string path, int line)
    {
        ArgumentNullException.ThrowIfNull(path);

        if (_lines.GetOrAdd(path, LineIndexOf).At(line) is { } id)
        {
            return Card(id, path);
        }

        // Where no node the file declares holds the line, its own node does, as far as its lines go.
        return Part(CodeGraphIds.FileId(path), path) is { } file && file.Start <= line && line <= file.End ? file : null;
    }

    /// <summary>Each file's line index, by the path asked, as <see cref="AtLine(string, int)"/> first made it.</summary>
    private readonly ConcurrentDictionary<string, LineIndex> _lines = new(StringComparer.Ordinal);

    /// <summary>
    /// The line index of the file at <paramref name="path"/>: each node its extract declares there, by the lines the card
    /// of it in that file spans (<see cref="Card(string, string?)"/>: its first declaration there, <see cref="DeclaredIn"/>)
    /// and the node it is declared inside, and the nodes each line's references are made from - a probe, which asks whether a
    /// declaration is there, making none. Each node's declaration is looked up once, so a file's index costs its own nodes,
    /// never the other files a node it shares is declared in.
    /// </summary>
    private LineIndex LineIndexOf(string path)
    {
        var declared = (_inFile.TryGetValue(path, out var ids) ? ids : [])
            .Select(id => (Id: id, At: DeclaredIn(id, path)))
            .Where(one => one.At is not null)
            .Select(one => new LineIndex.Spanned(one.Id, one.At!.Node.Start, one.At.Node.End, one.At.Node.Parent?.Whole()))
            .ToList();
        var references = (_extracts.TryGetValue(path, out var extract) ? extract?.References : null) ?? [];

        return new LineIndex(declared, references.Where(reference => !reference.Probe).Select(reference => (reference.Line, reference.From.Whole())));
    }

    /// <summary>
    /// The node at each line of one file, as <see cref="AtLine(string, int)"/> answers it. At a line some of the file's nodes
    /// begin on, the outermost of them - of those that end last, the ones no other of them is declared inside, and where
    /// several are, as the tightest are weighed below - weighed once a line, where the nodes are read in the order they begin.
    /// At any other line, the innermost node that holds it, made once by one sweep of the file's nodes in the order they
    /// begin, with an explicit stack of the nodes open at a line: a node is pushed on the line it begins on and passed over
    /// once a line past its end is reached, so the top of the stack is a node of the tightest lines, and the nodes of exactly
    /// its lines lie beneath it - the tightest at every line from one change of the top to the next. Each such set is weighed
    /// once, where it is first on top; a line asked reads its set by a search of the changes. Linear in the nodes and the
    /// references, besides the sort; no walk visits a node twice.
    /// </summary>
    private sealed class LineIndex
    {
        private readonly List<(int From, Tightest? Tightest)> _changes = [];
        private readonly Dictionary<int, string?> _beginning = [];
        private readonly Dictionary<string, string?> _parents = new(StringComparer.Ordinal);
        private readonly ILookup<int, string> _references;

        /// <summary>One node the file declares: its id, the lines it spans, and the node it is declared inside, where one is.</summary>
        /// <param name="Id">The node's id.</param>
        /// <param name="Start">Its first line.</param>
        /// <param name="End">Its last line.</param>
        /// <param name="Parent">The node it is declared inside, by id; null for one declared at the file's top.</param>
        public sealed record Spanned(string Id, int Start, int End, string? Parent);

        /// <param name="nodes">The file's nodes, each once.</param>
        /// <param name="references">Each reference's line and the node it is made from.</param>
        public LineIndex(IReadOnlyList<Spanned> nodes, IEnumerable<(int Line, string From)> references)
        {
            foreach (var node in nodes)
            {
                _parents.TryAdd(node.Id, node.Parent);
            }

            _references = references.ToLookup(reference => reference.Line, reference => reference.From);

            // Every line the open nodes can change at: where one begins, and the line after one ends.
            var sorted = nodes.Where(node => node.Start <= node.End)
                .OrderBy(node => node.Start)
                .ThenByDescending(node => node.End)
                .ThenBy(node => node.Id, StringComparer.Ordinal)
                .ToList();

            // Each line nodes begin on, weighed once: the nodes that begin there lie together, those that end last first.
            for (var first = 0; first < sorted.Count;)
            {
                var widest = new List<string>();
                var after = first;

                for (; after < sorted.Count && sorted[after].Start == sorted[first].Start; after++)
                {
                    if (sorted[after].End == sorted[first].End)
                    {
                        widest.Add(sorted[after].Id);
                    }
                }

                _beginning[sorted[first].Start] = Outermost(widest, sorted[first].Start);
                first = after;
            }

            var lines = sorted.Select(node => node.Start).Concat(sorted.Select(node => node.End + 1)).Distinct().Order().ToList();
            var open = new List<int>();
            var weighed = new Dictionary<int, Tightest>();
            var next = 0;

            foreach (var line in lines)
            {
                while (next < sorted.Count && sorted[next].Start <= line)
                {
                    open.Add(next++);
                }

                // A node past its end leaves once it is on top: one beneath an open node that began later cannot be the tightest.
                while (open.Count > 0 && sorted[open[^1]].End < line)
                {
                    open.RemoveAt(open.Count - 1);
                }

                Tightest? tightest = null;

                if (open.Count > 0 && !weighed.TryGetValue(open[^1], out tightest))
                {
                    // The nodes of exactly the top's lines lie right beneath it: they began on its line, pushed before it, and leave with it.
                    var top = sorted[open[^1]];
                    var members = new List<string>();

                    for (var at = open.Count - 1; at >= 0 && sorted[open[at]].Start == top.Start && sorted[open[at]].End == top.End; at--)
                    {
                        members.Add(sorted[open[at]].Id);
                    }

                    weighed[open[^1]] = tightest = new Tightest(this, members);
                }

                if (_changes.Count == 0 || !ReferenceEquals(_changes[^1].Tightest, tightest))
                {
                    _changes.Add((line, tightest));
                }
            }
        }

        /// <summary>The node at <paramref name="line"/>, by id, as <see cref="AtLine(string, int)"/> says it; null where no node of the file holds the line.</summary>
        public string? At(int line)
        {
            // A line nodes begin on is the outermost of them: a one-line type of its one member, the record of its one
            // parameter, the object of its one key.
            if (_beginning.TryGetValue(line, out var begun) && begun is not null)
            {
                return begun;
            }

            // The last change at or before the line.
            var low = 0;
            var high = _changes.Count - 1;
            var found = -1;

            while (low <= high)
            {
                var middle = low + ((high - low) / 2);

                if (_changes[middle].From <= line)
                {
                    found = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (found < 0 || _changes[found].Tightest is not { } tightest)
            {
                return null;
            }

            if (tightest.Innermost.Count == 1)
            {
                return tightest.Innermost[0];
            }

            if (tightest.Declaring is { } declaring)
            {
                return declaring;
            }

            // Of several innermost no node of the tightest holds, the one that makes a reference at the line.
            var making = _references[line].Where(tightest.Holds).Distinct(StringComparer.Ordinal).ToList();

            return making.Count == 1 ? making[0] : making.Count > 1 ? Around(making, _ => true) : tightest.Around;
        }

        /// <summary>
        /// The node at a line some nodes begin on, of <paramref name="widest"/>, those that begin there and end last: the
        /// one no other of them is declared inside; where several are, the one of them that makes a reference at the line,
        /// else the nearest node those that do - or, where none does, all of them - are declared inside; null where they share
        /// none, and the line is read as any other.
        /// </summary>
        private string? Outermost(IReadOnlyList<string> widest, int line)
        {
            var of = widest.ToHashSet(StringComparer.Ordinal);
            List<string> outermost = [.. widest.Where(id => _parents.GetValueOrDefault(id) is not { } parent || !of.Contains(parent)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];

            if (outermost.Count == 1)
            {
                return outermost[0];
            }

            var outer = outermost.ToHashSet(StringComparer.Ordinal);
            var making = _references[line].Where(outer.Contains).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

            return making.Count == 1 ? making[0] : Around(making.Count > 1 ? making : outermost, _ => true);
        }

        /// <summary>
        /// The nearest node every one of <paramref name="ids"/> is declared inside, walking their parents while
        /// <paramref name="within"/> takes each: the first one's parents in order, each other's until it meets one of those -
        /// each walk knowing what it passed, so a parent named twice ends it; null where they meet at none.
        /// </summary>
        private string? Around(IReadOnlyList<string> ids, Func<string, bool> within)
        {
            var chain = new Dictionary<string, int>(StringComparer.Ordinal);
            var order = new List<string>();

            for (var at = _parents.GetValueOrDefault(ids[0]); at is not null && within(at) && chain.TryAdd(at, order.Count); at = _parents.GetValueOrDefault(at))
            {
                order.Add(at);
            }

            var met = 0;

            foreach (var id in ids.Skip(1))
            {
                var passed = new HashSet<string>(StringComparer.Ordinal);
                var at = _parents.GetValueOrDefault(id);

                while (at is not null && within(at) && !chain.ContainsKey(at) && passed.Add(at))
                {
                    at = _parents.GetValueOrDefault(at);
                }

                if (at is null || !chain.TryGetValue(at, out var index))
                {
                    return null;
                }

                met = Math.Max(met, index);
            }

            return order.Count == 0 ? null : order[met];
        }

        /// <summary>
        /// The nodes of one file's tightest lines at some line, weighed once: those no other of them is declared inside, the
        /// one of them every such is declared inside where there are several, and the nearest node they are all declared
        /// inside beyond them.
        /// </summary>
        private sealed class Tightest
        {
            private readonly HashSet<string> _members;
            private readonly HashSet<string> _innermost;
            private readonly Lazy<string?> _around;

            public Tightest(LineIndex index, IReadOnlyList<string> members)
            {
                _members = members.ToHashSet(StringComparer.Ordinal);

                // A node declared inside another of the same lines has its parent among them: every node between holds them too.
                var outer = members.Select(member => index._parents.GetValueOrDefault(member)).OfType<string>().Where(_members.Contains).ToHashSet(StringComparer.Ordinal);

                Innermost = [.. members.Where(member => !outer.Contains(member)).Order(StringComparer.Ordinal)];
                _innermost = Innermost.ToHashSet(StringComparer.Ordinal);
                Declaring = Innermost.Count > 1 ? index.Around(Innermost, _members.Contains) : null;
                _around = new(() => index.Around(Innermost, _ => true));
            }

            /// <summary>Those no other of them is declared inside, by id, in order.</summary>
            public IReadOnlyList<string> Innermost { get; }

            /// <summary>Where several are innermost, the one of the tightest they are all declared inside, nearest them; null else.</summary>
            public string? Declaring { get; }

            /// <summary>The nearest node every innermost one is declared inside, of any lines; null where they share none.</summary>
            public string? Around => _around.Value;

            /// <summary>Whether <paramref name="id"/> is one of those no other of them is declared inside.</summary>
            public bool Holds(string id) => _innermost.Contains(id);
        }
    }

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

    private GraphCard? AtLine(string symbol)
    {
        var colon = symbol.LastIndexOf(':');

        if (colon <= 0 || !int.TryParse(symbol.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var line))
        {
            return null;
        }

        return AtLine(HeldPath(symbol[..colon]) ?? symbol[..colon], line);
    }

    /// <summary>The kind within a kind of the node <paramref name="id"/>, where the view holds it.</summary>
    private string? SubKindOf(string id) => _nodes.TryGetValue(id, out var at) && at.Count > 0 ? at[0].Node.SubKind : null;

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

        // A node's summary and its path count far less than its name: a word there is a twentieth of one in the name.
        var matched = NameWeight(card, terms) + ((Hits(card.Summary, terms) + Hits(card.Path, terms)) * 0.05);

        return matched / terms.Whole * 100;
    }

    /// <summary>
    /// How much of <paramref name="terms"/>' weight <paramref name="card"/>'s name holds: each of the query's words the
    /// name holds whole, as often as the query says it, weighed by how rare it is - summed in one order, so names that
    /// hold the same words weigh alike.
    /// </summary>
    private double NameWeight(GraphCard card, Terms terms)
        => _words.GetValueOrDefault(card.Id, []).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Sum(word => terms.Weight.GetValueOrDefault(word));

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

    /// <summary>
    /// <paramref name="symbol"/> with its language's own separators - C++'s and PHP's <c>::</c>, PHP's <c>\</c> - written as
    /// the model's <c>.</c>, as every qualified name is held, a leading separator dropped where the name begins with one
    /// (<c>::Foo</c>, <c>\Foo\Bar</c>); a dot the name itself begins with - a rule's <c>.header</c> - is the name's own, and kept.
    /// </summary>
    internal static string Dotted(string symbol)
    {
        var dotted = symbol.Replace("::", ".", StringComparison.Ordinal).Replace('\\', '.');
        return symbol.StartsWith("::", StringComparison.Ordinal) || symbol.StartsWith('\\') ? dotted.TrimStart('.') : dotted;
    }

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

/// <summary>
/// What a <c>sites</c> answer looks for its data by, in the literals and in the text it searches alike, and the one owner of
/// whether a value or a line holds it (<see cref="Holds"/>): the word code and data write it by
/// (<see cref="CodebaseGraphQueries.SitesSearch"/>, or a quoted literal's value) as a whole word
/// (<see cref="GraphWord.Whole"/>); where the word is a class's or an id's selector as a rule writes it
/// (<c>.btn</c>, <c>#main</c>), the selector with no name character of a class after it - a hyphen among them, so
/// <c>.btn</c> is not held by <c>.btn-primary</c> - and its bare name as a whole word of a class list - beside the start or
/// the end, white space or a quote mark - as markup's class and id attributes and code's class names (<c>addClass('btn')</c>)
/// write it; and, of any other word, each key path it names as an environment variable writes one, whole by the same rule.
/// No pattern is compiled of any of them, so none is refused for its length and none grows with how many key paths the data
/// names: the word is looked for by its characters (<see cref="GraphWord"/>), and every key path in one pass of the text for
/// all of them at once, so no key path is left out.
/// </summary>
public sealed class GraphSitesSearch
{
    /// <summary>What every key path an environment variable writes holds: the double underscore it parts its places by (<see cref="CodeKeyPaths.Environment"/>).</summary>
    private const string Separator = "__";

    /// <summary>The forms the word is held in: the word whole; or, of a selector, the selector as a rule writes it and its name as a class list does.</summary>
    private readonly IReadOnlyList<GraphWord> _words;

    private readonly HashSet<string> _spellings;
    private readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> _spelt;
    private readonly SearchValues<string>? _starts;
    private readonly int[] _lengths;

    private GraphSitesSearch(string word, string? selector, HashSet<string> spellings)
    {
        Word = word;
        Selector = selector;
        _words = selector is not null
            ? [new GraphWord(word, before: null, after: character => !CodebaseGraphQueries.WordCharacter(character) && character != '-'), new GraphWord(selector, ClassListEdge, ClassListEdge)]
            : [GraphWord.Whole(word)];
        _spellings = spellings;
        _spelt = spellings.GetAlternateLookup<ReadOnlySpan<char>>();
        _starts = spellings.Count == 0 ? null : SearchValues.Create([.. spellings], StringComparison.Ordinal);
        _lengths = [.. spellings.Select(spelt => spelt.Length).Distinct().Order()];
    }

    /// <summary>
    /// What a sites answer looks for <paramref name="word"/> by, and, where it is no selector, each of <paramref name="spellings"/>
    /// - the key paths it names as an environment variable writes them - but the word itself, each once.
    /// </summary>
    /// <param name="word">The data's word: the name code and data write it by, or a quoted literal's value.</param>
    /// <param name="spellings">The key paths it names, each as an environment variable writes it; none where it is left out.</param>
    /// <exception cref="ArgumentException">A spelling holds no double underscore, so it is no key path an environment variable writes.</exception>
    public static GraphSitesSearch Of(string word, IEnumerable<string>? spellings = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(word);

        var selector = CodebaseGraphQueries.SelectorName(word);
        var spelt = new HashSet<string>(StringComparer.Ordinal);

        foreach (var spelling in selector is null ? spellings ?? [] : [])
        {
            if (!spelling.Contains(Separator, StringComparison.Ordinal))
            {
                throw new ArgumentException($"'{spelling}' is no key path as an environment variable writes one: it holds no '{Separator}'.", nameof(spellings));
            }

            if (spelling != word)
            {
                spelt.Add(spelling);
            }
        }

        return new GraphSitesSearch(word, selector, spelt);
    }

    /// <summary>The word the data is looked for by.</summary>
    public string Word { get; }

    /// <summary>Whether <paramref name="character"/>, beside a selector's bare name, parts it from the rest of a class list: white space or a quote mark.</summary>
    private static bool ClassListEdge(char character) => char.IsWhiteSpace(character) || character is '"' or '\'' or '`';

    /// <summary>The bare name of the selector <see cref="Word"/> is written as (<see cref="CodebaseGraphQueries.SelectorName"/>), searched as a class list writes it too; null where it is none.</summary>
    public string? Selector { get; }

    /// <summary>The key paths the data names, each as an environment variable writes it, that a value or a line is asked whether it holds whole beside the word.</summary>
    public IReadOnlyCollection<string> Spellings => _spellings;

    /// <summary>
    /// Whether <paramref name="text"/> - a literal's value, a line - holds the data: its word as the word is looked for, or one
    /// of its key paths whole - each edge of it that is a word character beside the text's start or end or a character that
    /// is none, as <see cref="GraphWord.Whole"/> reads a word - found by one pass over the text for every key
    /// path at once, each place one begins at asked once of each length a key path has.
    /// </summary>
    /// <param name="text">A literal's value, or a line.</param>
    public bool Holds(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var form in _words)
        {
            if (form.In(text))
            {
                return true;
            }
        }

        if (_starts is null)
        {
            return false;
        }

        var span = text.AsSpan();

        for (var from = 0; from < span.Length;)
        {
            var found = span[from..].IndexOfAny(_starts);

            if (found < 0)
            {
                return false;
            }

            var at = from + found;

            // A key path that begins with a word character begins a word; one that ends with one ends a word.
            if (at == 0 || !CodebaseGraphQueries.WordCharacter(span[at]) || !CodebaseGraphQueries.WordCharacter(span[at - 1]))
            {
                foreach (var length in _lengths)
                {
                    var end = at + length;

                    if (end > span.Length)
                    {
                        break;
                    }

                    if ((end == span.Length || !CodebaseGraphQueries.WordCharacter(span[end - 1]) || !CodebaseGraphQueries.WordCharacter(span[end]))
                        && _spelt.Contains(span[at..end]))
                    {
                        return true;
                    }
                }
            }

            from = at + 1;
        }

        return false;
    }
}
