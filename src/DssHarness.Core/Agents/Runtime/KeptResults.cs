using System.Collections.Concurrent;
using System.Text;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Pages;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The whole results of a conversation's calls, a page at a time: what <c>read_result</c> reads, and what a call's own
/// answer gives the first page of. A call's whole result is the file beside the stream its records keep it in
/// (<see cref="AgentCallRecords.ResultFile"/>) - an answer longer than a page, a command's log - and its pages are laid from
/// that file once (<see cref="TextPages"/>), not numbered, at the room of a tool's answer to a model, and kept for the
/// conversation's life under the call's own key (<see cref="PageKey.OfCall"/>) in the conversation's kept answers, the
/// step's <c>graph-pages</c> directory, which is put away when the conversation ends - so every page of a result, a resumed
/// conversation's among them, is read by seeking to its mark. A page asked of a file that changed since its pages were laid
/// is laid again, and says so (<see cref="TextPages.Relaid"/>). Beside a call's id, <c>read_result</c> reads a message of the
/// step's stored stream by the id a compaction record gives it (<see cref="StoredStream.MessageId"/>), and the continuation
/// record a conversation was begun with by <see cref="ContinuationId"/>; and the same pages, numbered, are what
/// <c>view_file</c> gives of a file.
/// </summary>
/// <remarks>
/// A file's digest is held by how it stands (<see cref="FileStanding"/>): it is laid again - which hashes it, in the one
/// pass that lays it - only where its standing changed since it was last laid, never hashed whole for each page asked.
/// </remarks>
/// <param name="fileSystem">The file system the results are read through.</param>
/// <param name="stepDirectory">The step's directory, whose <c>tool-results</c> hold the conversation's whole results.</param>
/// <param name="pages">The conversation's kept answers, where each result's pages are kept.</param>
public sealed class KeptResults(IFileSystem fileSystem, string stepDirectory, KeptAnswers<TextPagesKept> pages)
{
    /// <summary>
    /// The id the continuation record a conversation was begun with is kept under, among its results: its first page is in
    /// the conversation's first message, and <c>read_result</c> gives every page of it by this id.
    /// </summary>
    public const string ContinuationId = "continuation-record";

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly string _stepDirectory = stepDirectory ?? throw new ArgumentNullException(nameof(stepDirectory));
    private readonly KeptAnswers<TextPagesKept> _pages = pages ?? throw new ArgumentNullException(nameof(pages));

    /// <summary>How each text's file stood when its pages were last laid, and the digest they were laid at, by the key they are kept under.</summary>
    private readonly ConcurrentDictionary<string, Laid> _laid = new(StringComparer.Ordinal);

    /// <summary>The characters a page of a result gives its lines: a tool's answer to a model, less what its header and its notes take.</summary>
    public static int Room { get; } = PageRoom.For(PageRoom.ModelAnswer, []);

    /// <summary>
    /// The whole results of the conversation whose step records are in <paramref name="stepDirectory"/>, their pages kept
    /// in its kept answers (<see cref="OrchestratorLayout.StepPagesDirectoryName"/>): the directory the engine puts away
    /// when the conversation ends, beside the pages its graph answers keep, under keys none of them holds.
    /// </summary>
    /// <param name="fileSystem">The file system the results are read through.</param>
    /// <param name="stepDirectory">The step's directory.</param>
    public static KeptResults Of(IFileSystem fileSystem, string stepDirectory)
    {
        ArgumentNullException.ThrowIfNull(stepDirectory);

        return new KeptResults(
            fileSystem,
            stepDirectory,
            new KeptAnswers<TextPagesKept>(Path.Combine(stepDirectory, OrchestratorLayout.StepPagesDirectoryName), fileSystem, TextPagesKept.Contract, new TextPagesFreshness()));
    }

    /// <summary>What <c>read_result</c> is refused with for an id no result is kept for.</summary>
    /// <param name="id">The id asked.</param>
    public static string NoneKept(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"no result is kept for '{ReportText.Printable(id)}' in this conversation";
    }

    /// <summary>
    /// How the header of a page a call's own answer gives says the next page is asked for (<see cref="Paging.Header"/>):
    /// of <c>read_result</c>, by the call's id - the page that follows is that tool's, not the call's.
    /// </summary>
    /// <param name="id">The call's id.</param>
    public static string AskOf(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        return $"ask {AgentTools.ReadResult} with id: '{ReportText.Printable(id)}',";
    }

