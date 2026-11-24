using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;

namespace DssHarness.Core.Platform;

/// <summary>
/// Where this tool keeps what is one per user of a machine, whichever repositories and worktrees reach it: the hold
/// that keeps the machine awake between commands, the heavy legs admitted onto it, the set-ups its legs hold - and the
/// isolation's own directory, which holds the caches and the temporary directories of what runs inside it.
/// </summary>
/// <remarks>
/// Among this user's own application data, never in a directory other users can write: whoever could write there
/// could hold a machine awake, or keep every other user's legs waiting. A process that can name no such directory -
/// given no home, as a program started under an id no account has - keeps nothing there, and is told why, rather than
/// being sent to the directory every user shares. <see cref="Variable"/> moves all of it at once to a directory the user
/// names, and reads as nothing but this tool's: the home, the user data and the user cache are every program's, and
/// moving one of them to move this tool's state would move what every other program keeps there with it.
/// </remarks>
public static class UserState
{
    /// <summary>
    /// The variable of this tool's own that names the directory it keeps its per-user state in, as a fully qualified path,
    /// in place of the system's default: read first, on every system, by <see cref="Directory"/> and
    /// <see cref="IsolationDirectory"/> alike. Set to nothing, it counts as unset, as the XDG variables' empty values do -
    /// a shell's <c>export</c> of a variable that holds nothing gives it so, and Windows cannot hold an empty value at all.
    /// Set to anything else that is not a fully qualified path, it names no directory, and is refused for it
    /// (<see cref="VariableNamesNoDirectoryException"/>) by every reader of this tool's per-user state rather than read as
    /// the default, which is the very place its setter meant to leave.
    /// </summary>
    public const string Variable = "DSSHARNESS_USER_STATE";

    /// <summary>The name of the isolation's own directory, under the directory <see cref="Variable"/> names or the user's cache directory.</summary>
    public const string IsolationDirectoryName = "dssharness-isolation";

    /// <summary>
    /// This user's own directory for this tool: the directory <see cref="Variable"/> names, where it names one, else below
    /// the home directory this process was given. Every file and folder below it that a command of this tool keeps is
    /// <see cref="File"/>'s to name, and this names the place they share.
    /// </summary>
    /// <exception cref="DirectoryNotFoundException">
    /// No directory of this user's own could be named: a <see cref="VariableNamesNoDirectoryException"/> where
    /// <see cref="Variable"/> names none.
    /// </exception>
    public static string Directory => DirectoryOf(Environment.GetEnvironmentVariable);

    /// <summary>
    /// This user's own directory for this tool, <see cref="Variable"/> read from <paramref name="environment"/>: the
    /// directory it names, where it names one, else below the home directory this process was given.
    /// </summary>
    /// <param name="environment">Reads the variable.</param>
    /// <exception cref="DirectoryNotFoundException">
    /// No directory of this user's own could be named: a <see cref="VariableNamesNoDirectoryException"/> where
    /// <see cref="Variable"/> names none.
    /// </exception>
    internal static string DirectoryOf(Func<string, string?> environment)
        => Given(environment) ?? Path.Combine(ApplicationData(), ToolPackage.Command);

    /// <summary>
    /// The isolation's own directory on this machine, which holds the caches of what runs inside it, each launch's
    /// temporary directory and the directories a leg's work starts what runs outside it from: under the directory
    /// <see cref="Variable"/> names, where it names one, on every system; else under the user's cache directory - on
    /// Windows <c>LOCALAPPDATA</c> where it names one whole, else <c>AppData\Local</c> in the home; elsewhere
    /// <c>XDG_CACHE_HOME</c> where it names one whole, else the home's own cache directory, macOS's the Library's.
    /// </summary>
    /// <param name="home">The user's home.</param>
    /// <param name="platform">This machine's system.</param>
    /// <param name="environment">Reads this process's environment.</param>
    /// <exception cref="VariableNamesNoDirectoryException"><see cref="Variable"/> names no directory.</exception>
    public static string IsolationDirectory(string home, PlatformId platform, Func<string, string?> environment)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(home);
        ArgumentNullException.ThrowIfNull(environment);

        if (Given(environment) is { } given)
        {
            return Path.Combine(given, IsolationDirectoryName);
        }

        string? Named(string variable) => environment(variable) is { Length: > 0 } named && Path.IsPathFullyQualified(named) ? named : null;

