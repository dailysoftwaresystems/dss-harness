using RepoHarness.Core.Hosts;

namespace RepoHarness.Core.Platform;

/// <summary>
/// Where this tool keeps what is one per user of a machine, whichever repositories and worktrees reach it: the hold
/// that keeps the machine awake between commands, the heavy legs admitted onto it.
/// </summary>
/// <remarks>
/// Among this user's own application data, never in a directory other users can write: whoever could write there
/// could hold a machine awake, or keep every other user's legs waiting. A process that can name no such directory -
/// given no home, as a program started under an id no account has - keeps nothing there, and is told why, rather than
/// being sent to the directory every user shares.
/// </remarks>
public static class UserState
{
    /// <summary>The file <paramref name="name"/> in this user's own directory for this tool.</summary>
    /// <param name="name">The file's name.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string File(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Path.Combine(ApplicationData(), ToolPackage.Command, name);
    }

    /// <summary>This user's own application data directory, below the home directory this process was given.</summary>
    /// <remarks>
    /// macOS's lookup of it asks the system for the account's home rather than reading <c>HOME</c>, which every
    /// other path a process there derives honors - and which a process given another home, as a test gives it,
    /// then does not reach. Its place below that home is the same. Windows's lookup asks the system for the
    /// account's folder, which no environment moves, so the <c>LOCALAPPDATA</c> the process was given comes first:
    /// it names that same folder unless the process was given another.
    /// </remarks>
    private static string ApplicationData()
    {
        if (OperatingSystem.IsMacOS()
            && Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } home)
        {
            return Path.Combine(home, "Library", "Application Support");
        }

        if (OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } given
            && Path.IsPathFullyQualified(given))
        {
            return given;
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify) is { Length: > 0 } own
            ? own
            : throw new DirectoryNotFoundException(
                "no directory of this user's own application data could be named: the process was given no home, and the "
                + "system has none for its account");
    }
}
