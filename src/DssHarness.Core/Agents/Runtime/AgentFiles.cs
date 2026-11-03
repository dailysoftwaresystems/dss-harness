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
/// link. A search reads text alone: a binary file, one past <see cref="MostSearchedBytes"/> and one that is no text this
/// tool reads are passed over and counted, and one that could not be read is counted apart, with why the first was not.
/// The one way the tools read and write a file's bytes besides (<see cref="ReadAsync"/>, <see cref="Write"/>): a read
/// through the file as it was opened, where it stands decided again before a byte passes; a write relative to its
/// directory held from its writable root with no link on the way (<see cref="HeldDirectory"/>). A directory is listed by
/// its path, so what a link put in place of one since leads to is what it lists - names, never a byte of a file, each file
/// read through its own open.
/// </summary>
/// <param name="git">Lists a work tree's files.</param>
/// <param name="fileSystem">Walks a directory git does not list, and reads and writes each file.</param>
/// <param name="clock">Times a pattern on each file's text.</param>
public sealed class AgentFiles(IGitClient git, IFileSystem fileSystem, TimeProvider clock)
{
    /// <summary>The largest file a search reads: two megabytes.</summary>
    public const long MostSearchedBytes = 2 * 1024 * 1024;

    /// <summary>How long a search's pattern may take on one file's text, its lines together, before the search is refused; each line's match bounded by it too.</summary>
    public static readonly TimeSpan PatternBound = TimeSpan.FromSeconds(2);

    /// <summary>The refusal a search's own decision stands for, where it only says whether a place may be read: counted, never said.</summary>
    private static readonly AgentPolicyAnswer.Denied NotToBeRead = new(ConfinedPaths.RootsRule, AgentPolicySource.Builtin, "a place the reader may not read");

    private readonly IGitClient _git = git ?? throw new ArgumentNullException(nameof(git));
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly TimeProvider _clock = clock ?? throw new ArgumentNullException(nameof(clock));

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
    /// does not counted as left out - read as text; at most <paramref name="most"/> hits, each with
    /// <paramref name="context"/> lines on either side, and how many more there were.
    /// </summary>
    /// <param name="directory">The directory, absolute.</param>
    /// <param name="pattern">The pattern, made with <see cref="PatternBound"/> as its timeout.</param>
    /// <param name="glob">The files searched, by their paths relative to <paramref name="directory"/>; null for every one.</param>
    /// <param name="comparison">How a path is compared with <paramref name="glob"/>.</param>
    /// <param name="context">How many lines before and after each hit are given.</param>
    /// <param name="most">The most hits given.</param>
    /// <param name="mayRead">Whether the agent may read a file, by its absolute path.</param>
    /// <param name="cancellationToken">Stops the search.</param>
    /// <exception cref="RegexMatchTimeoutException">The pattern took longer than <see cref="PatternBound"/> on a file.</exception>
    public async Task<AgentSearch> SearchAsync(
        string directory,
        Regex pattern,
        GlobPattern? glob,
        StringComparison comparison,
        int context,
        int most,
        Func<string, bool> mayRead,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(pattern);
        ArgumentNullException.ThrowIfNull(mayRead);
        ArgumentOutOfRangeException.ThrowIfNegative(context);
        ArgumentOutOfRangeException.ThrowIfLessThan(most, 1);

        var hits = new List<AgentSearchHit>();
        var more = 0;
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

            FileStanding? standing;
            byte[] bytes;

            try
            {
                standing = _fileSystem.StandingOf(file);

                // A file the listing names that is no longer there - gone since, or a deletion git still lists - is none to search.
                if (standing is null)
                {
                    continue;
                }

                if (standing.Length > MostSearchedBytes)
                {
                    passed++;
                    continue;
                }

                // Read through the file as it was opened, where it stands decided again as it was before the open: one a
                // link put on its way since leads to a place the reader may not read is left out as one it may not.
                var read = await ReadAsync(file, MostSearchedBytes, place => mayRead(place) ? null : NotToBeRead, cancellationToken).ConfigureAwait(false);

                if (read.Bytes is not { } held)
                {
                    refused++;
                    continue;
                }

                bytes = held;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Said apart from what was passed over by choice: a file not read may hold what was searched for.
                unread++;
                firstUnread ??= new AgentSearchUnread(path, ex.Message);
                continue;
            }

            if (EncodedText.Read(bytes) is not { IsRead: true } reading)
            {
                passed++;
                continue;
            }

            searched++;
            var lines = Codebase.CodeFileText.Lines(reading.Text.Text);
            var started = _clock.GetTimestamp();

            for (var at = 0; at < lines.Count; at++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // The file's lines share one bound: many lines each under it would otherwise hold the search for hours.
                if (_clock.GetElapsedTime(started) > PatternBound)
                {
                    throw new RegexMatchTimeoutException(lines[at], pattern.ToString(), PatternBound);
                }

                if (!pattern.IsMatch(lines[at]))
                {
                    continue;
                }

                if (hits.Count == most)
                {
                    more++;
                    continue;
                }

                var first = Math.Max(0, at - context);
                var last = Math.Min(lines.Count - 1, at + context);

                hits.Add(new AgentSearchHit(
                    path,
                    at + 1,
                    lines[at],
                    [.. Enumerable.Range(first, at - first).Select(line => new AgentSearchLine(line + 1, lines[line]))],
                    [.. Enumerable.Range(at + 1, last - at).Select(line => new AgentSearchLine(line + 1, lines[line]))]));
            }
        }

