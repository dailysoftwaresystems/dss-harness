using DssHarness.Core.Platform;

namespace DssHarness.Core.Git;

/// <summary>
/// What every git of this tool's own runs with, the one owner of it: no file-system monitor - a program the repository's
/// configuration names, which git would run at every look at the work tree - and none of the repository's hooks but where
/// a command runs them on purpose (<see cref="GitHooks"/>), each set through <c>GIT_CONFIG_PARAMETERS</c>, as git's own
/// <c>-c</c> is and as git hands it on to every git it starts, once the inherited variables are gone; no replacement of
/// an object by a ref under <c>refs/replace/</c> (<c>GIT_NO_REPLACE_OBJECTS</c>), so every object is read as it was
/// written; and, in a tree whose git directories were pinned before an agent's launch or an execution over its work
/// (<see cref="GitDirectoryPins"/>) - a main checkout's as well as a linked worktree's - those directories, whatever the
/// tree's own files say of them now.
/// </summary>
public static class GitOwnSettings
{
    /// <summary>The variable git reads its command line's settings from, and hands them on in.</summary>
    public const string ParametersVariable = "GIT_CONFIG_PARAMETERS";

    /// <summary>The setting that keeps git from starting the file-system monitor a repository's configuration names.</summary>
    public const string NoMonitor = "core.fsmonitor=false";

    /// <summary>The variable that keeps git from reading an object through a replacement ref.</summary>
    public const string NoReplaceVariable = "GIT_NO_REPLACE_OBJECTS";

    /// <summary>
    /// Sets in <paramref name="environment"/>, the inherited git variables already cleared, the tool's own settings for the
    /// command <paramref name="arguments"/> run - no file-system monitor, no object replaced, and no hook where <paramref name="hooksRun"/> is
    /// false and the command is no question (<see cref="GitHooks.Off"/>) - and the git directories pinned for
    /// <paramref name="directory"/>, where it is the root of a tree an isolated agent works over: its own git directory,
    /// its common one and its work tree.
    /// </summary>
    /// <param name="environment">The command's environment.</param>
    /// <param name="system">The operating system git runs on.</param>
    /// <param name="arguments">The command's arguments.</param>
    /// <param name="hooksRun">Whether the command runs the repository's hooks on purpose.</param>
    /// <param name="directory">The directory the command runs in.</param>
    /// <param name="pins">The pins; none where null.</param>
    public static void Apply(IDictionary<string, string?> environment, PlatformId system, IReadOnlyList<string> arguments, bool hooksRun, string directory, GitDirectoryPins? pins)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(arguments);

        IEnumerable<string> settings = hooksRun || GitHooks.Off(system, arguments) is not { } off ? [NoMonitor] : [NoMonitor, off];

        // Each as git itself writes one there: in single quotes, which hold every other character as it is.
        environment[ParametersVariable] = string.Join(' ', settings.Select(setting => $"'{setting}'"));
        environment[NoReplaceVariable] = "1";

        if (pins?.Of(directory) is { } pin)
        {
            environment["GIT_DIR"] = pin.GitDirectory;
            environment["GIT_COMMON_DIR"] = pin.CommonDirectory;
            environment["GIT_WORK_TREE"] = pin.Tree;
        }
    }
}
