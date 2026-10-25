using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DssHarness.Core.Output;

namespace DssHarness.Core.Codebase;

/// <summary>What a step's context pack starts from: a reason step's <c>graphFrom</c>.</summary>
public enum ContextPackFrom
{
    /// <summary>What the step's prompt names, the working sets it is given and the files it writes.</summary>
    Task,

    /// <summary>What the work changed against its base and that change's impact set first, then what the task gives.</summary>
    Changes,
}

/// <summary>One node of a working set a step's pack is given: a seed of the weight of one named exactly, an edited one above the rest.</summary>
/// <param name="Id">The node's id.</param>
/// <param name="Edited">Whether the step that worked with it edited it.</param>
public sealed record ContextPackWorked(string Id, bool Edited);

/// <summary>What a step's context pack is made from, beside the view: its filled prompt, its read rule, and what the step says of its work.</summary>
/// <param name="Prompt">The step's filled prompt: the request, the plan item, each upstream output it reads.</param>
/// <param name="MayRead">Whether the step's read rule lets a file be read, by its path relative to the tree: a node of a file it does not is left out, never named, and counted.</param>
public sealed record ContextPackRequest(string Prompt, Func<string, bool> MayRead)
{
    /// <summary>What the pack starts from.</summary>
    public ContextPackFrom From { get; init; } = ContextPackFrom.Task;

    /// <summary>The pack's cap in estimated tokens (<see cref="EstimatedTokens"/>), from 0 to <see cref="CodebaseContextPack.MostTokens"/>; 0 gives no pack.</summary>
    public int Tokens { get; init; } = CodebaseContextPack.DefaultTokens;

    /// <summary>
    /// Whether the step's <c>writes</c> covers a file, by its path relative to the tree - the step's policy's own rule
    /// (<c>AgentPolicy.StepWrites</c>): the declarations of each file it covers are seeds. None where the step names no writes.
    /// </summary>
    public Func<string, bool> Writes { get; init; } = _ => false;

    /// <summary>The nodes of the working sets the step is given, in the order they are given.</summary>
    public IReadOnlyList<ContextPackWorked> Worked { get; init; } = [];

    /// <summary>
    /// What the tree holds otherwise than its base, where it was read: the files that differ (<see cref="ICodebaseGraphService.DifferingAsync"/>,
    /// a step of either kind) and, under <see cref="ContextPackFrom.Changes"/>, the declarations that lead the pack
    /// (<see cref="ICodebaseGraphService.ChangesAsync"/>).
    /// </summary>
    public CodebaseGraphChanges? Changes { get; init; }

    /// <summary>Why the tree's changes could not be read, where the pack starts from them and they could not; the pack is then made from the task, and its first line says why.</summary>
    public string? ChangesUnread { get; init; }
}

/// <summary>A step's context pack: the text given at the head of its first message, and what it was made of.</summary>
/// <param name="Text">The pack, line by line: its first line, the nodes, its last line; empty where the cap is 0, or too small to hold its first and last lines.</param>
/// <param name="Seeds">The nodes it was seeded from, each once, with why: what the step's records say it started from.</param>
/// <param name="Nodes">The nodes it lists, by id, each once, in the order it lists them.</param>
/// <param name="Lines">Each line it lists a node or a declaration taken out by, in order, as it writes it: what a delta gives of the pack a conversation was not given.</param>
/// <param name="Tokens">Its estimated tokens (<see cref="EstimatedTokens"/>).</param>
/// <param name="LeftOut">How many of its lines did not fit the cap - a node's, or a declaration's taken out - each left out whole.</param>
/// <param name="Refused">How many nodes the step's read rule left out.</param>
/// <param name="Near">How near each node is to the step's work, which the <c>graph</c> tool's <c>find</c> breaks a tie by.</param>
public sealed record ContextPack(string Text, IReadOnlyList<ContextPackSeed> Seeds, IReadOnlyList<string> Nodes, IReadOnlyList<ContextPackLine> Lines, int Tokens, int LeftOut, int Refused, GraphNearness Near);

/// <summary>One line of a pack that lists a node, or a declaration taken out.</summary>
/// <param name="Id">The node it lists; null for a declaration taken out of a file, as the base held it there.</param>
/// <param name="Text">The line as the pack writes it, its relation and marks with it.</param>
public sealed record ContextPackLine(string? Id, string Text);

/// <summary>One node a pack was seeded from.</summary>
/// <param name="Id">The node's id.</param>
/// <param name="Why">Why it is a seed: the heaviest of its seeds', as its line says it where no path reaches it with more - <c>named</c>, <c>edited before</c>, <c>matches the task</c>.</param>
public sealed record ContextPackSeed(string Id, string Why);

/// <summary>A text's estimated tokens: its UTF-8 bytes over <see cref="BytesPerToken"/>, rounded up - what bounds a pack, and what an answer's records say.</summary>
public static class EstimatedTokens
{
    /// <summary>The bytes one estimated token stands for.</summary>
    public const int BytesPerToken = 4;

