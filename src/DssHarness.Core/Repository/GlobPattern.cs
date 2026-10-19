using DssHarness.Core.Platform;
using DssHarness.Core.Results;

namespace DssHarness.Core.Repository;

/// <summary>
/// One path pattern as the agent clients' rules take it - a restriction's path, a step's
/// <c>writes</c>, the glob of a file search - read once and matched against many paths.
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
/// Deliberately not <see cref="PathPatterns"/>, which stays the one matcher of the configured path
/// lists (<c>sync.neverTransfer</c>, <c>sync.exclude</c>, <c>lineEndings.exclude</c>, the roots an
/// anchor cites): that grammar is poorer on purpose - an entry is rooted and <c>*</c> is refused -
/// and no configured list is read by this and no rule by that. A pattern that would mean one thing
/// as each of the two is a pattern nobody can read. What the two share is the spelling rule
/// (<see cref="PathPatterns.Misspelling"/>) and the rule that a path climbing out of the tree names
/// nothing in it (<see cref="PlatformPaths.ClimbsOut"/>), which a pattern here and a path matched
/// against it are both held to.
/// </para>
/// <para>
/// A trailing <c>/**</c> matches everything <em>beneath</em> what precedes it and not that path
/// itself, as gitignore reads it: a rule refuses what is written <em>in</em> a place, and a rule
/// that also matched the place would refuse a directory nobody can write.
/// </para>
/// </remarks>
public sealed class GlobPattern
{
    private readonly string[] _segments;
    private readonly bool _anchored;
    private readonly bool _directoriesOnly;

    private GlobPattern(string[] segments, bool anchored, bool directoriesOnly)
    {
        _segments = segments;
        _anchored = anchored;
        _directoriesOnly = directoriesOnly;
    }

    /// <summary>
    /// The pattern in the one spelling that reads back as the same matcher: its segments joined by
    /// <c>/</c>, led by one where the pattern is anchored and its one segment would not say so, and
    /// ended by one where it names a directory.
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
            PathPatterns.Normalize(pattern).Split('/'),
            anchored: written.TrimEnd('/').Contains('/', StringComparison.Ordinal),
            directoriesOnly: written.EndsWith('/'));
    }

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
    /// Whether <paramref name="relativePath"/> is matched: whole path from the root where the
    /// pattern is anchored, any one segment of it where it is not - and so everything beneath a
    /// matched name, as gitignore reads it, which matches a directory by matching what is written
    /// inside it. A pattern naming a directory matches no path's last segment itself.
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

        if (candidate.Length == 0 || candidate == "." || PlatformPaths.ClimbsOut(relativePath) || PathPatterns.Misspelling(relativePath) is not null)
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
        // gitignore covers it, through the 'src/cache' the pattern matches.
        for (var depth = 1; depth <= deepest; depth++)
        {
            if (Covers(0, 0, segments, depth, comparison))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/>'s segments from <paramref name="candidateIndex"/> up to
    /// <paramref name="depth"/> match the pattern's from <paramref name="patternIndex"/> to their
    /// ends.
    /// </summary>
    private bool Covers(int patternIndex, int candidateIndex, string[] candidate, int depth, StringComparison comparison)
    {
        if (patternIndex == _segments.Length)
        {
            return candidateIndex == depth;
        }

        if (_segments[patternIndex] == "**")
        {
            if (patternIndex == _segments.Length - 1)
            {
                // A trailing '**' stands for one segment or more, never for the path itself: see the
                // class remarks.
                return candidateIndex < depth;
            }

            // A '**' anywhere else stands for any number of segments, none included.
            for (var taken = candidateIndex; taken <= depth; taken++)
            {
                if (Covers(patternIndex + 1, taken, candidate, depth, comparison))
                {
                    return true;
                }
            }

            return false;
        }

        return candidateIndex < depth
            && SegmentMatches(_segments[patternIndex], candidate[candidateIndex], comparison)
            && Covers(patternIndex + 1, candidateIndex + 1, candidate, depth, comparison);
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
            if (patternAt < pattern.Length && (pattern[patternAt] == '?' || Same(pattern[patternAt], candidate[candidateAt], comparison)))
            {
                patternAt++;
                candidateAt++;
            }
            else if (patternAt < pattern.Length && pattern[patternAt] == '*')
            {
                star = patternAt++;
                starAt = candidateAt;
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
