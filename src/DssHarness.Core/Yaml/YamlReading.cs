using YamlDotNet.Core;
using YamlDotNet.Core.Events;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Yaml;

/// <summary>
/// The one reader of YAML this tool has, over YamlDotNet's representation model: it loads a file's
/// root mapping, and reads the keys, scalars and booleans of a file of mappings, reporting every
/// problem together with the line and column it was found at.
/// </summary>
/// <remarks>
/// <para>
/// Read through the representation model rather than a serialiser on purpose. A serialiser would
/// reach a typed object faster and lose the two things a file of rules needs most: the position of
/// the key that was wrong, and the fact that a key was not recognised at all. A silently ignored
/// key is a rule nobody applied, and the configuration reader elsewhere in this repository refuses
/// one for the same reason.
/// </para>
/// <para>
/// Every reader of a YAML file in this tool - the action reader and the workflow reader - reads
/// through these helpers, so two files written the same way are read the same way, save for the one
/// thing a reader may ask of its own files before they are read: bounds (<see cref="YamlBounds"/>).
/// The workflow reader asks them, so a workflow file holding an alias or nested past its bound is
/// refused; the action reader asks none, so an action file is read whatever it holds of either. The codebase graph's
/// reader of data files reads a file's documents as written (<see cref="Written"/>), and follows its aliases at a bound of
/// its own; every depth any of them is held to is counted by one walk of the events.
/// </para>
/// </remarks>
public static class YamlReading
{
    /// <summary>
    /// Loads <paramref name="text"/> as one document whose top level is a mapping of keys, or says
    /// why it is not: every problem reported together.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="oneDocument">
    /// How a refusal says the file holds one document, as the kind of file being read spells it -
    /// for instance <c>an action file holds one, so that nothing has to decide which of them a
    /// runner ran</c>.
    /// </param>
    /// <param name="problems">Where problems are collected.</param>
    /// <param name="bounds">
    /// What the text is held to before it is read into nodes, or <see langword="null"/> where the
    /// reader holds it to nothing: see <see cref="YamlBounds"/>.
    /// </param>
    public static YamlMappingNode? Root(string text, string oneDocument, List<string> problems, YamlBounds? bounds = null)
    {
        ArgumentNullException.ThrowIfNull(problems);

        var root = Load(text, oneDocument, bounds, out var stop);

        if (stop is not null)
        {
            problems.Add(stop.Line > 0 ? $"line {stop.Line}, column {stop.Column}: {stop.Why}" : stop.Why);
        }

        return root;
    }

    /// <summary>
    /// Loads <paramref name="text"/> as <see cref="Root"/> does, and says where and why it is no such document where it is
    /// not - for a reader that marks the line a file stops reading at, as a graph's does.
    /// </summary>
    /// <param name="text">The file's contents.</param>
    /// <param name="oneDocument">How a refusal says the file holds one document (<see cref="Root"/>).</param>
    /// <param name="bounds">What the text is held to before it is read into nodes, or <see langword="null"/>.</param>
    /// <param name="stop">Where and why it is no such document; <see langword="null"/> where it is one.</param>
    public static YamlMappingNode? Load(string text, string oneDocument, YamlBounds? bounds, out YamlStop? stop)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (bounds is not null && Bounded(text, bounds) is { } bounded)
        {
            stop = bounded;
            return null;
        }

