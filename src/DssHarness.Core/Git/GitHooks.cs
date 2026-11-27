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
    /// The commands git runs a repository's hooks for, as git's documentation of its hooks (githooks(5)) names them, each its
    /// subcommand and, where only one of its own commands runs them, that one: <c>am</c> (applypatch-msg, pre-applypatch,
    /// post-applypatch); <c>commit</c> (pre-commit, prepare-commit-msg, commit-msg, post-commit, and post-rewrite as it
    /// amends); <c>merge</c> (pre-merge-commit, prepare-commit-msg, commit-msg, post-merge) and <c>pull</c>, which merges or
    /// rebases; <c>rebase</c> (pre-rebase, post-rewrite, post-checkout); <c>cherry-pick</c> and <c>revert</c>, which commit
    /// as <c>commit</c> does; <c>checkout</c>, <c>switch</c>, <c>clone</c> and <c>worktree add</c> (post-checkout);
    /// <c>push</c> (pre-push, and the receiving repository's own where it is one here); <c>receive-pack</c> (pre-receive,
    /// update, proc-receive, post-receive, post-update, push-to-checkout); <c>send-email</c> (sendemail-validate); <c>gc</c>
    /// (pre-auto-gc, as it runs with <c>--auto</c>); and <c>p4 submit</c> (the p4 hooks). The two hooks it names no command
    /// for - reference-transaction, run by any command that moves a ref, and post-index-change, by any that writes the
    /// index - make no command one of these: every command that writes would be, and the tool's questions and changes read
    /// git's own words; and no git of the tool's runs either but where it runs the hooks on purpose. Each keeps the
    /// person's locale (<see cref="GitOwnSettings.Untranslated"/>), so a hook it runs behaves as it does in their shell.
    /// </summary>
    public static IReadOnlyList<string> Runners { get; } =
        ["am", "commit", "merge", "pull", "rebase", "cherry-pick", "revert", "checkout", "switch", "clone", "worktree add", "push", "receive-pack", "send-email", "gc", "p4 submit"];

    /// <summary>Whether <paramref name="arguments"/> run a command git runs a repository's hooks for (<see cref="Runners"/>).</summary>
    /// <param name="arguments">The command's arguments, its subcommand after any of git's own options.</param>
    public static bool MayRun(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        if (SubcommandAt(arguments) is not (>= 0 and var at))
        {
            return false;
        }

        return Runners.Any(runner => runner.Split(' ') is var words
            && at + words.Length <= arguments.Count
            && words.Select((word, index) => string.Equals(word, arguments[at + index], StringComparison.Ordinal)).All(same => same));
    }

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

        return SubcommandAt(arguments) is >= 0 and var at && Questions.Contains(arguments[at], StringComparer.Ordinal)
            ? null
            : $"{PathKey}={Nowhere(system)}";
    }

    /// <summary>
    /// Where among <paramref name="arguments"/> the subcommand they run is: the first word past git's own options, each
    /// <c>-c</c> and <c>-C</c> with its value; -1 where there is none.
    /// </summary>
    /// <param name="arguments">The command's arguments.</param>
    internal static int SubcommandAt(IReadOnlyList<string> arguments)
    {
        for (var at = 0; at < arguments.Count; at++)
        {
            if (arguments[at] is "-c" or "-C")
            {
                at++;
            }
            else if (!arguments[at].StartsWith('-'))
            {
                return at;
            }
        }

        return -1;
    }
}