        return new AgentSearch(hits, more, refused, passed, searched, listing, unread, firstUnread);
    }

    /// <summary>
    /// The bytes of the file at <paramref name="file"/>, read through the file as it was opened - never by its path again -
    /// once the place the open file stands at (<see cref="IFileSystem.FinalPathOf"/>) is decided again by
    /// <paramref name="refusal"/>, as the path was decided before the open: a link put in place of the file, or of a
    /// directory on its way, after that decision reaches nothing the decision refuses. Opened never to wait - a pipe put
    /// there is refused before a byte is read (<see cref="IFileSystem.OpenRead"/>) - and read to <paramref name="most"/>
    /// bytes at most, however much it grew since it was measured. The one way an agent's file tools read a file's bytes. A
    /// second name of the same file - a hard link - is a place of its own, and decided as one.
    /// </summary>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="most">The most bytes read; a file holding more is refused, nothing of it given.</param>
    /// <param name="refusal">Why a place may not be read, by its absolute path; null where it may.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <returns>The bytes; or, where the open file stands at a place <paramref name="refusal"/> refuses, why - nothing read.</returns>
    /// <exception cref="IOException">The file could not be opened, named or read, is no regular file, or holds more than <paramref name="most"/> bytes.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not read it.</exception>
    public async Task<AgentFileRead> ReadAsync(string file, long most, Func<string, AgentPolicyAnswer.Denied?> refusal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(refusal);
        ArgumentOutOfRangeException.ThrowIfNegative(most);

        var stream = _fileSystem.OpenRead(file);

        await using (stream.ConfigureAwait(false))
        {
            var opened = _fileSystem.FinalPathOf(stream);

            if (refusal(opened) is { } denied)
            {
                return new AgentFileRead(null, denied with
                {
                    Reason = $"'{file}' was opened as '{opened}', where a link put on its way after it was decided leads; nothing was read: {denied.Reason}",
                });
            }

            // One byte past the bound is asked for, so a file grown past it since it was measured is told, never cut.
            var buffer = new byte[(int)Math.Min(most + 1, int.MaxValue)];
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);

            return read > most
                ? throw new IOException($"'{file}' holds more than {DiskSpace.Size(most)} now, past what it held when it was measured, and nothing of it was read")
                : new AgentFileRead(buffer[..read], null);
        }
    }

    /// <summary>
    /// Writes <paramref name="bytes"/> to <paramref name="file"/> whole and atomically, relative to its directory held from
    /// <paramref name="root"/> - the writable root it was decided in - with no link on the way (<see cref="HeldDirectory"/>),
    /// the directories missing on the way made: a file made new in that directory, then renamed over the file there, a link
    /// at its place replaced as itself. Before a byte is written, the place it lands at - the file's name in the directory
    /// held, as the system names it - is decided again by <paramref name="refusal"/>, as the path was decided before. A link
    /// put on the way after that decision, or swapped in while it writes, reaches nothing. The one way an agent's file tools
    /// write a file's bytes.
    /// </summary>
    /// <param name="root">The writable root <paramref name="file"/> was decided in, as the decision spells it.</param>
    /// <param name="file">The file, absolute, as it was decided.</param>
    /// <param name="bytes">What it is to hold.</param>
    /// <param name="refusal">Why a place may not be written, by its absolute path; null where it may.</param>
    /// <returns>Null where it was written; else why not, the file as it was.</returns>
    /// <exception cref="IOException">The file could not be written.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write it.</exception>
    public AgentPolicyAnswer.Denied? Write(string root, string file, byte[] bytes, Func<string, AgentPolicyAnswer.Denied?> refusal)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(bytes);
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

            held.Write(Path.GetFileName(file), stream => stream.Write(bytes));
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
/// <param name="More">How many more lines matched than were given.</param>
/// <param name="Refused">How many files the agent may not read were left out.</param>
/// <param name="Passed">How many files were passed over: binary, past the bound, or no text this tool reads.</param>
/// <param name="Searched">How many files were read.</param>
/// <param name="Listing">The listing searched: what it left out by name, and the places it could not read (<see cref="AgentListing"/>).</param>
/// <param name="Unread">How many files could not be looked up or read.</param>
/// <param name="FirstUnread">The first of them and why; <see langword="null"/> where every file was read.</param>
public sealed record AgentSearch(
    IReadOnlyList<AgentSearchHit> Hits,
    int More,
    int Refused,
    int Passed,
    int Searched,
    AgentListing Listing,
    int Unread,
    AgentSearchUnread? FirstUnread)
{
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
            yield return $"[{ReportText.Counted(Passed, "file")} passed over: binary, over {DiskSpace.Size(AgentFiles.MostSearchedBytes)}, or no text this tool reads]";
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
public sealed record AgentSearchHit(string Path, int Line, string Text, IReadOnlyList<AgentSearchLine> Before, IReadOnlyList<AgentSearchLine> After);

/// <summary>A line given around a hit.</summary>
/// <param name="Line">Its line number, from 1.</param>
/// <param name="Text">The line.</param>
public sealed record AgentSearchLine(int Line, string Text);
