using DssHarness.Core.Platform;

namespace DssHarness.Core.Git;

/// <summary>
/// The rule of the repository's hooks under the tool's own git: none of them runs, but where a run's commit or push runs
/// them on purpose (<see cref="IGitClient.RunWithHooksAsync"/>), inside the operating system's isolation where this machine
/// has one. A hook is the repository's code - in a tree the agents write, or one core.hooksPath points into, a husky
/// directory, a pre-commit or lefthook configuration it reads - and a fold, a refresh, a rebase, a sync or a query of the
/// tool's would otherwise run it outside every isolation as a side effect nobody asked for: a checkout runs post-checkout,
/// an index written runs post-index-change, a ref moved runs reference-transaction.
/// </summary>
public static class GitHooks
{
    /// <summary>The configuration key git finds its hooks by.</summary>
    public const string PathKey = "core.hooksPath";

    /// <summary>
    /// The subcommands that only answer what git holds - none writes the index or a ref, so none runs a hook - left as git
    /// runs them: <c>rev-parse</c> and <c>config</c> so answer where the hooks are as git runs them for everyone else.
    /// </summary>
    public static IReadOnlyList<string> Questions { get; } =
        ["version", "rev-parse", "config", "cat-file", "ls-tree", "ls-files", "check-ignore", "check-attr", "merge-base", "rev-list", "for-each-ref", "show-ref", "log", "show", "diff-tree"];

    /// <summary>
    /// The place that holds no hook on <paramref name="system"/>, which no command can make one in: the null device -
    /// <c>/dev/null</c>, and <c>\\.\NUL</c> on Windows, whose name in the device namespace no directory takes.
    /// </summary>
    /// <param name="system">The operating system git runs on.</param>
    public static string Nowhere(PlatformId system) => system == PlatformId.Windows ? @"\\.\NUL" : "/dev/null";

    /// <summary>
    /// The setting that runs none of the repository's hooks, as <c>key=value</c> - none where the command is one of
    /// <see cref="Questions"/>, which so answers where the hooks are as git runs them for everyone else.
    /// </summary>
    /// <param name="system">The operating system git runs on.</param>
    /// <param name="arguments">The command's arguments, its subcommand after any of git's own options.</param>
    public static string? Off(PlatformId system, IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        return Subcommand(arguments) is { } subcommand && Questions.Contains(subcommand, StringComparer.Ordinal)
            ? null
            : $"{PathKey}={Nowhere(system)}";
    }

    /// <summary>The subcommand <paramref name="arguments"/> run: the first word past git's own options, each <c>-c</c> and <c>-C</c> with its value.</summary>
    private static string? Subcommand(IReadOnlyList<string> arguments)
    {
        for (var at = 0; at < arguments.Count; at++)
        {
            if (arguments[at] is "-c" or "-C")
            {
                at++;
            }
            else if (!arguments[at].StartsWith('-'))
            {
                return arguments[at];
            }
        }

        return null;
    }
}
