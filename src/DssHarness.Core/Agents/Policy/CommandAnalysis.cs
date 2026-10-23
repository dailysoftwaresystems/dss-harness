namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// Reads a command whole before any rule is matched against it: what one client hands as an
/// argument list and another as a shell string is flattened into commands, each a list of words,
/// however the command hides its program - and what it cannot read whole it refuses to read, so
/// the policy denies it.
/// </summary>
/// <remarks>
/// <para>
/// A shell string is split as the system's own shell splits it: on <c>;</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>|</c>, <c>&amp;</c>, line ends and a closing parenthesis nothing opened (a
/// <c>case</c> pattern's); the body of a command substitution (<c>$(...)</c>, backticks) and of a
/// subshell is read as commands of their own, and the shell's structural words - <c>if</c>,
/// <c>then</c>, <c>do</c>, <c>done</c> and the rest - never join a command; a line a backslash
/// continues is one line, <c>$'...'</c> is read with its escapes decoded, and a redirect's file is
/// a word with its operator before it (<c>&gt;out.txt</c>). The <c>NAME=value</c> words before a
/// program stay the command's own, its environment, and <c>env</c>'s pairs become them; wrappers
/// (<c>env</c>, <c>nohup</c>, <c>time</c>, <c>timeout</c>, <c>nice</c>, <c>xargs</c>,
/// <c>command</c>, <c>builtin</c>, <c>exec</c>, <c>eval</c> and PowerShell's
/// <c>Invoke-Expression</c>) are stripped with their own options; it goes into
/// <c>sh</c>/<c>bash</c>/<c>zsh -c</c> - and reads the words after the script as scripts too, for
/// the script may run them - into <c>cmd /c</c> and <c>/k</c> by cmd's own quoting, and into
/// <c>powershell</c>/<c>pwsh</c>'s <c>-Command</c> and a decoded <c>-EncodedCommand</c>, each by
/// any prefix PowerShell takes, by PowerShell's quoting; and a shell another program runs with
/// its script among its words (<c>find -exec sh -c ...</c>) is gone into as well.
/// </para>
/// <para>
/// What it cannot read whole it refuses, by a <see cref="NotSupportedException"/> naming why, and
/// the policy denies what it was refused: a script nested past <see cref="Depth"/> levels of going
/// into, parentheses nested past <see cref="Nesting"/>, a shell that reads its script from its
/// input, a program named by an expansion - a variable, a substitution, a pattern, a brace - and an
/// <c>xargs</c> that hands a shell its script from its input. What it does not see is said in
/// help, not guessed at: the inside of a script file a command runs, and a program another program
/// runs from words that do not name it.
/// </para>
/// <para>
/// A program is matched by its name as this reads it - its last segment, a Windows program's end
/// (<c>.exe</c>, <c>.com</c>, <c>.cmd</c>, <c>.bat</c>, <c>.ps1</c>) folded away - without regard
/// to case, so a wrapper or a shell named in either case is read as one: what this reads as a
/// command that is not one can only meet a rule that would refuse it, never lose one.
/// </para>
/// </remarks>
public static partial class CommandAnalysis
{
    /// <summary>How deep one command goes into the scripts its words name: a shell's <c>-c</c>, a decoded encoded command; a script nested deeper is refused.</summary>
    public const int Depth = 4;

    /// <summary>How deep the parentheses of one script nest - its substitutions and subshells - before it is refused.</summary>
    public const int Nesting = 32;

