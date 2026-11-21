using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents.Policy;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;

namespace DssHarness.Core.Agents.Runtime;

/// <summary>
/// The files an agent's tools list and search, and the one search they run: <c>find_files</c> and <c>search_files</c>
/// list a directory by it. In a git work tree a directory's files are what git lists there - tracked and untracked, git's ignore rules applied; where git
/// lists nothing - a directory git ignores, or none of git's - the files the directory holds, walked without following a
/// link. A search reads text alone, every file whatever its size, streamed a line at a time (<see cref="StreamedText"/>):
/// a binary file and one that is no text this tool reads are passed over and counted, and one that could not be read is
/// counted apart, with why the first was not.
/// The one way the tools read and write a file's bytes besides (<see cref="OpenDecided"/>, <see cref="ReadAsync"/>,
/// <see cref="Write(string, string, Action{Stream}, Func{string, AgentPolicyAnswer.Denied?})"/>): a read through the file as
/// it was opened, where it stands decided again before a byte passes; a write relative to its directory held from its
/// writable root with no link on the way (<see cref="HeldDirectory"/>). A directory is listed by its path, so what a link put
/// in place of one since leads to is what it lists - names, never a byte of a file, each file read through its own open.
/// </summary>
/// <param name="git">Lists a work tree's files.</param>
/// <param name="fileSystem">Walks a directory git does not list, and reads and writes each file.</param>
public sealed class AgentFiles(IGitClient git, IFileSystem fileSystem)
{
    /// <summary>The refusal a search's own decision stands for, where it only says whether a place may be read: counted, never said.</summary>
    private static readonly AgentPolicyAnswer.Denied NotToBeRead = new(ConfinedPaths.RootsRule, AgentPolicySource.Builtin, "a place the reader may not read");

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    /// <summary>
    /// Every file under <paramref name="directory"/>, relative to it with forward separators, in ordinal order: what git
    /// lists there, else what the directory holds; a link and a directory are never among them, nor a file whose name no
    /// pattern reads (<see cref="GlobPattern.Reads"/>), which is counted.
    /// </summary>
    /// <param name="directory">The directory, absolute.</param>
    /// <param name="cancellationToken">Stops the listing.</param>
    public async Task<AgentListing> ListAsync(string directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);

        IEnumerable<string> listed = [];

        if (await _git.GetRepositoryRootAsync(directory, cancellationToken).ConfigureAwait(false) is not null)
        {
            var names = await _git.ListTreeFilesAsync(directory, cancellationToken: cancellationToken).ConfigureAwait(false);
            listed = names.Select(name => name.Text).Distinct(StringComparer.Ordinal).ToList();
        }

        var unread = 0;
        AgentSearchUnread? firstUnread = null;

        // A place under the directory that cannot be read is passed by and counted, as a file a search cannot read is:
        // one such place never takes the listing of the rest with it.
        void Unread(string path, Exception ex)
        {
            unread++;
            firstUnread ??= new AgentSearchUnread(path, ex.Message);
        }

        if (!listed.Any())
        {
            listed = _fileSystem.EnumerateReachableFiles(directory, (place, ex) => Unread(Path.GetRelativePath(directory, place).Replace(Path.DirectorySeparatorChar, '/'), ex))
                .Select(file => Path.GetRelativePath(directory, file).Replace(Path.DirectorySeparatorChar, '/'))
                .Where(path => !path.Split('/').Contains(".git", StringComparer.OrdinalIgnoreCase))
                .ToList();
        }

        var files = new List<string>();

