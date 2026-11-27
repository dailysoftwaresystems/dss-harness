using DssHarness.Core.Platform;

namespace DssHarness.Core.Git;

/// <summary>
/// What every git of this tool's own runs with, the one owner of it: no file-system monitor - a program the repository's
/// configuration names, which git would run at every look at the work tree - none of the repository's hooks but where a
/// command runs them on purpose (<see cref="GitHooks"/>), and, but for those, no recursion into a submodule, each set
/// through <c>GIT_CONFIG_PARAMETERS</c>, as git's own <c>-c</c> is and as git hands it on to every git it starts, once the
/// inherited variables are gone; no replacement of an object by a ref under <c>refs/replace/</c>
/// (<c>GIT_NO_REPLACE_OBJECTS</c>), so every object is read as it was written; every comparison of a work tree told a
/// submodule's state by its commit alone, on its command line (<see cref="Arguments"/>); on Windows, every path past 260
/// characters reached as any other is (<see cref="LongPaths"/>); and, in a tree whose git directories were pinned before
/// an agent's launch or an execution over its work (<see cref="GitDirectoryPins"/>) - a main checkout's as well as a
/// linked worktree's - those directories, whatever the tree's own files say of them now; and git's messages untranslated
/// but where the command is one git runs the repository's hooks for, which keeps the person's locale
/// (<see cref="Untranslated"/>).
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
    /// <summary>The variable that names the locale a git of the tool's runs under, which outranks every other.</summary>
    public const string LocaleVariable = "LC_ALL";

    /// <summary>
    /// The locale a git of the tool's runs under, but one git runs the repository's hooks for: C, in which git says every
    /// message in its own words, untranslated, so no answer is read in a language the reading does not match
    /// (<see cref="Untranslated"/>).
    /// </summary>
    public const string Locale = "C";

    /// <summary>
    /// Sets in <paramref name="environment"/> the locale the git <paramref name="arguments"/> run starts under - the one place
    /// it is set, for every git of the tool's: <see cref="Locale"/>, but where the command is one git runs the repository's
    /// hooks for (<see cref="GitHooks.MayRun"/>), which is left the person's own, so a hook it runs - a JVM's, Ruby's, a
    /// shell's, and each program they start, a server's over ssh among them - behaves as it does in their shell, and the
    /// person reads git's words of it in their language. Its words are matched nowhere: only questions' are.
    /// </summary>
    /// <param name="environment">The command's environment.</param>
    /// <param name="arguments">The command's arguments.</param>
    public static void Untranslated(IDictionary<string, string?> environment, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(arguments);

        if (!GitHooks.MayRun(arguments))
        {
            environment[LocaleVariable] = Locale;
        }
    }

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
    /// The setting that has git for Windows reach a path past 260 characters as it reaches any other, as git on every
    /// other system always does. A worktree's build directory holds paths that long, and without it git stops part way
    /// at the first one: adding, listing the status of, or removing a worktree - a removal with its .git file and its
    /// record already gone. Given on Windows alone (<see cref="Apply"/>), the one system whose git reads it, to every
    /// git of the tool's, one that runs the hooks among them: it changes what git can reach, never what a command does.
    /// </summary>
    public const string LongPaths = "core.longpaths=true";

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
    /// command that runs the hooks, inside the isolation, runs as the user's configuration says; on Windows, long paths
    /// (<see cref="LongPaths"/>), whether it runs the hooks or not; the untranslated locale but for a command git runs the
    /// hooks for (<see cref="Untranslated"/>) - and the git directories pinned for
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

        if (system == PlatformId.Windows)
        {
            settings = [.. settings, LongPaths];
        }

        // Each as git itself writes one there: in single quotes, which hold every other character as it is.
        environment[ParametersVariable] = string.Join(' ', settings.Select(setting => $"'{setting}'"));
        environment[NoReplaceVariable] = "1";
        Untranslated(environment, arguments);

        if (pins?.Of(directory) is { } pin)
        {
            environment["GIT_DIR"] = pin.GitDirectory;
            environment["GIT_COMMON_DIR"] = pin.CommonDirectory;
            environment["GIT_WORK_TREE"] = pin.Tree;
        }
    }
}
