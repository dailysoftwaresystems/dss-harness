using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Codebase;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Help;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;
using DssHarness.Core.Text;

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

    /// <summary>A directory's files with their languages, lines, tests and top declarations; a file's or a type's declarations; or the repository's.</summary>
    Outline,

    /// <summary>A node's place: what it is declared in, its siblings and members, the declarations beside it, its callers and callees.</summary>
    Around,

    /// <summary>What a change of a node reaches - its callers at every distance, overrides, implementations, dispatchers, derived types - and the tests that reach any of them, each with its path.</summary>
    Impact,

    /// <summary>The exact lines of nodes, read fresh.</summary>
    Body,

    /// <summary>Every place that decides data: what defines, calls, uses, makes, references, inherits, implements or overrides it, every literal that holds it and every key it names.</summary>
    Sites,

    /// <summary>A node's lines replaced where its hash is unchanged.</summary>
    Edit,

    /// <summary>The narrowing of reads lifted for a file for the rest of the step.</summary>
    WholeFile,

    /// <summary>A page of the step's context pack, by its number: every page, from page 1, where its first message pushed none.</summary>
    Pack,

    /// <summary>A page of the codebase changes the step's message began, by its number.</summary>
    Changes,

    /// <summary>A page of the run's repository map, by its number: every page, from page 1, where its first message pushed none.</summary>
    Map,
}

/// <summary>What answering a <c>graph</c> call left: the text given to the model, how it ended, and the file a write touched.</summary>
/// <param name="Text">The answer, as the model reads it.</param>
/// <param name="Outcome">Whether it answered, refused a misuse, was refused by the policy, or wrote.</param>
/// <param name="Path">The file a write touched, or the policy refused, relative to the tree; null otherwise.</param>
/// <param name="Denied">The policy's refusal, where it refused the call.</param>
public sealed record GraphToolAnswer(string Text, GraphToolOutcome Outcome, string? Path = null, AgentPolicyAnswer.Denied? Denied = null)
{
    /// <summary>The nodes the call asked about, served and edited - what the step's working set is made of - and those a pack's page it gave listed, which it is not made of.</summary>
    public GraphToolFacts Facts { get; init; } = GraphToolFacts.None;

    /// <summary>
    /// The files the call leaves viewed, relative to the tree - those whose lines the page given served, and the one an
    /// edit wrote - each with the digest of the bytes the page's lines were read from, or the edit wrote: what the runtime
    /// records as the view of each for its edit check, as a <c>view_file</c> records the bytes it read. A page read from a
    /// kept result leaves the file as page 1 read it, so an edit made from stale lines is refused as changed since viewed.
    /// </summary>
    public IReadOnlyList<GraphFileRead> Viewed { get; init; } = [];

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
/// The nodes one <c>graph</c> call used, each by its id: those it asked about by name - <c>node</c>, <c>related</c>,
/// <c>around</c>, <c>impact</c> and <c>body</c> - those whose lines it served, and the one an <c>edit</c> wrote, with the
/// lines it wrote; and, apart from them, those a <c>pack</c> answer listed without serving them (<see cref="Listed"/>).
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

    /// <summary>
    /// The nodes the page of the context pack a <c>pack</c> answer gave lists, by id, each once, in the order it lists
    /// them (<see cref="GraphTextPage.Nodes"/>): a line of each, never its lines - so no use of any, and the working set
    /// counts none of them - kept so the step's figures say how many of the pages it pulled it then used
    /// (<c>pulled.pack.used</c>); none for every other call.
    /// </summary>
    public IReadOnlyList<string> Listed { get; init; } = [];
}

/// <summary>The lines one <c>edit</c> wrote: what a node's new text is matched to the nodes it declares by, whatever they are called now.</summary>
/// <param name="Path">The file, relative to the tree, as the view names it.</param>
/// <param name="Start">The first line written, from 1, as the file stood once it had.</param>
/// <param name="Count">How many lines it wrote; none where its text was empty.</param>
/// <param name="Hash">
/// Their hash, as a node's is (<see cref="CodeGraphIds.Digest(IReadOnlyList{string}, int, int)"/>, its first eight
/// characters): what finds them where a later write moved them; null where it wrote none.
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

/// <summary>
/// One page of a repository map the <c>graph</c> tool was asked for, as its serving reads it from where the map is kept:
/// the page's text, and how many pages the map holds - which a page past the last is refused by, as any answer's is.
/// </summary>
/// <param name="Pages">How many pages the map holds; 0 where the page could not be read.</param>
/// <param name="Text">The page's text; null where the page is past the last, or could not be read.</param>
/// <param name="Why">Why the page could not be read, where it could not; null otherwise.</param>
public sealed record GraphMapPage(int Pages, string? Text, string? Why = null);

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
/// What a whole read of one file is under the narrowing of reads (<c>graphReads: narrow</c>): answered with the file's
/// outline in its place; allowed as an escape <c>lowConfidence</c>, with why the graph is not sure of the file; or allowed
/// plainly.
/// </summary>
/// <param name="Narrowed">Whether the read is answered with the file's outline in its place.</param>
/// <param name="Text">
/// Where it is narrowed, the line the model reads first, saying what it is given and how to read the file's lines -
/// followed by the outline itself where <see cref="CodebaseGraphTool.WholeReadAsync"/> answers it; where it is an escape,
/// why the graph is not sure of the file.
/// </param>
/// <param name="Escape">The escape that lets the read - <see cref="GraphEscapeLine.LowConfidence"/> - or null where it is narrowed or needs none.</param>
/// <param name="Path">The file, by the path the view holds it at, where it is narrowed or let by as an escape - what its line records it as; null where the read passes plainly.</param>
public sealed record GraphWholeRead(bool Narrowed, string Text, string? Escape, string? Path = null)
{
    /// <summary>A read that passes plainly: not narrowed, no escape to record.</summary>
    public static GraphWholeRead Passed { get; } = new(Narrowed: false, string.Empty, null);
}