        var stream = new YamlStream();

        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException exception)
        {
            stop = new YamlStop(Line(exception.Start.Line), Line(exception.Start.Column), $"the file is not valid YAML ({exception.Message}).");
            return null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            // What the representation model throws besides its own - a key written twice among them - carries no place:
            // the whole file is said, never a line it would make up.
            stop = new YamlStop(0, 0, $"the file does not read as YAML, at a place its reader does not say ({exception.Message}).");
            return null;
        }

        if (stream.Documents.Count == 0)
        {
            stop = new YamlStop(0, 0, "the file is empty.");
            return null;
        }

        if (stream.Documents.Count > 1)
        {
            stop = new YamlStop(0, 0, $"the file holds {stream.Documents.Count} YAML documents; {oneDocument}.");
            return null;
        }

        if (stream.Documents[0].RootNode is YamlMappingNode mapping)
        {
            stop = null;
            return mapping;
        }

        var node = stream.Documents[0].RootNode;
        stop = new YamlStop(Line(node.Start.Line), Line(node.Start.Column), "the file's top level is not a mapping of keys.");
        return null;
    }

    /// <summary>
    /// <paramref name="text"/>'s documents as the text writes them, read once as events: each node where it is written,
    /// an alias kept as the node it names rather than repeated - what a data file's reader walks, following aliases at a
    /// bound of its own - no container nested deeper than <paramref name="mostNesting"/> read, and where the text stops
    /// reading as YAML, what was read before it kept and the rest not read. Each place reading stopped is said, with why.
    /// </summary>
    /// <remarks>
    /// The representation model is not read here: it resolves an alias into the node it names, so neither where the alias
    /// is written nor how often it repeats a part of the file is left to see, and it reads one file of several documents,
    /// or of a key written twice, as none.
    /// </remarks>
    /// <param name="text">The file's contents.</param>
    /// <param name="mostNesting">How deep mappings and lists may nest, past which what a container holds is not read.</param>
    internal static YamlWrittenText Written(string text, int mostNesting)
    {
        ArgumentNullException.ThrowIfNull(text);

        var documents = new List<YamlWritten>();
        var stops = new List<YamlWrittenStop>();
        var anchors = new Dictionary<string, YamlWritten>(StringComparer.Ordinal);
        var open = new Stack<(YamlWritten Node, string? Anchor)>();
        YamlWritten? root = null;
        var last = Mark.Empty;
        var skipped = 0;

        void Place(YamlWritten node, string? anchor)
        {
            if (open.TryPeek(out var holder))
            {
                holder.Node.Children.Add(node);
            }
            else
            {
                root ??= node;
            }

            if (anchor is not null && node.Kind == YamlWrittenKind.Scalar)
            {
                anchors[anchor] = node;
            }
        }

        try
        {
            foreach (var (current, depth) in Nested(new Parser(new StringReader(text))))
            {
                // What a container nested past the bound holds is passed over, its own close ending it.
                if (skipped > 0)
                {
                    skipped += current is MappingStart or SequenceStart ? 1 : current is MappingEnd or SequenceEnd ? -1 : 0;
                    last = current.End;
                    continue;
                }

                switch (current)
                {
                    case DocumentStart:
                        // An anchor names a node of its own document alone.
                        anchors.Clear();
                        root = null;
                        break;

                    case DocumentEnd:
                        if (root is not null && !(root is { Kind: YamlWrittenKind.Scalar, Plain: true, Value.Length: 0 }))
                        {
                            documents.Add(root);
                        }

                        root = null;
                        break;

                    case Scalar scalar:
                        Place(new YamlWritten(YamlWrittenKind.Scalar, scalar.Start) { End = scalar.End, Value = scalar.Value, Plain = scalar.Style == ScalarStyle.Plain }, scalar.Anchor.IsEmpty ? null : scalar.Anchor.Value);
                        break;

                    case AnchorAlias alias:
                        var named = anchors.GetValueOrDefault(alias.Value.Value);

                        // What an alias repeats nests as deep as it does from the alias's place: one that would nest past the
                        // bound there is not followed, so no walk through aliases goes deeper than one of the text does.
                        if (named is not null && depth + named.Reach > mostNesting)
                        {
                            Place(new YamlWritten(YamlWrittenKind.Alias, alias.Start) { End = alias.End, Value = alias.Value.Value, Cut = true }, null);
                            stops.Add(new YamlWrittenStop(alias.Start, $"the alias '*{alias.Value.Value}' repeats here what nests more than {mostNesting} mappings and lists deep, and none of it is read"));
                            break;
                        }

                        Place(new YamlWritten(YamlWrittenKind.Alias, alias.Start) { End = alias.End, Value = alias.Value.Value, Target = named, Reach = named?.Reach ?? 0 }, null);
                        break;

                    case MappingStart or SequenceStart:
                        var start = (NodeEvent)current;
                        var node = new YamlWritten(start is MappingStart ? YamlWrittenKind.Mapping : YamlWrittenKind.Sequence, start.Start)
                        {
                            End = start.End,
                            Flow = start is MappingStart { Style: MappingStyle.Flow } or SequenceStart { Style: SequenceStyle.Flow },
                        };

                        if (depth > mostNesting)
                        {
                            node.Cut = true;
                            node.Reach = 1;
                            Place(node, null);
                            stops.Add(new YamlWrittenStop(start.Start, $"the file nests more than {mostNesting} mappings and lists deep here, and nothing deeper is read"));
                            skipped = 1;
                            break;
                        }

                        Place(node, null);
                        open.Push((node, start.Anchor.IsEmpty ? null : start.Anchor.Value));
                        break;

                    case MappingEnd or SequenceEnd:
                        var (closed, anchor) = open.Pop();
                        closed.End = current.End;
                        closed.Reach = 1 + (closed.Children.Count == 0 ? 0 : closed.Children.Max(child => child.Reach));

                        // An anchor names its node once the node is whole: an alias inside it names an earlier one, never
                        // the node it stands in, so nothing the text writes repeats itself.
                        if (anchor is not null)
                        {
                            anchors[anchor] = closed;
                        }

                        break;
                }

                last = current.End;
            }
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or ArgumentException)
        {
            stops.Add(exception is YamlException yaml
                ? new YamlWrittenStop(yaml.Start, $"the file stops reading as YAML here ({yaml.Message.TrimEnd('.')}), and nothing after it is read")
                : new YamlWrittenStop(last, $"the file stops reading as YAML past here ({exception.Message.TrimEnd('.')}), and nothing after it is read"));

            // What was read before the fault is kept, each container still open ending where reading did.
            while (open.TryPop(out var unclosed))
            {
                unclosed.Node.End = last;
            }

            if (root is not null)
            {
                documents.Add(root);
            }
        }

        return new YamlWrittenText(documents, stops);
    }

    /// <summary>A line or a column YamlDotNet counts, as an <see cref="int"/>.</summary>
    private static int Line(long at) => (int)Math.Clamp(at, 0, int.MaxValue);

    /// <summary>A problem's place in the file, so it points at what it is about.</summary>
    public static string At(YamlNode node, string message)
        => $"line {node.Start.Line}, column {node.Start.Column}: {message}";

    /// <summary>The plain name <paramref name="node"/> is, or a problem where it is not one.</summary>
    public static string? KeyOf(YamlNode node, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, "a key is not a plain name."));
        return null;
    }

    /// <summary>The scalar <paramref name="node"/> is, or <see langword="null"/> where it is not one.</summary>
    public static string? ScalarOf(YamlNode node)
        => node is YamlScalarNode { Value: { } value } ? value : null;

    /// <summary>
    /// The scalar <paramref name="node"/> is, or a problem where it is not one, naming
    /// <paramref name="what"/> it was meant to be.
    /// </summary>
    public static string? RequireScalar(YamlNode node, string what, List<string> problems)
    {
        if (node is YamlScalarNode { Value: { } value })
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is not a single value."));
        return null;
    }

    /// <summary>
    /// The true or false <paramref name="node"/> is, or a problem where it says something else,
    /// naming <paramref name="what"/> it was meant to be.
    /// </summary>
    /// <remarks>
    /// Read as <see cref="bool.TryParse(string, out bool)"/> reads it: the word true or false in any
    /// case - <c>True</c> and <c>FALSE</c> among them - with any space around it. Anything else, the
    /// other words YAML can mean a boolean by - <c>yes</c>, <c>on</c> - included, is refused rather
    /// than read as false. Keys like these turn guards on, so a misspelling read as "off" is a file
    /// that looks guarded and is not - which is the failure the guards exist to make impossible.
    /// </remarks>
    public static bool? RequireBoolean(YamlNode node, string what, List<string> problems)
    {
        var text = RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (bool.TryParse(text, out var value))
        {
            return value;
        }

        problems.Add(At(node, $"'{what}' is '{text}', which is not true or false."));
        return null;
    }

    /// <summary>
    /// The key <paramref name="node"/> names, when <paramref name="known"/> lists it. Any other is
    /// a problem added here, before a reader looks at it.
    /// </summary>
    public static string? KnownKey(YamlNode node, IReadOnlyList<string> known, string what, List<string> problems)
    {
        var key = KeyOf(node, problems);

        if (key is null || known.Contains(key, StringComparer.Ordinal))
        {
            return key;
        }

        problems.Add(At(node, $"'{key}' is not a {what}. Known: '{string.Join("', '", known)}'."));
        return null;
    }

    /// <summary>
    /// Where <paramref name="text"/> first does not keep to <paramref name="bounds"/>, read as events
    /// before anything is read into nodes: no alias, and mappings and lists nested no deeper than the
    /// bounds allow; <see langword="null"/> where it keeps to them.
    /// </summary>
    /// <remarks>
    /// Read as events because nodes are what the bounds guard: an alias makes a few hundred bytes
    /// stand for millions of nodes, and nesting past any file's depth is recursed through to be
    /// read at all. Text that is not YAML passes here, to be refused where it is read into nodes, in
    /// the parser's own words.
    /// </remarks>
    private static YamlStop? Bounded(string text, YamlBounds bounds)
    {
        try
        {
            foreach (var (current, depth) in Nested(new Parser(new StringReader(text))))
            {
                switch (current)
                {
                    case AnchorAlias alias:
                        return new YamlStop(Line(alias.Start.Line), Line(alias.Start.Column), $"the alias '*{alias.Value}' repeats a part of the file by reference; {bounds.NoAlias}.");

                    case MappingStart or SequenceStart when depth > bounds.MaxDepth:
                        return new YamlStop(Line(current.Start.Line), Line(current.Start.Column), $"the file nests more than {bounds.MaxDepth} levels deep; {bounds.TooDeep}.");
                }
            }
        }
        catch (YamlException)
        {
            return null;
        }

        return null;
    }

    /// <summary>
    /// Each event <paramref name="parser"/> reads, with how deep in mappings and lists it stands - a container's start and
    /// its end at the depth of the container, 1 for one at the top: the one count of nesting every bound on YAML reads.
    /// </summary>
    private static IEnumerable<(ParsingEvent Event, int Depth)> Nested(IParser parser)
    {
        var depth = 0;

        while (parser.MoveNext())
        {
            var current = parser.Current!;

            switch (current)
            {
                case MappingStart or SequenceStart:
                    yield return (current, ++depth);
                    break;

                case MappingEnd or SequenceEnd:
                    yield return (current, depth--);
                    break;

                default:
                    yield return (current, depth);
                    break;
            }
        }
    }
}

