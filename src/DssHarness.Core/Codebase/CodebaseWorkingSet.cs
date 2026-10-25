using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents;
using DssHarness.Core.Agents.ClaudeCode;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Agents.Runtime;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Codebase;

/// <summary>How a step used a node, as its working set says it.</summary>
public static class WorkingSetUses
{
    /// <summary>A write met the node's own lines - the <c>graph</c> tool's <c>edit</c>, <c>Edit</c>, <c>Write</c>, <c>replace_in_file</c>, <c>insert_in_file</c>, <c>create_file</c> - or an <c>edit</c> named it.</summary>
    public const string Edited = "edited";

    /// <summary>The <c>graph</c> tool's <c>body</c> served its lines.</summary>
    public const string Served = "served";

    /// <summary>A read met its own lines: <c>Read</c>, <c>view_file</c>.</summary>
    public const string Read = "read";

    /// <summary>The <c>graph</c> tool was asked about it by name: <c>node</c>, <c>related</c>, <c>body</c>.</summary>
    public const string Found = "found";

    /// <summary>Every use, in the order a node's uses are written.</summary>
    public static IReadOnlyList<string> All { get; } = [Edited, Served, Read, Found];
}

/// <summary>One node a step touched: its id, how it used it and how often, and - where the view still holds it at the step's end - its handle, file, lines and hash then.</summary>
/// <param name="Id">The node's id.</param>
/// <param name="Uses">How the step used it, each once, in <see cref="WorkingSetUses.All"/>' order.</param>
/// <param name="Times">How often it was touched: once for each of its uses in each call that touched it.</param>
/// <param name="Handle">Its handle at the step's end; null where the view no longer holds it.</param>
/// <param name="Path">The file it was touched in, or declared in, at the step's end; null where the view no longer holds it.</param>
/// <param name="Start">Its first line there; null where the view no longer holds it.</param>
/// <param name="End">Its last line there, inclusive; null where the view no longer holds it.</param>
/// <param name="Hash">Its hash there - the first eight characters of its digest; null for a node with none, and where the view no longer holds it.</param>
/// <param name="Gone">Whether the view no longer holds it at the step's end: kept by its id alone.</param>
public sealed record WorkingSetNode(
    string Id,
    IReadOnlyList<string> Uses,
    int Times,
    string? Handle = null,
    string? Path = null,
    int? Start = null,
    int? End = null,
    string? Hash = null,
    bool Gone = false)
{
    /// <summary>Whether a write met it: a seed above the rest.</summary>
    [JsonIgnore]
    public bool Edited => Uses.Contains(WorkingSetUses.Edited, StringComparer.Ordinal);

    /// <summary>Why the node does not read as one, or null where it does.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Id) ? "a node is named by its id"
            : Uses.Count == 0 || !Uses.SequenceEqual(WorkingSetUses.All.Where(Uses.Contains), StringComparer.Ordinal)
                ? $"'{Id}' is used as each of {string.Join(", ", WorkingSetUses.All)} once at most, in that order, and as one at least"
            : Times < Uses.Count ? $"'{Id}' is touched at least once for each of its uses"
            : Gone && (Handle ?? Path ?? Hash) is not null || Gone && (Start ?? End) is not null ? $"'{Id}' is gone, and kept by its id alone"
            : !Gone && (string.IsNullOrWhiteSpace(Handle) || string.IsNullOrWhiteSpace(Path) || Start is not >= 1 || End < Start || End is null)
                ? $"'{Id}' is held, and named by its handle, its file and its lines"
            : null;
}

