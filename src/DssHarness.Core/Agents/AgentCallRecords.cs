using System.Security.Cryptography;
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
        }, JsonStateFile.LineOptions));

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
        }, JsonStateFile.LineOptions));

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
        }, JsonStateFile.LineOptions));

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
        }, JsonStateFile.LineOptions));

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
/// (<see cref="File"/>). A line may head a block, and the lines after it belong to it (<see cref="HeadAsync"/>,
/// <see cref="BlockLineAsync"/>) - a search's hits beneath their declaration's line: an answer past a page whose lines
/// headed any keeps its blocks beside its file, in the same pass, each where the file holds it, ended by the digest of
/// the file's bytes (<see cref="TextBlocks"/>), so a page of it that begins inside one gives its head again first; one
/// whose lines headed none keeps none.
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

    /// <summary>The blocks the lines held so far have ended, in order: handed to the file's writer once the answer passes a page.</summary>
    private readonly List<TextBlock> _heldBlocks = [];

    private readonly Channel<Piece> _pieces = Channel.CreateBounded<Piece>(
        new BoundedChannelOptions(Waiting) { SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait });

    private Task? _writing;
    private bool _first = true;
    private volatile bool _ended;

    /// <summary>The byte of the call's file the next line begins at: every line before it, and the line feed after each.</summary>
    private long _offset;

    /// <summary>
    /// The block the line written last is of: where its head begins, where the line after its head does, and whether a line
    /// of it came after its head; null where the line written last is of none.
    /// </summary>
    private (long Head, long Body, bool Lines)? _block;

    /// <summary>What a line of the answer is to its blocks: of none, the head of one, or a line of the block its last head opened.</summary>
    private enum Role
    {
        None,
        Head,
        OfBlock,
    }

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

    /// <summary>Writes <paramref name="line"/>, masked, a line of no block: held while the answer fits a page, else written on into the call's file.</summary>
    /// <param name="line">One line of the answer, with no line ending in it.</param>
    /// <param name="cancellationToken">Stops a wait for the file's writer.</param>
    public ValueTask LineAsync(string line, CancellationToken cancellationToken) => WrittenAsync(line, Role.None, cancellationToken);

    /// <summary>
    /// Writes <paramref name="line"/>, masked, as <see cref="LineAsync"/> writes a line, as the head of a block: the lines
    /// written after it by <see cref="BlockLineAsync"/> belong to it, until a line of no block or another head is written.
    /// </summary>
    /// <param name="line">One line of the answer, with no line ending in it.</param>
    /// <param name="cancellationToken">Stops a wait for the file's writer.</param>
    public ValueTask HeadAsync(string line, CancellationToken cancellationToken) => WrittenAsync(line, Role.Head, cancellationToken);

    /// <summary>Writes <paramref name="line"/>, masked, as <see cref="LineAsync"/> writes a line, as a line of the block the last head opened.</summary>
    /// <param name="line">One line of the answer, with no line ending in it.</param>
    /// <param name="cancellationToken">Stops a wait for the file's writer.</param>
    /// <exception cref="InvalidOperationException">No block is open: the line written last is of none.</exception>
    public ValueTask BlockLineAsync(string line, CancellationToken cancellationToken) => WrittenAsync(line, Role.OfBlock, cancellationToken);

    /// <summary>Writes <paramref name="line"/>, masked, as <paramref name="role"/> says it is to the answer's blocks.</summary>
    private async ValueTask WrittenAsync(string line, Role role, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (role == Role.OfBlock && _block is null)
        {
            throw new InvalidOperationException("A line of a block follows its head, and no block is open: the line written last is of none.");
        }

        var masked = _mask.Redact(line);
        var begins = _offset;

        Count++;
        _offset += Utf8NoMark.GetByteCount(masked) + 1;

        // A line of no block, or another's head, ends the block open before it: the block ends where the line begins.
        if (role != Role.OfBlock)
        {
            await EndedAsync(begins, cancellationToken).ConfigureAwait(false);
        }

        _block = role switch
        {
            Role.Head => (begins, _offset, false),
            Role.OfBlock => _block!.Value with { Lines = true },
            _ => null,
        };

        if (_writing is not null)
        {
            await HandAsync(new Piece("\n" + masked, null), cancellationToken).ConfigureAwait(false);
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
            await HandAsync(new Piece(_held.ToString(), null), cancellationToken).ConfigureAwait(false);
            _held.Clear();

            foreach (var block in _heldBlocks)
            {
                await HandAsync(new Piece(null, block), cancellationToken).ConfigureAwait(false);
            }

            _heldBlocks.Clear();
        }
    }

    /// <summary>
    /// Ends the block open, where one is and a line of it came after its head, at <paramref name="next"/> - where the line
    /// after it begins, or the answer's end: held while the answer fits a page, else handed to the file's writer.
    /// </summary>
    private async ValueTask EndedAsync(long next, CancellationToken cancellationToken)
    {
        if (_block is { Lines: true } open)
        {
            var block = new TextBlock(open.Head, open.Body, next);

            if (_writing is null)
            {
                _heldBlocks.Add(block);
            }
            else
            {
                await HandAsync(new Piece(null, block), cancellationToken).ConfigureAwait(false);
            }
        }

        _block = null;
    }

    /// <summary>Hands <paramref name="piece"/> to the file's writer, waiting while it is behind: a writer that failed raises why.</summary>
    private async ValueTask HandAsync(Piece piece, CancellationToken cancellationToken)
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

    /// <summary>
    /// A writer of the answer as text rather than as lines, however its writes part it: what a reader that writes text - a
    /// page the web is read into - writes the answer through. Each line it is given, up to a line feed, a carriage return
    /// before it dropped, is written as <see cref="LineAsync"/> writes one; what follows the last line feed, once the
    /// writer is ended (<see cref="ResultLinesWriter.EndAsync"/>).
    /// </summary>
    /// <param name="cancellationToken">Stops a wait for the file's writer.</param>
    public ResultLinesWriter Writer(CancellationToken cancellationToken) => new(this, cancellationToken);

    /// <summary>
    /// Writes the call's file again, once the answer has ended past a page, as <paramref name="write"/> makes it of what the
    /// file holds: the answer's lines read a piece at a time, never held whole, and what is written about them the
    /// caller's, masked by it. The file is put in place whole (<see cref="IFileSystem.WriteAtomic"/>), on a thread of its
    /// own, as the answer's lines were. Blocks its lines headed are of the lines as they were written: the digest they end
    /// with is no longer the file's, and its pages are laid as its lines alone (<see cref="TextBlocks"/>).
    /// </summary>
    /// <param name="write">Writes the file anew, given what it held and where the new one is written.</param>
    /// <exception cref="InvalidOperationException">The answer has not ended, or its lines fit a page and it has no file.</exception>
    public Task RewriteAsync(Action<TextReader, TextWriter> write)
    {
        ArgumentNullException.ThrowIfNull(write);

        if (!_ended || File is not { } file)
        {
            throw new InvalidOperationException("Only an answer that has ended past a page has a file to write again.");
        }

        return Task.Factory.StartNew(
            () => _fileSystem.WriteAtomic(file, stream =>
            {
                using var reader = new StreamReader(_fileSystem.OpenRead(file), Utf8NoMark);
                using var writer = new StreamWriter(stream, Utf8NoMark, leaveOpen: true);

                write(reader, writer);
            }),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    /// <summary>Ends the answer: once it returns, every line is in the call's file, and every block its lines headed beside it, where the answer passed a page.</summary>
    public async Task EndAsync()
    {
        // The block open ends with the answer: past its last line, which no line feed follows.
        if (Count > 0)
        {
            await EndedAsync(_offset - 1, CancellationToken.None).ConfigureAwait(false);
        }

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
    /// place once the answer ends - leaving the file as it was where the answer was abandoned. From the first block a piece
    /// brings, if one does, the blocks are written beside the file as they come (<see cref="TextBlocks.FileOf"/>), ended by
    /// the digest of every byte the file holds, and put in place just before it.
    /// </summary>
    private void Write()
    {
        try
        {
            _fileSystem.CreateDirectory(Path.GetDirectoryName(_file)!);
            _fileSystem.WriteAtomic(_file, stream =>
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

                if (Pump(stream, hash, blocks: null) is { } first)
                {
                    _fileSystem.WriteAtomic(TextBlocks.FileOf(_file), kept =>
                    {
                        var blocks = new TextBlocks.Writer(kept);

                        blocks.Add(first);
                        Pump(stream, hash, blocks);
                        Whole();

                        // The digest FileContentHash gives the bytes the file holds, taken as they were written.
                        blocks.End(Convert.ToHexStringLower(hash.GetHashAndReset()));
                    });
                }

                Whole();
            });
        }
        catch (Exception ex)
        {
            // A producer waiting on a writer that stopped is let go, and told why.
            _pieces.Writer.TryComplete(ex);
            throw;
        }
    }

    /// <summary>
    /// Writes the pieces as they come until the last: each text's bytes into <paramref name="text"/>, hashed as they are
    /// written, and each block into <paramref name="blocks"/> - where none is given yet, the first block a piece brings is
    /// returned, for its file to be opened, and the pieces after it are left; null once every piece is written.
    /// </summary>
    private TextBlock? Pump(Stream text, IncrementalHash hash, TextBlocks.Writer? blocks)
    {
        var reader = _pieces.Reader;

        while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var piece))
            {
                if (piece.Block is not { } block)
                {
                    // Each piece is whole lines, or a line feed and a line, so no character is parted from the one after it.
                    var bytes = Utf8NoMark.GetBytes(piece.Text!);
                    hash.AppendData(bytes);
                    text.Write(bytes);
                }
                else if (blocks is null)
                {
                    return block;
                }
                else
                {
                    blocks.Add(block);
                }
            }
        }

        return null;
    }

    /// <summary>Refuses to put in place what an answer abandoned before its end wrote.</summary>
    /// <exception cref="OperationCanceledException">The answer was abandoned.</exception>
    private void Whole()
    {
        if (!_ended)
        {
            throw new OperationCanceledException("the answer was abandoned before its end, and its file is not put in place");
        }
    }

    /// <summary>What the file's writer is handed: text of the answer, or a block its lines ended - one of the two.</summary>
    /// <param name="Text">Text of the answer: whole lines, or a line feed and a line.</param>
    /// <param name="Block">A block the answer's lines ended, where the file holds it.</param>
    private readonly record struct Piece(string? Text, TextBlock? Block);
}

