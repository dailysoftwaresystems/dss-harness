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
/// this place - at most <see cref="DataNodes.MostRepeated"/> visits, the alias itself one, and its merge keys read as the
/// keys of the mapping that merges them, one merged of a list of mappings too, a key the mapping writes itself winning;
/// what is counted rather than read is counted from what the text writes, never by visiting what an alias repeats
/// (<see cref="Counts"/>). A file of several documents
/// is each document a section by its place, <c>[0]</c> the first. A GitHub Actions workflow - a YAML file under
/// <c>.github/workflows/</c> - is read further as its jobs and its steps, each a <c>section</c> inside its holder: a step's
/// <c>uses:</c> that names a local action by a path of the repository an import of its <c>action.yml</c>, and a job's
/// <c>uses:</c> that names a reusable workflow of the repository an import of that workflow's file. Where the file stops
/// reading as YAML, or nests past <see cref="DataNodes.MostNesting"/> levels, what was read before is kept and the line is
/// marked dynamic.
/// </summary>
public sealed class YamlExtractor : ICodeGraphExtractor
{
    /// <summary>
    /// The most names a count of a file's keys compares to tell which keys of the mappings its merge keys merge are shadowed -
    /// by a key the merging mapping writes itself, or by an earlier mapping it merges - past which a key two of them hold is
    /// counted once for each and the place is marked: a few lines of merges of large mappings can make a count compare
    /// without end.
    /// </summary>
    internal const int MostCompared = 100 * FileExtract.MostKeys;

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
            $"what a file's aliases and merge keys repeat past {DataNodes.MostRepeated.ToString(CultureInfo.InvariantCulture)} visits - an alias, each node reached through one and each key a merge reads through one a visit, a workflow's reading of its jobs among them - counted from what the text writes, never read, the line marked dynamic; and what a file nests past {DataNodes.MostNesting.ToString(CultureInfo.InvariantCulture)} levels: not read, the line marked dynamic",
            $"which keys of the mappings a merge key merges are shadowed, past {MostCompared.ToString(CultureInfo.InvariantCulture)} names a file's count compares to tell it: from there on, a key both a mapping and what it merges hold, or two mappings it merges, is counted once for each, the line marked dynamic",
            "what follows the point a file stops reading as YAML - a template's {{ }} among it: what came before is read, the line it stops at marked dynamic",
            "a key's value past the cut of a summary",
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
    /// What reading one file's nodes shares: its lines, its nodes - whose budget of what the file repeats by reference
    /// (<see cref="DataNodes.Repeats"/>) every visit through an alias spends, a walk's and a workflow's reading of its jobs
    /// alike - and how many keys each node holds (<see cref="Counts"/>), which spends none of it.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="nodes">The file's nodes, whose budget of repeats is the file's.</param>
    private sealed class Reading(Lines lines, DataNodes nodes)
    {
        public Lines Lines => lines;

        public DataNodes Nodes => nodes;

        public Counts Counts { get; } = new(lines, nodes);
    }

    /// <summary>A file's documents, each by its place, as a walk of its keys reads them.</summary>
    private sealed class Documents(IReadOnlyList<YamlWritten> documents, Reading reading) : IDataValue
    {
        public bool Holds => true;

        public string? Text => null;

        public int End => documents.Max(document => new Node(document, null, reading).End);

        public bool Unread => false;

        public long Scalars => documents.Aggregate(0L, (count, document) => DataNodes.Sum(count, reading.Counts.Of(document)));

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
            => documents.Select((document, at) => (DataPlace.At(at), reading.Lines.Of(document.Start), (IDataValue)new Node(document, null, reading)));
    }

