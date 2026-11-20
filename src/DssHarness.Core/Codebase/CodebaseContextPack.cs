using System.Globalization;
using System.Text.RegularExpressions;
using DssHarness.Core.Output;
using DssHarness.Core.Text;

namespace DssHarness.Core.Codebase;

/// <summary>What a step's context pack starts from: a reason step's <c>graphFrom</c>.</summary>
public enum ContextPackFrom
{
    /// <summary>What the step's prompt names, the working sets it is given and the files it writes.</summary>
    Task,

    /// <summary>What the work changed against its base and that change's impact set first, then what the task gives.</summary>
    Changes,
}

/// <summary>
/// One node of a working set a step's pack is given: a seed of the weight of one named exactly, an edited one above the
/// rest, each weighed by how often the work touched it.
/// </summary>
/// <param name="Id">The node's id.</param>
/// <param name="Edited">Whether the step that worked with it edited it.</param>
public sealed record ContextPackWorked(string Id, bool Edited)
{
    /// <summary>
    /// How many times the work touched it, every working set it is given in counted, at least once: each touch past the
    /// first raises its weight by a part of what is left of one, so a node touched more weighs more, and an edited one
    /// stays above every one that was not.
    /// </summary>
    public int Uses { get; init; } = 1;
}

/// <summary>
/// One site or home of a class of the work item a step's prompt reads the classes of: the path it stands at, the
/// declaration it is - empty where the site is a whole file, a configuration entry or a literal - and whether it is
/// the class's one home.
/// </summary>
/// <param name="Path">The path the site stands at, relative to the tree.</param>
/// <param name="Symbol">The declaration at the site; null or empty where the site names a file, an entry or a literal.</param>
/// <param name="Home">Whether the site is the class's one home.</param>
public sealed record ContextPackClosure(string Path, string? Symbol, bool Home);

/// <summary>What a step's context pack is made from, beside the view: its filled prompt, its read rule, and what the step says of its work.</summary>
/// <param name="Prompt">The step's filled prompt: the request, the plan item, each upstream output it reads.</param>
/// <param name="MayRead">Whether the step's read rule lets a file be read, by its path relative to the tree: a node of a file it does not is left out, never named, and counted.</param>
public sealed record ContextPackRequest(string Prompt, Func<string, bool> MayRead)
{
    /// <summary>What the pack starts from.</summary>
    public ContextPackFrom From { get; init; } = ContextPackFrom.Task;

    /// <summary>
    /// The estimated tokens of a page of the pack (<see cref="EstimatedTokens"/>): page 1 is what the step's first message
    /// holds, and every later page is the pack going on, at the same size; 0 gives no pack.
    /// </summary>
    public int Tokens { get; init; } = CodebaseContextPack.DefaultTokens;

    /// <summary>
    /// Whether the step may ask the graph tool for the pack's later pages - its graph is <c>tools</c> - which the pack's
    /// last line then says how to; where it may not, the line says that the rest is on pages a step with the tool reaches.
    /// </summary>
    public bool Asks { get; init; } = true;

    /// <summary>
    /// Whether the step's <c>writes</c> covers a file, by its path relative to the tree - the step's policy's own rule
    /// (<c>AgentPolicy.StepWrites</c>): the declarations of each file it covers are seeds. None where the step names no writes.
    /// </summary>
    public Func<string, bool> Writes { get; init; } = _ => false;

    /// <summary>The nodes of the working sets the step is given, in the order they are given.</summary>
    public IReadOnlyList<ContextPackWorked> Worked { get; init; } = [];

    /// <summary>
    /// Every site and home of the classes of the work item the step goes on with, where its prompt reads them: each
    /// site's declaration a seed of the weight of a named symbol - its file's declarations at a named file's, where it
    /// names no symbol or its file carries none by that name - so a fresh agent's pack holds every site's declaration,
    /// not only those near the defect. Empty for a step whose prompt reads no classes.
    /// </summary>
    public IReadOnlyList<ContextPackClosure> Closure { get; init; } = [];

    /// <summary>
    /// What the tree holds otherwise than its base, where it was read: the files that differ (<see cref="ICodebaseGraphService.DifferingAsync"/>,
    /// a step of either kind) and, under <see cref="ContextPackFrom.Changes"/>, the declarations that lead the pack
    /// (<see cref="ICodebaseGraphService.ChangesAsync"/>).
    /// </summary>
    public CodebaseGraphChanges? Changes { get; init; }

    /// <summary>Why the tree's changes could not be read, where the pack starts from them and they could not; the pack is then made from the task, and its first line says why.</summary>
    public string? ChangesUnread { get; init; }
}

