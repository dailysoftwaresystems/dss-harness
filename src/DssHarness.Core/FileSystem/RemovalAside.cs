using System.Globalization;
using System.Text;

namespace DssHarness.Core.FileSystem;

/// <summary>
/// The process whose removal moved a directory aside, as the aside's name says it.
/// </summary>
/// <param name="ProcessId">Its id.</param>
/// <param name="Stamp">
/// What tells it from a later holder of its id (<see cref="Execution.IProcessIdentity.Current"/>), as it said it; absent where
/// it would not say, and in the asides of the builds before this one, which named a process by its id alone.
/// </param>
public sealed record AsideRemover(int ProcessId, string? Stamp);

/// <summary>
/// Where a directory is renamed to while it is removed: beside it, hidden, and named as nothing a build or a sync makes
/// is. A removal renames first, which is done at once and under whatever lock keeps a run out, and deletes after; one
/// that stops part way leaves the directory aside, for whatever next removes it to finish.
/// </summary>
/// <remarks>
/// A build directory's removal names its aside by the process removing it (<see cref="Of(string, int, string?)"/>) - its id,
/// and the stamp that tells it from a later holder of that id, as the run lock's and the slots' records name a holder - so
/// the process that moved a directory is the only one that deletes what it moved: two processes removing one build
/// directory - a clean of it and another tree's leg making room - each delete only their own, and neither counts what the
/// other freed. Named by the id alone, a removal killed part way left an aside every later removal passed over as a running
/// process's for as long as any process carried its id, which on Windows comes back within minutes. The aside named by no
/// process, which builds before this one wrote and every other removal still writes, and one named by an id alone, which
/// builds before this one wrote, are read as asides forever.
/// </remarks>
public static class RemovalAside
{
    /// <summary>What follows the directory's own name in its aside's, after a dot before it.</summary>
    public const string Suffix = ".removing";

    /// <summary>What parts <see cref="Suffix"/> from the id of the process an aside is named by, and that id from its stamp.</summary>
    private const char ByProcess = '-';

    /// <summary>What begins a character of a stamp spelt by its code, as a name cannot hold it as it is.</summary>
    private const char Escape = '_';

    /// <summary>Where <paramref name="directory"/> is renamed to while it is removed.</summary>
    /// <param name="directory">The directory.</param>
    public static string Of(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        return Path.Combine(Path.GetDirectoryName(whole)!, "." + Path.GetFileName(whole) + Suffix);
    }

    /// <summary>
    /// Where <paramref name="directory"/> is renamed to while process <paramref name="processId"/> - started as
    /// <paramref name="stamp"/> says - removes it: an aside of that process's own, which it alone deletes. The stamp is spelt
    /// so any file system holds it: each letter, digit and dash as it is, and every other character as <c>_</c> and its
    /// four-digit code.
    /// </summary>
    /// <param name="directory">The directory.</param>
    /// <param name="processId">The process removing it.</param>
    /// <param name="stamp">What tells that process from a later holder of its id; <see langword="null"/> where the platform would not say.</param>
    public static string Of(string directory, int processId, string? stamp)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(processId);

        var named = Of(directory) + ByProcess + processId.ToString(CultureInfo.InvariantCulture);

        return stamp is { Length: > 0 } ? named + ByProcess + Spelt(stamp) : named;
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
    public static AsideRemover? By(string name) => Read(name)?.By;

    /// <summary>What an aside's name says: the directory it was, and the process that moved it there where one is named.</summary>
    private static (string Was, AsideRemover? By)? Read(string name)
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

        if (after[0] != ByProcess)
        {
            return null;
        }

        // The id, then, where one was said, its stamp after the next dash: each spelt as this class spells it, and nothing else.
        var parts = after[1..].Split(ByProcess, 2);
        var stamp = parts.Length == 2 ? Unspelt(parts[1]) : null;

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var process)
            && process > 0
            && string.Equals(parts[0], process.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            && (parts.Length == 1 || stamp is not null)
                ? (name[1..at], new AsideRemover(process, stamp))
                : null;
    }

    /// <summary>A stamp as an aside's name holds it: each letter, digit and dash as it is, every other character as <c>_XXXX</c>.</summary>
    private static string Spelt(string stamp)
    {
        var spelt = new StringBuilder(stamp.Length);

        foreach (var character in stamp)
        {
            if (Plain(character))
            {
                spelt.Append(character);
            }
            else
            {
                spelt.Append(Escape).Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
            }
        }

        return spelt.ToString();
    }

    /// <summary>
    /// The stamp <paramref name="spelt"/> spells, or <see langword="null"/> where it is not one <see cref="Spelt"/> would
    /// have written: empty, a character it would have escaped, or an escape that is not four upper-case hex digits.
    /// </summary>
    private static string? Unspelt(string spelt)
    {
        if (spelt.Length == 0)
        {
            return null;
        }

        var stamp = new StringBuilder(spelt.Length);

        for (var index = 0; index < spelt.Length; index++)
        {
            var character = spelt[index];

            if (Plain(character))
            {
                stamp.Append(character);
                continue;
            }

            if (character != Escape
                || index + 4 >= spelt.Length
                || !ushort.TryParse(spelt.AsSpan(index + 1, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)
                || Plain((char)code)
                || !string.Equals(spelt.Substring(index + 1, 4), code.ToString("X4", CultureInfo.InvariantCulture), StringComparison.Ordinal))
            {
                return null;
            }

            stamp.Append((char)code);
            index += 4;
        }

        return stamp.ToString();
    }

    /// <summary>Whether a stamp's character is written into a name as it is: an ASCII letter or digit, or a dash.</summary>
    private static bool Plain(char character) => char.IsAsciiLetterOrDigit(character) || character == ByProcess;
}
