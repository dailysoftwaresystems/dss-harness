using System.Globalization;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// Where a directory is renamed to while it is removed: beside it, hidden, and named as nothing a build or a sync makes
/// is. A removal renames first, which is done at once and under whatever lock keeps a run out, and deletes after; one
/// that stops part way leaves the directory aside, for whatever next removes it to finish.
/// </summary>
/// <remarks>
/// A build directory's removal names its aside by the process removing it (<see cref="Of(string, int)"/>), so the process
/// that moved a directory is the only one that deletes what it moved: two processes removing one build directory - a
/// clean of it and another tree's leg making room - each delete only their own, and neither counts what the other freed.
/// The aside named by no process, which builds before this one wrote and every other removal still writes, is read as an
/// aside forever.
/// </remarks>
public static class RemovalAside
{
    /// <summary>What follows the directory's own name in its aside's, after a dot before it.</summary>
    public const string Suffix = ".removing";

    /// <summary>What parts <see cref="Suffix"/> from the id of the process an aside is named by.</summary>
    private const char ByProcess = '-';

    /// <summary>Where <paramref name="directory"/> is renamed to while it is removed.</summary>
    /// <param name="directory">The directory.</param>
    public static string Of(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        return Path.Combine(Path.GetDirectoryName(whole)!, "." + Path.GetFileName(whole) + Suffix);
    }

    /// <summary>
    /// Where <paramref name="directory"/> is renamed to while process <paramref name="processId"/> removes it: an aside of
    /// that process's own, which it alone deletes.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="processId">The process removing it.</param>
    public static string Of(string directory, int processId)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        return Of(directory) + ByProcess + processId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The name of the directory an aside named <paramref name="name"/> was, or <see langword="null"/> where
    /// <paramref name="name"/> is no aside's: named by a process or by none.
    /// </summary>
    /// <param name="name">A directory's own name, with no path before it.</param>
    public static string? Was(string name) => Read(name)?.Was;

    /// <summary>
    /// The process whose removal moved a directory to the aside named <paramref name="name"/>, or <see langword="null"/>
    /// where the aside is named by none - one a build before this one, or a removal of another kind, left - or is no aside.
    /// </summary>
    /// <param name="name">A directory's own name, with no path before it.</param>
    public static int? By(string name) => Read(name)?.By;

    /// <summary>What an aside's name says: the directory it was, and the process that moved it there where one is named.</summary>
    private static (string Was, int? By)? Read(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var at = name.LastIndexOf(Suffix, StringComparison.Ordinal);

        if (name.Length < 2 || name[0] != '.' || at <= 1)
        {
            return null;
        }

        var after = name[(at + Suffix.Length)..];

        if (after.Length == 0)
        {
            return (name[1..at], null);
        }

        return after[0] == ByProcess
            && int.TryParse(after[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var process)
            && process > 0
            && string.Equals(after[1..], process.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
                ? (name[1..at], process)
                : null;
    }
}
