using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>What an agent asks the codebase graph.</summary>
public enum GraphOp
{
    /// <summary>The nodes that match a query, best first.</summary>
    Find,

    /// <summary>A node's card and its edges grouped by kind.</summary>
    Node,

    /// <summary>The nodes reached from a node over one relation.</summary>
    Related,

    /// <summary>A file's or a type's declarations, or the repository's.</summary>
    Outline,

    /// <summary>The exact lines of nodes, read fresh.</summary>
    Body,

    /// <summary>A node's lines replaced where its hash is unchanged.</summary>
    Edit,

    /// <summary>The narrowing of reads lifted for a file for the rest of the step.</summary>
    WholeFile,
}

/// <summary>What answering a <c>graph</c> call left: the text given to the model, how it ended, and the file a write touched.</summary>
/// <param name="Text">The answer, as the model reads it.</param>
/// <param name="Outcome">Whether it answered, refused a misuse, was refused by the policy, or wrote.</param>
/// <param name="Path">The file a write touched, or the policy refused, relative to the tree; null otherwise.</param>
/// <param name="Denied">The policy's refusal, where it refused the call.</param>
public sealed record GraphToolAnswer(string Text, GraphToolOutcome Outcome, string? Path = null, AgentPolicyAnswer.Denied? Denied = null)
{
    /// <summary>The nodes the call asked about, served and edited: what the step's working set is made of.</summary>
    public GraphToolFacts Facts { get; init; } = GraphToolFacts.None;

    /// <summary>
    /// The files the call leaves viewed, relative to the tree - those a <c>body</c> served lines of and the one an edit
    /// made - which the runtime counts as views of them for its edit check, as a <c>view_file</c> is a view.
    /// </summary>
    public IReadOnlyList<string> Viewed { get; init; } = [];

    /// <summary>
    /// The escape a <c>wholeFile</c> that lifted the narrowing of reads made, <see cref="GraphEscapeLine.Stated"/> with the
    /// agent's reason - its file by the path the view holds it at, with the lines and the tokens the view read it as; null
    /// for every other call.
    /// </summary>
    public GraphEscapeLine? Escape { get; init; }

    /// <summary>
    /// The pages of the answer, where it was asked as pages: its page and its pages, the whole answer's exact total and
    /// the entries this page gave - what the <c>query-codebase-graph</c> verb's document answers; null where the call was
    /// no answered ask of the graph.
    /// </summary>
    public GraphPageAnswer? Paging { get; init; }
}

/// <summary>
/// The nodes one <c>graph</c> call used, each by its id: those it asked about by name - <c>node</c>, <c>related</c> and
/// <c>body</c> - those whose lines it served, and the one an <c>edit</c> wrote, with the lines it wrote.
/// </summary>
/// <param name="Asked">The nodes it asked about, in the order it named them.</param>
/// <param name="Served">The nodes a line of which a <c>body</c>'s answer gave, in order.</param>
/// <param name="Edited">The node an <c>edit</c> wrote, by the id it had; none where it wrote none.</param>
/// <param name="Wrote">The lines an <c>edit</c> wrote, as the file stood once it had; null where it wrote none.</param>
/// <param name="Hits">How many text hits the answer added where the graph was not sure.</param>
public sealed record GraphToolFacts(IReadOnlyList<string> Asked, IReadOnlyList<string> Served, IReadOnlyList<string> Edited, GraphWrittenLines? Wrote = null, int Hits = 0)
{
    /// <summary>A call that used no node.</summary>
    public static GraphToolFacts None { get; } = new([], [], []);
}

/// <summary>The lines one <c>edit</c> wrote: what a node's new text is matched to the nodes it declares by, whatever they are called now.</summary>
/// <param name="Path">The file, relative to the tree, as the view names it.</param>
/// <param name="Start">The first line written, from 1, as the file stood once it had.</param>
/// <param name="Count">How many lines it wrote; none where its text was empty.</param>
/// <param name="Hash">
/// Their hash, as a node's is (<see cref="CodeGraphIds.Digest"/>, its first eight characters): what finds them where a
/// later write moved them; null where it wrote none.
/// </param>
public sealed record GraphWrittenLines(string Path, int Start, int Count, string? Hash = null)
{
    /// <summary>Why the lines do not read as an edit's, or null where they do.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Path) ? "an edit names the file it wrote"
            : Start < 1 || Count < 0 ? "an edit's lines start at line 1 at the earliest, and are never fewer than none"
            : Hash is not null && (Count == 0 || Hash.Length != 8 || !Hash.All(char.IsAsciiHexDigitLower)) ? "an edit's lines are named by a hash of eight lowercase hexadecimal digits, and no lines by none"
            : null;
}

/// <summary>What became of a <c>graph</c> call.</summary>
public enum GraphToolOutcome
{
    /// <summary>It answered.</summary>
    Answered,

    /// <summary>It was a misuse - an operation not given, a member it does not read, one it needs missing - answered with a usage line.</summary>
    Usage,

    /// <summary>The step's policy refused the write an <c>edit</c> asked for, as it refuses <c>replace_in_file</c>.</summary>
    Refused,

    /// <summary>An <c>edit</c> wrote the file its node is in.</summary>
    Wrote,
}

/// <summary>
/// What a whole read of one file is under the narrowing of reads (<c>graphReads: narrow</c>): denied, with the way to
/// the graph; allowed as an escape <c>lowConfidence</c>, with why the graph is not sure of the file; or allowed plainly.
/// </summary>
/// <param name="Narrowed">Whether the read is denied.</param>
/// <param name="Text">Where it is denied, the sentence the model reads with the way to the graph; where it is an escape, why the graph is not sure of the file.</param>
/// <param name="Escape">The escape that lets the read - <see cref="GraphEscapeLine.LowConfidence"/> - or null where it is denied or needs none.</param>
/// <param name="Path">The file, by the path the view holds it at, where it is denied or let by as an escape - what its line records it as; null where the read passes plainly.</param>
public sealed record GraphWholeRead(bool Narrowed, string Text, string? Escape, string? Path = null)
{
    /// <summary>A read that passes plainly: not denied, no escape to record.</summary>
    public static GraphWholeRead Passed { get; } = new(Narrowed: false, string.Empty, null);
}

