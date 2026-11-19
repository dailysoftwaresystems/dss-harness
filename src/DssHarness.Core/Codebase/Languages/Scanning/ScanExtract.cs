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
    private readonly List<string> _wanted = [];
    private readonly Dictionary<(string Wanted, string Kind, string? Parent), string> _parts = [];
    private readonly Dictionary<string, ExtractScope> _scopes = new(StringComparer.Ordinal);
    private readonly List<ExtractImport> _imports = [];
    private readonly List<ExtractReference> _references = [];
    private readonly List<ExtractDynamic> _dynamic = [];
    private readonly ExtractLiterals _literals = new();

    /// <summary>The key of the node of what the file's top does outside its declarations, where the reader made one.</summary>
    private string? _top;

    /// <summary>Why the file is generated code, where the reader knows; <see langword="null"/> where it is not.</summary>
    public string? Generated { get; set; }

    /// <summary>
    /// One string literal of the file, decoded, on <paramref name="line"/>, written in the code of the node of the key
    /// <paramref name="from"/> - none where it is written at the file's top, outside every declaration, whose node it is
    /// once the reader makes one (<see cref="Top"/>) - kept by the one rule every reader keeps them by
    /// (<see cref="ExtractLiterals"/>).
    /// </summary>
    public void Literal(string value, int line, string? from) => _literals.Add(value, line, from);

    /// <summary>
    /// Says the node of the key <paramref name="key"/> is what the file's top does outside its declarations - a module's,
    /// a script's - the node of every literal written there, made the first time a reference needs it.
    /// </summary>
    public void Top(string key) => _top = key;

    /// <summary>
    /// Adds <paramref name="node"/>, whose <see cref="ExtractNode.Id"/> is the id it would take and whose
    /// <see cref="ExtractNode.Parent"/> is the key of the node that holds it; answers its key.
    /// </summary>
    public string Add(ExtractNode node)
    {
        var key = string.Create(CultureInfo.InvariantCulture, $"{KeyMark}{_nodes.Count}");

        _wanted.Add(node.Id);
        _nodes.Add(node with { Id = key });
        return key;
    }

    /// <summary>
    /// <paramref name="node"/> as a part of the node of its id, kind and holder the file already declares, which then runs
    /// from its first line to <paramref name="node"/>'s last; added where there is none. Answers its key.
    /// </summary>
    public string Part(ExtractNode node)
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
    public ExtractNode Node(string key) => _nodes[IndexOf(key)];

    /// <summary>
    /// Changes the node of <paramref name="key"/> to what <paramref name="change"/> makes of it - what the file says of a
    /// declaration after it, as a JavaScript module's <c>export { a }</c> does - its key and the id it would take kept.
    /// </summary>
    public void Change(string key, Func<ExtractNode, ExtractNode> change)
    {
        var at = IndexOf(key);
        _nodes[at] = change(_nodes[at]) with { Id = key, Parent = _nodes[at].Parent };
    }

    /// <summary>Declares the scope <paramref name="name"/>, once: its first declaration says whether it is open and what holds it.</summary>
    public void Scope(string name, bool open, string? parent) => _scopes.TryAdd(name, new ExtractScope(name, open) { Parent = parent });

    /// <summary>Adds an import, in the order the file makes it.</summary>
    public void Import(ExtractImport import) => _imports.Add(import);

    /// <summary>Adds a reference made from the node of the key <paramref name="from"/>.</summary>
    public void Reference(string from, string kind, string name, int line, int? arity = null, ExtractQualifier? qualifier = null)
    {
        if (name.Length > 0)
        {
            _references.Add(new ExtractReference(from, kind, name, line) { Arity = arity, Qualifier = qualifier });
        }
    }

    /// <summary>Marks <paramref name="line"/> dynamic, with why.</summary>
    public void Dynamic(int line, string why) => _dynamic.Add(new ExtractDynamic(line, why));

    /// <summary>The extract, every node given its id: those with a body first, in the order they were added, then those without.</summary>
    public FileExtract Build()
    {
        var ids = new ExtractIds();
        var given = new Dictionary<string, string>(StringComparer.Ordinal);

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

        return new FileExtract([.. _nodes.Select(node => node with { Id = given[node.Id], Parent = node.Parent is { } parent ? given[parent] : null, SuffixOf = node.SuffixOf?.Select(key => given[key]).ToList() })])
        {
            Scopes = [.. _scopes.Values],
            Imports = _imports,
            References = [.. _references.Select(reference => reference with { From = given[reference.From] })],
            Generated = Generated,
            Dynamic = _dynamic,
            Literals = _literals.Kept(from => from is not null ? given[from] : _top is { } top ? given[top] : null),
        };
    }

    private static int IndexOf(string key) => int.Parse(key.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture);
}