/// <summary>
/// What a step found and used (value 372): each node it edited, served, read and found, to be written by the engine when
/// the step ends as <c>working-set.json</c> beside its output - never by the model; nothing writes it yet - and read as
/// seeds by the context packs of the steps after it (<see cref="ContextPackRequest.Worked"/>).
/// </summary>
/// <remarks>
/// It is made from the step's <c>graph.jsonl</c> (<see cref="GraphCallLine"/>) and the tool calls its stored stream holds,
/// on every client's form of them, each read and write matched to the nodes the view holds at the step's end whose own
/// lines it meets (<see cref="CodeOwnLines"/>): a read by where the lines its answer gave stand once in the file as the
/// step left it, else by their numbers; a write by where its new text stands there once - every place, where it replaced
/// every occurrence - else by the lines its call names; a whole file's write by every line. Text is found there line by
/// line, each line's trailing blanks aside, as a client may strip them as it writes.
/// </remarks>
/// <param name="Nodes">The nodes, at most <see cref="MostNodes"/>: edited first, then by how often each was touched, then by when first - the graph's calls before the stream's.</param>
/// <param name="LeftOut">How many more were touched past <see cref="MostNodes"/>.</param>
/// <param name="Unplaced">
/// How many writes were matched to no node: a deletion, which writes no lines, and new text found nowhere it may stand -
/// once, or at least once where it replaced every occurrence - by a call that names no lines.
/// </param>
public sealed partial record CodebaseWorkingSet(IReadOnlyList<WorkingSetNode> Nodes, int LeftOut = 0, int Unplaced = 0)
{
    /// <summary>The most nodes a working set keeps.</summary>
    public const int MostNodes = 200;

    /// <summary>Why the record does not read as one, or null where it does.</summary>
    public string? Problem()
    {
        if (Nodes.Count > MostNodes)
        {
            return $"a working set keeps at most {MostNodes.ToString(CultureInfo.InvariantCulture)} nodes";
        }

        if (LeftOut < 0 || Unplaced < 0)
        {
            return "a count is never below 0";
        }

        if (Nodes.Select(node => node.Problem()).FirstOrDefault(problem => problem is not null) is { } problem)
        {
            return problem;
        }

        return Nodes.GroupBy(node => node.Id, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1) is { } twice
            ? $"'{twice.Key}' is in the working set twice"
            : null;
    }

    /// <summary>
    /// The working set of a step that has ended: the nodes its <c>graph</c> calls asked about, served and edited, and those
    /// whose own lines its reads and writes met, as <paramref name="view"/> - its view at the step's end - holds them.
    /// </summary>
    /// <param name="view">The step's view, synced at its end.</param>
    /// <param name="stream">The step's stored stream (<see cref="Orchestration.OrchestrationStore.ReadStepStream"/>), every attempt's.</param>
    /// <param name="graph">The step's <c>graph.jsonl</c>, in order.</param>
    /// <param name="fileSystem">Reads the files the step wrote, as it left them.</param>
    /// <param name="pathComparison">How the platform compares paths: a call's path under the tree is told by it.</param>
    public static CodebaseWorkingSet Make(
        CodebaseGraphQueries view,
        IReadOnlyList<AgentEvent> stream,
        IReadOnlyList<GraphStepLine> graph,
        IFileSystem fileSystem,
        StringComparison pathComparison)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(fileSystem);

