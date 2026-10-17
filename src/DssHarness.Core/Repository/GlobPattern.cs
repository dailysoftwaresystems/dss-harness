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
/// segment of its own, standing for any number of segments, leading, middle or trailing; a pattern
/// that holds a separator anywhere in it - a leading or trailing one included, and a <c>./</c>
/// prefix among them - is matched from the root of what it is matched against, one that holds none
/// matches that name at any depth. Everything else - brackets among them - is an ordinary
/// character, said here so nobody reads a character class into one.
/// </para>
/// <para>
/// Deliberately not <see cref="PathPatterns"/>, which stays the one matcher of the configured path
/// lists (<c>sync.neverTransfer</c>, <c>sync.exclude</c>, <c>lineEndings.exclude</c>, the roots an
/// anchor cites): that grammar is poorer on purpose - an entry is rooted and <c>*</c> is refused -
/// and no configured list is read by this and no rule by that. A pattern that would mean one thing
/// as each of the two is a pattern nobody can read.
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

    private GlobPattern(string[] segments, bool anchored)
    {
        _segments = segments;
        _anchored = anchored;
    }

    /// <summary>The pattern as it was normalised, which is how it matches.</summary>
    public override string ToString() => string.Join('/', _segments);

    /// <summary>
    /// Reads <paramref name="pattern"/> into a matcher.
    /// </summary>
    /// <param name="pattern">The pattern, as a rule or a tool's glob field gives it.</param>
    /// <exception cref="HarnessException">
    /// The pattern is empty, holds a <c>.</c> segment or a doubled separator, or holds <c>**</c>
    /// inside a segment.
    /// </exception>
    public static GlobPattern Parse(string pattern)
    {
        var problem = Problem(pattern);

        if (problem is not null)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"the pattern '{pattern}' {problem}");
        }

        var normalized = PathPatterns.Normalize(pattern)!;

        return new GlobPattern(normalized.Split('/'), Anchored(pattern));
    }

    /// <summary>
    /// Why <paramref name="pattern"/> cannot be read, or <see langword="null"/> when it can.
    /// </summary>
    /// <param name="pattern">The pattern, as written.</param>
    /// <remarks>
    /// A misspelling that the file system would read past - a <c>.</c> segment, a doubled
    /// separator - is refused as the configured path lists refuse it, by the one spelling rule the
    /// two share: a pattern that looks right and matches nothing is a rule that silently protects
    /// nothing.
    /// </remarks>
    public static string? Problem(string? pattern)
    {
        var normalized = PathPatterns.Normalize(pattern);

        if (normalized.Length == 0)
        {
            return "is empty";
        }

        if (PathPatterns.Misspelling(normalized) is { } misspelled)
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
    /// pattern holds a separator, any one segment of it where the pattern holds none - and so
    /// everything beneath a matched name, as gitignore reads it, which matches a directory by
    /// matching what is written inside it.
    /// </summary>
    /// <param name="relativePath">A path relative to what the pattern is matched against, spelled either way.</param>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is null or blank.</exception>
    public bool Matches(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        var candidate = PathPatterns.Normalize(relativePath);

        if (candidate.Length == 0)
        {
            return false;
        }

        var segments = candidate.Split('/');

        if (!_anchored)
        {
            // No separator in the pattern, so it names one segment, at any depth: a name it matches
            // anywhere in the path carries everything beneath it, which is how a rule that names a
            // directory refuses what is written inside it.
            return segments.Any(segment => SegmentMatches(_segments[0], segment));
        }

        // A directory the pattern matches is matched all the way down, so each directory above the
        // path is tried as the match itself: 'src/cache/x.tmp' is covered by '**/cache' as
        // gitignore covers it, through the 'src/cache' the pattern matches.
        for (var depth = 1; depth <= segments.Length; depth++)
        {
            if (Covers(0, 0, segments, depth))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the pattern as written is matched from the root: a separator anywhere in it says so,
    /// a leading or trailing one included, as gitignore reads it - the leading one is normalised
    /// away and the anchoring decided before it is.
    /// </summary>
    private static bool Anchored(string pattern)
        => pattern.Replace('\\', '/').Trim().IndexOf('/') >= 0;

    /// <summary>
    /// Whether <paramref name="candidate"/>'s segments from <paramref name="candidateIndex"/> up to
    /// <paramref name="depth"/> match the pattern's from <paramref name="patternIndex"/> to their
    /// ends.
    /// </summary>
    private bool Covers(int patternIndex, int candidateIndex, string[] candidate, int depth)
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
                if (Covers(patternIndex + 1, taken, candidate, depth))
                {
                    return true;
                }
            }

            return false;
        }

        return candidateIndex < depth
            && SegmentMatches(_segments[patternIndex], candidate[candidateIndex])
            && Covers(patternIndex + 1, candidateIndex + 1, candidate, depth);
    }

    /// <summary>Whether one segment matches another under <c>*</c> and <c>?</c>, and nothing else.</summary>
    private static bool SegmentMatches(string pattern, string candidate)
    {
        var patternAt = 0;
        var candidateAt = 0;
        var star = -1;
        var starAt = 0;

        while (candidateAt < candidate.Length)
        {
            if (patternAt < pattern.Length && (pattern[patternAt] == '?' || pattern[patternAt] == candidate[candidateAt]))
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
}
