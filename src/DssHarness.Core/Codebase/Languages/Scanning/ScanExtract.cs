using System.Globalization;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// One file's extract as a scanned language's reader gathers it: nodes added by a key the reader holds while it walks,
/// each given its id once the file is read - every declaration with a body before every one without, so a prototype of
/// a definition in the same file never takes the definition's id; parts of one namespace in a file one node.
/// </summary>
internal sealed class ScanExtract
{
    private const char KeyMark = '\u0001';

    private readonly List<ExtractNode> _nodes = [];
    private readonly List<QualifiedName> _wanted = [];
    private readonly Dictionary<(QualifiedName Wanted, string Kind, QualifiedName? Parent), QualifiedName> _parts = [];
    private readonly Dictionary<QualifiedName, ExtractScope> _scopes = [];
    private readonly List<ExtractImport> _imports = [];
    private readonly List<ExtractReference> _references = [];
    private readonly List<ExtractDynamic> _dynamic = [];
    private readonly ExtractLiterals _literals = new();

    /// <summary>The key of the node of what the file's top does outside its declarations, where the reader made one.</summary>
    private QualifiedName? _top;

    /// <summary>Why the file is generated code, where the reader knows; <see langword="null"/> where it is not.</summary>
    public string? Generated { get; set; }

    /// <summary>The names an import of the file that takes what it publishes makes visible (<see cref="FileExtract.Published"/>); <see langword="null"/> where its language has no such import.</summary>
    public IReadOnlyList<string>? Published { get; set; }

    /// <summary>Whether a name the file's code reads that none of its scopes declares and none of its imports makes visible is bound by nothing (<see cref="FileExtract.BoundWithin"/>).</summary>
    public bool BoundWithin { get; set; }

    /// <summary>Whether the file is the one that says its directory is one unit of its language's layout (<see cref="FileExtract.FolderUnit"/>).</summary>
    public bool FolderUnit { get; set; }

    /// <summary>Whether what the file publishes holds what its own imports that take a file's published names take (<see cref="FileExtract.PublishesTaken"/>).</summary>
    public bool PublishesTaken { get; set; }

    /// <summary>
    /// Whether the references added now are made around their node rather than in its body (<see cref="ExtractReference.Around"/>):
    /// set while a reader reads a declaration's header - a Python function's decorators, annotations and defaults, a class's decorators,
    /// type parameters' bounds and bases - and unset after.
    /// </summary>
    public bool Around { get; set; }

    /// <summary>
    /// One string literal of the file, decoded, on <paramref name="line"/>, written in the code of the node of the key
    /// <paramref name="from"/> - none where it is written at the file's top, outside every declaration, whose node it is
    /// once the reader makes one (<see cref="Top"/>) - kept by the one rule every reader keeps them by
    /// (<see cref="ExtractLiterals"/>).
    /// </summary>
    public void Literal(string value, int line, QualifiedName? from) => _literals.Add(value, line, from);

    /// <summary>
    /// Says the node of the key <paramref name="key"/> is what the file's top does outside its declarations - a module's,
    /// a script's - the node of every literal written there, made the first time a reference needs it.
    /// </summary>
    public void Top(QualifiedName key) => _top = key;

    /// <summary>
    /// Adds <paramref name="node"/>, whose <see cref="ExtractNode.Id"/> is the id it would take and whose
    /// <see cref="ExtractNode.Parent"/> is the key of the node that holds it; answers its key.
    /// </summary>
    public QualifiedName Add(ExtractNode node)
    {
        var key = QualifiedName.Of(string.Create(CultureInfo.InvariantCulture, $"{KeyMark}{_nodes.Count}"));

        _wanted.Add(node.Id);
        _nodes.Add(node with { Id = key });
        return key;
    }