/// <summary>
/// The one tool <c>graph</c>, over the lookups of <see cref="CodebaseGraphQueries"/>: its seven operations - <c>find</c>,
/// <c>node</c>, <c>related</c>, <c>outline</c>, <c>body</c>, <c>edit</c> and <c>wholeFile</c> - the same on every client, each
/// answering in pages of whole entries with a line range and a hash on every node, and a text search where the graph is not
/// sure. The schema a step is given holds only the operations it may use. No step opens it: the tool
/// runtime's table does not hold it. The <c>query-codebase-graph</c> verb runs its read operations by hand.
/// </summary>
/// <remarks>
/// It answers from the view it was handed, and reads a body or an edit's file fresh itself, so a node that moved since is
/// told by its hash. Where it is handed <c>reExtract</c>, a moved node is extracted again before its body is served, and
/// an edit's file after it is written, and the tool answers from the view that gives from then on; after a write or a
/// command of another tool's, the sync that follows it hands the tool the view it left (<see cref="RefreshAsync"/>,
/// <see cref="AfterWrites.SyncGraphAsync"/>). A path it is given is the file the view holds by the tree's own rule of
/// case: the one file the view holds in another case where the tree's file system finds a name in any case.
/// </remarks>
/// <param name="queries">The indexed view it answers from.</param>
/// <param name="files">The one search a text fallback runs, as <c>search_files</c> runs it, and the one way a body is read fresh and an edit written.</param>
/// <param name="fileSystem">Looks a body's file up.</param>
/// <param name="paths">
/// Where a body's read and an edit's write may reach, as the runtime's file tools' do: the step's agent's roots in a step,
/// the tree alone for the verb (<see cref="ConfinedPaths.Within"/>) - so a file of the view that became a link out of the
/// tree since the view was read, or lies along one, is never read or written through.
/// </param>
/// <param name="mayRead">Whether the step's policy lets a file be read, by its path relative to the tree: a node of a file it does not is left out, and counted.</param>
/// <param name="mayWrite">The step's policy on writing a file, by its path relative to the tree; null where <c>edit</c> is not given.</param>
/// <param name="reExtract">Checks a file again by its path relative to the tree and answers the view it leaves; null where nothing may be synced (the verb).</param>
/// <param name="near">
/// How near each node is to the work the tool serves, which breaks a tie in <c>find</c> - read at each <c>find</c>, so a tool
/// held for a whole chat follows its last message (a step's or a message's context pack gives it); null where there is
/// no work, or it says none.
/// </param>
/// <param name="pages">
/// Where the first page of an answer keeps its whole result, under the step's <c>graph-pages\</c> directory, so its later
/// pages - and a resumed conversation's - are pages of that one result; null where none is kept (the verb).
/// </param>
public sealed class CodebaseGraphTool(
    CodebaseGraphQueries queries,
    AgentFiles files,
    IFileSystem fileSystem,
    ConfinedPaths paths,
    Func<string, bool> mayRead,
    Func<string, AgentPolicyAnswer>? mayWrite = null,
    Func<string, CancellationToken, Task<CodebaseGraphQueries>>? reExtract = null,
    Func<GraphNearness?>? near = null,
    GraphPageStore? pages = null)
{
    /// <summary>The most characters an answer holds; a page past it ends at a whole entry, and the next holds the rest.</summary>
    public const int MostCharacters = 24 * 1024;

    /// <summary>The results a page of <c>find</c> holds.</summary>
    public const int FindLines = 20;

    /// <summary>The edges a page of <c>node</c> holds of each kind.</summary>
    public const int NodeLinesAKind = 15;

    /// <summary>The nodes a page of <c>related</c> holds.</summary>
    public const int RelatedLines = 60;

    /// <summary>The declarations a page of <c>outline</c> holds.</summary>
    public const int OutlineLines = 400;

    /// <summary>The lines a page of <c>body</c> holds, all its symbols together.</summary>
    public const int BodyLines = 600;

    /// <summary>The most symbols one <c>body</c> reads.</summary>
    public const int BodySymbols = 8;

    /// <summary>The hits a page of the text fallback holds.</summary>
    public const int TextHits = 20;

    /// <summary>The most hops a <c>related</c> walk goes.</summary>
    public const int MostDepth = 3;

    /// <summary>The most lines an <c>edit</c>'s text may hold.</summary>
    public const int MostEditLines = 600;

    /// <summary>The most characters of a line a text hit gives, the rest left out.</summary>
    public const int MostHitCharacters = 200;

    /// <summary>The most files whose extract is gone an answer names, the rest counted.</summary>
    public const int UnheldNamed = 5;

    /// <summary>The names of the operations, as <c>op</c> says them.</summary>
    private static readonly IReadOnlyDictionary<GraphOp, string> Names = new Dictionary<GraphOp, string>
    {
        [GraphOp.Find] = "find",
        [GraphOp.Node] = "node",
        [GraphOp.Related] = "related",
        [GraphOp.Outline] = "outline",
        [GraphOp.Body] = "body",
        [GraphOp.Edit] = "edit",
        [GraphOp.WholeFile] = "wholeFile",
    };

    private readonly AgentFiles _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ConfinedPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly Func<string, bool> _mayRead = mayRead ?? throw new ArgumentNullException(nameof(mayRead));
    private readonly SemaphoreSlim _extracting = new(1, 1);
    private readonly SemaphoreSlim _editing = new(1, 1);
    private readonly GraphPageStore? _pages = pages;
    private CodebaseGraphQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));

    /// <summary>How the tree's file system compares names, as it answers for itself: asked once, the tree being the tool's for its life.</summary>
    private StringComparison? _case;

    /// <summary>The read operations, which a step with the <c>read</c> capability and the verb are given.</summary>
    public static IReadOnlyList<GraphOp> Read { get; } = [GraphOp.Find, GraphOp.Node, GraphOp.Related, GraphOp.Outline, GraphOp.Body];

    /// <summary>The view the tool answers from now: the one it was handed, or the one its last extraction left.</summary>
    public CodebaseGraphQueries Queries => _queries;

    /// <summary>The name <c>op</c> says <paramref name="op"/> by.</summary>
    /// <param name="op">An operation.</param>
    public static string NameOf(GraphOp op) => Names[op];

    /// <summary>The operation <c>op</c> says by <paramref name="name"/>; null for a name it says none by.</summary>
    /// <param name="name">The name, as an input's <c>op</c> spells it.</param>
    public static GraphOp? OpOf(string name)
    {
        foreach (var (op, said) in Names)
        {
            if (string.Equals(said, name, StringComparison.Ordinal))
            {
                return op;
            }
        }

        return null;
    }

    /// <summary>The operations a step is given: the read ones always, <c>edit</c> where it may write, <c>wholeFile</c> where its reads are narrowed.</summary>
    /// <param name="write">Whether the step has the <c>write</c> capability.</param>
    /// <param name="narrow">Whether the step's reads are narrowed (<c>graphReads: narrow</c>).</param>
    public static IReadOnlySet<GraphOp> Operations(bool write, bool narrow)
    {
        var ops = new HashSet<GraphOp>(Read);

        if (write)
        {
            ops.Add(GraphOp.Edit);
        }

        if (narrow)
        {
            ops.Add(GraphOp.WholeFile);
        }

        return ops;
    }

    /// <summary>
    /// The most lines a file may hold and a whole read of it still pass the narrowing: a longer one the view holds as
    /// parsed is denied, with the way to the graph (<c>graphReads: narrow</c>).
    /// </summary>
    public const int NarrowLines = 200;

    /// <summary>
    /// The files whose narrowing a <c>wholeFile</c> lifted, each once, for the rest of the step: read by every narrowed read
    /// and written by a <c>wholeFile</c> on whichever thread serves it - Claude Code's served calls run beside its own reads.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _lifted = new(StringComparer.Ordinal);

    /// <summary>
    /// What a whole read of the file at <paramref name="path"/> of <paramref name="lines"/> lines is under the narrowing
    /// (<c>graphReads: narrow</c>): denied, with the way to the graph, where the view holds the file as parsed and it is
    /// over <see cref="NarrowLines"/> lines - or allowed, an escape <c>lowConfidence</c> with why where the graph is not
    /// sure of it, plainly where the view does not hold it as parsed, it is short enough, or a <c>wholeFile</c> lifted it.
    /// </summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    /// <param name="lines">How many lines the file holds, as the reader read it.</param>
    public GraphWholeRead WholeRead(string path, int lines)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Narrowable(path) is not { } held)
        {
            return GraphWholeRead.Passed;
        }

        if (_queries.UnsureOfFile(held) is { } why)
        {
            return new GraphWholeRead(Narrowed: false, why, GraphEscapeLine.LowConfidence, held);
        }

        return lines > NarrowLines
            ? new GraphWholeRead(
                Narrowed: true,
                $"'{held}' is {lines.ToString(CultureInfo.InvariantCulture)} lines and in the codebase graph: ask graph for its outline and the bodies you need, read a range of it, or, if you need it whole, say why with graph's wholeFile",
                null,
                held)
            : GraphWholeRead.Passed;
    }

    /// <summary>Whether a <c>wholeFile</c> lifted the narrowing of the file at <paramref name="path"/> for the rest of the step.</summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    public bool Lifted(string path) => Held(path) is { } held && _lifted.ContainsKey(held);

    /// <summary>
    /// Whether a whole read of the file at <paramref name="path"/> may be narrowed at all - the view holds it as parsed and no
    /// <c>wholeFile</c> lifted it - asked before the file is read, so a read nothing can narrow reads nothing for it.
    /// </summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    public bool MayNarrow(string path) => Narrowable(path) is not null;

    /// <summary>The path the view holds a parsed file at that <paramref name="path"/> names, where no <c>wholeFile</c> lifted it; null otherwise.</summary>
    private string? Narrowable(string path)
        => Held(path) is { } held && _queries.Parsed(held) && !_lifted.ContainsKey(held) ? held : null;

    /// <summary>
    /// The path the view holds the file <paramref name="path"/> names at, by the tree's own rule of case: as given, or the one
    /// file the view holds in another case where the tree's file system finds a name in any case; null where it holds none.
    /// </summary>
    private string? Held(string path)
        => _queries.HeldPath(path, _case ??= _paths.ComparisonIn(_queries.Tree));

    /// <summary>The members each operation reads beside <c>op</c>, and of them those it needs.</summary>
    /// <param name="op">An operation.</param>
    public static (IReadOnlyList<string> Reads, IReadOnlyList<string> Needs) Members(GraphOp op) => op switch
    {
        GraphOp.Find => (["query", "kind", "path", "page"], ["query"]),
        GraphOp.Node => (["symbol", "page"], ["symbol"]),
        GraphOp.Related => (["symbol", "relation", "depth", "page"], ["symbol", "relation"]),
        GraphOp.Outline => (["path", "symbol", "page"], []),
        GraphOp.Body => (["symbols", "full", "page"], ["symbols"]),
        GraphOp.Edit => (["symbol", "hash", "text", "page"], ["symbol", "hash", "text"]),
        GraphOp.WholeFile => (["path", "reason", "page"], ["path", "reason"]),
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not an operation of the graph tool."),
    };

    /// <summary>The schema of the <c>graph</c> input made for a step given <paramref name="ops"/>: <c>op</c> an enumeration of them alone, the members they read optional, nothing else taken.</summary>
    /// <param name="ops">The operations the step may use.</param>
    public static string Schema(IReadOnlySet<GraphOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var given = Ordered(ops).ToList();
        var properties = new JsonObject
        {
            ["op"] = new JsonObject
            {
                ["type"] = "string",
                ["enum"] = new JsonArray([.. given.Select(op => (JsonNode)Names[op])]),
                ["description"] = "What to ask of the graph.",
            },
        };

        foreach (var (name, type, description) in Described())
        {
            var readers = given.Where(op => Members(op).Reads.Contains(name)).Select(op => Names[op]).ToList();

            if (readers.Count == 0)
            {
                continue;
            }

            var member = new JsonObject { ["type"] = type };

            if (type == "array")
            {
                member["items"] = new JsonObject { ["type"] = "string" };
            }

            if (name == "relation")
            {
                member["enum"] = new JsonArray([.. GraphRelations.All.Select(relation => (JsonNode)relation)]);
            }

            member["description"] = $"{string.Join(", ", readers)}: {description}";
            properties[name] = member;
        }

        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray("op"),
            ["additionalProperties"] = false,
        }.ToJsonString();
    }

    /// <summary>What the model is told the tool does, for a step given <paramref name="ops"/>: the operations it may use alone, with their bounds.</summary>
    /// <param name="ops">The operations the step may use.</param>
    public static string Description(IReadOnlySet<GraphOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var said = new Dictionary<GraphOp, string>
        {
            [GraphOp.Find] = $"find - the nodes a query's words match, best first, {FindLines} a page",
            [GraphOp.Node] = $"node - a node's card and its edges by kind, {NodeLinesAKind} of each a page",
            [GraphOp.Related] = $"related - the nodes a relation reaches, 1 to {MostDepth} hops, {RelatedLines} a page",
            [GraphOp.Outline] = $"outline - a file's or a type's declarations, or the repository's, {OutlineLines} a page",
            [GraphOp.Body] = $"body - the lines of 1 to {BodySymbols} nodes, read fresh and numbered, {BodyLines} lines a page",
            [GraphOp.Edit] = $"edit - a node's lines replaced where the hash you give is its hash now, at most {MostEditLines} lines",
            [GraphOp.WholeFile] = "wholeFile - lifts the narrowing of reads for one file for the rest of the step, with why",
        };

        return "Asks the codebase graph of the tree you work in: "
            + string.Join("; ", Ordered(ops).Select(op => said[op]))
            + $". Every node is named with its handle and path:start-end #hash - read a range of a file by those lines - and every answer is pages of whole entries: one of more than one page starts with the line 'page k of n' and any page is asked for with page, from 1; an answer with no header holds all of it. Where the graph is not sure, the answer adds text: lines that hold the name.";
    }

    /// <summary>Runs the <c>graph</c> call <paramref name="input"/> asks, where its operation is among <paramref name="ops"/>.</summary>
    /// <param name="input">The call's input.</param>
    /// <param name="ops">The operations the step may use.</param>
    /// <param name="cancellationToken">Stops the call.</param>
    public async Task<GraphToolAnswer> RunAsync(JsonObject input, IReadOnlySet<GraphOp> ops, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(ops);

        var given = string.Join(", ", Ordered(ops).Select(op => Names[op]));

        if (Str(input, "op") is not { } name)
        {
            return Usage($"op is required: one of {given}");
        }

        if (!Names.Any(pair => pair.Value == name && ops.Contains(pair.Key)))
        {
            return Usage($"op '{name}' is not one this step may use: {given}");
        }

        var op = Names.Single(pair => pair.Value == name).Key;

        if (Misused(input, op) is { } misused)
        {
            return Usage(misused);
        }

        return op switch
        {
            GraphOp.Find => await FindAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Node => await NodeAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Related => await RelatedAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Outline => await OutlineAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Body => await BodyAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Edit => await OneEditAtATimeAsync(input, cancellationToken).ConfigureAwait(false),
            _ => WholeFile(input),
        };
    }

    private async Task<GraphToolAnswer> FindAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var query = Str(input, "query")!;
        var path = Str(input, "path") is { } given ? Held(given) ?? given : null;

        return await PagedAsync(input, GraphOp.Find, $"find {query}", async token =>
        {
            var matched = _queries.Find(query, Str(input, "kind"), path, int.MaxValue, 0, near?.Invoke()).Cards;
            var (kept, left) = Readable(matched);

            if (kept.Count > 0)
            {
                return new GraphAnswer([], [Section(null, [.. kept.Select(card => new GraphEntry(GraphLines.Of(card)))], FindLines)], Notes(left), [], []);
            }

            // A query no node matches is answered by the text search alone: its hits are the answer's entries.
            var searched = await SearchAsync(query, token).ConfigureAwait(false);
            return new GraphAnswer(["no node matched"], [.. searched.SectionsOf(query)], [.. Notes(left), .. searched.NoHitsOf(query), .. searched.Notes], [], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphToolAnswer> NodeAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;

        if (Resolved(symbol) is not { } card)
        {
            return await UnresolvedAsync(input, GraphOp.Node, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, GraphOp.Node, $"node {symbol}", async token =>
        {
            var leading = new List<string> { GraphLines.Of(card) };
            var sections = new List<GraphSection>();
            var notes = new List<string>();
            var left = 0;
            var unsure = _queries.Unsure(card.Id);

            foreach (var (group, members) in _queries.Edges(card.Id).Groups)
            {
                var kept = members.Where(edge => Reads(edge.Card)).ToList();
                left += members.Count - kept.Count;

                if (kept.Count == 0)
                {
                    continue;
                }

                // Every edge of the kind decides whether the graph is sure of the answer: no page's bound hides one. A use
                // is recall, never a doubt - its candidates say nothing of how sure the graph is of what it declares.
                var doubt = group is not "users" and not "uses";
                unsure |= kept.Any(edge => doubt && (string.Equals(edge.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal) || _queries.Unsure(edge.Card.Id)));
                sections.Add(Section($"{group} ({kept.Count}):", [.. kept.Select(edge => new GraphEntry("  " + EdgeLine(edge)))], NodeLinesAKind));
            }

            notes.AddRange(Notes(left));

            if (unsure)
            {
                var searched = await SearchAsync(card.Name, token).ConfigureAwait(false);
                sections.AddRange(searched.SectionsOf(card.Name));
                notes.AddRange(searched.NoHitsOf(card.Name));
                notes.AddRange(searched.Notes);
            }

            return new GraphAnswer(leading, sections, notes, [card.Id], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphToolAnswer> RelatedAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;
        var relation = Str(input, "relation")!;
        var depth = Int(input, "depth") ?? 1;

        if (Resolved(symbol) is not { } card)
        {
            return await UnresolvedAsync(input, GraphOp.Related, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, GraphOp.Related, $"related {symbol}", async token =>
        {
            var reached = _queries.Related(card.Id, relation, depth);
            var kept = reached.Where(one => Reads(one.Card)).ToList();
            var lines = kept.Select(one => one.Crowded is { } edges
                ? $"{one.Distance}: {GraphLines.Of(one.Card)} | {edges} {relation}, not gone through"
                : $"{one.Distance}: {GraphLines.Of(one.Card)}").ToList();
            var leading = lines.Count == 0 ? [$"no node is {relation} of '{card.Qualified}'"] : Array.Empty<string>();
            var sections = new List<GraphSection>();
            var notes = new List<string>(Notes(reached.Count - kept.Count));

            if (_queries.Unsure(card.Id) || kept.Any(one => one.Candidates || _queries.Unsure(one.Card.Id)))
            {
                var searched = await SearchAsync(card.Name, token).ConfigureAwait(false);
                sections.AddRange(searched.SectionsOf(card.Name));
                notes.AddRange(searched.NoHitsOf(card.Name));
                notes.AddRange(searched.Notes);
            }

            return new GraphAnswer(leading, lines.Count == 0 ? sections : [.. sections.Prepend(Section(null, [.. lines.Select(line => new GraphEntry(line))], RelatedLines))], notes, [card.Id], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphToolAnswer> OutlineAsync(JsonObject input, CancellationToken cancellationToken)
    {
        if (Str(input, "path") is { } given)
        {
            var path = Held(given) ?? given;

            if (!_mayRead(path))
            {
                return Said($"'{path}' is a file your reads do not reach");
            }

            if (_queries.OutlineFile(path) is not { } cards)
            {
                return Said($"the view holds no file at '{path}'");
            }

            return await PagedAsync(input, GraphOp.Outline, $"outline {path}", token => Task.FromResult(OutlineOf(path, cards)), cancellationToken).ConfigureAwait(false);
        }

        if (Str(input, "symbol") is { } symbol)
        {
            if (Resolved(symbol) is not { } card)
            {
                return await UnresolvedAsync(input, GraphOp.Outline, symbol, cancellationToken).ConfigureAwait(false);
            }

            var members = _queries.OutlineType(card.Id);
            var (kept, left) = Readable(members);

            return await PagedAsync(input, GraphOp.Outline, $"outline {symbol}", token => Task.FromResult(new GraphAnswer(
                MembersOf(card, kept),
                kept.Count == 0 ? [] : [Section(null, [.. kept.Select(one => new GraphEntry("  " + GraphLines.Of(one)))], OutlineLines)],
                Notes(left),
                [],
                [])), cancellationToken).ConfigureAwait(false);
        }

        var overview = _queries.Overview();
        var lines = new List<string>();
        lines.AddRange(overview.Projects.Where(Reads).Select(project => "project " + GraphLines.Of(project)));
        lines.AddRange(overview.Directories
            .Select(directory => (directory.Directory, Files: directory.Files.Count(_mayRead)))
            .Where(directory => directory.Files > 0)
            .Select(directory => $"directory {directory.Directory} ({ReportText.Counted(directory.Files, "file")})"));
        lines.AddRange(overview.Namespaces
            .Where(one => one.Files.Any(_mayRead))
            .Select(one => $"namespace {one.Card.Qualified} ({ReportText.Counted(one.Declarations.Count(Reads), "declaration")})"));

        return await PagedAsync(input, GraphOp.Outline, "outline", token => Task.FromResult(new GraphAnswer(
            RepositoryOf(lines),
            lines.Count == 0 ? [] : [Section(null, [.. lines.Select(line => new GraphEntry(line))], OutlineLines)],
            [],
            [],
            [])), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A file outline's answer: its leading lines and its declarations as one section.</summary>
    private GraphAnswer OutlineOf(string path, IReadOnlyList<GraphCard> cards)
    {
        var leading = new List<string> { $"outline of {path} ({ReportText.Counted(cards.Count, "declaration")}):" };

        if (cards.Count == 0)
        {
            leading.Add("no declaration");
        }

        return new(
            leading,
            cards.Count == 0 ? [] : [Section(null, [.. cards.Select(card => new GraphEntry(GraphLines.Of(card)))], OutlineLines)],
            [],
            [],
            []);
    }

    /// <summary>A type outline's leading lines: its card, and where it holds none, that said.</summary>
    private IReadOnlyList<string> MembersOf(GraphCard card, IReadOnlyList<GraphCard> members)
    {
        var leading = new List<string> { GraphLines.Of(card) };

        if (members.Count == 0)
        {
            leading.Add("no declaration inside it");
        }

        return leading;
    }

    /// <summary>The repository outline's leading lines: the view, its commit and its files, and where it holds none, that said.</summary>
    private IReadOnlyList<string> RepositoryOf(IReadOnlyList<string> lines)
    {
        var leading = new List<string> { $"{_queries.View} at {_queries.Head ?? "no commit"}: {ReportText.Counted(_queries.FileCount, "file")}" };

        if (lines.Count == 0)
        {
            leading.Add("nothing to outline");
        }

        return leading;
    }

    private async Task<GraphToolAnswer> BodyAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var full = Flag(input, "full");

        return await PagedAsync(input, GraphOp.Body, $"body {string.Join(", ", Strings(input, "symbols"))}", async token =>
        {
            var entries = new List<GraphEntry>();
            var asked = new List<string>();
            var viewed = new List<string>();

            foreach (var symbol in Strings(input, "symbols"))
            {
                if (Resolved(symbol) is not { } card)
                {
                    // A symbol that names no one node is said inside the answer, as its own lines are: a body is one stream.
                    entries.AddRange(await UnresolvedEntriesAsync(symbol, token).ConfigureAwait(false));
                    continue;
                }

                asked.Add(card.Id);

                var (lines, why) = await ReadLinesAsync(card.Path, token).ConfigureAwait(false);
                var hash = lines is null || card.Hash is null ? null : Hash(lines, card);

                // A declaration whose lines moved since the view was read is extracted again, where the tool may check its file, and served as it is now.
                if (hash is not null && hash != card.Hash && reExtract is not null)
                {
                    if ((await ExtractedAgainAsync(card.Path, token).ConfigureAwait(false)).Part(card.Id, card.Path) is not { } found)
                    {
                        entries.Add(new($"'{card.Qualified}' is no longer declared in '{card.Path}'", Lines: 0));
                        continue;
                    }

                    card = found;
                    (lines, why) = await ReadLinesAsync(card.Path, token).ConfigureAwait(false);
                    hash = lines is null ? null : Hash(lines, card);
                }

                if (lines is null)
                {
                    entries.Add(new($"'{card.Qualified}' is not read: {why}", Lines: 0));
                    continue;
                }

                // A span the file no longer reaches has no lines to number: said, never served from where they are not.
                if (hash == Gone)
                {
                    entries.Add(new($"'{card.Qualified}' is not at {card.Path}:{card.Start}-{card.End} now: the file holds {ReportText.Counted(lines.Count, "line")}; find it again", Lines: 0));
                    continue;
                }

                var head = $"{card.Handle} {card.Qualified} {card.Path}:{card.Start}-{card.End}";

                // A whole file is no declaration and has no hash; a declaration's is the one its lines give now.
                if (hash is not null)
                {
                    head += $" #{hash}";

                    if (hash != card.Hash)
                    {
                        head += $" (changed since the graph read it as #{card.Hash})";
                    }
                }

                // The block's head and every line of it name the node, so a page that ends inside it says it goes on.
                entries.Add(new(head, Lines: 0, GoesOn: card.Qualified, Node: card.Id));

                foreach (var (text, number) in Shown(card, CodebaseGraphQueries.Body(card, lines), full))
                {
                    entries.Add(number is { } line
                        ? new($"{line.ToString(CultureInfo.InvariantCulture)}\t{text}", Lines: 1, GoesOn: card.Qualified, Node: card.Id)
                        : new(text, Lines: 0, GoesOn: card.Qualified, Node: card.Id));
                }

                viewed.Add(card.Path);

                if (_queries.Unsure(card.Id))
                {
                    entries.AddRange(await HitEntriesAsync(card.Name, token).ConfigureAwait(false));
                }
            }

            return new GraphAnswer([], [Section(null, entries, BodyLines)], [], asked.Distinct(StringComparer.Ordinal).ToList(), viewed.Distinct(StringComparer.Ordinal).ToList());
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphToolAnswer> OneEditAtATimeAsync(JsonObject input, CancellationToken cancellationToken)
    {
        await _editing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await EditAsync(input, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _editing.Release();
        }
    }

    private async Task<GraphToolAnswer> EditAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;
        var hash = Str(input, "hash")!.TrimStart('#');
        var text = Str(input, "text")!;

        if (CodeFileText.Lines(text).Count > MostEditLines)
        {
            return Usage($"text holds {ReportText.Counted(CodeFileText.Lines(text).Count, "line")}: an edit gives at most {MostEditLines}");
        }

        if (Resolved(symbol) is not { } card)
        {
            return await UnresolvedAsync(input, GraphOp.Edit, symbol, cancellationToken);
        }

        if (!string.Equals(card.Hash, hash, StringComparison.OrdinalIgnoreCase)
            && _queries.Parts(card.Id).FirstOrDefault(part => string.Equals(part.Hash, hash, StringComparison.OrdinalIgnoreCase) && Reads(part)) is { } named)
        {
            card = named;
        }

        if (card.Hash is null)
        {
            return Said($"'{card.Qualified}' is a whole file, and no declaration an edit replaces: edit a declaration in it");
        }

        if ((mayWrite?.Invoke(card.Path) ?? new AgentPolicyAnswer.Denied("graph", AgentPolicySource.Builtin, "edit is not given to this step")).Refusal() is { } denied)
        {
            return new GraphToolAnswer(denied.Reason, GraphToolOutcome.Refused, card.Path, denied);
        }

        // The file is written where the runtime's own tools may write it, and never through a link: one put along its path
        // since the view was read is refused as theirs is.
        if (_paths.Write(Full(card.Path)).Denied is { } confined)
        {
            return new GraphToolAnswer(confined.Reason, GraphToolOutcome.Refused, card.Path, confined);
        }

        // An edit answers the card its file's new extract gives, and finds a node that moved by it: a view it cannot check again is no view to edit by.
        if (reExtract is null)
        {
            return Said("edit is not run here: it needs the step's view, which this tool cannot check again");
        }

        var (reading, why) = await ReadFileAsync(card.Path, cancellationToken).ConfigureAwait(false);

        if (reading is null)
        {
            return Said(why!);
        }

        var now = Hash(CodeFileText.Lines(reading.Text!.Text), card);

        // The node may only have moved since the view was read: its file is checked again, and the hash compared where it stands now.
        if (!Same(now, hash))
        {
            if ((await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false)).Part(card.Id, card.Path) is not { } found)
            {
                return Said($"'{card.Qualified}' is no longer declared in '{card.Path}': find it again");
            }

            card = found;
            (reading, why) = await ReadFileAsync(card.Path, cancellationToken).ConfigureAwait(false);

            if (reading is null)
            {
                return Said(why!);
            }

            now = Hash(CodeFileText.Lines(reading.Text!.Text), card);

            // Optimistic concurrency: the lines are written only where they are the ones the agent saw.
            if (!Same(now, hash))
            {
                return Said($"'{card.Qualified}' changed since you saw it (#{hash}, now #{now}, lines {card.Start}-{card.End}): read it again and edit with the new hash\n{GraphLines.Of(card with { Hash = now })}");
            }
        }

        var edited = EditedText.Of(reading.Text!);
        var was = edited.LineCount;
        edited.ReplaceLines(card.Start, card.End, text);
        var moved = edited.LineCount - was;
        // Where it lands decided again before a byte is written, as the runtime's own writes are (AgentFiles.Write).
        if (_files.Write(_paths.WritableRootOf(Full(card.Path)), Full(card.Path), edited.Bytes(), landing => _paths.Opened(landing, write: true).Denied) is { } landed)
        {
            return new GraphToolAnswer(landed.Reason, GraphToolOutcome.Refused, card.Path, landed);
        }

        var after = await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false);
        var written = card.End - card.Start + 1 + moved;
        var shift = moved == 0 ? "no later declaration moved" : $"the file's later declarations moved {ReportText.Counted(Math.Abs(moved), "line")} {(moved > 0 ? "down" : "up")}";
        var held = (after.OutlineFile(card.Path) ?? []).Where(one => one.Start >= card.Start && one.Start < card.Start + written).Select(one => "  " + GraphLines.Of(one)).ToList();
        var said = after.Part(card.Id, card.Path) is { } refound ? GraphLines.Of(refound)
            : written == 0 ? $"the new lines are none: '{card.Qualified}' is gone from '{card.Path}'"
            : held.Count == 0 ? $"the new lines no longer declare '{card.Qualified}', nor anything else"
            : $"the new lines no longer declare '{card.Qualified}'; they hold:\n" + string.Join('\n', held);
        var editedAnswer = await PagedAsync(input, GraphOp.Edit, $"edit {symbol}", token => Task.FromResult(new GraphAnswer(
            [$"edited '{card.Qualified}' in '{card.Path}'; {shift}"],
            [Section(null, [.. said.Split('\n').Where(line => line.Length > 0).Select(line => new GraphEntry(line))], OutlineLines)],
            UnheldLines(),
            [],
            [card.Path])), cancellationToken).ConfigureAwait(false);

        if (editedAnswer.Paging?.Refusal is not null)
        {
            return Usage(editedAnswer.Text);
        }

        return editedAnswer with
        {
            Outcome = GraphToolOutcome.Wrote,
            Path = card.Path,
            Facts = new GraphToolFacts([], [], [card.Id], new GraphWrittenLines(card.Path, card.Start, written, WrittenHash(edited, card.Start, written))),
            Viewed = [card.Path],
        };
    }

    /// <summary>The hash of the <paramref name="count"/> lines from <paramref name="start"/> of a file as an edit left it, as a node's is; null where there are none.</summary>
    private static string? WrittenHash(EditedText edited, int start, int count)
    {
        var lines = CodeFileText.Lines(edited.Text);
        return count > 0 && start + count - 1 <= lines.Count ? CodeGraphIds.Digest(lines, start, start + count - 1)[..8] : null;
    }

    /// <summary>
    /// The view after the file at <paramref name="path"/> is checked again, which the tool answers from from then on: one
    /// check at a time, so a view a later check left is never put back by an earlier one.
    /// </summary>
    private async Task<CodebaseGraphQueries> ExtractedAgainAsync(string path, CancellationToken cancellationToken)
    {
        await _extracting.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return _queries = await reExtract!(path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _extracting.Release();
        }
    }

    /// <summary>
    /// The view the tool answers from from now on: the one <paramref name="read"/> gives - what the sync after a write or a
    /// command of another tool's left - read under the one lock an edit's and a moved body's extraction take, so a view an
    /// earlier read gave never replaces one a later read gave. Where nothing may be synced (the verb), nothing changes.
    /// </summary>
    /// <param name="read">Reads the view as it now stands.</param>
    /// <param name="cancellationToken">Stops the wait and the read.</param>
    public async Task RefreshAsync(Func<CancellationToken, Task<CodebaseGraphQueries>> read, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (reExtract is null)
        {
            return;
        }

        await _extracting.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            _queries = await read(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _extracting.Release();
        }
    }

    /// <summary>
    /// <c>wholeFile</c>: the narrowing of reads lifted for one file the view holds as parsed, for the rest of the step - an
    /// escape stated, with the lines and the tokens the view read the file as and the agent's reason. A path the view does
    /// not hold as parsed - none there, binary, a file no implementation reads - is a misuse, said, for no read of it is
    /// narrowed; and so is one the step's reads do not reach.
    /// </summary>
    private GraphToolAnswer WholeFile(JsonObject input)
    {
        var path = Str(input, "path")!;
        var reason = Str(input, "reason")!;

        if (Held(path) is not { } held || !_queries.Parsed(held) || _queries.FileAt(held) is not { Lines: > 0 and var lines } file)
        {
            return Usage($"the view holds no file it parsed at '{path}': wholeFile lifts the narrowing of a file the graph parsed, and no whole read of any other is narrowed");
        }

        if (!_mayRead(held))
        {
            return Usage($"'{held}' is a file your reads do not reach");
        }

        _lifted.TryAdd(held, true);

        var paged = GraphPages.Assemble([], [$"the narrowing of reads is lifted for '{held}' for the rest of this step: {reason}"], UnheldLines(), Int(input, "page") ?? 1, $"wholeFile {path}", MostCharacters);

        return new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered)
        {
            Escape = new GraphEscapeLine(held, lines, EstimatedTokens.OfBytes(file.Bytes), GraphEscapeLine.Stated, reason),
            Paging = paged,
        };
    }

    /// <summary>
    /// The node <paramref name="symbol"/> names where it names one the step may read; null otherwise. A node whose part that
    /// speaks for it is in a file the step may not read is not served by another part: its edges come from that part too.
    /// </summary>
    private GraphCard? Resolved(string symbol) => _queries.Resolve(symbol) is GraphResolve.Found found && Reads(found.Card) ? found.Card : null;

    /// <summary>What a symbol that names no one node the step reads is answered with: its candidates, or a text search for it - in pages as any answer.</summary>
    private async Task<GraphToolAnswer> UnresolvedAsync(JsonObject input, GraphOp op, string symbol, CancellationToken cancellationToken)
    {
        switch (_queries.Resolve(symbol))
        {
            case GraphResolve.Candidates candidates:
                var (kept, left) = Readable(candidates.Cards);

                return await PagedAsync(input, op, $"{NameOf(op)} {symbol}", token => Task.FromResult(new GraphAnswer(
                    [$"'{symbol}' is not one name: {ReportText.Counted(kept.Count, "node")} carry it - ask again by a handle"],
                    [Section(null, [.. kept.Select(card => new GraphEntry(GraphLines.Of(card)))], FindLines)],
                    Notes(left),
                    [],
                    [])), cancellationToken).ConfigureAwait(false);

            case GraphResolve.Found:
                return Said($"'{symbol}' is in a file your reads do not reach");

            default:
                var name = Simple(symbol);
                var searched = name is null ? Searched.None : await SearchAsync(name, cancellationToken).ConfigureAwait(false);

                return await PagedAsync(input, op, $"{NameOf(op)} {symbol}", token => Task.FromResult(new GraphAnswer(
                    [$"'{symbol}' is not a node the view holds"],
                    name is null ? [] : searched.SectionsOf(name),
                    name is null ? [] : [.. searched.NoHitsOf(name), .. searched.Notes],
                    [],
                    [])), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A body's entries for a symbol that names no one node the step reads: said, never served, and the text search where none is held.</summary>
    private async Task<IReadOnlyList<GraphEntry>> UnresolvedEntriesAsync(string symbol, CancellationToken cancellationToken)
    {
        switch (_queries.Resolve(symbol))
        {
            case GraphResolve.Candidates candidates:
                var (kept, left) = Readable(candidates.Cards);

                return [
                    new GraphEntry($"'{symbol}' is not one name: {ReportText.Counted(kept.Count, "node")} carry it - ask again by a handle", Lines: 0),
                    .. kept.Select(card => new GraphEntry(GraphLines.Of(card), Lines: 0)),
                    .. Notes(left).Select(note => new GraphEntry(note, Lines: 0))];

            case GraphResolve.Found:
                return [new($"'{symbol}' is in a file your reads do not reach", Lines: 0)];

            default:
                var entries = new List<GraphEntry> { new($"'{symbol}' is not a node the view holds", Lines: 0) };

                if (Simple(symbol) is { } name)
                {
                    entries.AddRange(await HitEntriesAsync(name, cancellationToken).ConfigureAwait(false));
                }

                return entries;
        }
    }

    /// <summary>What a text search gave: its hits as entries, and the notes of what it did not read.</summary>
    /// <param name="Hits">The hits, each one line - entries of the answer, at the text fallback's size a page.</param>
    /// <param name="Notes">The notes of the files the search did not read, said at the answer's end.</param>
    private sealed record Searched(IReadOnlyList<GraphEntry> Hits, IReadOnlyList<string> Notes)
    {
        /// <summary>A search that gave nothing and said nothing.</summary>
        public static Searched None { get; } = new([], []);

        /// <summary>The answer's sections of the hits: one, captioned with what it holds.</summary>
        public IReadOnlyList<GraphSection> SectionsOf(string word)
            => Hits.Count == 0 ? [] : [new GraphSection($"text: lines that hold '{word.Trim()}'", Hits, TextHits)];

        /// <summary>The note that ends an answer whose search gave nothing, where the word was one to search.</summary>
        public IReadOnlyList<string> NoHitsOf(string word)
            => Hits.Count == 0 && word.Trim().Length > 0 ? [$"text: no line holds '{word.Trim()}'"] : [];
    }

    /// <summary>The lines that hold <paramref name="name"/> as a whole word, case-sensitive, as <c>search_files</c> searches - the text a not-sure answer adds, and the notes of what it did not read.</summary>
    private async Task<Searched> SearchAsync(string name, CancellationToken cancellationToken)
    {
        var word = name.Trim();

        if (word.Length == 0)
        {
            return Searched.None;
        }

        try
        {
            var pattern = new Regex($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}_])", RegexOptions.CultureInvariant, AgentFiles.PatternBound);
            // Each file held to the tree as well as to what the graph may read: the search decides again where each file it
            // opened stands, and a link put on its way since may lead out of the tree, where no relative path reads. No
            // bound on the hits: the answer's pages hold them all, at the text fallback's size a page.
            var search = await _files.SearchAsync(_queries.Tree, pattern, null, StringComparison.Ordinal, 0, int.MaxValue, file => _paths.Read(file).Full is not null && _mayRead(Relative(file)), cancellationToken).ConfigureAwait(false);

            return new(
                [.. search.Hits.Select(hit => new GraphEntry($"  {hit.Path}:{hit.Line.ToString(CultureInfo.InvariantCulture)} {Hit(hit.Text.Trim())}", Lines: 1, Hit: true))],
                [.. search.LeftOut(path => path)]);
        }
        catch (RegexMatchTimeoutException)
        {
            return new([new($"text: the search for '{word}' took too long and was left off", Lines: 0)], []);
        }
    }

    /// <summary>The lines a text search for <paramref name="name"/> added inside a body's stream, where the graph was not sure of one of its symbols.</summary>
    private async Task<IReadOnlyList<GraphEntry>> HitEntriesAsync(string name, CancellationToken cancellationToken)
    {
        var searched = await SearchAsync(name, cancellationToken).ConfigureAwait(false);

        if (searched.Hits.Count == 0)
        {
            return [.. searched.NoHitsOf(name).Select(note => new GraphEntry(note, Lines: 0)), .. searched.Notes.Select(note => new GraphEntry(note, Lines: 0))];
        }

        // A body's fallback rides in its own stream: its caption says what its hits are, and the notes of what the search
        // did not read end the answer, where the pages leave them.
        return [new($"text: lines that hold '{name.Trim()}'", Lines: 0), .. searched.Hits, .. searched.Notes.Select(note => new GraphEntry(note, Lines: 0))];
    }

    /// <summary>
    /// The file at <paramref name="path"/> read whole, in its encoding, or why it is not: a place the runtime's own tools
    /// may not read (<see cref="ConfinedPaths.Read"/>) - a link out of the tree put along it since the view was read, by its
    /// path or, once opened, by where the open file stands (<see cref="AgentFiles.ReadAsync"/>) - none there, past the bytes
    /// a view holds whole (<see cref="AgentTools.MostViewedBytes"/>), or no text this tool reads.
    /// </summary>
    private async Task<(EncodedTextReading? Reading, string? Why)> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var confined = _paths.Read(Full(path));

        if (confined.Full is not { } full)
        {
            return (null, confined.Denied!.Reason);
        }

        if (_fileSystem.StandingOf(full) is not { } standing)
        {
            return (null, $"there is no file at '{path}'");
        }

        if (standing.Length > AgentTools.MostViewedBytes)
        {
            return (null, $"'{path}' holds {DiskSpace.Size(standing.Length)}, past the {DiskSpace.Size(AgentTools.MostViewedBytes)} a body or an edit reads whole: read a range of it");
        }

        var read = await _files.ReadAsync(full, AgentTools.MostViewedBytes, opened => _paths.Opened(opened, write: false).Denied, cancellationToken).ConfigureAwait(false);

        if (read.Denied is { } moved)
        {
            return (null, moved.Reason);
        }

        var reading = EncodedText.Read(read.Bytes!);
        return reading.IsRead ? (reading, null) : (null, $"'{path}' is {(reading.Binary ? "binary" : "no text this tool reads")}: {reading.Why}");
    }

    private async Task<(IReadOnlyList<string>? Lines, string? Why)> ReadLinesAsync(string path, CancellationToken cancellationToken)
    {
        var (reading, why) = await ReadFileAsync(path, cancellationToken).ConfigureAwait(false);
        return (reading is null ? null : CodeFileText.Lines(reading.Text!.Text), why);
    }

    /// <summary>The lines of <paramref name="body"/> as <c>body</c> gives them: a type's members' bodies left out unless <paramref name="full"/>, each member's signature kept.</summary>
    private IEnumerable<(string Text, int? Number)> Shown(GraphCard card, IReadOnlyList<string> body, bool full)
    {
        // A body is left out from the line its brace stands alone on, or from the line after the one it opens on - a
        // signature's own line (K&R, an arrow) is always kept.
        var gaps = !full && string.Equals(card.Kind, CodeNodeKinds.Type, StringComparison.Ordinal)
            ? [.. _queries.OutlineType(card.Id)
                .Where(member => member.BodyStart is { } bodyStart && string.Equals(member.Path, card.Path, StringComparison.Ordinal) && member.Start >= card.Start && member.End <= card.End && bodyStart >= card.Start && bodyStart - card.Start < body.Count)
                .Select(member => (From: body[member.BodyStart!.Value - card.Start].Trim() == "{" ? member.BodyStart!.Value : member.BodyStart!.Value + 1, member.End))
                .Where(gap => gap.From <= gap.End)]
            : new List<(int From, int End)>();

        for (var at = 0; at < body.Count; at++)
        {
            var line = card.Start + at;

            if (gaps.FirstOrDefault(gap => line >= gap.From && line <= gap.End) is { From: > 0 } gap)
            {
                if (line == gap.From)
                {
                    yield return ($"      … lines {gap.From.ToString(CultureInfo.InvariantCulture)}-{gap.End.ToString(CultureInfo.InvariantCulture)}, a member's body, left out; full: true gives them", null);
                }

                continue;
            }

            yield return (body[at], line);
        }
    }

    private static string Hash(IReadOnlyList<string> lines, GraphCard card)
        => card.Start < 1 || card.End < card.Start || card.End > lines.Count ? Gone : CodeGraphIds.Digest(lines, card.Start, card.End)[..8];

    /// <summary>The hash of a span the file no longer reaches: no lines to give, nor to replace.</summary>
    private const string Gone = "gone";

    /// <summary>Whether the hash a node's lines have <paramref name="now"/> is the one <paramref name="given"/>: never where the span is gone.</summary>
    private static bool Same(string now, string given) => now != Gone && string.Equals(now, given, StringComparison.OrdinalIgnoreCase);

    private static string Hit(string text) => text.Length <= MostHitCharacters ? text : TextCut.Head(text, MostHitCharacters) + " …";

    private string Full(string path) => Path.Combine(_queries.Tree, path.Replace('/', Path.DirectorySeparatorChar));

    private string Relative(string full) => Path.GetRelativePath(_queries.Tree, full).Replace('\\', '/');

    private bool Reads(GraphCard card) => _mayRead(card.Path);

    private (List<GraphCard> Kept, int Left) Readable(IReadOnlyList<GraphCard> cards)
    {
        var kept = cards.Where(Reads).ToList();
        return (kept, cards.Count - kept.Count);
    }

    /// <summary>The notes of nodes an answer left out because the step's reads do not reach their files.</summary>
    private static IReadOnlyList<string> Notes(int left) => left == 0 ? [] : [$"{ReportText.Counted(left, "node")} left out: in files your reads do not reach"];

    /// <summary>One section of an answer, of <paramref name="entries"/> at <paramref name="size"/> a page.</summary>
    private static GraphSection Section(string? caption, IReadOnlyList<GraphEntry> entries, int size) => new(caption, entries, size);

    /// <summary>
    /// The whole answer an operation computes, in the shape its pages read: what it begins with, its sections, the notes
    /// it ends with, the nodes its ask named and the files it served lines of.
    /// </summary>
    /// <param name="Leading">The lines the answer begins with, said on every page.</param>
    /// <param name="Sections">The answer's sections, in their order.</param>
    /// <param name="Trailing">The notes the answer ends with, said on its last page.</param>
    /// <param name="Nodes">The nodes the ask named, by their ids - what the answer's facts hold as asked.</param>
    /// <param name="Viewed">The files the answer served lines of, as the runtime counts views of them.</param>
    private sealed record GraphAnswer(
        IReadOnlyList<string> Leading,
        IReadOnlyList<GraphSection> Sections,
        IReadOnlyList<string> Trailing,
        IReadOnlyList<string> Nodes,
        IReadOnlyList<string> Viewed);

    /// <summary>
    /// The answer of an operation's page, from what <paramref name="compute"/> answers with or what an earlier page
    /// kept: the first page of an ask computes the whole answer once and keeps it - under the digest of the graph it was
    /// computed on - and a later page of the same ask reads what was kept, so it is a page of that one answer; where the
    /// graph has moved to another digest, the answer is made again on it and the page says so.
    /// </summary>
    /// <param name="input">The call's input, its page member telling the page asked.</param>
    /// <param name="op">The operation.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="compute">Computes the whole answer, where the page asked needs it.</param>
    /// <param name="cancellationToken">Stops the computing.</param>
    private async Task<GraphToolAnswer> PagedAsync(JsonObject input, GraphOp op, string asked, Func<CancellationToken, Task<GraphAnswer>> compute, CancellationToken cancellationToken)
    {
        var page = Int(input, "page") ?? 1;
        var key = GraphPageStore.KeyOf(NameOf(op), input);
        var digest = _queries.Graph;
        var kept = _pages?.Find(key);
        var answer = default(GraphAnswer);
        var recounted = false;

        if (page == 1 || kept is null || !string.Equals(kept.Digest, digest, StringComparison.Ordinal))
        {
            answer = await compute(cancellationToken).ConfigureAwait(false);
            recounted = page > 1 && kept is not null;
            _pages?.Keep(new GraphPageKept(GraphPageKept.CurrentSchema, key, digest, asked, answer.Leading, answer.Sections, answer.Trailing, answer.Nodes, answer.Viewed));
        }

        var held = answer ?? new GraphAnswer(kept!.Leading, kept.Sections, kept.Trailing, kept.Nodes, kept.Viewed);
        var paged = GraphPages.Assemble(held.Sections, held.Leading, [.. held.Trailing, .. UnheldLines()], page, asked, MostCharacters, recounted);

        if (paged.Refusal is not null)
        {
            return new GraphToolAnswer(paged.Text, GraphToolOutcome.Usage) { Paging = paged };
        }

        return new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered)
        {
            Facts = new GraphToolFacts(
                held.Nodes.ToArray(),
                paged.Entries.Where(entry => entry.Lines > 0 && entry.Node is not null).Select(entry => entry.Node!).Distinct(StringComparer.Ordinal).ToArray(),
                [],
                Hits: paged.Entries.Count(entry => entry.Hit)),
            Viewed = held.Viewed,
            Paging = paged,
        };
    }

    /// <summary>An answer of one sentence, no pages asked of it: said as it is, with the notes every answer ends with.</summary>
    private GraphToolAnswer Said(string text)
    {
        var paged = GraphPages.Assemble([], [text], UnheldLines(), 1, "graph", MostCharacters);

        return new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered) { Paging = paged };
    }

    private static string EdgeLine(GraphEdge edge)
        => string.Equals(edge.Confidence, CodeConfidences.Exact, StringComparison.Ordinal) ? GraphLines.Of(edge.Card) : $"{GraphLines.Of(edge.Card)} | {edge.Confidence}";

    /// <summary>The simple name <paramref name="symbol"/> ends with, a text search's word; null where it names a file or a line of one, which no word is.</summary>
    private static string? Simple(string symbol)
    {
        if (CodebaseGraphQueries.NamesAFile(symbol))
        {
            return null;
        }

        var cut = Math.Max(Math.Max(symbol.LastIndexOf(':'), symbol.LastIndexOf('.')), symbol.LastIndexOf('\\'));
        return cut >= 0 && cut < symbol.Length - 1 ? symbol[(cut + 1)..] : symbol;
    }

    private static IEnumerable<GraphOp> Ordered(IEnumerable<GraphOp> ops) => ops.Order();

    private static IEnumerable<(string Name, string Type, string Description)> Described() =>
    [
        ("query", "string", "the words to match, split at case changes, underscores, digits and dots as a name is."),
        ("kind", "string", $"the one kind to keep: {string.Join(", ", CodeNodeKinds.Extracted)}."),
        ("path", "string", "a file's path, relative to the tree with forward separators."),
        ("page", "integer", "which page of the answer to give, from 1; the first where it is left out."),
        ("symbol", "string", "a node: its handle, its id, a path:line, or a qualified or simple name."),
        ("relation", "string", "which edge to follow."),
        ("depth", "integer", $"how many hops, 1 to {MostDepth}; 1 where it is left out."),
        ("symbols", "array", $"1 to {BodySymbols} nodes to read, each a handle, an id or a name."),
        ("full", "boolean", "whether a type's members' bodies are given too."),
        ("hash", "string", "the node's hash as you last saw it; the edit is refused where its lines moved since."),
        ("text", "string", $"the node's new lines, at most {MostEditLines}."),
        ("reason", "string", "why the whole file is needed."),
    ];

    /// <summary>
    /// Why <paramref name="input"/> is no call of <paramref name="op"/> - a member it does not read, one it needs missing or
    /// empty, a bound passed - as its usage line; null where it is one. Asked of the input alone, before any view is read.
    /// </summary>
    /// <param name="input">The call's input.</param>
    /// <param name="op">The operation it asks.</param>
    public static string? Misused(JsonObject input, GraphOp op)
    {
        ArgumentNullException.ThrowIfNull(input);

        var (reads, needs) = Members(op);
        var extra = input.Select(member => member.Key).Where(name => name != "op" && !reads.Contains(name)).ToList();

        // The retired ways of going on are refused naming what took their place: a model meets one way to ask for more.
        if (extra.Count > 0)
        {
            return extra.All(name => name is "offset" or "limit")
                ? $"op '{Names[op]}' does not read {string.Join(", ", extra)}: an answer's further pages are asked by page, from 1"
                : $"op '{Names[op]}' does not read {string.Join(", ", extra)}: it reads {(reads.Count == 0 ? "nothing more" : string.Join(", ", reads))}";
        }

        if (Described().FirstOrDefault(member => input[member.Name] is not null && !Holds(input, member.Name, member.Type)) is { Name: { } wrong } typed)
        {
            return $"{wrong} is not {typed.Type switch { "integer" => "an integer", "array" => "an array of strings", var type => $"a {type}" }}: {typed.Description}";
        }

        if (needs.FirstOrDefault(name => input[name] is null) is { } missing)
        {
            return $"op '{Names[op]}' needs {missing}: {Described().First(member => member.Name == missing).Description}";
        }

        // A name that names nothing is no request: said, never thrown at the lookups.
        if (new[] { "query", "kind", "symbol", "path", "hash", "reason" }.FirstOrDefault(name => input[name] is not null && string.IsNullOrWhiteSpace(Str(input, name))) is { } blank)
        {
            return $"{blank} is empty: {Described().First(member => member.Name == blank).Description}";
        }

        if (Strings(input, "symbols").Any(string.IsNullOrWhiteSpace))
        {
            return $"op 'body' needs symbols: 1 to {BodySymbols} handles, ids or names, none empty";
        }

        if (op == GraphOp.Outline && input["path"] is not null && input["symbol"] is not null)
        {
            return "op 'outline' takes path or symbol, not both";
        }

        if (op == GraphOp.Body && Strings(input, "symbols") is not { Count: >= 1 and <= BodySymbols })
        {
            return $"op 'body' needs symbols: 1 to {BodySymbols} handles, ids or names";
        }

        if (op == GraphOp.Related && (Str(input, "relation") is not { } relation || !GraphRelations.All.Contains(relation)))
        {
            return $"relation is '{Str(input, "relation")}': give one of {string.Join(", ", GraphRelations.All)}";
        }

        return Int(input, "depth") is { } depth and (< 1 or > MostDepth) ? $"depth is {depth.ToString(CultureInfo.InvariantCulture)}: give 1 to {MostDepth}"
            : Int(input, "page") is { } asked and < 1 ? $"page is {asked.ToString(CultureInfo.InvariantCulture)}: pages start at 1"
            : null;
    }

    /// <summary>The note every answer ends with, of the files whose extract the view no longer holds, where there are any.</summary>
    private IReadOnlyList<string> UnheldLines()
    {
        var unheld = _queries.Unheld;

        if (unheld.Count == 0)
        {
            return [];
        }

        var readable = unheld.Where(_mayRead).ToList();
        var named = readable.Count == 0 ? string.Empty : ": " + string.Join(", ", readable.Take(UnheldNamed)) + (readable.Count > UnheldNamed ? ", …" : string.Empty);
        var hidden = unheld.Count - readable.Count;
        var unread = hidden == 0 ? string.Empty : $" ({ReportText.Counted(hidden, "file")} of them where your reads do not reach)";
        return [$"{ReportText.Counted(unheld.Count, "file")} whose extract the graph no longer holds, its declarations in no answer{named}{unread}"];
    }

    private static GraphToolAnswer Usage(string text) => new(text, GraphToolOutcome.Usage);

    private static bool Holds(JsonObject input, string name, string type) => type switch
    {
        "string" => Str(input, name) is not null,
        "integer" => Int(input, name) is not null,
        "boolean" => input[name] is JsonValue json && json.TryGetValue(out bool _),
        "array" => input[name] is JsonArray array && array.All(item => item is JsonValue one && one.TryGetValue(out string? _)),
        _ => true,
    };

    private static string? Str(JsonObject input, string name)
        => input.TryGetPropertyValue(name, out var value) && value is JsonValue json && json.TryGetValue(out string? text) ? text : null;

    private static int? Int(JsonObject input, string name)
        => input.TryGetPropertyValue(name, out var value) && value is JsonValue json && json.TryGetValue(out int number) ? number : null;

    private static bool Flag(JsonObject input, string name)
        => input.TryGetPropertyValue(name, out var value) && value is JsonValue json && json.TryGetValue(out bool flag) && flag;

    private static IReadOnlyList<string> Strings(JsonObject input, string name)
        => input.TryGetPropertyValue(name, out var value) && value is JsonArray array
            ? [.. array.OfType<JsonValue>().Select(json => json.TryGetValue(out string? text) ? text : null).OfType<string>()]
            : [];
}