/// <summary>
/// A step's context pack: its page 1 - the text given at the head of its first message - what it was made of, and the
/// pages after it, laid from the same walk as they are asked for (<see cref="Page"/>).
/// </summary>
/// <param name="Text">Page 1, line by line: its first line, the nodes, its last line; empty where the pack's page size is 0, or too small to hold its first and last lines.</param>
/// <param name="Seeds">The nodes it was seeded from, each once, with why: what the step's records say it started from.</param>
/// <param name="Nodes">The nodes page 1 lists, by id, each once, in the order it lists them.</param>
/// <param name="Lines">Each line page 1 lists a node or a declaration taken out by, in order, as it writes it: what a delta gives of the pack a conversation was not given.</param>
/// <param name="Tokens">Page 1's estimated tokens (<see cref="EstimatedTokens"/>).</param>
/// <param name="Refused">How many nodes the step's read rule left out of the walk that page 1 was laid from.</param>
/// <param name="Near">How near each node is to the step's work, which the <c>graph</c> tool's <c>find</c> breaks a tie by.</param>
public sealed record ContextPack(string Text, IReadOnlyList<ContextPackSeed> Seeds, IReadOnlyList<string> Nodes, IReadOnlyList<ContextPackLine> Lines, int Tokens, int Refused, GraphNearness Near)
{
    /// <summary>The pack's pages, page 1 among them; null where it has none.</summary>
    internal GraphTextPages? Paged { get; init; }

    /// <summary>Whether a page follows page 1: the pack goes on past what the first message holds.</summary>
    public bool More => Paged?.Page(1) is { Last: false };

    /// <summary>
    /// Page <paramref name="page"/> of the pack, from 1: laid by going on with the walk page 1 was laid from, as far as it
    /// needs; null where the pack ends before it, or has no page at all.
    /// </summary>
    /// <param name="page">The page asked, from 1.</param>
    public GraphTextPage? Page(int page) => Paged?.Page(page);

    /// <summary>How many pages the pack holds, where every one is laid; null while it may go on past those laid.</summary>
    public int? Pages => Paged?.Known;
}

/// <summary>One line of a pack that lists a node, or a declaration taken out.</summary>
/// <param name="Id">The node it lists; null for a declaration taken out of a file, as the base held it there.</param>
/// <param name="Text">The line as the pack writes it, its relation and marks with it.</param>
public sealed record ContextPackLine(string? Id, string Text);

/// <summary>One node a pack was seeded from.</summary>
/// <param name="Id">The node's id.</param>
/// <param name="Why">Why it is a seed: the heaviest of its seeds', as its line says it where no path reaches it with more - <c>named</c>, <c>edited before</c>, <c>matches the task</c>.</param>
public sealed record ContextPackSeed(string Id, string Why);

/// <summary>A text's estimated tokens: its UTF-8 bytes over <see cref="BytesPerToken"/>, rounded up - what sizes a pack's page, and what an answer's records say.</summary>
public static class EstimatedTokens
{
    /// <summary>The bytes one estimated token stands for.</summary>
    public const int BytesPerToken = 4;

    /// <summary>The estimated tokens of <paramref name="text"/>.</summary>
    /// <param name="text">A text.</param>
    public static int Of(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return OfBytes(System.Text.Encoding.UTF8.GetByteCount(text));
    }

    /// <summary>The estimated tokens of a text of <paramref name="bytes"/> UTF-8 bytes.</summary>
    /// <param name="bytes">How many bytes.</param>
    public static int OfBytes(long bytes) => (int)((bytes + BytesPerToken - 1) / BytesPerToken);
}

/// <summary>
/// The context pack (value 370) and change-first (value 371): the part of the graph a step's work touches, made at its
/// start from the view and what the step says alone - so the same step on the same tree is given the same pack - and
/// given in pages of the step's tokens, best first, page 1 in its first message and every later page asked of the graph
/// tool. Its seeds are what the prompt names exactly (a symbol or a path in backquotes, a qualified name, a handle, a
/// <c>path:line</c>), every site and home of the classes of the work item its prompt reads the classes of
/// (<see cref="ContextPackRequest.Closure"/>, each site's declaration at the weight of a named symbol, its file's at a
/// named file's where it names none the file carries), the working sets it is given, the declarations of the files its
/// <c>writes</c> covers and every node its words match, each weighed by how well it matches. From all of them at once a
/// walk goes best first over the edges <see cref="Walked"/> names, as far as the pages asked for need and with no count
/// of hops: each node is reached once, at its best score - its seed's weight halved at each edge, weighed by the edge's
/// confidence and shared among the neighbours of its kind the node it was reached from has, so a hub is gone through and
/// its many neighbours are ranked down by how many they are, never cut. Under <see cref="ContextPackFrom.Changes"/> the
/// declarations the work changed lead, then their impact set walked the same way from them - their tests first - then
/// what the task gives.
/// </summary>
public static partial class CodebaseContextPack
{
    /// <summary>The size of a page where a step says none: <c>graphPromptTokens</c>' default.</summary>
    public const int DefaultTokens = 3000;

    /// <summary>The weight of a node the prompt names exactly, or a working set carries.</summary>
    public const double NamedWeight = 4;

    /// <summary>The weight of a node a working set says was edited, and of a declaration the work changed: above every other seed.</summary>
    public const double EditedWeight = 5;