    /// <summary>
    /// A YAML node as a walk of its keys reads it: an alias as the node it names, every line where this place writes it -
    /// through an alias, the alias's own line, which <paramref name="over"/> holds for all it repeats. Each member it gives
    /// through an alias - an alias written here, what one repeats, a key a merge reads through one, one a key before it
    /// shadows among them - is a visit of what the file repeats (<see cref="DataNodes.Repeats"/>). Past the file's budget,
    /// what this place still holds through one is given as one member that is counted, never read (<see cref="Counted"/>):
    /// the rest of what an alias repeats, or of what a mapping merges, whole, and an alias written here alone - so a visit
    /// past the budget costs no more than its count.
    /// </summary>
    /// <param name="written">The node as the text writes it here.</param>
    /// <param name="over">The line of the alias it is reached through, where it is reached through one; null where written here.</param>
    /// <param name="reading">What reading the file's nodes shares.</param>
    private sealed class Node(YamlWritten written, int? over, Reading reading) : IDataValue
    {
        private readonly Lines _lines = reading.Lines;

        /// <summary>What the node is: an alias's node, or itself; null for an alias that names nothing or is not followed.</summary>
        private YamlWritten? Resolved => Resolve(written);

        /// <summary>
        /// The line of the alias the node and all it holds are reached through - an alias's own, where it is written here -
        /// which makes a visit of it one of what the file repeats; null where it is written here.
        /// </summary>
        private int? Over => over ?? (written.Kind == YamlWrittenKind.Alias ? _lines.Of(written.Start) : null);

        /// <summary>A container, or an alias not followed: what it repeats nests too deep to read, and none of it is.</summary>
        public bool Holds => written.Cut || Resolved?.Kind is YamlWrittenKind.Mapping or YamlWrittenKind.Sequence;

        public string? Text => Resolved?.Kind == YamlWrittenKind.Scalar ? Resolved.Value : null;

        public int End => Over ?? Last(written);

        public bool Unread => false;

        public long Scalars => reading.Counts.Of(written);

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members
        {
            get
            {
                var repeated = Over is not null;
                long given = 0;

                foreach (var visit in Visits())
                {
                    if (visit.Value.Over is not null && !reading.Nodes.Repeats(visit.Line))
                    {
                        // Past the budget, every later visit through a repeat is refused too: what is left of a container
                        // reached through one, or of a mapping's merged keys, is one member counted, and an alias written
                        // here is counted alone, the keys written beside it read on.
                        if (repeated || visit.Merged)
                        {
                            yield return (visit.Place, visit.Line, new Counted(visit.Line, Math.Max(0, Scalars - given)));
                            yield break;
                        }

                        given = DataNodes.Sum(given, visit.Value.Scalars);
                        yield return (visit.Place, visit.Line, new Counted(visit.Line, visit.Value.Scalars));
                        continue;
                    }

                    if (!visit.Shadowed)
                    {
                        given = DataNodes.Sum(given, visit.Value.Scalars);
                        yield return (visit.Place, visit.Line, visit.Value);
                    }
                }
            }
        }

