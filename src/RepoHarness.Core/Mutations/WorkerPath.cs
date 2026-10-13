using System.Text;

namespace RepoHarness.Core.Mutations;

/// <summary>
/// The one place a worker's own path is told apart from what its build wrote: wherever the worker's root stands in a
/// build line or in a file its configure made, it is replaced by <see cref="Tree"/> before anything is hashed.
/// </summary>
/// <remarks>
/// <para>
/// Every worker of every tree of one leg must come to the same record of what a target is built from, for the same
/// content: a worker is a copy of the tree kept at a path of its own, and CMake writes that path into the build lines
/// it generates and into the files its configure makes. Replaced, the path says nothing of which worker built; left,
/// no two workers would ever agree, and an arm verified in another worker than the one that drove it would never hold.
/// </para>
/// <para>
/// The root is looked for as its machine spells a path, with <c>/</c> throughout as CMake spells every path it is given,
/// and with each <c>\</c> doubled as a generated header escapes one; on a machine whose paths open with a drive, with the
/// drive's letter in either case. A file that holds the path another way never compares equal across workers, and
/// what was judged on it stays pending, which is the safe direction.
/// </para>
/// </remarks>
internal static class WorkerPath
{
    /// <summary>What a worker's root is replaced by.</summary>
    public const string Tree = "{tree}";

    /// <summary><paramref name="text"/> with every spelling of <paramref name="workerRoot"/> in it replaced by <see cref="Tree"/>.</summary>
    /// <param name="text">A build line, or the text of a file a worker's configure made.</param>
    /// <param name="workerRoot">The worker's root, as this machine names it.</param>
    public static string Told(string text, string workerRoot)
    {
        ArgumentNullException.ThrowIfNull(text);

        foreach (var spelling in Spellings(workerRoot))
        {
            text = text.Replace(spelling, Tree, StringComparison.Ordinal);
        }

        return text;
    }

    /// <summary>
    /// <paramref name="bytes"/> with every spelling of <paramref name="workerRoot"/> in them, as UTF-8 spells it, replaced
    /// by <see cref="Tree"/>: a file need not be text for the path in it to be told, and nothing else of it changes.
    /// </summary>
    /// <param name="bytes">What a file a worker's configure made holds.</param>
    /// <param name="workerRoot">The worker's root, as this machine names it.</param>
    public static byte[] Told(ReadOnlySpan<byte> bytes, string workerRoot)
    {
        var told = bytes.ToArray();
        var replacement = Encoding.UTF8.GetBytes(Tree);

        foreach (var spelling in Spellings(workerRoot).Select(Encoding.UTF8.GetBytes))
        {
            if (told.AsSpan().IndexOf(spelling) < 0)
            {
                continue;
            }

            var written = new List<byte>(told.Length);
            var rest = told.AsSpan();

            while (rest.IndexOf(spelling) is >= 0 and var at)
            {
                written.AddRange(rest[..at]);
                written.AddRange(replacement);
                rest = rest[(at + spelling.Length)..];
            }

            written.AddRange(rest);
            told = [.. written];
        }

        return told;
    }

    /// <summary>
    /// Each spelling of <paramref name="workerRoot"/> a build may hold, the longest first so none is replaced in part:
    /// as this machine spells it, with <c>/</c> throughout, and with each <c>\</c> doubled - each with its drive's letter
    /// in either case, where it opens with one.
    /// </summary>
    /// <param name="workerRoot">The worker's root, as this machine names it.</param>
    internal static IReadOnlyList<string> Spellings(string workerRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workerRoot);

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workerRoot));
        var spellings = new HashSet<string>(StringComparer.Ordinal) { root, root.Replace('\\', '/'), root.Replace("\\", "\\\\", StringComparison.Ordinal) };

        if (root.Length > 1 && root[1] == ':' && char.IsAsciiLetter(root[0]))
        {
            foreach (var spelling in spellings.ToList())
            {
                spellings.Add(char.ToUpperInvariant(spelling[0]) + spelling[1..]);
                spellings.Add(char.ToLowerInvariant(spelling[0]) + spelling[1..]);
            }
        }

        return [.. spellings.OrderByDescending(spelling => spelling.Length).ThenBy(spelling => spelling, StringComparer.Ordinal)];
    }
}