    /// <summary>The estimated tokens of <paramref name="text"/>.</summary>
    /// <param name="text">A text.</param>
    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OfBytes(Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>The estimated tokens of a text of <paramref name="bytes"/> UTF-8 bytes.</summary>
    /// <param name="bytes">How many bytes.</param>
    public static int OfBytes(long bytes) => (int)((bytes + BytesPerToken - 1) / BytesPerToken);
}

/// <summary>
/// The context pack (value 370) and change-first (value 371): the part of the graph a step's work touches, made at its
/// start from the view and what the step says alone - so the same step on the same tree is given the same pack - and
/// written as lines within its cap. Its seeds are what the prompt names exactly (a symbol or a path in backquotes, a
/// qualified name, a handle, a <c>path:line</c>), the working sets it is given, the declarations of the files its
/// <c>writes</c> covers and the nodes its words match best; from each the walk goes <see cref="NamedHops"/> hops from a
/// node named or worked with and <see cref="OtherHops"/> from the others over the edges <see cref="Walked"/> names, each
/// node scored by its seed's weight halved at each hop and weighed by each edge's confidence, and not gone through along
/// a kind it is a hub of. Under <see cref="ContextPackFrom.Changes"/> the declarations the work changed lead, then their
/// tests, then the rest of their impact set, then what the task gives.
/// </summary>
public static partial class CodebaseContextPack
{
    /// <summary>The cap where a step says none: <c>graphPromptTokens</c>' default.</summary>
    public const int DefaultTokens = 3000;

    /// <summary>The highest cap a step may say.</summary>
    public const int MostTokens = 20000;

    /// <summary>How many of the nodes the prompt's words match best are seeds.</summary>
    public const int WordSeeds = 10;

    /// <summary>How many hops the walk goes from a node the prompt names, or a working set carries.</summary>
    public const int NamedHops = 2;

    /// <summary>How many hops the walk goes from every other seed: a file's declarations, a word match.</summary>
    public const int OtherHops = 1;

    /// <summary>The weight of a node the prompt names exactly, or a working set carries.</summary>
    public const double NamedWeight = 4;

    /// <summary>The weight of a node a working set says was edited: above every other seed.</summary>
    public const double EditedWeight = 5;

    /// <summary>The weight of each declaration of a file the prompt names.</summary>
    public const double FileWeight = 3;

    /// <summary>The weight of each declaration of a file the step's <c>writes</c> covers, and of each node a name several carry.</summary>
    public const double WrittenWeight = 2;

    /// <summary>The weight of a node the prompt's words match.</summary>
    public const double WordWeight = 2;

    /// <summary>The bytes the cap keeps for the last line, whatever it says.</summary>
    public const int LastLineRoom = 200;

    /// <summary>
    /// The edges a walk follows, each with the side it is followed from: <c>calls</c> both ways, the type relations
    /// (<c>inherits</c> and <c>mixesIn</c>, <c>implements</c>, <c>overrides</c>, <c>dispatches</c>) both ways, <c>tests</c>
    /// both ways, and <c>references</c> from a node to the types it names.
    /// </summary>
    public static IReadOnlyList<(GraphSide Side, string Kind)> Walked { get; } =
    [
        (GraphSide.In, CodeEdgeKinds.Calls),
        (GraphSide.Out, CodeEdgeKinds.Calls),
        (GraphSide.Out, CodeEdgeKinds.Inherits),
        (GraphSide.In, CodeEdgeKinds.Inherits),
        (GraphSide.Out, CodeEdgeKinds.MixesIn),
        (GraphSide.In, CodeEdgeKinds.MixesIn),
        (GraphSide.Out, CodeEdgeKinds.Implements),
        (GraphSide.In, CodeEdgeKinds.Implements),
        (GraphSide.Out, CodeEdgeKinds.Overrides),
        (GraphSide.In, CodeEdgeKinds.Overrides),
        (GraphSide.Out, CodeEdgeKinds.Dispatches),
        (GraphSide.In, CodeEdgeKinds.Dispatches),
        (GraphSide.In, CodeEdgeKinds.Tests),
        (GraphSide.Out, CodeEdgeKinds.Tests),
        (GraphSide.Out, CodeEdgeKinds.References),
    ];

    /// <summary>Makes the pack of <paramref name="request"/> from <paramref name="queries"/>' view.</summary>
    /// <param name="queries">The step's view, synced at its start.</param>
    /// <param name="request">The step's filled prompt and what it says of its work.</param>
    public static ContextPack Make(CodebaseGraphQueries queries, ContextPackRequest request)
    {
        ArgumentNullException.ThrowIfNull(queries);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Prompt);
        ArgumentNullException.ThrowIfNull(request.MayRead);
        ArgumentNullException.ThrowIfNull(request.Writes);
        ArgumentOutOfRangeException.ThrowIfNegative(request.Tokens);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(request.Tokens, MostTokens);

        return new Packing(queries, request).Make();
    }

    /// <summary>The weight an edge of <paramref name="confidence"/> carries a score by: <c>exact</c> 1, <c>unique</c> 0.7, <c>candidates</c> 0.3.</summary>
    /// <param name="confidence">One of <see cref="CodeConfidences.All"/>.</param>
    public static double Confidence(string confidence) => confidence switch
    {
        CodeConfidences.Exact => 1,
        CodeConfidences.Unique => 0.7,
        _ => 0.3,
    };

