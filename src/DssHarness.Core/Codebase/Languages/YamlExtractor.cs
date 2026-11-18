using System.Globalization;
using DssHarness.Core.Yaml;
using YamlDotNet.Core;

namespace DssHarness.Core.Codebase.Languages;

/// <summary>
/// A YAML data file - <c>.yml</c> and <c>.yaml</c>, but a Dart package's <c>pubspec.yaml</c>, which its own extractor
/// reads - read as the data it holds, its nodes made as every data file's are (<see cref="DataNodes"/>): each top-level
/// member that holds others a <c>section</c> over its lines, and each scalar key anywhere down to
/// <see cref="DataNodes.MostDepth"/> places of its path a <c>member</c> SubKind <c>key</c> named by its path, its value as
/// its summary. Its aliases are followed - a key reached through one on the alias's line, where the key is written into
/// this place - at most <see cref="DataNodes.MostRepeated"/> nodes, and its merge keys read as the keys of the mapping that
/// merges them, one merged of a list of mappings too, a key the mapping writes itself winning. A file of several documents
/// is each document a section by its place, <c>[0]</c> the first. A GitHub Actions workflow - a YAML file under
/// <c>.github/workflows/</c> - is read further as its jobs and its steps, each a <c>section</c> inside its holder: a step's
/// <c>uses:</c> that names a local action by a path of the repository an import of its <c>action.yml</c>, and a job's
/// <c>uses:</c> that names a reusable workflow of the repository an import of that workflow's file. Where the file stops
/// reading as YAML, or nests past <see cref="DataNodes.MostNesting"/> levels, what was read before is kept and the line is
/// marked dynamic.
/// </summary>
public sealed class YamlExtractor : ICodeGraphExtractor
{
    /// <summary>The key a workflow names its jobs under.</summary>
    private const string Jobs = "jobs";

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
        "YAML data files (.yml, .yaml, anchors and aliases followed and merge keys read): each scalar key anywhere in the file as a key node named by its path with its value as its summary, each top-level member that holds others as a section over its lines, each document of a file of several as a section by its place ([0] the first), a GitHub Actions workflow's jobs and steps each as a section, a step's uses: naming a local action as an import of its action.yml, and a job's uses: naming a reusable workflow of the repository as an import of its file",
        [
            "pubspec.yaml, which the pubspec extractor reads",
            $"a key nested past {DataNodes.MostDepth.ToString(CultureInfo.InvariantCulture)} places of its path, and a file of more than {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} keys and sections: the first {FileExtract.MostKeys.ToString(CultureInfo.InvariantCulture)} are kept, the rest counted, and an answer about the file says how many",
            $"what aliases repeat past {DataNodes.MostRepeated.ToString(CultureInfo.InvariantCulture)} nodes, and what a file nests past {DataNodes.MostNesting.ToString(CultureInfo.InvariantCulture)} levels: counted or not read, the line marked dynamic",
            "what follows the point a file stops reading as YAML - a template's {{ }} among it: what came before is read, the line it stops at marked dynamic",
            "a key's value past the cut of a summary; a secret a key holds is kept as the file writes it, as reading the file shows it, and an answer masks only the run's own secrets, as every tool's answer does",
            "a uses: that names an action or a workflow of a marketplace or of another repository by its owner: no import of it",
        ]);

    /// <inheritdoc/>
    public FileExtract Extract(CodeFileInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var lines = new Lines(CodeFileText.LineIndex(input.Text), input.Lines.Count, input.Text.Length);
        var read = YamlReading.Written(input.Text, DataNodes.MostNesting);
        var nodes = new DataNodes(Language, input.Path, input.Lines.Count);
        var imports = new List<ExtractImport>();
        var reading = new Reading(lines, nodes);

        // A file of several documents is each document by its place; one of one, that document.
        IDataValue? root = read.Documents.Count switch
        {
            0 => null,
            1 => new Node(read.Documents[0], null, reading),
            _ => new Documents(read.Documents, reading),
        };

        if (root is not null)
        {
            var sections = nodes.Walk(root);

            if (read.Documents.Count == 1 && root.Holds && IsWorkflow(input.Path) && sections.TryGetValue(Jobs, out var jobs))
            {
                Workflow(input.Path, root, jobs, nodes, imports, lines);
            }
        }

        foreach (var stop in read.Stops)
        {
            nodes.Stopped(lines.Of(stop.At), stop.Why);
        }

        return new FileExtract([.. nodes.Nodes])
        {
            Imports = imports,
            KeysBeyond = nodes.Beyond,
            Dynamic = nodes.Dynamic,
        };
    }

    /// <summary>Whether <paramref name="path"/> is a GitHub Actions workflow: a YAML file under <c>.github/workflows/</c>.</summary>
    private static bool IsWorkflow(string path)
        => path.StartsWith(".github/workflows/", StringComparison.Ordinal) && (path.EndsWith(".yml", StringComparison.Ordinal) || path.EndsWith(".yaml", StringComparison.Ordinal));

    /// <summary>
    /// A workflow's jobs and steps: each job a <c>section</c> over its lines inside the <c>jobs</c> section, each of its steps
    /// a section inside it - each where the text writes it, an alias by its own line - a step's <c>uses:</c> naming a local
    /// action an import of its <c>action.yml</c>, a job's naming a reusable workflow of the repository an import of its file.
    /// </summary>
    private void Workflow(string file, IDataValue root, string jobsSection, DataNodes nodes, List<ExtractImport> imports, Lines lines)
    {
        if (root.Members.FirstOrDefault(member => member.Place.Name == Jobs) is not { Value.Holds: true } held)
        {
            return;
        }

        foreach (var (place, jobLine, job) in held.Value.Members)
        {
            if (place.Name is not { Length: > 0 } name || !job.Holds)
            {
                continue;
            }

            var jobEnd = Math.Max(jobLine, job.End);
            var jobPath = DataNodes.PathOf([DataPlace.Named(Jobs), place]);

            if (nodes.Section(name, jobPath, jobLine, jobEnd, jobsSection) is not { } jobSection)
            {
                return;
            }

            var members = job.Members.ToList();
            Used(file, members, workflow: true, imports);

            if (members.FirstOrDefault(member => member.Place.Name == "steps") is not { Value.Holds: true } steps)
            {
                continue;
            }

            var items = steps.Value.Members.ToList();

            for (var at = 0; at < items.Count; at++)
            {
                if (!items[at].Value.Holds)
                {
                    continue;
                }

                var start = items[at].Line;
                var end = at + 1 < items.Count ? Math.Max(start, items[at + 1].Line - 1) : Math.Max(start, jobEnd);
                var step = items[at].Value.Members.ToList();
                var named = step.FirstOrDefault(member => member.Place.Name == "name").Value?.Text is { Length: > 0 } given ? given : $"step {(at + 1).ToString(CultureInfo.InvariantCulture)}";

                _ = nodes.Section(named, DataNodes.PathOf([DataPlace.Named(Jobs), place, DataPlace.Named("steps"), DataPlace.At(at)]), start, Math.Min(end, jobEnd), jobSection);
                Used(file, step, workflow: false, imports);
            }
        }
    }

    /// <summary>
    /// A <c>uses:</c> among <paramref name="members"/> - a step's or a job's - that names a path of the repository: a job's an
    /// import of the reusable workflow's file it names, a step's of the local action's <c>action.yml</c>, with
    /// <c>action.yaml</c> standing in for it where that is the one the tree holds. A path of the repository is its root's,
    /// so the import names it from the workflow file's directory.
    /// </summary>
    private static void Used(string file, IReadOnlyList<(DataPlace Place, int Line, IDataValue Value)> members, bool workflow, List<ExtractImport> imports)
    {
        if (members.FirstOrDefault(member => member.Place.Name == "uses") is not { Value.Text: { Length: > 1 } used } uses || used[0] != '.')
        {
            return;
        }

        var path = used.Split('@')[0];

        if (workflow)
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, Rooted(file, CodeGraphIds.Clean(path)), uses.Line));
            return;
        }

        foreach (var (name, fallback) in new[] { ("action.yml", false), ("action.yaml", true) })
        {
            imports.Add(new ExtractImport(CodeImportForms.Relative, Rooted(file, CodeGraphIds.Clean($"{path}/{name}")), uses.Line) { Fallback = fallback });
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

    /// <summary>
    /// What reading one file's nodes shares: its lines, how many scalar keys each node holds - counted once a node - and the
    /// budget of what the file repeats by reference (<see cref="DataNodes.Repeats"/>), which a merge key's reading spends
    /// as a walk's does.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="nodes">The file's nodes, whose budget of repeats is the file's.</param>
    private sealed class Reading(Lines lines, DataNodes nodes)
    {
        public Lines Lines => lines;

        public DataNodes Nodes => nodes;

        public Dictionary<YamlWritten, long> Scalars { get; } = new(ReferenceEqualityComparer.Instance);
    }

    /// <summary>A file's documents, each by its place, as a walk of its keys reads them.</summary>
    private sealed class Documents(IReadOnlyList<YamlWritten> documents, Reading reading) : IDataValue
    {
        public bool Holds => true;

        public string? Text => null;

        public int End => documents.Max(document => new Node(document, null, reading).End);

        public bool Repeated => false;

        public long Scalars => documents.Sum(document => new Node(document, null, reading).Scalars);

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
            => documents.Select((document, at) => (DataPlace.At(at), reading.Lines.Of(document.Start), (IDataValue)new Node(document, null, reading)));
    }

    /// <summary>
    /// A YAML node as a walk of its keys reads it: an alias as the node it names, every line where this place writes it -
    /// through an alias, the alias's own line, which <paramref name="over"/> holds for all it repeats.
    /// </summary>
    /// <param name="written">The node as the text writes it here.</param>
    /// <param name="over">The line of the alias it is reached through, where it is reached through one; null where written here.</param>
    /// <param name="reading">What reading the file's nodes shares.</param>
    private sealed class Node(YamlWritten written, int? over, Reading reading) : IDataValue
    {
        private readonly Lines _lines = reading.Lines;

        /// <summary>What the node is: an alias's node, or itself; null for an alias that names nothing or is not followed.</summary>
        private YamlWritten? Resolved => written.Kind == YamlWrittenKind.Alias ? written.Target : written;

        /// <summary>The line of the alias the node and all it holds are reached through, where it is one.</summary>
        private int? Over => over ?? (written.Kind == YamlWrittenKind.Alias ? _lines.Of(written.Start) : null);

        /// <summary>A container, or an alias not followed: what it repeats nests too deep to read, and none of it is.</summary>
        public bool Holds => written.Cut || Resolved?.Kind is YamlWrittenKind.Mapping or YamlWrittenKind.Sequence;

        public string? Text => Resolved?.Kind == YamlWrittenKind.Scalar ? Resolved.Value : null;

        public int End => Over ?? Last(written);

        public bool Repeated => over is not null;

        public long Scalars => written is { Kind: YamlWrittenKind.Alias, Cut: true } ? 0 : Resolved is not { } node ? 1 : Count(node);

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
        {
            get
            {
                if (written.Cut || Resolved is not { Cut: false } node)
                {
                    yield break;
                }

                if (node.Kind == YamlWrittenKind.Sequence)
                {
                    for (var at = 0; at < node.Children.Count; at++)
                    {
                        yield return (DataPlace.At(at), Over ?? _lines.Of(node.Children[at].Start), new Node(node.Children[at], Over, reading));
                    }

                    yield break;
                }

                if (node.Kind != YamlWrittenKind.Mapping)
                {
                    yield break;
                }

                foreach (var member in Effective(node, Over))
                {
                    yield return member;
                }
            }
        }

        /// <summary>
        /// The keys of <paramref name="mapping"/>, as a mapping that merges others reads: its own, in the order written, then
        /// each merged mapping's - of a merge key's mapping or list of mappings, in order - but one of a name a key before it
        /// gave, the mapping's own winning, an earlier merged one over a later. Each key a merge reads - one it gives, and one
        /// a key before it shadows - is a visit of what the file repeats (<see cref="DataNodes.Repeats"/>): a few lines of
        /// merges of large mappings stand for many keys, and past the file's budget none is read.
        /// </summary>
        private IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Effective(YamlWritten mapping, int? repeated)
        {
            var given = new HashSet<string>(StringComparer.Ordinal);
            var merged = new List<(YamlWritten Source, int? Over)>();

            for (var at = 0; at + 1 < mapping.Children.Count; at += 2)
            {
                var (key, value) = (mapping.Children[at], mapping.Children[at + 1]);

                if (key is { Kind: YamlWrittenKind.Scalar, Plain: true, Value: "<<" })
                {
                    merged.AddRange(Merged(value, repeated));
                    continue;
                }

                if (key.Kind != YamlWrittenKind.Scalar || key.Value is not { } name)
                {
                    continue;
                }

                given.Add(name);
                yield return (DataPlace.Named(name), repeated ?? _lines.Of(key.Start), new Node(value, repeated, reading));
            }

            foreach (var (source, through) in merged)
            {
                foreach (var member in new Node(source, through, reading).Members)
                {
                    if (!reading.Nodes.Repeats(member.Line))
                    {
                        yield break;
                    }

                    if (member.Place.Name is { } name && given.Add(name))
                    {
                        yield return member;
                    }
                }
            }
        }

        /// <summary>The mappings a merge key's value merges: a mapping, an alias of one, or a list of either, each with the line of the alias it is reached through.</summary>
        private IEnumerable<(YamlWritten Source, int? Over)> Merged(YamlWritten value, int? repeated)
        {
            var through = repeated ?? (value.Kind == YamlWrittenKind.Alias ? _lines.Of(value.Start) : null);
            var resolved = value.Kind == YamlWrittenKind.Alias ? value.Target : value;

            if (resolved?.Kind == YamlWrittenKind.Mapping)
            {
                yield return (value, repeated);
            }
            else if (resolved?.Kind == YamlWrittenKind.Sequence && !resolved.Cut)
            {
                foreach (var item in resolved.Children)
                {
                    if ((item.Kind == YamlWrittenKind.Alias ? item.Target : item)?.Kind == YamlWrittenKind.Mapping)
                    {
                        yield return (item, through);
                    }
                }
            }
        }

        /// <summary>How many scalar keys <paramref name="node"/> holds at any depth, merged keys among them - a scalar itself one: counted once a node, so a count of what aliases repeat costs what the text writes.</summary>
        private long Count(YamlWritten node)
        {
            if (node.Kind == YamlWrittenKind.Scalar)
            {
                return 1;
            }

            if (reading.Scalars.TryGetValue(node, out var known))
            {
                return known;
            }

            long count = 0;

            foreach (var member in new Node(node, null, reading).Members)
            {
                var more = member.Value.Scalars;
                count = more > long.MaxValue - count ? long.MaxValue : count + more;
            }

            reading.Scalars[node] = count;
            return count;
        }

        /// <summary>
        /// The last line <paramref name="node"/> is written on: a scalar's or an alias's own, a flow container's closing
        /// bracket's, and a block container's - whose own end is where the next thing begins - the last of what it holds.
        /// </summary>
        private int Last(YamlWritten node)
        {
            if (node.Kind is YamlWrittenKind.Scalar or YamlWrittenKind.Alias || node.Flow || node.Cut)
            {
                return Math.Max(_lines.Of(node.Start), _lines.Before(node.End));
            }

            return node.Children.Count == 0 ? _lines.Of(node.Start) : Math.Max(_lines.Of(node.Start), node.Children.Max(Last));
        }
    }
}