/// <summary>What a YAML node is, as the text writes it (<see cref="YamlWritten"/>).</summary>
internal enum YamlWrittenKind
{
    /// <summary>A scalar: a value written out.</summary>
    Scalar,

    /// <summary>A mapping of keys.</summary>
    Mapping,

    /// <summary>A list.</summary>
    Sequence,

    /// <summary>An alias: a reference to a node anchored before it, written once and standing for that node.</summary>
    Alias,
}

/// <summary>
/// One node of a YAML document as the text writes it (<see cref="YamlReading.Written"/>): where it begins and ends, a
/// scalar's value, a container's children, an alias's node - never the alias's node repeated in its place.
/// </summary>
/// <param name="kind">What it is.</param>
/// <param name="start">Where it begins.</param>
internal sealed class YamlWritten(YamlWrittenKind kind, Mark start)
{
    /// <summary>What it is.</summary>
    public YamlWrittenKind Kind { get; } = kind;

    /// <summary>Where it begins.</summary>
    public Mark Start { get; } = start;

    /// <summary>Where it ends: a block container's own end is where the next thing begins, so its last line is read from what it holds.</summary>
    public Mark End { get; set; }

    /// <summary>A scalar's value; an alias's anchor name; <see langword="null"/> for a container.</summary>
    public string? Value { get; init; }

