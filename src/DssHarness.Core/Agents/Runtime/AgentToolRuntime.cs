using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Schemas;
using DssHarness.Core.Web;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>One call of a tool, as the model made it.</summary>
/// <param name="Id">The call's id, as the provider named it.</param>
/// <param name="Name">The tool it calls.</param>
/// <param name="Input">Its input, as JSON.</param>
public sealed record AgentToolCallRequest(string Id, string Name, string Input);

/// <summary>What a call of the runtime answered, as the model is given it.</summary>
/// <param name="Id">The call's id.</param>
/// <param name="Ok">Whether it did what it was asked; false for a refusal and a failure, whose text says why.</param>
/// <param name="Text">What the model is given: the answer, masked, cut where it is too large for its event.</param>
/// <param name="File">The file the whole answer is kept in: a command's log, and any other answer where it was cut; null where there is none.</param>
public sealed record AgentToolCallResult(string Id, bool Ok, string Text, string? File);

/// <summary>What a run's calls of the runtime came to, for its result: each count, and what was left running.</summary>
public sealed class AgentToolTally
{
    private int _calls;
    private int _denials;
    private int _filesWritten;
    private int _webRequests;
    private readonly ConcurrentQueue<string> _uncontained = new();
    private readonly ConcurrentDictionary<string, byte> _files = new(StringComparer.Ordinal);

    /// <summary>How many calls were made.</summary>
    public int Calls => Volatile.Read(ref _calls);

    /// <summary>How many were refused.</summary>
    public int Denials => Volatile.Read(ref _denials);

    /// <summary>How many files were written or removed.</summary>
    public int FilesWritten => Volatile.Read(ref _filesWritten);

    /// <summary>How many requests of the web were asked, the refused among them.</summary>
    public int WebRequests => Volatile.Read(ref _webRequests);

    /// <summary>The files read and written, each once, in ordinal order.</summary>
    public IReadOnlyList<string> Files => [.. _files.Keys.Order(StringComparer.Ordinal)];

    /// <summary>What a command's container could not reach when it ended, one entry each.</summary>
    public IReadOnlyList<string> Uncontained => [.. _uncontained];

    /// <summary>Counts a call.</summary>
    internal void Called() => Interlocked.Increment(ref _calls);

    /// <summary>Counts a call refused.</summary>
    internal void Denied() => Interlocked.Increment(ref _denials);

    /// <summary>Counts <paramref name="count"/> files written or removed at <paramref name="file"/>, and keeps the path.</summary>
    /// <param name="file">The path written: a file, or a directory deleted or moved whole.</param>
    /// <param name="count">How many files that was.</param>
    internal void Wrote(string file, int count = 1)
    {
        Interlocked.Add(ref _filesWritten, count);
        _files.TryAdd(file, 0);
    }

    /// <summary>Keeps a path read.</summary>
    /// <param name="file">The file or directory read.</param>
    internal void Read(string file) => _files.TryAdd(file, 0);

    /// <summary>Counts a request of the web asked, allowed or refused.</summary>
    internal void Asked() => Interlocked.Increment(ref _webRequests);

    /// <summary>Keeps what a command's container could not reach when it ended.</summary>
    /// <param name="what">The command, and what was left.</param>
    internal void LeftRunning(string what) => _uncontained.Enqueue(what);
}

/// <summary>
/// The harness's own tools, run for one agent's run on an API client (part 03, "The tool runtime"): each call decided
/// before it runs - its tool among those the step is given, its input held to the tool's schema, each path held to the
/// agent's roots (<see cref="ConfinedPaths"/>) and asked of the policy, a command and an address asked of it too - then
/// run, its answer masked and cut to its event's bound, the whole kept beside the stream, and recorded: each denial and
/// each request of the web by <see cref="AgentCallRecords"/>, each call's end a <see cref="ToolCallFinished"/>. The
/// client's loop emits the call's start, as its provider's stream shows it.
/// </summary>
/// <remarks>
/// A replace or an insert needs its file viewed first, by its content: a view records the file's hash, the runtime's own
/// write records the new one, and an edit of a file whose hash moved since is refused, nothing written. In a turn, calls
/// that read run together, at most <see cref="MostTogether"/> at once; a call that writes or runs stands alone between
/// them; answers are given in the order the calls were made.
/// </remarks>
public sealed partial class AgentToolRuntime : IDisposable
{
    /// <summary>The most calls that read run at once in a turn.</summary>
    public const int MostTogether = 4;

