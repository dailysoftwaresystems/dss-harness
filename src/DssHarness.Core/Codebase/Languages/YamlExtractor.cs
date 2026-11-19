using System.Globalization;
using DssHarness.Core.Yaml;
using YamlDotNet.Core;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A YAML data file - <c>.yml</c> and <c>.yaml</c>, but a Dart package's <c>pubspec.yaml</c>, which its own extractor
/// reads - read as the data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): each member that
/// holds others a <c>section</c> over its lines inside the section that holds it, and each scalar key a <c>member</c>
/// SubKind <c>key</c> named by its path, its value as its summary - every one, however deep. What the file repeats by
/// reference is read once, where it is written: an alias is a key whose value is the alias, with a reference to the node
/// its anchor names; a mapping's merge key no key of its own, the mapping a reference to each mapping it merges, in order
/// - its keys, a key the mapping writes itself shadowing a merged one's, reached through that reference - and a mapping
/// written in the merge key's place read there, at <c>&lt;&lt;</c>. A file of several documents is each document a
/// section by its place, <c>[0]</c> the first. A GitHub Actions workflow - a YAML file under <c>.github/workflows/</c> -
/// has each of its steps' sections named by the step's <c>name:</c>, else by its place (<c>step 1</c>), a step's
/// <c>uses:</c> that names a local action by a path of the repository an import of its <c>action.yml</c>, and a job's
/// <c>uses:</c> that names a reusable workflow of the repository an import of that workflow's file - each job and step
/// read once, through every alias and merge that reaches it. Where the file stops reading as YAML, what was read before is
/// kept and the line is marked dynamic.
/// </summary>
public sealed class YamlExtractor : ICodeGraphExtractor
{
    /// <summary>The key a workflow names its jobs under.</summary>
    private const string Jobs = "jobs";

    /// <summary>The name a merge key merges by where it merges a mapping written in its own place.</summary>
    private const string MergeKey = "<<";

    /// <inheritdoc/>
    public string Language => "yaml";

    /// <inheritdoc/>
    public int Version => 1;