    /// <summary>What the first page a call's own answer gives says after its counts: the file that holds the whole.</summary>
    /// <param name="file">The call's file.</param>
    public static string WholeIn(string file)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        return $"the whole is '{file}'";
    }

    /// <summary>
    /// Whether a text of <paramref name="characters"/> characters is one page of a call's answer, however its lines fall: a
    /// page gives a line its characters and one for its end, and each line but the last ends with a character of its own.
    /// </summary>
    /// <param name="characters">How many characters the text holds.</param>
    public static bool OnePage(long characters) => characters < Room;

    /// <summary>Whether <paramref name="text"/> is one page of a call's answer, however its lines fall (<see cref="OnePage(long)"/>).</summary>
    /// <param name="text">The text.</param>
    public static bool OnePage(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return OnePage(text.Length);
    }

    /// <summary>Whether <paramref name="text"/> is one page of a call's answer, however its lines fall (<see cref="OnePage(long)"/>).</summary>
    /// <param name="text">The text.</param>
    public static bool OnePage(StringBuilder text)
    {
        ArgumentNullException.ThrowIfNull(text);

        return OnePage(text.Length);
    }

    /// <summary>
    /// The first page of the call <paramref name="id"/>'s answer, laid from <paramref name="bytes"/> - the bytes its file
    /// <paramref name="file"/> is to hold, in memory - as <c>read_result</c> lays them from the file, so the page it gives
    /// next goes on where this one ends: its header says how many pages the answer holds, how <c>read_result</c> gives the
    /// next (<see cref="AskOf"/>) and where the whole is (<see cref="WholeIn"/>). Null where the answer is one page, and is
    /// given whole.
    /// </summary>
    /// <param name="bytes">The answer as its file is to hold it: UTF-8, with no mark.</param>
    /// <param name="id">The call's id.</param>
    /// <param name="file">The call's file (<see cref="AgentCallRecords.ResultFile"/>).</param>
    public static string? FirstPageOf(byte[] bytes, string id, string file)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(file);

        Stream Held(string _) => new MemoryStream(bytes, writable: false);

        // Bytes in memory are read without waiting: each step of the lay and of the page is done when it returns.
        var kept = Done(TextPages.LayAsync(Held, file, PageKey.OfCall(id), Asked(id), Room, numbered: false, CancellationToken.None));

        return kept.Marks.Count == 1
            ? null
            : Done(TextPages.PageAsync(Held, kept, 1, numbered: false, WholeIn(file), AskOf(id), CancellationToken.None)).Text;
    }

    /// <summary>
    /// The <paramref name="page"/> of the whole result kept for <paramref name="id"/>: a call's, a message of the step's
    /// stored stream a compaction record named (written whole beside the results the first time it is asked, from the
    /// stream), or the continuation record (<see cref="ContinuationId"/>) - from the pages kept where the file is the one
    /// they were laid on, laid again - and said so, where pages laid on the file as it was are kept - where it is not; null
    /// where no result is kept for the id.
    /// </summary>
    /// <param name="id">The id, as a call's client named it or a record gave it.</param>
    /// <param name="page">The page asked, from 1: one past what an int counts is past the last, said by its own number.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The result's file does not read as text.</exception>
    public async Task<PageAnswer<TextEntry>?> ReadAsync(string id, long page, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentOutOfRangeException.ThrowIfLessThan(page, 1);

        var file = AgentCallRecords.ResultFile(_stepDirectory, id);

        if (_fileSystem.StandingOf(file) is null && !Materialized(id, file))
        {
            return null;
        }

        var (kept, note) = await LaidAsync(file, PageKey.OfCall(id), Asked(id), numbered: false, _fileSystem.OpenRead, cancellationToken).ConfigureAwait(false);

        return await TextPages.PageAsync(_fileSystem.OpenRead, kept, page, numbered: false, note, Paging.AskAgain, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The first page of the call <paramref name="id"/>'s answer, from the file that holds it whole already - a command's
    /// log, an answer written there a line at a time (<see cref="ResultLines"/>) - as the call's own answer gives it: its
    /// header saying how <c>read_result</c> gives the next page and where the whole is; the page whole, with no header,
    /// where the file is one page.
    /// </summary>
    /// <param name="id">The call's id.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The file does not read as text.</exception>
    public async Task<string> FirstPageAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);

        var file = AgentCallRecords.ResultFile(_stepDirectory, id);
        var (kept, note) = await LaidAsync(file, PageKey.OfCall(id), Asked(id), numbered: false, _fileSystem.OpenRead, cancellationToken).ConfigureAwait(false);
        var said = kept.Marks.Count == 1 ? note : note is null ? WholeIn(file) : $"{note} · {WholeIn(file)}";

        return (await TextPages.PageAsync(_fileSystem.OpenRead, kept, 1, numbered: false, said, AskOf(id), cancellationToken).ConfigureAwait(false)).Text;
    }

    /// <summary>
    /// The pages of the text at <paramref name="file"/> kept for <paramref name="key"/>: those kept where the file stands as
    /// it did when they were last laid and they are still kept; else laid again in one pass - which hashes it - and, where
    /// pages laid on the same bytes are kept (a resumed conversation's), those; else the new ones, kept, and said why: the
    /// text changed since pages of it were laid (<see cref="TextPages.Relaid"/>), or they could not be kept
    /// (<see cref="TextPages.Unkept"/>). What the page given from them says after its counts, null where nothing is said.
    /// </summary>
    /// <param name="file">The text's file, absolute.</param>
    /// <param name="key">The ask its pages are kept for (<see cref="PageKey"/>).</param>
    /// <param name="asked">How its pages name what was asked, for a page past the last to say.</param>
    /// <param name="numbered">Whether each line is numbered, as a file viewed numbers its lines.</param>
    /// <param name="open">Opens the file to read: a reader that decides where an open file stands opens it so.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="InvalidDataException">The file does not read as text.</exception>
    internal async Task<(TextPagesKept Kept, string? Note)> LaidAsync(string file, string key, string asked, bool numbered, Func<string, Stream> open, CancellationToken cancellationToken)
    {
        var standing = _fileSystem.StandingOf(file);

        if (standing is not null && _laid.TryGetValue(key, out var laid) && laid.Standing == standing && _pages.Find(key, laid.Digest) is { Kept: { } held } && held.Path == file)
        {
            return (held, null);
        }

        // Laid again, which reads the file once: pages kept on the same bytes - by a resumed conversation - are the pages
        // its earlier ones were given from, and pages kept on other bytes say the text changed.
        var fresh = await TextPages.LayAsync(open, file, key, asked, Room, numbered, cancellationToken).ConfigureAwait(false);
        var found = _pages.Find(key, fresh.Digest);
        TextPagesKept kept;
        string? note = null;

        if (found.Kept is { } same && same.Path == file)
        {
            kept = same;
        }
        else
        {
            // Kept for a resume; where the disk refuses it, the page that laid them says so, and they are held here.
            kept = fresh;
            note = found.Moved ? TextPages.Relaid : null;

            if (_pages.Keep(fresh) is { } unkept)
            {
                note = note is null ? TextPages.Unkept(unkept) : $"{note} · {TextPages.Unkept(unkept)}";
            }
        }

        if (standing is not null)
        {
            _laid[key] = new Laid(standing, fresh.Digest);
        }

        return (kept, note);
    }

    /// <summary>How the pages of the result kept for <paramref name="id"/> name what was asked, for a page past the last to say.</summary>
    private static string Asked(string id) => $"{AgentTools.ReadResult} {ReportText.Printable(id)}";

    /// <summary>A task over bytes in memory, which is done when it returns: what it gave.</summary>
    private static T Done<T>(Task<T> task)
        => task.IsCompleted
            ? task.GetAwaiter().GetResult()
            : throw new InvalidOperationException("a text held in memory is read without waiting, and its read waited");

    /// <summary>
    /// Whether the message of the step's stored stream <paramref name="id"/> names (<see cref="StoredStream.MessageId"/>)
    /// was found there and written whole to <paramref name="file"/>, where its pages are then laid as a call's result's
    /// are: false where the id names no message the stream holds.
    /// </summary>
    private bool Materialized(string id, string file)
    {
        if (!StoredStream.NamesAMessage(id) || StoredStream.MessageText(StoredStream.Read(_fileSystem, _stepDirectory), id) is not { } text)
        {
            return false;
        }

        // Masked already, as every line of the stream is.
        var bytes = Encoding.UTF8.GetBytes(text);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
        _fileSystem.WriteAtomic(file, stream => stream.Write(bytes));
        return true;
    }

    /// <summary>How a result's file stood when its pages were laid, and the digest of the bytes they were laid on.</summary>
    private sealed record Laid(FileStanding Standing, string Digest);
}
