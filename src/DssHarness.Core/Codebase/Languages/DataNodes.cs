namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The nodes of a data file - JSON's, YAML's, XML's - as one owner makes them: a configuration key a <c>member</c>
/// SubKind <see cref="CodeSubKinds.Key"/> named by its path with its value as its summary, a container a <c>section</c>
/// over its lines inside the section that holds it, an XML element a <c>member</c> SubKind
/// <see cref="CodeSubKinds.Element"/>, and what the file repeats by reference - a YAML alias, a merge key - a reference to
/// the node it repeats, read once where it is written (<see cref="CodeQualifierKinds.Own"/>). Every key, container and
/// element the file holds is a node, however many and however deep, a walk of them followed on a stack of its own; every
/// summary and signature on one line and whole (<see cref="ExtractText"/>), so no data file's extract is one the core
/// refuses.
/// </summary>
/// <param name="language">The implementation's name, which prefixes every id.</param>
/// <param name="file">The file's path, relative to the tree with forward separators.</param>
/// <param name="lineCount">The file's lines (<see cref="CodeFileText.Lines"/>), which every line named is held within.</param>
internal sealed class DataNodes(string language, string file, int lineCount)
{
    private readonly ExtractIds _ids = new();
    private readonly List<ExtractNode> _nodes = [];
    private readonly Dictionary<string, int> _at = new(StringComparer.Ordinal);
    private readonly List<ExtractDynamic> _dynamic = [];
    private readonly List<ExtractReference> _references = [];

    /// <summary>The nodes made, in the order they were made.</summary>
    public IReadOnlyList<ExtractNode> Nodes => _nodes;

    /// <summary>The places a walk stopped reading, each with why.</summary>
    public IReadOnlyList<ExtractDynamic> Dynamic => _dynamic;

    /// <summary>The references the file makes to nodes of its own: what it repeats by reference, each to the node it repeats.</summary>
    public IReadOnlyList<ExtractReference> References => _references;

    /// <summary>Marks <paramref name="line"/> as a place reading stopped at, with why.</summary>
    /// <param name="line">The line, from 1, held within the file's.</param>
    /// <param name="why">Why reading stopped there.</param>
    public void Stopped(int line, string why) => _dynamic.Add(new ExtractDynamic(FileExtract.LineWithin(line, lineCount), why));

    /// <summary>The key at <paramref name="path"/> on <paramref name="line"/>, its value <paramref name="value"/>, inside <paramref name="parent"/>: its id.</summary>
    public string Key(IReadOnlyList<DataPlace> path, int line, string? value, string? parent)
        => Key(path[^1].Written, PathOf(path), line, value, parent);