    /// <inheritdoc/>
    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".yml", ".yaml");

    /// <inheritdoc/>
    public bool Outlines => true;

    /// <inheritdoc/>
    public CodeGraphExtractorHelp Help { get; } = new(
        "YAML data files (.yml, .yaml): each scalar key anywhere in the file, however deep, as a key node named by its path with its value as its summary, each member that holds others as a section over its lines inside the one that holds it, an alias as a key whose value is the alias with a uses edge to the node its anchor names, a mapping with a merge key as a uses edge to each mapping it merges - what an anchor names read once, where it is written - each document of a file of several as a section by its place ([0] the first), a GitHub Actions workflow's steps named by their name:, a step's uses: naming a local action as an import of its action.yml, and a job's uses: naming a reusable workflow of the repository as an import of its file",
        [
            "pubspec.yaml, which the pubspec extractor reads",
            "a key reached only through an alias or a merge, as a node of its own where the alias is written: it is the anchor's node, which the uses edge reaches, and a key path written through the alias reaches it through that edge",
            "a merge key at a file's top, which no node holds: what it merges is reached by its keys' names alone",
            "a key that is no scalar - a list or a mapping written as a key: no node of it",
            "what follows the point a file stops reading as YAML - a template's {{ }} among it: what came before is read, the line it stops at marked dynamic",
            "a uses: that names an action or a workflow of a marketplace or of another repository by its owner: no import of it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = new Lines(CodeFileText.LineIndex(input.Text), input.Lines.Count, input.Text.Length);
        var read = YamlReading.Written(input.Text);
        var nodes = new DataNodes(Language, input.Path, input.Lines.Count);
        var imports = new List<ExtractImport>();
        var last = LastLines(read.Documents, lines);

        // A file of several documents is each document by its place; one of one, that document.
        IDataValue? root = read.Documents.Count switch
        {
            0 => null,
            1 => new Node(read.Documents[0], lines, last),
            _ => new Documents(read.Documents, lines, last),
        };

        if (root is not null)
        {
            var walk = nodes.Walk(root);

            if (read.Documents.Count == 1 && IsWorkflow(input.Path))
            {
                Workflow(input.Path, read.Documents[0], walk, nodes, imports, lines);
            }
        }

        foreach (var stop in read.Stops)
        {
            nodes.Stopped(lines.Of(stop.At), stop.Why);
        }

        return new FileExtract([.. nodes.Nodes])
        {
            References = nodes.References,
            Imports = imports,
            Dynamic = nodes.Dynamic,
        };
    }

    /// <summary>Whether <paramref name="path"/> is a GitHub Actions workflow: a YAML file under <c>.github/workflows/</c>.</summary>
    private static bool IsWorkflow(string path)
        => path.StartsWith(".github/workflows/", StringComparison.Ordinal) && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    /// <summary>
    /// A workflow's jobs and steps, each read once however many aliases and merges reach it: each step written in a job's
    /// list of steps has its section named by its <c>name:</c>, else <c>step</c> and its place; a step's <c>uses:</c>
    /// naming a local action an import of its <c>action.yml</c>, a job's naming a reusable workflow of the repository an
    /// import of its file, each <c>uses:</c> once, on the line it is written on, however many steps or jobs reach it.
    /// </summary>
    private static void Workflow(string file, YamlWritten root, DataWalk walk, DataNodes nodes, List<ExtractImport> imports, Lines lines)
    {
        if (Resolve(Member(root, Jobs)?.Value) is not { Kind: YamlWrittenKind.Mapping } jobs)
        {
            return;
        }

        var read = new HashSet<YamlWritten>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<YamlWritten>(ReferenceEqualityComparer.Instance);

        foreach (var (_, written) in Entries(jobs))
        {
            if (Resolve(written) is not { Kind: YamlWrittenKind.Mapping } job || !read.Add(job))
            {
                continue;
            }

            Used(file, Member(job, "uses"), workflow: true, used, imports, lines);

            if (Resolve(Member(job, "steps")?.Value) is not { Kind: YamlWrittenKind.Sequence } steps)
            {
                continue;
            }

            for (var at = 0; at < steps.Children.Count; at++)
            {
                if (Resolve(steps.Children[at]) is not { Kind: YamlWrittenKind.Mapping } step || !read.Add(step))
                {
                    continue;
                }

                // A step written where an alias names it elsewhere keeps the name of the place it is written at.
                if (steps.Children[at].Kind == YamlWrittenKind.Mapping && walk.Made.TryGetValue(step, out var section))
                {
                    nodes.Rename(section, Resolve(Member(step, "name")?.Value) is { Kind: YamlWrittenKind.Scalar, Value: { Length: > 0 } given } ? given : $"step {(at + 1).ToString(CultureInfo.InvariantCulture)}");
                }

                Used(file, Member(step, "uses"), workflow: false, used, imports, lines);
            }
        }
    }

    /// <summary>
    /// A <c>uses:</c> - a step's or a job's - that names a path of the repository: a job's an import of the reusable
    /// workflow's file it names, a step's of the local action's <c>action.yml</c>, with <c>action.yaml</c> standing in for
    /// it where that is the one the tree holds. A path of the repository is its root's, so the import names it from the
    /// workflow file's directory. A <c>uses:</c> already read - <paramref name="read"/> - is read again by none.
    /// </summary>
    private static void Used(string file, (YamlWritten Key, YamlWritten Value)? uses, bool workflow, HashSet<YamlWritten> read, List<ExtractImport> imports, Lines lines)
    {
        if (uses is not { } member || !read.Add(member.Key) || Resolve(member.Value) is not { Kind: YamlWrittenKind.Scalar, Value: { Length: > 1 } named } || named[0] != '.')
        {
            return;
        }

        var path = named.Split('@')[0];
        var line = lines.Of(member.Key.Start);

        if (workflow)
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, Rooted(file, CodeGraphIds.Clean(path)), line));
            return;
        }

        foreach (var (name, fallback) in new[] { ("action.yml", false), ("action.yaml", true) })
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, Rooted(file, CodeGraphIds.Clean($"{path}/{name}")), line) { Fallback = fallback });
        }
    }

    /// <summary>
    /// The path <paramref name="written"/> names - a path of the repository's root - as the file at
    /// <paramref name="file"/> writes one relative to its own directory: the linker resolves it against the file's
    /// directory, which takes the climb out.
    /// </summary>
    private static string Rooted(string file, string written)
    {
        var directory = file.Contains('/', StringComparison.Ordinal) ? file[..file.LastIndexOf('/')] : string.Empty;
        var up = directory.Length == 0 ? 0 : directory.Split('/').Length;
        return string.Concat(Enumerable.Repeat("../", up)) + written;
    }

    /// <summary>
    /// The key <paramref name="name"/> of <paramref name="mapping"/> with its value: the first the mapping writes itself,
    /// else the first of the mappings it merges, in order, theirs in turn - each mapping looked in once, however its merges
    /// come round, on a stack of its own; <see langword="null"/> where none holds it.
    /// </summary>
    private static (YamlWritten Key, YamlWritten Value)? Member(YamlWritten mapping, string name)
    {
        var seen = new HashSet<YamlWritten>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<YamlWritten>([mapping]);

        while (pending.TryPop(out var looked))
        {
            if (!seen.Add(looked))
            {
                continue;
            }

            foreach (var (key, value) in Entries(looked))
            {
                if (key.Value == name)
                {
                    return (key, value);
                }
            }

            // The mappings it merges, the first looked in first: the first pushed last.
            foreach (var (merged, _, _) in MergesOf(looked).Reverse())
            {
                pending.Push(merged);
            }
        }

        return null;
    }

    /// <summary>What <paramref name="node"/> is: an alias's node, or itself; null for an alias that names nothing.</summary>
    private static YamlWritten? Resolve(YamlWritten? node) => node?.Kind == YamlWrittenKind.Alias ? node.Target : node;

    /// <summary>Whether <paramref name="key"/> is a merge key: a plain <c>&lt;&lt;</c>, never a quoted one.</summary>
    private static bool IsMerge(YamlWritten key) => key is { Kind: YamlWrittenKind.Scalar, Plain: true, Value: MergeKey };

    /// <summary>The keys <paramref name="mapping"/> writes itself, each with its value, in the order written: each a scalar, a merge key never.</summary>
    private static IEnumerable<(YamlWritten Key, YamlWritten Value)> Entries(YamlWritten mapping)
    {
        for (var at = 0; at + 1 < mapping.Children.Count; at += 2)
        {
            if (mapping.Children[at] is { Kind: YamlWrittenKind.Scalar, Value: not null } key && !IsMerge(key))
            {
                yield return (key, mapping.Children[at + 1]);
            }
        }
    }

    /// <summary>
    /// The mappings <paramref name="mapping"/>'s merge keys merge, each once, in the order written - of each merge key's
    /// mapping, alias of one, or list of either, an alias of a list among them - each with the name it is merged by, an
    /// alias's anchor's, and the merge key or the alias it is merged on. The one rule of what a merge merges.
    /// </summary>
    private static IEnumerable<(YamlWritten Merged, string Name, YamlWritten At)> MergesOf(YamlWritten mapping)
    {
        if (mapping.Kind != YamlWrittenKind.Mapping)
        {
            yield break;
        }

        var seen = new HashSet<YamlWritten>(ReferenceEqualityComparer.Instance);

        for (var at = 0; at + 1 < mapping.Children.Count; at += 2)
        {
            if (!IsMerge(mapping.Children[at]))
            {
                continue;
            }

            var value = mapping.Children[at + 1];

            if (Resolve(value) is { Kind: YamlWrittenKind.Mapping } one)
            {
                if (seen.Add(one))
                {
                    yield return (one, value.Kind == YamlWrittenKind.Alias ? value.Value! : MergeKey, value.Kind == YamlWrittenKind.Alias ? value : mapping.Children[at]);
                }
            }
            else if (Resolve(value) is { Kind: YamlWrittenKind.Sequence } list)
            {
                foreach (var item in list.Children)
                {
                    if (Resolve(item) is { Kind: YamlWrittenKind.Mapping } merged && seen.Add(merged))
                    {
                        yield return (merged, item.Kind == YamlWrittenKind.Alias ? item.Value! : MergeKey, value.Kind == YamlWrittenKind.Alias ? value : item);
                    }
                }
            }
        }
    }

    /// <summary>
    /// The last line each node of <paramref name="documents"/> is written on: a scalar's or an alias's own, a flow
    /// container's closing bracket's, and a block container's - whose own end is where the next thing begins - the last of
    /// what it holds; each node told once, what it holds before it, in an order of the reader's own rather than the call's.
    /// </summary>
    private static Dictionary<YamlWritten, int> LastLines(IReadOnlyList<YamlWritten> documents, Lines lines)
    {
        // Each node before what it holds; read back from the end, what it holds before it.
        var order = new List<YamlWritten>();
        var pending = new Stack<YamlWritten>(documents);

        while (pending.TryPop(out var node))
        {
            order.Add(node);

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }

        var last = new Dictionary<YamlWritten, int>(ReferenceEqualityComparer.Instance);

        for (var at = order.Count - 1; at >= 0; at--)
        {
            var node = order[at];
            var start = lines.Of(node.Start);

            last[node] = node.Kind is YamlWrittenKind.Scalar or YamlWrittenKind.Alias || node.Flow ? Math.Max(start, lines.Before(node.End))
                : node.Children.Count == 0 ? start
                : Math.Max(start, node.Children.Max(child => last[child]));
        }

        return last;
    }

    /// <summary>The file's lines, as the core counts them: a mark of the reader's told as the line its character is on.</summary>
    /// <param name="index">Where the file's lines begin.</param>
    /// <param name="count">How many lines the file has.</param>
    /// <param name="length">How long its text is.</param>
    private sealed class Lines(CodeLineIndex index, int count, int length)
    {
        /// <summary>The line <paramref name="mark"/> begins on, within the file's.</summary>
        public int Of(Mark mark) => FileExtract.LineWithin(index.LineAt((int)Math.Clamp(mark.Index, 0, length)), count);

        /// <summary>The line the character before <paramref name="mark"/> is on: where what ends there ends.</summary>
        public int Before(Mark mark) => FileExtract.LineWithin(index.LineAt((int)Math.Clamp(mark.Index - 1, 0, length)), count);
    }

    /// <summary>A file's documents, each by its place, as a walk of its keys reads them.</summary>
    private sealed class Documents(IReadOnlyList<YamlWritten> documents, Lines lines, Dictionary<YamlWritten, int> last) : IDataValue
    {
        public bool Holds => true;

        public string? Text => null;

        public int End => documents.Max(document => last[document]);

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
            => documents.Select((document, at) => (DataPlace.At(at), lines.Of(document.Start), (IDataValue)new Node(document, lines, last)));
    }

    /// <summary>
    /// A YAML node as a walk of its keys reads it, where the text writes it: an alias as a reference to the node its anchor
    /// names, never that node again; a mapping's merge keys as a reference to each mapping they merge, a mapping written in
    /// a merge key's place a member there, at <c>&lt;&lt;</c>, and a list written there of what it holds that is written in
    /// it alone - its aliases are what the mapping merges, and a list of aliases alone no member at all.
    /// </summary>
    /// <param name="written">The node as the text writes it.</param>
    /// <param name="lines">The file's lines.</param>
    /// <param name="last">The last line each node of the file is written on.</param>
    /// <param name="merging">Whether it is a list a merge key holds, whose aliases are merged and no members of it.</param>
    private sealed class Node(YamlWritten written, Lines lines, Dictionary<YamlWritten, int> last, bool merging = false) : IDataValue
    {
        public bool Holds => written.Kind is YamlWrittenKind.Mapping or YamlWrittenKind.Sequence;

        public string? Text => written.Kind == YamlWrittenKind.Scalar ? written.Value : null;

        public int End => last[written];

        public object Identity => written;

        public (object? Target, string Name)? Alias => written.Kind == YamlWrittenKind.Alias ? (written.Target, written.Value!) : null;

        public IEnumerable<(object Target, string Name, int Line)> Merges
            => MergesOf(written).Select(merge => ((object)merge.Merged, merge.Name, lines.Of(merge.At.Start)));

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
        {
            get
            {
                if (written.Kind == YamlWrittenKind.Sequence)
                {
                    for (var at = 0; at < written.Children.Count; at++)
                    {
                        if (!merging || written.Children[at].Kind != YamlWrittenKind.Alias)
                        {
                            yield return (DataPlace.At(at), lines.Of(written.Children[at].Start), new Node(written.Children[at], lines, last));
                        }
                    }

                    yield break;
                }

                if (written.Kind != YamlWrittenKind.Mapping)
                {
                    yield break;
                }

                for (var at = 0; at + 1 < written.Children.Count; at += 2)
                {
                    if (written.Children[at] is not { Kind: YamlWrittenKind.Scalar, Value: { } name } key)
                    {
                        continue;
                    }

                    var value = written.Children[at + 1];

                    // A merge key is no key: what it holds written in its place is read there, and what an alias names is
                    // merged by reference alone.
                    if (!IsMerge(key))
                    {
                        yield return (DataPlace.Named(name), lines.Of(key.Start), new Node(value, lines, last));
                    }
                    else if (value.Kind == YamlWrittenKind.Mapping || (value.Kind == YamlWrittenKind.Sequence && value.Children.Any(item => item.Kind != YamlWrittenKind.Alias)))
                    {
                        yield return (DataPlace.Named(MergeKey), lines.Of(key.Start), new Node(value, lines, last, merging: value.Kind == YamlWrittenKind.Sequence));
                    }
                }
            }
        }
    }
}
