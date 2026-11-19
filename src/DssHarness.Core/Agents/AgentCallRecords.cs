using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Agents;

/// <summary>
/// What a step's calls leave beside its stored stream, on every client: each denial a line of <c>denials.jsonl</c> and a
/// <see cref="PermissionDenied"/>; each request of the web a line of <c>web.jsonl</c> when it is decided, before anything
/// leaves the machine, another when it is answered, and a <see cref="WebRequested"/>; each web search source's part a line
/// of it too, whether or not it sent a request, and a <see cref="WebRequested"/> - a <see cref="Notice"/> where its
/// answer could not be read; and a result longer than one page of a tool's answer kept whole in <c>tool-results/</c>, its
/// call answered with the result's first page, which says how <c>read_result</c> gives the rest
/// (<see cref="Runtime.KeptResults"/>). The one owner of those records, so a denial, a page and a long result read alike
/// whoever ran the call: Claude Code's client, or the harness's own tool runtime.
/// </summary>
/// <remarks>Every text is masked before it is written or emitted. Each file is opened when its first line is written, and
/// held until disposed: one writer of each, as <see cref="AppendLog"/> holds it.</remarks>
public sealed class AgentCallRecords : IDisposable
{
    /// <summary>Each call refused, one line each.</summary>
    public const string DenialsFileName = "denials.jsonl";

    /// <summary>Each request of the web, when it is decided and when it is answered, and each web search source's part, one line each.</summary>
    public const string WebFileName = "web.jsonl";

    /// <summary>The folder, in the step's directory, a call's result longer than a page is kept in whole.</summary>
    public const string ToolResultsDirectoryName = "tool-results";

    /// <summary>
    /// The cap a call's answer was once cut at, its head and tail kept. No call's answer is cut by it: one longer than a
    /// page is kept whole and answered with its first page (<see cref="Kept"/>).
    /// </summary>
    public const int ResultCap = 32 * 1024;

    /// <summary>What a cut answer once kept of its start, a quarter of <see cref="ResultCap"/>: no answer is cut now.</summary>
    public const int ResultHead = ResultCap / 4;

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

    /// <summary>
    /// The file a call's whole result is kept in, beside the stream: named by the call's whole id, as the key its pages are
    /// kept under spells it (<see cref="PageKey.OfCall"/>), so two ids however long are two files - an id of no character
    /// among them, which is an id as any other.
    /// </summary>
    /// <param name="stepDirectory">The step's directory.</param>
    /// <param name="callId">The call's id, as its client named it.</param>
    public static string ResultFile(string stepDirectory, string callId)
    {
        ArgumentNullException.ThrowIfNull(stepDirectory);
        ArgumentNullException.ThrowIfNull(callId);

        return Path.Combine(stepDirectory, ToolResultsDirectoryName, PageKey.OfCall(callId) + ".txt");
    }

    /// <summary>
    /// A call's id as the name of what the step keeps of the call apart from its result - a measure call's folder: the id
    /// itself where every character of it is a letter, a digit, <c>-</c> or <c>_</c> and it is shorter than a digest's name
    /// and not spelt as one, so a provider's id reads as itself; else the name its digest spells (<see cref="PageKey.OfCall"/>),
    /// so two ids are never one name, however long or however spelt; <c>call</c> for an id that holds none.
    /// </summary>
    /// <param name="callId">The call's id, as its client named it.</param>
    public static string NameOf(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        if (callId.Length == 0)
        {
            return "call";
        }

        var digest = PageKey.OfCall(callId);

        return callId.Length < digest.Length
            && !callId.StartsWith(PageKey.CallPrefix, StringComparison.Ordinal)
            && callId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                ? callId
                : digest;
    }

    /// <summary>
    /// <paramref name="result"/> as its call is answered and its event carries it, masked: whole where it is one page of a
    /// tool's answer to a model (<see cref="Runtime.KeptResults.Room"/>); else kept whole in its call's file
    /// (<see cref="ResultFile"/>) and given as its first page, whose header says how many pages it holds and how
    /// <c>read_result</c> gives the next, and names the file - nothing of it cut. What is given, and the file where one was
    /// written.
    /// </summary>
    /// <param name="callId">The call's id.</param>
    /// <param name="result">The result, as the call answered it.</param>
    public (string Shown, string? File) Kept(string callId, string result)
    {
        ArgumentNullException.ThrowIfNull(callId);
        ArgumentNullException.ThrowIfNull(result);

        var masked = _run.Mask.Redact(result);

        if (Runtime.KeptResults.OnePage(masked))
        {
            return (masked, null);
        }

        // The bytes the file holds are the bytes page 1 is laid from: the pages read_result gives later are laid from
        // the file, the same bytes, so its page 2 goes on where this page 1 ends.
        var bytes = Encoding.UTF8.GetBytes(masked);
        var file = ResultFile(_run.StepDirectory, callId);

        if (Runtime.KeptResults.FirstPageOf(bytes, callId, file) is not { } first)
        {
            return (masked, null);
        }

        _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
        _fileSystem.WriteAtomic(file, stream => stream.Write(bytes));

        return (first, file);
    }