    /// <summary>
    /// How the node <paramref name="step"/> reaches stands to <paramref name="from"/>, the node it was reached from, as its
    /// line says it: <c>calls FoldAsync</c>, <c>tests FoldAsync</c>, <c>called by Run</c>.
    /// </summary>
    /// <param name="step">The edge followed.</param>
    /// <param name="from">The simple name of the node it was followed from.</param>
    public static string Phrase(GraphStep step, string from)
    {
        ArgumentNullException.ThrowIfNull(step);

        var verb = (step.Kind, step.Side) switch
        {
            (CodeEdgeKinds.Calls, GraphSide.In) => "calls",
            (CodeEdgeKinds.Calls, GraphSide.Out) => "called by",
            (CodeEdgeKinds.Inherits, GraphSide.Out) => "base of",
            (CodeEdgeKinds.Inherits, GraphSide.In) => "derives from",
            (CodeEdgeKinds.MixesIn, GraphSide.Out) => "mixed into",
            (CodeEdgeKinds.MixesIn, GraphSide.In) => "mixes in",
            (CodeEdgeKinds.Implements, GraphSide.Out) => "implemented by",
            (CodeEdgeKinds.Implements, GraphSide.In) => "implements",
            (CodeEdgeKinds.Overrides, GraphSide.Out) => "overridden by",
            (CodeEdgeKinds.Overrides, GraphSide.In) => "overrides",
            (CodeEdgeKinds.Dispatches, GraphSide.Out) => "answers",
            (CodeEdgeKinds.Dispatches, GraphSide.In) => "dispatches to",
            (CodeEdgeKinds.Tests, GraphSide.In) => "tests",
            (CodeEdgeKinds.Tests, GraphSide.Out) => "tested by",
            (CodeEdgeKinds.References, GraphSide.Out) => "named by",
            (_, GraphSide.Out) => $"{step.Kind} of",
            _ => step.Kind,
        };

        return $"{verb} {from}";
    }

    /// <summary>What a node has too many of, along one walked kind, for a walk to go through it that way: <c>callers</c>, <c>types it names</c>.</summary>
    private static string Many((GraphSide Side, string Kind) edge) => edge switch
    {
        (GraphSide.In, CodeEdgeKinds.Calls) => "callers",
        (GraphSide.Out, CodeEdgeKinds.Calls) => "callees",
        (GraphSide.Out, CodeEdgeKinds.Inherits) => "bases",
        (GraphSide.In, CodeEdgeKinds.Inherits) => "derived types",
        (GraphSide.Out, CodeEdgeKinds.MixesIn) => "mixins",
        (GraphSide.In, CodeEdgeKinds.MixesIn) => "types mixing it in",
        (GraphSide.Out, CodeEdgeKinds.Implements) => "interfaces",
        (GraphSide.In, CodeEdgeKinds.Implements) => "implementations",
        (GraphSide.Out, CodeEdgeKinds.Overrides) => "members it overrides",
        (GraphSide.In, CodeEdgeKinds.Overrides) => "overriding members",
        (GraphSide.Out, CodeEdgeKinds.Dispatches) => "members it dispatches to",
        (GraphSide.In, CodeEdgeKinds.Dispatches) => "members dispatching to it",
        (GraphSide.In, CodeEdgeKinds.Tests) => "tests",
        (GraphSide.Out, CodeEdgeKinds.Tests) => "members it tests",
        (GraphSide.Out, CodeEdgeKinds.References) => "types it names",
        _ => $"{edge.Kind} edges",
    };

    /// <summary>The making of one pack: its seeds, its walk, its sections, and the lines that fit.</summary>
    private sealed class Packing(CodebaseGraphQueries queries, ContextPackRequest request)
    {
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
        private readonly HashSet<string> _namedPaths = new(StringComparer.Ordinal);
        private readonly List<Seed> _seeds = [];
        private readonly Dictionary<string, Seed> _seeded = new(StringComparer.Ordinal);
        private readonly Dictionary<string, Reach> _best = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<((GraphSide Side, string Kind) Edge, int Degree)>> _crowds = new(StringComparer.Ordinal);

        public ContextPack Make()
        {
            Seeded();
            Walk();

            var sections = new List<Section>();
            var listed = new HashSet<string>(StringComparer.Ordinal);
            List<Section> changes = request.From == ContextPackFrom.Changes && request.Changes is { } read ? ChangeSections(read, listed) : [];

            sections.AddRange(changes);
            sections.Add(new Section(changes.Count > 0 ? "the task:" : null, TaskEntries(listed), ByFile: true));

            var first = FirstLine(changes.Count > 0 ? changes[0].Entries.Count : 0);
            var fitted = Fit(first, sections, out var leftOut);
            List<string> nodes = [.. (fitted ?? []).SelectMany(section => section.Entries).Select(entry => entry.Id).OfType<string>().Distinct(StringComparer.Ordinal)];
            var near = new GraphNearness(
                [.. nodes, .. request.Worked.Select(one => one.Id)],
                request.Writes,
                request.Changes?.Files ?? [],
                _namedPaths,
                Identifier().Matches(request.Prompt).Select(match => match.Value));
            List<ContextPackSeed> seeds = [.. _seeded.Values.OrderBy(seed => seed.Order).Select(seed => new ContextPackSeed(seed.Id, seed.Why))];

            if (fitted is null)
            {
                return new ContextPack(string.Empty, seeds, [], [], 0, leftOut, _refused.Count, near);
            }

            var body = new StringBuilder(first).Append('\n');

            foreach (var (header, entries, _) in fitted.Where(section => section.Entries.Count > 0))
            {
                if (header is not null)
                {
                    body.Append(header).Append('\n');
                }

                foreach (var entry in entries)
                {
                    body.Append(entry.Line).Append('\n');
                }
            }

            var said = body.ToString();
            var text = said + LastLine(nodes.Count, leftOut, EstimatedTokens.Of(said + LastLine(nodes.Count, leftOut, EstimatedTokens.Of(said))));
            List<ContextPackLine> lines = [.. fitted.SelectMany(section => section.Entries).Select(entry => new ContextPackLine(entry.Id, entry.Line))];
            return new ContextPack(text, seeds, nodes, lines, EstimatedTokens.Of(text), leftOut, _refused.Count, near);
        }