        foreach (var path in listed)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (_fileSystem.KindOf(Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar))) == PathKind.File)
                {
                    files.Add(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Unread(path, ex);
            }
        }

        var named = files.Where(GlobPattern.Reads).Order(StringComparer.Ordinal).ToList();

        return new AgentListing(named, files.Count - named.Count, unread, firstUnread);
    }

    /// <summary>
    /// The lines under <paramref name="directory"/> that <paramref name="pattern"/> matches, file by file in the listing's
    /// order: each file <paramref name="glob"/> takes, where one is given, and <paramref name="mayRead"/> allows - one it
    /// does not counted as left out - read as text a line at a time, whatever it holds, each hit with
    /// <paramref name="context"/> lines on either side handed to <paramref name="found"/> as it is found, in order. No hit
    /// is held past its handing on, so a search of a million hits holds no more than one hit's lines at a time.
    /// </summary>
    /// <param name="directory">The directory, absolute.</param>
    /// <param name="pattern">The pattern, matched against each line.</param>
    /// <param name="glob">The files searched, by their paths relative to <paramref name="directory"/>; null for every one.</param>
    /// <param name="comparison">How a path is compared with <paramref name="glob"/>.</param>
    /// <param name="context">How many lines before and after each hit are given, any number.</param>
    /// <param name="mayRead">Whether the agent may read a file, by its absolute path.</param>
    /// <param name="found">Takes each hit, as it is found.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    public async Task<AgentSearch> SearchAsync(
        string directory,
        Regex pattern,
        GlobPattern? glob,
        StringComparison comparison,
        int context,
        Func<string, bool> mayRead,
        Func<AgentSearchHit, CancellationToken, ValueTask> found,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(mayRead);
        ArgumentNullException.ThrowIfNull(found);
        ArgumentOutOfRangeException.ThrowIfNegative(context);

        long given = 0;
        var refused = 0;
        var passed = 0;
        var searched = 0;
        var unread = 0;
        AgentSearchUnread? firstUnread = null;
        var listing = await ListAsync(directory, cancellationToken).ConfigureAwait(false);

        foreach (var path in listing.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (glob is not null && !glob.Matches(path, comparison))
            {
                continue;
            }

            var file = Path.Combine(directory, path.Replace('/', Path.DirectorySeparatorChar));

            if (!mayRead(file))
            {
                refused++;
                continue;
            }

            long ofFile = 0;

            try
            {
                // A file the listing names that is no longer there - gone since, or a deletion git still lists - is none to search.
                if (_fileSystem.StandingOf(file) is not { })
                {
                    continue;
                }

                // Read through the file as it was opened, where it stands decided again as it was before the open: one a
                // link put on its way since leads to a place the reader may not read is left out as one it may not.
                var (stream, _) = OpenDecided(file, place => mayRead(place) ? null : NotToBeRead);

                if (stream is null)
                {
                    refused++;
                    continue;
                }

                await using (stream.ConfigureAwait(false))
                {
                    using var text = await StreamedText.OpenAsync(stream, file, null, cancellationToken).ConfigureAwait(false);

                    await foreach (var hit in HitsAsync(text.LinesAsync(cancellationToken), path, pattern, context, cancellationToken).ConfigureAwait(false))
                    {
                        await found(hit, cancellationToken).ConfigureAwait(false);
                        ofFile++;
                    }
                }

                searched++;
                given += ofFile;
            }
            catch (StreamedTextUnreadException ex)
            {
                given += ofFile;

                // A file that is no text from its first lines is passed over; one that stopped being text after it gave
                // hits gave them, and is said as a file not read to its end, with why.
                if (ofFile == 0)
                {
                    passed++;
                }
                else
                {
                    unread++;
                    firstUnread ??= new AgentSearchUnread(path, $"it stopped reading as text after its first hits, which are given: {ex.Why}");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Said apart from what was passed over by choice: a file not read may hold what was searched for.
                given += ofFile;
                unread++;
                firstUnread ??= new AgentSearchUnread(path, ex.Message);
            }
        }

        return new AgentSearch([], 0, refused, passed, searched, listing, unread, firstUnread) { Found = given };
    }

    /// <summary>
    /// The hits of a file's <paramref name="lines"/>, in order: each line <paramref name="pattern"/> matches, with the
    /// <paramref name="context"/> lines before it and after it - the lines before held as they pass, a hit's lines after it
    /// taken as they come, and the hit given once it has them, or at the file's end.
    /// </summary>
    private static async IAsyncEnumerable<AgentSearchHit> HitsAsync(
        IAsyncEnumerable<string> lines,
        string path,
        Regex pattern,
        int context,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var before = new Queue<AgentSearchLine>();
        var waiting = new Queue<(AgentSearchHit Hit, List<AgentSearchLine> After)>();
        long number = 0;

        await foreach (var line in lines.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            number++;
            var read = new AgentSearchLine(number, line);

            foreach (var (_, after) in waiting)
            {
                after.Add(read);
            }

            while (waiting.TryPeek(out var first) && first.After.Count >= context)
            {
                waiting.Dequeue();
                yield return first.Hit with { After = first.After };
            }

            if (pattern.IsMatch(line))
            {
                var hit = new AgentSearchHit(path, number, line, [.. before], []);

                if (context == 0)
                {
                    yield return hit;
                }
                else
                {
                    waiting.Enqueue((hit, []));
                }
            }

            if (context > 0)
            {
                before.Enqueue(read);

                if (before.Count > context)
                {
                    before.Dequeue();
                }
            }
        }

        while (waiting.TryDequeue(out var rest))
        {
            yield return rest.Hit with { After = rest.After };
        }
    }

    /// <summary>
    /// The file at <paramref name="file"/> opened to read - opened as itself, never by its path again - once the place the
    /// open file stands at (<see cref="IFileSystem.FinalPathOf"/>) is decided again by <paramref name="refusal"/>, as the
    /// path was decided before the open: a link put in place of the file, or of a directory on its way, after that decision
    /// reaches nothing the decision refuses. Opened never to wait - a pipe put there is refused before a byte is read
    /// (<see cref="IFileSystem.OpenRead"/>). The one way an agent's file tools open a file to read. A second name of the
    /// same file - a hard link - is a place of its own, and decided as one.
    /// </summary>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="refusal">Why a place may not be read, by its absolute path; null where it may.</param>
    /// <returns>The open file, which its caller closes; or, where it stands at a place <paramref name="refusal"/> refuses, why - nothing read, nothing left open.</returns>
    /// <exception cref="IOException">The file could not be opened or named, or is no regular file.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    public (Stream? Stream, AgentPolicyAnswer.Denied? Denied) OpenDecided(string file, Func<string, AgentPolicyAnswer.Denied?> refusal)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(refusal);

        var stream = _fileSystem.OpenRead(file);

        try
        {
            var opened = _fileSystem.FinalPathOf(stream);

            if (refusal(opened) is { } denied)
            {
                stream.Dispose();

                return (null, denied with
                {
                    Reason = $"'{file}' was opened as '{opened}', where a link put on its way after it was decided leads; nothing was read: {denied.Reason}",
                });
            }

            return (stream, null);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The file at <paramref name="file"/> opened to read as <see cref="OpenDecided"/> opens it, for a reader that is handed
    /// an opener: one that stands at a place <paramref name="refusal"/> refuses raises its refusal
    /// (<see cref="AgentReadRefusedException"/>), nothing read.
    /// </summary>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="refusal">Why a place may not be read, by its absolute path; null where it may.</param>
    /// <exception cref="AgentReadRefusedException">The open file stands at a place the refusal refuses.</exception>
    public Stream OpenedOrRefused(string file, Func<string, AgentPolicyAnswer.Denied?> refusal)
    {
        var (stream, denied) = OpenDecided(file, refusal);

        return stream ?? throw new AgentReadRefusedException(denied!);
    }

    /// <summary>
    /// The bytes of the file at <paramref name="file"/>, read through the file as it was opened, decided again where it
    /// stands (<see cref="OpenDecided"/>), and read to its end, never more than <paramref name="most"/> bytes however much it
    /// grew since it was measured, held as it is read: nothing is set aside for bytes the file does not hold.
    /// </summary>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="most">The most bytes read; a file holding more is refused, nothing of it given. <see cref="long.MaxValue"/> reads the file whatever it holds.</param>
    /// <param name="refusal">Why a place may not be read, by its absolute path; null where it may.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The bytes; or, where the open file stands at a place <paramref name="refusal"/> refuses, why - nothing read.</returns>
    /// <exception cref="IOException">The file could not be opened, named or read, is no regular file, or holds more than <paramref name="most"/> bytes.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    public async Task<AgentFileRead> ReadAsync(string file, long most, Func<string, AgentPolicyAnswer.Denied?> refusal, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(most);

        var (stream, denied) = OpenDecided(file, refusal);

        if (stream is null)
        {
            return new AgentFileRead(null, denied);
        }

        await using (stream.ConfigureAwait(false))
        {
            // Read to the end, a byte past the bound told rather than cut: a file grown past it since it was measured is
            // refused whole.
            using var held = new MemoryStream();
            var chunk = new byte[81920];

            while (await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false) is var read and > 0)
            {
                if (read > most - held.Length)
                {
                    throw new IOException($"'{file}' holds more than {DiskSpace.Size(most)} now, past what it held when it was measured, and nothing of it was read");
                }

                held.Write(chunk, 0, read);
            }

            return new AgentFileRead(held.ToArray(), null);
        }
    }

    /// <summary>Writes <paramref name="bytes"/> to <paramref name="file"/> as <see cref="Write(string, string, Action{Stream}, Func{string, AgentPolicyAnswer.Denied?})"/> writes a file.</summary>
    /// <param name="root">The writable root <paramref name="file"/> was decided in, as the decision spells it.</param>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="bytes">What it is to hold.</param>
    /// <param name="refusal">Why a place may not be written, by its absolute path; null where it may.</param>
    /// <returns>Null where it was written; else why not, the file as it was.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public AgentPolicyAnswer.Denied? Write(string root, string file, byte[] bytes, Func<string, AgentPolicyAnswer.Denied?> refusal)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        return Write(root, file, stream => stream.Write(bytes), refusal);
    }

    /// <summary>
    /// Writes <paramref name="file"/> whole and atomically from what <paramref name="write"/> writes, relative to its
    /// directory held from <paramref name="root"/> - the writable root it was decided in - with no link on the way
    /// (<see cref="HeldDirectory"/>), the directories missing on the way made: a file made new in that directory, then
    /// renamed over the file there, a link at its place replaced as itself; what <paramref name="write"/> raises leaves the
    /// file as it was. Before a byte is written, the place it lands at - the file's name in the directory held, as the
    /// system names it - is decided again by <paramref name="refusal"/>, as the path was decided before. A link put on the
    /// way after that decision, or swapped in while it writes, reaches nothing. The one way an agent's file tools write a
    /// file, an edit copied through it among them.
    /// </summary>
    /// <param name="root">The writable root <paramref name="file"/> was decided in, as the decision spells it.</param>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="write">Writes what it is to hold, a little at a time.</param>
    /// <param name="refusal">Why a place may not be written, by its absolute path; null where it may.</param>
    /// <returns>Null where it was written; else why not, the file as it was.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public AgentPolicyAnswer.Denied? Write(string root, string file, Action<Stream> write, Func<string, AgentPolicyAnswer.Denied?> refusal)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(refusal);

        HeldDirectory held;

        try
        {
            held = _fileSystem.Hold(root, Path.GetDirectoryName(Path.GetFullPath(file))!, make: true, file);
        }
        catch (LinkOnTheWayException link)
        {
            return Linked(link);
        }

        using (held)
        {
            var landing = Path.Join(held.FinalPath(), Path.GetFileName(file));

            if (refusal(landing) is { } denied)
            {
                return denied with
                {
                    Reason = $"'{file}' would be written as '{landing}', where a link put on its way after it was decided leads; nothing was written: {denied.Reason}",
                };
            }

            held.Write(Path.GetFileName(file), write);
            return null;
        }
    }

    /// <summary>
    /// The refusal of a write or a removal a link put on its way since it was decided stopped (<see cref="LinkOnTheWayException"/>):
    /// by the rule that refuses a write through a link, nothing done.
    /// </summary>
    /// <param name="link">What stopped it.</param>
    public static AgentPolicyAnswer.Denied Linked(LinkOnTheWayException link)
    {
        ArgumentNullException.ThrowIfNull(link);

        return new AgentPolicyAnswer.Denied(ConfinedPaths.LinksRule, AgentPolicySource.Builtin, $"{link.Message}; nothing was done");
    }
}