        return new Making(view, fileSystem, pathComparison).Make(stream, graph);
    }

    /// <summary>
    /// The seeds <paramref name="sets"/> give a step's pack, in the order they are given: each node the view held once,
    /// edited where any set says so - a node gone is no seed.
    /// </summary>
    /// <param name="sets">The working sets the step is given: of the step it goes on with, of each it depends on directly, of its own earlier visits.</param>
    public static IReadOnlyList<ContextPackWorked> Seeds(IEnumerable<CodebaseWorkingSet> sets)
    {
        ArgumentNullException.ThrowIfNull(sets);

        var seeds = new List<ContextPackWorked>();
        var at = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var node in sets.SelectMany(set => set.Nodes).Where(node => !node.Gone))
        {
            if (!at.TryGetValue(node.Id, out var index))
            {
                at[node.Id] = seeds.Count;
                seeds.Add(new ContextPackWorked(node.Id, node.Edited));
            }
            else if (node.Edited && !seeds[index].Edited)
            {
                seeds[index] = seeds[index] with { Edited = true };
            }
        }

        return seeds;
    }

    /// <summary>The making of one working set: each touch of a node counted, in the order first touched.</summary>
    private sealed class Making(CodebaseGraphQueries view, IFileSystem fileSystem, StringComparison pathComparison)
    {
        private readonly Dictionary<string, Touch> _touched = new(StringComparer.Ordinal);
        private readonly Dictionary<string, IReadOnlyList<string>?> _lines = new(StringComparer.Ordinal);
        private int _unplaced;

        public CodebaseWorkingSet Make(IReadOnlyList<AgentEvent> stream, IReadOnlyList<GraphStepLine> graph)
        {
            foreach (var call in graph.OfType<GraphCallLine>())
            {
                // The lines an edit wrote - where their hash stands once now, else where it wrote them - matched as every write's
                // are; the node it named, where they declare it no longer.
                var met = call.Wrote is { Count: > 0 } wrote && view.HeldPath(wrote.Path) is { } written
                    ? Met(written, [Hashed(written, wrote) ?? (wrote.Start, wrote.Start + wrote.Count - 1)], WorkingSetUses.Edited)
                    : [];

                Touched([.. call.Edited.Except(met, StringComparer.Ordinal)], WorkingSetUses.Edited);
                Touched(call.Served, WorkingSetUses.Served);
                Touched(call.Asked, WorkingSetUses.Found);
            }

            // A call is named by its start, or by the message of the model that made it.
            var tools = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var @event in stream)
            {
                switch (@event)
                {
                    case ToolCallStarted started:
                        tools[started.Id] = started.Tool;
                        break;

                    case AssistantMessage message:
                        foreach (var made in message.Calls)
                        {
                            tools[made.Id] = made.Name;
                        }

                        break;

                    case ToolCallFinished { Ok: true } finished when tools.TryGetValue(finished.Id, out var tool) && Input(finished.Input) is { } input:
                        Call(tool, input, finished.Result);
                        break;
                }
            }

            List<WorkingSetNode> nodes = [.. _touched.Values
                .OrderByDescending(touch => touch.Uses.Contains(WorkingSetUses.Edited))
                .ThenByDescending(touch => touch.Times)
                .ThenBy(touch => touch.Order)
                .Select(Node)];

            return new CodebaseWorkingSet([.. nodes.Take(MostNodes)], Math.Max(0, nodes.Count - MostNodes), _unplaced);
        }

        /// <summary>One call of a client's file tools, as its client names it: a read by the lines its answer gave, a write by the lines it wrote.</summary>
        private void Call(string tool, JsonObject input, string result)
        {
            switch (tool)
            {
                case ClaudeCodeTools.Read or AgentTools.ViewFile:
                    if (Held(input, tool) is { } read && Numbered(result) is { } given)
                    {
                        // Where the lines it gave stand once now - a later write may have moved them - else where their numbers say.
                        Met(read, [given.Text is { } text && Located(read, text, every: false) is [var once] ? once : (given.First, given.Last)], WorkingSetUses.Read);
                    }

                    break;

                case ClaudeCodeTools.Edit or AgentTools.ReplaceInFile:
                    Wrote(input, tool, Text(input, "new_string"), Flag(input, "replace_all"), named: null);
                    break;

                case AgentTools.InsertInFile:
                    Wrote(input, tool, Text(input, "text"), every: false, Number(input, "line") is { } line && Text(input, "text") is { } inserted
                        ? (line, line + Math.Max(1, CodeFileText.Lines(inserted).Count) - 1)
                        : null);
                    break;

                case ClaudeCodeTools.Write or AgentTools.CreateFile:
                    if (Held(input, tool) is { } whole && LinesOf(whole) is { } all)
                    {
                        Met(whole, [(1, all.Count)], WorkingSetUses.Edited);
                    }

                    break;
            }
        }

        /// <summary>
        /// A write of <paramref name="text"/>: where it stands once in the file as the step left it - each place it stands,
        /// where it replaced <paramref name="every"/> occurrence - else the lines its call <paramref name="named"/>, else
        /// matched to none and counted.
        /// </summary>
        private void Wrote(JsonObject input, string tool, string? text, bool every, (int First, int Last)? named)
        {
            if (Held(input, tool) is not { } path)
            {
                return;
            }

            var placed = Located(path, text, every);

            if (placed.Count == 0 && named is { } lines)
            {
                placed = [lines];
            }

            if (placed.Count == 0)
            {
                _unplaced++;
            }

            Met(path, placed, WorkingSetUses.Edited);
        }

        /// <summary>
        /// The lines <paramref name="text"/> stands on in the file at <paramref name="path"/> as the step left it, each
        /// line's trailing blanks aside: the one place it stands, or each place where <paramref name="every"/> - none where it
        /// holds nothing but blanks, or stands nowhere it may.
        /// </summary>
        private List<(int First, int Last)> Located(string path, string? text, bool every)
        {
            if (string.IsNullOrWhiteSpace(text) || LinesOf(path) is not { } lines)
            {
                return [];
            }

            var sought = Trimmed(CodeFileText.Lines(text.TrimEnd('\r', '\n')));
            var whole = Trimmed(lines);
            var places = new List<(int First, int Last)>();

            // A line feed it opens or closes with - a blank line, or one of blanks - says where it stands, and is no line it wrote.
            var lead = sought.Length - sought.TrimStart('\n').Length;
            var spans = sought.AsSpan().Trim('\n').Count('\n');

            for (var at = whole.IndexOf(sought, StringComparison.Ordinal); at >= 0; at = whole.IndexOf(sought, at + sought.Length, StringComparison.Ordinal))
            {
                var first = whole.AsSpan(0, at).Count('\n') + 1 + lead;
                places.Add((first, first + spans));
            }

            return every || places.Count == 1 ? places : [];
        }

        /// <summary>Where the lines an edit wrote stand once in the file as the step left it, by their hash; null where they stand nowhere once, or no hash names them.</summary>
        private (int First, int Last)? Hashed(string path, GraphWrittenLines wrote)
        {
            if (wrote.Hash is not { } hash || LinesOf(path) is not { } lines)
            {
                return null;
            }

            List<int> starts = [.. Enumerable.Range(1, Math.Max(0, lines.Count - wrote.Count + 1))
                .Where(start => CodeGraphIds.Digest(lines, start, start + wrote.Count - 1).StartsWith(hash, StringComparison.Ordinal))
                .Take(2)];

            return starts is [var start] ? (start, start + wrote.Count - 1) : null;
        }

        /// <summary>Lines joined by line feeds, each without its trailing spaces and tabs.</summary>
        private static string Trimmed(IEnumerable<string> lines) => string.Join('\n', lines.Select(line => line.TrimEnd(' ', '\t')));

        /// <summary>Each declaration of the file at <paramref name="path"/> whose own lines one of <paramref name="places"/> meets, touched once as <paramref name="use"/>: their ids.</summary>
        private List<string> Met(string path, IReadOnlyList<(int First, int Last)> places, string use)
        {
            if (places.Count == 0)
            {
                return [];
            }

            List<GraphCard> declared = [.. (view.OutlineFile(path) ?? []).Where(card => CodeNodeKinds.Declarations.Contains(card.Kind, StringComparer.Ordinal))];
            var met = new List<string>();

            foreach (var card in declared)
            {
                List<(int Start, int End)> inner = [.. declared
                    .Where(other => other.Start >= card.Start && other.End <= card.End && (other.Start, other.End) != (card.Start, card.End))
                    .Select(other => (other.Start, other.End))];

                if (places.Any(place => CodeOwnLines.Meet(place.First, place.Last, card.Start, card.End, inner)))
                {
                    Touched([card.Id], use, path);
                    met.Add(card.Id);
                }
            }

            return met;
        }

        private void Touched(IReadOnlyList<string> ids, string use, string? path = null)
        {
            foreach (var id in ids)
            {
                if (!_touched.TryGetValue(id, out var touch))
                {
                    touch = _touched[id] = new Touch(id, _touched.Count);
                }

                // The first file a read or a write met it in names it: a graph call names no file.
                touch.Path ??= path;
                touch.Uses.Add(use);
                touch.Times++;
            }
        }

        private WorkingSetNode Node(Touch touch)
        {
            List<string> uses = [.. WorkingSetUses.All.Where(touch.Uses.Contains)];

            if (!view.Holds(touch.Id))
            {
                return new WorkingSetNode(touch.Id, uses, touch.Times, Gone: true);
            }

            var card = (touch.Path is { } path ? view.Part(touch.Id, path) : null) ?? view.Card(touch.Id);
            return new WorkingSetNode(touch.Id, uses, touch.Times, card.Handle, card.Path, card.Start, card.End, card.Hash);
        }

        /// <summary>The file the view holds that a call's path names - Claude Code's <c>file_path</c>, the runtime's <c>path</c>, relative to the tree or under it - or null where it names none.</summary>
        private string? Held(JsonObject input, string tool)
        {
            if (Text(input, ClaudeCodeTools.Find(tool)?.Tool.Subject ?? "path") is not { } written
                || string.IsNullOrWhiteSpace(written))
            {
                return null;
            }

            string? relative;

            try
            {
                relative = AgentPolicy.Beneath(view.Tree, Path.Combine(view.Tree, written), pathComparison);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return null;
            }

            return relative is null ? null : view.HeldPath(relative);
        }

        /// <summary>The lines of the file at <paramref name="path"/> as the step left it, as an extract numbers them; null where it is gone or does not read.</summary>
        private IReadOnlyList<string>? LinesOf(string path)
        {
            if (!_lines.TryGetValue(path, out var lines))
            {
                try
                {
                    var decoded = CodeFileText.Decode(fileSystem.ReadAllBytes(Path.Combine(view.Tree, path.Replace('/', Path.DirectorySeparatorChar))));
                    lines = decoded.IsRead ? CodeFileText.Lines(decoded.Text) : null;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    lines = null;
                }

                _lines[path] = lines;
            }

            return lines;
        }

        private static JsonObject? Input(string input)
        {
            try
            {
                return JsonNode.Parse(input) as JsonObject;
            }
            catch (JsonException)
            {
                return null;
            }
        }

        private static string? Text(JsonObject input, string name) => input[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        private static int? Number(JsonObject input, string name) => input[name] is JsonValue value && value.TryGetValue<int>(out var number) && number >= 1 ? number : null;

        private static bool Flag(JsonObject input, string name) => input[name] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

        /// <summary>
        /// The first and last line a read's answer numbered - <c>12\t</c> as <c>view_file</c> and Claude Code number them, or
        /// <c>12→</c> - and, where they follow one another, the text of those lines; null where it numbered none.
        /// </summary>
        private static (int First, int Last, string? Text)? Numbered(string result)
        {
            var numbered = NumberedLine().Matches(result)
                .Select(match => (Ok: int.TryParse(match.Groups["line"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var line) && line >= 1, Line: line, Text: match.Groups["text"].Value.TrimEnd('\r')))
                .Where(one => one.Ok)
                .ToList();

            if (numbered.Count == 0)
            {
                return null;
            }

            var following = numbered.Select((one, at) => one.Line == numbered[0].Line + at).All(same => same);
            return (numbered[0].Line, Math.Max(numbered[0].Line, numbered.Max(one => one.Line)), following ? string.Join('\n', numbered.Select(one => one.Text)) : null);
        }
    }

    /// <summary>One node a step touched, as it is counted: how, how often, when first, and the first file a read or a write met it in.</summary>
    private sealed class Touch(string id, int order)
    {
        public string Id { get; } = id;

        public int Order { get; } = order;

        public string? Path { get; set; }

        public HashSet<string> Uses { get; } = new(StringComparer.Ordinal);

        public int Times { get; set; }
    }

    [GeneratedRegex(@"^[ \t]*(?<line>\d+)(?:\t|→)(?<text>[^\n]*)", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex NumberedLine();
}
