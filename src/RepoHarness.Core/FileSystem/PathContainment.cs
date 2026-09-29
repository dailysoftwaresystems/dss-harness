namespace RepoHarness.Core.FileSystem;

/// <summary>Decides whether one path lies beneath another, or is the same path.</summary>
public static class PathContainment
{
    /// <summary>
    /// Whether <paramref name="candidate"/> lies strictly beneath <paramref name="root"/>.
    /// </summary>
    /// <remarks>
    /// Both paths are fully resolved first, so <c>..</c> segments cannot escape, and a
    /// separator is required at the boundary: a bare prefix test would also accept a
    /// sibling whose name merely starts with the same characters. A root is not beneath
    /// itself. This exists to guard a recursive delete, so every doubtful case answers no.
    /// </remarks>
    /// <param name="root">Directory the candidate must be inside.</param>
    /// <param name="candidate">Path being checked.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool IsStrictlyInside(string root, string candidate, StringComparison comparison)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate);

        var resolvedRoot = Whole(root);
        var resolvedCandidate = Whole(candidate);

        if (resolvedCandidate.Length <= resolvedRoot.Length
            || !resolvedCandidate.StartsWith(resolvedRoot, comparison))
        {
            return false;
        }

        // A filesystem root such as "C:\" or "/" keeps its separator after trimming, so
        // there the boundary is already the last character of the root itself.
        return IsSeparator(resolvedRoot[^1]) || IsSeparator(resolvedCandidate[resolvedRoot.Length]);
    }

    /// <summary>Whether <paramref name="left"/> and <paramref name="right"/> name the same path, once each is resolved.</summary>
    /// <remarks>
    /// Compared as spelled, links not followed: a caller comparing with a path git resolved resolves its own first.
    /// </remarks>
    /// <param name="left">One path.</param>
    /// <param name="right">The other.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool AreSame(string left, string right, StringComparison comparison)
        => string.Equals(Whole(left), Whole(right), comparison);

    /// <summary>Whether <paramref name="candidate"/> is <paramref name="root"/> itself or lies beneath it.</summary>
    /// <param name="root">The directory.</param>
    /// <param name="candidate">Path being checked.</param>
    /// <param name="comparison">How this platform compares paths.</param>
    public static bool IsInsideOrSame(string root, string candidate, StringComparison comparison)
        => AreSame(root, candidate, comparison) || IsStrictlyInside(root, candidate, comparison);

    private static string Whole(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsSeparator(char character)
        => character == Path.DirectorySeparatorChar || character == Path.AltDirectorySeparatorChar;
}