/// <summary>A file read for an agent's tool, or why it was not: one or the other.</summary>
/// <param name="Bytes">What the file holds; null where it was refused.</param>
/// <param name="Denied">Why it was refused; null where it was read.</param>
public sealed record AgentFileRead(byte[]? Bytes, AgentPolicyAnswer.Denied? Denied);

/// <summary>The files under a directory, as a listing gives them, and how many it left out by their names.</summary>
/// <param name="Files">Every file, relative to the directory with forward separators, in ordinal order.</param>
/// <param name="Unnamed">
/// How many files were left out whose names no pattern reads: a '\' that reads as a separator where it is none, spelling
/// a '.' or '..' segment, or a name of spaces alone.
/// </param>
/// <param name="Unread">How many places under the directory could not be read: a directory beneath it, or what a listed path is.</param>
/// <param name="FirstUnread">The first of them and why; <see langword="null"/> where every place was read.</param>
public sealed record AgentListing(IReadOnlyList<string> Files, int Unnamed, int Unread, AgentSearchUnread? FirstUnread)
{
    /// <summary>The note that <paramref name="count"/> files the reader may not read were left out.</summary>
    /// <param name="count">How many.</param>
    public static string Refused(int count) => $"[{ReportText.Counted(count, "file")} you may not read were left out]";