    /// <summary>The weight of each declaration of a file the prompt names.</summary>
    public const double FileWeight = 3;

    /// <summary>The weight of each declaration of a file the step's <c>writes</c> covers, and of each node a name several carry.</summary>
    public const double WrittenWeight = 2;

    /// <summary>The weight of the node the prompt's words match best; every other match weighs its score's part of it.</summary>
    public const double WordWeight = 2;

    /// <summary>The bytes a page keeps for its last line, whatever it says (<see cref="GraphTextPages.LastLineRoom"/>).</summary>
    public const int LastLineRoom = GraphTextPages.LastLineRoom;

    /// <summary>What a section of a pack's change-first lines that lists the files that could not be compared is headed by.</summary>
    public const string NotCompared = "could not be compared:";

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

    /// <summary>
    /// The edges a change's impact set is walked over from the declarations it changed: their tests, their callers, the
    /// members they override and that override them, their implementations and what dispatches to them, and their
    /// derived types - each from the node reached, as far as the pages asked for need.
    /// </summary>
    public static IReadOnlyList<(GraphSide Side, string Kind)> Impacted { get; } =
    [
        .. GraphRelations.Edges(GraphRelations.Tests),
        .. GraphRelations.Edges(GraphRelations.Callers),
        (GraphSide.Out, CodeEdgeKinds.Overrides),
        (GraphSide.In, CodeEdgeKinds.Overrides),
        .. GraphRelations.Edges(GraphRelations.Implementations),
        (GraphSide.In, CodeEdgeKinds.Dispatches),
        .. GraphRelations.Edges(GraphRelations.Derived),
    ];

    /// <summary>Makes the pack of <paramref name="request"/> from <paramref name="queries"/>' view: its page 1, and the rest as it is asked for.</summary>
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

    /// <summary>
    /// How much of a node's score an edge carries on to a node it reaches, before the share of the neighbours of its kind:
    /// half, as one more step from the work; a change's tests carry it whole, so they lead its impact.
    /// </summary>
    private static double Carried(string kind, bool impact) => impact && string.Equals(kind, CodeEdgeKinds.Tests, StringComparison.Ordinal) ? 1 : 0.5;

    /// <summary>The making of one pack: its seeds, its walks, and its pages, laid as they are asked for.</summary>
    private sealed class Packing(CodebaseGraphQueries queries, ContextPackRequest request)
    {
        private readonly HashSet<string> _refused = new(StringComparer.Ordinal);
        private readonly HashSet<string> _namedPaths = new(StringComparer.Ordinal);
        private readonly List<Seed> _seeds = [];
        private readonly Dictionary<string, Seed> _seeded = new(StringComparer.Ordinal);

        /// <summary>The nodes a line of the pack lists already: a later walk goes through each and never lists it again.</summary>
        private readonly HashSet<string> _listed = new(StringComparer.Ordinal);

        /// <summary>The caption the task's lines are under: none where the pack holds no change.</summary>
        private string? _taskCaption;

        public ContextPack Make()
        {
            Seeded();

            var changed = request.From == ContextPackFrom.Changes && request.Changes is { } read ? Changed(read) : null;
            var uncompared = Uncompared();
            var first = FirstLine(changed?.Lines.Count ?? 0, uncompared.Count);

            _taskCaption = changed is { Lines.Count: > 0 } ? "the task:" : null;

            List<ContextPackSeed> seeds = [.. _seeded.Values.OrderBy(seed => seed.Order).Select(seed => new ContextPackSeed(seed.Id, seed.Why))];
            var page = request.Tokens == 0 ? null : new GraphTextPages(Lines(uncompared, changed), request.Tokens, number => number == 1 ? first : $"codebase graph: context pack page {Number(number)}", Last, ByFile);
            var one = page?.Page(1);
            List<string> nodes = [.. (one?.Lines ?? []).Select(line => line.Id).OfType<string>().Distinct(StringComparer.Ordinal)];
            var near = new GraphNearness(
                [.. nodes, .. request.Worked.Select(worked => worked.Id)],
                request.Writes,
                request.Changes?.Files ?? [],
                _namedPaths,
                Identifier().Matches(request.Prompt).Select(match => match.Value));

            if (one is null)
            {
                return new ContextPack(string.Empty, seeds, [], [], 0, _refused.Count, near);
            }

            List<ContextPackLine> lines = [.. one.Lines.Where(line => !string.Equals(line.Caption, NotCompared, StringComparison.Ordinal)).Select(line => new ContextPackLine(line.Id, line.Text))];
            return new ContextPack(one.Text, seeds, nodes, lines, one.Tokens, _refused.Count, near) { Paged = page };
        }