    /// <summary>The key <paramref name="name"/> - its whole path <paramref name="qualified"/> - on <paramref name="line"/>: its id.</summary>
    public string Key(string name, string qualified, int line, string? value, string? parent)
    {
        var at = FileExtract.LineWithin(line, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(qualified)}"), CodeNodeKinds.Member, ExtractText.Whole(name), ExtractText.Whole(qualified), at, at, ExtractText.Signature(ExtractText.Flat(qualified)))
        {
            SubKind = CodeSubKinds.Key,
            Parent = parent,
            Summary = value is null ? null : SummaryOf(value),
        });
    }

    /// <summary>The element <paramref name="name"/> - its whole path <paramref name="qualified"/> - beginning on <paramref name="line"/>, inside <paramref name="parent"/>: its id.</summary>
    public string Element(string name, string qualified, int line, string? parent)
    {
        var at = FileExtract.LineWithin(line, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(qualified)}"), CodeNodeKinds.Member, ExtractText.Whole(name), ExtractText.Whole(qualified), at, at, ExtractText.Signature(ExtractText.Flat(qualified)))
        {
            SubKind = CodeSubKinds.Element,
            Parent = parent,
        });
    }

    /// <summary>The section <paramref name="name"/> at <paramref name="path"/>, over the lines <paramref name="start"/> to <paramref name="end"/>, inside <paramref name="parent"/>: its id.</summary>
    public string Section(string name, string path, int start, int end, string? parent)
    {
        var from = FileExtract.LineWithin(start, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(path)}"), CodeNodeKinds.Section, ExtractText.Whole(name), $"{file}#{ExtractText.Whole(path)}", from, Math.Max(from, FileExtract.LineWithin(end, lineCount)), ExtractText.Signature(ExtractText.Flat(path))) { Parent = parent });
    }

    /// <summary><paramref name="node"/>, a node of the file's own kind - a XAML view's type, a member of it - its id given as the file's other ids are: its id.</summary>
    public string Node(ExtractNode node)
        => Add(node with { Id = _ids.Next(node.Id), Signature = ExtractText.Signature(ExtractText.Flat(node.Signature)) });

    /// <summary>Sets the last line of the node <paramref name="id"/> made: its lines end there, never before they begin.</summary>
    public void Ends(string id, int end)
    {
        var at = _at[id];
        _nodes[at] = _nodes[at] with { End = Math.Max(_nodes[at].Start, FileExtract.LineWithin(end, lineCount)) };
    }

    /// <summary>Names the node <paramref name="id"/> made <paramref name="name"/>: a workflow's step by the name it gives itself.</summary>
    public void Rename(string id, string name)
    {
        var at = _at[id];
        _nodes[at] = _nodes[at] with { Name = ExtractText.Whole(name) };
    }

    /// <summary>
    /// Sets the summary of the node <paramref name="id"/> made from <paramref name="value"/>, where it has none: the text an
    /// element holds, first, on one line and whole.
    /// </summary>
    public void Summarize(string id, string value)
    {
        var at = _at[id];

        if (_nodes[at].Summary is null && SummaryOf(value) is { } summary)
        {
            _nodes[at] = _nodes[at] with { Summary = summary };
        }
    }

    /// <summary>
    /// A reference of the node <paramref name="from"/> to the node <paramref name="to"/> of this file - what it repeats by
    /// reference, by the name it is repeated by, on <paramref name="line"/>.
    /// </summary>
    public void Refers(string from, string to, string name, int line)
        => _references.Add(new ExtractReference(from, CodeEdgeKinds.Uses, ExtractText.Whole(name), FileExtract.LineWithin(line, lineCount))
        {
            Qualifier = new ExtractQualifier(CodeQualifierKinds.Own, to),
        });

    /// <summary>
    /// Walks <paramref name="root"/>, a data file's value: each scalar key a key node named by its path, each container a
    /// section over its lines inside the one that holds it, each item of a list placed by its place, an alias a key whose
    /// value is the alias and a reference to the node it names, a container that merges others a reference to each - every
    /// one of them, however many and however deep, on a stack of the walk's own; the references made once the walk has
    /// placed every node, so each names the node it repeats whatever the order the file writes them in.
    /// </summary>
    /// <param name="root">The file's value.</param>
    /// <returns>The nodes made of what the file holds: the sections at its top by the name of the member each is, and each value's node by the value.</returns>
    public DataWalk Walk(IDataValue root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        var made = new Dictionary<object, string>(ReferenceEqualityComparer.Instance);
        var refers = new List<(string From, object To, string Name, int Line)>();
        var path = new List<DataPlace>();
        var pending = new Stack<(IEnumerator<(DataPlace Place, int Line, IDataValue Value)> Members, string? Holder)>();
        pending.Push((root.Members.GetEnumerator(), null));

        try
        {
            while (pending.TryPeek(out var frame))
            {
                if (!frame.Members.MoveNext())
                {
                    frame.Members.Dispose();
                    pending.Pop();

                    // The container just walked leaves the path, the root, which is on none, alone not.
                    if (pending.Count > 0)
                    {
                        path.RemoveAt(path.Count - 1);
                    }

                    continue;
                }

                var (place, line, member) = frame.Members.Current;
                path.Add(place);
                string id;

                if (member.Alias is { } alias)
                {
                    id = Key(path, line, $"*{alias.Name}", frame.Holder);

                    if (alias.Target is { } target)
                    {
                        refers.Add((id, target, alias.Name, line));
                    }
                }
                else if (!member.Holds)
                {
                    id = Key(path, line, member.Text, frame.Holder);
                }
                else
                {
                    id = Section(place.Written, PathOf(path), line, Math.Max(line, member.End), frame.Holder);

                    if (path.Count == 1)
                    {
                        sections.TryAdd(place.Written, id);
                    }

                    refers.AddRange(member.Merges.Select(merge => (id, merge.Target, merge.Name, merge.Line)));
                    Made(member, id);
                    pending.Push((member.Members.GetEnumerator(), id));
                    continue;
                }

                Made(member, id);
                path.RemoveAt(path.Count - 1);
            }
        }
        finally
        {
            while (pending.TryPop(out var open))
            {
                open.Members.Dispose();
            }
        }

        foreach (var (from, to, name, line) in refers)
        {
            if (made.TryGetValue(to, out var node))
            {
                Refers(from, node, name, line);
            }
        }

        return new DataWalk(sections, made);

        void Made(IDataValue value, string id)
        {
            if (value.Identity is { } identity)
            {
                made.TryAdd(identity, id);
            }
        }
    }

    /// <summary>The path <paramref name="places"/> are, as every reader of data files writes one (<see cref="CodeKeyPaths.Of"/>).</summary>
    public static string PathOf(IReadOnlyList<DataPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);
        return CodeKeyPaths.Of(places.Select(place => (place.Written, place.Bracketed)));
    }

    /// <summary><paramref name="value"/> as a node's summary: on one line, whole (<see cref="ExtractText.Value"/>); <see langword="null"/> where nothing is left of it.</summary>
    private static string? SummaryOf(string value) => ExtractText.Value(value);

    private string Add(ExtractNode node)
    {
        _at[node.Id] = _nodes.Count;
        _nodes.Add(node);
        return node.Id;
    }
}