    /// <summary>
    /// What the listing left out, a note each: the files whose names no pattern reads, and the places it could not read,
    /// with why the first was not.
    /// </summary>
    /// <param name="said">How a place is said, from its path relative to the directory listed.</param>
    public IEnumerable<string> LeftOut(Func<string, string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        if (Unnamed > 0)
        {
            yield return $"[{ReportText.Counted(Unnamed, "file")} whose names no pattern reads were left out]";
        }

        if (FirstUnread is { } first)
        {
            yield return first.Note(Unread, "place", said);
        }
    }
}

/// <summary>What one search found: its hits, how many more there were, and the files it did not read.</summary>
/// <param name="Hits">The hits given, file by file, each file's in its lines' order.</param>
/// <param name="More">How many more lines matched than were held; none for a search that hands each hit on as it is found.</param>
/// <param name="Refused">How many files the agent may not read were left out.</param>
/// <param name="Passed">How many files were passed over: binary, past the bound its caller gave, or no text this tool reads.</param>
/// <param name="Searched">How many files were read.</param>
/// <param name="Listing">The listing searched: what it left out by name, and the places it could not read (<see cref="AgentListing"/>).</param>
/// <param name="Unread">How many files could not be looked up or read.</param>
/// <param name="FirstUnread">The first of them and why; <see langword="null"/> where every file was read.</param>
public sealed record AgentSearch(
    IReadOnlyList<AgentSearchHit> Hits,
    long More,
    int Refused,
    int Passed,
    int Searched,
    AgentListing Listing,
    int Unread,
    AgentSearchUnread? FirstUnread)
{
    /// <summary>How many hits the search found, every one counted: those held and those past them, or those handed on.</summary>
    public long Found { get; init; }

    /// <summary>
    /// What the search did not read, a note each: the files the reader may not read, those passed over, what its listing
    /// left out, and the files it could not read, with why the first was not.
    /// </summary>
    /// <param name="said">How a file is said, from its path relative to the directory searched.</param>
    public IEnumerable<string> LeftOut(Func<string, string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        if (Refused > 0)
        {
            yield return AgentListing.Refused(Refused);
        }

        if (Passed > 0)
        {
            yield return $"[{ReportText.Counted(Passed, "file")} passed over: binary, or no text this tool reads]";
        }

        foreach (var note in Listing.LeftOut(said))
        {
            yield return note;
        }

        if (FirstUnread is { } first)
        {
            yield return first.Note(Unread, "file", said);
        }
    }
}