    /// <summary>The rule a call of a tool the step was not given is refused by.</summary>
    public const string ToolsRule = "the step's tools";

    /// <summary>The rule a directory deleted or moved whole is refused by where it holds more than <see cref="AgentTools.MostDeletedEntries"/> entries.</summary>
    public const string MostEntriesRule = "the most entries";

    /// <summary>What a replace or an insert is refused with where its file changed since it was viewed.</summary>
    public const string ChangedSinceViewed = "file changed since you last viewed it; view it again";

    /// <summary>What a replace or an insert is refused with where its file was never viewed.</summary>
    public const string NeverViewed = "view the file first: an edit needs the file as you last saw it";

    private readonly AgentRun _run;
    private readonly IAgentEventSink _events;
    private readonly AgentToolHost _host;
    private readonly AgentPolicy _policy;
    private readonly ConfinedPaths _paths;
    private readonly AgentCallRecords _records;
    private readonly IReadOnlyDictionary<string, AgentToolDefinition> _tools;
    private readonly IReadOnlyDictionary<string, JsonNode> _schemas;
    private readonly ConcurrentDictionary<string, string> _viewed;
    private readonly Web.Search.WebSearchStep? _webSearch;

    /// <summary>The runtime of <paramref name="run"/>, its ground decided once.</summary>
    internal AgentToolRuntime(AgentRun run, IAgentEventSink events, AgentToolHost host)
    {
        _run = run;
        _events = events;
        _host = host;

        var ground = run.Ground ?? throw new ArgumentException("the run was given no policy to decide its calls by, and no call runs undecided", nameof(run));

        _policy = new AgentPolicy(host.FileSystem, host.Platform, ground);
        _paths = new ConfinedPaths(host.FileSystem, host.Platform, ground);
        _records = new AgentCallRecords(host.FileSystem, run, host.Clock, events);
        Tools = AgentTools.Offered(ground, run.Commands, run.WebSearch);
        _tools = Tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
        _schemas = Tools.ToDictionary(tool => tool.Name, tool => JsonNode.Parse(tool.Schema)!, StringComparer.Ordinal);
        _viewed = new ConcurrentDictionary<string, string>(StringComparer.FromComparison(host.Platform.PathComparison));

        // The step's search, where it has a source to search: its sources' endpoints held to the Web(...) rules, an index's
        // fill to the run's, its results to every rule of the web, webDomains among them; the indexes of its sitemap sources
        // kept with the main checkout's.
        _webSearch = _tools.ContainsKey(AgentTools.WebSearch) && run.WebSearch is { } search
            ? new Web.Search.WebSearchStep(
                search.Sources,
                search.ResultsPerSource,
                search.MaxPerStep,
                run.Workspace.RunDirectory,
                new Repository.HarnessLayout(run.Workspace.MainCheckout, run.Workspace.MainCheckout),
                run.Mask,
                search.Keys,
                new Web.Search.WebSearchRules(_policy.WebSource, _policy.WebFill, _policy.Web),
                new SearchRecords(this))
            : null;

        if (_webSearch is not null && host.Search is null)
        {
            throw new ArgumentException("the run's step has sources to search, and its host was given no web search to search them with", nameof(host));
        }
    }

    /// <summary>The tools this run is offered, by name.</summary>
    public IReadOnlyList<AgentToolDefinition> Tools { get; }

    /// <summary>What the run's calls came to so far.</summary>
    public AgentToolTally Tally { get; } = new();

    /// <summary>
    /// Runs the calls of one turn: those that read together, at most <see cref="MostTogether"/> at once, each that writes
    /// or runs alone, after every call before it and before every call after; the answers in the order of the calls.
    /// </summary>
    /// <param name="calls">The turn's calls, in the order the model made them.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    public async Task<IReadOnlyList<AgentToolCallResult>> RunTurnAsync(IReadOnlyList<AgentToolCallRequest> calls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(calls);

        var results = new AgentToolCallResult[calls.Count];
        var reads = new List<int>();

