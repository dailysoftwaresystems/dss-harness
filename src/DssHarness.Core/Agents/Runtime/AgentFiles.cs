using System.Text.RegularExpressions;
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
/// </summary>
/// <param name="git">Lists a work tree's files.</param>
/// <param name="fileSystem">Walks a directory git does not list, and reads each file searched.</param>
/// <param name="clock">Times a pattern on each file's text.</param>
public sealed class AgentFiles(IGitClient git, IFileSystem fileSystem, TimeProvider clock)
{
    /// <summary>The largest file a search reads: two megabytes.</summary>
    public const long MostSearchedBytes = 2 * 1024 * 1024;

    /// <summary>How long a search's pattern may take on one file's text, its lines together, before the search is refused; each line's match bounded by it too.</summary>
    public static readonly TimeSpan PatternBound = TimeSpan.FromSeconds(2);

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

                bytes = await _fileSystem.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
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
}

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
