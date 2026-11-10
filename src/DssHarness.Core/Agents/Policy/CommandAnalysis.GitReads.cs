namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// git branch's listing modes: each makes the command a listing - the words after it names to match - git refuses it
    /// beside every mode that writes - a branch deleted, moved, copied, its upstream set or unset, its description edited -
    /// and with it no branch is made of a name beside it. Printed by <c>help side-tasks</c>; read by
    /// <see cref="GitReadsOnly"/>.
    /// </summary>
    public static IReadOnlyList<string> GitBranchListing { get; } = ["-l", "--list", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at", "--show-current"];

    /// <summary>
    /// git tag's listing modes: each makes the command a listing, or one git refuses where another mode stands beside it -
    /// a tag deleted or verified, or made with a message, a signature or by force. Printed by <c>help side-tasks</c>; read
    /// by <see cref="GitReadsOnly"/>.
    /// </summary>
    public static IReadOnlyList<string> GitTagListing { get; } = ["-l", "--list", "-n", "--contains", "--no-contains", "--merged", "--no-merged", "--points-at"];

    /// <summary>git reflog's verbs that only read: printed by <c>help side-tasks</c>.</summary>
    public static IReadOnlyList<string> GitReflogReads { get; } = ["show", "list", "exists"];

    /// <summary>git notes' verbs that only read: printed by <c>help side-tasks</c>.</summary>
    public static IReadOnlyList<string> GitNotesReads { get; } = ["list", "show", "get-ref"];

    /// <summary>git branch's options, as its own option reader takes them.</summary>
    private static readonly OptionTable GitBranchOptions = new(
        "u",
        "t",
        ["set-upstream-to", "contains", "no-contains", "merged", "no-merged", "sort", "points-at", "format"],
        ["track", "color", "abbrev", "column"],
        ["verbose", "quiet", "unset-upstream", "remotes", "all", "delete", "move", "omit-empty", "copy", "list", "show-current",
         "create-reflog", "edit-description", "force", "ignore-case", "recurse-submodules", "no-color", "no-column", "no-abbrev"]);

    /// <summary>git branch's short options.</summary>
    private const string GitBranchLetters = "vqturadDmMcClfi";

    /// <summary>git branch's modes that write: a branch deleted, moved or copied, its upstream set or unset, its description edited.</summary>
    private static readonly string[] GitBranchWrites = ["d", "D", "delete", "m", "M", "move", "c", "C", "copy", "u", "set-upstream-to", "unset-upstream", "edit-description"];

    /// <summary>git tag's options, as its own option reader takes them.</summary>
    private static readonly OptionTable GitTagOptions = new(
        "mFu",
        "n",
        ["message", "file", "trailer", "cleanup", "local-user", "contains", "no-contains", "merged", "no-merged", "sort", "points-at", "format"],
        ["column", "color"],
        ["list", "delete", "verify", "annotate", "edit", "sign", "force", "create-reflog", "ignore-case", "omit-empty", "no-column", "no-color"]);

    /// <summary>git tag's short options.</summary>
    private const string GitTagLetters = "lndvamFesufi";

    /// <summary>git tag's modes other than its listing: a tag deleted or verified.</summary>
    private static readonly string[] GitTagModes = ["d", "delete", "v", "verify"];

    /// <summary>git symbolic-ref's options: <c>-m</c> takes the reason a write records.</summary>
    private static readonly OptionTable GitSymbolicRefOptions = new("m", string.Empty, [], [], ["quiet", "delete", "short", "recurse", "no-recurse"]);

    /// <summary>git symbolic-ref's short options.</summary>
    private const string GitSymbolicRefLetters = "qdm";

    /// <summary>git notes' own options, before its verb: <c>--ref</c> names the notes it works on.</summary>
    private static readonly OptionTable GitNotesOptions = new(string.Empty, string.Empty, ["ref"], [], []);

    /// <summary>
    /// Whether a git command of a verb that writes git's state only reads where it is written so, as git reads its options
    /// wherever they stand among its words - an option's value never taken for an option, a long option by any start of its
    /// name that names it alone, a short one in a cluster (<see cref="Getopt"/>): git branch in a listing mode
    /// (<see cref="GitBranchListing"/>) or naming no branch, with no mode that writes; git tag in a listing mode
    /// (<see cref="GitTagListing"/>) or naming no tag, neither deleting nor verifying; git config getting or listing
    /// (<see cref="GitConfigGets"/>, its verbs <see cref="GitConfigReadVerbs"/>) or naming one key alone, adding,
    /// replacing, unsetting, renaming, removing and editing none; git symbolic-ref naming one ref, deleting none; git reflog
    /// with nothing after it, an option first - its showing, which takes every word after - or a verb that reads
    /// (<see cref="GitReflogReads"/>); and git notes, after its <c>--ref</c>, with no verb or one that reads
    /// (<see cref="GitNotesReads"/>). An option this does not know is held as one that may write - a negation among them,
    /// <c>--no-list</c> undoing the <c>--list</c> before it - and so is a word the shell expands where it may be any of
    /// them, and a reading the analysis makes of a configuration set where the command runs (<see cref="RuleReading.Configures"/>).
    /// </summary>
    /// <param name="reading">The command as its rules read it, each word with how it stands to the shell.</param>
    /// <returns>
    /// <see cref="AgentRuleMatch.Yes"/> where the command only reads; <see cref="AgentRuleMatch.Expanded"/> where a word the
    /// shell expands may make it one that writes; <see cref="AgentRuleMatch.No"/> where it writes, reads by an option this
    /// does not know, or is none of these verbs.
    /// </returns>
    internal static AgentRuleMatch GitReadsOnly(RuleReading reading)
    {
        var words = reading.Words;

        // A configuration set where the command runs is the git config it is, whatever words the reading carries.
        if (reading.Configures || words.Count < 2 || !Is(Name(words[0]), "git"))
        {
            return AgentRuleMatch.No;
        }

        // From the verb on, as git's option reader is handed them; and whether a word from the first after the verb to
        // the one at 'to' is one the shell expands.
        IReadOnlyList<string> verb = [.. words.Skip(1)];
        bool Expands(int to) => Enumerable.Range(2, Math.Max(0, Math.Min(to, words.Count - 1) - 1)).Any(at => Expansion(words[at], reading.QuotedAt(at)) is not null);
        var all = words.Count - 1;

        switch (words[1])
        {
            case "branch":
                return Expands(all) ? AgentRuleMatch.Expanded : Lists(verb, GitBranchOptions, GitBranchLetters, GitBranchListing, GitBranchWrites);

            case "tag":
                return Expands(all) ? AgentRuleMatch.Expanded : Lists(verb, GitTagOptions, GitTagLetters, GitTagListing, GitTagModes);

            case "config":
            {
                if (Expands(all))
                {
                    return AgentRuleMatch.Expanded;
                }

                var (options, positional) = ConfigWords(words, 2);
                bool Knows(string option) => new[] { GitConfigValued, GitConfigReads, GitConfigAdds, GitConfigShaping }.Any(known => known.Contains(option, StringComparer.Ordinal));

                return options.Any(option => !Knows(option) || GitConfigUnsets.Contains(option, StringComparer.Ordinal) || GitConfigAdds.Contains(option, StringComparer.Ordinal)) ? AgentRuleMatch.No
                    : options.Any(option => GitConfigGets.Contains(option, StringComparer.Ordinal)) ? AgentRuleMatch.Yes
                    : positional.Count > 0 && GitConfigVerbs.Contains(positional[0], StringComparer.Ordinal) ? (GitConfigReadVerbs.Contains(positional[0], StringComparer.Ordinal) ? AgentRuleMatch.Yes : AgentRuleMatch.No)
                    : positional.Count == 1 ? AgentRuleMatch.Yes : AgentRuleMatch.No;
            }

            case "symbolic-ref":
            {
                if (Expands(all))
                {
                    return AgentRuleMatch.Expanded;
                }

                var (options, operands, unknown) = Getopt(verb, GitSymbolicRefOptions);

                return !unknown && options.All(option => Known(option.Option, GitSymbolicRefOptions, GitSymbolicRefLetters) && option.Option is not ("d" or "delete")) && operands.Count == 1
                    ? AgentRuleMatch.Yes
                    : AgentRuleMatch.No;
            }

            case "reflog":
                // reflog takes no option of its own: one first, or any word no verb of its, is its showing's, with every word after.
                return words.Count == 2 ? AgentRuleMatch.Yes
                    : Expands(2) ? AgentRuleMatch.Expanded
                    : words[2].StartsWith('-') || GitReflogReads.Contains(words[2], StringComparer.Ordinal) ? AgentRuleMatch.Yes
                    : AgentRuleMatch.No;

            case "notes":
            {
                // Its verb is the first word no option and no option's value; the words after it are the verb's own.
                var (options, operands, _) = Getopt(verb, GitNotesOptions);
                var first = operands.Count > 0 ? operands[0].At : verb.Count;

                return Expands(first + 1) ? AgentRuleMatch.Expanded
                    : options.Any(option => option.Word < first && option.Option != "ref") ? AgentRuleMatch.No
                    : operands.Count == 0 || GitNotesReads.Contains(operands[0].Word, StringComparer.Ordinal) ? AgentRuleMatch.Yes
                    : AgentRuleMatch.No;
            }

            default:
                return AgentRuleMatch.No;
        }
    }

    /// <summary>
    /// Whether a git branch or git tag command (<paramref name="words"/>, its verb first) is a listing, as git reads its
    /// options: every option one this knows, none of <paramref name="modes"/>, and one of <paramref name="listing"/> among
    /// them or no name given.
    /// </summary>
    private static AgentRuleMatch Lists(IReadOnlyList<string> words, OptionTable table, string letters, IReadOnlyList<string> listing, IReadOnlyList<string> modes)
    {
        var (options, operands, unknown) = Getopt(words, table);
        var names = options.Select(option => option.Option).ToList();

        return !unknown
            && names.All(name => Known(name, table, letters))
            && !names.Any(name => modes.Contains(name, StringComparer.Ordinal))
            && (names.Any(name => listing.Contains(name.Length == 1 ? $"-{name}" : $"--{name}", StringComparer.Ordinal)) || operands.Count == 0)
                ? AgentRuleMatch.Yes
                : AgentRuleMatch.No;
    }

    /// <summary>Whether <paramref name="option"/>, as <see cref="Getopt"/> names one, is an option of <paramref name="table"/>'s: one of its <paramref name="letters"/>, or one of its long names.</summary>
    private static bool Known(string option, OptionTable table, string letters)
        => option.Length == 1
            ? letters.Contains(option[0], StringComparison.Ordinal)
            : table.LongValued.Concat(table.LongOptional).Concat(table.LongFlags).Contains(option, StringComparer.Ordinal);
}