    /// <summary>Whether it is a plain scalar - one with no quotes or block - as a merge key's <c>&lt;&lt;</c> is.</summary>
    public bool Plain { get; init; }

    /// <summary>Whether it is a container written in the flow style, its closing bracket its last.</summary>
    public bool Flow { get; init; }

    /// <summary>
    /// Whether it is a container nested past the reader's bound, whose content is not read, or an alias whose node nests
    /// past it from the alias's place, which is not followed.
    /// </summary>
    public bool Cut { get; set; }

    /// <summary>
    /// How many levels of mappings and lists it holds, itself among them, its aliases followed: 0 for a scalar and an alias
    /// not followed, an alias's node's for one followed - never more than the reader's bound from where it is written.
    /// </summary>
    public int Reach { get; set; }

    /// <summary>A list's items; a mapping's keys and values, each key before its value.</summary>
    public List<YamlWritten> Children { get; } = [];

    /// <summary>The node an alias names, anchored before it in its document; <see langword="null"/> where none is.</summary>
    public YamlWritten? Target { get; init; }
}

/// <summary>A YAML text's documents as written, each with something in it, and each place reading stopped (<see cref="YamlReading.Written"/>).</summary>
/// <param name="Documents">Each document's root, in the order written.</param>
/// <param name="Stops">Each place reading stopped, with why.</param>
internal sealed record YamlWrittenText(IReadOnlyList<YamlWritten> Documents, IReadOnlyList<YamlWrittenStop> Stops);

/// <summary>A place reading a YAML text stopped, with why.</summary>
/// <param name="At">Where.</param>
/// <param name="Why">Why, as a clause.</param>
internal sealed record YamlWrittenStop(Mark At, string Why);

/// <summary>Where and why a text is no document of one mapping (<see cref="YamlReading.Load"/>).</summary>
/// <param name="Line">The line it is found at, from 1; 0 where it is the whole file's.</param>
/// <param name="Column">The column, from 1; 0 where it is the whole file's.</param>
/// <param name="Why">Why, as a sentence.</param>
public sealed record YamlStop(int Line, int Column, string Why);

/// <summary>
/// What a reader holds its files' YAML to before they are read into nodes (<see cref="YamlReading.Root"/>): no alias,
/// and no deeper nesting than <paramref name="MaxDepth"/>. Asked by a reader of files whose every part is meant to be
/// written out, as a workflow's definition is; the action reader asks none.
/// </summary>
/// <param name="MaxDepth">How deep mappings and lists may nest inside one another.</param>
/// <param name="NoAlias">
/// Why the reader's files write no alias, completing the refusal of one after a semicolon - for instance <c>a
/// definition writes each of its steps out, so that what it says is what it reads</c>.
/// </param>
/// <param name="TooDeep">
/// Why none of the reader's files nests deeper, completing the refusal of one after a semicolon - for instance <c>no
/// definition does</c>.
/// </param>
public sealed record YamlBounds(int MaxDepth, string NoAlias, string TooDeep);