        /// <summary>
        /// Every line of the pack, in the order its pages give them, read as far as the pages asked for need: each differing
        /// file that could not be compared, then the declarations the work changed, then their impact set best first, then
        /// what the task gives best first - each node listed once, by the first that reaches it.
        /// </summary>
        private IEnumerable<GraphTextLine> Lines(IReadOnlyList<CodeUncomparedFile> uncompared, (List<Entry> Lines, List<GraphCard> Held)? changed)
        {
            foreach (var file in uncompared)
            {
                yield return new GraphTextLine(NotCompared, $"{ReportText.Printable(file.Path)} ({TextLines.OneLine(file.Why)})");
            }

            if (changed is { Lines.Count: > 0 } change)
            {
                foreach (var entry in change.Lines)
                {
                    yield return new GraphTextLine("changed:", entry.Line, entry.Id);
                }

                // Their impact set is walked twice, the same walk each time: the tests it reaches first, under a section of
                // their own, then every other node it reaches.
                IEnumerable<Reach> Impact() => BestFirst(change.Held.Select((card, order) => new Reach(card.Id, EditedWeight, order, 0, "changed", CodeConfidences.Exact)), Impacted, impact: true);

                foreach (var line in Section("their tests:", Impact().Where(reach => reach.Kind == CodeEdgeKinds.Tests)))
                {
                    yield return line;
                }

                foreach (var line in Section("their impact:", Impact().Where(reach => reach.Kind is not null and not CodeEdgeKinds.Tests)))
                {
                    yield return line;
                }
            }

            var seeds = _seeds.Select(seed => new Reach(seed.Id, seed.Weight, seed.Order, 0, seed.Why, CodeConfidences.Exact));

            foreach (var line in Section(_taskCaption, BestFirst(seeds, Walked, impact: false)))
            {
                yield return line;
            }
        }

        /// <summary>
        /// One section's lines from a walk best first: each node it reaches that no line listed yet, in the order the walk
        /// reaches it, generated code after every other - held back until the walk has given the rest.
        /// </summary>
        private IEnumerable<GraphTextLine> Section(string? caption, IEnumerable<Reach> walk)
        {
            var generated = new List<GraphTextLine>();

            foreach (var reach in walk)
            {
                if (!_listed.Add(reach.Id))
                {
                    continue;
                }

                var card = queries.Card(reach.Id);
                var line = new GraphTextLine(caption, $"{GraphLines.Of(card)} | {Related(reach.Relation, reach.Confidence)}", card.Id);

                if (card.Generated)
                {
                    generated.Add(line);
                    continue;
                }

                yield return line;
            }

            foreach (var line in generated)
            {
                yield return line;
            }
        }

        /// <summary>
        /// The walk from <paramref name="sources"/> over <paramref name="edges"/>, best first, as far as it is read: each node
        /// once, at its best score - its source's weight, at each edge carried on by <see cref="Carried"/>, weighed by the
        /// edge's confidence and shared among the nodes of that edge's kind the node it was reached from reaches, so a hub's
        /// many neighbours are ranked down by how many they are and never cut. Ties go to the earlier source, then the fewer
        /// edges, then the id. A node the step may not read is not gone through at all, and is counted. It keeps what it has
        /// reached and never reaches it again: linear in the edges it follows, its cycles among them, with no count of hops.
        /// </summary>
        private IEnumerable<Reach> BestFirst(IEnumerable<Reach> sources, IReadOnlyList<(GraphSide Side, string Kind)> edges, bool impact)
        {
            var queue = new PriorityQueue<Reach, Reach>(ReachOrder.Instance);
            var best = new Dictionary<string, Reach>(StringComparer.Ordinal);
            var settled = new HashSet<string>(StringComparer.Ordinal);

            void Offer(Reach reach)
            {
                if (settled.Contains(reach.Id) || (best.TryGetValue(reach.Id, out var had) && ReachOrder.Instance.Compare(reach, had) >= 0))
                {
                    return;
                }

                best[reach.Id] = reach;
                queue.Enqueue(reach, reach);
            }

            foreach (var source in sources)
            {
                Offer(source);
            }

            while (queue.TryDequeue(out var reach, out _))
            {
                // A reach a better one came after is passed over: a node is settled once, at its best.
                if (!settled.Add(reach.Id))
                {
                    continue;
                }

                yield return reach;

                var name = queries.Card(reach.Id).Name;
                var steps = queries.Steps(reach.Id, edges);
                var degrees = steps.GroupBy(step => (step.Side, step.Kind)).ToDictionary(kind => kind.Key, kind => kind.Select(step => step.To).Distinct(StringComparer.Ordinal).Count());

                foreach (var step in steps)
                {
                    if (settled.Contains(step.To) || !Reads(queries.Card(step.To)))
                    {
                        continue;
                    }

                    var score = reach.Score * Carried(step.Kind, impact) * Confidence(step.Confidence) / degrees[(step.Side, step.Kind)];
                    Offer(new Reach(step.To, score, reach.Order, reach.Hop + 1, Phrase(step, name), step.Confidence) { Kind = step.Kind });
                }
            }
        }

