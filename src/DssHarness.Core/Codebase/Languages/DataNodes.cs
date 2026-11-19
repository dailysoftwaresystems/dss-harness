using System.Globalization;
using System.Text;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// The nodes of a data file - JSON's, YAML's, XML's - as one owner makes them: a configuration key a <c>member</c>
/// SubKind <see cref="CodeSubKinds.Key"/> named by its path with its value as its summary, a container at the file's top a
/// <c>section</c> over its lines, an XML element a <c>member</c> SubKind <see cref="CodeSubKinds.Element"/>; every one
/// counted against <see cref="FileExtract.MostKeys"/>, the keys and elements past it - and those nested deeper than
/// <see cref="MostDepth"/> - counted in <see cref="FileExtract.KeysBeyond"/>; every text fitted to the model's bounds, a
/// summary and a signature flattened onto one line and cut (<see cref="ExtractText"/>), so no data file's extract is one the
/// core refuses.
/// </summary>
/// <param name="language">The implementation's name, which prefixes every id.</param>
/// <param name="file">The file's path, relative to the tree with forward separators.</param>
/// <param name="lineCount">The file's lines (<see cref="CodeFileText.Lines"/>), which every line named is held within.</param>
internal sealed class DataNodes(string language, string file, int lineCount)
{
    /// <summary>How deep a data file's keys are read, in places of their path: a key nested deeper is counted, not kept.</summary>
    public const int MostDepth = 8;

    /// <summary>
    /// How deep a data file may nest at all, past which nothing deeper of it is read and the place is marked: a guard against
    /// a file made to nest without end, whose reading would recurse as deep.
    /// </summary>
    public const int MostNesting = 64;

    /// <summary>
    /// The most nodes a walk visits again through what a file repeats by reference - a YAML alias - past which what it
    /// repeats is counted, not read, and the place is marked: a few hundred bytes of aliases can stand for millions of
    /// nodes.
    /// </summary>
    public const int MostRepeated = 10 * FileExtract.MostKeys;

    private readonly ExtractIds _ids = new();
    private readonly List<ExtractNode> _nodes = [];
    private readonly List<ExtractDynamic> _dynamic = [];
    private long _beyond;
    private int _repeated;
    private bool _repeatedPast;

    /// <summary>The nodes made, in the order they were made.</summary>
    public IReadOnlyList<ExtractNode> Nodes => _nodes;

    /// <summary>The places a walk stopped reading, each with why.</summary>
    public IReadOnlyList<ExtractDynamic> Dynamic => _dynamic;

    /// <summary>How many keys and elements are counted and not kept, at most <see cref="int.MaxValue"/>.</summary>
    public int Beyond => (int)Math.Min(_beyond, int.MaxValue);

    /// <summary>Whether the extract keeps <see cref="FileExtract.MostKeys"/> nodes already, so every key and element after is counted.</summary>
    public bool Full => _nodes.Count >= FileExtract.MostKeys;

    /// <summary>Counts <paramref name="keys"/> keys or elements no node is made of.</summary>
    /// <param name="keys">How many.</param>
    public void Count(long keys) => _beyond = keys > long.MaxValue - _beyond ? long.MaxValue : _beyond + keys;

    /// <summary>Marks <paramref name="line"/> as a place reading stopped at, with why.</summary>
    /// <param name="line">The line, from 1, held within the file's.</param>
    /// <param name="why">Why reading stopped there.</param>
    public void Stopped(int line, string why) => _dynamic.Add(new ExtractDynamic(FileExtract.LineWithin(line, lineCount), why));

    /// <summary>
    /// The key at <paramref name="path"/> on <paramref name="line"/>, its value <paramref name="value"/>, inside
    /// <paramref name="parent"/>: its id, or <see langword="null"/> where the extract is <see cref="Full"/>, the key counted.
    /// </summary>
    public string? Key(IReadOnlyList<DataPlace> path, int line, string? value, string? parent)
        => Key(path[^1].Written, PathOf(path), line, value, parent);