        async Task ReadsAsync()
        {
            foreach (var chunk in reads.Chunk(MostTogether))
            {
                var answered = await Task.WhenAll(chunk.Select(at => RunAsync(calls[at], cancellationToken))).ConfigureAwait(false);

                for (var index = 0; index < chunk.Length; index++)
                {
                    results[chunk[index]] = answered[index];
                }
            }

            reads.Clear();
        }

        for (var at = 0; at < calls.Count; at++)
        {
            if (_tools.TryGetValue(calls[at].Name, out var tool) && tool.Barrier)
            {
                await ReadsAsync().ConfigureAwait(false);
                results[at] = await RunAsync(calls[at], cancellationToken).ConfigureAwait(false);
            }
            else
            {
                reads.Add(at);
            }
        }

        await ReadsAsync().ConfigureAwait(false);

        return results;
    }

    /// <summary>Runs one call, decided first, and records what it did.</summary>
    /// <param name="call">The call.</param>
    /// <param name="cancellationToken">The hard stop.</param>
    public async Task<AgentToolCallResult> RunAsync(AgentToolCallRequest call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);

        Tally.Called();
        var started = _host.Clock.GetTimestamp();
        Answer answer;

        try
        {
            answer = await AnswerAsync(call, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && Faulted(ex))
        {
            answer = Answer.Failed($"{call.Name} failed: {ex.Message.TrimEnd('.')}");
        }

        return await FinishedAsync(call, answer, started).ConfigureAwait(false);
    }

    /// <summary>
    /// Answers a call that is not run, refused by <paramref name="denied"/> - a call made in a handoff turn, which no tool
    /// runs in - recorded as every denial is.
    /// </summary>
    /// <param name="call">The call.</param>
    /// <param name="denied">Why it is not run.</param>
    public async Task<AgentToolCallResult> RefuseAsync(AgentToolCallRequest call, AgentPolicyAnswer.Denied denied)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(denied);

        Tally.Called();
        var started = _host.Clock.GetTimestamp();

        return await FinishedAsync(call, Answer.Refused(call.Name, denied), started).ConfigureAwait(false);
    }

    /// <summary>Closes the run's records.</summary>
    public void Dispose() => _records.Dispose();

    /// <summary>A call's answer recorded: a denial as every denial is, its text masked and cut, and its end emitted.</summary>
    private async Task<AgentToolCallResult> FinishedAsync(AgentToolCallRequest call, Answer answer, long started)
    {
        if (answer.Denial is { } denial)
        {
            Tally.Denied();
            await _records.DeniedAsync(call.Name, denial.Subject, denial.Denied).ConfigureAwait(false);
        }

        var (shown, file) = answer.File is { } kept
            ? (_run.Mask.Redact(answer.Text), kept)
            : _records.Kept(call.Id, answer.Text);

        await _events.EmitAsync(new ToolCallFinished(
            call.Id,
            answer.Ok,
            _host.Clock.GetElapsedTime(started),
            _run.Mask.Redact(call.Input),
            shown,
            file)).ConfigureAwait(false);

        return new AgentToolCallResult(call.Id, answer.Ok, shown, file);
    }

    /// <summary>The call's answer: refused where its tool is not given or its input does not read, else its tool's.</summary>
    private async Task<Answer> AnswerAsync(AgentToolCallRequest call, CancellationToken cancellationToken)
    {
        if (!_tools.TryGetValue(call.Name, out var tool))
        {
            return Answer.Refused(call.Name, new AgentPolicyAnswer.Denied(
                ToolsRule,
                AgentPolicySource.Builtin,
                $"'{call.Name}' is no tool this step is given: it is given {string.Join(", ", Tools.Select(given => given.Name))}"));
        }

        JsonObject input;

        try
        {
            input = JsonNode.Parse(call.Input) as JsonObject ?? throw new JsonException("it is no JSON object");
            ReadWhole(input);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Answer.Failed($"{call.Name}'s input does not read as JSON ({ex.Message.TrimEnd('.')}); {Usage(tool)}");
        }

        if (JsonSchemaSubset.Problems(_schemas[tool.Name], input) is [_, ..] problems)
        {
            return Answer.Failed($"{call.Name}'s input does not hold: {string.Join("; ", problems)}; {Usage(tool)}");
        }

        return tool.Name switch
        {
            AgentTools.ViewFile => await ViewAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.FindFiles => await FindAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.SearchFiles => await SearchAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.CreateFile => await CreateAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.ReplaceInFile => await ReplaceAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.InsertInFile => await InsertAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.CreateDirectory => CreateDirectory(input),
            AgentTools.DeleteFile => DeleteFile(input),
            AgentTools.DeleteDirectory => DeleteDirectory(input),
            AgentTools.MovePath => MovePath(input),
            AgentTools.RunCommand => await RunCommandAsync(call.Id, input, cancellationToken).ConfigureAwait(false),
            AgentTools.WebFetch => await FetchAsync(input, cancellationToken).ConfigureAwait(false),
            AgentTools.WebSearch => await WebSearchAsync(input, cancellationToken).ConfigureAwait(false),
            _ => throw new InvalidOperationException($"the runtime offers '{tool.Name}' and runs no such tool"),
        };
    }

    /// <summary>
    /// Reads every string <paramref name="node"/> holds, so a text no string holds - half of a surrogate pair, which the
    /// parser lets by - refuses the input here rather than meeting a tool.
    /// </summary>
    private static void ReadWhole(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject members:
                foreach (var (_, value) in members)
                {
                    ReadWhole(value);
                }

                break;
            case JsonArray items:
                foreach (var item in items)
                {
                    ReadWhole(item);
                }

                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                _ = value.GetValue<string>();
                break;
        }
    }

    /// <summary>How a tool is called, said in a refused call's answer: each member it takes, the required ones marked.</summary>
    private static string Usage(AgentToolDefinition tool)
    {
        var schema = JsonNode.Parse(tool.Schema)!.AsObject();
        var required = schema["required"]!.AsArray().Select(name => name!.GetValue<string>()).ToHashSet(StringComparer.Ordinal);

        return $"{tool.Name} takes " + string.Join(", ", schema["properties"]!.AsObject().Select(member =>
            $"{member.Key}{(required.Contains(member.Key) ? string.Empty : "?")} ({member.Value!["type"]!.GetValue<string>()})"));
    }

    /// <summary>Whether a tool's fault is told the model as its answer: a file system's, a process's, a bound's and git's, never a defect of this tool's own.</summary>
    private static bool Faulted(Exception ex)
        => ex is IOException or UnauthorizedAccessException or HarnessException or ProgramStartException or ArgumentException or NotSupportedException or System.Text.DecoderFallbackException or System.Text.EncoderFallbackException;

    /// <summary>A place a read reaches, decided: held to the roots, then asked of the policy.</summary>
    private Decided ReadPlace(string path) => Decide(_paths.Read(path), full => _policy.Read(full));

    /// <summary>A directory a search reaches, decided: held to the roots, then asked of the policy as a search.</summary>
    private Decided SearchPlace(string path) => Decide(_paths.Read(path), full => _policy.Search(full));

    /// <summary>A place a write reaches, decided: held to the writable roots and through no link, then asked of the policy.</summary>
    private Decided WritePlace(string path) => Decide(_paths.Write(path), full => _policy.Write(full));

    /// <summary>A place decided: refused where it is not confined to the roots, else as <paramref name="policy"/> answers it.</summary>
    private static Decided Decide(ConfinedPath confined, Func<string, AgentPolicyAnswer> policy)
        => confined.Full is not { } full ? new Decided(null, confined.Denied)
            : policy(full) is AgentPolicyAnswer.Denied denied ? new Decided(null, denied)
            : new Decided(full, null);

    /// <summary>A member's text, or null where the input leaves it out.</summary>
    private static string? Text(JsonObject input, string name) => input[name]?.GetValue<string>();

    /// <summary>A member's whole number, or null where the input leaves it out; one past what an int holds is refused by its caller's bound.</summary>
    private static long? Number(JsonObject input, string name) => input[name] is { } node ? (long)node.GetValue<double>() : null;

    /// <summary>A member's flag, false where the input leaves it out.</summary>
    private static bool Flag(JsonObject input, string name) => input[name]?.GetValue<bool>() ?? false;

    /// <summary>A count said as a person reads it.</summary>
    private static string Count(long count) => count.ToString(CultureInfo.InvariantCulture);

    /// <summary>The hash of a file's bytes, as an edit compares it.</summary>
    private static string Hash(ReadOnlySpan<byte> bytes) => FileContentHash.Of(bytes);

    /// <summary>A place decided: the place, or why it may not be reached.</summary>
    private sealed record Decided(string? Full, AgentPolicyAnswer.Denied? Denied);

    /// <summary>What a call answered, before it is recorded.</summary>
    private sealed record Answer(bool Ok, string Text, string? File, (string Subject, AgentPolicyAnswer.Denied Denied)? Denial)
    {
        /// <summary>A call that did what it was asked, saying <paramref name="text"/>.</summary>
        public static Answer Done(string text) => new(true, text, null, null);

        /// <summary>A call that failed, saying why.</summary>
        public static Answer Failed(string text) => new(false, text, null, null);

        /// <summary>A call refused by <paramref name="denied"/>, of <paramref name="subject"/>: recorded as every denial is.</summary>
        public static Answer Refused(string subject, AgentPolicyAnswer.Denied denied) => new(false, denied.Reason, null, (subject, denied));

        /// <summary>An answer whose whole is kept in <paramref name="file"/> already: a command's log.</summary>
        public static Answer Kept(bool ok, string text, string file) => new(ok, text, file, null);
    }
}