        /// <summary>
        /// Every seed, in the order the walk takes them: what the prompt names, the classes' sites and homes, the working sets,
        /// the files the step writes, and every node the prompt's words match, each weighed by its score's part of the best's.
        /// </summary>
        private void Seeded()
        {
            foreach (var (card, weight, why, kind) in Named())
            {
                Seed(card, weight, why, kind);
            }

            foreach (var closure in request.Closure)
            {
                foreach (var (card, weight, why) in AtSite(closure))
                {
                    Seed(card, weight, why, SeedKind.Closure);
                }
            }

            foreach (var worked in request.Worked.Where(one => queries.Holds(one.Id)))
            {
                // Weighed by use: each touch past the first adds a part of what is left of one, never a whole one, so an
                // edited node stays above every node that was not.
                var weight = (worked.Edited ? EditedWeight : NamedWeight) + 1 - (1.0 / Math.Max(1, worked.Uses));
                Seed(queries.Card(worked.Id), weight, worked.Edited ? "edited before" : "used before", SeedKind.Worked);
            }

            foreach (var path in queries.Paths.Where(request.Writes).Order(StringComparer.Ordinal))
            {
                foreach (var card in Declarations(path))
                {
                    Seed(card, WrittenWeight, "in a file the step writes", SeedKind.Written);
                }
            }

            if (!string.IsNullOrWhiteSpace(request.Prompt))
            {
                var matched = queries.Scored(request.Prompt).Where(scored => Declares(scored.Card) && Reads(scored.Card)).ToList();
                var best = matched.Count == 0 ? 0 : matched.Max(scored => scored.Score);

                foreach (var (card, score) in matched)
                {
                    Seed(card, WordWeight * score / best, "matches the task", SeedKind.Words);
                }
            }
        }

        /// <summary>
        /// A seed, where the step may read its node and no seed of it before weighs as much; the node is recorded by its
        /// heaviest seed, the first of equals - the one its own line is reached by.
        /// </summary>
        private void Seed(GraphCard card, double weight, string why, SeedKind kind)
        {
            if (!Reads(card) || (_seeded.TryGetValue(card.Id, out var had) && had.Weight >= weight))
            {
                return;
            }

            var seed = new Seed(card.Id, weight, why, _seeds.Count, kind);
            _seeds.Add(seed);
            _seeded[card.Id] = seed;
        }