    /// <summary>
    /// A writer of the call <paramref name="callId"/>'s answer a line at a time, each line masked: held while the answer
    /// is one page of a tool's answer, and from the line that passes it written on into the call's file
    /// (<see cref="ResultFile"/>), so an answer of any length costs a page of memory (<see cref="ResultLines"/>).
    /// </summary>
    /// <param name="callId">The call's id.</param>
    public ResultLines Lines(string callId)
    {
        ArgumentNullException.ThrowIfNull(callId);

        return new ResultLines(_fileSystem, _run.Mask, ResultFile(_run.StepDirectory, callId));
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
}

/// <summary>
/// A call's answer written a line at a time (<see cref="AgentCallRecords.Lines"/>), each line masked as it is written: the
/// lines held while they fit one page of a tool's answer to a model (<see cref="Runtime.KeptResults.Room"/>), and from the
/// line that passes it every line written on into the call's file, the lines held before it first - so an answer of any
/// length, a search of a million hits among them, is never held whole. The file is written whole and put in place when
/// the answer ends (<see cref="IFileSystem.WriteAtomic"/>), by a writer of its own that takes the lines as they come, a
/// producer that outruns it waiting; an answer abandoned before its end leaves no file. Once every line is written, the
/// answer is the lines whole where they fit a page (<see cref="Text"/>), else the call's file, which holds every one
/// (<see cref="File"/>).
/// </summary>
public sealed class ResultLines : IAsyncDisposable
{
    /// <summary>How many pieces of the answer wait for its file's writer before a producer waits with them: none is ever dropped.</summary>
    private const int Waiting = 256;

    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false);

    private readonly IFileSystem _fileSystem;
    private readonly ILineMask _mask;
    private readonly string _file;
    private readonly StringBuilder _held = new();
    private readonly Channel<string> _pieces = Channel.CreateBounded<string>(
        new BoundedChannelOptions(Waiting) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    private Task? _writing;
    private bool _first = true;
    private volatile bool _ended;

    /// <summary>A writer of the answer whose file, once it passes a page, is <paramref name="file"/>.</summary>
    internal ResultLines(IFileSystem fileSystem, ILineMask mask, string file)
    {
        _fileSystem = fileSystem;
        _mask = mask;
        _file = file;
    }

    /// <summary>How many lines were written, every one counted.</summary>
    public long Count { get; private set; }

    /// <summary>The answer whole, where its lines fit one page; null where they passed it and are in <see cref="File"/>.</summary>
    public string? Text => _writing is null ? _held.ToString() : null;

    /// <summary>The call's file, which holds every line once the answer ends, where they passed a page; null where they fit one.</summary>
    public string? File => _writing is null ? null : _file;

    /// <summary>Writes <paramref name="line"/>, masked: held while the answer fits a page, else written on into the call's file.</summary>
    /// <param name="line">One line of the answer, with no line ending in it.</param>
    /// <param name="cancellationToken">Stops a wait for the file's writer.</param>
    public async ValueTask LineAsync(string line, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);

        var masked = _mask.Redact(line);
        Count++;

        if (_writing is not null)
        {
            await HandAsync("\n" + masked, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!_first)
        {
            _held.Append('\n');
        }

        _first = false;
        _held.Append(masked);

        // Past a page, everything held so far goes to the file, and every line after it is written there as it comes.
        if (!Runtime.KeptResults.OnePage(_held))
        {
            _writing = Task.Factory.StartNew(Write, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await HandAsync(_held.ToString(), cancellationToken).ConfigureAwait(false);
            _held.Clear();
        }
    }

    /// <summary>Hands <paramref name="piece"/> to the file's writer, waiting while it is behind: a writer that failed raises why.</summary>
    private async ValueTask HandAsync(string piece, CancellationToken cancellationToken)
    {
        try
        {
            await _pieces.Writer.WriteAsync(piece, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            // Closed by the writer that failed: what it failed with is the answer's.
            await _writing!.ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Ends the answer: once it returns, every line is in the call's file, where the answer passed a page.</summary>
    public async Task EndAsync()
    {
        _ended = true;
        _pieces.Writer.TryComplete();

        if (_writing is { } writing)
        {
            await writing.ConfigureAwait(false);
        }
    }

    /// <summary>Lets the file's writer go: an answer not ended leaves no file.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!_ended)
        {
            _pieces.Writer.TryComplete();

            if (_writing is { } writing)
            {
                await writing.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }

    /// <summary>
    /// Writes the call's file whole from the pieces as they come, on a thread of its own that waits for each, and puts it in
    /// place once the answer ends - leaving the file as it was where the answer was abandoned.
    /// </summary>
    private void Write()
    {
        try
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(_file)!);
            _fileSystem.WriteAtomic(_file, stream =>
            {
                using var writer = new StreamWriter(stream, Utf8NoMark, leaveOpen: true);
                var reader = _pieces.Reader;

                while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out var piece))
                    {
                        writer.Write(piece);
                    }
                }

                if (!_ended)
                {
                    throw new OperationCanceledException("the answer was abandoned before its end, and its file is not put in place");
                }
            });
        }
        catch (Exception ex)
        {
            // A producer waiting on a writer that stopped is let go, and told why.
            _pieces.Writer.TryComplete(ex);
            throw;
        }
    }
}