/// <summary>
/// A call's answer written as text (<see cref="ResultLines.Writer"/>): each line it is given, up to a line feed, a carriage
/// return before it dropped, written into the answer as it ends, and what follows the last line feed once the writer is
/// ended - so a text of any length costs a line of memory, and the answer's lines join back into the text it was given.
/// </summary>
public sealed class ResultLinesWriter : TextWriter
{
    private static readonly UTF8Encoding Utf8NoMark = new(encoderShouldEmitUTF8Identifier: false);

    private readonly ResultLines _lines;
    private readonly CancellationToken _cancellationToken;
    private readonly StringBuilder _line = new();
    private bool _written;

    /// <summary>A writer into <paramref name="lines"/>, whose waits for the file's writer <paramref name="cancellationToken"/> stops.</summary>
    internal ResultLinesWriter(ResultLines lines, CancellationToken cancellationToken)
    {
        _lines = lines;
        _cancellationToken = cancellationToken;
    }

    /// <inheritdoc/>
    public override Encoding Encoding => Utf8NoMark;

    /// <inheritdoc/>
    public override void Write(char value) => Write([value]);

    /// <inheritdoc/>
    public override void Write(char[] buffer, int index, int count) => Write(buffer.AsSpan(index, count));

    /// <inheritdoc/>
    public override void Write(string? value) => Write(value.AsSpan());

