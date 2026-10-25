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
}

/// <summary>
/// The nodes one <c>graph</c> call used, each by its id: those it asked about by name - <c>node</c>, <c>related</c> and
/// <c>body</c> - those whose lines it served, and the one an <c>edit</c> wrote, with the lines it wrote.
/// </summary>
/// <param name="Asked">The nodes it asked about, in the order it named them.</param>
/// <param name="Served">The nodes a line of which a <c>body</c>'s answer gave, in order.</param>
/// <param name="Edited">The node an <c>edit</c> wrote, by the id it had; none where it wrote none.</param>
/// <param name="Wrote">The lines an <c>edit</c> wrote, as the file stood once it had; null where it wrote none.</param>
public sealed record GraphToolFacts(IReadOnlyList<string> Asked, IReadOnlyList<string> Served, IReadOnlyList<string> Edited, GraphWrittenLines? Wrote = null)
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
/// The one tool <c>graph</c>, over the lookups of <see cref="CodebaseGraphQueries"/>: its seven operations - <c>find</c>,
/// <c>node</c>, <c>related</c>, <c>outline</c>, <c>body</c>, <c>edit</c> and <c>wholeFile</c> - the same on every client, each
/// answering in bounded lines of text with a line range and a hash on every node, and a text search where the graph is not
/// sure. The schema a step is given holds only the operations it may use. Nothing opens it in a step yet (part 03's tool
/// runtime; slice 27d gives it one); the <c>query-codebase-graph</c> verb runs its read operations by hand.
/// </summary>
/// <remarks>
/// It answers from the view it was handed - checked fresh by the caller, as the engine checks every answer's files
/// (<see cref="ICodebaseGraphService.CheckAsync"/>) - and reads a body or an edit's file fresh itself, so a node that moved
/// since is told by its hash. Where it is handed <c>reExtract</c>, a moved node is extracted again before its body is
/// served, and an edit's file after it is written, and the tool answers from the view that gives from then on.
/// </remarks>
/// <param name="queries">The indexed view it answers from.</param>
/// <param name="files">The one search a text fallback runs, as <c>search_files</c> runs it.</param>
/// <param name="fileSystem">Reads a body fresh, and writes an edit.</param>
/// <param name="mayRead">Whether the step's policy lets a file be read, by its path relative to the tree: a node of a file it does not is left out, and counted.</param>
/// <param name="mayWrite">The step's policy on writing a file, by its path relative to the tree; null where <c>edit</c> is not given.</param>
/// <param name="reExtract">Checks a file again by its path relative to the tree and answers the view it leaves; null where nothing may be synced (the verb).</param>
/// <param name="near">How near each node is to the step's own work, which breaks a tie in <c>find</c> (the step's context pack gives it); null where there is no step.</param>
public sealed class CodebaseGraphTool(
    CodebaseGraphQueries queries,
    AgentFiles files,
    IFileSystem fileSystem,
    Func<string, bool> mayRead,
    Func<string, AgentPolicyAnswer>? mayWrite = null,
    Func<string, CancellationToken, Task<CodebaseGraphQueries>>? reExtract = null,
    GraphNearness? near = null)
{
    /// <summary>The most characters an answer holds, cut at a whole line past it.</summary>
    public const int MostCharacters = 24 * 1024;

    /// <summary>The most nodes a <c>find</c> gives where it names no limit.</summary>
    public const int FindLines = 20;

    /// <summary>The most nodes a <c>find</c> may ask for.</summary>
    public const int FindMost = 50;

    /// <summary>The most edges a <c>node</c> gives of each kind, with the count of the rest.</summary>
    public const int NodeLinesAKind = 15;

    /// <summary>The most nodes a <c>related</c> walk gives.</summary>
    public const int RelatedLines = 60;

    /// <summary>The most declarations an <c>outline</c> gives.</summary>
    public const int OutlineLines = 400;

    /// <summary>The most lines a <c>body</c> gives, all its symbols together.</summary>
    public const int BodyLines = 600;

    /// <summary>The most symbols one <c>body</c> reads.</summary>
    public const int BodySymbols = 8;

    /// <summary>The most hits a text search where the graph is not sure adds.</summary>
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
    private readonly Func<string, bool> _mayRead = mayRead ?? throw new ArgumentNullException(nameof(mayRead));
    private readonly SemaphoreSlim _extracting = new(1, 1);
    private readonly SemaphoreSlim _editing = new(1, 1);
    private CodebaseGraphQueries _queries = queries ?? throw new ArgumentNullException(nameof(queries));

    /// <summary>The read operations, which a step with the <c>read</c> capability and the verb are given.</summary>
    public static IReadOnlyList<GraphOp> Read { get; } = [GraphOp.Find, GraphOp.Node, GraphOp.Related, GraphOp.Outline, GraphOp.Body];

    /// <summary>The view the tool answers from now: the one it was handed, or the one its last extraction left.</summary>
    public CodebaseGraphQueries Queries => _queries;

    /// <summary>The name <c>op</c> says <paramref name="op"/> by.</summary>
    /// <param name="op">An operation.</param>
    public static string NameOf(GraphOp op) => Names[op];

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

    /// <summary>The members each operation reads beside <c>op</c>, and of them those it needs.</summary>
    /// <param name="op">An operation.</param>
    public static (IReadOnlyList<string> Reads, IReadOnlyList<string> Needs) Members(GraphOp op) => op switch
    {
        GraphOp.Find => (["query", "kind", "path", "limit", "offset"], ["query"]),
        GraphOp.Node => (["symbol"], ["symbol"]),
        GraphOp.Related => (["symbol", "relation", "depth", "offset"], ["symbol", "relation"]),
        GraphOp.Outline => (["path", "symbol", "offset"], []),
        GraphOp.Body => (["symbols", "full"], ["symbols"]),
        GraphOp.Edit => (["symbol", "hash", "text"], ["symbol", "hash", "text"]),
        GraphOp.WholeFile => (["path", "reason"], ["path", "reason"]),
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
            [GraphOp.Find] = $"find - the nodes a query's words match, best first, at most {FindLines} ({FindMost} with limit)",
            [GraphOp.Node] = $"node - a node's card and its edges by kind, {NodeLinesAKind} of each",
            [GraphOp.Related] = $"related - the nodes a relation reaches, 1 to {MostDepth} hops, at most {RelatedLines}",
            [GraphOp.Outline] = $"outline - a file's or a type's declarations, or the repository's, at most {OutlineLines}",
            [GraphOp.Body] = $"body - the lines of 1 to {BodySymbols} nodes, read fresh and numbered, at most {BodyLines} in all",
            [GraphOp.Edit] = $"edit - a node's lines replaced where the hash you give is its hash now, at most {MostEditLines} lines",
            [GraphOp.WholeFile] = "wholeFile - lifts the narrowing of reads for one file for the rest of the step, with why",
        };

        return "Asks the codebase graph of the tree you work in: "
            + string.Join("; ", Ordered(ops).Select(op => said[op]))
            + $". Every node is named with its handle and path:start-end #hash - read a range of a file by those lines - and an answer past {(MostCharacters / 1024).ToString(CultureInfo.InvariantCulture)} KB is cut at a whole line, saying the offset to ask again with. Where the graph is not sure, the answer adds text: lines that hold the name.";
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
        var limit = Int(input, "limit") ?? FindLines;
        var offset = Int(input, "offset") ?? 0;
        var matched = _queries.Find(query, Str(input, "kind"), Str(input, "path") is { } path ? _queries.HeldPath(path) ?? path : null, int.MaxValue, 0, near).Cards;
        var (kept, left) = Readable(matched);
        var text = kept.Count == 0 ? "no node matched" : Paged([.. kept.Select(GraphLines.Of)], offset, limit);

        if (matched.Count == 0)
        {
            text += await SearchAsync(query, cancellationToken).ConfigureAwait(false);
        }

        return Answered(text + LeftOut(left));
    }

    private async Task<GraphToolAnswer> NodeAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;

        if (Resolved(symbol) is not { } card)
        {
            return Answered(await UnresolvedAsync(symbol, cancellationToken).ConfigureAwait(false));
        }

        var lines = new List<string> { GraphLines.Of(card) };
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

            lines.Add($"{group} ({kept.Count}):");
            lines.AddRange(kept.Take(NodeLinesAKind).Select(edge => "  " + EdgeLine(edge)));
            unsure |= kept.Take(NodeLinesAKind).Any(edge => string.Equals(edge.Confidence, CodeConfidences.Candidates, StringComparison.Ordinal) || _queries.Unsure(edge.Card.Id));

            if (kept.Count > NodeLinesAKind)
            {
                lines.Add($"  cut: {NodeLinesAKind} of {kept.Count} shown");
            }
        }

        var text = string.Join('\n', lines);

        if (unsure)
        {
            text += await SearchAsync(card.Name, cancellationToken).ConfigureAwait(false);
        }

        return Answered(text + LeftOut(left), new GraphToolFacts([card.Id], [], []));
    }

    private async Task<GraphToolAnswer> RelatedAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var symbol = Str(input, "symbol")!;
        var relation = Str(input, "relation")!;
        var depth = Int(input, "depth") ?? 1;
        var offset = Int(input, "offset") ?? 0;

        if (Resolved(symbol) is not { } card)
        {
            return Answered(await UnresolvedAsync(symbol, cancellationToken).ConfigureAwait(false));
        }

        var reached = _queries.Related(card.Id, relation, depth);
        var kept = reached.Where(one => Reads(one.Card)).ToList();
        var lines = kept.Select(one => one.Crowded is { } edges
            ? $"{one.Distance}: {GraphLines.Of(one.Card)} | {edges} {relation}, not gone through"
            : $"{one.Distance}: {GraphLines.Of(one.Card)}").ToList();
        var text = lines.Count == 0 ? $"no node is {relation} of '{card.Qualified}'" : Paged(lines, offset, RelatedLines);

        if (_queries.Unsure(card.Id) || kept.Any(one => one.Candidates || _queries.Unsure(one.Card.Id)))
        {
            text += await SearchAsync(card.Name, cancellationToken).ConfigureAwait(false);
        }

        return Answered(text + LeftOut(reached.Count - kept.Count), new GraphToolFacts([card.Id], [], []));
    }

    private async Task<GraphToolAnswer> OutlineAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var offset = Int(input, "offset") ?? 0;

        if (Str(input, "path") is { } given)
        {
            var path = _queries.HeldPath(given) ?? given;

            if (!_mayRead(path))
            {
                return Answered($"'{path}' is a file your reads do not reach");
            }

            if (_queries.OutlineFile(path) is not { } cards)
            {
                return Answered($"the view holds no file at '{path}'");
            }

            return Answered($"outline of {path} ({ReportText.Counted(cards.Count, "declaration")}):\n" + (cards.Count == 0 ? "no declaration" : Paged([.. cards.Select(GraphLines.Of)], offset, OutlineLines)));
        }

        if (Str(input, "symbol") is { } symbol)
        {
            if (Resolved(symbol) is not { } card)
            {
                return Answered(await UnresolvedAsync(symbol, cancellationToken).ConfigureAwait(false));
            }

            var members = _queries.OutlineType(card.Id);
            var (kept, left) = Readable(members);
            var text = GraphLines.Of(card) + "\n" + (kept.Count == 0 ? "no declaration inside it" : Paged([.. kept.Select(one => "  " + GraphLines.Of(one))], offset, OutlineLines));
            return Answered(text + LeftOut(left));
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

        return Answered($"{_queries.View} at {_queries.Head ?? "no commit"}: {ReportText.Counted(_queries.FileCount, "file")}\n" + (lines.Count == 0 ? "nothing to outline" : Paged(lines, offset, OutlineLines)));
    }

    private async Task<GraphToolAnswer> BodyAsync(JsonObject input, CancellationToken cancellationToken)
    {
        var full = Flag(input, "full");
        var builder = new StringBuilder();
        var given = 0;
        var asked = new List<string>();

        // Each node whose lines began to be given, with where its first line ends in the answer: served only where the cap keeps it.
        var served = new List<(string Id, int End)>();

        foreach (var symbol in Strings(input, "symbols"))
        {
            if (Resolved(symbol) is not { } card)
            {
                builder.Append(await UnresolvedAsync(symbol, cancellationToken).ConfigureAwait(false)).Append('\n');
                continue;
            }

            asked.Add(card.Id);

            var (lines, why) = await ReadLinesAsync(card.Path, cancellationToken).ConfigureAwait(false);
            var hash = lines is null || card.Hash is null ? null : Hash(lines, card);

            // A declaration whose lines moved since the view was read is extracted again, where the tool may check its file, and served as it is now.
            if (hash is not null && hash != card.Hash && reExtract is not null)
            {
                if ((await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false)).Part(card.Id, card.Path) is not { } found)
                {
                    builder.Append($"'{card.Qualified}' is no longer declared in '{card.Path}'").Append('\n');
                    continue;
                }

                card = found;
                (lines, why) = await ReadLinesAsync(card.Path, cancellationToken).ConfigureAwait(false);
                hash = lines is null ? null : Hash(lines, card);
            }

            if (lines is null)
            {
                builder.Append($"'{card.Qualified}' is not read: {why}").Append('\n');
                continue;
            }

            // A span the file no longer reaches has no lines to number: said, never served from where they are not.
            if (hash == Gone)
            {
                builder.Append($"'{card.Qualified}' is not at {card.Path}:{card.Start}-{card.End} now: the file holds {ReportText.Counted(lines.Count, "line")}; find it again").Append('\n');
                continue;
            }

            builder.Append($"{card.Handle} {card.Qualified} {card.Path}:{card.Start}-{card.End}");

            // A whole file is no declaration and has no hash; a declaration's is the one its lines give now.
            if (hash is not null)
            {
                builder.Append($" #{hash}");

                if (hash != card.Hash)
                {
                    builder.Append($" (changed since the graph read it as #{card.Hash})");
                }
            }

            builder.Append('\n');
            var first = true;

            foreach (var (text, number) in Shown(card, CodebaseGraphQueries.Body(card, lines), full))
            {
                if (given == BodyLines)
                {
                    builder.Append($"cut: {BodyLines} lines shown; ask for fewer symbols, or one at a time").Append('\n');
                    return Served(builder, asked, served);
                }

                builder.Append(number is { } line ? $"{line.ToString(CultureInfo.InvariantCulture)}\t{text}" : text);

                if (first)
                {
                    served.Add((card.Id, builder.Length));
                    first = false;
                }

                builder.Append('\n');
                given++;
            }

            if (_queries.Unsure(card.Id))
            {
                builder.Append((await SearchAsync(card.Name, cancellationToken).ConfigureAwait(false)).TrimStart('\n')).Append('\n');
            }
        }

        return Served(builder, asked, served);
    }

    /// <summary>
    /// A body's answer, cut at its cap, and the nodes it used: each asked about, and each one of whose lines the answer
    /// gives - the cap keeps what the answer was before it, up to a whole line.
    /// </summary>
    private GraphToolAnswer Served(StringBuilder builder, IReadOnlyList<string> asked, IReadOnlyList<(string Id, int End)> served)
    {
        var text = builder.ToString().TrimEnd('\n');
        var answer = Answered(text);

        return answer with
        {
            Facts = new GraphToolFacts(
                [.. asked.Distinct(StringComparer.Ordinal)],
                [.. served.Where(one => answer.Text.Length >= one.End && answer.Text.AsSpan(0, one.End).SequenceEqual(text.AsSpan(0, one.End))).Select(one => one.Id).Distinct(StringComparer.Ordinal)],
                []),
        };
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
            return Answered(await UnresolvedAsync(symbol, cancellationToken).ConfigureAwait(false));
        }

        if (!string.Equals(card.Hash, hash, StringComparison.OrdinalIgnoreCase)
            && _queries.Parts(card.Id).FirstOrDefault(part => string.Equals(part.Hash, hash, StringComparison.OrdinalIgnoreCase) && Reads(part)) is { } named)
        {
            card = named;
        }

        if (card.Hash is null)
        {
            return Answered($"'{card.Qualified}' is a whole file, and no declaration an edit replaces: edit a declaration in it");
        }

        if ((mayWrite?.Invoke(card.Path) ?? new AgentPolicyAnswer.Denied("graph", AgentPolicySource.Builtin, "edit is not given to this step")) is AgentPolicyAnswer.Denied denied)
        {
            return new GraphToolAnswer(denied.Reason, GraphToolOutcome.Refused, card.Path, denied);
        }

        // An edit answers the card its file's new extract gives, and finds a node that moved by it: a view it cannot check again is no view to edit by.
        if (reExtract is null)
        {
            return Answered("edit is not run here: it needs the step's view, which this tool cannot check again");
        }

        var (reading, why) = await ReadFileAsync(card.Path, cancellationToken).ConfigureAwait(false);

        if (reading is null)
        {
            return Answered(why!);
        }

        var now = Hash(CodeFileText.Lines(reading.Text!.Text), card);

        // The node may only have moved since the view was read: its file is checked again, and the hash compared where it stands now.
        if (!Same(now, hash))
        {
            if ((await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false)).Part(card.Id, card.Path) is not { } found)
            {
                return Answered($"'{card.Qualified}' is no longer declared in '{card.Path}': find it again");
            }

            card = found;
            (reading, why) = await ReadFileAsync(card.Path, cancellationToken).ConfigureAwait(false);

            if (reading is null)
            {
                return Answered(why!);
            }

            now = Hash(CodeFileText.Lines(reading.Text!.Text), card);

            // Optimistic concurrency: the lines are written only where they are the ones the agent saw.
            if (!Same(now, hash))
            {
                return Answered($"'{card.Qualified}' changed since you saw it (#{hash}, now #{now}, lines {card.Start}-{card.End}): read it again and edit with the new hash\n{GraphLines.Of(card with { Hash = now })}");
            }
        }

        var edited = EditedText.Of(reading.Text!);
        var was = edited.LineCount;
        edited.ReplaceLines(card.Start, card.End, text);
        var moved = edited.LineCount - was;
        await _fileSystem.WriteAllBytesAtomicAsync(Full(card.Path), edited.Bytes(), cancellationToken).ConfigureAwait(false);

        var after = await ExtractedAgainAsync(card.Path, cancellationToken).ConfigureAwait(false);
        var written = card.End - card.Start + 1 + moved;
        var shift = moved == 0 ? "no later declaration moved" : $"the file's later declarations moved {ReportText.Counted(Math.Abs(moved), "line")} {(moved > 0 ? "down" : "up")}";
        var held = (after.OutlineFile(card.Path) ?? []).Where(one => one.Start >= card.Start && one.Start < card.Start + written).Select(one => "  " + GraphLines.Of(one)).ToList();
        var said = after.Part(card.Id, card.Path) is { } refound ? GraphLines.Of(refound)
            : written == 0 ? $"the new lines are none: '{card.Qualified}' is gone from '{card.Path}'"
            : held.Count == 0 ? $"the new lines no longer declare '{card.Qualified}', nor anything else"
            : $"the new lines no longer declare '{card.Qualified}'; they hold:\n" + string.Join('\n', held);

        return new GraphToolAnswer(Capped($"edited '{card.Qualified}' in '{card.Path}'; {shift}\n{said}", Unheld()), GraphToolOutcome.Wrote, card.Path) { Facts = new GraphToolFacts([], [], [card.Id], new GraphWrittenLines(card.Path, card.Start, written, WrittenHash(edited, card.Start, written))) };
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

    private GraphToolAnswer WholeFile(JsonObject input)
        => Answered($"the narrowing of reads is lifted for '{Str(input, "path")}' for the rest of this step: {Str(input, "reason")}");

    /// <summary>
    /// The node <paramref name="symbol"/> names where it names one the step may read; null otherwise. A node whose part that
    /// speaks for it is in a file the step may not read is not served by another part: its edges come from that part too.
    /// </summary>
    private GraphCard? Resolved(string symbol) => _queries.Resolve(symbol) is GraphResolve.Found found && Reads(found.Card) ? found.Card : null;

    /// <summary>What a symbol that names no one node the step reads is answered with: its candidates, or a text search for it.</summary>
    private async Task<string> UnresolvedAsync(string symbol, CancellationToken cancellationToken)
    {
        switch (_queries.Resolve(symbol))
        {
            case GraphResolve.Candidates candidates:
                var (kept, left) = Readable(candidates.Cards);
                return $"'{symbol}' is not one name: {ReportText.Counted(kept.Count, "node")} carry it - ask again by a handle\n" + string.Join('\n', kept.Select(GraphLines.Of)) + LeftOut(left);

            case GraphResolve.Found:
                return $"'{symbol}' is in a file your reads do not reach";

            default:
                return $"'{symbol}' is not a node the view holds" + (Simple(symbol) is { } name ? await SearchAsync(name, cancellationToken).ConfigureAwait(false) : string.Empty);
        }
    }

    /// <summary>The lines that hold <paramref name="name"/> as a whole word, case-sensitive, as <c>search_files</c> searches - the text a not-sure answer ends with.</summary>
    private async Task<string> SearchAsync(string name, CancellationToken cancellationToken)
    {
        var word = name.Trim();

        if (word.Length == 0)
        {
            return string.Empty;
        }

        try
        {
            var pattern = new Regex($@"(?<![\p{{L}}\p{{N}}_]){Regex.Escape(word)}(?![\p{{L}}\p{{N}}_])", RegexOptions.CultureInvariant, AgentFiles.PatternBound);
            var search = await _files.SearchAsync(_queries.Tree, pattern, null, StringComparison.Ordinal, 0, TextHits, file => _mayRead(Relative(file)), cancellationToken).ConfigureAwait(false);

            var notes = string.Concat(search.LeftOut(path => path).Select(note => "\n  " + note));

            if (search.Hits.Count == 0)
            {
                return $"\ntext: no line holds '{word}'{notes}";
            }

            var more = search.More > 0 ? $"; {ReportText.Counted(search.More, "more line")} left out" : string.Empty;
            return $"\ntext: lines that hold '{word}'{more}\n" + string.Join('\n', search.Hits.Select(hit => $"  {hit.Path}:{hit.Line.ToString(CultureInfo.InvariantCulture)} {Hit(hit.Text.Trim())}")) + notes;
        }
        catch (RegexMatchTimeoutException)
        {
            return $"\ntext: the search for '{word}' took too long and was left off";
        }
    }

    /// <summary>
    /// The file at <paramref name="path"/> read whole, in its encoding, or why it is not: none there, past the bytes a view
    /// holds whole (<see cref="AgentTools.MostViewedBytes"/>), or no text this tool reads.
    /// </summary>
    private async Task<(EncodedTextReading? Reading, string? Why)> ReadFileAsync(string path, CancellationToken cancellationToken)
    {
        var full = Full(path);

        if (_fileSystem.StandingOf(full) is not { } standing)
        {
            return (null, $"there is no file at '{path}'");
        }

        if (standing.Length > AgentTools.MostViewedBytes)
        {
            return (null, $"'{path}' holds {DiskSpace.Size(standing.Length)}, past the {DiskSpace.Size(AgentTools.MostViewedBytes)} a body or an edit reads whole: read a range of it");
        }

        var reading = EncodedText.Read(await _fileSystem.ReadAllBytesAsync(full, cancellationToken).ConfigureAwait(false));
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

    private static string LeftOut(int left) => left == 0 ? string.Empty : $"\n{ReportText.Counted(left, "node")} left out: in files your reads do not reach";

    /// <summary>
    /// The page of <paramref name="lines"/> from <paramref name="offset"/>: at most <paramref name="bound"/> and no more than
    /// fits the answer's cap, cut at a whole line with how many were shown and the offset to ask again with.
    /// </summary>
    private static string Paged(IReadOnlyList<string> lines, int offset, int bound)
    {
        var built = new StringBuilder();
        var shown = 0;

        // Room is kept for the cut's own line, a text search's and a left-out count after it.
        foreach (var line in lines.Skip(offset).Take(bound))
        {
            if (built.Length + line.Length + 1 > MostCharacters - 4096)
            {
                break;
            }

            built.Append(line).Append('\n');
            shown++;
        }

        var text = built.ToString().TrimEnd('\n');
        var through = offset + shown;

        if (offset >= lines.Count && lines.Count > 0)
        {
            return $"offset {offset.ToString(CultureInfo.InvariantCulture)} is past the last of {lines.Count.ToString(CultureInfo.InvariantCulture)}";
        }

        if (through < lines.Count)
        {
            var range = offset == 0 ? shown.ToString(CultureInfo.InvariantCulture) : $"{(offset + 1).ToString(CultureInfo.InvariantCulture)}-{through.ToString(CultureInfo.InvariantCulture)}";
            text += $"\ncut: {range} of {lines.Count.ToString(CultureInfo.InvariantCulture)} shown; ask again with offset {through.ToString(CultureInfo.InvariantCulture)}";
        }

        return text;
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
        ("limit", "integer", $"the most nodes to give, {FindLines} where it is left out, up to {FindMost}."),
        ("offset", "integer", "how many to pass over, for the next page an answer's cut names."),
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

        if (extra.Count > 0)
        {
            return $"op '{Names[op]}' does not read {string.Join(", ", extra)}: it reads {(reads.Count == 0 ? "nothing more" : string.Join(", ", reads))}";
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
            : Int(input, "limit") is { } limit and (< 1 or > FindMost) ? $"limit is {limit.ToString(CultureInfo.InvariantCulture)}: give 1 to {FindMost}"
            : Int(input, "offset") is < 0 ? "offset is below 0: give how many to pass over, from 0"
            : null;
    }

    /// <summary>An answer, cut at its cap, ending with the files whose declarations no answer of this view can give, where there are any.</summary>
    private GraphToolAnswer Answered(string text) => new(Capped(text, Unheld()), GraphToolOutcome.Answered);

    private GraphToolAnswer Answered(string text, GraphToolFacts facts) => Answered(text) with { Facts = facts };

    private string Unheld()
    {
        var unheld = _queries.Unheld;

        if (unheld.Count == 0)
        {
            return string.Empty;
        }

        var readable = unheld.Where(_mayRead).ToList();
        var named = readable.Count == 0 ? string.Empty : ": " + string.Join(", ", readable.Take(UnheldNamed)) + (readable.Count > UnheldNamed ? ", …" : string.Empty);
        var hidden = unheld.Count - readable.Count;
        var unread = hidden == 0 ? string.Empty : $" ({ReportText.Counted(hidden, "file")} of them where your reads do not reach)";
        return $"\n{ReportText.Counted(unheld.Count, "file")} whose extract the graph no longer holds, its declarations in no answer{named}{unread}";
    }

    private static GraphToolAnswer Usage(string text) => new(text, GraphToolOutcome.Usage);

    /// <summary><paramref name="text"/> and <paramref name="tail"/>, the text cut at its last whole line within the answer's cap, saying so, where they are longer.</summary>
    private static string Capped(string text, string tail)
    {
        if (text.Length + tail.Length <= MostCharacters)
        {
            return text + tail;
        }

        const string Note = "\ncut: the answer passed its cap; ask for less";
        var room = MostCharacters - Note.Length - tail.Length;
        var end = text.LastIndexOf('\n', room);
        return (end > 0 ? text[..end] : TextCut.Head(text, room)) + Note + tail;
    }

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
