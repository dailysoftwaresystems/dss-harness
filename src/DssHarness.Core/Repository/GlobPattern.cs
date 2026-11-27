using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <summary>
/// One path pattern as the agent clients' rules take it - a restriction's path, the glob of a file
/// search - and as a workflow's <c>codebaseGraph.include</c> and <c>codebaseGraph.exclude</c> say which
/// paths are graphed, or one entry of a list of a tree's own paths, read from its root - a step's
/// <c>writes</c> among them (<see cref="FromRoot"/>): read once and matched against many paths.
/// </summary>
/// <remarks>
/// <para>
/// The grammar of a rule the engine hands to a client as one of its own deny rules, which is
/// gitignore's: segments joined by <c>/</c>; <c>*</c> standing for any run of characters within one
/// segment and never across a separator; <c>?</c> for one character of a segment; <c>**</c> a whole
/// segment of its own, standing for any number of segments, leading, middle or trailing. A pattern
/// that holds a separator at its start or in its middle - a <c>./</c> prefix among them - is matched
/// from the root of what it is matched against; one that holds none, or only a trailing one, matches
/// that name at any depth. A trailing separator says the pattern names a directory: it matches only
/// what is beneath a directory it matches, and never a path's last segment itself, which is all a
/// rule asked about a path can know is not a directory. Everything else - brackets among them - is an
/// ordinary character, said here so nobody reads a character class into one.
/// </para>
/// <para>
/// Deliberately not <see cref="PathPatterns"/>, which stays the one matcher of the path lists of
/// <c>config.json</c> (<c>sync.neverTransfer</c>, <c>sync.exclude</c>, <c>lineEndings.exclude</c>, the
/// roots an anchor cites): that grammar is poorer on purpose - an entry is rooted and <c>*</c> is
/// refused - and no list of <c>config.json</c> is read by this, and no rule nor workflow pattern by
/// that. A pattern that would mean one thing as each of the two is a pattern nobody can read. What the two share is the spelling rule
/// (<see cref="PathPatterns.Misspelling"/>) and the rule that a path climbing out of the tree names
/// nothing in it (<see cref="PlatformPaths.ClimbsOut"/>), which a pattern here and a path matched
/// against it are both held to. The one list of <c>config.json</c> this does read is a test
/// invocation's <c>keepOnFailure</c>, whose entries name files a failing run kept by pattern - a
/// whole tree of them, at any depth, which the poorer grammar cannot say.
/// </para>
/// <para>
/// A trailing <c>/**</c> matches everything <em>beneath</em> what precedes it and not that path
/// itself, as gitignore reads it: a rule refuses what is written <em>in</em> a place, and a rule
/// that also matched the place would refuse a directory nobody can write.
/// </para>
/// <para>
/// A list of a tree's own paths is read the other way, from the tree's root (<see cref="FromRoot"/>): a work item's owned
/// and forbidden paths, the files an issue being fixed holds, a step's <c>writes</c> and a fold's <c>within</c> - the
/// plan's ownedPaths flow into the last two, so what the plan's check found no two items to share is what each item's
/// agent may write and its fold carry. There every entry is anchored at the root - a name with no separator is the
/// root's own, never one at any depth - and a path, an entry holding no wildcard, names that one path, holding what is
/// beneath it only where it names a directory: so the root's <c>Makefile</c> and <c>calc.c</c> never meet through a
/// <c>Makefile/calc.c</c> no tree can hold. Whether it names one is asked of one tree for a whole run, its start commit's
/// (<see cref="TreeDirectories"/>), and a refusal that reading causes says how to write what an entry once meant
/// (<see cref="WrittenFor(string, string, StringComparison)"/>).
/// </para>
/// </remarks>
public sealed class GlobPattern
{
    private readonly string[] _segments;
    private readonly bool _anchored;
    private readonly bool _directoriesOnly;

    /// <summary>
    /// Whether the pattern names its one path and nothing beneath it: a path read from the root (<see cref="FromRoot"/>)
    /// that names no directory. Such a pattern is anchored, its segments hold no wildcard, and it names no directory.
    /// </summary>
    private readonly bool _exact;