/// <summary>What a walk of a data file made (<see cref="DataNodes.Walk"/>).</summary>
/// <param name="Sections">The sections at the file's top, by the name of the member each is.</param>
/// <param name="Made">Each value's node, by the value's own (<see cref="IDataValue.Identity"/>), where it has one.</param>
internal sealed record DataWalk(IReadOnlyDictionary<string, string> Sections, IReadOnlyDictionary<object, string> Made);

/// <summary>One place of a data file's key path: a name, or an item's place in a list.</summary>
/// <param name="Name">The name; <see langword="null"/> for an item.</param>
/// <param name="Item">An item's place, from 0; -1 for a name.</param>
internal readonly record struct DataPlace(string? Name, int Item)
{
    /// <summary>The place of the member named <paramref name="name"/>.</summary>
    public static DataPlace Named(string name) => new(name, -1);

    /// <summary>The place of the item <paramref name="item"/>, from 0.</summary>
    public static DataPlace At(int item) => new(null, item);

    /// <summary>Whether a path holds it in brackets, to the place before it (<see cref="CodeKeyPaths.Bracketed"/>).</summary>
    public bool Bracketed => CodeKeyPaths.Bracketed(Name);

    /// <summary>The place as a path writes it (<see cref="CodeKeyPaths.Name"/>, <see cref="CodeKeyPaths.Item"/>).</summary>
    public string Written => Name is null ? CodeKeyPaths.Item(Item) : CodeKeyPaths.Name(Name);
}

/// <summary>A value of a data file as a walk of its keys reads it, whatever the file's language (<see cref="DataNodes.Walk(IDataValue)"/>).</summary>
internal interface IDataValue
{
    /// <summary>Whether it holds members or items - a mapping, an object, a list - rather than being a scalar.</summary>
    bool Holds { get; }

    /// <summary>A scalar's text as the file writes it; <see langword="null"/> for one that holds others, and for a scalar of no text.</summary>
    string? Text { get; }

    /// <summary>The last line it is written on, from 1: where its content ends, its closing bracket among it.</summary>
    int End { get; }

    /// <summary>Its members or items, in the order the file writes them, each with its place and the line it is written on.</summary>
    IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members { get; }

    /// <summary>What it is as the file's reader holds it, which a reference to it names; <see langword="null"/> where nothing refers to a value.</summary>
    object? Identity => null;

    /// <summary>
    /// What it stands for, where it is an alias - the value its anchor names (<see cref="Identity"/>), null where none is,
    /// and the anchor's name - which the walk makes a reference to, never reading it again here.
    /// </summary>
    (object? Target, string Name)? Alias => null;

    /// <summary>The containers it merges, in order, each by its own (<see cref="Identity"/>), with the name it is merged by and the line it is merged on.</summary>
    IEnumerable<(object Target, string Name, int Line)> Merges => [];
}