        /// <summary>
        /// Each visit reading the node makes, as the text gives its members: a list's items; a mapping's own keys in the order
        /// written, then each merged mapping's (<see cref="MergesOf"/>) - its own merged ones among them - one of a name a key
        /// before it gave shadowed, the mapping's own winning, an earlier merged one over a later. Each is on the line this
        /// place writes it: where it is reached through an alias, the alias's.
        /// </summary>
        private IEnumerable<Visit> Visits()
        {
            if (written.Cut || Resolved is not { Cut: false } node)
            {
                yield break;
            }

            if (node.Kind == YamlWrittenKind.Sequence)
            {
                for (var at = 0; at < node.Children.Count; at++)
                {
                    yield return new Visit(DataPlace.At(at), Over ?? _lines.Of(node.Children[at].Start), new Node(node.Children[at], Over, reading), Merged: false, Shadowed: false);
                }

                yield break;
            }

            if (node.Kind != YamlWrittenKind.Mapping)
            {
                yield break;
            }

            var given = new HashSet<string>(StringComparer.Ordinal);

            foreach (var (key, value) in OwnKeys(node))
            {
                given.Add(key.Value!);
                yield return new Visit(DataPlace.Named(key.Value!), Over ?? _lines.Of(key.Start), new Node(value, Over, reading), Merged: false, Shadowed: false);
            }

            foreach (var (merged, list) in MergesOf(node))
            {
                var source = new Node(merged, Over ?? (list is null ? null : _lines.Of(list.Start)), reading);

                foreach (var visit in source.Visits())
                {
                    yield return visit with { Merged = true, Shadowed = visit.Shadowed || !given.Add(visit.Place.Name!) };
                }
            }
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

    /// <summary>One visit a node's reading makes: the member's place, its line, its node, whether a merge gave it, and whether a key before it of its name shadows it.</summary>
    /// <param name="Place">Its place.</param>
    /// <param name="Line">The line it is on, where this place writes it.</param>
    /// <param name="Value">Its node.</param>
    /// <param name="Merged">Whether a merge key gave it.</param>
    /// <param name="Shadowed">Whether a key before it of its name shadows it, so it is visited and given to no walk.</param>
    private readonly record struct Visit(DataPlace Place, int Line, Node Value, bool Merged, bool Shadowed);

    /// <summary>What a place holds through what the file repeats, past the file's budget: counted, never read (<see cref="IDataValue.Unread"/>).</summary>
    /// <param name="line">The line it stands on.</param>
    /// <param name="scalars">How many scalar keys it holds.</param>
    private sealed class Counted(int line, long scalars) : IDataValue
    {
        public bool Holds => false;

        public string? Text => null;

        public int End => line;

        public bool Unread => true;

        public long Scalars => scalars;

        public IEnumerable<(DataPlace Place, int Line, IDataValue Value)> Members => [];
    }

    /// <summary>What <paramref name="node"/> is: an alias's node, or itself; null for an alias that names nothing or is not followed.</summary>
    private static YamlWritten? Resolve(YamlWritten node) => node.Kind == YamlWrittenKind.Alias ? node.Target : node;

    /// <summary>Whether <paramref name="key"/> is a merge key: a plain <c>&lt;&lt;</c>, never a quoted one.</summary>
    private static bool IsMerge(YamlWritten key) => key is { Kind: YamlWrittenKind.Scalar, Plain: true, Value: "<<" };

    /// <summary>The keys <paramref name="mapping"/> writes itself, each with its value, in the order written: each a scalar, a merge key never.</summary>
    private static IEnumerable<(YamlWritten Key, YamlWritten Value)> OwnKeys(YamlWritten mapping)
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
    /// mapping, alias of one, or list of either, an alias of a list among them - each as it is written here, with the alias
    /// of its list where the list is one, whose line its keys are read on. A mapping merged twice is merged once: every key
    /// it gives the second time a key before it shadows. The one rule of what a merge merges, which a walk and a count read
    /// alike.
    /// </summary>
    private static IEnumerable<(YamlWritten Merged, YamlWritten? List)> MergesOf(YamlWritten mapping)
    {
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
                    yield return (value, null);
                }
            }
            else if (Resolve(value) is { Kind: YamlWrittenKind.Sequence, Cut: false } list)
            {
                foreach (var item in list.Children)
                {
                    if (Resolve(item) is { Kind: YamlWrittenKind.Mapping } merged && seen.Add(merged))
                    {
                        yield return (item, value.Kind == YamlWrittenKind.Alias ? value : null);
                    }
                }
            }
        }
    }

    /// <summary>
    /// How many scalar keys each node of a file holds at any depth, a scalar itself one: counted once a node from what the
    /// text writes, never by visiting what an alias or a merge repeats, so counting spends nothing of the budget a walk reads
    /// repeats by. A mapping that merges others holds its own keys and its merged mappings' - each as many as it gives, the
    /// first of a name it writes twice - but those of a name a key before gave: the mapping's own, then an earlier merged
    /// mapping's. Which names a merged mapping shares with the mapping that merges it, and with another it merges, is told by
    /// their names, compared at most <see cref="MostCompared"/> times a file: past that, a name both hold is counted once for
    /// each, and the line is marked.
    /// </summary>
    /// <param name="lines">The file's lines.</param>
    /// <param name="nodes">The file's nodes, where the mark is made.</param>
    private sealed class Counts(Lines lines, DataNodes nodes)
    {
        private readonly Dictionary<YamlWritten, (long All, long Merged)> _held = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<YamlWritten, Dictionary<string, long>?> _names = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<YamlWritten[], long> _merged = new(SameMappings.Instance);
        private long _compared;
        private bool _comparedPast;

        /// <summary>How many scalar keys <paramref name="written"/> holds at any depth, as written here: an alias its node's, one not followed none, one naming nothing one.</summary>
        public long Of(YamlWritten written) => written.Kind switch
        {
            YamlWrittenKind.Scalar => 1,
            YamlWrittenKind.Alias => written.Cut ? 0 : written.Target is { } target ? Of(target) : 1,
            _ => Held(written).All,
        };

        /// <summary>
        /// How many scalar keys the container <paramref name="node"/> holds - all of them, and those it gives a mapping that
        /// merges it, the first of each name alone - counted once a node.
        /// </summary>
        private (long All, long Merged) Held(YamlWritten node)
        {
            if (_held.TryGetValue(node, out var known))
            {
                return known;
            }

            (long All, long Merged) held = (0, 0);

            if (node is { Cut: false, Kind: YamlWrittenKind.Sequence })
            {
                var all = node.Children.Aggregate(0L, (count, child) => DataNodes.Sum(count, Of(child)));
                held = (all, all);
            }
            else if (node is { Cut: false, Kind: YamlWrittenKind.Mapping })
            {
                long own = 0;
                long again = 0;
                var names = new HashSet<string>(StringComparer.Ordinal);

                foreach (var (key, value) in OwnKeys(node))
                {
                    var count = Of(value);
                    own = DataNodes.Sum(own, count);

                    if (!names.Add(key.Value!))
                    {
                        again = DataNodes.Sum(again, count);
                    }
                }

                YamlWritten[] merged = [.. MergesOf(node).Select(merge => Resolve(merge.Merged)!)];
                var given = merged.Length == 0 ? 0 : Math.Max(0, Merged(merged, node) - Shadowed(names, merged, node));
                held = (DataNodes.Sum(own, given), DataNodes.Sum(own - again, given));
            }

            _held[node] = held;
            return held;
        }

        /// <summary>How many keys the mappings <paramref name="merged"/> give merged together, an earlier one's of a name two hold: counted once a list of them.</summary>
        private long Merged(YamlWritten[] merged, YamlWritten at)
        {
            if (merged.Length == 1)
            {
                return Held(merged[0]).Merged;
            }

            if (_merged.TryGetValue(merged, out var known))
            {
                return known;
            }

            long total = 0;
            long? shared = 0;

            for (var later = 0; later < merged.Length; later++)
            {
                total = DataNodes.Sum(total, Held(merged[later]).Merged);
                shared = later == 0 || shared is not { } before ? shared : Shared(merged, later, at) is { } more ? DataNodes.Sum(before, more) : null;
            }

            // Past the bound on comparing, no name two of them share is told: each is counted once for each.
            var count = shared is { } told ? Math.Max(0, total - told) : total;
            _merged[merged] = count;
            return count;
        }

        /// <summary>
        /// How many keys the mapping at <paramref name="later"/> of <paramref name="merged"/> gives of the names a mapping
        /// before it gives, read from whichever of the two holds fewer names; <see langword="null"/> past the bound on comparing.
        /// </summary>
        private long? Shared(YamlWritten[] merged, int later, YamlWritten at)
        {
            if (Names(merged[later], at) is not { } names)
            {
                return null;
            }

            var earlier = new List<Dictionary<string, long>>();

            foreach (var before in merged[..later])
            {
                if (Names(before, at) is not { } given)
                {
                    return null;
                }

                earlier.Add(given);
            }

            long shared = 0;

            if (names.Count <= earlier.Sum(given => (long)given.Count))
            {
                foreach (var (name, count) in names)
                {
                    if (!Compare(earlier.Count, at))
                    {
                        return null;
                    }

                    if (earlier.Any(given => given.ContainsKey(name)))
                    {
                        shared = DataNodes.Sum(shared, count);
                    }
                }

                return shared;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var name in earlier.SelectMany(given => given.Keys))
            {
                if (!Compare(1, at))
                {
                    return null;
                }

                if (seen.Add(name) && names.TryGetValue(name, out var count))
                {
                    shared = DataNodes.Sum(shared, count);
                }
            }

            return shared;
        }

        /// <summary>How many keys the mappings <paramref name="merged"/> give of the names <paramref name="own"/>, which the mapping merging them writes itself: the first of them to give each.</summary>
        private long Shadowed(HashSet<string> own, YamlWritten[] merged, YamlWritten at)
        {
            if (own.Count == 0)
            {
                return 0;
            }

            var indexes = new List<Dictionary<string, long>>();

            foreach (var mapping in merged)
            {
                if (Names(mapping, at) is not { } names)
                {
                    return 0;
                }

                indexes.Add(names);
            }

            long shadowed = 0;

            foreach (var name in own)
            {
                if (!Compare(indexes.Count, at))
                {
                    return 0;
                }

                if (indexes.FirstOrDefault(names => names.ContainsKey(name)) is { } giving)
                {
                    shadowed = DataNodes.Sum(shadowed, giving[name]);
                }
            }

            return shadowed;
        }

        /// <summary>
        /// Each name <paramref name="mapping"/> gives a mapping that merges it, with how many keys it gives of it - its own
        /// first, then its merged mappings' - made once a mapping; <see langword="null"/> past the bound on comparing.
        /// </summary>
        private Dictionary<string, long>? Names(YamlWritten mapping, YamlWritten at)
        {
            if (_names.TryGetValue(mapping, out var known))
            {
                return known;
            }

            var names = new Dictionary<string, long>(StringComparer.Ordinal);

            bool Gathered()
            {
                if (mapping.Cut)
                {
                    return true;
                }

                foreach (var (key, value) in OwnKeys(mapping))
                {
                    if (!Compare(1, at))
                    {
                        return false;
                    }

                    names.TryAdd(key.Value!, Of(value));
                }

                foreach (var (merged, _) in MergesOf(mapping))
                {
                    if (Names(Resolve(merged)!, at) is not { } inner)
                    {
                        return false;
                    }

                    foreach (var (name, count) in inner)
                    {
                        if (!Compare(1, at))
                        {
                            return false;
                        }

                        names.TryAdd(name, count);
                    }
                }

                return true;
            }

            var gathered = Gathered() ? names : null;
            _names[mapping] = gathered;
            return gathered;
        }

        /// <summary>Whether <paramref name="comparisons"/> more names may be compared, the line of <paramref name="at"/> marked the first time they may not.</summary>
        private bool Compare(int comparisons, YamlWritten at)
        {
            if (!_comparedPast && _compared <= MostCompared - comparisons)
            {
                _compared += comparisons;
                return true;
            }

            if (!_comparedPast)
            {
                _comparedPast = true;
                nodes.Stopped(lines.Of(at.Start), $"the file's merge keys compare more than {MostCompared.ToString(CultureInfo.InvariantCulture)} names, and from here on a key both a mapping and what it merges hold, or two mappings it merges, is counted once for each");
            }

            return false;
        }
    }

    /// <summary>Two lists of merged mappings alike: the same mappings, in the same order.</summary>
    private sealed class SameMappings : IEqualityComparer<YamlWritten[]>
    {
        public static SameMappings Instance { get; } = new();

        public bool Equals(YamlWritten[]? one, YamlWritten[]? other)
            => ReferenceEquals(one, other) || (one is not null && other is not null && one.Length == other.Length && one.Zip(other).All(pair => ReferenceEquals(pair.First, pair.Second)));

        public int GetHashCode(YamlWritten[] mappings)
        {
            var hash = new HashCode();

            foreach (var mapping in mappings)
            {
                hash.Add(System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(mapping));
            }

            return hash.ToHashCode();
        }
    }
}