    private GlobPattern(string[] segments, bool anchored, bool directoriesOnly, bool exact = false)
    {
        _segments = segments;
        _anchored = anchored;
        _directoriesOnly = directoriesOnly;
        _exact = exact;
    }

    /// <summary>
    /// The pattern in the one spelling that reads back as the same matcher, by the reading that made it - <see cref="Parse"/>,
    /// or <see cref="FromRoot"/> asked of the same tree: its segments joined by <c>/</c>, led by one where the pattern is
    /// anchored and its one segment would not say so, and ended by one where it names a directory.
    /// </summary>
    public override string ToString()
        => (_anchored && _segments.Length == 1 ? "/" : string.Empty)
            + string.Join('/', _segments)
            + (_directoriesOnly ? "/" : string.Empty);

    /// <summary>
    /// Reads <paramref name="pattern"/> into a matcher.
    /// </summary>
    /// <param name="pattern">The pattern, as a rule or a tool's glob field gives it.</param>
    /// <exception cref="HarnessException">
    /// The pattern is empty or the root alone, holds a <c>.</c> or <c>..</c> segment or a doubled
    /// separator, or holds <c>**</c> inside a segment.
    /// </exception>
    public static GlobPattern Parse(string pattern)
    {
        var problem = Problem(pattern);

        if (problem is not null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"the pattern '{pattern}' {problem}");
        }

        // Read from the pattern as written, with either separator, before normalising takes the
        // separators that say these things away: one at its start or in its middle anchors it, and
        // one at its end says it names a directory.
        var written = pattern.Replace('\\', '/');

