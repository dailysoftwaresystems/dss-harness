using System.Globalization;
using System.Text;
using System.Text.Json;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Output;

namespace DssHarness.Core.Agents;

/// <summary>
/// What a step's calls leave beside its stored stream, on every client: each denial a line of <c>denials.jsonl</c> and a
/// <see cref="PermissionDenied"/>; each request of the web a line of <c>web.jsonl</c> when it is decided, before anything
/// leaves the machine, another when it is answered, and a <see cref="WebRequested"/>; each web search source's part a line
/// of it too, whether or not it sent a request, and a <see cref="WebRequested"/> - a <see cref="Notice"/> where its
/// answer could not be read; a result too large for its event
/// kept whole in <c>tool-results/</c>, its event carrying its first quarter and its last three quarters of
/// <see cref="ResultCap"/>. The one owner of those records, so a denial, a page and a long result read alike whoever ran
/// the call: Claude Code's client, or the harness's own tool runtime.
/// </summary>
/// <remarks>Every text is masked before it is written or emitted. Each file is opened when its first line is written, and
/// held until disposed: one writer of each, as <see cref="AppendLog"/> holds it.</remarks>
public sealed class AgentCallRecords : IDisposable
{
    /// <summary>Each call refused, one line each.</summary>
    public const string DenialsFileName = "denials.jsonl";

    /// <summary>Each request of the web, when it is decided and when it is answered, and each web search source's part, one line each.</summary>
    public const string WebFileName = "web.jsonl";

    /// <summary>The folder, in the step's directory, a call's result too large for its event is kept in whole.</summary>
    public const string ToolResultsDirectoryName = "tool-results";

    /// <summary>
    /// The largest result a call's event carries whole, in characters; a larger one is kept whole in a file beside the
    /// stream, and its event carries its first quarter and its last three quarters of this, with what was left out between.
    /// </summary>
    public const int ResultCap = 32 * 1024;

    /// <summary>How much of a result too large for its event is kept from its start: a quarter of <see cref="ResultCap"/>.</summary>
    public const int ResultHead = ResultCap / 4;

    /// <summary>The longest a call's id is in the name of its result's file; what is past it is cut.</summary>
    private const int MostIdInName = 96;

    private readonly IFileSystem _fileSystem;
    private readonly AgentRun _run;
    private readonly TimeProvider _clock;
    private readonly IAgentEventSink _events;
    private readonly Lazy<AppendLog> _denials;
    private readonly Lazy<AppendLog> _web;

    /// <summary>Opens the records of <paramref name="run"/>'s calls in its step's directory.</summary>
    /// <param name="fileSystem">Where the records are kept.</param>
    /// <param name="run">The run whose calls they are: its step's directory, and its mask.</param>
    /// <param name="clock">What stamps each line.</param>
    /// <param name="events">Where each denial and each request of the web is emitted.</param>
    public AgentCallRecords(IFileSystem fileSystem, AgentRun run, TimeProvider clock, IAgentEventSink events)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(events);

        _fileSystem = fileSystem;
        _run = run;
        _clock = clock;
        _events = events;

