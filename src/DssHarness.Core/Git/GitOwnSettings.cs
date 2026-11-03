using DssHarness.Core.Platform;

namespace DssHarness.Core.Git;

/// <summary>
/// What every git of this tool's own runs with, the one owner of it: no file-system monitor - a program the repository's
/// configuration names, which git would run at every look at the work tree - none of the repository's hooks but where a
/// command runs them on purpose (<see cref="GitHooks"/>), and, but for those, no recursion into a submodule, each set
/// through <c>GIT_CONFIG_PARAMETERS</c>, as git's own <c>-c</c> is and as git hands it on to every git it starts, once the
/// inherited variables are gone; no replacement of an object by a ref under <c>refs/replace/</c>
/// (<c>GIT_NO_REPLACE_OBJECTS</c>), so every object is read as it was written; every comparison of a work tree told a
/// submodule's state by its commit alone, on its command line (<see cref="Arguments"/>); and, in a tree whose git
/// directories were pinned before an agent's launch or an execution over its work (<see cref="GitDirectoryPins"/>) - a
/// main checkout's as well as a linked worktree's - those directories, whatever the tree's own files say of them now.
/// </summary>
/// <remarks>
/// A submodule's repository is configuration of its own, and where it sits in a tree an isolated agent writes - a
/// repository it made there, its <c>.git</c> a directory of the tree or a file pointing anywhere, put in the index as a
/// submodule's entry - that configuration is the agent's: a clean filter it names runs whenever git asks the submodule's
/// own git whether its work tree changed, which a status or a diff of the superproject does unless told not to. So no git
/// of the tool's that runs outside the isolation asks it: none recurses into one, and none compares a submodule's work
/// tree.
/// </remarks>
public static class GitOwnSettings
{
    /// <summary>The variable git reads its command line's settings from, and hands them on in.</summary>
    public const string ParametersVariable = "GIT_CONFIG_PARAMETERS";

    /// <summary>The setting that keeps git from starting the file-system monitor a repository's configuration names.</summary>
    public const string NoMonitor = "core.fsmonitor=false";

    /// <summary>The variable that keeps git from reading an object through a replacement ref.</summary>
    public const string NoReplaceVariable = "GIT_NO_REPLACE_OBJECTS";

    /// <summary>
    /// The setting that keeps a checkout, a reset, a read of a tree, a fetch or a push from recursing into a submodule -
    /// running its own git, with its own configuration - whatever the user's configuration asks.
    /// </summary>
    public const string NoRecursion = "submodule.recurse=false";

    /// <summary>
    /// How a comparison of the tool's tells a submodule's state: by the commit its HEAD names against the one its entry
    /// records - a submodule added, removed or moved to another commit still listed - never by asking its own git about
    /// its work tree. On the command line, which a <c>.gitmodules</c> file's <c>ignore</c> - a file of the tree - cannot
    /// outrank, as it outranks a configured default.
    /// </summary>
    public const string SubmoduleCommits = "--ignore-submodules=dirty";

    /// <summary>
    /// The subcommands that compare a work tree, each of which asks every submodule's own git about its work tree unless
    /// told not to.
    /// </summary>
    public static IReadOnlyList<string> Comparisons { get; } = ["status", "diff", "diff-files", "diff-index"];

    /// <summary>
    /// The arguments a command of the tool's runs with: <paramref name="arguments"/>, a comparison of a work tree
    /// (<see cref="Comparisons"/>) given <see cref="SubmoduleCommits"/> right after its subcommand where it names none,
    /// and one that hides submodules altogether left as it is.
    /// </summary>
    /// <param name="arguments">The command's arguments.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="arguments"/> ask a comparison to look into a submodule's work tree, which no command of the tool's
    /// does.
    /// </exception>
    public static IReadOnlyList<string> Arguments(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (GitHooks.SubcommandAt(arguments) is not (>= 0 and var at) || !Comparisons.Contains(arguments[at], StringComparer.Ordinal))
        {
            return arguments;
        }

        foreach (var argument in arguments.Skip(at + 1).TakeWhile(argument => argument != "--"))
        {
            if (argument.StartsWith("--ignore-submodules", StringComparison.Ordinal))
            {
                return argument is "--ignore-submodules" or "--ignore-submodules=all" or SubmoduleCommits
                    ? arguments
                    : throw new ArgumentException($"No git of the tool's looks into a submodule's work tree, which '{argument}' asks.", nameof(arguments));
            }
        }

        return [.. arguments.Take(at + 1), SubmoduleCommits, .. arguments.Skip(at + 1)];
    }

    /// <summary>
    /// Sets in <paramref name="environment"/>, the inherited git variables already cleared, the tool's own settings for the
    /// command <paramref name="arguments"/> run - no file-system monitor and no object replaced; where <paramref name="hooksRun"/> is
    /// false, no recursion into a submodule, and no hook where the command is no question (<see cref="GitHooks.Off"/>), while a
    /// command that runs the hooks, inside the isolation, runs as the user's configuration says - and the git directories pinned for
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

        IEnumerable<string> settings = hooksRun
            ? [NoMonitor]
            : [NoMonitor, NoRecursion, .. GitHooks.Off(system, arguments) is { } off ? [off] : Array.Empty<string>()];

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