        /// <summary>
        /// What the prompt names exactly, in the order it names them: each backquoted symbol or path, each
        /// <c>path:line</c> (a stack frame's and a compiler's among them), each qualified name and each handle the view holds.
        /// A name several nodes the step may read carry seeds every one of them, however many, sharing the weight of a
        /// name written (<see cref="WrittenWeight"/>) among them, each its part of it; a file seeds its declarations.
        /// </summary>
        private IEnumerable<(GraphCard Card, double Weight, string Why, SeedKind Kind)> Named()
        {
            var said = new List<(int At, string Text, bool Quoted)>();
            var quoted = new List<(int Start, int End)>();

            foreach (Match match in Quoted().Matches(request.Prompt))
            {
                said.Add((match.Index, match.Groups["text"].Value.Trim(), true));
                quoted.Add((match.Index, match.Index + match.Length));
            }

            bool InQuotes(int at) => quoted.Any(span => at >= span.Start && at < span.End);

            foreach (Match match in AtLines().SelectMany(pattern => pattern.Matches(request.Prompt)).Where(match => !InQuotes(match.Index)))
            {
                said.Add((match.Index, match.Value, false));
            }

            foreach (var (at, name) in QualifiedNames(request.Prompt).Where(name => !InQuotes(name.At)))
            {
                said.Add((at, name, false));
            }

            foreach (var (at, handle) in Handles(request.Prompt).Where(handle => !InQuotes(handle.At)))
            {
                if (queries.Resolve(handle) is GraphResolve.Found found && found.Card.Handle == handle)
                {
                    said.Add((at, handle, false));
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

        private IEnumerable<(GraphCard Card, double Weight, string Why, SeedKind Kind)> Resolved(string text, bool quoted)
        {
            if (text.Length == 0)
            {
                return [];
            }

            if (AtLines().Select(pattern => pattern.Match(text)).FirstOrDefault(match => match.Success && match.Length == text.Length) is { } atLine)
            {
                if (Relative(atLine.Groups["path"].Value) is not { } path || !int.TryParse(atLine.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line))
                {
                    return [];
                }

                _namedPaths.Add(path);
                return queries.AtLine(path, line) is { } card ? [(card, NamedWeight, "named", SeedKind.Named)] : [];
            }

            if (!quoted)
            {
                return queries.Resolve(text) is GraphResolve.Found found ? [(found.Card, NamedWeight, "named", SeedKind.Named)] : [];
            }

            var name = SymbolNamed(text);

            if (Relative(name) is { } file)
            {
                _namedPaths.Add(file);
                return [.. Declarations(file).Select(card => (card, FileWeight, "in a file named", SeedKind.File))];
            }

            return queries.Resolve(name) switch
            {
                GraphResolve.Found found => [(found.Card, NamedWeight, "named", SeedKind.Named)],
                GraphResolve.Candidates candidates when candidates.Cards.Where(Reads).ToList() is { Count: > 0 } read
                    => [.. read.Select(card => (card, WrittenWeight / read.Count, $"one of {Number(read.Count)} named '{name}'", SeedKind.Named))],
                _ => [],
            };
        }

        /// <summary>
        /// The seeds of one site or home of a class the item closes: the declaration the site's symbol names in the
        /// site's file, at the weight of a named symbol - each of them, where the name is one several of the file's
        /// declarations carry - and, where the site names no symbol or nothing in the file carries it, the file's
        /// declarations as a file the prompt names, at a named file's weight. The site's path and its symbol are read by
        /// the pack's own rules of what a prompt names - the path the view holds it at, with either separator, a leading
        /// <c>./</c> or an ending of it (<see cref="Relative"/>), and the symbol without its parameters
        /// (<see cref="SymbolNamed"/>) - so a site the plan check accepts seeds what the prompt's own name of it would.
        /// Nothing where the view holds no such file.
        /// </summary>
        private IEnumerable<(GraphCard Card, double Weight, string Why)> AtSite(ContextPackClosure closure)
        {
            if (Relative(closure.Path) is not { } path)
            {
                return [];
            }

            var declared = Declarations(path).ToList();
            var why = closure.Home ? "the home of a class the item closes" : "a site of a class the item closes";

            var named = closure.Symbol is { Length: > 0 } written && SymbolNamed(written.Trim()) is { Length: > 0 } symbol
                ? declared.Where(card => string.Equals(card.Name, symbol, StringComparison.Ordinal)
                    || string.Equals(card.Qualified, symbol, StringComparison.Ordinal)
                    || card.Qualified.EndsWith("." + symbol, StringComparison.Ordinal)).ToList()
                : [];

            return named.Count > 0
                ? named.Select(card => (card, NamedWeight, why))
                : declared.Select(card => (card, FileWeight, "in a file a class of the item names"));
        }

        /// <summary>
        /// What a written name names, as the pack reads a prompt's names: a symbol written with its parameters names the
        /// symbol - <c>TokenOf(string)</c> names <c>TokenOf</c> - and anything that names a file, or holds no parameters,
        /// stands as written.
        /// </summary>
        /// <param name="text">The name, as the prompt or a class's site wrote it.</param>
        private static string SymbolNamed(string text)
            => text.IndexOf('(', StringComparison.Ordinal) is > 0 and var paren && !CodebaseGraphQueries.NamesAFile(text) ? text[..paren].TrimEnd() : text;

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
        /// The declarations changed the step may read - each with where its lines differ, one moved between files said so,
        /// and those taken out as the base held them, what went with its container said by the container - generated code
        /// after every other, and the cards of those the view holds, which the impact set is walked from; null where no
        /// change the step may read was found.
        /// </summary>
        private (List<Entry> Lines, List<GraphCard> Held)? Changed(CodebaseGraphChanges changes)
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

                if (_listed.Add(card.Id))
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

            return changed.Count == 0 ? null : ([.. changed.OrderBy(entry => entry.Generated ? 1 : 0)], held);
        }

        /// <summary>The differing files the step may read that could not be compared, with why: each a line of its own, ahead of what the pack lists.</summary>
        private List<CodeUncomparedFile> Uncompared() => [.. request.Changes?.Uncompared.Where(file => request.MayRead(file.Path)) ?? []];

        /// <summary>A node's relation as its line says it: the edge's confidence where it is not exact.</summary>
        private static string Related(string relation, string confidence)
            => string.Equals(confidence, CodeConfidences.Exact, StringComparison.Ordinal) ? relation : $"{relation} | {confidence}";

        /// <summary>
        /// A page's lines as the page writes them: those of the task by file - each file where its best node on the page
        /// stands, its nodes by line - and every other in the order it was reached.
        /// </summary>
        private IReadOnlyList<GraphTextLine> ByFile(IReadOnlyList<GraphTextLine> page)
        {
            var task = page.Where(line => line.Id is not null && string.Equals(line.Caption, _taskCaption, StringComparison.Ordinal)).ToList();

            if (task.Count == 0)
            {
                return page;
            }

            var files = task.Select(line => queries.Card(line.Id!).Path).Distinct(StringComparer.Ordinal).ToList();
            var arranged = task
                .OrderBy(line => files.IndexOf(queries.Card(line.Id!).Path))
                .ThenBy(line => queries.Card(line.Id!).Start)
                .ThenBy(line => line.Id, StringComparer.Ordinal)
                .ToList();

            return [.. page.Where(line => !task.Contains(line)), .. arranged];
        }

        /// <summary>
        /// The pack's first line: the view, its commit, how many files the step may read the tree holds otherwise than its
        /// base and how many of them could not be compared - each listed after it, with why - and what the pack was seeded
        /// from - or, under change-first, how many lines the changes lead with, or why the task's seeds stand in for them:
        /// never that no declaration changed while a file was not compared, for what it changed is not known.
        /// </summary>
        private string FirstLine(int changesLead, int uncompared)
        {
            var line = new System.Text.StringBuilder($"codebase graph: view '{queries.View}' at {(queries.Head is { } head ? ReportText.Commit(head) : "no commit")}");

            if (request.Changes is { } read)
            {
                line.Append(CultureInfo.InvariantCulture, $", {ReportText.Counted(read.Files.Count(request.MayRead), "file")} differing from its base {ReportText.Commit(read.Base)}");
            }

            line.Append("; ");

            // Every differing file not compared leaves nothing that was: the task's seeds stand in, said as following from it.
            var noneCompared = uncompared > 0 && uncompared == request.Changes!.Files.Count(request.MayRead);

            if (uncompared > 0)
            {
                line.Append(CultureInfo.InvariantCulture, $"{ReportText.Counted(uncompared, "differing file")} could not be compared, each listed under '{NotCompared}'");
                line.Append(request.From == ContextPackFrom.Changes && changesLead == 0 && noneCompared ? ", " : "; ");
            }

            if (request.From == ContextPackFrom.Changes)
            {
                line.Append(changesLead > 0 ? $"packed from those changes first - {ReportText.Counted(changesLead, "declaration")} added, changed or taken out, their tests, then their impact - then from the task: "
                    : request.ChangesUnread is { } why ? $"its changes could not be read ({TextLines.OneLine(why).TrimEnd('.')}), so packed from the task: "
                    : request.Changes is null ? "no base to compare with, so packed from the task: "
                    : noneCompared ? "so packed from the task: "
                    : uncompared > 0 ? "no declaration among the rest changed, so packed from the task: "
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
            SeedKind.Closure => ReportText.Counted(count, "declaration") + " at the sites and homes of the classes it is to close",
            SeedKind.File => ReportText.Counted(count, "declaration") + " of the files it names",
            SeedKind.Worked => ReportText.Counted(count, "working set node"),
            SeedKind.Written => ReportText.Counted(count, "declaration") + " of the files the step writes",
            _ => ReportText.Counted(count, "node") + " its words match",
        };

        /// <summary>
        /// A page's last line: how many nodes it lists and its tokens of the page's size, and where the pack goes on - nothing
        /// left out where it is one page; else which page, and how to ask for the next, or that it is the last - and how many
        /// nodes the read rule left out of the walk so far, or in all on the last.
        /// </summary>
        private string Last(GraphTextPageEnd end)
        {
            var nodes = end.Lines.Select(line => line.Id).OfType<string>().Distinct(StringComparer.Ordinal).Count();
            var where = end is { Last: true, Page: 1 } ? "nothing left out"
                : end.Last ? $"context pack page {Number(end.Page)} of {Number(end.Pages!.Value)} · the last"
                : request.Asks ? $"context pack page {Number(end.Page)} · ask graph for pack with page: {Number(end.Page + 1)} for the next"
                : $"context pack page {Number(end.Page)} · the rest is on the pages after it, which a step whose graph is tools asks the graph for";
            var line = $"{ReportText.Counted(nodes, "node")}, ~{Number(end.Tokens)} of {Number(request.Tokens)} tokens; {where}";

            return _refused.Count == 0 ? line
                : end.Last ? $"{line}; {Number(_refused.Count)} in files the step may not read left out"
                : $"{line}; {Number(_refused.Count)} so far in files the step may not read left out";
        }

        /// <summary>Whether the step may read <paramref name="card"/>'s file, a node it may not counted as left out.</summary>
        private bool Reads(GraphCard card)
        {
            if (request.MayRead(card.Path))
            {
                return true;
            }

            _refused.Add(card.Id);
            return false;
        }

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
    }

    /// <summary>
    /// Each qualified name <paramref name="prompt"/> holds, with where it begins: dotted or <c>::</c>-joined parts, each a
    /// letter or underscore then word characters, at a place no word character, dot, colon or separator stands before, and
    /// ending where no word character or separator follows - its last parts given up, at a dot, where one does, as a
    /// name that runs into a path is read only up to the path.
    /// </summary>
    private static IEnumerable<(int At, string Name)> QualifiedNames(string prompt)
    {
        foreach (Match match in QualifiedName().Matches(prompt))
        {
            if (match.Index > 0 && (char.IsLetterOrDigit(prompt[match.Index - 1]) || prompt[match.Index - 1] is '_' or '.' or ':' or '\\' or '/'))
            {
                continue;
            }

            var end = match.Index + match.Length;

            if (end < prompt.Length && (char.IsLetterOrDigit(prompt[end]) || prompt[end] is '_' or '/' or '\\'))
            {
                // The name runs into a path: given up to the end of its part before the last, where it has two parts left.
                var name = match.Value;
                var separator = Math.Max(name.LastIndexOf('.'), name.LastIndexOf("::", StringComparison.Ordinal));
                var kept = separator > 0 ? name[..separator] : string.Empty;

                if (kept.Contains('.', StringComparison.Ordinal) || kept.Contains("::", StringComparison.Ordinal))
                {
                    yield return (match.Index, kept);
                }

                continue;
            }

            yield return (match.Index, match.Value);
        }
    }

    /// <summary>
    /// Each handle <paramref name="prompt"/> holds, with where it begins: a run of six to sixty-four lowercase hexadecimal
    /// digits, whole - no word character or dash beside it.
    /// </summary>
    private static IEnumerable<(int At, string Handle)> Handles(string prompt)
    {
        foreach (Match match in HexRun().Matches(prompt))
        {
            var end = match.Index + match.Length;

            if (match.Length is >= CodeGraphIds.HandleLength and <= 64
                && (match.Index == 0 || !(char.IsLetterOrDigit(prompt[match.Index - 1]) || prompt[match.Index - 1] is '_' or '-'))
                && (end == prompt.Length || !(char.IsLetterOrDigit(prompt[end]) || prompt[end] is '_' or '-')))
            {
                yield return (match.Index, match.Value);
            }
        }
    }

    /// <summary>A seed: a node, its weight, why it is one, where it stands among the seeds, and what kind of seed it is.</summary>
    private sealed record Seed(string Id, double Weight, string Why, int Order, SeedKind Kind);

    /// <summary>What made a node a seed, in the order the first line says them.</summary>
    private enum SeedKind
    {
        Named,
        Closure,
        File,
        Worked,
        Written,
        Words,
    }

    /// <summary>A node a walk reached, as its best path reached it.</summary>
    private sealed record Reach(string Id, double Score, int Order, int Hop, string Relation, string Confidence)
    {
        /// <summary>The kind of the edge its best path reached it by; null for a node a walk starts from.</summary>
        public string? Kind { get; init; }
    }

    /// <summary>The order a walk settles what it reached in: the better score first, then the earlier source, the fewer edges, the id.</summary>
    private sealed class ReachOrder : IComparer<Reach>
    {
        public static ReachOrder Instance { get; } = new();

        public int Compare(Reach? x, Reach? y)
        {
            ArgumentNullException.ThrowIfNull(x);
            ArgumentNullException.ThrowIfNull(y);

            return x.Score != y.Score ? y.Score.CompareTo(x.Score)
                : x.Order != y.Order ? x.Order.CompareTo(y.Order)
                : x.Hop != y.Hop ? x.Hop.CompareTo(y.Hop)
                : string.CompareOrdinal(x.Id, y.Id);
        }
    }

    /// <summary>One changed line of the pack: the node it names, where it names one, and whether it is generated code.</summary>
    private sealed record Entry(string? Id, string Line, bool Generated);

    private static Regex Quoted() => QuotedPattern.Value;

    /// <summary>
    /// The three spellings of a file and a line in it - a stack frame's <c>:line n</c>, a range's <c>:n</c> and a
    /// compiler's <c>(n,c)</c> - each a pattern of its own over <see cref="AtLinePath"/>: the linear engine reads the
    /// three as one alternation only in part, losing a match before the last where a path-shaped word precedes them,
    /// while each alone it reads as the backtracking engine does.
    /// </summary>
    private static IEnumerable<Regex> AtLines() => [AtLineWordPattern.Value, AtLineNumberedPattern.Value, AtLineParenPattern.Value];

    /// <summary>A file's path: a drive's root and any name and separator, ending in a name with a dot and an extension.</summary>
    private const string AtLinePath = @"(?<path>(?:[A-Za-z]:[\\/])?[\w.\-/\\]*\w\.\w+)";

    private static readonly Lazy<Regex> QuotedPattern = new(() => LinearRegex.Compile(@"`(?<text>[^`\r\n]+)`"));

    private static readonly Lazy<Regex> AtLineWordPattern = new(() => LinearRegex.Compile(AtLinePath + @":line (?<line>\d+)"));

    private static readonly Lazy<Regex> AtLineNumberedPattern = new(() => LinearRegex.Compile(AtLinePath + @":(?<line>\d+)(?:-\d+)?"));

    private static readonly Lazy<Regex> AtLineParenPattern = new(() => LinearRegex.Compile(AtLinePath + @"\((?<line>\d+)(?:,\d+)?\)"));

    private static Regex QualifiedName() => QualifiedPattern.Value;

    private static Regex HexRun() => HexPattern.Value;

    private static Regex Identifier() => IdentifierPattern.Value;

    private static readonly Lazy<Regex> QualifiedPattern = new(() => LinearRegex.Compile(@"[A-Za-z_]\w*(?:(?:\.|::)[A-Za-z_]\w*)+"));

    private static readonly Lazy<Regex> HexPattern = new(() => LinearRegex.Compile("[0-9a-f]+"));

    private static readonly Lazy<Regex> IdentifierPattern = new(() => LinearRegex.Compile(@"[A-Za-z_]\w*"));
}