    /// <summary>
    /// The key <paramref name="name"/> - its whole path <paramref name="qualified"/> - on <paramref name="line"/>: its id, or
    /// <see langword="null"/> where the extract is <see cref="Full"/>, the key counted.
    /// </summary>
    public string? Key(string name, string qualified, int line, string? value, string? parent)
    {
        if (Full)
        {
            Count(1);
            return null;
        }

        var at = FileExtract.LineWithin(line, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(qualified)}"), CodeNodeKinds.Member, ExtractText.Whole(name), ExtractText.Whole(qualified), at, at, ExtractText.Signature(ExtractText.Flat(qualified)))
        {
            SubKind = CodeSubKinds.Key,
            Parent = parent,
            Summary = value is null ? null : SummaryOf(value),
        });
    }

    /// <summary>
    /// The element <paramref name="name"/> - its whole path <paramref name="qualified"/> - beginning on
    /// <paramref name="line"/>, inside <paramref name="parent"/>: its id, or <see langword="null"/> where the extract is
    /// <see cref="Full"/>, the element counted.
    /// </summary>
    public string? Element(string name, string qualified, int line, string? parent)
    {
        if (Full)
        {
            Count(1);
            return null;
        }

        var at = FileExtract.LineWithin(line, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(qualified)}"), CodeNodeKinds.Member, ExtractText.Whole(name), ExtractText.Whole(qualified), at, at, ExtractText.Signature(ExtractText.Flat(qualified)))
        {
            SubKind = CodeSubKinds.Element,
            Parent = parent,
        });
    }

    /// <summary>
    /// The section <paramref name="name"/> at <paramref name="path"/>, over the lines <paramref name="start"/> to
    /// <paramref name="end"/>, inside <paramref name="parent"/>: its id, or <see langword="null"/> where the extract is
    /// <see cref="Full"/> - a section holds keys, which are counted.
    /// </summary>
    public string? Section(string name, string path, int start, int end, string? parent)
    {
        if (Full)
        {
            return null;
        }

        var from = FileExtract.LineWithin(start, lineCount);

        return Add(new ExtractNode(_ids.Next($"{language}:{file}#{ExtractText.Whole(path)}"), CodeNodeKinds.Section, ExtractText.Whole(name), $"{file}#{ExtractText.Whole(path)}", from, Math.Max(from, FileExtract.LineWithin(end, lineCount)), ExtractText.Signature(ExtractText.Flat(path))) { Parent = parent });
    }

    /// <summary>
    /// <paramref name="node"/>, a node of the file's own kind - a XAML view's type - its id given as the file's other ids
    /// are: its id, or <see langword="null"/> where the extract is <see cref="Full"/>.
    /// </summary>
    public string? Node(ExtractNode node)
        => Full ? null : Add(node with { Id = _ids.Next(node.Id), Signature = ExtractText.Signature(ExtractText.Flat(node.Signature)) });

    /// <summary>Sets the last line of the node <paramref name="id"/> made: its lines end there, never before they begin.</summary>
    public void Ends(string id, int end)
    {
        var at = _nodes.FindLastIndex(node => node.Id == id);
        _nodes[at] = _nodes[at] with { End = Math.Max(_nodes[at].Start, FileExtract.LineWithin(end, lineCount)) };
    }

    /// <summary>
    /// Sets the summary of the node <paramref name="id"/> made from <paramref name="value"/>, where it has none: the text an
    /// element holds, first, flattened and cut.
    /// </summary>
    public void Summarize(string id, string value)
    {
        var at = _nodes.FindLastIndex(node => node.Id == id);

        if (_nodes[at].Summary is null && SummaryOf(value) is { } summary)
        {
            _nodes[at] = _nodes[at] with { Summary = summary };
        }
    }

    /// <summary>
    /// Walks <paramref name="root"/>, a data file's value: each scalar key a key node named by its path, each container at
    /// the top a section over its lines, each item of a list placed by its place - nothing deeper than
    /// <see cref="MostDepth"/> places kept, the keys past it counted, and a container repeated past
    /// <see cref="MostRepeated"/> visits counted and its place marked.
    /// </summary>
    /// <param name="root">The file's value.</param>
    /// <returns>The sections made at the top, by the name of the member each is.</returns>
    public IReadOnlyDictionary<string, string> Walk(IDataValue root)
    {
        ArgumentNullException.ThrowIfNull(root);

        var sections = new Dictionary<string, string>(StringComparer.Ordinal);
        Walk(root, [], null, sections);
        return sections;
    }

    private void Walk(IDataValue value, List<DataPlace> path, string? parent, Dictionary<string, string> sections)
    {
        foreach (var (place, line, member) in value.Members)
        {
            if (member.Repeated && !Repeats(line))
            {
                Count(member.Scalars);
                continue;
            }

            path.Add(place);

            try
            {
                if (!member.Holds)
                {
                    Key(path, line, member.Text, parent);
                    continue;
                }

                var holder = parent;

                if (path.Count == 1 && Section(place.Written, PathOf(path), line, Math.Max(line, member.End), parent) is { } section)
                {
                    sections.TryAdd(place.Written, section);
                    holder = section;
                }

                // A container nested as deep as keys are read holds keys past it, and the extract once full keeps no
                // more: what it holds is counted, not walked.
                if (path.Count >= MostDepth || Full)
                {
                    Count(member.Scalars);
                    continue;
                }

                Walk(member, path, holder, sections);
            }
            finally
            {
                path.RemoveAt(path.Count - 1);
            }
        }
    }

    /// <summary>
    /// Whether reading may visit one more node through what the file repeats - a walk's, and a reader's of its own that
    /// reads what a reference repeats, as YAML's merge keys - the place marked the first time it may not: one budget of
    /// <see cref="MostRepeated"/> for all of it.
    /// </summary>
    /// <param name="line">The line the visit is made from, which the mark names.</param>
    public bool Repeats(int line)
    {
        if (++_repeated <= MostRepeated)
        {
            return true;
        }

        if (!_repeatedPast)
        {
            _repeatedPast = true;
            Stopped(line, $"the file's aliases repeat more than {MostRepeated.ToString(CultureInfo.InvariantCulture)} nodes, and what they repeat from here on is not read");
        }

        return false;
    }

    /// <summary>
    /// The path <paramref name="places"/> are: each name joined by a dot, an item's place and a name that is empty, white
    /// space or holds a control character held in brackets to the place before it - <c>targets.x64.callingConvention</c>,
    /// <c>limits[1].value</c>, <c>packages[""].name</c>, <c>messages["Sure?\nIt cannot be undone."]</c> - always one line.
    /// </summary>
    public static string PathOf(IReadOnlyList<DataPlace> places)
    {
        ArgumentNullException.ThrowIfNull(places);

        var path = new StringBuilder();

        foreach (var place in places)
        {
            _ = place.Bracketed || path.Length == 0 ? path.Append(place.Written) : path.Append('.').Append(place.Written);
        }

        return path.ToString();
    }

    /// <summary><paramref name="value"/> as a node's summary: on one line, cut; <see langword="null"/> where nothing is left of it.</summary>
    private static string? SummaryOf(string value) => ExtractText.Flat(value) is { Length: > 0 } flat ? ExtractText.Summary(flat) : null;

    private string Add(ExtractNode node)
    {
        _nodes.Add(node);
        return node.Id;
    }
}