/// <summary>
/// What the harness's runtime runs on: the file system, the platform, git, the phase runner, the fetch, the clock and the
/// tool's environment - one for the process, from which each run of an API client is to open a runtime; none opens one yet.
/// </summary>
/// <param name="fileSystem">Reads and writes the agent's files.</param>
/// <param name="platform">The platform, whose paths and names compare its way.</param>
/// <param name="git">Lists a work tree's files.</param>
/// <param name="phases">Runs a command an agent runs as a phase, contained.</param>
/// <param name="sessions">Where a contained command's session is recorded while it runs.</param>
/// <param name="fetch">The harness's own fetch.</param>
/// <param name="clock">Times each call, and a search's pattern on each file.</param>
/// <param name="environment">The tool's own environment, which a command would otherwise inherit; <see cref="AgentChildEnvironment.Own"/> where none is given.</param>
/// <param name="search">The harness's own web search; none for a host whose runs search no source.</param>
public sealed class AgentToolHost(
    IFileSystem fileSystem,
    IHostPlatform platform,
    IGitClient git,
    PhaseRunner phases,
    ChildSessions sessions,
    HarnessFetch fetch,
    TimeProvider clock,
    Func<IReadOnlyDictionary<string, string>>? environment = null,
    Web.Search.HarnessWebSearch? search = null)
{
    /// <summary>Reads and writes the agent's files.</summary>
    internal IFileSystem FileSystem { get; } = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>The platform, whose paths and names compare its way.</summary>
    internal IHostPlatform Platform { get; } = platform ?? throw new ArgumentNullException(nameof(platform));

    /// <summary>Lists and searches a directory's files.</summary>
    internal AgentFiles Files { get; } = new(git ?? throw new ArgumentNullException(nameof(git)), fileSystem, clock);

    /// <summary>Runs a command as a phase.</summary>
    internal PhaseRunner Phases { get; } = phases ?? throw new ArgumentNullException(nameof(phases));

    /// <summary>Where a contained command's session is recorded.</summary>
    internal ChildSessions Sessions { get; } = sessions ?? throw new ArgumentNullException(nameof(sessions));

    /// <summary>The harness's own fetch.</summary>
    internal HarnessFetch Fetch { get; } = fetch ?? throw new ArgumentNullException(nameof(fetch));

    /// <summary>Times each call.</summary>
    internal TimeProvider Clock { get; } = clock ?? throw new ArgumentNullException(nameof(clock));

    /// <summary>The tool's own environment, read as each command starts.</summary>
    internal Func<IReadOnlyDictionary<string, string>> Environment { get; } = environment ?? AgentChildEnvironment.Own;

    /// <summary>The harness's own web search; none for a host whose runs search no source.</summary>
    internal Web.Search.HarnessWebSearch? Search { get; } = search;

    /// <summary>Opens the runtime of <paramref name="run"/>: its tools, its policy and its records.</summary>
    /// <param name="run">The run, whose ground decides every call.</param>
    /// <param name="events">Where its calls' ends, denials and requests of the web are emitted.</param>
    /// <exception cref="ArgumentException">The run was given no ground: no call runs undecided.</exception>
    public AgentToolRuntime Open(AgentRun run, IAgentEventSink events)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(events);

        return new AgentToolRuntime(run, events, this);
    }
}
