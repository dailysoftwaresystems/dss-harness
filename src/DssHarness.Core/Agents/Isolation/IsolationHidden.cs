using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Agents.Isolation;

/// <summary>
/// The places the isolation hides from every isolated command, neither read nor written: the credential slots, every
/// Claude Code directory and the file Claude Code keeps beside one named <c>.claude</c> (<c>~/.claude.json</c>). The one
/// list every profile hides (<see cref="AgentIsolation"/>) and every read of the tool's out of a place isolated work writes
/// that follows a link refuses to reach (a pull, <see cref="Sync.ISyncTransport.PullFileAsync"/>).
/// </summary>
public static class IsolationHidden
{
    /// <summary>The file Claude Code keeps its own state in, beside a Claude Code directory named <c>.claude</c>.</summary>
    public const string ClaudeFileName = ".claude.json";

    /// <summary>
    /// The hidden places: each slot and each Claude Code directory as a directory, the file beside each Claude Code
    /// directory named <c>.claude</c>, and each place the user keeps credentials in (<see cref="HiddenPlace"/>).
    /// </summary>
    /// <param name="slots">The credential slots.</param>
    /// <param name="claudeCode">The Claude Code directories.</param>
    /// <param name="credentials">The places the user keeps credentials in outside the slots.</param>
    public static IReadOnlyList<IsolatedPath> Places(IReadOnlyList<string> slots, IReadOnlyList<string> claudeCode, IReadOnlyList<HiddenPlace> credentials)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(claudeCode);
        ArgumentNullException.ThrowIfNull(credentials);

        return
        [
            .. slots.Concat(claudeCode).Select(place => new IsolatedPath(place, Directory: true)),
            .. claudeCode
                .Where(directory => Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)) == ".claude")
                .Select(directory => new IsolatedPath(Path.TrimEndingDirectorySeparator(directory) + ".json", Directory: false)),
            .. credentials.Select(credential => new IsolatedPath(credential.Path, credential.Directory)),
        ];
    }

    /// <summary>Whether <paramref name="place"/> is the file Claude Code keeps beside a Claude Code directory.</summary>
    /// <param name="place">A hidden place.</param>
    public static bool IsClaudeFile(string place) => Path.GetFileName(place) == ClaudeFileName;

    /// <summary>
    /// The hidden place <paramref name="opened"/> - where an open file or directory stands, as the system names it by its
    /// handle (<see cref="IFileSystem.FinalPathOf"/>, <see cref="IFileSystem.FinalPathOfDirectory"/>) - lies in; null where
    /// none. Each place is compared by where it resolves and as the system names it, whatever the letter case, as a link
    /// that leads into one is followed to it under any spelling the system takes.
    /// </summary>
    /// <param name="fileSystem">Resolves and names each hidden place.</param>
    /// <param name="hidden">The hidden places (<see cref="Places"/>).</param>
    /// <param name="opened">Where what was opened stands.</param>
    public static string? Holding(IFileSystem fileSystem, IReadOnlyList<IsolatedPath> hidden, string opened)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(hidden);
        ArgumentException.ThrowIfNullOrWhiteSpace(opened);

        foreach (var place in hidden)
        {
            if (Spellings(fileSystem, place).Any(spelt => PathContainment.IsInsideOrSame(spelt, opened, StringComparison.OrdinalIgnoreCase)))
            {
                return place.Path;
            }
        }

        return null;
    }

    /// <summary>A hidden place as given, where it resolves, and - a directory there - as the system names it opened.</summary>
    private static IEnumerable<string> Spellings(IFileSystem fileSystem, IsolatedPath place)
    {
        yield return Path.TrimEndingDirectorySeparator(Path.GetFullPath(place.Path));

        string? resolved = null;
        string? named = null;

        try
        {
            resolved = Path.TrimEndingDirectorySeparator(fileSystem.ResolveLinks(place.Path));
            named = place.Directory && fileSystem.DirectoryExists(place.Path) ? Path.TrimEndingDirectorySeparator(fileSystem.FinalPathOfDirectory(place.Path)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A place that cannot be followed is held to its spelling alone; what was opened is a real path, never through it.
        }

        if (resolved is not null)
        {
            yield return resolved;
        }

        if (named is not null)
        {
            yield return named;
        }
    }
}