/// <summary>One place of a data file's key path: a name, or an item's place in a list.</summary>
/// <param name="Name">The name; <see langword="null"/> for an item.</param>
/// <param name="Item">An item's place, from 0; -1 for a name.</param>
internal readonly record struct DataPlace(string? Name, int Item)
{
    /// <summary>The place of the member named <paramref name="name"/>.</summary>
    public static DataPlace Named(string name) => new(name, -1);

    /// <summary>The place of the item <paramref name="item"/>, from 0.</summary>
    public static DataPlace At(int item) => new(null, item);

    /// <summary>
    /// Whether a path holds it in brackets, to the place before it: an item, a name that is empty or white space, which no
    /// dotted path can hold, and a name that holds a control character - a line break a key written as its source string
    /// holds - which no one line can.
    /// </summary>
    public bool Bracketed => Name is null || string.IsNullOrWhiteSpace(Name) || Name.Any(char.IsControl);

    /// <summary>
    /// The place as a path writes it, always on one line: a name as it is, one that is bracketed quoted in brackets as
    /// <see cref="ReportText.Quoted"/> writes it - each control character escaped - and an item <c>[i]</c>.
    /// </summary>
    public string Written => Name is null ? $"[{Item.ToString(CultureInfo.InvariantCulture)}]"
        : Bracketed ? $"[{ReportText.Quoted(Name)}]"
        : Name;
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

    /// <summary>Whether it is reached through what the file repeats by reference - an alias - so a walk visits it again.</summary>
    bool Repeated { get; }

    /// <summary>How many scalar keys it holds at any depth, itself one where it holds none: what a walk counts of it where it walks it not.</summary>
    long Scalars { get; }

    /// <summary>Its members or items, in the order the file writes them, each with its place and the line it is written on.</summary>
    IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members { get; }
}