        /// <summary>Every seed, in the order the walk takes them: what the prompt names, the working sets, the files the step writes, the words' best matches.</summary>
        private void Seeded()
        {
            foreach (var (card, weight, hops, why, kind) in Named())
            {
                Seed(card, weight, hops, why, kind);
            }

            foreach (var worked in request.Worked.Where(one => queries.Holds(one.Id)))
            {
                Seed(queries.Card(worked.Id), worked.Edited ? EditedWeight : NamedWeight, NamedHops, worked.Edited ? "edited before" : "used before", SeedKind.Worked);
            }

            foreach (var path in queries.Paths.Where(request.Writes).Order(StringComparer.Ordinal))
            {
                foreach (var card in Declarations(path))
                {
                    Seed(card, WrittenWeight, OtherHops, "in a file the step writes", SeedKind.Written);
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                foreach (var card in queries.Find(request.Prompt, null, null, int.MaxValue, 0).Cards.Where(Declares).Where(Reads).Take(WordSeeds))
                {
                    Seed(card, WordWeight, OtherHops, "matches the task", SeedKind.Words);
                }
            }
        }

        /// <summary>
        /// A seed, where the step may read its node and no seed of it before goes as far with as much weight; the node is
        /// recorded by its heaviest seed, the first of equals - the one its own line is reached by.
        /// </summary>
        private void Seed(GraphCard card, double weight, int hops, string why, SeedKind kind)
        {
            if (!Reads(card) || (_seeded.TryGetValue(card.Id, out var had) && had.Weight >= weight && had.Hops >= hops))
            {
                return;
            }

            var seed = new Seed(card.Id, weight, hops, why, _seeds.Count, kind);
            _seeds.Add(seed);

            if (had is null || weight > had.Weight)
            {
                _seeded[card.Id] = seed;
            }
        }

        /// <summary>
        /// What the prompt names exactly, in the order it names them: each backquoted symbol or path, each
        /// <c>path:line</c> (a stack frame's and a compiler's among them), each qualified name and each handle the view holds.
        /// A name several nodes the step may read carry seeds each at <see cref="WrittenWeight"/>, where they are no more
        /// than an edge may name (<see cref="CodeConfidences.MostCandidates"/>); a file seeds its declarations.
        /// </summary>
        private IEnumerable<(GraphCard Card, double Weight, int Hops, string Why, SeedKind Kind)> Named()
        {
            var said = new List<(int At, string Text, bool Quoted)>();
            var quoted = new List<(int Start, int End)>();

            foreach (Match match in Quoted().Matches(request.Prompt))
            {
                said.Add((match.Index, match.Groups["text"].Value.Trim(), true));
                quoted.Add((match.Index, match.Index + match.Length));
            }

            bool InQuotes(Match match) => quoted.Any(span => match.Index >= span.Start && match.Index < span.End);

            foreach (Match match in AtLine().Matches(request.Prompt).Where(match => !InQuotes(match)))
            {
                said.Add((match.Index, match.Value, false));
            }

            foreach (Match match in QualifiedName().Matches(request.Prompt).Where(match => !InQuotes(match)))
            {
                said.Add((match.Index, match.Value, false));
            }

            foreach (Match match in Handle().Matches(request.Prompt).Where(match => !InQuotes(match)))
            {
                if (queries.Resolve(match.Value) is GraphResolve.Found found && found.Card.Handle == match.Value)
                {
                    said.Add((match.Index, match.Value, false));
                }
            }

            foreach (var (_, text, isQuoted) in said.OrderBy(one => one.At))
            {
                foreach (var seed in Resolved(text, isQuoted))
                {
                    yield return seed;
                }
            }
        }

        private IEnumerable<(GraphCard Card, double Weight, int Hops, string Why, SeedKind Kind)> Resolved(string text, bool quoted)
        {
            if (text.Length == 0)
            {
                return [];
            }

            if (AtLine().Match(text) is { Success: true } atLine && atLine.Length == text.Length)
            {
                if (Relative(atLine.Groups["path"].Value) is not { } path || !int.TryParse(atLine.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
                {
                    return [];
                }

                _namedPaths.Add(path);
                return queries.AtLine(path, line) is { } card ? [(card, NamedWeight, NamedHops, "named", SeedKind.Named)] : [];
            }

            if (!quoted)
            {
                return queries.Resolve(text) is GraphResolve.Found found ? [(found.Card, NamedWeight, NamedHops, "named", SeedKind.Named)] : [];
            }

            // A symbol written with its parameters names the symbol.
            var name = text.IndexOf('(', StringComparison.Ordinal) is > 0 and var paren && !CodebaseGraphQueries.NamesAFile(text) ? text[..paren].TrimEnd() : text;

            if (Relative(name) is { } file)
            {
                _namedPaths.Add(file);
                return [.. Declarations(file).Select(card => (card, FileWeight, OtherHops, "in a file named", SeedKind.File))];
            }

            return queries.Resolve(name) switch
            {
                GraphResolve.Found found => [(found.Card, NamedWeight, NamedHops, "named", SeedKind.Named)],
                GraphResolve.Candidates candidates when candidates.Cards.Where(Reads).ToList() is { Count: > 0 and <= CodeConfidences.MostCandidates } read
                    => [.. read.Select(card => (card, WrittenWeight, NamedHops, $"one of {Number(read.Count)} named '{name}'", SeedKind.Named))],
                _ => [],
            };
        }

        /// <summary>
        /// The path the view holds that <paramref name="written"/> names, with either separator: relative to the tree, the
        /// longest of its endings the view holds - one under the tree, or a stack frame's or a compiler's from another
        /// machine or checkout - or the one file the view holds that ends with it - a compiler's path relative to its
        /// project; null where it holds none, or several end with it.
        /// </summary>
        private string? Relative(string written)
        {
            if (queries.HeldPath(written) is { } held)
            {
                return held;
            }

            var parts = written.Split('/', '\\');

            if (Enumerable.Range(1, Math.Max(0, parts.Length - 1)).Select(at => queries.HeldPath(string.Join('/', parts[at..]))).FirstOrDefault(found => found is not null) is { } ending)
            {
                return ending;
            }

            var tail = "/" + string.Join('/', parts.Where(part => part.Length > 0));
            return tail.Length > 1 && queries.Paths.Where(path => path.EndsWith(tail, StringComparison.Ordinal)).Take(2).ToList() is [var only] ? only : null;
        }

        /// <summary>The declarations of the file at <paramref name="path"/> a seed is told by: its types, members and sections.</summary>
        private IEnumerable<GraphCard> Declarations(string path) => (queries.OutlineFile(path) ?? []).Where(Declares);

        private static bool Declares(GraphCard card) => CodeNodeKinds.Declarations.Contains(card.Kind, StringComparer.Ordinal);

        /// <summary>
        /// The walk from each seed, in order: each node reached keeps its best score - its seed's weight halved at each hop
        /// and weighed by each edge's confidence - and the relation to the node it was reached from on that path. A seed is
        /// walked from along every kind, as <c>related</c> walks from the node asked about; a node reached is not gone
        /// through along a kind it is a hub of, and a node the step may not read is not gone through at all.
        /// </summary>
        private void Walk()
        {
            foreach (var seed in _seeds)
            {
                Offer(new Reach(seed.Id, seed.Weight, seed.Order, 0, seed.Why, CodeConfidences.Exact));

                var reached = new Dictionary<string, double>(StringComparer.Ordinal) { [seed.Id] = seed.Weight };
                var layer = new SortedDictionary<string, double>(StringComparer.Ordinal) { [seed.Id] = seed.Weight };

                for (var hop = 1; hop <= seed.Hops && layer.Count > 0; hop++)
                {
                    var next = new SortedDictionary<string, double>(StringComparer.Ordinal);

                    foreach (var (from, score) in layer)
                    {
                        var name = queries.Card(from).Name;
                        HashSet<(GraphSide Side, string Kind)> crowded = hop == 1 ? [] : [.. Crowds(from).Select(crowd => crowd.Edge)];

                        foreach (var step in queries.Steps(from, Walked).Where(step => !crowded.Contains((step.Side, step.Kind))))
                        {
                            if (!Reads(queries.Card(step.To)))
                            {
                                continue;
                            }

                            var weighed = score * 0.5 * Confidence(step.Confidence);

                            if (reached.TryGetValue(step.To, out var had) && had >= weighed)
                            {
                                continue;
                            }

                            reached[step.To] = weighed;
                            next[step.To] = weighed;
                            Offer(new Reach(step.To, weighed, seed.Order, hop, Phrase(step, name), step.Confidence));
                        }
                    }

                    layer = next;
                }
            }
        }

        private void Offer(Reach reach)
        {
            if (!_best.TryGetValue(reach.Id, out var had) || Better(reach, had))
            {
                _best[reach.Id] = reach;
            }
        }

        private static bool Better(Reach one, Reach other)
            => one.Score != other.Score ? one.Score > other.Score
                : one.Order != other.Order ? one.Order < other.Order
                : one.Hop < other.Hop;

        /// <summary>Each walked kind <paramref name="id"/> is a hub of (<see cref="CodebaseGraphQueries.Crowd"/>), with how many edges of it it has.</summary>
        private IReadOnlyList<((GraphSide Side, string Kind) Edge, int Degree)> Crowds(string id)
        {
            if (!_crowds.TryGetValue(id, out var crowds))
            {
                crowds = _crowds[id] = [.. Walked.Select(edge => (Edge: edge, Degree: queries.Crowd(id, [edge]))).Where(one => one.Degree is not null).Select(one => (one.Edge, one.Degree!.Value))];
            }

            return crowds;
        }

        /// <summary>
        /// The sections change-first leads with: the declarations changed the step may read - each with where its lines
        /// differ, one moved between files said so, and those taken out as the base held them, what went with its container
        /// said by the container - then the tests of each and of each one's direct callers, then the rest of the impact
        /// set: callers to two hops, overrides both ways, implementations and dispatches, derived types; generated code
        /// after every other in each. None where no change the step may read was found.
        /// </summary>
        private List<Section> ChangeSections(CodebaseGraphChanges changes, HashSet<string> listed)
        {
            var changed = new List<Entry>();
            var held = new List<GraphCard>();
            var parts = new HashSet<(string Id, string Path)>();
            var removed = changes.Nodes.Where(node => node.Change == CodeChanges.Removed).ToList();
            var gone = removed.ToLookup(node => node.Id, StringComparer.Ordinal);
            var movedFrom = new HashSet<CodeChangedNode>(ReferenceEqualityComparer.Instance);

            foreach (var node in changes.Nodes.Where(node => node.Change != CodeChanges.Removed).OrderBy(node => node.Path, StringComparer.Ordinal).ThenBy(node => node.Start))
            {
                // A part of a node spread over files is listed for each file it changed in.
                if (!queries.Holds(node.Id) || queries.Part(node.Id, node.Path) is not { } card || !Reads(card) || !parts.Add((node.Id, node.Path)))
                {
                    continue;
                }

                if (listed.Add(card.Id))
                {
                    held.Add(card);
                }

                // A declaration added where the base held it in another file the step may read moved from there.
                var moved = node.Change == CodeChanges.Added ? gone[node.Id].FirstOrDefault(was => was.Path != node.Path && request.MayRead(was.Path) && !movedFrom.Contains(was)) : null;
                var said = moved is not null ? $"moved from {moved.Path}:{Number(moved.Start)}-{Number(moved.End)}"
                    : node.Change == CodeChanges.Added ? "added"
                    : Where(node.Lines);

                if (moved is not null)
                {
                    movedFrom.Add(moved);
                }

                changed.Add(new Entry(card.Id, $"{GraphLines.Of(card)} | {said}", card.Generated));
            }

            // Each declaration taken out and not said moved, as the base held it - one taken out with what it was declared
            // inside said by that alone - whether or not another file still declares a part of it.
            var told = removed.Where(node => !movedFrom.Contains(node)).Select(node => (node.Id, node.Path)).ToHashSet();

            foreach (var node in removed.Where(node => !movedFrom.Contains(node) && (node.Parent is not { } parent || !told.Contains((parent, node.Path)))).OrderBy(node => node.Path, StringComparer.Ordinal).ThenBy(node => node.Start))
            {
                if (!request.MayRead(node.Path))
                {
                    _refused.Add(node.Id);
                    continue;
                }

                changed.Add(new Entry(null, $"removed {node.Kind} {node.Qualified} | was {node.Path}:{Number(node.Start)}-{Number(node.End)} at {ReportText.Commit(changes.Base)}", false));
            }

            if (changed.Count == 0)
            {
                return [];
            }

            // A caller the step may not read is listed nowhere and gone through to nothing: its tests and its own callers
            // would be said as its, by its name.
            var callers = held.Select(card => (Card: card, Steps: Steps(card.Id, GraphRelations.Edges(GraphRelations.Callers)))).ToList();
            var readable = callers.SelectMany(one => one.Steps).Where(step => Readable(queries.Card(step.To))).ToList();
            var tests = held.SelectMany(card => Steps(card.Id, GraphRelations.Edges(GraphRelations.Tests)).Select(step => (step, card.Name)))
                .Concat(readable.SelectMany(caller => Steps(caller.To, GraphRelations.Edges(GraphRelations.Tests)).Select(step => (step, queries.Card(caller.To).Name))));
            var near = callers.SelectMany(one => one.Steps.Select(step => (step, one.Card.Name)));
            var far = readable.Where(step => queries.Crowd(step.To, GraphRelations.Edges(GraphRelations.Callers)) is null)
                .SelectMany(caller => Steps(caller.To, GraphRelations.Edges(GraphRelations.Callers)).Select(step => (step, queries.Card(caller.To).Name)));
            var overrides = held.SelectMany(card => Steps(card.Id, [(GraphSide.Out, CodeEdgeKinds.Overrides), (GraphSide.In, CodeEdgeKinds.Overrides)]).Select(step => (step, card.Name)));
            var implementations = held.SelectMany(card => Steps(card.Id, [.. GraphRelations.Edges(GraphRelations.Implementations), (GraphSide.In, CodeEdgeKinds.Dispatches)]).Select(step => (step, card.Name)));
            var derived = held.SelectMany(card => Steps(card.Id, GraphRelations.Edges(GraphRelations.Derived)).Select(step => (step, card.Name)));

            return
            [
                new Section("changed:", Generated(changed), ByFile: false),
                new Section("their tests:", Impact(tests, listed), ByFile: false),
                new Section("their impact:", [.. Impact(near, listed, mark: true), .. Impact(far, listed), .. Impact(overrides, listed), .. Impact(implementations, listed), .. Impact(derived, listed)], ByFile: false),
            ];
        }

        private IReadOnlyList<GraphStep> Steps(string id, IReadOnlyList<(GraphSide Side, string Kind)> edges) => queries.Steps(id, edges);

        /// <summary>
        /// One part of the impact set: each node not listed yet, the step may read, by file and line, generated code after
        /// every other, with its relation to the node it was reached from - and, for a direct caller, that it is a hub of
        /// callers not gone through, where it is.
        /// </summary>
        private List<Entry> Impact(IEnumerable<(GraphStep Step, string From)> reached, HashSet<string> listed, bool mark = false)
        {
            var entries = new List<(GraphCard Card, string Line)>();

            foreach (var (step, from) in reached)
            {
                var card = queries.Card(step.To);

                if (!Reads(card) || !listed.Add(card.Id))
                {
                    continue;
                }

                var hub = mark && queries.Crowd(card.Id, GraphRelations.Edges(GraphRelations.Callers)) is { } callers
                    ? $" | hub, not gone through its {Number(callers)} {Many((GraphSide.In, CodeEdgeKinds.Calls))}"
                    : string.Empty;
                entries.Add((card, $"{GraphLines.Of(card)} | {Related(Phrase(step, from), step.Confidence)}{hub}"));
            }

            return Generated([.. entries
                .OrderBy(entry => entry.Card.Path, StringComparer.Ordinal)
                .ThenBy(entry => entry.Card.Start)
                .ThenBy(entry => entry.Card.Id, StringComparer.Ordinal)
                .Select(entry => new Entry(entry.Card.Id, entry.Line, entry.Card.Generated))]);
        }

        /// <summary><paramref name="entries"/> in their order, generated code after every other.</summary>
        private static List<Entry> Generated(List<Entry> entries) => [.. entries.OrderBy(entry => entry.Generated ? 1 : 0)];

        /// <summary>
        /// What the task gives, best first: every node the walk reached and no section listed, generated code after every
        /// other, then by score, by the order of its seed, by hop and by id; a node reached that is a hub of a kind says
        /// which, a seed being walked from along every kind.
        /// </summary>
        private List<Entry> TaskEntries(HashSet<string> listed)
            => [.. _best.Values
                .Where(reach => !listed.Contains(reach.Id))
                .Select(reach => (Reach: reach, Card: queries.Card(reach.Id)))
                .OrderBy(one => one.Card.Generated ? 1 : 0)
                .ThenByDescending(one => one.Reach.Score)
                .ThenBy(one => one.Reach.Order)
                .ThenBy(one => one.Reach.Hop)
                .ThenBy(one => one.Reach.Id, StringComparer.Ordinal)
                .Select(one => new Entry(one.Card.Id, $"{GraphLines.Of(one.Card)} | {Related(one.Reach.Relation, one.Reach.Confidence)}{(one.Reach.Hop > 0 ? Hubbed(one.Card.Id) : string.Empty)}", one.Card.Generated))];

        /// <summary>A node's relation as its line says it: the edge's confidence where it is not exact.</summary>
        private static string Related(string relation, string confidence)
            => string.Equals(confidence, CodeConfidences.Exact, StringComparison.Ordinal) ? relation : $"{relation} | {confidence}";

        /// <summary>The kinds a node reached is a hub of, which the walk did not go through it along; nothing where it is none.</summary>
        private string Hubbed(string id)
            => Crowds(id) is { Count: > 0 } crowds
                ? $" | hub, not gone through its {string.Join(", ", crowds.Select(crowd => $"{Number(crowd.Degree)} {Many(crowd.Edge)}"))}"
                : string.Empty;

        /// <summary>
        /// The lines that fit the cap, section by section and best first, each left out whole where it does not - and the
        /// task's grouped by file then, each file where its best node stands, its nodes by line; null where the cap cannot
        /// hold the first and last lines, every line left out.
        /// </summary>
        private List<Section>? Fit(string first, List<Section> sections, out int leftOut)
        {
            var used = (long)Bytes(first) + LastLineRoom;
            leftOut = 0;

            if (request.Tokens == 0)
            {
                return null;
            }

            if (EstimatedTokens.OfBytes(used) > request.Tokens)
            {
                leftOut = sections.Sum(section => section.Entries.Count);
                return null;
            }

            var fitted = new List<Section>();

            foreach (var (header, entries, byFile) in sections)
            {
                var kept = new List<Entry>();

                foreach (var entry in entries)
                {
                    var cost = Bytes(entry.Line) + (kept.Count == 0 && header is not null ? Bytes(header) : 0);

                    if (EstimatedTokens.OfBytes(used + cost) > request.Tokens)
                    {
                        leftOut++;
                        continue;
                    }

                    used += cost;
                    kept.Add(entry);
                }

                fitted.Add(new Section(header, byFile ? ByFile(kept) : kept, byFile));
            }

            return fitted;
        }

        /// <summary>The task's nodes by file: each file where its best node stands, its nodes by line.</summary>
        private List<Entry> ByFile(List<Entry> kept)
        {
            var cards = kept.ToDictionary(entry => entry.Id!, entry => queries.Card(entry.Id!), StringComparer.Ordinal);
            var files = kept.Select(entry => cards[entry.Id!].Path).Distinct(StringComparer.Ordinal).ToList();

            return [.. kept
                .OrderBy(entry => files.IndexOf(cards[entry.Id!].Path))
                .ThenBy(entry => cards[entry.Id!].Start)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)];
        }

        /// <summary>
        /// The pack's first line: the view, its commit, how many files the step may read the tree holds otherwise than its
        /// base, and what the pack was seeded from - or, under change-first, how many lines the changes lead with, or why
        /// the task's seeds stand in for them.
        /// </summary>
        private string FirstLine(int changesLead)
        {
            var line = new StringBuilder($"codebase graph: view '{queries.View}' at {(queries.Head is { } head ? ReportText.Commit(head) : "no commit")}");

            if (request.Changes is { } read)
            {
                line.Append(CultureInfo.InvariantCulture, $", {ReportText.Counted(read.Files.Count(request.MayRead), "file")} differing from its base {ReportText.Commit(read.Base)}");
            }

            line.Append("; ");

            if (request.From == ContextPackFrom.Changes)
            {
                line.Append(changesLead > 0 ? $"packed from those changes first - {ReportText.Counted(changesLead, "declaration")} added, changed or taken out, their tests, then their impact - then from the task: "
                    : request.ChangesUnread is { } why ? $"its changes could not be read ({TextLines.OneLine(why).TrimEnd('.')}), so packed from the task: "
                    : request.Changes is null ? "no base to compare with, so packed from the task: "
                    : request.Changes.Files.Any(request.MayRead) ? "no declaration among them changed, so packed from the task: "
                    : "no change against its base, so packed from the task: ");
            }
            else
            {
                line.Append("packed from the task: ");
            }

            var parts = _seeded.Values.GroupBy(seed => seed.Kind).OrderBy(group => group.Key)
                .Select(group => Said(group.Key, group.Count()))
                .ToList();

            line.Append(parts.Count == 0 ? "nothing in it names or matches a node the view holds - ask the graph tool" : string.Join(", ", parts));
            return line.ToString();
        }

        private static string Said(SeedKind kind, int count) => kind switch
        {
            SeedKind.Named => ReportText.Counted(count, "node") + " it names",
            SeedKind.File => ReportText.Counted(count, "declaration") + " of the files it names",
            SeedKind.Worked => ReportText.Counted(count, "working set node"),
            SeedKind.Written => ReportText.Counted(count, "declaration") + " of the files the step writes",
            _ => "the " + ReportText.Counted(count, "best word match"),
        };

        /// <summary>The pack's last line: how many nodes it lists and its tokens, and how many lines were left out whole, and by the read rule.</summary>
        private string LastLine(int nodes, int leftOut, int tokens)
        {
            var line = $"{ReportText.Counted(nodes, "node")}, ~{Number(tokens)} of {Number(request.Tokens)} tokens; "
                + (leftOut == 0 ? "nothing left out" : $"{Number(leftOut)} more left out whole, past the cap");

            return _refused.Count == 0 ? line : $"{line}; {Number(_refused.Count)} in files the step may not read left out";
        }

        /// <summary>Whether the step may read <paramref name="card"/>'s file, a node it may not counted as left out.</summary>
        private bool Reads(GraphCard card)
        {
            if (Readable(card))
            {
                return true;
            }

            _refused.Add(card.Id);
            return false;
        }

        private bool Readable(GraphCard card) => request.MayRead(card.Path);

        private static string Where(IReadOnlyList<CodeLineHunk> hunks)
        {
            var changed = hunks.Where(hunk => hunk.Count > 0).Select(hunk => hunk.Count == 1 ? Number(hunk.Start) : $"{Number(hunk.Start)}-{Number(hunk.End)}").ToList();
            var taken = hunks.Where(hunk => hunk.Count == 0).Select(hunk => Number(hunk.Start - 1)).ToList();
            var said = new List<string>();

            if (changed.Count > 0)
            {
                said.Add($"changed at {string.Join(", ", changed)}");
            }

            if (taken.Count > 0)
            {
                said.Add($"lines taken out after {string.Join(", ", taken)}");
            }

            return said.Count == 0 ? "changed" : string.Join("; ", said);
        }

        private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

        private static int Bytes(string line) => Encoding.UTF8.GetByteCount(line) + 1;
    }

    /// <summary>A seed: a node, its weight, how many hops the walk goes from it, why it is one, where it stands among the seeds, and what kind of seed it is.</summary>
    private sealed record Seed(string Id, double Weight, int Hops, string Why, int Order, SeedKind Kind);

    /// <summary>What made a node a seed, in the order the first line says them.</summary>
    private enum SeedKind
    {
        Named,
        File,
        Worked,
        Written,
        Words,
    }

    /// <summary>A node the walk reached, as its best path reached it.</summary>
    private sealed record Reach(string Id, double Score, int Order, int Hop, string Relation, string Confidence);

    /// <summary>One line of the pack: the node it names, where it names one, and whether it is generated code.</summary>
    private sealed record Entry(string? Id, string Line, bool Generated);

    /// <summary>One section of the pack: the line it is headed by, where it has one, its lines best first, and whether they are written by file.</summary>
    private sealed record Section(string? Header, List<Entry> Entries, bool ByFile);

    [GeneratedRegex(@"`(?<text>[^`\r\n]+)`", RegexOptions.CultureInvariant)]
    private static partial Regex Quoted();

    [GeneratedRegex(@"(?<path>(?:[A-Za-z]:[\\/])?[\w.\-/\\]*\w\.\w+)(?::line (?<line>\d+)|:(?<line>\d+)(?:-\d+)?|\((?<line>\d+)(?:,\d+)?\))", RegexOptions.CultureInvariant)]
    private static partial Regex AtLine();

    [GeneratedRegex(@"(?<![\w.:\\/])[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)+(?![\w/\\])", RegexOptions.CultureInvariant)]
    private static partial Regex QualifiedName();

    [GeneratedRegex(@"(?<![\w-])[0-9a-f]{6,64}(?![\w-])", RegexOptions.CultureInvariant)]
    private static partial Regex Handle();

    [GeneratedRegex(@"[A-Za-z_]\w*", RegexOptions.CultureInvariant)]
    private static partial Regex Identifier();
}