    /// <summary>
    /// <paramref name="node"/> as a part of the node of its id, kind and holder the file already declares, which then runs
    /// from its first line to <paramref name="node"/>'s last; added where there is none. Answers its key.
    /// </summary>
    public QualifiedName Part(ExtractNode node)
    {
        if (_parts.TryGetValue((node.Id, node.Kind, node.Parent), out var key))
        {
            var at = IndexOf(key);
            _nodes[at] = _nodes[at] with { End = Math.Max(_nodes[at].End, node.End) };
            return key;
        }

        return _parts[(node.Id, node.Kind, node.Parent)] = Add(node);
    }

    /// <summary>The node of <paramref name="key"/>, as added.</summary>
    public ExtractNode Node(QualifiedName key) => _nodes[IndexOf(key)];

    /// <summary>
    /// Changes the node of <paramref name="key"/> to what <paramref name="change"/> makes of it - what the file says of a
    /// declaration after it, as a JavaScript module's <c>export { a }</c> does - its key and the id it would take kept.
    /// </summary>
    public void Change(QualifiedName key, Func<ExtractNode, ExtractNode> change)
    {
        var at = IndexOf(key);
        _nodes[at] = change(_nodes[at]) with { Id = key, Parent = _nodes[at].Parent };
    }

    /// <summary>Declares the scope <paramref name="name"/>, once: its first declaration says whether it is open and what holds it.</summary>
    public void Scope(QualifiedName name, bool open, QualifiedName? parent) => _scopes.TryAdd(name, new ExtractScope(name, open) { Parent = parent });

    /// <summary>Adds an import, in the order the file makes it.</summary>
    public void Import(ExtractImport import) => _imports.Add(import);

    /// <summary>
    /// Adds a reference made from the node of the key <paramref name="from"/>; answers its place among the file's
    /// references - what a reference made for the next name of a chain links its qualifier to
    /// (<see cref="ExtractQualifier.Link"/>) - or -1 where it names nothing and is not added. A <paramref name="probe"/> asks
    /// whether a declaration is there (<see cref="ExtractReference.Probe"/>): where none is, no doubt is counted. One added while
    /// <see cref="Around"/> is set is made around its node (<see cref="ExtractReference.Around"/>).
    /// </summary>
    public int Reference(QualifiedName from, string kind, string name, int line, int? arity = null, ExtractQualifier? qualifier = null, bool probe = false)
    {
        if (name.Length == 0)
        {
            return -1;
        }

        _references.Add(new ExtractReference(from, kind, name, line) { Arity = arity, Qualifier = qualifier, Probe = probe, Around = Around });
        return _references.Count - 1;
    }

    /// <summary>The reference at <paramref name="at"/>, as added (<see cref="Reference"/>).</summary>
    public ExtractReference ReferenceAt(int at) => _references[at];

    /// <summary>
    /// Marks <paramref name="line"/> dynamic, with why, of the <paramref name="kind"/> its implementation's help tells the limit that
    /// says it by (<see cref="CodeGraphLimit.Signs"/>), so an answer says of the file that limit in full.
    /// </summary>
    public void Dynamic(int line, string why, string kind) => _dynamic.Add(new ExtractDynamic(line, why, kind));

    /// <summary>The extract, every node given its id: those with a body first, in the order they were added, then those without.</summary>
    public FileExtract Build()
    {
        var ids = new ExtractIds();
        var given = new Dictionary<QualifiedName, QualifiedName>();

        foreach (var prototypes in (bool[])[false, true])
        {
            for (var at = 0; at < _nodes.Count; at++)
            {
                if (_nodes[at].Prototype == prototypes)
                {
                    given[_nodes[at].Id] = ids.Next(_wanted[at]);
                }
            }
        }

        return new FileExtract(_nodes)
        {
            Scopes = [.. _scopes.Values],
            Imports = _imports,
            References = _references,
            Generated = Generated,
            Dynamic = _dynamic,
            Literals = _literals.Kept(from => from ?? _top),
            Published = Published,
            BoundWithin = BoundWithin,
            FolderUnit = FolderUnit,
            PublishesTaken = PublishesTaken,
        }.Renamed(key => given[key]);
    }

    private static int IndexOf(QualifiedName key) => int.Parse(key.Whole().AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
}