/// <summary>A place a listing or a search could not read, and why.</summary>
/// <param name="Path">The place, relative to the directory listed or searched with forward separators.</param>
/// <param name="Reason">Why, as the file system said it.</param>
public sealed record AgentSearchUnread(string Path, string Reason)
{
    /// <summary>The note that <paramref name="count"/> <paramref name="kind"/>s could not be read, and why this, the first, was not.</summary>
    /// <param name="count">How many could not be read.</param>
    /// <param name="kind">What they are: a file, a place.</param>
    /// <param name="said">How this place is said, from its path.</param>
    public string Note(int count, string kind, Func<string, string> said)
    {
        ArgumentNullException.ThrowIfNull(said);

        return $"[{ReportText.Counted(count, kind)} could not be read, the first '{said(Path)}': {Reason.TrimEnd('.')}]";
    }
}

/// <summary>A line a search matched, with the lines around it.</summary>
/// <param name="Path">Its file, relative to the directory searched with forward separators.</param>
/// <param name="Line">Its line number, from 1, as a file viewed counts them.</param>
/// <param name="Text">The line.</param>
/// <param name="Before">The lines before it given as context.</param>
/// <param name="After">The lines after it given as context.</param>
public sealed record AgentSearchHit(string Path, long Line, string Text, IReadOnlyList<AgentSearchLine> Before, IReadOnlyList<AgentSearchLine> After);

/// <summary>A line given around a hit.</summary>
/// <param name="Line">Its line number, from 1.</param>
/// <param name="Text">The line.</param>
public sealed record AgentSearchLine(long Line, string Text);

/// <summary>
/// A file an opener handed to a reader stood at a place the read's own refusal refuses (<see cref="AgentFiles.OpenedOrRefused"/>):
/// nothing of it read, and why, as the refusal said it.
/// </summary>
/// <param name="denied">Why it was refused.</param>
public sealed class AgentReadRefusedException(AgentPolicyAnswer.Denied denied) : IOException(denied.Reason)
{
    /// <summary>Why it was refused.</summary>
    public AgentPolicyAnswer.Denied Denied { get; } = denied;
}
