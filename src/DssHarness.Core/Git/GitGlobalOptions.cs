namespace DssHarness.Core.Git;

/// <summary>
/// git's own options, which stand before its subcommand: the one grammar of them, read by every reader that has to find
/// the subcommand a git command line runs - the tool's own git, which says whether a command runs the repository's hooks
/// and which settings it runs with (<see cref="GitHooks"/>, <see cref="GitOwnSettings"/>), and the agents' policy, which
/// reads what an agent's git command does.
/// </summary>
/// <remarks>
/// An option that takes its value as the next word takes that word with it, so the word is never read as the subcommand:
/// <c>git --git-dir /r/.git commit</c> runs <c>commit</c>, not <c>/r/.git</c>. The same option with its value in the same
/// word - <c>--git-dir=/r/.git</c>, <c>-C/r</c>, <c>-ca=b</c> - is one word.
/// </remarks>
public static class GitGlobalOptions
{
    /// <summary>
    /// Whether <paramref name="option"/> is one of git's own options that takes its value as the next word: <c>-C</c> and
    /// <c>-c</c>, <c>--config-env</c>, and the long ones that name a place or a list (<see cref="NamesValue"/>).
    /// </summary>
    /// <param name="option">One word before the subcommand.</param>
    public static bool TakesNextWord(string option)
        => option is "-C" or "-c" or "--config-env" || NamesValue(option);

    /// <summary>
    /// Whether <paramref name="name"/> is one of git's own long options that take a value - in the next word, or after
    /// <c>=</c> in the same one - beside <c>--config-env</c>: <c>--git-dir</c>, <c>--work-tree</c>, <c>--namespace</c>,
    /// <c>--super-prefix</c>, <c>--attr-source</c>, <c>--shallow-file</c> and <c>--list-cmds</c>.
    /// </summary>
    /// <param name="name">The option's name, without any <c>=</c> and value.</param>
    public static bool NamesValue(string name)
        => name is "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix" or "--attr-source" or "--shallow-file" or "--list-cmds";

    /// <summary>Whether <paramref name="option"/> is one of git's own options that takes no value.</summary>
    /// <param name="option">One word before the subcommand.</param>
    public static bool ValueLess(string option)
        => option is "--bare" or "--no-optional-locks" or "--literal-pathspecs" or "--glob-pathspecs"
            or "--noglob-pathspecs" or "--icase-pathspecs" or "--no-replace-objects" or "--no-lazy-fetch" or "--no-advice"
            or "--html-path" or "--man-path" or "--info-path" or "--exec-path" or "-p" or "--paginate" or "-P" or "--no-pager"
            or "-v" or "--version" or "-h" or "--help";

    /// <summary>
    /// Where among <paramref name="words"/> the subcommand stands: the first word from <paramref name="from"/> past git's own
    /// options, each that takes the next word as its value (<see cref="TakesNextWord"/>) with it; -1 where none stands.
    /// </summary>
    /// <param name="words">The command's words.</param>
    /// <param name="from">Where git's own options begin: 0 for git's arguments alone, 1 past the program's name.</param>
    public static int SubcommandAt(IReadOnlyList<string> words, int from = 0)
    {
        ArgumentNullException.ThrowIfNull(words);

        for (var at = from; at < words.Count; at++)
        {
            if (TakesNextWord(words[at]))
            {
                at++;
            }
            else if (!words[at].StartsWith('-'))
            {
                return at;
            }
        }

        return -1;
    }
}