    /// <summary>
    /// The commands an argument list runs: the list as it is, and a script one of its words names
    /// gone into, its environment first where it carries one.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return [.. FromWords(argv, 1, []).Where(words => words.Count > 0)];
    }

    /// <summary>The commands a shell string runs, however it hides them.</summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        return [.. FromScript(shell, 1, []).Where(words => words.Count > 0)];
    }

    /// <summary>
    /// Whether <paramref name="word"/> is an assignment the shell reads before a command -
    /// <c>NAME=value</c>, a name of letters, digits and underscores that begins with no digit -
    /// which gives the command its environment.
    /// </summary>
    public static bool IsAssignment(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var equals = word.IndexOf('=', StringComparison.Ordinal);

        return equals > 0
            && (char.IsAsciiLetter(word[0]) || word[0] == '_')
            && word[..equals].All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>How many of a command's first words are its environment: the assignments before its program.</summary>
    /// <param name="words">One command's words, as <see cref="Commands(string)"/> or <see cref="Commands(IReadOnlyList{string})"/> flattened it.</param>
    public static int EnvironmentOf(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        var at = 0;

        while (at < words.Count && IsAssignment(words[at]))
        {
            at++;
        }

        return at;
    }

    /// <summary>
    /// The readings of a command the rules are matched against, a rule that meets any of them meeting
    /// the command: its words with its environment left out; git's own options dropped before its
    /// subcommand, so <c>git -C x push</c> meets a rule on <c>git push</c>, and gh's own flags before
    /// its command's path, so <c>gh pr -R o/r merge</c> meets one on <c>gh pr merge</c> - and, before
    /// that reading, a git command whose configuration is set where it runs read as the
    /// <c>git config</c> it is too: a <c>-c</c> or a <c>--config-env</c> of any key but the ones that
    /// only shape what git prints (<see cref="ShowingKeys"/>), <c>--exec-path</c> given a directory,
    /// and any command that sets git's configuration through the environment
    /// (<c>GIT_CONFIG_COUNT</c> and its pairs, <c>GIT_EXEC_PATH</c>, <c>GIT_TEMPLATE_DIR</c>) - for
    /// <c>git -c alias.x=commit x</c> would otherwise pass every rule on <c>commit</c>, and
    /// <c>git -c core.pager=less push</c> still meets every rule on <c>git push</c>.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one.</param>
    /// <param name="comparison">How git's own name compares: names compare as this platform compares paths.</param>
    /// <exception cref="NotSupportedException">git or gh is given an option before its subcommand this does not know, which may take the word after it.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> ForRules(IReadOnlyList<string> words, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(words);

        var readings = new List<IReadOnlyList<string>>();

        // Setting git's configuration through the environment is configuring git, whatever runs
        // with it: an export alone, an env, the words before git itself.
        if (words.FirstOrDefault(SetsGitConfiguration) is { } setting)
        {
            readings.Add(["git", "config", setting.TrimStart('$').Split('=', 2)[0], .. words.Skip(EnvironmentOf(words))]);
        }

        var (command, configured) = Plain(words, comparison);

        if (configured)
        {
            readings.Add(["git", "config", .. command.Skip(1)]);
        }

        readings.Add(command);

        return readings;
    }

    /// <summary>
    /// A command's words as the rules read them (<see cref="ForRules"/>) - its environment left out,
    /// git's and gh's own options before their command dropped - and whether git's configuration is
    /// set on its command line.
    /// </summary>
    private static (IReadOnlyList<string> Command, bool Configured) Plain(IReadOnlyList<string> words, StringComparison comparison)
    {
        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return (environment == 0 ? words : [.. words.Skip(environment)], false);
        }

        if (string.Equals(Name(words[environment]), "gh", comparison))
        {
            return (GhForRules([.. words.Skip(environment)]), false);
        }

        if (!string.Equals(Name(words[environment]), "git", comparison))
        {
            return (environment == 0 ? words : [.. words.Skip(environment)], false);
        }

        var config = false;
        var at = environment + 1;

        while (at < words.Count && words[at].StartsWith('-'))
        {
            var option = words[at];

            // -c and --config-env, the setting glued into them or the next word: any key but the
            // ones that only shape what git prints makes the command the git config it is.
            if (option is "-c" or "--config-env")
            {
                config |= at + 1 >= words.Count || Configures(words[at + 1]);
                at += 2;
                continue;
            }

            if (option.StartsWith("--config-env=", StringComparison.Ordinal) || (option.StartsWith("-c", StringComparison.Ordinal) && option.Length > 2))
            {
                config |= Configures(option.StartsWith("--", StringComparison.Ordinal) ? option["--config-env=".Length..] : option[2..]);
                at++;
                continue;
            }

            // --exec-path given a directory is where git finds the programs of its subcommands.
            if (option.StartsWith("--exec-path=", StringComparison.Ordinal))
            {
                config = true;
                at++;
                continue;
            }

            // -C, its path glued into it or the next word; an option with its value in the same
            // word stands as one word too. The options that take a value in the next word are the
            // ones git names for them, and an option git does not name is refused: dropping a word
            // that was the subcommand would read the command as one it is not.
            if ((option.StartsWith("-C", StringComparison.Ordinal) && option.Length > 2) || (option.StartsWith("--", StringComparison.Ordinal) && option.Contains('=', StringComparison.Ordinal) && GitTakesValue(option.Split('=', 2)[0])))
            {
                at++;
                continue;
            }

            if (option == "-C" || GitTakesValue(option))
            {
                at += 2;
                continue;
            }

            if (GitValueLess(option))
            {
                at++;
                continue;
            }

            throw new NotSupportedException($"'{option}' is an option of git's own this does not read, which may take the word after it as its value");
        }

        return at >= words.Count ? ([.. words.Skip(environment)], false) : (["git", .. words.Skip(at)], config);
    }

    /// <summary>
    /// A gh command as its rules read it: gh, the words of its command's path - its flags before
    /// the path's end taken out, a flag's value with it - then the rest as it stands. gh finds its
    /// command past any flag, so <c>gh pr -R o/r merge 5</c> is <c>gh pr merge 5</c>. A flag before
    /// the path's end that this does not know may take the word after it, and is refused.
    /// </summary>
    private static IReadOnlyList<string> GhForRules(IReadOnlyList<string> words)
    {
        var path = new List<string> { "gh" };
        var at = 1;

        // The path of the commands a rule names on gh is two words at most: 'pr merge', 'api',
        // 'workflow run', 'alias set'.
        while (at < words.Count && path.Count < 3)
        {
            var word = words[at];

            if (!word.StartsWith('-'))
            {
                path.Add(word);
                at++;

                // gh api's path ends at api: what follows is its endpoint and its own flags.
                if (word.Equals("api", StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                continue;
            }

            if (word is "-R" or "--repo" && at + 1 < words.Count)
            {
                at += 2;
                continue;
            }

            if (word.StartsWith("--repo=", StringComparison.Ordinal) || (word.StartsWith("-R", StringComparison.Ordinal) && word.Length > 2) || word is "-h" or "--help" or "--version")
            {
                at++;
                continue;
            }

            // Past the path's first word a flag is its command's own: the path is what it is.
            if (path.Count > 1)
            {
                break;
            }

            throw new NotSupportedException($"'{word}' is a flag of gh's own this does not read, which may take the word after it as its value");
        }

        return [.. path, .. words.Skip(at)];
    }

    /// <summary>git's global options that take their value as the next word, read before its subcommand.</summary>
    private static bool GitTakesValue(string option)
        => option is "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix" or "--attr-source" or "--shallow-file" or "--list-cmds";

    /// <summary>git's global options that take no value, read before its subcommand.</summary>
    private static bool GitValueLess(string option)
        => option is "--bare" or "--no-optional-locks" or "--literal-pathspecs" or "--glob-pathspecs"
            or "--noglob-pathspecs" or "--icase-pathspecs" or "--no-replace-objects" or "--no-lazy-fetch" or "--no-advice"
            or "--html-path" or "--man-path" or "--info-path" or "--exec-path" or "-p" or "--paginate" or "-P" or "--no-pager"
            or "-v" or "--version" or "-h" or "--help";

    /// <summary>
    /// Whether a <c>git -c</c> or <c>--config-env</c> setting configures git where it runs: any
    /// key but one of <see cref="ShowingKeys"/>, which only shape what git prints. An alias, a key
    /// naming a program git runs, a hook's directory, a remote's or a helper's - each would run, or
    /// make a name for, a command of the setter's choosing outside every rule on git's subcommand.
    /// </summary>
    private static bool Configures(string setting)
    {
        var key = setting.Split('=', 2)[0];

        return !ShowingKeys.Any(shown => shown.EndsWith('.')
            ? key.StartsWith(shown, StringComparison.OrdinalIgnoreCase)
            : key.Equals(shown, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The keys a <c>git -c</c> may set without making the command the <c>git config</c> it is:
    /// each only shapes what git prints, or how it names a path - a section of them by its name and
    /// a dot. Every other key is configuration.
    /// </summary>
    private static readonly string[] ShowingKeys =
    [
        "color.", "advice.", "column.", "i18n.", "status.", "pretty.", "blame.", "grep.", "versionsort.",
        "core.quotepath", "core.abbrev", "core.autocrlf", "core.safecrlf", "core.longpaths",
        "log.date", "log.decorate", "log.abbrevcommit", "log.follow", "log.graphcolors", "log.mailmap", "log.excludedecoration",
        "diff.noprefix", "diff.mnemonicprefix", "diff.renames", "diff.algorithm", "diff.colormoved", "diff.colormovedws",
        "diff.relative", "diff.indentheuristic", "diff.context", "diff.interhunkcontext", "diff.statgraphwidth", "diff.suppressblankempty",
        "merge.conflictstyle", "init.defaultbranch", "user.name", "user.email", "protocol.version",
    ];

    /// <summary>
    /// The environment names through which git reads its configuration (every <c>GIT_CONFIG</c>
    /// name - the count and its keys and values, the parameters, the files it reads), the directory
    /// it finds its programs in, and the template it copies hooks from: set for a command, each is a
    /// <c>git -c</c> of the setter's choosing. A name whose value is a program git runs is read as
    /// that program's command line instead (<see cref="ProgramValued"/>).
    /// </summary>
    private static readonly string[] GitConfigurationNames = ["GIT_EXEC_PATH", "GIT_TEMPLATE_DIR"];

    /// <summary>
    /// Whether <paramref name="word"/> sets git's configuration through the environment:
    /// <c>NAME=value</c> or PowerShell's <c>$env:NAME=value</c> of such a name.
    /// </summary>
    private static bool SetsGitConfiguration(string word)
    {
        var assignment = word.StartsWith("$env:", StringComparison.OrdinalIgnoreCase) ? word["$env:".Length..] : word;

        if (!IsAssignment(assignment))
        {
            return false;
        }

        var name = assignment.Split('=', 2)[0];

        return name.StartsWith("GIT_CONFIG", StringComparison.OrdinalIgnoreCase)
            || GitConfigurationNames.Contains(name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The ends of a Windows program's name that are folded away: what Windows finds a command by.</summary>
    private static readonly string[] ProgramEnds = [".exe", ".com", ".cmd", ".bat", ".ps1", ".dll"];

    /// <summary>The word as a program's name: its last segment, with a Windows program's end folded away.</summary>
    internal static string Name(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return word;
        }

        // Either separator ends a directory, whichever system this runs on: a Windows path is read
        // on Linux too.
        var name = word.TrimEnd('/', '\\').Replace('\\', '/').Split('/')[^1];

        return ProgramEnds.FirstOrDefault(end => name.Length > end.Length && name.EndsWith(end, StringComparison.OrdinalIgnoreCase)) is { } folded
            ? name[..^folded.Length]
            : name;
    }

    /// <summary>
    /// The words <paramref name="word"/> stands for once the shell expands its braces - <c>{a,b}c</c>
    /// is <c>ac</c> and <c>bc</c> - so a root action spelled in braces is still one; the word alone
    /// where it holds none. A word that spells more than <see cref="BraceBound"/> words is refused,
    /// for a word past the bound would go unread.
    /// </summary>
    /// <exception cref="NotSupportedException">The word spells more words than the bound.</exception>
    internal static IReadOnlyList<string> BraceExpansions(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var results = new List<string>();
        var pending = new Stack<string>([word]);

        while (pending.Count > 0)
        {
            if (results.Count + pending.Count > BraceBound)
            {
                throw new NotSupportedException($"'{word}' spells more than {BraceBound} words by its braces, which this does not read");
            }

            var current = pending.Pop();

            if (BraceGroup(current) is not { } group)
            {
                results.Add(current);
                continue;
            }

            foreach (var alternative in group.Alternatives)
            {
                pending.Push(current[..group.Open] + alternative + current[(group.Close + 1)..]);
            }
        }

        return results;
    }

    /// <summary>How many words one word's braces may spell before it is refused.</summary>
    private const int BraceBound = 64;

    /// <summary>
    /// The first brace group of a word that holds a comma at its own level, and its alternatives;
    /// null where it holds none. Read in one pass, every brace matched by a stack, so no word's
    /// length makes it slow.
    /// </summary>
    private static (int Open, int Close, IReadOnlyList<string> Alternatives)? BraceGroup(string word)
    {
        var open = new Stack<(int At, List<int> Commas)>();
        (int Open, int Close, List<int> Commas)? first = null;

        for (var at = 0; at < word.Length; at++)
        {
            if (word[at] == '{')
            {
                open.Push((at, []));
            }
            else if (word[at] == ',' && open.Count > 0)
            {
                open.Peek().Commas.Add(at);
            }
            else if (word[at] == '}' && open.Count > 0)
            {
                var (opened, commas) = open.Pop();

                if (commas.Count > 0 && (first is null || opened < first.Value.Open))
                {
                    first = (opened, at, commas);
                }
            }
        }

        if (first is not { } group)
        {
            return null;
        }

        var alternatives = new List<string>();
        var start = group.Open + 1;

        foreach (var comma in group.Commas)
        {
            alternatives.Add(word[start..comma]);
            start = comma + 1;
        }

        alternatives.Add(word[start..group.Close]);
        return (group.Open, group.Close, alternatives);
    }

    /// <summary>
    /// The commands the words run, the words' own command among them: its environment kept before
    /// it, wrappers stripped, a script one of the words names gone into - at the program, and at a
    /// shell another program runs - an <c>eval</c> read as the script it is, and the value of a
    /// variable that names a program another runs (<c>GIT_PAGER</c>, <c>EDITOR</c>) read as the
    /// script it is.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> FromWords(IReadOnlyList<string> words, int depth, IReadOnlyList<string> environment)
    {
        // The assignments this level gives, whose program-valued names run their values.
        var given = new List<string>();

        // Each wrapper is stripped in turn, here rather than by going into it, so no length of
        // wrappers overflows the stack.
        for (var wrappers = 0; ; wrappers++)
        {
            if (wrappers > Nesting)
            {
                throw new NotSupportedException($"'{string.Join(' ', words)}' wraps its program past {Nesting} times, which this does not read");
            }

            // PowerShell's '$env:NAME = value' sets the environment of what runs after it, as an
            // export does: an assignment, never a program.
            if (PowerShellEnvironment(words) is { } set)
            {
                words = [set];
            }

            // The assignments before the program are the command's environment, carried to what it runs.
            var assigned = EnvironmentOf(words);

            if (assigned > 0)
            {
                given.AddRange(words.Take(assigned));
                environment = [.. environment, .. words.Take(assigned)];
                words = [.. words.Skip(assigned)];
            }

            if (words.Count == 0)
            {
                // Assignments alone run nothing; they stay a command of their own, so what they name is
                // still met by every rule on a path, and a program one names is read.
                if (environment.Count > 0)
                {
                    yield return environment;
                }

                foreach (var command in ProgramsNamed(given, depth, environment))
                {
                    yield return command;
                }

                yield break;
            }

            var name = Name(words[0]);

            // A script the command names, gone into: sh -c 'a && b' and the words after its script, a
            // here-string or here-document a shell reads, cmd /c a & b, powershell -Command and a
            // decoded -EncodedCommand, trap's, watch's, script's and flock's command line.
            if (Into(name, words, program: true) is { } scripts)
            {
                foreach (var command in GoneInto(scripts, depth, environment))
                {
                    yield return command;
                }

                foreach (var command in ProgramsNamed(given, depth, environment))
                {
                    yield return command;
                }

                yield break;
            }

            // env -S takes a command line of its own, which it splits into the words of the command it
            // runs, the words after it among them.
            if (Is(name, "env") && SplitString(words) is { } split)
            {
                if (depth > Depth)
                {
                    throw Nested(words);
                }

                foreach (var command in FromWords([.. split.Pairs, .. Script(split.Script, 0).SelectMany(command => command), .. split.Tail], depth + 1, environment))
                {
                    yield return command;
                }

                yield break;
            }

            // eval and PowerShell's Invoke-Expression join their arguments into one script and run that.
            if (Is(name, "eval", "iex", "Invoke-Expression"))
            {
                if (words.Count > 1)
                {
                    var script = string.Join(' ', words.Skip(1));

                    foreach (var command in GoneInto([Is(name, "eval") ? script : PowerShellAsPosix(script)], depth, environment))
                    {
                        yield return command;
                    }
                }

                yield break;
            }

            // Wrappers, with their own options and values, stripped until a command is left; env's
            // pairs become the command's environment.
            if (Stripped(name, words) is not { } stripped)
            {
                break;
            }

            words = stripped;
        }

        // The program itself: one named by an expansion the shell makes is not read at all.
        if (Expansion(words[0]) is { } expansion)
        {
            throw new NotSupportedException($"'{words[0]}' names the program it runs by {expansion}, which this does not read");
        }

        yield return environment.Count == 0 ? words : [.. environment, .. words];

        // A program another runs by a variable: the environment's own, and what an export or a
        // declaration of the command gives.
        IEnumerable<string> declared = Is(Name(words[0]), "export", "declare", "typeset", "local", "readonly")
            ? words.Skip(1).Where(IsAssignment)
            : [];

        foreach (var command in ProgramsNamed([.. given, .. declared], depth, environment))
        {
            yield return command;
        }

        // A program git's own settings name, which git runs through the shell: core.pager, core.sshCommand, an
        // alias's '!' line, as the variables above are read.
        if (Is(Name(words[0]), "git") && GitSettingScripts(words, environment) is { Count: > 0 } settings)
        {
            foreach (var command in GoneInto(settings, depth, environment))
            {
                yield return command;
            }
        }

        // A shell another program runs with its script among the command's words - find -exec,
        // ssh, watch - is gone into too: what it runs is a command of its own.
        for (var at = 1; at < words.Count; at++)
        {
            var rest = words.Skip(at).ToList();

            if (Into(Name(words[at]), rest, program: false) is { } inner)
            {
                foreach (var command in GoneInto(inner, depth, environment))
                {
                    yield return command;
                }
            }
            else if (Is(Name(words[at]), "env") && SplitString(rest) is { } innerSplit)
            {
                foreach (var command in GoneInto([innerSplit.Script], depth, environment))
                {
                    yield return command;
                }
            }
        }
    }

    /// <summary>
    /// The names whose value is a program another runs through the shell - git's pager, editor,
    /// ssh and askpass, its external diff, and the pager and editor any program runs: set for a
    /// command, each runs a command of the setter's choosing, read as the script it is.
    /// </summary>
    private static readonly string[] ProgramValued =
    [
        "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_SSH", "GIT_SSH_COMMAND", "GIT_ASKPASS",
        "GIT_EXTERNAL_DIFF", "PAGER", "EDITOR", "VISUAL", "SSH_ASKPASS", "MANPAGER",
    ];

    /// <summary>
    /// The commands the program-valued names among <paramref name="assignments"/> run, each value read as a script - and
    /// what git's configuration through the environment sets, which git may run so too: each <c>GIT_CONFIG_VALUE_n</c>
    /// of a key git runs as a command line (<see cref="RunsProgram"/>) or of none named, and each such setting
    /// <c>GIT_CONFIG_PARAMETERS</c> holds.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> ProgramsNamed(IReadOnlyList<string> assignments, int depth, IReadOnlyList<string> environment)
    {
        var pairs = assignments.Select(assignment => assignment.Split('=', 2)).Where(pair => pair[1].Trim().Length > 0).ToList();
        var values = new List<string>();

        foreach (var pair in pairs)
        {
            if (ProgramValued.Contains(pair[0], StringComparer.OrdinalIgnoreCase))
            {
                values.Add(pair[1]);
            }
            else if (pair[0].StartsWith("GIT_CONFIG_VALUE_", StringComparison.OrdinalIgnoreCase))
            {
                var key = pairs.LastOrDefault(other => other[0].Equals("GIT_CONFIG_KEY_" + pair[0]["GIT_CONFIG_VALUE_".Length..], StringComparison.OrdinalIgnoreCase));

                if (key is null || RunsProgram(key[1]))
                {
                    values.Add(SettingScript(key?[1] ?? string.Empty, pair[1]));
                }
            }
            else if (pair[0].Equals("GIT_CONFIG_PARAMETERS", StringComparison.OrdinalIgnoreCase))
            {
                values.AddRange(Script(pair[1], 0)
                    .SelectMany(command => command)
                    .Where(setting => setting.Contains('=', StringComparison.Ordinal) && RunsProgram(setting.Split('=', 2)[0]))
                    .Select(setting => SettingScript(setting.Split('=', 2)[0], setting.Split('=', 2)[1])));
            }
        }

        return values.Count == 0 ? [] : GoneInto(values, depth, environment);
    }

    /// <summary>
    /// The scripts a git command's own settings run: the value of each <c>-c</c> before its subcommand, and of each
    /// <c>--config-env</c>'s variable, of a key git runs as a command line (<see cref="RunsProgram"/>) - every other
    /// value is data git reads, never a command.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A <c>--config-env</c> of such a key names a variable the command does not set: what it runs is a value set
    /// elsewhere - an export before it - which this does not read.
    /// </exception>
    private static List<string> GitSettingScripts(IReadOnlyList<string> words, IReadOnlyList<string> environment)
    {
        var scripts = new List<string>();

        for (var at = 1; at < words.Count && words[at].StartsWith('-'); at++)
        {
            var option = words[at];
            var fromEnvironment = option.StartsWith("--config-env", StringComparison.Ordinal);
            string? setting = null;

            if (option is "-c" or "--config-env")
            {
                setting = at + 1 < words.Count ? words[++at] : null;
            }
            else if (option.StartsWith("--config-env=", StringComparison.Ordinal))
            {
                setting = option["--config-env=".Length..];
            }
            else if (option.StartsWith("-c", StringComparison.Ordinal) && option.Length > 2)
            {
                setting = option[2..];
            }
            else if (option == "-C" || GitTakesValue(option))
            {
                at++;
            }

            if (setting is null || !setting.Contains('=', StringComparison.Ordinal) || !RunsProgram(setting.Split('=', 2)[0]))
            {
                continue;
            }

            var (key, value) = (setting.Split('=', 2)[0], setting.Split('=', 2)[1]);

            if (!fromEnvironment)
            {
                scripts.Add(SettingScript(key, value));
            }
            else if (environment.Select(assignment => assignment.Split('=', 2)).LastOrDefault(pair => pair[0] == value) is { Length: 2 } named)
            {
                scripts.Add(SettingScript(key, named[1]));
            }
            else
            {
                throw new NotSupportedException($"'{option}' runs what the variable {value} holds, which the command does not set, and this does not read");
            }
        }

        return scripts;
    }

    /// <summary>
    /// The command line a git setting's value is: an alias's <c>!</c> line without it, for git runs it through the shell,
    /// and any other alias the git command it names.
    /// </summary>
    private static string SettingScript(string key, string value)
        => !key.StartsWith("alias.", StringComparison.OrdinalIgnoreCase) ? value
            : value.StartsWith('!') ? value[1..]
            : "git " + value;

    /// <summary>
    /// Whether git runs the value of <paramref name="key"/> as a command line: its pager, editors, ssh, askpass, monitor,
    /// proxy and credential helpers, an external diff and a diff driver's, a merge driver, a filter's clean, smudge and
    /// process, a tool's or a browser's command, a signing program, a remote's upload and receive packs, and an alias -
    /// keys compared in any case, as git compares them.
    /// </summary>
    private static bool RunsProgram(string key)
    {
        var named = key.ToLowerInvariant();

        return ProgramKeys.Contains(named)
            || named.StartsWith("pager.", StringComparison.Ordinal)
            || named.StartsWith("alias.", StringComparison.Ordinal)
            || (named.Count(character => character == '.') >= 2 && ProgramKeyEnds.Any(end => named.EndsWith(end, StringComparison.Ordinal)));
    }

    /// <summary>The keys of git's configuration, in its lower case, whose value it runs as a command line.</summary>
    private static readonly HashSet<string> ProgramKeys =
    [
        "core.pager", "core.editor", "core.sshcommand", "core.askpass", "core.fsmonitor", "core.gitproxy",
        "core.alternaterefscommand", "sequence.editor", "credential.helper", "diff.external", "gpg.program",
        "interactive.difffilter", "uploadpack.packobjectshook", "sendemail.sendmailcmd",
    ];

    /// <summary>
    /// The ends of git's keys of a section and a subsection (<c>diff.&lt;driver&gt;.textconv</c>) whose value it runs as a
    /// command line.
    /// </summary>
    private static readonly string[] ProgramKeyEnds =
        [".textconv", ".command", ".driver", ".clean", ".smudge", ".process", ".cmd", ".helper", ".program", ".uploadpack", ".receivepack"];

    /// <summary>
    /// PowerShell's setting of a variable of the environment - <c>$env:NAME=value</c> or
    /// <c>$env:NAME = value</c> - as the assignment it is, <c>NAME=value</c>; null where the
    /// words are no such setting.
    /// </summary>
    private static string? PowerShellEnvironment(IReadOnlyList<string> words)
    {
        if (words.Count == 0 || !words[0].StartsWith("$env:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var named = words[0]["$env:".Length..];

        if (words.Count == 1 && IsAssignment(named))
        {
            return named;
        }

        return words.Count == 3 && words[1] == "=" && IsAssignment(named + "=x") ? $"{named}={words[2]}" : null;
    }

    /// <summary>The commands each script runs, one level further in: refused past <see cref="Depth"/>.</summary>
    private static IEnumerable<IReadOnlyList<string>> GoneInto(IReadOnlyList<string> scripts, int depth, IReadOnlyList<string> environment)
    {
        if (depth > Depth)
        {
            throw Nested(scripts);
        }

        foreach (var script in scripts)
        {
            foreach (var command in FromScript(script, depth + 1, environment))
            {
                yield return command;
            }
        }
    }

    /// <summary>The refusal of a script nested past <see cref="Depth"/> levels: a root action inside it would not be seen.</summary>
    private static NotSupportedException Nested(IReadOnlyList<string> words)
        => new($"'{string.Join(' ', words)}' is a script nested past {Depth} levels of going into, which this does not read");

    /// <summary>
    /// What expansion names the word, where the shell would make one before it runs it: a
    /// substitution (<c>$(...)</c>, a backtick, kept as <c>$(...)</c>, <c>&lt;(...)</c>), a
    /// variable (<c>$X</c>, cmd's <c>%X%</c>), a pattern (<c>*</c>, <c>?</c>, <c>[</c>) or a brace
    /// (<c>{a,b}</c>, <c>{a..b}</c>); null where it names none.
    /// </summary>
    internal static string? Expansion(string word)
        => word.Contains("$(", StringComparison.Ordinal) || word.Contains("<(", StringComparison.Ordinal) || word.Contains(">(", StringComparison.Ordinal) ? "a substitution"
            : word.Contains('$', StringComparison.Ordinal) || word.Contains('%', StringComparison.Ordinal) ? "a variable"
            : word.IndexOfAny(['*', '?']) >= 0 || (word.IndexOf('[', StringComparison.Ordinal) is var open and >= 0 && word.IndexOf(']', open + 1) > open + 1) ? "a pattern"
            : word.Contains('{', StringComparison.Ordinal) && (word.Contains(',', StringComparison.Ordinal) || word.Contains("..", StringComparison.Ordinal)) ? "a brace"
            : null;

    /// <summary>
    /// The command line <c>env -S</c> (<c>--split-string</c>) takes, the pairs before it and the
    /// words that follow it: null where the words name none. Read before any word of the command
    /// proper, as env reads it.
    /// </summary>
    private static (IReadOnlyList<string> Pairs, string Script, IReadOnlyList<string> Tail)? SplitString(IReadOnlyList<string> words)
    {
        var pairs = new List<string>();

        for (var at = 1; at < words.Count; at++)
        {
            var option = words[at];

            if (option == "-S" || option == "--split-string")
            {
                return at + 1 < words.Count
                    ? (pairs, words[at + 1], [.. words.Skip(at + 2)])
                    : null;
            }

            // Glued to its value: '-Ssudo x', '--split-string=sudo x'.
            if (option.StartsWith("-S", StringComparison.Ordinal) && option.Length > 2)
            {
                return (pairs, option[2..], [.. words.Skip(at + 1)]);
            }

            if (option.StartsWith("--split-string=", StringComparison.Ordinal))
            {
                return (pairs, option["--split-string=".Length..], [.. words.Skip(at + 1)]);
            }

            // Before the option: the pairs it gives, and the names it takes away again.
            if (IsAssignment(option))
            {
                pairs.Add(option);
                continue;
            }

            if ((option == "-u" || option == "--unset") && at + 1 < words.Count)
            {
                at++;
                continue;
            }

            if (option is "-i" or "-0" or "--ignore-environment" or "--null")
            {
                continue;
            }

            return null;
        }

        return null;
    }

    /// <summary>
    /// The scripts <paramref name="name"/> would run with <paramref name="words"/>, each ready for
    /// the shell reader, where it names one; null where it names none. Where it runs a script this
    /// cannot read - a shell that reads its script from what an earlier command printed, a script
    /// file named by an expansion - it is refused, where <paramref name="program"/> says the words
    /// are a command's own; a word another program takes as its argument (<c>which bash</c>) is
    /// never refused so.
    /// </summary>
    /// <param name="name">The program's name, as <see cref="Name"/> reads it.</param>
    /// <param name="words">The program and its words, its redirects after them.</param>
    /// <param name="program">Whether the words are a command's own, its program first, rather than another program's arguments.</param>
    private static IReadOnlyList<string>? Into(string name, IReadOnlyList<string> words, bool program)
    {
        var redirects = words.Where(IsRedirect).ToList();
        words = [.. words.Where(word => !IsRedirect(word))];

        if (words.Skip(1).Any(word => word is "--version" or "--help" or "-?" or "/?"
            || word.Equals("-Version", StringComparison.OrdinalIgnoreCase) || word.Equals("-Help", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        if (Is(name, Shells))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option == "--")
                {
                    return at + 1 < words.Count ? ScriptFile(words[at + 1], program) : Input(name, redirects, program);
                }

                if (!(option.StartsWith('-') || option.StartsWith('+')))
                {
                    return ScriptFile(option, program);
                }

                // An option that takes a value in the next word: the value is no script and no option.
                if (ShellValueTaking.Contains(option, StringComparer.Ordinal))
                {
                    at++;
                    continue;
                }

                // The option that carries c takes the next word as its script, alone (-c) or glued
                // into a short cluster (-lc); a long option (--norc among them) is never a short
                // cluster, however many letters it holds. The words after the script are its own
                // $0, $1 and on, which the script may run: read as scripts too.
                var cluster = !option.StartsWith("--", StringComparison.Ordinal) && option.Length > 1;

                if (option == "-c" || (cluster && option.Contains('c', StringComparison.Ordinal)))
                {
                    return at + 1 < words.Count ? [.. words.Skip(at + 1)] : null;
                }

                // -s reads the script from the input, the words after it its arguments.
                if (cluster && option.Contains('s', StringComparison.Ordinal))
                {
                    break;
                }
            }

            return Input(name, redirects, program);
        }

        if (Is(name, "source", "."))
        {
            return words.Count > 1 ? ScriptFile(words[1], program) : null;
        }

        if (Is(name, "cmd"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                // /k runs what it is given as /c does, and stays open after: what it runs is what
                // the rules must meet. cmd takes the command glued to either too.
                if (words[at].StartsWith("/c", StringComparison.OrdinalIgnoreCase) || words[at].StartsWith("/k", StringComparison.OrdinalIgnoreCase))
                {
                    var glued = words[at][2..];
                    var script = string.Join(' ', (glued.Length > 0 ? [glued] : Array.Empty<string>()).Concat(words.Skip(at + 1)));

                    return script.Length > 0 ? [CmdAsPosix(script)] : null;
                }
            }

            return Input(name, redirects, program, CmdAsPosix);
        }

        if (Is(name, "powershell", "pwsh"))
        {
            // Every word that is no parameter may be where Windows PowerShell's command begins, by
            // position, or a parameter's value: each is read as the command it may begin, so none is
            // missed for a parameter whose value this misjudged. pwsh takes a script file so.
            var positional = new List<string>();
            string? file = null;

            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option == "-")
                {
                    return Input(name, redirects, program, PowerShellAsPosix);
                }

                if (!option.StartsWith('-') && !option.StartsWith('/'))
                {
                    if (Is(name, "powershell"))
                    {
                        positional.Add(PowerShellAsPosix(string.Join(' ', words.Skip(at))));
                    }
                    else
                    {
                        file ??= option;
                    }

                    continue;
                }

                var parameter = option[1..].ToLowerInvariant();

                if (parameter.Length == 0)
                {
                    continue;
                }

                if (parameter == "ec" || "encodedcommand".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 < words.Count ? [.. positional, PowerShellAsPosix(Decode(words[at + 1]))] : null;
                }

                if (parameter is "cwa" or "commandwithargs" || "command".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 < words.Count ? [.. positional, PowerShellAsPosix(string.Join(' ', words.Skip(at + 1)))] : null;
                }

                if ("file".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return positional.Count > 0 ? positional : at + 1 < words.Count ? ScriptFile(words[at + 1], program) : null;
                }
            }

            return positional.Count > 0 ? positional
                : file is not null ? ScriptFile(file, program)
                : Input(name, redirects, program, PowerShellAsPosix);
        }

        // trap's first word is the command it runs at a signal; script's and flock's -c, and the
        // words watch runs through the shell, are command lines of their own.
        if (Is(name, "trap"))
        {
            var first = words.Skip(1).SkipWhile(word => word == "--").FirstOrDefault();

            return first is null or "-" or "-p" or "-l" or "-P" ? null : [first];
        }

        if (Is(name, "script", "flock"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                if (words[at] is "-c" or "--command" && at + 1 < words.Count)
                {
                    return [words[at + 1]];
                }

                if (words[at].StartsWith("--command=", StringComparison.Ordinal))
                {
                    return [words[at]["--command=".Length..]];
                }
            }

            return null;
        }

        if (Is(name, "watch"))
        {
            var at = 1;

            while (at < words.Count && words[at].StartsWith('-'))
            {
                // -x runs the words as they are, each met by the rules on its own program.
                if (words[at] is "-x" or "--exec")
                {
                    return null;
                }

                at += words[at] is "-n" or "--interval" or "-q" or "--equexit" && at + 1 < words.Count ? 2 : 1;
            }

            return at < words.Count ? [string.Join(' ', words.Skip(at))] : null;
        }

        return null;
    }

    /// <summary>
    /// A script file a shell is handed: unseen, as help says, where a word names it - and refused
    /// where an expansion names it (<c>bash &lt;(curl ...)</c>, <c>source $F</c>), where the words
    /// are a command's own, for what it runs is what another command printed.
    /// </summary>
    private static IReadOnlyList<string>? ScriptFile(string file, bool program)
        => program && Expansion(file) is { } expansion
            ? throw new NotSupportedException($"'{file}' names the script it runs by {expansion}, which this does not read")
            : null;

    /// <summary>
    /// The script a program that reads one from its input is given: a here-string's or a
    /// here-document's text, spelled for the shell reader; null where a file is its input - a
    /// script file, unseen as any is. A program given nothing reads what an earlier command printed,
    /// and is refused, where the words are a command's own.
    /// </summary>
    private static IReadOnlyList<string>? Input(string name, IReadOnlyList<string> redirects, bool program, Func<string, string>? spelled = null)
    {
        foreach (var redirect in redirects)
        {
            if (InputText(redirect) is { } text)
            {
                return [spelled is null ? text : spelled(text)];
            }

            if (redirect.StartsWith('<') && !redirect.StartsWith("<>", StringComparison.Ordinal))
            {
                return ScriptFile(redirect[1..], program);
            }
        }

        return program
            ? throw new NotSupportedException($"'{name}' reads the script it runs from its input, which this does not read")
            : null;
    }

    /// <summary>The shells whose <c>-c</c> takes a script.</summary>
    private static readonly string[] Shells =
        ["sh", "bash", "zsh", "dash", "ksh", "mksh", "pdksh", "oksh", "ash", "yash", "posh", "rbash", "fish", "csh", "tcsh"];

    /// <summary>The shells' own options that take a value in the next word.</summary>
    private static readonly string[] ShellValueTaking = ["-o", "+o", "-O", "+O", "--rcfile", "--init-file"];

    /// <summary>A decoded <c>-EncodedCommand</c>: base64 of the script as PowerShell writes it, UTF-16LE.</summary>
    private static string Decode(string encoded)
    {
        try
        {
            return System.Text.Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            // Not base64: read it as the script it would have been, a word at a time.
            return encoded;
        }
    }

    /// <summary>
    /// A cmd script spelled for the shell reader: cmd quotes with double quotes alone and escapes
    /// with a caret, while a backslash, a single quote, a dollar and a backtick are its ordinary
    /// characters - each escaped here, so the reader splits the script where cmd does.
    /// </summary>
    private static string CmdAsPosix(string script)
    {
        var builder = new System.Text.StringBuilder(script.Length);
        var quoted = false;

        for (var at = 0; at < script.Length; at++)
        {
            var character = script[at];

            if (character == '"')
            {
                quoted = !quoted;
                builder.Append(character);
            }
            else if (!quoted && character == '^' && at + 1 < script.Length)
            {
                builder.Append('\\').Append(script[++at]);
            }
            else if (character is '\\' or '$' or '`' || (!quoted && character == '\''))
            {
                builder.Append('\\').Append(character);
            }
            else
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    /// <summary>
    /// A PowerShell script spelled for the shell reader: PowerShell escapes with a backtick, keeps a
    /// backslash as an ordinary character and doubles a single quote inside single quotes - each
    /// rewritten here, so the reader splits the script, and reads its names, where PowerShell does.
    /// </summary>
    private static string PowerShellAsPosix(string script)
    {
        var builder = new System.Text.StringBuilder(script.Length);
        var quote = '\0';

        for (var at = 0; at < script.Length; at++)
        {
            var character = script[at];

            if (quote == '\'')
            {
                if (character == '\'' && at + 1 < script.Length && script[at + 1] == '\'')
                {
                    builder.Append("'\\''");
                    at++;
                }
                else
                {
                    if (character == '\'')
                    {
                        quote = '\0';
                    }

                    builder.Append(character);
                }

                continue;
            }

            if (character == '`' && at + 1 < script.Length)
            {
                var escaped = script[++at];

                // Inside double quotes the reader escapes only what is special there; anything else
                // is the character itself.
                if (quote == '"' && escaped is not ('"' or '$' or '`' or '\\' or '\n'))
                {
                    builder.Append(escaped);
                }
                else
                {
                    builder.Append('\\').Append(escaped);
                }

                continue;
            }

            if (character == '\\')
            {
                builder.Append("\\\\");
                continue;
            }

            if (character == '"')
            {
                quote = quote == '"' ? '\0' : '"';
            }
            else if (character == '\'' && quote == '\0')
            {
                quote = '\'';
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// The command with its wrapper stripped - <paramref name="name"/> and its own options and
    /// values gone, so <c>nohup git push</c> is the <c>git push</c> every rule is matched against,
    /// and <c>env A=1 git push</c> the <c>A=1 git push</c> it is - or null where
    /// <paramref name="name"/> wraps nothing. What <c>xargs</c> hands its command from its input
    /// stands as <see cref="InputWord"/>, an expansion: at its replace string, else after the
    /// command's words, so a rule its words would meet is not decided without it.
    /// </summary>
    private static IReadOnlyList<string>? Stripped(string name, IReadOnlyList<string> words)
    {
        if (!Is(name, "env", "nohup", "time", "timeout", "nice", "xargs", "command", "builtin", "exec", "busybox"))
        {
            return null;
        }

        var at = 1;

        // The wrapper's own spelling from here on: its options are read in the one case each of its
        // kinds writes them.
        name = name.ToLowerInvariant();

        if (name == "env")
        {
            // NAME=value pairs, kept as the command's environment, and names taken away again with
            // -u, before the command. -S and --split-string, a command line of env's own, are read by
            // the flattener above this.
            var pairs = new List<string>();

            while (at < words.Count)
            {
                if (IsAssignment(words[at]))
                {
                    pairs.Add(words[at]);
                    at++;
                }
                else if ((words[at] == "-u" || words[at] == "--unset") && at + 1 < words.Count)
                {
                    at += 2;
                }
                else if (words[at] is "-i" or "-0" or "--ignore-environment" or "--null")
                {
                    at++;
                }
                else
                {
                    break;
                }
            }

            return [.. pairs, .. words.Skip(at)];
        }

        if (name == "timeout")
        {
            // Its options, each with its value where it takes one, then the duration the command waits.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "--foreground" or "-f" or "-p" || words[at].Contains('=', StringComparison.Ordinal) ? 1
                    : words[at] is "-k" or "-s" or "--kill-after" or "--signal" && at + 1 < words.Count ? 2
                    : 1;
            }

            if (at + 1 < words.Count && Duration().IsMatch(words[at]))
            {
                at++;
            }
        }
        else if (name == "nice")
        {
            if (at < words.Count && (words[at] == "-n" || words[at] == "--adjustment") && at + 1 < words.Count)
            {
                at += 2;
            }
            else if (at < words.Count && Niceness().IsMatch(words[at]))
            {
                at++;
            }
        }
        else if (name == "time")
        {
            if (at < words.Count && words[at] == "-p")
            {
                at++;
            }
        }
        else if (name == "xargs")
        {
            // Its options, each a word of its own and each that takes a value with it; the command
            // is the first word that is none. A replace string - -I's, -i's or --replace's - is where
            // the input goes; without one the input follows the command's words.
            string? replace = null;

            while (at < words.Count && words[at].StartsWith('-'))
            {
                var option = words[at];

                if (option == "-I" && at + 1 < words.Count)
                {
                    replace = words[at + 1];
                    at += 2;
                    continue;
                }

                if (option.StartsWith("-I", StringComparison.Ordinal) || option.StartsWith("-i", StringComparison.Ordinal))
                {
                    replace = option.Length > 2 ? option[2..] : "{}";
                    at++;
                    continue;
                }

                if (option.StartsWith("--replace", StringComparison.Ordinal))
                {
                    replace = option.StartsWith("--replace=", StringComparison.Ordinal) ? option["--replace=".Length..] : "{}";
                    at++;
                    continue;
                }

                at += option is "-d" or "-P" or "-n" or "-s" or "-E" or "-L" or "-a" or "--arg-file" or "--delimiter"
                    or "--max-args" or "--max-procs" or "--max-chars" or "--process-slot-var" && at + 1 < words.Count ? 2 : 1;
            }

            // With no command of its own it echoes what it is handed.
            IReadOnlyList<string> command = at < words.Count ? [.. words.Skip(at)] : ["echo"];

            return replace is { Length: > 0 }
                ? [.. command.Select(word => word.Replace(replace, InputWord, StringComparison.Ordinal))]
                : [.. command, InputWord];
        }
        else if (name is "command" or "builtin")
        {
            while (at < words.Count && words[at] is "-v" or "-V" or "-p")
            {
                at++;
            }
        }

        return [.. words.Skip(at)];
    }

    /// <summary>A duration as timeout takes it: digits, with a unit and a fraction.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(\.\d+)?[smhd]?$")]
    private static partial System.Text.RegularExpressions.Regex Duration();

    /// <summary>Whether <paramref name="name"/> is one of <paramref name="programs"/>, without regard to case, as a program's name is read here.</summary>
    private static bool Is(string name, params string[] programs)
        => programs.Any(program => name.Equals(program, StringComparison.OrdinalIgnoreCase));

    /// <summary>A niceness as nice takes it: a dash and digits, one word.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^-\d+$")]
    private static partial System.Text.RegularExpressions.Regex Niceness();

    /// <summary>
    /// The commands a script runs, read as the system's own shell reads it: split on its
    /// separators, its command substitutions and subshells read as commands of their own, each
    /// command then flattened again - its own words may name a script or carry a wrapper.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> FromScript(string script, int depth, IReadOnlyList<string> environment)
    {
        foreach (var words in Script(script, 0))
        {
            foreach (var flat in FromWords(words, depth, environment))
            {
                yield return flat;
            }
        }
    }
}