/// <summary>
/// The one tool <c>graph</c>, over the lookups of <see cref="CodebaseGraphQueries"/>: its operations - <c>find</c>,
/// <c>node</c>, <c>related</c>, <c>outline</c>, <c>around</c>, <c>impact</c>, <c>body</c>, <c>sites</c>, <c>edit</c> and
/// <c>wholeFile</c> - the same on every
/// client, each answering in pages of whole entries with a line range and a hash on every node, and a text search where the
/// graph is not sure; and <c>pack</c>, <c>changes</c> and <c>map</c>, the later pages of the context pack, of the codebase
/// changes a step's message began and of the repository map, where they go on past it. The schema a step is given holds
/// only the operations it may use. No step opens it: the tool
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
/// Where the page that computes an answer of more than one page keeps its whole result - the conversation's
/// <c>graph-pages</c> directory, a step's or a side-task's - so its later pages, and a resumed or continued
/// conversation's, are pages of that one result; null where none is kept (the verb).
/// </param>
/// <param name="mask">
/// The secrets of the run the tool serves: a result of more than one page is masked by them as it is computed, before it
/// is paged and kept, so its pages and what the conversation's records keep of it hold none, as every answer the run is
/// given holds none (<see cref="AgentToolRuntime"/>); null where the tool serves a person's own view (the verb).
/// </param>
/// <param name="pack">
/// The pages of the step's context pack, read at each <c>pack</c> - every page, from page 1, where its first message pushed
/// none - a side-task's following its last message's pack; null, or answering null, where it has none.
/// </param>
/// <param name="changes">The pages of the codebase changes the step's message began, read at each <c>changes</c>; null, or answering null, where it was told none.</param>
/// <param name="map">A page of the run's repository map, read at each <c>map</c> by its number - every page, from page 1, where its first message pushed none; null, or answering none, where it was given no map.</param>
public sealed partial class CodebaseGraphTool(
    CodebaseGraphQueries queries,
    AgentFiles files,
    IFileSystem fileSystem,
    ConfinedPaths paths,
    Func<string, bool> mayRead,
    Func<string, AgentPolicyAnswer>? mayWrite = null,
    Func<string, CancellationToken, Task<CodebaseGraphQueries>>? reExtract = null,
    Func<GraphNearness?>? near = null,
    GraphPageStore? pages = null,
    Execution.ILineMask? mask = null,
    Func<GraphTextPages?>? pack = null,
    Func<GraphTextPages?>? changes = null,
    Func<int, GraphMapPage>? map = null)
{
    /// <summary>
    /// The most characters an answer holds (<see cref="PageRoom.GraphAnswer"/>): its pages hold whole entries within it
    /// (<see cref="GraphPages"/>), and an entry wider than a page's room is given in pieces, a page each, each marked where
    /// it goes on.
    /// </summary>
    public const int MostCharacters = PageRoom.GraphAnswer;

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

    /// <summary>The hits a page of the text fallback holds of each word it searched, a section of its own in every operation's answer, a body's among them.</summary>
    public const int TextHits = 20;

    /// <summary>The names of the operations, as <c>op</c> says them.</summary>
    private static readonly IReadOnlyDictionary<GraphOp, string> Names = new Dictionary<GraphOp, string>
    {
        [GraphOp.Find] = "find",
        [GraphOp.Node] = "node",
        [GraphOp.Related] = "related",
        [GraphOp.Outline] = "outline",
        [GraphOp.Around] = "around",
        [GraphOp.Impact] = "impact",
        [GraphOp.Body] = "body",
        [GraphOp.Sites] = "sites",
        [GraphOp.Edit] = "edit",
        [GraphOp.WholeFile] = "wholeFile",
        [GraphOp.Pack] = "pack",
        [GraphOp.Changes] = "changes",
        [GraphOp.Map] = "map",
    };

    private readonly AgentFiles _files = files ?? throw new ArgumentNullException(nameof(files));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly ConfinedPaths _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    private readonly Func<string, bool> _mayRead = mayRead ?? throw new ArgumentNullException(nameof(mayRead));
    private readonly SemaphoreSlim _extracting = new(1, 1);
    private readonly SemaphoreSlim _editing = new(1, 1);
    private readonly GraphPageStore? _pages = pages;

    /// <summary>The pages of each result the store holds, laid out once (<see cref="GraphPages.Lay"/>), by the result: a later page is given from its layout, never filled again.</summary>
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<GraphPageKept, PageLayout<GraphEntry>> _layouts = new();
    private readonly Execution.ILineMask? _mask = mask;
    private CodebaseGraphQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));

    /// <summary>How the tree's file system compares names, as it answers for itself: asked once, the tree being the tool's for its life.</summary>
    private StringComparison? _case;

    /// <summary>The read operations, which a step with the <c>read</c> capability and the verb are given.</summary>
    public static IReadOnlyList<GraphOp> Read { get; } = [GraphOp.Find, GraphOp.Node, GraphOp.Related, GraphOp.Outline, GraphOp.Around, GraphOp.Impact, GraphOp.Body, GraphOp.Sites];

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

    /// <summary>
    /// The operations a step - or a side-task - is given: the read ones always, <c>edit</c> where it may write,
    /// <c>wholeFile</c> where its reads are narrowed, <c>pack</c> where its context pack has a page its first message
    /// does not hold - every page, from page 1, where none was pushed - <c>changes</c> where the codebase changes it is
    /// told go on past its message's, and <c>map</c> wherever there is a repository map: every page of it, from page 1,
    /// for nothing given the tool is pushed the map - the one rule of it, the steps' and the side-task's alike.
    /// </summary>
    /// <param name="write">Whether the step has the <c>write</c> capability.</param>
    /// <param name="narrow">Whether the step's reads are narrowed (<c>graphReads: narrow</c>).</param>
    /// <param name="pack">Whether the step's context pack has a page its first message does not hold.</param>
    /// <param name="changes">Whether the codebase changes the step is told have a page after its message's.</param>
    /// <param name="mapPages">How many pages the repository map the step's tool serves holds; 0 where there is none.</param>
    public static IReadOnlySet<GraphOp> Operations(bool write, bool narrow, bool pack = false, bool changes = false, int mapPages = 0)
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

        if (pack)
        {
            ops.Add(GraphOp.Pack);
        }

        if (changes)
        {
            ops.Add(GraphOp.Changes);
        }

        if (mapPages > 0)
        {
            ops.Add(GraphOp.Map);
        }

        return ops;
    }

    /// <summary>
    /// The most lines a file may hold and a whole read of it still pass the narrowing: a longer one the view holds as
    /// parsed is answered with its outline in its place (<c>graphReads: narrow</c>, <see cref="WholeReadAsync"/>).
    /// </summary>
    public const int NarrowLines = 200;

    /// <summary>
    /// The files whose narrowing a <c>wholeFile</c> lifted, each once, for the rest of the step: read by every narrowed read
    /// and written by a <c>wholeFile</c> on whichever thread serves it - Claude Code's served calls run beside its own reads.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _lifted = new(StringComparer.Ordinal);

    /// <summary>
    /// What a whole read of the file at <paramref name="path"/> of <paramref name="lines"/> lines is under the narrowing
    /// (<c>graphReads: narrow</c>): narrowed - answered with its outline, the line said first saying how to read its lines
    /// (<see cref="WholeReadAsync"/>) - where the view holds the file as parsed and it is over <see cref="NarrowLines"/>
    /// lines; or allowed, an escape <c>lowConfidence</c> with why where the graph is not sure of it, plainly where the view
    /// does not hold it as parsed, it is short enough, or a <c>wholeFile</c> lifted it.
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
                $"'{ReportText.Printable(held)}' is {lines.ToString(CultureInfo.InvariantCulture)} lines and in the codebase graph, so its outline is given in place of the whole file: read the lines you need with start_line and end_line, or the file a page at a time with page, ask graph's body for a declaration by its handle, or, if you need the file whole, say why with graph's wholeFile",
                null,
                held)
            : GraphWholeRead.Passed;
    }

    /// <summary>Whether a <c>wholeFile</c> lifted the narrowing of the file at <paramref name="path"/> for the rest of the step.</summary>
    /// <param name="path">The file, relative to the tree, as the reader spells it.</param>
    public bool Lifted(string path) => Held(path) is { } held && _lifted.ContainsKey(held);

    /// <summary>The path the view holds a parsed file at that <paramref name="path"/> names, where no <c>wholeFile</c> lifted it; null otherwise.</summary>
    private string? Narrowable(string path)
        => Held(path) is { } held && _queries.Parsed(held) && !_lifted.ContainsKey(held) ? held : null;

    /// <summary>
    /// The path the view holds the file <paramref name="path"/> names at, by the tree's own rule of case: as given, or the one
    /// file the view holds in another case where the tree's file system finds a name in any case; null where it holds none.
    /// </summary>
    private string? Held(string path) => Held(_queries, path);

    /// <summary>The path <paramref name="view"/> holds the file <paramref name="path"/> names at, as <see cref="Held(string)"/> reads it of the view the tool answers from now.</summary>
    private string? Held(CodebaseGraphQueries view, string path)
        => view.HeldPath(path, _case ??= _paths.ComparisonIn(view.Tree));

    /// <summary>
    /// The members each operation reads beside <c>op</c>, and of them those it needs. A read takes <c>page</c>; a write
    /// does not - an <c>edit</c>'s answer and a <c>wholeFile</c>'s are one page, said after the write, never paged - so a
    /// <c>page</c> given to one is refused before anything is written.
    /// </summary>
    /// <param name="op">An operation.</param>
    public static (IReadOnlyList<string> Reads, IReadOnlyList<string> Needs) Members(GraphOp op) => op switch
    {
        GraphOp.Find => (["query", "kind", "path", "page"], ["query"]),
        GraphOp.Node => (["symbol", "page"], ["symbol"]),
        GraphOp.Related => (["symbol", "relation", "depth", "page"], ["symbol", "relation"]),
        GraphOp.Outline => (["path", "symbol", "page"], []),
        GraphOp.Around or GraphOp.Impact => (["symbol", "page"], ["symbol"]),
        GraphOp.Body => (["symbols", "full", "page"], ["symbols"]),
        GraphOp.Sites => (["data", "page"], ["data"]),
        GraphOp.Edit => (["symbol", "hash", "text"], ["symbol", "hash", "text"]),
        GraphOp.WholeFile => (["path", "reason"], ["path", "reason"]),
        GraphOp.Pack or GraphOp.Changes or GraphOp.Map => (["page"], ["page"]),
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
        }.ToJsonString(AgentEnvelope.Written);
    }

    /// <summary>
    /// What the model is told the tool does, for a step given <paramref name="ops"/>: the operations it may use alone, each
    /// by the question it answers, with their bounds - and, where it may use all three, that <c>outline</c>, <c>around</c>
    /// and <c>impact</c> answer in one call what takes many reads and searches.
    /// </summary>
    /// <param name="ops">The operations the step may use.</param>
    public static string Description(IReadOnlySet<GraphOp> ops)
    {
        ArgumentNullException.ThrowIfNull(ops);

        var said = new Dictionary<GraphOp, string>
        {
            [GraphOp.Find] = $"find - which nodes carry these words? best first, {FindLines} a page",
            [GraphOp.Node] = $"node - what is this node, and what is joined to it? its card and its edges by kind, {NodeLinesAKind} of each a page",
            [GraphOp.Related] = $"related - what does one relation reach from it? as many hops as you ask, by distance, {RelatedLines} a page",
            [GraphOp.Outline] = $"outline - what does this directory, file or type hold? a directory's every file, its subdirectories' too, with its language, its lines, whether it is a test and what it tests, and its top declarations; a file's or a type's declarations; with neither, the repository's; {OutlineLines} a page",
            [GraphOp.Around] = $"around - where does this node stand? what it is declared in - file, namespace, type - its siblings, its members, the declarations just before and after it, its callers and callees, {NodeLinesAKind} of each a page",
            [GraphOp.Impact] = $"impact - what does changing it reach? its callers at every distance, what it overrides and what overrides it, its implementations, dispatchers and derived types, and every test that reaches any of them, each with the path it reaches by, {RelatedLines} of each a page",
            [GraphOp.Body] = $"body - what are its lines? the lines of the nodes you name, read fresh and numbered, {BodyLines} lines a page",
            [GraphOp.Sites] = $"sites - what decides this data? every place that decides data you name - a symbol (a handle, an id, a path:line or a name), a key path (dotted - a name holding a dot or a bracket quoted in brackets, A[\"B.C\"] or A['B.C'], or its dots as they are, A.B.C, asking of every key it may mean; or as .NET's configuration writes one: A:B, A__B), a rule's selector, a quoted literal or a diagnostic id: what defines, calls, uses, makes, references, inherits, implements or overrides it, every literal that holds it, every key it names - each place one card saying every way it decides, an edge's other than exact with how sure it is, its lines beneath it on the page it stands on, the cards grouped by file kind and tier, {RelatedLines} places a page; what the graph does not answer of it - files it does not hold, holds unread, no longer holds the extract of or is unsure of, files it outlines, files with a reference of its name it bound to nothing, and for a quoted literal every file - searched by text and said by the coverage lines that end every answer, with every limit of each language whose files it read - one some file shows the sign of in full, with how many show it, every other by its short name on one line a language, 'help {HelpTopics.CodebaseGraphLanguages}' saying each in full",
            [GraphOp.Edit] = "edit - a node's lines replaced where the hash you give is its hash now",
            [GraphOp.WholeFile] = "wholeFile - lifts the narrowing of reads for one file for the rest of the step, with why",
            [GraphOp.Pack] = "pack - a page of your task's context pack - the part of the graph your task and the work before it reach - best first, from page 1 (where your first message holds a codebase part, that is page 1), its last line saying how to ask for the next",
            [GraphOp.Changes] = "changes - a page of the codebase changes your message began, its last line saying how to ask for the next",
            [GraphOp.Map] = "map - a page of the repository map - the tree at a glance: its projects and top directories, then its most central declarations - from page 1, its last line saying where the rest is",
        };

        // Said where the step may use all three: they are what saves the reads and searches an agent would make instead.
        var navigating = ops.Contains(GraphOp.Outline) && ops.Contains(GraphOp.Around) && ops.Contains(GraphOp.Impact)
            ? " Ask before view_file or search_files: one call of outline, around or impact answers what takes many file reads and searches, and you then read only the lines it names."
            : string.Empty;

        return "Asks the codebase graph of the tree you work in: "
            + string.Join("; ", Ordered(ops).Select(op => said[op]))
            + $". Every node is named with its handle and path:start-end #hash - read a range of a file by those lines - and every answer of a read is pages of whole entries, a line wider than a page given in pieces marked …: one of more than one page starts with the line 'page k of n' and any page is asked for with page, from 1; an answer with no header holds all of it. Where the graph is not sure, the answer adds text: lines that hold the name."
            + navigating;
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
            return Usage($"op '{ReportText.Printable(name)}' is not one this step may use: {given}");
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
            GraphOp.Around => await AroundAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Impact => await ImpactAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Body => await BodyAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Sites => await SitesAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Edit => await OneEditAtATimeAsync(input, cancellationToken).ConfigureAwait(false),
            GraphOp.Pack => TextPage(pack?.Invoke(), "pack", "context pack", Int(input, "page")!.Value, lists: true),
            GraphOp.Changes => TextPage(changes?.Invoke(), "changes", "codebase changes", Int(input, "page")!.Value, lists: false),
            GraphOp.Map => MapPage(map?.Invoke(Int(input, "page")!.Value), Int(input, "page")!.Value),
            _ => WholeFile(input),
        };
    }

    /// <summary>
    /// <c>pack</c> and <c>changes</c>: page <paramref name="page"/> of the step's pack or of the changes its message began, as
    /// it was laid whole when it was made, its first and last lines with it - a pack's page saying the nodes it lists
    /// where <paramref name="lists"/> (<see cref="GraphToolFacts.Listed"/>); a page past the last refused with how many
    /// there are, as any answer's is, and a text the step was not given said.
    /// </summary>
    private static GraphToolAnswer TextPage(GraphTextPages? text, string asked, string what, int page, bool lists)
    {
        if (text is null)
        {
            return Usage($"this step was given no {what} to page");
        }

        if (text.Page(page) is { } given)
        {
            return new GraphToolAnswer(given.Text, GraphToolOutcome.Answered) { Facts = lists ? GraphToolFacts.None with { Listed = given.Nodes } : GraphToolFacts.None };
        }

        return Usage(Paging.PastTheLastSaid(page, text.Count, asked, note: null));
    }

    /// <summary>
    /// <c>map</c>: page <paramref name="page"/> of the run's repository map, from page 1 - a step with the tool is pushed none
    /// of it - read from where the run keeps it; a page past the last refused with how many there are, as any answer's is, a map the run was given
    /// none of said, and a page that cannot be read said with why.
    /// </summary>
    /// <param name="read">The page as the run's serving of its map read it; null where the run was given no map.</param>
    /// <param name="page">The page asked, from 1.</param>
    private static GraphToolAnswer MapPage(GraphMapPage? read, int page)
        => read is not { Pages: > 0 }
            ? Usage(read is { Why: { Length: > 0 } why } ? $"the repository map's page {Paging.Number(page)} could not be read: {why}" : "this run was given no repository map to page")
            : read.Text is { } text ? new GraphToolAnswer(text, GraphToolOutcome.Answered)
            : Usage(Paging.PastTheLastSaid(page, read.Pages, "map", note: null));

    private async Task<GraphToolAnswer> FindAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var query = Str(input, "query")!;
        var kind = Str(input, "kind");
        var view = _queries;
        var path = Str(input, "path") is { } given ? Held(view, given) ?? given : null;

        return await PagedAsync(input, view, GraphOp.Find, new JsonObject { ["query"] = query, ["kind"] = kind, ["path"] = path }, $"find {ReportText.Printable(query)}", async (on, token) =>
        {
            var matched = on.Find(query, kind, path, int.MaxValue, 0, near?.Invoke()).Cards;
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
        var view = _queries;

        if (Resolved(view, symbol) is not { } card)
        {
            return await UnresolvedAsync(input, view, GraphOp.Node, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, view, GraphOp.Node, new JsonObject { ["symbol"] = card.Id }, $"node {ReportText.Printable(symbol)}", async (on, token) =>
        {
            var leading = new List<string> { GraphLines.Of(card) };
            var sections = new List<GraphSection>();
            var notes = new List<string>();
            var left = 0;
            var unsure = on.Unsure(card.Id);

            foreach (var (group, members) in on.Edges(card.Id).Groups)
            {
                var kept = members.Where(edge => Reads(edge.Card)).ToList();
                left += members.Count - kept.Count;

                if (kept.Count == 0)
                {
                    continue;
                }

                // Every edge of the kind decides whether the graph is sure of the answer: no page's bound hides one. A use
                // is recall, never a doubt - its candidates say nothing of how sure the graph is of what it declares.
                unsure |= kept.Any(on.Doubts);
                sections.Add(Section($"{group} ({kept.Count}):", [.. kept.Select(edge => new GraphEntry("  " + EdgeLine(edge)))], NodeLinesAKind));
            }

            notes.AddRange(Notes(left));
            notes.AddRange(on.UnreadOf(card));

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
        var view = _queries;

        if (Resolved(view, symbol) is not { } card)
        {
            return await UnresolvedAsync(input, view, GraphOp.Related, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, view, GraphOp.Related, new JsonObject { ["symbol"] = card.Id, ["relation"] = relation, ["depth"] = depth }, $"related {ReportText.Printable(symbol)}", async (on, token) =>
        {
            var reached = on.Related(card.Id, relation, depth);
            var kept = reached.Where(one => Reads(one.Card)).ToList();
            var lines = kept.Select(one => $"{one.Distance}: {GraphLines.Of(one.Card)}").ToList();
            var leading = lines.Count == 0 ? [$"no node is {relation} of '{ReportText.Printable(card.Qualified)}'"] : Array.Empty<string>();
            var sections = new List<GraphSection>();
            var notes = new List<string>(Notes(reached.Count - kept.Count));

            if (on.Unsure(card.Id) || kept.Any(on.Doubts))
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
        var view = _queries;

        if (Str(input, "path") is { } given)
        {
            var path = Held(view, given) ?? given;
            var printed = ReportText.Printable(path);

            // A path the view holds no file at is a directory where it holds files under it: each of them outlined.
            if (view.OutlineFile(path) is null && view.FilesUnder(given, _case ??= _paths.ComparisonIn(view.Tree)) is not null)
            {
                var directory = CodebaseGraphQueries.DirectoryNamed(given);

                return await PagedAsync(input, view, GraphOp.Outline, new JsonObject { ["path"] = directory }, $"outline {DirectoryShown(directory)}", (on, token) => Task.FromResult(DirectoryOf(on, directory)), cancellationToken).ConfigureAwait(false);
            }

            if (!_mayRead(path))
            {
                return Said(input, $"outline {printed}", $"'{printed}' is a file your reads do not reach");
            }

            if (view.OutlineFile(path) is not { } cards)
            {
                return Said(input, $"outline {printed}", $"the view holds no file or directory at '{printed}'");
            }

            return await PagedAsync(input, view, GraphOp.Outline, new JsonObject { ["path"] = path }, $"outline {printed}", (on, token) => Task.FromResult(OutlineOf(on, path, cards)), cancellationToken).ConfigureAwait(false);
        }

        if (Str(input, "symbol") is { } symbol)
        {
            if (Resolved(view, symbol) is not { } card)
            {
                return await UnresolvedAsync(input, view, GraphOp.Outline, symbol, cancellationToken).ConfigureAwait(false);
            }

            return await PagedAsync(input, view, GraphOp.Outline, new JsonObject { ["symbol"] = card.Id }, $"outline {ReportText.Printable(symbol)}", (on, token) =>
            {
                var (kept, left) = Readable(on.OutlineType(card.Id));

                return Task.FromResult(new GraphAnswer(
                    MembersOf(card, kept),
                    kept.Count == 0 ? [] : [Section(null, [.. kept.Select(one => new GraphEntry("  " + GraphLines.Of(one)))], OutlineLines)],
                    Notes(left),
                    [],
                    []));
            }, cancellationToken).ConfigureAwait(false);
        }

        // The repository's overview is the answer's whole result: made once, by the page that computes it.
        return await PagedAsync(input, view, GraphOp.Outline, new JsonObject(), "outline", (on, token) =>
        {
            var overview = on.Overview();
            var lines = new List<string>();
            lines.AddRange(overview.Projects.Where(Reads).Select(project => "project " + GraphLines.Of(project)));
            lines.AddRange(overview.Directories
                .Select(directory => (directory.Directory, Files: directory.Files.Count(_mayRead)))
                .Where(directory => directory.Files > 0)
                .Select(directory => $"directory {ReportText.Printable(directory.Directory)} ({ReportText.Counted(directory.Files, "file")})"));
            lines.AddRange(overview.Namespaces
                .Where(one => one.Files.Any(_mayRead))
                .Select(one => $"namespace {ReportText.Printable(one.Card.Qualified)} ({ReportText.Counted(one.Declarations.Count(Reads), "declaration")})"));

            // The root's own files, outlined as a directory's are: what no top directory's count says.
            var (files, left) = FilesOf([.. overview.Directories.Where(directory => directory.Directory == ".").SelectMany(directory => directory.Files)]);
            List<GraphSection> sections = lines.Count == 0 ? [] : [Section(null, [.. lines.Select(line => new GraphEntry(line))], OutlineLines)];

            if (files.Count > 0)
            {
                sections.Add(Section($"{DirectoryShown(string.Empty)} ({ReportText.Counted(files.Count, "file")}):", [.. files.SelectMany(file => FileEntries(on, file))], OutlineLines));
            }

            return Task.FromResult(new GraphAnswer(RepositoryOf(on, lines), sections, FilesLeft(left), [], []));
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A directory outline's answer: every file the view holds under <paramref name="directory"/> - its subdirectories'
    /// among them - that the step may read, those it may not counted; a section for each directory that holds any of them
    /// directly, by path, each file one result whose block holds its top declarations (<see cref="FileEntries"/>).
    /// </summary>
    private GraphAnswer DirectoryOf(CodebaseGraphQueries view, string directory)
    {
        var (files, left) = FilesOf(view.FilesUnder(directory, _case ??= _paths.ComparisonIn(view.Tree)) ?? []);
        var shown = DirectoryShown(directory);
        var leading = new List<string> { $"outline of {shown} ({ReportText.Counted(files.Count, "file")}):" };

        if (files.Count == 0)
        {
            leading.Add($"no file under {shown} is one your reads reach");
        }

        IReadOnlyList<GraphSection> sections = [.. files
            .GroupBy(CodebaseGraphQueries.DirectoryOf, StringComparer.Ordinal)
            .OrderBy(held => held.Key, StringComparer.Ordinal)
            .Select(held => Section($"{DirectoryShown(held.Key)} ({ReportText.Counted(held.Count(), "file")}):", [.. held.SelectMany(file => FileEntries(view, file))], OutlineLines))];

        return new GraphAnswer(leading, sections, FilesLeft(left), [], []);
    }

    /// <summary>Of <paramref name="files"/>, by path, those the step may read, and how many it may not.</summary>
    private (List<string> Files, int Left) FilesOf(IReadOnlyList<string> files)
    {
        var kept = files.Where(_mayRead).Order(StringComparer.Ordinal).ToList();
        return (kept, files.Count - kept.Count);
    }

    /// <summary>The note of the files an outline left out because the step's reads do not reach them.</summary>
    private static IReadOnlyList<string> FilesLeft(int left) => left == 0 ? [] : [$"{ReportText.Counted(left, "file")} left out: your reads do not reach them"];

    /// <summary>A directory as an outline names it, with its trailing separator: <c>./</c> for the tree's root.</summary>
    private static string DirectoryShown(string directory) => directory.Length == 0 ? "./" : $"{ReportText.Printable(directory)}/";

    /// <summary>
    /// One file of a directory outline, as one result's block: its own node's line - or its path where the view holds no
    /// node of it - with the language that read it, or the state no language read it in, and <c>test</c> where it is test
    /// code; then, riding on it, each of its top declarations (<see cref="CodebaseGraphQueries.TopDeclarations"/>), each
    /// with what it tests where the graph's <c>tests</c> edges say so, the targets the step may not read left out.
    /// </summary>
    private IEnumerable<GraphEntry> FileEntries(CodebaseGraphQueries view, string path)
    {
        var block = ReportText.Printable(path);
        var node = CodeGraphIds.FileId(path);
        var entry = view.FileAt(path)!;
        var head = view.Part(node, path) is { } file ? GraphLines.Of(file) : block;
        var read = view.ReadByALanguage(path) && entry.Language is { } language ? language : entry.State;

        yield return new GraphEntry($"{head} | {read}{(view.TestFile(path) ? " | test" : string.Empty)}", GoesOn: block, Node: node);

        foreach (var top in view.TopDeclarations(path))
        {
            var tests = top.Tests.Where(Reads).Select(target => $"{ReportText.Printable(target.Name)} ({target.Handle})").ToList();
            var tested = tests.Count == 0 ? string.Empty : $" | tests {string.Join(", ", tests)}";

            yield return new GraphEntry($"  {GraphLines.Of(top.Card)}{tested}", Lines: 0, GoesOn: block, Node: node);
        }
    }

    /// <summary>
    /// A file outline's answer: its leading line, then what the graph does not hold of the file - every place its reader
    /// stopped or could not bind, however many, paged as any entry is - then its declarations as one section.
    /// </summary>
    private static GraphAnswer OutlineOf(CodebaseGraphQueries view, string path, IReadOnlyList<GraphCard> cards)
    {
        var leading = new List<string> { $"outline of {ReportText.Printable(path)} ({ReportText.Counted(cards.Count, "declaration")}):" };

        // What the graph does not hold of the file is said before what it does: an outline that stops where its reader did is never read as the whole file.
        List<GraphEntry> unread = [.. view.Unread(path).Select(line => new GraphEntry(line, Lines: 0))];

        if (cards.Count == 0)
        {
            unread.Add(new GraphEntry("no declaration", Lines: 0));
        }

        return new(
            leading,
            [.. unread.Count == 0 ? [] : new[] { Section(null, unread, OutlineLines) }, .. cards.Count == 0 ? [] : new[] { Section(null, [.. cards.Select(card => new GraphEntry(GraphLines.Of(card)))], OutlineLines) }],
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
    private static IReadOnlyList<string> RepositoryOf(CodebaseGraphQueries view, IReadOnlyList<string> lines)
    {
        var leading = new List<string> { $"{view.View} at {view.Head ?? "no commit"}: {ReportText.Counted(view.FileCount, "file")}" };

        if (lines.Count == 0)
        {
            leading.Add("nothing to outline");
        }

        return leading;
    }

    /// <summary>
    /// <c>around</c>: a node's place, in one answer - its card; what it is declared in, outermost first - its file, its
    /// namespace, its type (<see cref="CodebaseGraphQueries.Containers"/>); its siblings in the nearest of those
    /// (<see cref="CodebaseGraphQueries.Siblings"/>); what is declared inside it (<see cref="CodebaseGraphQueries.Inside"/>);
    /// the declarations just before and after it in its file (<see cref="CodebaseGraphQueries.Beside"/>); and its callers and
    /// callees, as <c>node</c> gives them - each a node line with its span and hash, a section of its own counted in its
    /// caption, at <see cref="NodeLinesAKind"/> a page, the nodes the step may not read left out and counted, and a text
    /// search where the graph is not sure, as <c>node</c> adds one.
    /// </summary>
    private async Task<GraphToolAnswer> AroundAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;
        var view = _queries;

        if (Resolved(view, symbol) is not { } card)
        {
            return await UnresolvedAsync(input, view, GraphOp.Around, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, view, GraphOp.Around, new JsonObject { ["symbol"] = card.Id }, $"around {ReportText.Printable(symbol)}", async (on, token) =>
        {
            var sections = new List<GraphSection>();
            var left = 0;

            void Add(string caption, IReadOnlyList<GraphCard> cards, bool counted = true)
            {
                var (kept, unread) = Readable(cards);
                left += unread;

                if (kept.Count > 0)
                {
                    sections.Add(Section(counted ? $"{caption} ({kept.Count}):" : $"{caption}:", [.. kept.Select(one => new GraphEntry("  " + GraphLines.Of(one)))], NodeLinesAKind));
                }
            }

            Add("declared in", on.Containers(card.Id, card.Path), counted: false);
            Add("siblings", on.Siblings(card.Id, card.Path));
            Add("members", on.Inside(card.Id, card.Path));

            var (before, after) = on.Beside(card.Id, card.Path);
            List<GraphEntry> beside = [.. new (string Side, GraphCard? Card)[] { ("before", before), ("after", after) }
                .Where(side => side.Card is not null && Reads(side.Card))
                .Select(side => new GraphEntry($"  {side.Side}: {GraphLines.Of(side.Card!)}"))];

            if (beside.Count > 0)
            {
                sections.Add(Section("beside it:", beside, NodeLinesAKind));
            }

            // Its callers and callees as node gives them, each edge with how sure it is; an edge of candidates makes the answer not sure.
            var edges = on.Edges(card.Id);
            var unsure = on.Unsure(card.Id);

            foreach (var (group, members) in new[] { ("callers", edges.Callers), ("callees", edges.Callees) })
            {
                var kept = members.Where(edge => Reads(edge.Card)).ToList();
                left += members.Count - kept.Count;
                unsure |= kept.Any(on.Doubts);

                if (kept.Count > 0)
                {
                    sections.Add(Section($"{group} ({kept.Count}):", [.. kept.Select(edge => new GraphEntry("  " + EdgeLine(edge)))], NodeLinesAKind));
                }
            }

            var notes = new List<string>(Notes(left));

            if (unsure)
            {
                var searched = await SearchAsync(card.Name, token).ConfigureAwait(false);
                sections.AddRange(searched.SectionsOf(card.Name));
                notes.AddRange(searched.NoHitsOf(card.Name));
                notes.AddRange(searched.Notes);
            }

            return new GraphAnswer([GraphLines.Of(card)], sections, notes, [card.Id], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>impact</c>: what a change of a node reaches - the one walk of a change's impact the change-first pack leads with
    /// (<see cref="CodebaseContextPack.Impact"/>), from the node and every other part of its declaration - every other node it
    /// settles under a section of the relation whose edge reached it, the relations of <see cref="GraphRelations.Impact"/> in
    /// their order and its tests last: its callers at every distance, what it overrides
    /// and what overrides it, its implementations, the interface members that dispatch to it, its derived types, and every
    /// test that reaches any of them. Each a node line with how many edges its best path has and that path, each edge of it
    /// said as the pack says one and its confidence where it is not exact, nearest first and then best first, at
    /// <see cref="RelatedLines"/> of each a page; a node the step may not read is neither listed nor gone through, only
    /// counted, and a text search is added where the graph is not sure, as <c>related</c> adds one.
    /// </summary>
    private async Task<GraphToolAnswer> ImpactAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;
        var view = _queries;

        if (Resolved(view, symbol) is not { } card)
        {
            return await UnresolvedAsync(input, view, GraphOp.Impact, symbol, cancellationToken).ConfigureAwait(false);
        }

        return await PagedAsync(input, view, GraphOp.Impact, new JsonObject { ["symbol"] = card.Id }, $"impact {ReportText.Printable(symbol)}", async (on, token) =>
        {
            var left = new HashSet<string>(StringComparer.Ordinal);

            bool Through(GraphCard reached)
            {
                if (Reads(reached))
                {
                    return true;
                }

                left.Add(reached.Id);
                return false;
            }

            var sources = on.OneDeclaration(card.Id).Where(part => Through(on.Card(part))).Select(part => new GraphWalked(part, CodebaseContextPack.EditedWeight, 0, 0));
            var settled = new Dictionary<string, GraphWalked>(StringComparer.Ordinal);
            var reached = new List<GraphWalked>();

            foreach (var one in CodebaseContextPack.Impact(on, sources, Through))
            {
                settled[one.Id] = one;

                if (one.By is not null)
                {
                    reached.Add(one);
                }
            }

            // The path each node's best walk reached it by, from it back to the node asked about - every node on it settled
            // before it, and named by its handle too, for an override and what it overrides carry one name.
            string PathOf(GraphWalked one)
            {
                var steps = new List<string>();

                for (var at = one; at.By is { } by; at = settled[at.From!])
                {
                    var from = on.Card(at.From!);
                    var named = $"{ReportText.Printable(from.Name)} ({from.Handle})";
                    var phrase = steps.Count == 0 ? CodebaseContextPack.Phrase(by, named) : CodebaseContextPack.Which(by, named);
                    steps.Add(string.Equals(by.Confidence, CodeConfidences.Exact, StringComparison.Ordinal) ? phrase : $"{phrase} ({by.Confidence})");
                }

                return string.Join(", ", steps);
            }

            var sections = new List<GraphSection>();

            foreach (var relation in GraphRelations.Impact.Where(relation => relation != GraphRelations.Tests).Append(GraphRelations.Tests))
            {
                var edges = GraphRelations.Edges(relation);
                var of = reached.Where(one => edges.Contains((one.By!.Side, one.By.Kind))).OrderBy(one => one.Hop).ToList();

                if (of.Count > 0)
                {
                    sections.Add(Section($"{relation} ({of.Count}):", [.. of.Select(one => new GraphEntry($"  {one.Hop.ToString(CultureInfo.InvariantCulture)}: {GraphLines.Of(on.Card(one.Id))} | {PathOf(one)}"))], RelatedLines));
                }
            }

            var leading = new List<string> { GraphLines.Of(card) };

            if (reached.Count == 0)
            {
                leading.Add($"a change of '{ReportText.Printable(card.Qualified)}' reaches no other node the graph holds");
            }

            var notes = new List<string>(Notes(left.Count));

            if (on.Unsure(card.Id) || reached.Any(one => string.Equals(one.By!.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal) || on.Unsure(one.Id)))
            {
                var searched = await SearchAsync(card.Name, token).ConfigureAwait(false);
                sections.AddRange(searched.SectionsOf(card.Name));
                notes.AddRange(searched.NoHitsOf(card.Name));
                notes.AddRange(searched.Notes);
            }

            return new GraphAnswer(leading, sections, notes, [card.Id], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<GraphToolAnswer> BodyAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var full = Flag(input, "full");
        var symbols = Strings(input, "symbols");
        var view = _queries;
        var ask = new JsonObject
        {
            ["symbols"] = new JsonArray([.. symbols.Select(symbol => (JsonNode)(Resolved(view, symbol)?.Id ?? symbol))]),
            ["full"] = full,
        };

        return await PagedAsync(input, view, GraphOp.Body, ask, $"body {string.Join(", ", symbols.Select(ReportText.Printable))}", async (on, token) =>
        {
            var entries = new List<GraphEntry>();
            var asked = new List<string>();
            var read = new List<GraphFileRead>();
            var searched = new Searches(this);

            // What the answer visited is known to it, and never visited again: a symbol named twice is said once, a node
            // two symbols resolve to served once, and a file two nodes are declared in read once - again only where an
            // extract of it found its lines moved, which makes it a file the answer has not read.
            var named = new HashSet<string>(StringComparer.Ordinal);
            var served = new HashSet<string>(StringComparer.Ordinal);
            var files = new Dictionary<string, (IReadOnlyList<string>? Lines, string? Digest, string? Why)>(StringComparer.Ordinal);

            async Task<(IReadOnlyList<string>? Lines, string? Digest, string? Why)> LinesOfAsync(string path, CancellationToken token)
            {
                if (!files.TryGetValue(path, out var lines))
                {
                    files[path] = lines = await ReadLinesAsync(path, token).ConfigureAwait(false);
                }

                return lines;
            }

            foreach (var symbol in symbols)
            {
                if (!named.Add(symbol))
                {
                    continue;
                }

                if (Resolved(on, symbol) is not { } card)
                {
                    // A symbol that names no one node is said inside the answer, as its own lines are: a body is one
                    // stream. The lines a text search adds for it are the fallback's own section, at its own size a page.
                    var (said, word) = Unresolved(on, symbol);
                    entries.AddRange(said);

                    if (word is not null)
                    {
                        await searched.AddAsync(word, token).ConfigureAwait(false);
                    }

                    continue;
                }

                if (!served.Add(card.Id))
                {
                    continue;
                }

                asked.Add(card.Id);

                var (lines, digest, why) = await LinesOfAsync(card.Path, token).ConfigureAwait(false);
                var hash = lines is null || card.Hash is null ? null : Hash(lines, card);

                // A declaration whose lines moved since the view was read is extracted again, where the tool may check its
                // file, and served as it is now: the answer goes on from the view that check left, and ends on it.
                if (hash is not null && hash != card.Hash && reExtract is not null)
                {
                    on = await ExtractedAgainAsync(card.Path, token).ConfigureAwait(false);
                    files.Remove(card.Path);

                    if (on.Part(card.Id, card.Path) is not { } found)
                    {
                        entries.Add(new($"'{ReportText.Printable(card.Qualified)}' is no longer declared in '{ReportText.Printable(card.Path)}'", Lines: 0));
                        continue;
                    }

                    card = found;
                    (lines, digest, why) = await LinesOfAsync(card.Path, token).ConfigureAwait(false);
                    hash = lines is null ? null : Hash(lines, card);
                }

                if (lines is null)
                {
                    entries.Add(new($"'{ReportText.Printable(card.Qualified)}' is not read: {why}", Lines: 0));
                    continue;
                }

                // A span the file no longer reaches has no lines to number: said, never served from where they are not.
                if (hash == Gone)
                {
                    entries.Add(new($"'{ReportText.Printable(card.Qualified)}' is not at {ReportText.Printable(card.Path)}:{card.Start}-{card.End} now: the file holds {ReportText.Counted(lines.Count, "line")}; find it again", Lines: 0));
                    continue;
                }

                var qualified = ReportText.Printable(card.Qualified);
                var head = $"{card.Handle} {qualified} {ReportText.Printable(card.Path)}:{card.Start}-{card.End}";

                // A whole file is no declaration and has no hash; a declaration's is the one its lines give now.
                if (hash is not null)
                {
                    head += $" #{hash}";

                    if (hash != card.Hash)
                    {
                        head += $" (changed since the graph read it as #{card.Hash})";
                    }
                }

                // The block's head and every line of it name the node, so a page that ends inside it says it goes on; each
                // served line names its file, which the page that gives it leaves viewed as its lines were read.
                entries.Add(new(head, Lines: 0, GoesOn: qualified, Node: card.Id));

                foreach (var (text, number) in Shown(on, card, CodebaseGraphQueries.Body(card, lines), full))
                {
                    entries.Add(number is { } line
                        ? new($"{line.ToString(CultureInfo.InvariantCulture)}\t{text}", Lines: 1, GoesOn: qualified, Node: card.Id, File: card.Path)
                        : new(text, Lines: 0, GoesOn: qualified, Node: card.Id));
                }

                // A file read again in one answer - its extract found its lines moved - is viewed as the first read found it:
                // where the later read differs, an edit made from the earlier lines is refused as changed since viewed.
                if (!read.Any(file => string.Equals(file.Path, card.Path, StringComparison.Ordinal)))
                {
                    read.Add(new GraphFileRead(card.Path, digest!));
                }

                if (on.Unsure(card.Id))
                {
                    await searched.AddAsync(card.Name, token).ConfigureAwait(false);
                }
            }

            return new GraphAnswer([], [Section(null, entries, BodyLines), .. searched.Sections], searched.Notes, asked.Distinct(StringComparer.Ordinal).ToList(), read) { On = on };
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// <c>sites</c>: every place that decides the data named - what defines it, holds it as a key, calls it, uses it, makes
    /// it, names it by its kind, inherits it, implements or overrides it (<see cref="CodebaseGraphQueries.SitesOf"/>, every
    /// definer's own edges alone, one hop each, so a hub is listed and never walked through), every literal that holds it,
    /// and every line that holds it of the files the graph does not answer of it
    /// (<see cref="CodebaseGraphQueries.TextScopeOf"/>) - each place one card, the node at it, saying every way it decides,
    /// the cards grouped by file kind and tier (<see cref="CodebaseGraphQueries.KindOf"/>,
    /// <see cref="CodebaseGraphQueries.TierOf"/>), and a raw line where the graph read no node of its file. A
    /// data that names a node by itself - its handle, its id, a <c>path:line</c> - asks of that node, its name the word its
    /// literals and the text are searched for, and a selector is searched for as it is written; a name written with its
    /// scopes, or a key path in any spelling its reader reads - but for a name a definer, none of them a key, is found by as it
    /// is written, by its name, its qualified name or with some of its scopes, which is searched for as that name - is searched
    /// for by its last name, as code and data write it, and each key path it names by its places as an environment variable
    /// writes them too (<see cref="CodebaseGraphQueries.SitesSearch"/>, <see cref="GraphSitesSearch"/>: no pattern, whatever the
    /// word's length and however many key paths it names); a quoted one asks by its value alone, never the symbols. Every place
    /// passes the step's reads at one gate (<see cref="SiteList"/>), and those they do not reach are counted. The coverage
    /// lines that end the answer say what the text search read, each file counted once it was read, and why; then every limit
    /// of each language whose files the graph read (<see cref="Unseen"/>).
    /// </summary>
    private async Task<GraphToolAnswer> SitesAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var data = Str(input, "data")!;
        var view = _queries;

        return await PagedAsync(input, view, GraphOp.Sites, new JsonObject { ["data"] = data }, $"sites {ReportText.Printable(data.Trim())}", async (on, token) =>
        {
            var written = data.Trim();
            var quoted = QuotedValue(data);
            var sites = new SiteList(this, on);

            // The definers of the data - its declarations, and the keys it names, which are held and never defined. A quoted
            // data has none, for it asks by its value alone.
            var definers = quoted is null ? on.DefinersOf(written) : [];

            foreach (var card in definers)
            {
                sites.Add(card, string.Equals(card.SubKind, CodeSubKinds.Key, StringComparison.Ordinal) ? GraphSiteHows.HoldsTheKey : GraphSiteHows.Defines);
            }

            // What decides of the data beside its definers: one hop off every definer, a part walked as the node itself, each
            // node with how sure the surest edge that reaches it is - each place it reaches held to the step's reads where it
            // stands, so a readable caller of a definer the reads do not reach is listed, which tells nothing the step cannot
            // read itself.
            foreach (var group in on.SitesOf([.. definers.Select(card => card.Id)]))
            {
                foreach (var edge in group.Edges)
                {
                    sites.Add(edge.Card, group.How, edge.Confidence);
                }
            }

            // What the literals and the text are searched for (CodebaseGraphQueries.SitesSearch): the value; or the name of the
            // node the data names by itself, the selector it is written as, or its last name - a name written with its scopes,
            // a key path in any spelling, is written in code and in data by its last name (shape.Area(), "TimeoutSeconds": 30),
            // and a whole word of it is held by every longer name that ends with it - and each key path it names as an
            // environment variable writes it, which no whole word of its last key is held by: each line and value asked by the
            // search's characters alone, no pattern compiled however long the word or however many key paths it names.
            var searched = quoted is null ? on.SitesSearch(written, definers) : GraphSitesSearch.Of(quoted);

            foreach (var place in on.LiteralSites(searched))
            {
                sites.AddLiteral(place);
            }

            // The text search: every file the graph does not answer of the data, each for the first reason it is so, counted
            // once the search read it; a name's references the linker bound to nothing of its name tell files of it. Each file
            // is told by the one path its listing names it by - what the search takes it by, each hit's and each read's - and
            // a file the graph answers of is never taken, so it is never counted among those the reader may not read.
            IReadOnlyList<string> names = quoted is null ? [.. definers.Select(card => card.Name).Append(searched.Word).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)] : [];
            var unbound = on.UnboundNaming(names);
            var scopes = new Dictionary<string, GraphTextScope>(StringComparer.Ordinal);
            var read = new Dictionary<GraphTextScope, List<string>>();
            var hits = new Dictionary<GraphTextScope, int>();
            var search = await TextSearchAsync(
                on,
                searched.Holds,
                listed =>
                {
                    if (on.TextScopeOf(listed, unbound, quoted is not null) is not { } scope)
                    {
                        return false;
                    }

                    scopes[listed] = scope;
                    return true;
                },
                hit =>
                {
                    var scope = scopes[hit.Path];
                    hits[scope] = hits.GetValueOrDefault(scope) + 1;
                    sites.AddHit(hit, scope);
                },
                listed => (read.TryGetValue(scopes[listed], out var files) ? files : read[scopes[listed]] = []).Add(listed),
                token).ConfigureAwait(false);

            var notes = new List<string>(Coverage(read, hits, searched, names));
            notes.AddRange(Unseen(on));
            notes.AddRange(search.LeftOut(ReportText.Printable));

            // A place left out may be a node or a literal's line where the graph read no node: places, either way.
            if (sites.Left > 0)
            {
                notes.Add($"{ReportText.Counted(sites.Left, "place")} left out: in files your reads do not reach");
            }

            var leading = new List<string> { quoted is null ? $"every place that decides '{ReportText.Printable(written)}':" : $"every place that decides the literal '{ReportText.Printable(quoted)}':" };
            var sections = sites.Sections();

            // Said of what was searched alone: the files the graph answers of were read from the graph, never by text.
            if (sections.Count == 0)
            {
                leading.Add(sites.Left > 0 ? "every place the graph holds of it is in a file your reads do not reach, and no line of the files searched by text holds it"
                    : quoted is null ? "the graph holds no declaration of it and no literal that holds it, and no line of the files searched by text holds it"
                    : "the graph holds no literal that holds it, and no line of the files searched by text holds it");
            }

            // What the ask named, as the facts hold it: its definers the step may read.
            return new GraphAnswer(leading, sections, notes, [.. definers.Where(Reads).Select(card => card.Id)], []);
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The coverage lines a <c>sites</c> answer ends with, of the files its text search read - each file once it was read,
    /// by why it was searched (<paramref name="read"/>), with the hits it gave: the files the graph lists only and the files it
    /// is unsure of always, said where none was; and, where any was, the files held in another state no implementation of a
    /// language read them in, by the state, those the graph does not hold, those whose extract it no longer holds, those it
    /// outlines, those holding a reference named the data it bound to nothing of that name, and those read for a value; the
    /// forms a selector was searched in, and how many key paths it names were searched as an environment variable writes
    /// each, where <paramref name="searched"/> searched any so; and, where any file the graph lists only was searched, that
    /// nothing is assumed of a language with no extractor.
    /// </summary>
    private static IEnumerable<string> Coverage(IReadOnlyDictionary<GraphTextScope, List<string>> read, IReadOnlyDictionary<GraphTextScope, int> hits, GraphSitesSearch searched, IReadOnlyList<string> names)
    {
        var word = searched.Word;

        string Searched(GraphTextScope scope, string said, bool too = false)
            => $"coverage: {ReportText.Counted(read[scope].Count, "file")} {said} searched by text{(too ? " too" : string.Empty)} - {ReportText.Counted(hits.GetValueOrDefault(scope), "hit")}";

        string Kinds(GraphTextScope scope) => KindsOf(read[scope]);

        var listed = new GraphTextScope(GraphTextReason.Unread, CodeFileStates.Listed);
        var unsure = new GraphTextScope(GraphTextReason.Unsure);
        var unheld = new GraphTextScope(GraphTextReason.Unheld);
        var unextracted = new GraphTextScope(GraphTextReason.Unextracted);
        var outlined = new GraphTextScope(GraphTextReason.Outlined);
        var unbound = new GraphTextScope(GraphTextReason.Unbound);
        var value = new GraphTextScope(GraphTextReason.Value);

        yield return read.ContainsKey(listed) ? Searched(listed, $"the graph lists only ({Kinds(listed)})") : "coverage: no file the graph lists only was searched by text";

        foreach (var state in CodeFileStates.All.Where(state => state != CodeFileStates.Listed && read.ContainsKey(new(GraphTextReason.Unread, state))))
        {
            var scope = new GraphTextScope(GraphTextReason.Unread, state);
            yield return Searched(scope, $"the graph holds {state}, read by no implementation of a language ({Kinds(scope)}),");
        }

        if (read.ContainsKey(unheld))
        {
            yield return Searched(unheld, $"the graph does not hold, written since its last sync or kept out of it ({Kinds(unheld)}),");
        }

        if (read.ContainsKey(unextracted))
        {
            yield return Searched(unextracted, $"whose extract the graph no longer holds, their declarations in no answer ({Kinds(unextracted)}),");
        }

        yield return read.ContainsKey(unsure) ? Searched(unsure, "the graph is unsure of,", too: true) : "coverage: no file the graph is unsure of was searched by text";

        if (read.ContainsKey(outlined))
        {
            yield return Searched(outlined, $"the graph outlines ({Kinds(outlined)}), their values and their text held by no literal of it,", too: true);
        }

        if (read.ContainsKey(unbound))
        {
            yield return Searched(unbound, $"holding a reference named {string.Join(" or ", names.Select(name => $"'{ReportText.Printable(name)}'"))} that the graph bound to nothing of that name,", too: true);
        }

        if (read.ContainsKey(value))
        {
            yield return Searched(value, "the graph read, for it holds a value only where code writes it as a string literal,", too: true);
        }

        if (searched.Selector is { } bare)
        {
            yield return $"coverage: '{ReportText.Printable(word)}' searched as a rule writes it and as the name '{ReportText.Printable(bare)}', a whole word of a class list";
        }

        if (searched.Spellings.Count > 0)
        {
            yield return $"coverage: '{ReportText.Printable(word)}' searched as a whole word, and {ReportText.Counted(searched.Spellings.Count, "key path")} it names as an environment variable writes each, its places joined by '__'";
        }

        if (read.ContainsKey(listed))
        {
            yield return "coverage: nothing is assumed of a language with no extractor; those files are read by text alone";
        }
    }

    /// <summary>
    /// Every limit of each language whose files the answer read from the graph - each file the view holds that an
    /// implementation of a language read (<see cref="CodebaseGraphQueries.ReadByALanguage"/>) and the step may read - for each
    /// language once, in order, one rule for every language (<see cref="CodeGraphLimit"/>): a limit whose sign
    /// (<see cref="CodeGraphLimit.Signs"/>) some of those files give - the kind of a dynamic mark one holds, or what the linker found
    /// of its references and its imports, each sign one thing it knows (<see cref="CodebaseGraphQueries.SignsOf"/>) - said in full, as its help says it,
    /// with how many give it; and every other limit of the language by its short name (<see cref="CodeGraphLimit.Name"/>), on one
    /// line for the language that points to the help topic that says each in full under its name
    /// (<see cref="HelpTopics.CodebaseGraphLanguages"/>) - none left unsaid, and the answer small.
    /// What a <c>sites</c> answer ends with, read before what it says of those files is trusted. A file no implementation of
    /// a language read is said by the coverage lines: nothing is assumed of a language with no extractor.
    /// </summary>
    private IEnumerable<string> Unseen(CodebaseGraphQueries view)
    {
        var languages = view.Paths
            .Where(path => _mayRead(path) && view.ReadByALanguage(path))
            .GroupBy(path => view.FileAt(path)!.Language!, StringComparer.Ordinal)
            .OrderBy(language => language.Key, StringComparer.Ordinal);

        foreach (var language in languages)
        {
            // A language no implementation of this build reads - an earlier build's - has no limit this build can say.
            if (CodeGraphExtractors.OfThisBuild.Find(language.Key) is not { } extractor)
            {
                continue;
            }

            var signs = language.Select(view.SignsOf).ToList();
            var named = new List<string>();

            foreach (var limit in extractor.Help.DoesNotSee)
            {
                var shown = limit.Signs.Count == 0 ? 0 : signs.Count(file => file.Any(limit.Signs.Contains));

                if (shown == 0)
                {
                    named.Add(limit.Name);
                    continue;
                }

                yield return $"{extractor.Language} does not see {limit.Says} - shown by {ReportText.Counted(shown, "file")} read.";
            }

            if (named.Count > 0)
            {
                yield return OtherLimits(extractor.Language, named);
            }
        }
    }

    /// <summary>
    /// The one line a <c>sites</c> answer says the limits of <paramref name="language"/> no file it read shows the sign of by:
    /// each by its short name, <paramref name="names"/> in the order its help says them, and the help topic that says each in full
    /// under that name.
    /// </summary>
    /// <param name="language">The language's name.</param>
    /// <param name="names">The short names of its limits no file shows the sign of.</param>
    internal static string OtherLimits(string language, IReadOnlyList<string> names)
        => $"{language} also does not see ('help {HelpTopics.CodebaseGraphLanguages}' says each): {string.Join(", ", names)}.";

    /// <summary>
    /// The value a quoted <paramref name="data"/> asks by - its first and its last character one quote mark, single or
    /// double - or null where it asks by a name: a quoted literal is asked by its value alone, never the symbols, while a
    /// name is asked by everything the graph joins. The one reading of a quoted data, the usage check's (<see cref="Misused"/>,
    /// which refuses one whose value is empty or white space alone) and the answer's.
    /// </summary>
    /// <param name="data">The data, as the call gives it.</param>
    internal static string? QuotedValue(string data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var trimmed = data.Trim();
        return trimmed.Length >= 2 && trimmed[0] is '"' or '\'' && trimmed[^1] == trimmed[0] ? trimmed[1..^1] : null;
    }

    /// <summary>The kinds of <paramref name="files"/> - each path's by its name (<see cref="CodebaseGraphQueries.FileKind"/>), each once, in order - said in a coverage line.</summary>
    private static string KindsOf(IReadOnlyCollection<string> files)
        => string.Join(", ", files.Select(CodebaseGraphQueries.FileKind).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>
    /// The places one <c>sites</c> answer gives, each once. Every place passes the step's reads here - a definer, a node an
    /// edge reaches, a literal's place and a text hit alike - and one in a file they do not reach is left out and counted,
    /// so no source of places passes them by. Each place is the card of its node, with every way it decides - an edge's with
    /// how sure the surest edge that reaches it is, as the <c>node</c> operation says an edge - and the lines a literal or a
    /// text hit stands on beneath it; or a raw line where the graph read no node of the file.
    /// </summary>
    private sealed class SiteList(CodebaseGraphTool tool, CodebaseGraphQueries view)
    {
        private readonly Dictionary<string, Site> _sites = new(StringComparer.Ordinal);
        private readonly List<(string Path, long Line, string Said, bool Hit)> _raw = [];
        private readonly HashSet<string> _left = new(StringComparer.Ordinal);

        /// <summary>How many places were left out, each once: in files the step's reads do not reach.</summary>
        public int Left => _left.Count;

        /// <summary>The node <paramref name="card"/> as a place that decides as <paramref name="how"/> says, where the step may read it.</summary>
        public void Add(GraphCard card, string how, string confidence = CodeConfidences.Exact)
            => Of(card)?.Say(how, confidence);

        /// <summary>The place of a literal that holds the data: under its node's card, where the step may read its file.</summary>
        public void AddLiteral(GraphLiteralPlace place)
        {
            var said = $"{ReportText.Printable(place.Path)}:{place.Line.ToString(CultureInfo.InvariantCulture)} holds the literal '{ReportText.Printable(place.Value)}'";

            if (place.Node is { } node)
            {
                Of(node)?.Hold(place.Path, place.Line, said);
            }
            else if (tool._mayRead(place.Path))
            {
                _raw.Add((place.Path, place.Line, said, false));
            }
            else
            {
                _left.Add($"{place.Path}:{place.Line.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        /// <summary>
        /// A line the text search found: under the card of the node at it where the graph holds what a language read of its
        /// file and a node there, and passed over where a literal there already says the place; a raw line else. The search
        /// reads only files the step may read.
        /// </summary>
        public void AddHit(AgentSearchHit hit, GraphTextScope scope)
        {
            var held = view.HeldPath(hit.Path) ?? hit.Path;

            if (scope.Reason is not (GraphTextReason.Unheld or GraphTextReason.Unread or GraphTextReason.Unextracted) && view.AtLine(held, (int)hit.Line) is { } card)
            {
                Of(card)?.Find(held, hit.Line, HitLine(held, hit));
                return;
            }

            _raw.Add((held, hit.Line, HitLine(held, hit), true));
        }

        /// <summary>
        /// The answer's sections: one for each file kind and tier the places stand in, in order, each of how many places it
        /// holds - every place's card and its lines, by path, then line, then id, at <see cref="RelatedLines"/> places a page,
        /// each place a block its card heads (<see cref="Site.Entries"/>), so its lines never stand on a page without it.
        /// </summary>
        public IReadOnlyList<GraphSection> Sections()
        {
            var placed = _sites.Values
                .Select(site => (site.Card.Path, Line: (long)site.Card.Start, Id: site.Card.Id, Entries: site.Entries()))
                .Concat(_raw.Select(raw => (raw.Path, raw.Line, Id: string.Empty, Entries: (IReadOnlyList<GraphEntry>)[new GraphEntry("  " + raw.Said, Lines: 1, Hit: raw.Hit)])));

            return [.. placed
                .GroupBy(place => (Kind: view.KindOf(place.Path), Tier: view.TierOf(place.Path)))
                .OrderBy(group => group.Key.Kind, StringComparer.Ordinal)
                .ThenBy(group => group.Key.Tier, StringComparer.Ordinal)
                .Select(group => Section(
                    $"{ReportText.Printable(group.Key.Kind)} in {group.Key.Tier} ({group.Count().ToString(CultureInfo.InvariantCulture)}):",
                    [.. group
                        .OrderBy(place => place.Path, StringComparer.Ordinal)
                        .ThenBy(place => place.Line)
                        .ThenBy(place => place.Id, StringComparer.Ordinal)
                        .SelectMany(place => place.Entries)],
                    RelatedLines))];
        }

        /// <summary>The place of <paramref name="card"/>'s node, made where it is first named; null where the step's reads do not reach its file, counted once.</summary>
        private Site? Of(GraphCard card)
        {
            if (!tool.Reads(card))
            {
                _left.Add(card.Id);
                return null;
            }

            if (!_sites.TryGetValue(card.Id, out var site))
            {
                _sites[card.Id] = site = new Site(card);
            }

            return site;
        }

        /// <summary>One node's place: its card, every way it decides with how sure each is, and the lines a literal or a text hit stands on in it.</summary>
        private sealed class Site(GraphCard card)
        {
            private readonly Dictionary<string, string> _hows = new(StringComparer.Ordinal);
            private readonly List<(string Path, long Line, string Said, bool Hit)> _lines = [];
            private readonly HashSet<(string Path, long Line)> _held = [];

            public GraphCard Card { get; } = card;

            /// <summary>One more way it decides, at <paramref name="confidence"/>: the surest of those it was said by.</summary>
            public void Say(string how, string confidence)
                => _hows[how] = _hows.TryGetValue(how, out var was) ? CodeConfidences.Surer(was, confidence) : confidence;

            /// <summary>A literal that holds the data, on its line.</summary>
            public void Hold(string path, long line, string said)
            {
                Say(GraphSiteHows.HoldsTheLiteral, CodeConfidences.Exact);
                _held.Add((path, line));
                _lines.Add((path, line, said, false));
            }

            /// <summary>A line of text that holds the data - passed over where a literal there already says the place.</summary>
            public void Find(string path, long line, string said)
            {
                if (_held.Contains((path, line)))
                {
                    return;
                }

                Say(GraphSiteHows.Text, CodeConfidences.Exact);
                _lines.Add((path, line, said, true));
            }

            /// <summary>
            /// Its entries, one block of its node (<see cref="GraphEntry.Node"/>, named by its qualified name): its card with
            /// every way it decides, in the order <see cref="GraphSiteHows.All"/> says them, each edge's of other than exact with
            /// how sure it is - the place, counted once, at the block's head; then its lines, by path and line, each counting no
            /// place, a text hit's a hit. A page gives the block whole where a page holds it - one that cannot ends before it -
            /// and one wider than a page in parts, each page after its first beginning with the card again
            /// (<see cref="Paging"/>).
            /// </summary>
            public IReadOnlyList<GraphEntry> Entries()
            {
                var hows = GraphSiteHows.All
                    .Where(_hows.ContainsKey)
                    .Select(how => string.Equals(_hows[how], CodeConfidences.Exact, StringComparison.Ordinal) ? how : $"{how} ({_hows[how]})");
                var block = ReportText.Printable(Card.Qualified);

                return [
                    new GraphEntry($"  {GraphLines.Of(Card)} | {string.Join(", ", hows)}", GoesOn: block, Node: Card.Id),
                    .. _lines
                        .OrderBy(line => line.Path, StringComparer.Ordinal)
                        .ThenBy(line => line.Line)
                        .ThenBy(line => line.Hit)
                        .Select(line => new GraphEntry("      " + line.Said, Lines: 0, GoesOn: block, Node: Card.Id, Hit: line.Hit))];
            }
        }
    }

    /// <summary>
    /// The text searches one answer adds, each word once: their hits as sections of their own, each at the text
    /// fallback's size a page, and the notes of what they found nothing for or did not read, said where the answer ends.
    /// </summary>
    private sealed class Searches(CodebaseGraphTool tool)
    {
        private readonly HashSet<string> _words = new(StringComparer.Ordinal);

        /// <summary>The hits' sections, in the order the words were searched.</summary>
        public List<GraphSection> Sections { get; } = [];

        /// <summary>The notes the searches end the answer with.</summary>
        public List<string> Notes { get; } = [];

        /// <summary>Searches <paramref name="word"/>, where no search of this answer searched it.</summary>
        public async Task AddAsync(string word, CancellationToken cancellationToken)
        {
            if (!_words.Add(word.Trim()))
            {
                return;
            }

            var searched = await tool.SearchAsync(word, cancellationToken).ConfigureAwait(false);
            Sections.AddRange(searched.SectionsOf(word));
            Notes.AddRange(searched.NoHitsOf(word));
            Notes.AddRange(searched.Notes);
        }
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

        var view = _queries;

        if (Resolved(view, symbol) is not { } card)
        {
            return await UnresolvedAsync(input, view, GraphOp.Edit, symbol, cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(card.Hash, hash, StringComparison.OrdinalIgnoreCase)
            && view.Parts(card.Id).FirstOrDefault(part => string.Equals(part.Hash, hash, StringComparison.OrdinalIgnoreCase) && Reads(part)) is { } named)
        {
            card = named;
        }

        if (card.Hash is null)
        {
            return Said($"'{ReportText.Printable(card.Qualified)}' is a whole file, and no declaration an edit replaces: edit a declaration in it");
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
                return Said($"'{ReportText.Printable(card.Qualified)}' is no longer declared in '{ReportText.Printable(card.Path)}': find it again");
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
                return Said([$"'{ReportText.Printable(card.Qualified)}' changed since you saw it (#{ReportText.Printable(hash)}, now #{now}, lines {card.Start}-{card.End}): read it again and edit with the new hash", GraphLines.Of(card with { Hash = now })]);
            }
        }

        var edited = EditedText.Of(reading.Text!);
        var was = edited.LineCount;
        edited.ReplaceLines(card.Start, card.End, text);
        var moved = edited.LineCount - was;
        var bytes = edited.Bytes();

        // Where it lands decided again before a byte is written, as the runtime's own writes are (AgentFiles.Write).
        if (_files.Write(_paths.WritableRootOf(Full(card.Path)), Full(card.Path), bytes, landing => _paths.Opened(landing, write: true).Denied) is { } landed)
        {
            return new GraphToolAnswer(landed.Reason, GraphToolOutcome.Refused, card.Path, landed);
        }

        var after = await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false);
        var written = card.End - card.Start + 1 + moved;
        var shift = moved == 0 ? "no later declaration moved" : $"the file's later declarations moved {ReportText.Counted(Math.Abs(moved), "line")} {(moved > 0 ? "down" : "up")}";
        var held = (after.OutlineFile(card.Path) ?? []).Where(one => one.Start >= card.Start && one.Start < card.Start + written).Select(one => "  " + GraphLines.Of(one)).ToList();
        var qualified = ReportText.Printable(card.Qualified);
        var path = ReportText.Printable(card.Path);
        IReadOnlyList<string> said = after.Part(card.Id, card.Path) is { } refound ? [GraphLines.Of(refound)]
            : written == 0 ? [$"the new lines are none: '{qualified}' is gone from '{path}'"]
            : held.Count == 0 ? [$"the new lines no longer declare '{qualified}', nor anything else"]
            : [$"the new lines no longer declare '{qualified}'; they hold:", .. held];

        // A write is no paged read: its answer is one page, said once the write is done - the declarations its lines
        // hold, or, where they pass one page, how many, and the read that pages them.
        var leading = $"edited '{qualified}' in '{path}'; {shift}";
        var answer = OnePage([leading, .. said], $"edit {ReportText.Printable(symbol)}")
            ?? Said([leading, $"the new lines no longer declare '{qualified}'; they hold {ReportText.Counted(held.Count, "declaration")}: outline '{path}' to read them"]);

        return answer with
        {
            Outcome = GraphToolOutcome.Wrote,
            Path = card.Path,
            Facts = new GraphToolFacts([], [], [card.Id], new GraphWrittenLines(card.Path, card.Start, written, WrittenHash(edited, card.Start, written))),
            Viewed = [new GraphFileRead(card.Path, FileContentHash.Of(bytes))],
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
            return Usage($"the view holds no file it parsed at '{ReportText.Printable(path)}': wholeFile lifts the narrowing of a file the graph parsed, and no whole read of any other is narrowed");
        }

        if (!_mayRead(held))
        {
            return Usage($"'{ReportText.Printable(held)}' is a file your reads do not reach");
        }

        _lifted.TryAdd(held, true);

        return Said([$"the narrowing of reads is lifted for '{ReportText.Printable(held)}' for the rest of this step: {ReportText.Printable(reason)}"]) with
        {
            Escape = new GraphEscapeLine(held, lines, EstimatedTokens.OfBytes(file.Bytes), GraphEscapeLine.Stated, reason),
        };
    }

    /// <summary>
    /// The node <paramref name="symbol"/> names where it names one the step may read; null otherwise. A node whose part that
    /// speaks for it is in a file the step may not read is not served by another part: its edges come from that part too.
    /// </summary>
    private GraphCard? Resolved(CodebaseGraphQueries view, string symbol) => view.Resolve(symbol) is GraphResolve.Found found && Reads(found.Card) ? found.Card : null;

    /// <summary>
    /// What a symbol that names no one node the step reads is answered with: its candidates, or a text search for it - in
    /// pages as any read's answer, the search made by the page that computes the answer. An <c>edit</c> is no read: its
    /// answer is one page, and where the candidates or the lines that hold the name pass one, it says how many and the read
    /// that pages them.
    /// </summary>
    private async Task<GraphToolAnswer> UnresolvedAsync(JsonObject input, CodebaseGraphQueries view, GraphOp op, string symbol, CancellationToken cancellationToken)
    {
        var printed = ReportText.Printable(symbol);
        var asked = $"{NameOf(op)} {printed}";

        switch (view.Resolve(symbol))
        {
            case GraphResolve.Candidates candidates:
                var (kept, left) = Readable(candidates.Cards);
                var many = $"'{printed}' is not one name: {ReportText.Counted(kept.Count, "node")} carry it - ask again by a handle";
                var named = new GraphAnswer([many], [Section(null, [.. kept.Select(card => new GraphEntry(GraphLines.Of(card)))], FindLines)], Notes(left), [], []);

                return op == GraphOp.Edit
                    ? OnePage(named, asked) ?? Said([$"{many}; node '{printed}' gives them"])
                    : await PagedAsync(input, view, op, new JsonObject { ["symbol"] = symbol }, asked, (on, token) => Task.FromResult(named), cancellationToken).ConfigureAwait(false);

            case GraphResolve.Found:
                return op == GraphOp.Edit ? Said([$"'{printed}' is in a file your reads do not reach"]) : Said(input, asked, $"'{printed}' is in a file your reads do not reach");

            default:
                var none = $"'{printed}' is not a node the view holds";

                async Task<GraphAnswer> SearchedAsync(CodebaseGraphQueries on, CancellationToken token)
                {
                    var searched = new Searches(this);

                    if (Simple(symbol) is { } name)
                    {
                        await searched.AddAsync(name, token).ConfigureAwait(false);
                    }

                    return new GraphAnswer([none], searched.Sections, searched.Notes, [], []);
                }

                return op == GraphOp.Edit
                    ? OnePage(await SearchedAsync(view, cancellationToken).ConfigureAwait(false), asked) ?? Said([$"{none}; node '{printed}' gives the lines that hold its name"])
                    : await PagedAsync(input, view, op, new JsonObject { ["symbol"] = symbol }, asked, SearchedAsync, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What a body says, in its stream, of a symbol that names no one node the step reads - its candidates, or that its
    /// file is one the reads do not reach, or that the view holds no node of it - and the word its text search is for,
    /// where one is searched: a name the view does not hold.
    /// </summary>
    private (IReadOnlyList<GraphEntry> Said, string? Searched) Unresolved(CodebaseGraphQueries view, string symbol)
    {
        var printed = ReportText.Printable(symbol);

        switch (view.Resolve(symbol))
        {
            case GraphResolve.Candidates candidates:
                var (kept, left) = Readable(candidates.Cards);

                return ([
                    new GraphEntry($"'{printed}' is not one name: {ReportText.Counted(kept.Count, "node")} carry it - ask again by a handle", Lines: 0),
                    .. kept.Select(card => new GraphEntry(GraphLines.Of(card), Lines: 0)),
                    .. Notes(left).Select(note => new GraphEntry(note, Lines: 0))], null);

            case GraphResolve.Found:
                return ([new($"'{printed}' is in a file your reads do not reach", Lines: 0)], null);

            default:
                return ([new($"'{printed}' is not a node the view holds", Lines: 0)], Simple(symbol));
        }
    }

    /// <summary>What a text search gave: its hits as entries, and the notes of what it did not read.</summary>
    /// <param name="Hits">The hits, each one line - entries of a section of their own, at the text fallback's size a page (<see cref="TextHits"/>).</param>
    /// <param name="Notes">The notes of the files the search did not read, said at the answer's end.</param>
    private sealed record Searched(IReadOnlyList<GraphEntry> Hits, IReadOnlyList<string> Notes)
    {
        /// <summary>A search that gave nothing and said nothing.</summary>
        public static Searched None { get; } = new([], []);

        /// <summary>The answer's sections of the hits: one, captioned with what it holds.</summary>
        public IReadOnlyList<GraphSection> SectionsOf(string word)
            => Hits.Count == 0 ? [] : [new GraphSection($"text: lines that hold '{ReportText.Printable(word.Trim())}'", Hits, TextHits)];

        /// <summary>The note that ends an answer whose search gave nothing, where the word was one to search.</summary>
        public IReadOnlyList<string> NoHitsOf(string word)
            => Hits.Count == 0 && word.Trim().Length > 0 ? [$"text: no line holds '{ReportText.Printable(word.Trim())}'"] : [];
    }

    /// <summary>The lines that hold <paramref name="name"/> as a whole word (<see cref="GraphWord.Whole"/>), case-sensitive, as <c>search_files</c> searches - the text a not-sure answer adds, and the notes of what it did not read.</summary>
    private async Task<Searched> SearchAsync(string name, CancellationToken cancellationToken)
    {
        var word = name.Trim();

        if (word.Length == 0)
        {
            return Searched.None;
        }

        // No bound on the hits, nor on a file's bytes, nor on a hit's line: the answer's pages hold them all, at the text
        // fallback's size a page, a line wider than a page given in pieces.
        var hits = new List<AgentSearchHit>();
        var whole = GraphWord.Whole(word);
        var search = await TextSearchAsync(_queries, line => whole.In(line), take: null, hit => hits.Add(hit), read: null, cancellationToken).ConfigureAwait(false);

        return new(
            [.. hits.Select(hit => new GraphEntry("  " + HitLine(hit.Path, hit), Lines: 1, Hit: true))],
            [.. search.LeftOut(ReportText.Printable)]);
    }

    /// <summary>
    /// The one text search the tool runs, the text fallback's and a <c>sites</c> answer's alike - each line asked by
    /// <paramref name="holds"/> whether it holds what is searched for: every file under
    /// <paramref name="view"/>'s tree that <paramref name="take"/> takes, where one is given, by the path the tree's listing
    /// names it by - the one path the file is told by, each hit handed to <paramref name="found"/> and each file read to its
    /// end to <paramref name="read"/> naming it so - and the step may read: held to the tree as well as to what the graph may
    /// read, for the search decides again where each file it opened stands, and a link put on its way since may lead out of
    /// the tree, where no relative path reads.
    /// </summary>
    private Task<AgentSearch> TextSearchAsync(CodebaseGraphQueries view, Func<string, bool> holds, Func<string, bool>? take, Action<AgentSearchHit> found, Action<string>? read, CancellationToken cancellationToken)
        => _files.SearchAsync(
            view.Tree,
            holds,
            take,
            0,
            file => _paths.Read(file).Full is not null && _mayRead(Relative(file)),
            (hit, _) =>
            {
                found(hit);
                return ValueTask.CompletedTask;
            },
            read,
            cancellationToken);

    /// <summary>How a text hit is said, the line's place - <paramref name="path"/>, as the view holds it where it does - and the line itself.</summary>
    private static string HitLine(string path, AgentSearchHit hit) => $"{ReportText.Printable(path)}:{hit.Line.ToString(CultureInfo.InvariantCulture)} {hit.Text.Trim()}";

    /// <summary>
    /// The file at <paramref name="path"/> read whole, in its encoding, or why it is not: a place the runtime's own tools
    /// may not read (<see cref="ConfinedPaths.Read"/>) - a link out of the tree put along it since the view was read, by its
    /// path or, once opened, by where the open file stands (<see cref="AgentFiles.ReadAsync"/>) - none there, or no text
    /// this tool reads. A file is read whole whatever it holds: a body serves every line the graph declares.
    /// </summary>
    private async Task<(EncodedTextReading? Reading, string? Why)> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var (reading, _, why) = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return (reading, why);
    }

    /// <summary>The file at <paramref name="path"/> read as <see cref="ReadFileAsync"/> reads it, with the digest of the bytes it was read from.</summary>
    private async Task<(EncodedTextReading? Reading, string? Digest, string? Why)> ReadBytesAsync(string path, CancellationToken cancellationToken)
    {
        var confined = _paths.Read(Full(path));

        if (confined.Full is not { } full)
        {
            return (null, null, confined.Denied!.Reason);
        }

        if (_fileSystem.StandingOf(full) is null)
        {
            return (null, null, $"there is no file at '{ReportText.Printable(path)}'");
        }

        var read = await _files.ReadAsync(full, long.MaxValue, opened => _paths.Opened(opened, write: false).Denied, cancellationToken).ConfigureAwait(false);

        if (read.Denied is { } moved)
        {
            return (null, null, moved.Reason);
        }

        var reading = EncodedText.Read(read.Bytes!);
        return reading.IsRead ? (reading, FileContentHash.Of(read.Bytes!), null) : (null, null, $"'{ReportText.Printable(path)}' is {(reading.Binary ? "binary" : "no text this tool reads")}: {reading.Why}");
    }

    /// <summary>The lines of the file at <paramref name="path"/>, read fresh, with the digest of the bytes they were read from - what a page that serves them leaves as the file's view - or why they are not read.</summary>
    private async Task<(IReadOnlyList<string>? Lines, string? Digest, string? Why)> ReadLinesAsync(string path, CancellationToken cancellationToken)
    {
        var (reading, digest, why) = await ReadBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return (reading is null ? null : CodeFileText.Lines(reading.Text!.Text), digest, why);
    }

    /// <summary>The lines of <paramref name="body"/> as <c>body</c> gives them: a type's members' bodies left out unless <paramref name="full"/>, each member's signature kept.</summary>
    private static IEnumerable<(string Text, int? Number)> Shown(CodebaseGraphQueries view, GraphCard card, IReadOnlyList<string> body, bool full)
    {
        // A body is left out from the line its brace stands alone on, or from the line after the one it opens on - a
        // signature's own line (K&R, an arrow) is always kept.
        var gaps = !full && string.Equals(card.Kind, CodeNodeKinds.Type, StringComparison.Ordinal)
            ? [.. view.OutlineType(card.Id)
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

    private string Full(string path) => Path.Combine(_queries.Tree, path.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// The path <paramref name="full"/> stands at relative to the tree, with forward separators: what <see cref="Full"/>
    /// makes of a path read back, and a listing's path as the listing names it - only the system's own separator read as one,
    /// so a backslash a file's name holds where it is no separator stays the name's.
    /// </summary>
    private string Relative(string full) => Path.GetRelativePath(_queries.Tree, full).Replace(Path.DirectorySeparatorChar, '/');

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
    /// <param name="Read">The files the answer served lines of, each with the digest of the bytes they were read from: what a page that gives a file's lines leaves as its view.</param>
    private sealed record GraphAnswer(
        IReadOnlyList<string> Leading,
        IReadOnlyList<GraphSection> Sections,
        IReadOnlyList<string> Trailing,
        IReadOnlyList<string> Nodes,
        IReadOnlyList<GraphFileRead> Read)
    {
        /// <summary>
        /// The view the answer ended on, where its computing moved off the one it began on - a body that extracted a moved
        /// node's file again goes on from the view that check left - and so the digest its result is kept under; null
        /// where it ended on the view it began on, and for one read from what an earlier page kept.
        /// </summary>
        public CodebaseGraphQueries? On { get; init; }
    }

    /// <summary>
    /// The answer of a read's page, from what <paramref name="compute"/> answers with or what an earlier page kept - all of
    /// it of the one view <paramref name="view"/> the call captured as it began, never of a view a sync hands the tool while
    /// it computes. Page 1 always computes the whole answer, and keeps it where it is more than one page - under the digest
    /// of the graph of the view the answer ended on (<see cref="CodebaseGraphQueries.Graph"/>, which the step's own writes
    /// move as a fold does): the one it began on, or the one a body's check of a moved node's file left - and never reads
    /// what was kept. A later page of the same ask, resolved as the tool resolves it, reads what was kept under the digest
    /// of the view it began on, so it is a page of that one answer; where what was kept is under another digest alone - the
    /// graph moved - or no result of page 1 reads, the answer is made again on the graph as it stands and the page says
    /// which. A page with nothing kept for it outside a conversation (the verb) is counted as it stands, as every page of
    /// the verb is.
    /// </summary>
    /// <param name="input">The call's input, its page member telling the page asked.</param>
    /// <param name="view">The view the call began on, captured once: what the answer is computed on and its kept result looked up by.</param>
    /// <param name="op">The operation.</param>
    /// <param name="ask">The ask as the tool resolved it - each node by its id, each file as the view holds it, each default filled in - that its key is made of.</param>
    /// <param name="asked">How the answer names what was asked, for a page past the last to say.</param>
    /// <param name="compute">Computes the whole answer on the view it is handed, where the page asked needs it: everything the answer is made of, its text searches among it.</param>
    /// <param name="cancellationToken">Stops the computing.</param>
    private async Task<GraphToolAnswer> PagedAsync(JsonObject input, CodebaseGraphQueries view, GraphOp op, JsonObject ask, string asked, Func<CodebaseGraphQueries, CancellationToken, Task<GraphAnswer>> compute, CancellationToken cancellationToken)
    {
        var page = Int(input, "page") ?? 1;
        var key = GraphPageStore.KeyOf(NameOf(op), ask);
        GraphAnswer? answer = null;
        PageLayout<GraphEntry>? layout = null;
        string? note = null;

        if (page > 1 && _pages is not null)
        {
            var found = _pages.Find(key, view.Graph);

            if (found.Kept is { } kept)
            {
                // A result the store holds is laid out into pages once: every later page of it is given from that layout.
                answer = new GraphAnswer(kept.Leading, kept.Sections, kept.Trailing, kept.Nodes, kept.Read);
                layout = _layouts.GetValue(kept, held => GraphPages.Lay(held.Sections, held.Leading, [.. held.Trailing, .. UnheldLines(view)], MostCharacters));
            }
            else
            {
                note = found.Moved ? GraphPages.Recounted : GraphPages.NotKept(found.Why);
            }
        }

        var computed = answer is null;
        answer ??= Masked(await compute(view, cancellationToken).ConfigureAwait(false));

        // The view the answer ended on: the one it began on, or the one a body's check of a moved node's file left.
        var ended = answer.On ?? view;

        layout ??= GraphPages.Lay(answer.Sections, answer.Leading, [.. answer.Trailing, .. UnheldLines(ended)], MostCharacters);
        var paged = layout.Page(page, asked, note);

        // A result of one page has no later page to read it: only a result of more is kept, and its page says where it could not be.
        if (computed && _pages is not null && layout.Pages > 1)
        {
            var keeping = new GraphPageKept(GraphPageKept.CurrentSchema, key, ended.Graph, _pages.Reads, asked, answer.Leading, answer.Sections, answer.Trailing, answer.Nodes, answer.Read);
            _layouts.AddOrUpdate(keeping, layout);

            if (_pages.Keep(keeping) is { } unkept)
            {
                paged = layout.Page(page, asked, note is null ? GraphPages.Unkept(unkept) : $"{note} · {GraphPages.Unkept(unkept)}");
            }
        }

        if (paged.Refusal is not null)
        {
            return new GraphToolAnswer(paged.Text, GraphToolOutcome.Usage) { Paging = paged };
        }

        // What the page gave is the page's own: the nodes and the files whose lines it served, a file viewed as its lines were read.
        var served = paged.Entries.Where(entry => entry.File is not null).ToList();
        var read = answer.Read.GroupBy(file => file.Path, StringComparer.Ordinal).ToDictionary(file => file.Key, file => file.First().Digest, StringComparer.Ordinal);

        return new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered)
        {
            Facts = new GraphToolFacts(
                answer.Nodes.ToArray(),
                served.Where(entry => entry.Node is not null).Select(entry => entry.Node!).Distinct(StringComparer.Ordinal).ToArray(),
                [],
                Hits: paged.Entries.Count(entry => entry.Hit)),
            Viewed = [.. served.Select(entry => entry.File!).Distinct(StringComparer.Ordinal).Where(read.ContainsKey).Select(path => new GraphFileRead(path, read[path]))],
            Paging = paged,
        };
    }

    /// <summary>
    /// <paramref name="answer"/> with every line it shows masked by the run's secrets, where the tool is given them: its
    /// head and its notes, each section's caption, and each entry's text and the head it goes on under - what its pages
    /// give and what is kept of it. Its nodes and the files it read are named as the graph names them.
    /// </summary>
    private GraphAnswer Masked(GraphAnswer answer)
        => _mask is not { } mask ? answer : answer with
        {
            Leading = [.. answer.Leading.Select(mask.Redact)],
            Sections = [.. answer.Sections.Select(section => section with
            {
                Caption = section.Caption is null ? null : mask.Redact(section.Caption),
                Entries = [.. section.Entries.Select(entry => entry with { Text = mask.Redact(entry.Text), GoesOn = entry.GoesOn is null ? null : mask.Redact(entry.GoesOn) })],
            })],
            Trailing = [.. answer.Trailing.Select(mask.Redact)],
        };

    /// <summary>
    /// A read's answer of one sentence: said as it is, with the notes every answer ends with - one page, so a page past it
    /// is refused with its range, as any answer's page past the last is.
    /// </summary>
    private GraphToolAnswer Said(JsonObject input, string asked, string text)
    {
        var paged = GraphPages.Assemble([], [text], UnheldLines(), Int(input, "page") ?? 1, asked, MostCharacters);

        return new GraphToolAnswer(paged.Text, paged.Refusal is null ? GraphToolOutcome.Answered : GraphToolOutcome.Usage) { Paging = paged };
    }

    /// <summary>A write's answer, or a sentence no page is asked of: its lines, said as they are, with the notes every answer ends with.</summary>
    private GraphToolAnswer Said(IReadOnlyList<string> lines)
    {
        var paged = GraphPages.Assemble([], lines, UnheldLines(), 1, "graph", MostCharacters);

        return new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered) { Paging = paged };
    }

    /// <inheritdoc cref="Said(IReadOnlyList{string})"/>
    private GraphToolAnswer Said(string text) => Said([text]);

    /// <summary>
    /// A write's answer of <paramref name="lines"/>, the first said as its head and the rest as its entries, where all of
    /// it is one page; null where it is more, for a write's answer is never paged.
    /// </summary>
    private GraphToolAnswer? OnePage(IReadOnlyList<string> lines, string asked)
        => OnePage(new GraphAnswer([lines[0]], lines.Count == 1 ? [] : [Section(null, [.. lines.Skip(1).Select(line => new GraphEntry(line))], int.MaxValue)], [], [], []), asked);

    /// <summary>The first page of <paramref name="answer"/>, where all of it is one page; null where it is more.</summary>
    private GraphToolAnswer? OnePage(GraphAnswer answer, string asked)
    {
        var paged = GraphPages.Assemble(answer.Sections, answer.Leading, [.. answer.Trailing, .. UnheldLines()], 1, asked, MostCharacters);

        return paged.Pages > 1 ? null : new GraphToolAnswer(paged.Text, GraphToolOutcome.Answered) { Paging = paged };
    }

    private static string EdgeLine(GraphEdge edge)
        => string.Equals(edge.Confidence, CodeConfidences.Exact, StringComparison.Ordinal) ? GraphLines.Of(edge.Card) : $"{GraphLines.Of(edge.Card)} | {edge.Confidence}";

    /// <summary>
    /// The simple name <paramref name="symbol"/> ends with, a text search's word; null where it names a file or a line of one, which no word is.
    /// The separators a symbol is written with - a colon, a dot, a backslash - read as dots outside a quoted name alone, so a
    /// key path's word is its last name, a quoted one's own text (<see cref="CodeKeyPaths.OutsideQuotedNames"/>).
    /// </summary>
    private static string? Simple(string symbol)
    {
        if (CodebaseGraphQueries.NamesAFile(symbol))
        {
            return null;
        }

        return CodeKeyPaths.LastName(CodeKeyPaths.OutsideQuotedNames(symbol, run => run.Replace(':', '.').Replace('\\', '.')));
    }

    private static IEnumerable<GraphOp> Ordered(IEnumerable<GraphOp> ops) => ops.Order();

    private static IEnumerable<(string Name, string Type, string Description)> Described() =>
    [
        ("query", "string", "the words to match, split at case changes, underscores, digits and dots as a name is."),
        ("kind", "string", $"the one kind to keep: {string.Join(", ", CodeNodeKinds.Extracted)}."),
        ("path", "string", "a file's path - for outline a directory's too - relative to the tree with forward separators."),
        ("page", "integer", "which page of the answer to give, from 1; the first where it is left out."),
        ("symbol", "string", "a node: its handle, its id, a path:line, or a qualified or simple name."),
        ("relation", "string", $"which edge to follow: {string.Join("; ", GraphRelations.Described.Select(relation => $"{relation.Name}, {relation.Meaning}"))}."),
        ("depth", "integer", "how many hops, from 1; 1 where it is left out."),
        ("symbols", "array", "the nodes to read, one or more, each a handle, an id or a name."),
        ("full", "boolean", "whether a type's members' bodies are given too."),
        ("data", "string", "the data whose deciding places are asked: a symbol as symbol accepts one - a node's handle, its id, a path:line, or a qualified or simple name - a key path, dotted - a key whose own name holds a dot or a bracket quoted in brackets, in double or single quotes, Logging.LogLevel[\"Microsoft.AspNetCore\"] or Logging.LogLevel['Microsoft.AspNetCore'], or with its names' own dots as they are, Logging.LogLevel.Microsoft.AspNetCore, which asks of every key it may mean - or as .NET's configuration writes one (Payments:TimeoutSeconds, Payments__TimeoutSeconds, Logging:LogLevel:Microsoft.AspNetCore), or a diagnostic id, a rule's selector as a rule writes it (.btn, #main), asked by its name as a class list writes it too, or a quoted literal, asked by its value alone."),
        ("hash", "string", "the node's hash as you last saw it; the edit is refused where its lines moved since."),
        ("text", "string", "the node's new lines."),
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
        var extra = input.Select(member => member.Key).Where(name => name != "op" && !reads.Contains(name)).Select(ReportText.Printable).ToList();

        // The retired ways of going on are refused naming what took their place: a model meets one way to ask for more.
        if (extra.Count > 0)
        {
            return extra.All(name => name is "offset" or "limit") ? $"op '{Names[op]}' does not read {string.Join(", ", extra)}: an answer's further pages are asked by page, from 1"
                : extra is ["page"] ? $"op '{Names[op]}' does not read page: what it answers is one page, said once it is done"
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

        // A name that names nothing is no request: said, never thrown at the lookups - nor is a quoted data whose value is none.
        if (new[] { "query", "kind", "symbol", "path", "hash", "reason", "data" }.FirstOrDefault(name => input[name] is not null && string.IsNullOrWhiteSpace(Str(input, name))) is { } blank)
        {
            return $"{blank} is empty: {Described().First(member => member.Name == blank).Description}";
        }

        if (Str(input, "data") is { } data && QuotedValue(data) is { } value && string.IsNullOrWhiteSpace(value))
        {
            return $"data is empty: {Described().First(member => member.Name == "data").Description}";
        }

        if (Strings(input, "symbols").Any(string.IsNullOrWhiteSpace))
        {
            return "op 'body' needs symbols: one or more handles, ids or names, none empty";
        }

        if (op == GraphOp.Outline && input["path"] is not null && input["symbol"] is not null)
        {
            return "op 'outline' takes path or symbol, not both";
        }

        if (op == GraphOp.Body && Strings(input, "symbols") is not { Count: >= 1 })
        {
            return "op 'body' needs symbols: one or more handles, ids or names";
        }

        if (op == GraphOp.Related && (Str(input, "relation") is not { } relation || !GraphRelations.All.Contains(relation)))
        {
            return $"relation is '{ReportText.Printable(Str(input, "relation") ?? string.Empty)}': give one of {string.Join(", ", GraphRelations.All)}";
        }

        return Int(input, "depth") is { } depth and < 1 ? $"depth is {depth.ToString(CultureInfo.InvariantCulture)}: hops start at 1"
            : Int(input, "page") is { } asked and < 1 ? Paging.BelowTheFirst(asked)
            : null;
    }

    /// <summary>The note every answer ends with, of the files whose extract the view the tool answers from now no longer holds, where there are any.</summary>
    private IReadOnlyList<string> UnheldLines() => UnheldLines(_queries);

    /// <summary>The note every answer of <paramref name="view"/> ends with, of the files whose extract it no longer holds, where there are any.</summary>
    private IReadOnlyList<string> UnheldLines(CodebaseGraphQueries view)
    {
        var unheld = view.Unheld;

        if (unheld.Count == 0)
        {
            return [];
        }

        var readable = unheld.Where(_mayRead).ToList();
        var named = readable.Count == 0 ? string.Empty : ": " + string.Join(", ", readable.Select(ReportText.Printable));
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