        return new GlobPattern(
            Folded(PathPatterns.Normalize(pattern).Split('/')),
            anchored: written.TrimEnd('/').Contains('/', StringComparison.Ordinal),
            directoriesOnly: written.EndsWith('/'));
    }

    /// <summary>
    /// Reads <paramref name="entry"/>, one entry of a list of a tree's own paths, from the root of that tree: a work item's
    /// owned or forbidden paths, the files an issue being fixed holds, a step's <c>writes</c>, a fold's <c>within</c> - the
    /// one reading of them all, for the plan's ownedPaths flow into the last two, and what the plan's check found no two
    /// items to share must be what each may write and fold. The entry is anchored at the root whatever it holds, so a name
    /// with no separator is the root's own, never one at any depth. A path - an entry holding no wildcard
    /// (<see cref="IsPattern"/>) - names that one path, and holds what is beneath it only where it names a directory: one
    /// <paramref name="namesDirectory"/> says the tree holds, or one written with a trailing separator, which names what is
    /// beneath it alone, as <see cref="Parse"/> reads one. A pattern keeps its meaning, anchored at the root: a directory
    /// it matches covers what is beneath it.
    /// </summary>
    /// <param name="entry">The entry, as the list writes it, with either separator.</param>
    /// <param name="namesDirectory">
    /// Whether a path relative to the tree's root, spelled with forward separators, names a directory there: of a run's
    /// lists, the answer of the one tree the run reads them against, its start commit's (<see cref="TreeDirectories"/>).
    /// </param>
    /// <exception cref="HarnessException">The entry cannot be read, as <see cref="Problem"/> says why.</exception>
    public static GlobPattern FromRoot(string entry, Func<string, bool> namesDirectory)
    {
        ArgumentNullException.ThrowIfNull(namesDirectory);

        var problem = Problem(entry);

        if (problem is not null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"the pattern '{entry}' {problem}");
        }

        var segments = Folded(PathPatterns.Normalize(entry).Split('/'));
        var directoriesOnly = entry.Replace('\\', '/').EndsWith('/');

        return new GlobPattern(
            segments,
            anchored: true,
            directoriesOnly,
            exact: !directoriesOnly && !IsPattern(entry) && !namesDirectory(string.Join('/', segments)));
    }

    /// <summary>
    /// How to write what <paramref name="written"/> - an entry this pattern was read from by <see cref="FromRoot"/> - meant
    /// before the reading from the root, where <paramref name="path"/>, which it does not match, lies where the entry would
    /// once have reached: beneath a path that names no directory - <c>'docs/generated' names a file: a directory to be made
    /// is written 'docs/generated/'</c> - or at a depth below the root an entry of one name matched at -
    /// <c>'*.md' names the root's own: '**/*.md' names any depth</c>. The one wording of the change, said by every refusal
    /// the reading from the root causes: a step's writes, a fold's within and the plan's check of a class's home and sites.
    /// </summary>
    /// <param name="written">The entry as written.</param>
    /// <param name="path">A path from the root the entry does not match.</param>
    /// <param name="comparison">How names are compared: the case rule of the tree the path is in.</param>
    /// <returns>The sentence; null where the entry matches the path, or would not have reached it either way.</returns>
    public string? WrittenFor(string written, string path, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(written);

        if (!Reads(path) || Matches(path, comparison))
        {
            return null;
        }

        var segments = PathPatterns.Normalize(path).Split('/');
        var entry = written.Replace('\\', '/');

        if (_exact && segments.Length > _segments.Length && _segments.Select((name, at) => string.Equals(name, segments[at], comparison)).All(same => same))
        {
            return $"'{written}' names a file: a directory to be made is written '{entry.TrimEnd('/')}/'";
        }

        return _segments.Length == 1 && !entry.TrimEnd('/').Contains('/', StringComparison.Ordinal) && Parse(written).Matches(path, comparison)
            ? $"'{written}' names the root's own: '**/{entry}' names any depth"
            : null;
    }

    /// <summary>
    /// What each of <paramref name="entries"/> - each read from the root (<see cref="FromRoot"/>), with the entry it was
    /// read from - says of how to write what it once meant where a path of <paramref name="paths"/> is matched by none of
    /// them (<see cref="WrittenFor(string, string, StringComparison)"/>), each sentence once, joined by <c>; </c>; null where
    /// none says anything.
    /// </summary>
    /// <param name="entries">The entries, each read and as written.</param>
    /// <param name="paths">Paths from the root, each one the entries leave out.</param>
    /// <param name="comparison">How names are compared.</param>
    public static string? WrittenFor(IEnumerable<(GlobPattern Read, string Written)> entries, IEnumerable<string> paths, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(entries);
        ArgumentNullException.ThrowIfNull(paths);

        var read = entries.ToList();
        var said = paths.SelectMany(path => read.Select(entry => entry.Read.WrittenFor(entry.Written, path, comparison))).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        return said.Count == 0 ? null : string.Join("; ", said);
    }

    /// <summary>
    /// <paramref name="segments"/> with each run of <c>**</c> made one: two runs of any number of segments side by side
    /// are one, and a trailing run of one or more after any number is one or more still. A matcher then goes no deeper
    /// into a pattern than into the path it is matched against.
    /// </summary>
    private static string[] Folded(string[] segments)
        => [.. segments.Where((segment, at) => !(segment == "**" && at + 1 < segments.Length && segments[at + 1] == "**"))];

    /// <summary>
    /// Whether <paramref name="entry"/> holds a wildcard, the two this grammar reads - <c>*</c> and <c>?</c> - and so
    /// is a pattern to match rather than a path to take as written: the one answer, for every reader that has to tell
    /// an entry naming a file from one naming whatever matches.
    /// </summary>
    /// <param name="entry">The entry, as written.</param>
    public static bool IsPattern(string entry)
        => entry.Contains('*', StringComparison.Ordinal) || entry.Contains('?', StringComparison.Ordinal);

    /// <summary>
    /// Why <paramref name="pattern"/> cannot be read, or <see langword="null"/> when it can.
    /// </summary>
    /// <param name="pattern">The pattern, as written.</param>
    /// <remarks>
    /// A <c>..</c> segment, which no path inside the tree holds, is refused by the rule that owns it,
    /// as the configured path lists refuse a path outside the tree; a misspelling - a <c>.</c> segment
    /// or a doubled separator, which the file system reads past - by the one spelling rule the two
    /// share. Both are asked of the pattern as written: a pattern that looks right and matches nothing
    /// is a rule that silently protects nothing.
    /// </remarks>
    public static string? Problem(string? pattern)
    {
        var normalized = PathPatterns.Normalize(pattern);

        if (normalized.Length == 0)
        {
            return "is empty";
        }

        if (normalized == ".")
        {
            return "is '.', the root itself, which no path in it is; write '**' for everything beneath it";
        }

        if (PlatformPaths.ClimbsOut(pattern!))
        {
            return "holds a '..' segment, which no path inside the tree holds, so it would name nothing; "
                + "name the path from the tree's root";
        }

        if (PathPatterns.Misspelling(pattern) is { } misspelled)
        {
            return misspelled;
        }

        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Contains("**", StringComparison.Ordinal) && segment != "**")
            {
                return "holds '**' inside a segment; '**' is a segment of its own, standing for any "
                    + "number of segments";
            }
        }

        return null;
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> is a path <see cref="Matches"/> reads: one inside the tree spelled as its
    /// owner spells one - neither blank nor the root alone, with no <c>.</c> or <c>..</c> segment or doubled separator, a
    /// <c>\</c> read as a separator. A name a POSIX system holds can fail it: <c>a\..\b</c> is one file there.
    /// </summary>
    /// <param name="relativePath">A path relative to what a pattern is matched against.</param>
    public static bool Reads(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return false;
        }

        var candidate = PathPatterns.Normalize(relativePath);

        return !(candidate.Length == 0 || candidate == "." || PlatformPaths.ClimbsOut(relativePath) || PathPatterns.Misspelling(relativePath) is not null);
    }

    /// <summary>
    /// Whether <paramref name="relativePath"/> is matched: whole path from the root where the
    /// pattern is anchored, any one segment of it where it is not - and so everything beneath a
    /// matched name, as gitignore reads it, which matches a directory by matching what is written
    /// inside it. A pattern naming a directory matches no path's last segment itself; a path read
    /// from the root that names no directory (<see cref="FromRoot"/>) matches itself alone.
    /// </summary>
    /// <param name="relativePath">
    /// A path relative to what the pattern is matched against, spelled either way: a leading
    /// separator, a <c>./</c> prefix and a trailing separator are read past, as they always are.
    /// </param>
    /// <param name="comparison">
    /// How a name in the pattern and a name in the path are compared: the case rule of the tree the
    /// path is in, <see cref="StringComparison.OrdinalIgnoreCase"/> where its file system folds case.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="relativePath"/> is blank, the root alone, or holds a <c>.</c> or <c>..</c>
    /// segment or a doubled separator - no path of the tree as its owner spells it, so whether it
    /// is matched is the caller's mistake to mend, never an answer to give; or
    /// <paramref name="comparison"/> is not ordinal, which no name of a path is compared by.
    /// </exception>
    public bool Matches(string relativePath, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"a path's names are compared ordinally, with or without case, never by {comparison}", nameof(comparison));
        }

        var candidate = PathPatterns.Normalize(relativePath);

        if (!Reads(relativePath))
        {
            throw new ArgumentException(
                $"'{relativePath}' is not a path inside the tree spelled as its owner spells one: it is the root alone, or "
                + "holds a '.' or '..' segment or a doubled separator",
                nameof(relativePath));
        }

        var segments = candidate.Split('/');

        // A pattern naming a directory matches what is beneath one, so the path's last segment is
        // never the match itself.
        var deepest = _directoriesOnly ? segments.Length - 1 : segments.Length;

        if (!_anchored)
        {
            // No separator in the pattern but a trailing one, so it names one segment, at any depth:
            // a name it matches anywhere in the path carries everything beneath it, which is how a
            // rule that names a directory refuses what is written inside it.
            return segments.Take(deepest).Any(segment => SegmentMatches(_segments[0], segment, comparison));
        }

        // A directory the pattern matches is matched all the way down, so each directory above the
        // path is tried as the match itself: 'src/cache/x.tmp' is covered by '**/cache' as
        // gitignore covers it, through the 'src/cache' the pattern matches. Every depth at once, each
        // place in the pattern against each place in the path decided once, in one pass from their
        // ends: the ways a pattern of many '**' splits a path among them are past counting, the places
        // are not.
        var covers = new bool[_segments.Length + 1, deepest + 1];

        // The pattern spent, what is left of the path is covered: all of it beneath a matched name, and none of it where
        // the pattern names its one path and nothing beneath.
        for (var candidateIndex = _exact ? deepest : 1; candidateIndex <= deepest; candidateIndex++)
        {
            covers[_segments.Length, candidateIndex] = true;
        }

        for (var patternIndex = _segments.Length - 1; patternIndex >= 0; patternIndex--)
        {
            for (var candidateIndex = deepest; candidateIndex >= 0; candidateIndex--)
            {
                covers[patternIndex, candidateIndex] = _segments[patternIndex] == "**"

                    // A trailing '**' stands for one segment or more, never for the path itself: see the
                    // class remarks. A '**' anywhere else stands for any number of segments, none included.
                    ? patternIndex == _segments.Length - 1
                        ? candidateIndex < deepest
                        : covers[patternIndex + 1, candidateIndex] || (candidateIndex < deepest && covers[patternIndex, candidateIndex + 1])
                    : candidateIndex < deepest
                        && SegmentMatches(_segments[patternIndex], segments[candidateIndex], comparison)
                        && covers[patternIndex + 1, candidateIndex + 1];
            }
        }

        return covers[0, 0];
    }

    /// <summary>
    /// How far a path read one segment at a time from its start has gone in being matched, as <see cref="Matches"/> reads
    /// the whole path: the places in the pattern its segments so far may stand at, each <c>**</c> that may take no segment
    /// passed; whether the pattern was matched by a directory above the segment read last; and whether by the path read so
    /// far itself. Two paths whose readings stand alike are matched alike by every continuation of them, so a reader that
    /// follows many spellings of one path at once - each segment of it one of several names - keeps one spelling of each
    /// way the pattern can stand, never every spelling (the agents' policy, reading a Windows short name as each name it
    /// may shorten). Read by <see cref="Begin"/>, <see cref="Next"/> and <see cref="Ends"/>, which decide what
    /// <see cref="Matches"/> decides of the same path.
    /// </summary>
    /// <param name="Places">The places in the pattern, ascending, joined by commas; empty once a directory above is matched.</param>
    /// <param name="MatchedAbove">Whether a directory above the segment read last - a path of fewer segments - is matched.</param>
    /// <param name="MatchedHere">Whether the segments read so far are matched.</param>
    internal readonly record struct Progress(string Places, bool MatchedAbove, bool MatchedHere);

    /// <summary>Where a path stands before its first segment is read (<see cref="Progress"/>).</summary>
    internal Progress Begin() => new(_anchored ? Joined(Passed([0])) : string.Empty, MatchedAbove: false, MatchedHere: false);

    /// <summary>
    /// Where a path stands once <paramref name="segment"/> is read after <paramref name="progress"/>: a segment's pattern
    /// it matches passed, a <c>**</c> taking it, a trailing <c>**</c> - one segment or more - matched by it.
    /// </summary>
    /// <param name="progress">Where the path stood.</param>
    /// <param name="segment">Its next segment.</param>
    /// <param name="comparison">How names are compared, as <see cref="Matches"/> compares them.</param>
    internal Progress Next(Progress progress, string segment, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(segment);

        var above = progress.MatchedAbove || progress.MatchedHere;

        if (above && _exact)
        {
            // A path that names nothing beneath it is past once a segment follows it: nothing deeper is matched.
            return new Progress(string.Empty, MatchedAbove: false, MatchedHere: false);
        }

        if (above)
        {
            // A matched directory is matched all the way down: nothing below it changes that.
            return new Progress(string.Empty, MatchedAbove: true, MatchedHere: false);
        }

        if (!_anchored)
        {
            return new Progress(string.Empty, MatchedAbove: false, MatchedHere: SegmentMatches(_segments[0], segment, comparison));
        }

        var reached = new HashSet<int>();
        var matched = false;

        foreach (var place in progress.Places.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse))
        {
            if (place >= _segments.Length)
            {
                continue;
            }

            if (_segments[place] == "**")
            {
                // A trailing '**' is matched by one segment or more; any other takes this one and stays.
                matched |= place == _segments.Length - 1;
                reached.Add(place);
            }
            else if (SegmentMatches(_segments[place], segment, comparison))
            {
                matched |= place + 1 == _segments.Length;
                reached.Add(place + 1);
            }
        }

        return new Progress(Joined(Passed(reached)), MatchedAbove: false, MatchedHere: matched);
    }

    /// <summary>
    /// Whether the path read to <paramref name="progress"/> is matched, as <see cref="Matches"/> decides it: a directory
    /// above it matched, or - where the pattern does not name a directory, which matches only what lies beneath one - the
    /// path itself. A pattern that names its one path and nothing beneath it is never matched above.
    /// </summary>
    internal bool Ends(Progress progress) => progress.MatchedAbove || (!_directoriesOnly && progress.MatchedHere);

    /// <summary>The places <paramref name="places"/> stand for once each <c>**</c> but a trailing one is passed, taking no segment.</summary>
    private SortedSet<int> Passed(IEnumerable<int> places)
    {
        var passed = new SortedSet<int>(places);
        var waiting = new Stack<int>(passed);

        while (waiting.TryPop(out var place))
        {
            if (place < _segments.Length - 1 && _segments[place] == "**" && passed.Add(place + 1))
            {
                waiting.Push(place + 1);
            }
        }

        return passed;
    }

    /// <summary>The places, ascending, joined by commas: a progress's own spelling of them.</summary>
    private static string Joined(SortedSet<int> places) => string.Join(',', places);

    /// <summary>
    /// Whether some path is matched by this pattern and by <paramref name="other"/> both - one that exists or one that
    /// does not yet - so two patterns that would each let a different owner write a file are told apart from two that
    /// never meet. A path read from the root that names no directory (<see cref="FromRoot"/>) holds nothing beneath it,
    /// for no path is beneath a file - unless the other names only paths beneath it, by its own names, which are real only
    /// where it is a directory: <c>src/new</c> and <c>src/new/a.cs</c> name one place, and meet.
    /// </summary>
    /// <param name="other">The other pattern.</param>
    /// <param name="comparison">How names are compared: the case rule of the tree the paths would be in.</param>
    /// <exception cref="ArgumentException"><paramref name="comparison"/> is not ordinal.</exception>
    public bool Overlaps(GlobPattern other, StringComparison comparison = StringComparison.Ordinal)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (comparison is not (StringComparison.Ordinal or StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"a path's names are compared ordinally, with or without case, never by {comparison}", nameof(comparison));
        }

        var mine = Spelled(holdsBeneath: !_exact || other.LiesBeneath(this, comparison));
        var theirs = other.Spelled(holdsBeneath: !other._exact || LiesBeneath(other, comparison));
        var seen = new bool?[mine.Count + 1, theirs.Count + 1];

        return Meet(0, 0);

        // Whether what is left of the two can still be one path: a run of any segments on either side takes nothing or
        // takes one of the other's, and two names meet where one name both would take.
        bool Meet(int at, int against)
        {
            if (seen[at, against] is { } known)
            {
                return known;
            }

            var meets = (at == mine.Count && against == theirs.Count)
                || (at < mine.Count && mine[at] is null && (Meet(at + 1, against) || (against < theirs.Count && Meet(at, against + 1))))
                || (against < theirs.Count && theirs[against] is null && (Meet(at, against + 1) || (at < mine.Count && Meet(at + 1, against))))
                || (at < mine.Count && against < theirs.Count
                    && mine[at] is { } name && theirs[against] is { } theirName
                    && NamesMeet(name, theirName, comparison)
                    && Meet(at + 1, against + 1));

            seen[at, against] = meets;

            return meets;
        }
    }

    /// <summary>
    /// Whether every path this pattern matches lies beneath the one path <paramref name="path"/> names, by names of its
    /// own: anchored, and its first segments the path's own names, each written out - no wildcard among them - and
    /// followed by more of it, or by the separator that says it names a directory.
    /// </summary>
    /// <param name="path">A pattern that names one path and nothing beneath it.</param>
    /// <param name="comparison">How names are compared.</param>
    private bool LiesBeneath(GlobPattern path, StringComparison comparison)
        => _anchored
            && (_segments.Length > path._segments.Length || (_segments.Length == path._segments.Length && _directoriesOnly))
            && path._segments.Select((name, at) => !IsPattern(_segments[at]) && string.Equals(name, _segments[at], comparison)).All(same => same);

    /// <summary>
    /// The paths this pattern matches, spelled whole from the root: each name a segment's pattern, and null for a run of
    /// any number of segments. A pattern that is not anchored is preceded by such a run; what a matched directory holds
    /// follows, one segment or more where the pattern names a directory or ends in <c>**</c>, any number otherwise - and
    /// none where <paramref name="holdsBeneath"/> says the pattern holds nothing beneath its one path.
    /// </summary>
    /// <param name="holdsBeneath">Whether what is beneath a matched path is matched too: false only for a pattern that names its one path.</param>
    private List<string?> Spelled(bool holdsBeneath)
    {
        var spelled = new List<string?>();

        if (!_anchored)
        {
            spelled.Add(null);
        }

        for (var at = 0; at < _segments.Length; at++)
        {
            // A trailing '**' stands for one segment or more, as Covers reads it; anywhere else, for any number.
            if (_segments[at] == "**" && at < _segments.Length - 1)
            {
                spelled.Add(null);
            }
            else if (_segments[at] == "**")
            {
                spelled.Add("*");
            }
            else
            {
                spelled.Add(_segments[at]);
            }
        }

        if (_directoriesOnly)
        {
            spelled.Add("*");
        }

        if (holdsBeneath)
        {
            spelled.Add(null);
        }

        return spelled;
    }

    /// <summary>Whether one name is matched by two segments' patterns both, under <c>*</c> and <c>?</c>.</summary>
    private static bool NamesMeet(string left, string right, StringComparison comparison)
    {
        var seen = new bool?[left.Length + 1, right.Length + 1];

        return Meet(0, 0);

        bool Meet(int at, int against)
        {
            if (seen[at, against] is { } known)
            {
                return known;
            }

            var meets = (at == left.Length && against == right.Length)
                || (at < left.Length && left[at] == '*' && (Meet(at + 1, against) || (against < right.Length && Meet(at, against + 1))))
                || (against < right.Length && right[against] == '*' && (Meet(at, against + 1) || (at < left.Length && Meet(at + 1, against))))
                || (at < left.Length && against < right.Length
                    && left[at] != '*' && right[against] != '*'
                    && (left[at] == '?' || right[against] == '?' || Same(left[at], right[against], comparison))
                    && Meet(at + 1, against + 1));

            seen[at, against] = meets;

            return meets;
        }
    }

    /// <summary>Whether one segment matches another under <c>*</c> and <c>?</c>, and nothing else.</summary>
    private static bool SegmentMatches(string pattern, string candidate, StringComparison comparison)
    {
        var patternAt = 0;
        var candidateAt = 0;
        var star = -1;
        var starAt = 0;

        while (candidateAt < candidate.Length)
        {
            // A '*' of the pattern is one before it is a character: a name may hold a '*' of its own, which it meets as any
            // other character, never as the place a run begins.
            if (patternAt < pattern.Length && pattern[patternAt] == '*')
            {
                star = patternAt++;
                starAt = candidateAt;
            }
            else if (patternAt < pattern.Length && (pattern[patternAt] == '?' || Same(pattern[patternAt], candidate[candidateAt], comparison)))
            {
                patternAt++;
                candidateAt++;
            }
            else if (star >= 0)
            {
                patternAt = star + 1;
                candidateAt = ++starAt;
            }
            else
            {
                return false;
            }
        }

        while (patternAt < pattern.Length && pattern[patternAt] == '*')
        {
            patternAt++;
        }

        return patternAt == pattern.Length;
    }

    /// <summary>
    /// Whether two characters of a name are the same under <paramref name="comparison"/>, compared as
    /// two names of one character each are, so a pattern folds case exactly as a comparison of whole
    /// names does.
    /// </summary>
    private static bool Same(char pattern, char candidate, StringComparison comparison)
        => new ReadOnlySpan<char>(in pattern).Equals(new ReadOnlySpan<char>(in candidate), comparison);
}