        _denials = new Lazy<AppendLog>(() => Opened(DenialsFileName), LazyThreadSafetyMode.ExecutionAndPublication);
        _web = new Lazy<AppendLog>(() => Opened(WebFileName), LazyThreadSafetyMode.ExecutionAndPublication);
    }

    /// <summary>The file a call's whole result is kept in, beside the stream: its id, every character no file name holds on every system made <c>_</c>.</summary>
    /// <param name="stepDirectory">The step's directory.</param>
    /// <param name="callId">The call's id, as its client named it.</param>
    public static string ResultFile(string stepDirectory, string callId)
    {
        ArgumentNullException.ThrowIfNull(stepDirectory);
        ArgumentNullException.ThrowIfNull(callId);

        var name = new string([.. callId.Take(MostIdInName).Select(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' ? character : '_')]);

        return Path.Combine(stepDirectory, ToolResultsDirectoryName, (name.Length == 0 ? "call" : name) + ".txt");
    }

    /// <summary>
    /// <paramref name="text"/> as a call's event carries it: whole where it is no longer than <see cref="ResultCap"/>;
    /// otherwise its first <see cref="ResultHead"/> characters and its last of the rest of the cap, never a pair of
    /// surrogates cut, with what was left out between - and the whole kept in <paramref name="file"/>.
    /// </summary>
    /// <param name="text">The result, masked.</param>
    /// <param name="file">The file the whole is kept in, where it is cut.</param>
    public static string Cut(string text, string file)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(file);

        if (text.Length <= ResultCap)
        {
            return text;
        }

        var head = TextCut.Head(text, ResultHead);
        var tail = TextCut.Tail(text, ResultCap - ResultHead);

        return Told(head, text.Length - head.Length - tail.Length, file, tail);
    }

    /// <summary>
    /// What a call's event carries of a result read a line at a time from <paramref name="file"/>, which holds it whole: as
    /// <see cref="Cut(string, string)"/> carries it, read once and never held whole, so a command's output of any size costs the cap.
    /// </summary>
    /// <param name="lines">The result's lines, masked, in order.</param>
    /// <param name="file">The file that holds the whole.</param>
    public static string Cut(IEnumerable<string> lines, string file)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(file);

        // The head holds the cap, which the whole fits in where it is given whole; the tail holds the end of everything.
        var head = new StringBuilder();
        var tail = new Processes.StreamTail(ResultCap - ResultHead);
        long total = 0;
        var first = true;

        foreach (var line in lines)
        {
            var text = first ? line : "\n" + line;
            first = false;
            total += text.Length;
            tail.Add(text);
            head.Append(text.AsSpan(0, Math.Min(text.Length, ResultCap - head.Length)));
        }

        if (total <= ResultCap)
        {
            return head.ToString();
        }

        var kept = TextCut.Head(head.ToString(), ResultHead);
        var end = tail.ToString();

        return Told(kept, (int)Math.Min(int.MaxValue, total - kept.Length - end.Length), file, end);
    }

    /// <summary>
    /// <paramref name="result"/> as its call's event carries it, masked, and where it is too large for its event kept whole
    /// in its call's file (<see cref="ResultFile"/>): what the event carries, and the file where one was written.
    /// </summary>
    /// <param name="callId">The call's id.</param>
    /// <param name="result">The result, as the call answered it.</param>
    public (string Shown, string? File) Kept(string callId, string result)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(result);

        var masked = _run.Mask.Redact(result);

        if (masked.Length <= ResultCap)
        {
            return (masked, null);
        }

        var file = ResultFile(_run.StepDirectory, callId);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
        _fileSystem.WriteAllTextAtomic(file, masked);

        return (Cut(masked, file), file);
    }

    /// <summary>Records a call refused: a line of <c>denials.jsonl</c>, then a <see cref="PermissionDenied"/>, each masked.</summary>
    /// <param name="tool">The tool, by the name its client gives it.</param>
    /// <param name="subject">What it was asked of: a path, a command, an address.</param>
    /// <param name="denied">The denial, with its rule and its reason.</param>
    public async Task DeniedAsync(string tool, string subject, AgentPolicyAnswer.Denied denied)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(denied);

        var masked = _run.Mask.RedactUnescaped(subject);
        var reason = _run.Mask.Redact(denied.Reason);

        _denials.Value.Append(JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow(),
            tool,
            subject = masked,
            rule = denied.Rule,
            source = denied.Source.ToString(),
            reason,
        }));

        await _events.EmitAsync(new PermissionDenied(tool, masked, denied.Rule, SourceOf(denied.Source), reason)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records a request of the web as it is decided, before anything leaves the machine: a line of <c>web.jsonl</c>, and
    /// where it was refused a <see cref="WebRequested"/> that says so - an allowed one's is emitted when it is answered.
    /// </summary>
    /// <param name="tool">The tool that asks it.</param>
    /// <param name="by">What makes it: the harness's fetch, or a search source by its name.</param>
    /// <param name="address">The address, or the query.</param>
    /// <param name="answer">The policy's answer.</param>
    public async Task WebDecidedAsync(string tool, string by, string address, AgentPolicyAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(address);
        ArgumentNullException.ThrowIfNull(answer);

        var masked = _run.Mask.RedactUnescaped(address);
        var denied = answer.Refusal();

        _web.Value.Append(JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow(),
            line = "decided",
            tool,
            by,
            address = masked,
            allowed = denied is null,
            rule = denied?.Rule,
            source = denied?.Source.ToString(),
            reason = denied is null ? null : _run.Mask.Redact(denied.Reason),
        }));

        if (denied is not null)
        {
            await _events.EmitAsync(new WebRequested(masked, AgentToolRunner.Harness, Allowed: false)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Records what an allowed request of the web was answered: a line of <c>web.jsonl</c> with its status, its type and
    /// its size, or why it failed, and a <see cref="WebRequested"/>.
    /// </summary>
    /// <param name="tool">The tool that asked it.</param>
    /// <param name="by">What made it: the harness's fetch, or a search source by its name.</param>
    /// <param name="address">The address, as it was decided.</param>
    /// <param name="status">The status the server answered; null where none was read.</param>
    /// <param name="type">The type of what came back; null where none was read.</param>
    /// <param name="bytes">How many bytes came back; null where none were read.</param>
    /// <param name="failed">Why it failed, where it did.</param>
    public async Task WebAnsweredAsync(string tool, string by, string address, int? status, string? type, long? bytes, string? failed)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(by);
        ArgumentNullException.ThrowIfNull(address);

        var masked = _run.Mask.RedactUnescaped(address);

        _web.Value.Append(JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow(),
            line = failed is null ? "answered" : "failed",
            tool,
            by,
            address = masked,
            status,
            type,
            bytes,
            failed = failed is null ? null : _run.Mask.RedactUnescaped(failed),
        }));

        await _events.EmitAsync(new WebRequested(masked, AgentToolRunner.Harness, Allowed: true, status, bytes)).ConfigureAwait(false);
    }

    /// <summary>
    /// Records one source's part of a search: a line of <c>web.jsonl</c> - the source, its kind and version, the query,
    /// what it came to and how many results it gave, how many the step's rules left out, whether the run's record
    /// answered it, and why it was asked without its key where it was - and a <see cref="WebRequested"/>, so each source
    /// asked is an event, whether or not it sent a request; a source whose answer could not be read is a
    /// <see cref="Notice"/> too, naming its kind's version.
    /// </summary>
    /// <param name="part">The part.</param>
    public async Task WebSearchedAsync(Web.Search.WebSearchPart part)
    {
        ArgumentNullException.ThrowIfNull(part);

        var query = _run.Mask.Redact(part.Query);
        var answer = part.Answer;

        _web.Value.Append(JsonSerializer.Serialize(new
        {
            at = _clock.GetUtcNow(),
            line = "searched",
            tool = Runtime.AgentTools.WebSearch,
            by = part.Source,
            kind = part.Kind,
            version = part.Version,
            query,
            outcome = answer.Outcome.ToString(),
            results = answer.Results.Count,
            withheld = part.Withheld,
            cached = part.Cached,
            keyMissing = part.KeyMissing is null ? null : _run.Mask.Redact(part.KeyMissing),
            why = answer.Why is null ? null : _run.Mask.Redact(answer.Why),
        }));

        await _events.EmitAsync(new WebRequested(query, AgentToolRunner.Harness, Allowed: answer.Outcome != Web.Search.WebSearchOutcome.Refused)).ConfigureAwait(false);

        if (answer.Outcome == Web.Search.WebSearchOutcome.Changed)
        {
            await _events.EmitAsync(new Notice(_run.Mask.Redact($"web-search source '{part.Source}' answered what its kind '{part.Kind}' (version {part.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)}) could not read: {answer.Why}"))).ConfigureAwait(false);
        }
    }

    /// <summary>Closes the records; each ends at its last whole line.</summary>
    public void Dispose()
    {
        if (_denials.IsValueCreated)
        {
            _denials.Value.Dispose();
        }

        if (_web.IsValueCreated)
        {
            _web.Value.Dispose();
        }
    }

    /// <summary>The step's file <paramref name="name"/>, opened for its lines.</summary>
    private AppendLog Opened(string name)
    {
        _fileSystem.CreateDirectory(_run.StepDirectory);
        return AppendLog.Open(_fileSystem, Path.Combine(_run.StepDirectory, name));
    }

    /// <summary>Where a denial's rule comes from, as its event names it.</summary>
    /// <param name="source">Where the policy says the rule comes from.</param>
    public static AgentPermissionSource SourceOf(AgentPolicySource source)
        => source switch
        {
            AgentPolicySource.Config => AgentPermissionSource.Config,
            AgentPolicySource.Agent => AgentPermissionSource.Agent,
            AgentPolicySource.ClaudeCode => AgentPermissionSource.ClaudeCode,
            _ => AgentPermissionSource.Builtin,
        };

    /// <summary>The event's text of a result cut: its head, what was left out and where the whole is, its tail.</summary>
    private static string Told(string head, long leftOut, string file, string tail)
        => string.Create(CultureInfo.InvariantCulture, $"{head}\n[{leftOut} characters left out: the whole result is '{file}']\n{tail}");
}