    /// <inheritdoc/>
    public override void Write(ReadOnlySpan<char> buffer)
    {
        // A writer that writes as it goes - a page's text, made a line at a time - waits here while the file's writer is
        // behind, as one that writes asynchronously waits with it.
        foreach (var line in Taken(buffer))
        {
            _lines.LineAsync(line, _cancellationToken).AsTask().GetAwaiter().GetResult();
        }
    }

    /// <inheritdoc/>
    public override Task WriteAsync(char value) => WriteAsync(new[] { value }.AsMemory(), _cancellationToken);

    /// <inheritdoc/>
    public override Task WriteAsync(string? value) => WriteAsync((value ?? string.Empty).AsMemory(), _cancellationToken);

    /// <inheritdoc/>
    public override Task WriteAsync(char[] buffer, int index, int count) => WriteAsync(buffer.AsMemory(index, count), _cancellationToken);

    /// <inheritdoc/>
    public override async Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
    {
        foreach (var line in Taken(buffer.Span))
        {
            await _lines.LineAsync(line, _cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes what follows the last line feed as the answer's last line, where anything was written: the text ends.</summary>
    public async Task EndAsync()
    {
        if (_written)
        {
            await _lines.LineAsync(_line.ToString(), _cancellationToken).ConfigureAwait(false);
            _line.Clear();
        }
    }

    /// <summary>The lines <paramref name="buffer"/> ends, the one held before it first, each with its carriage return dropped; the rest held.</summary>
    private List<string> Taken(ReadOnlySpan<char> buffer)
    {
        var ended = new List<string>();

        if (buffer.IsEmpty)
        {
            return ended;
        }

        _written = true;

        while (buffer.IndexOf('\n') is var feed and >= 0)
        {
            _line.Append(buffer[..feed]);

            if (_line.Length > 0 && _line[^1] == '\r')
            {
                _line.Length--;
            }

            ended.Add(_line.ToString());
            _line.Clear();
            buffer = buffer[(feed + 1)..];
        }

        _line.Append(buffer);

        return ended;
    }
}
