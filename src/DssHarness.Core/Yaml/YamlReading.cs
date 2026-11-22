using System.Runtime.ExceptionServices;
using DssHarness.Core.Walks;
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
/// thing a reader may ask of its own files before they are read: rules (<see cref="YamlRules"/>).
/// The workflow reader asks them, so a workflow file holding an alias is refused; the action reader
/// asks none, so an action file is read whatever aliases it holds. The codebase graph's reader of data
/// files reads a file's documents as written (<see cref="Written"/>), each alias a reference to the node
/// its anchor names, never that node again.
/// </para>
/// <para>
/// No depth is refused. The representation model recurses once for each level a document nests, and a
/// stack overflow cannot be caught - it ends the process - so a text is first read as events, on a
/// stack of this reader's own, to count how deep it nests (<see cref="Nested"/>), and the model then
/// reads those events on a thread whose stack holds that depth (<see cref="DeepCalls.RunSized"/>), of
/// <see cref="FrameBytes"/> a level: a file nested a hundred thousand levels deep is read as one nested
/// two is, its every node keeping its line and column. An alias stays the node its anchor names - the
/// model gives the one node in each place an alias stands, never a copy of it.
/// </para>
/// </remarks>
public static class YamlReading
{
    /// <summary>
    /// The stack one level of the representation model's reading takes, at most: the recursion of a mapping or a list into
    /// the node it holds, and the hashing and comparing of a key that is no plain value, which recurse as deep.
    /// </summary>
    /// <remarks>
    /// Measured on YamlDotNet 18.1 under .NET 10 on Windows x64 - whose calls take the most stack of the systems this
    /// tool runs on, since each reserves room for its callee's arguments - by a probe outside the repository that
    /// recorded the lowest stack address reached as documents of 500 to 50,000 levels were read, and that searched, in
    /// steps of 16 KiB, for the least stack each document reads on without overflowing. A level of a mapping took up to
    /// 568 bytes and one of a list 421 where the reading ran as the runtime first compiles it, unoptimised, which is how
    /// a reading that recurses at once runs; fully optimised, 320 and 208. Hashing a key of nested lists, comparing two
    /// equal ones, and an error thrown and unwound at the deepest level took no more a level than reading did. 1024
    /// bytes is nearly twice the most measured: the margin for other processors, other compilations of the runtime's
    /// and later versions of the library.
    /// </remarks>
    internal const int FrameBytes = 1024;

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
    /// <param name="rules">
    /// What the text is held to before it is read into nodes, or <see langword="null"/> where the
    /// reader holds it to nothing: see <see cref="YamlRules"/>.
    /// </param>
    public static YamlMappingNode? Root(string text, string oneDocument, List<string> problems, YamlRules? rules = null)
    {
        ArgumentNullException.ThrowIfNull(problems);

        var root = Load(text, oneDocument, rules, out var stop);

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
    /// <param name="rules">What the text is held to before it is read into nodes, or <see langword="null"/>.</param>
    /// <param name="stop">Where and why it is no such document; <see langword="null"/> where it is one.</param>
    public static YamlMappingNode? Load(string text, string oneDocument, YamlRules? rules, out YamlStop? stop)
    {
        ArgumentNullException.ThrowIfNull(text);

        return Load(new StringReader(text), oneDocument, rules, out stop);
    }

    /// <summary>
    /// Loads the text <paramref name="text"/> reads as <see cref="Load(string, string, YamlRules?, out YamlStop?)"/> does:
    /// read once, as the parser asks for it, so a file is never held as one string to be read.
    /// </summary>
    /// <param name="text">A reader of the file's contents.</param>
    /// <param name="oneDocument">How a refusal says the file holds one document (<see cref="Root"/>).</param>
    /// <param name="rules">What the text is held to before it is read into nodes, or <see langword="null"/>.</param>
    /// <param name="stop">Where and why it is no such document; <see langword="null"/> where it is one.</param>
    public static YamlMappingNode? Load(TextReader text, string oneDocument, YamlRules? rules, out YamlStop? stop)
    {
        ArgumentNullException.ThrowIfNull(text);

        var measured = Measured(text, rules);

        if (measured.Refusal is not null)
        {
            stop = measured.Refusal;
            return null;
        }

        YamlStream stream;

        try
        {
            stream = DeepCalls.RunSized(
                measured.Depth,
                FrameBytes,
                () =>
                {
                    var read = new YamlStream();
                    read.Load(new YamlReplay(measured.Events, measured.Fault));
                    return read;
                });
        }
        catch (InsufficientExecutionStackException exception)
        {
            stop = new YamlStop(Line(measured.DeepestAt.Line), Line(measured.DeepestAt.Column), $"the file nests {measured.Depth} levels deep here, which no thread can read into nodes ({exception.Message}).");
            return null;
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
    /// however deep, an alias kept as a reference to the node its anchor names rather than repeated - what a data file's
    /// reader walks, reading what an anchor names once - and where the text stops reading as YAML, what was read before it
    /// kept and the rest not read. Each place reading stopped is said, with why.
    /// </summary>
    /// <remarks>
    /// The representation model is not read here: it resolves an alias into the node it names, so neither where the alias
    /// is written nor how often it repeats a part of the file is left to see, and it reads one file of several documents,
    /// or of a key written twice, as none. The events are read on a stack of the reader's own, so no nesting of the text
    /// is recursed through.
    /// </remarks>
    /// <param name="text">A reader of the file's contents, read once as the parser asks for it.</param>
    internal static YamlWrittenText Written(TextReader text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var documents = new List<YamlWritten>();
        var stops = new List<YamlWrittenStop>();
        var anchors = new Dictionary<string, YamlWritten>(StringComparer.Ordinal);
        var open = new Stack<(YamlWritten Node, string? Anchor)>();
        YamlWritten? root = null;
        var last = Mark.Empty;

        void Place(YamlWritten node)
        {
            if (open.TryPeek(out var holder))
            {
                holder.Node.Children.Add(node);
            }
            else
            {
                root ??= node;
            }
        }

        try
        {
            var parser = new Parser(text);

            while (parser.MoveNext())
            {
                var current = parser.Current!;

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
                        var written = new YamlWritten(YamlWrittenKind.Scalar, scalar.Start) { End = scalar.End, Value = scalar.Value, Plain = scalar.Style == ScalarStyle.Plain };
                        Place(written);

                        if (!scalar.Anchor.IsEmpty)
                        {
                            anchors[scalar.Anchor.Value] = written;
                        }

                        break;

                    case AnchorAlias alias:
                        Place(new YamlWritten(YamlWrittenKind.Alias, alias.Start) { End = alias.End, Value = alias.Value.Value, Target = anchors.GetValueOrDefault(alias.Value.Value) });
                        break;

                    case MappingStart or SequenceStart:
                        var start = (NodeEvent)current;
                        var node = new YamlWritten(start is MappingStart ? YamlWrittenKind.Mapping : YamlWrittenKind.Sequence, start.Start)
                        {
                            End = start.End,
                            Flow = start is MappingStart { Style: MappingStyle.Flow } or SequenceStart { Style: SequenceStyle.Flow },
                        };

                        Place(node);
                        open.Push((node, start.Anchor.IsEmpty ? null : start.Anchor.Value));
                        break;

                    case MappingEnd or SequenceEnd:
                        var (closed, anchor) = open.Pop();
                        closed.End = current.End;

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
    /// <paramref name="text"/> read as events before anything is read into nodes: the events themselves, kept for the
    /// model to read (<see cref="YamlReplay"/>), how deep they nest, the deepest place, and where the text first breaks a
    /// rule its reader asks (<paramref name="rules"/>) or holds a key the representation model could not read in time or
    /// in any stack.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The text is scanned once: the parser takes time that grows with the square of how deep brackets nest, since it
    /// looks again at the possible key of every open bracket for each token it reads, and the model reads the events kept
    /// here rather than scanning the text again. Where the text stops reading as YAML, the events read before the fault are kept and the
    /// fault with them, so the model reads to the same place and meets the same fault, in the parser's own words; the
    /// depth is the deepest those events reach, since the model recurses that deep before it meets the fault.
    /// </para>
    /// <para>
    /// The model hashes a key, and compares it with the other keys of its mapping, by walking all of it, with no record of
    /// what it walked: a key holding an alias that names a mapping or a list - one anchored before it, or one whose anchor
    /// is still to come - would be walked again at each alias, so a few hundred bytes would take years, and a key whose
    /// node holds itself would be walked without end, past any stack. No reader of this tool reads a key that is no plain
    /// name, so such a key is refused here, where its alias is met, rather than walked; an alias of a scalar in a key is
    /// read, as any scalar is.
    /// </para>
    /// </remarks>
    private static YamlMeasured Measured(TextReader text, YamlRules? rules)
    {
        var events = new List<ParsingEvent>();
        var deepest = 0;
        var deepestAt = Mark.Empty;

        // Whether each anchor of the document names a mapping or a list - the model names a container from where it opens,
        // so an alias inside it is the container itself.
        var anchors = new Dictionary<string, bool>(StringComparer.Ordinal);

        YamlMeasured Refused(Mark at, string why)
            => new(events, null, deepest, deepestAt, new YamlStop(Line(at.Line), Line(at.Column), why));

        try
        {
            foreach (var (current, depth, inKey) in Nested(new Parser(text)))
            {
                events.Add(current);

                switch (current)
                {
                    case DocumentStart:
                        anchors.Clear();
                        break;

                    case AnchorAlias alias when rules?.NoAlias is { } noAlias:
                        return Refused(alias.Start, $"the alias '*{alias.Value}' repeats a part of the file by reference; {noAlias}.");

                    case AnchorAlias alias when inKey && (!anchors.TryGetValue(alias.Value.Value, out var container) || container):
                        return Refused(alias.Start, $"the alias '*{alias.Value}' makes part of a key of a mapping or a list; a key is a plain name, and such a key would be read by walking all it names again at each alias - without end where it holds itself - so the file is not read.");

                    case MappingStart or SequenceStart:
                        if (depth > deepest)
                        {
                            deepest = depth;
                            deepestAt = current.Start;
                        }

                        if (((NodeEvent)current).Anchor is { IsEmpty: false } anchor)
                        {
                            anchors[anchor.Value] = true;
                        }

                        break;

                    case Scalar { Anchor.IsEmpty: false } scalar:
                        anchors[scalar.Anchor.Value] = false;
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is YamlException or InvalidOperationException or ArgumentException)
        {
            // The text stops reading as YAML here: the model is handed the fault where it meets the place.
            return new YamlMeasured(events, ExceptionDispatchInfo.Capture(exception), deepest, deepestAt, Refusal: null);
        }

        return new YamlMeasured(events, null, deepest, deepestAt, Refusal: null);
    }

    /// <summary>
    /// Each event <paramref name="parser"/> reads, with how deep in mappings and lists it stands - a container's start and
    /// its end at the depth of the container, 1 for one at the top: the one count of nesting the reader reads - and whether
    /// it stands in a key: a node that is a mapping's key, or anything inside one, and the end of a container that does.
    /// </summary>
    /// <remarks>Read on a stack of open containers of its own, so no nesting of the text is recursed through.</remarks>
    private static IEnumerable<(ParsingEvent Event, int Depth, bool InKey)> Nested(IParser parser)
    {
        var open = new Stack<YamlOpen>();

        // Whether the node read now, held by the innermost open container, stands in a key: a mapping's children are its
        // keys and values in turn, from a key.
        bool Placed()
        {
            if (!open.TryPeek(out var holder))
            {
                return false;
            }

            var key = holder.Mapping && holder.KeyNext;
            holder.KeyNext = !holder.KeyNext;
            return holder.InKey || key;
        }

        while (parser.MoveNext())
        {
            var current = parser.Current!;

            switch (current)
            {
                case MappingStart or SequenceStart:
                    var inKey = Placed();
                    open.Push(new YamlOpen(current is MappingStart, inKey));
                    yield return (current, open.Count, inKey);
                    break;

                case MappingEnd or SequenceEnd:
                    var closed = open.Pop();
                    yield return (current, open.Count + 1, closed.InKey);
                    break;

                case Scalar or AnchorAlias:
                    yield return (current, open.Count, Placed());
                    break;

                default:
                    yield return (current, open.Count, false);
                    break;
            }
        }
    }
}

/// <summary>A mapping or a list the events have opened and not yet closed (<see cref="YamlReading"/>'s count of nesting).</summary>
/// <param name="mapping">Whether it is a mapping, whose children are keys and values in turn.</param>
/// <param name="inKey">Whether it stands in a key: itself a key, or inside one.</param>
internal sealed class YamlOpen(bool mapping, bool inKey)
{
    /// <summary>Whether it is a mapping, whose children are keys and values in turn.</summary>
    public bool Mapping { get; } = mapping;

    /// <summary>Whether it stands in a key: itself a key, or inside one.</summary>
    public bool InKey { get; } = inKey;

    /// <summary>Whether a mapping's next child is a key; it begins with one.</summary>
    public bool KeyNext { get; set; } = true;
}

/// <summary>A text as its events measure it (<see cref="YamlReading.Load(TextReader, string, YamlRules?, out YamlStop?)"/>), before it is read into nodes.</summary>
/// <param name="Events">Its events, in order, as far as the text reads as YAML.</param>
/// <param name="Fault">What the parser threw where the text stopped reading as YAML; <see langword="null"/> where it read to its end.</param>
/// <param name="Depth">How deep it nests: its deepest container's depth, 1 for one at the top, 0 for a text of scalars.</param>
/// <param name="DeepestAt">Where its deepest container opens.</param>
/// <param name="Refusal">Where and why it is refused before it is read; <see langword="null"/> where it is read.</param>
internal sealed record YamlMeasured(IReadOnlyList<ParsingEvent> Events, ExceptionDispatchInfo? Fault, int Depth, Mark DeepestAt, YamlStop? Refusal);

/// <summary>
/// The events of a text scanned once (<see cref="YamlMeasured"/>), handed to the representation model as the parser
/// handed them: each in turn, then none - or, where the text stopped reading as YAML, the parser's own fault, thrown where
/// the parser threw it.
/// </summary>
/// <param name="events">The events, in order.</param>
/// <param name="fault">What the parser threw after the last of them; <see langword="null"/> where the text read to its end.</param>
internal sealed class YamlReplay(IReadOnlyList<ParsingEvent> events, ExceptionDispatchInfo? fault) : IParser
{
    private int _next;

    /// <inheritdoc/>
    public ParsingEvent? Current { get; private set; }

    /// <inheritdoc/>
    public bool MoveNext()
    {
        if (_next < events.Count)
        {
            Current = events[_next++];
            return true;
        }

        fault?.Throw();
        Current = null;
        return false;
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

/// <summary>Where and why a text is no document of one mapping (<see cref="YamlReading.Load(TextReader, string, YamlRules?, out YamlStop?)"/>).</summary>
/// <param name="Line">The line it is found at, from 1; 0 where it is the whole file's.</param>
/// <param name="Column">The column, from 1; 0 where it is the whole file's.</param>
/// <param name="Why">Why, as a sentence.</param>
public sealed record YamlStop(int Line, int Column, string Why);

/// <summary>
/// What a reader holds its files' YAML to before they are read into nodes (<see cref="YamlReading.Root"/>): the rules of
/// its own files, which no file of another kind is held to. Asked by a reader of files whose every part is meant to be
/// written out, as a workflow's definition is; the action reader asks none. No depth is among them: a file is read
/// however deep it nests.
/// </summary>
/// <param name="NoAlias">
/// Why the reader's files write no alias - <c>*name</c>, a merge key's included, which repeats a part of the file by
/// reference - completing the refusal of one after a semicolon, for instance <c>a definition writes each of its steps
/// out, so that what it says is what it reads</c>; <see langword="null"/> where an alias is read as the node its anchor
/// names.
/// </param>
public sealed record YamlRules(string? NoAlias);