        var cache = platform switch
        {
            PlatformId.Windows => Named("LOCALAPPDATA") ?? Path.Combine(home, "AppData", "Local"),
            PlatformId.MacOs => Named("XDG_CACHE_HOME") ?? Path.Combine(home, "Library", "Caches"),
            _ => Named("XDG_CACHE_HOME") ?? Path.Combine(home, ".cache"),
        };

        return Path.Combine(cache, IsolationDirectoryName);
    }

    /// <summary>The file <paramref name="name"/> in this user's own directory for this tool.</summary>
    /// <param name="name">The file's name.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string File(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return Path.Combine(Directory, name);
    }

    /// <summary>
    /// The record <paramref name="prefix"/> keeps of <paramref name="platform"/>'s machine, for the user running this
    /// process (<see cref="MachineFileName"/>). Where the machine can be told only by its name, that is said under
    /// <paramref name="command"/>, with why: a command started after the name changes keeps a record of its own.
    /// </summary>
    /// <param name="platform">Tells this machine from every other.</param>
    /// <param name="output">Says the record is named by the machine's name, where it is.</param>
    /// <param name="command">What the line saying so is said under.</param>
    /// <param name="prefix">What the record's file name begins with.</param>
    /// <param name="what">What the record holds, as the line names it: <c>heavy legs</c>.</param>
    /// <param name="besideTheOld">What a command keeping a record of its own then does beside the old one.</param>
    /// <exception cref="DirectoryNotFoundException">No directory of this user's own could be named.</exception>
    public static string MachineFile(IHostPlatform platform, IHarnessOutput output, string command, string prefix, string what, string besideTheOld)
    {
        ArgumentNullException.ThrowIfNull(platform);
        ArgumentNullException.ThrowIfNull(output);

        var machine = platform.MachineId;

        if (machine.ByName is { } why)
        {
            output.Info(
                command,
                $"This machine's {what} are recorded under its name, '{machine.Id}', since {why}; a command started after "
                + $"that name changes keeps a record of its own, and {besideTheOld}.");
        }

        return File(MachineFileName(prefix, machine.Id));
    }

    /// <summary>The file name of the record <paramref name="prefix"/> keeps of the machine <paramref name="machineId"/> tells apart.</summary>
    /// <param name="prefix">What the record's file name begins with.</param>
    /// <param name="machineId">What tells the machine from every other.</param>
    public static string MachineFileName(string prefix, string machineId) => $"{prefix}-{FileNames.SafeFor(machineId)}.json";

    /// <summary>
    /// The directory <see cref="Variable"/> names in <paramref name="environment"/>; null where it is not set, or set to
    /// nothing, which counts as unset.
    /// </summary>
    /// <param name="environment">Reads the variable.</param>
    /// <exception cref="VariableNamesNoDirectoryException">It is set to a path that is not fully qualified.</exception>
    private static string? Given(Func<string, string?> environment)
        => environment(Variable) switch
        {
            null or "" => null,
            var given when Path.IsPathFullyQualified(given) => given,
            var given => throw new VariableNamesNoDirectoryException(given),
        };

    /// <summary>This user's own application data directory, below the home directory this process was given.</summary>
    /// <remarks>
    /// macOS's lookup of it asks the system for the account's home rather than reading <c>HOME</c>, which every
    /// other path a process there derives honors; its place below that home is the same. Windows's lookup asks the
    /// system for the account's folder, which no environment moves, so the <c>LOCALAPPDATA</c> the process was given comes
    /// first: it names that same folder unless the process was given another.
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

/// <summary>
/// <see cref="UserState.Variable"/> set to a value that names no directory: refused by every reader of this tool's
/// per-user state, naming what it holds, never read as the default its setter meant to leave. A
/// <see cref="DirectoryNotFoundException"/>, as a process given no home meets one, so every reader that keeps nothing
/// then is refused alike; the one reader that takes a process given no home to declare nothing - the machine's settings
/// file - tells this apart from it, and refuses.
/// </summary>
/// <param name="given">What the variable holds.</param>
public sealed class VariableNamesNoDirectoryException(string given)
    : DirectoryNotFoundException($"{UserState.Variable} names '{given}', which is no fully qualified path")
{
    /// <summary>What puts it right, as a refusal's remedy says it.</summary>
    public const string Remedy = $"name a fully qualified directory in {UserState.Variable}, or leave it unset";
}
