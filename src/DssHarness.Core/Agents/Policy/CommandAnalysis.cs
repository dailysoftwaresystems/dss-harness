namespace DssHarness.Core.Agents.Policy;

/// <summary>
/// Reads a command whole before any rule is matched against it: what one client hands as an
/// argument list and another as a shell string is flattened into commands, each a list of words,
/// however the command hides its program.
/// </summary>
/// <remarks>
/// <para>
/// A shell string is split as the system's own shell splits it: on <c>;</c>, <c>&amp;&amp;</c>,
/// <c>||</c>, <c>|</c>, <c>&amp;</c> and line ends; the body of a command substitution
/// (<c>$(...)</c>, backticks) and of a subshell is read as commands of their own, and the shell's
/// structural words - <c>if</c>, <c>then</c>, <c>do</c>, <c>done</c> and the rest - never join a
/// command; wrappers (<c>env</c>, <c>nohup</c>, <c>time</c>, <c>timeout</c>, <c>nice</c>,
/// <c>xargs</c>, <c>command</c>, <c>builtin</c>, <c>exec</c>, <c>eval</c>) are stripped with their
/// own options; it goes into <c>sh</c>/<c>bash</c>/<c>zsh -c</c>, <c>cmd /c</c>,
/// <c>powershell</c>/<c>pwsh -Command</c> and a decoded <c>-EncodedCommand</c>, to
/// <see cref="Depth"/> levels of going into.
/// </para>
/// <para>
/// What it cannot read is said in help, not guessed at: the inside of a script file a command runs,
/// and a script nested past <see cref="Depth"/> levels of going into. Both stay as words - a path
/// named is still named, a root action still matched - only their inside is not opened.
/// </para>
/// <para>
/// A program is matched by its name as this reads it - its last segment, an <c>.exe</c> end folded
/// away - without regard to case, so a wrapper or a shell named in either case is read as one: a
/// word read as part of a command it is not part of can only lose a rule its command would have
/// met, never gain one.
/// </para>
/// </remarks>
public static partial class CommandAnalysis
{
    /// <summary>How deep one command goes into the scripts its words name: a shell's <c>-c</c>, a decoded encoded command.</summary>
    public const int Depth = 4;

    /// <summary>
    /// The commands an argument list runs: the list as it is, and a script one of its words names
    /// gone into.
    /// </summary>
    /// <param name="argv">The command as the tool runtime hands it: a program and its arguments, never a shell string.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);

        return [.. FromWords(argv, 1).Where(words => words.Count > 0)];
    }

    /// <summary>The commands a shell string runs, however it hides them.</summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    public static IReadOnlyList<IReadOnlyList<string>> Commands(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);

        return [.. FromScript(shell, 1).Where(words => words.Count > 0)];
    }

    /// <summary>
    /// The words of a command the rules are matched against: git's own options dropped before its
    /// subcommand, so <c>git -C x push</c> meets a rule on <c>git push</c> - and a <c>git -c</c>
    /// that defines an alias, or sets a key naming a program git runs, is the <c>git config</c> it
    /// is, for <c>git -c alias.x=commit x</c> would otherwise pass every rule on <c>commit</c>.
    /// </summary>
    /// <param name="words">One command's words, as <see cref="Commands(string)"/> or <see cref="Commands(IReadOnlyList{string})"/> flattened it.</param>
    /// <param name="comparison">How git's own name compares: names compare as this platform compares paths.</param>
    public static IReadOnlyList<string> ForRules(IReadOnlyList<string> words, StringComparison comparison)
    {
        ArgumentNullException.ThrowIfNull(words);

        if (words.Count == 0 || !string.Equals(Name(words[0]), "git", comparison))
        {
            return words;
        }

        var config = false;
        var at = 1;

        while (at < words.Count && words[at].StartsWith('-'))
        {
            var option = words[at];

            // -c, its value glued into it or the next word: an alias or a key naming a program git
            // runs makes the command the git config it is.
            if (option == "-c")
            {
                if (at + 1 < words.Count && NamesAProgram(words[at + 1]))
                {
                    config = true;
                }

                at += 2;
                continue;
            }

            if (option.StartsWith("-c", StringComparison.Ordinal) && option.Contains('='))
            {
                if (NamesAProgram(option[2..]))
                {
                    config = true;
                }

                at++;
                continue;
            }

            // -C, its path glued into it or the next word; an option with its value in the same
            // word stands as one word too. The options that take a value in the next word are the
            // few git names for them, and every other option takes none - dropping a word that was
            // the subcommand would read the command as one it is not.
            at += option.StartsWith("-C", StringComparison.Ordinal) && option.Length > 2
                || option.Contains('=')
                || ValueLess(option) ? 1
                : (option == "-C" || TakesNextWord(option)) && at + 1 < words.Count ? 2 : 1;
        }

        return at >= words.Count ? words
            : config ? ["git", "config", .. words.Skip(at)]
            : ["git", .. words.Skip(at)];
    }

    /// <summary>git's global options that take their value as the next word, read before its subcommand.</summary>
    private static bool TakesNextWord(string option)
        => option is "--git-dir" or "--work-tree" or "--namespace" or "--super-prefix";

    /// <summary>
    /// Whether a <c>git -c</c> value defines an alias or sets a key naming a program git runs: either
    /// makes the command the <c>git config</c> it is.
    /// </summary>
    private static bool NamesAProgram(string setting)
    {
        var key = setting.Split('=', 2)[0];

        return key.StartsWith("alias.", StringComparison.OrdinalIgnoreCase)
            || ProgramKeys.Any(suffix => key.Equals(suffix, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The keys whose value git runs as a program: set through <c>-c</c>, each would run a command of
    /// the setter's choosing outside every rule on git's subcommand.
    /// </summary>
    private static readonly string[] ProgramKeys =
    [
        "core.sshcommand",
        "core.hookspath",
        "core.editor",
        "core.sequenceeditor",
        "core.pager",
        "core.askpass",
        "core.fsmonitor",
        "credential.helper",
    ];

    /// <summary>git's options that take no value, read before its subcommand.</summary>
    private static bool ValueLess(string option)
        => option is "--bare" or "--no-optional-locks" or "--literal-pathspecs" or "--glob-pathspecs"
            or "--noglob-pathspecs" or "--icase-pathspecs" or "--no-replace-objects" or "--html-path"
            or "--man-path" or "--info-path" or "--exec-path" or "-p" or "--paginate" or "--no-pager";

    /// <summary>The word as a program's name: its last segment, with an <c>.exe</c> end folded away.</summary>
    internal static string Name(string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return word;
        }

        var name = Path.GetFileName(word.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>
    /// The commands the words run, the words' own command among them: wrappers stripped, a script
    /// one of the words names gone into, and an <c>eval</c> read as the script it is.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> FromWords(IReadOnlyList<string> words, int depth)
    {
        if (words.Count == 0)
        {
            yield break;
        }

        var name = Name(words[0]);

        // A script the command names, gone into: sh -c 'a && b', cmd /c a & b, powershell -Command
        // and a decoded -EncodedCommand. Its other arguments are the script's, not commands'.
        if (depth <= Depth && Into(name, words) is { } script)
        {
            foreach (var command in FromScript(script, depth + 1))
            {
                yield return command;
            }

            yield break;
        }

        // env -S takes a whole command line of its own, which it splits and runs: read it as the
        // script it is, and the words after it as further words of the same command.
        if (depth <= Depth && Is(name, "env") && SplitString(words) is { } split)
        {
            foreach (var command in FromScript(split.Script, depth + 1).Concat(FromWords(split.Tail, depth)))
            {
                yield return command;
            }

            yield break;
        }

        // eval joins its arguments into one script and runs that: read it as one, within the bound.
        if (name.Equals("eval", StringComparison.OrdinalIgnoreCase))
        {
            if (words.Count > 1 && depth <= Depth)
            {
                foreach (var command in FromScript(string.Join(' ', words.Skip(1)), depth + 1))
                {
                    yield return command;
                }
            }
            else if (words.Count > 1)
            {
                yield return words;
            }

            yield break;
        }

        // Wrappers, with their own options and values, stripped until a command is left.
        if (Stripped(name, words) is { } stripped)
        {
            foreach (var command in FromWords(stripped, depth))
            {
                yield return command;
            }

            yield break;
        }

        yield return words;
    }

    /// <summary>
    /// The command line <c>env -S</c> (<c>--split-string</c>) takes, and the words that follow it:
    /// null where the words name none. Read before any word of the command proper, as env reads it.
    /// </summary>
    private static (string Script, IReadOnlyList<string> Tail)? SplitString(IReadOnlyList<string> words)
    {
        for (var at = 1; at < words.Count; at++)
        {
            var option = words[at];

            if (option == "-S" || option == "--split-string")
            {
                return at + 1 < words.Count
                    ? (words[at + 1], [.. words.Skip(at + 2)])
                    : null;
            }

            // Glued to its value: '-Ssudo x', '--split-string=sudo x'.
            if (option.StartsWith("-S", StringComparison.Ordinal) && option.Length > 2)
            {
                return (option[2..], [.. words.Skip(at + 1)]);
            }

            if (option.StartsWith("--split-string=", StringComparison.Ordinal))
            {
                return (option["--split-string=".Length..], [.. words.Skip(at + 1)]);
            }

            // Before the option: the pairs and names env takes away again.
            if (!option.StartsWith('-') && option.Contains('='))
            {
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

    /// <summary>The script <paramref name="name"/> would run with <paramref name="words"/>, where it names one; null where it names none.</summary>
    private static string? Into(string name, IReadOnlyList<string> words)
    {
        if (Is(name, "sh", "bash", "zsh", "dash", "ksh"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option == "--")
                {
                    return null;
                }

                // The option that carries c takes the next word as its script, alone (-c) or glued
                // into it (-lc); a shell's other options take none.
                if (option == "-c" || (option.StartsWith('-') && option.Length > 1 && option.Contains('c')))
                {
                    return at + 1 < words.Count ? words[at + 1] : null;
                }
            }

            return null;
        }

        if (Is(name, "cmd"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                if (words[at].Equals("/c", StringComparison.OrdinalIgnoreCase) && at + 1 < words.Count)
                {
                    return string.Join(' ', words.Skip(at + 1));
                }
            }

            return null;
        }

        if (Is(name, "powershell", "pwsh"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase)
                    || option is "-e" or "-ec")
                {
                    return at + 1 < words.Count ? Decode(words[at + 1]) : null;
                }

                if (option.Equals("-Command", StringComparison.OrdinalIgnoreCase) || option == "-c")
                {
                    return at + 1 < words.Count ? string.Join(' ', words.Skip(at + 1)) : null;
                }
            }

            return null;
        }

        return null;
    }

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
    /// The command with its wrapper stripped - <paramref name="name"/> and its own options and
    /// values gone, so <c>env A=1 git push</c> is the <c>git push</c> every rule is matched against -
    /// or null where <paramref name="name"/> wraps nothing.
    /// </summary>
    private static IReadOnlyList<string>? Stripped(string name, IReadOnlyList<string> words)
    {
        if (!Is(name, "env", "nohup", "time", "timeout", "nice", "xargs", "command", "builtin", "exec"))
        {
            return null;
        }

        var at = 1;

        // The wrapper's own spelling from here on: its options are read in the one case each of its
        // kinds writes them.
        name = name.ToLowerInvariant();

        if (name == "env")
        {
            // NAME=value pairs, and names taken away again with -u, before the command. -S and
            // --split-string, a command line of env's own, are read by the flattener above this.
            while (at < words.Count)
            {
                if (!words[at].StartsWith('-') && words[at].Contains('='))
                {
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
        }
        else if (name == "timeout")
        {
            // Its options, each with its value where it takes one, then the duration the command waits.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "--foreground" or "-f" or "-p" || words[at].Contains('=') ? 1
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
            // is the first word that is none.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "-I" or "-d" or "-P" or "-n" or "-s" or "-E" or "-j" or "-L" or "-l" or "-o" or "-S"
                    && at + 1 < words.Count ? 2 : 1;
            }
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
    private static IEnumerable<IReadOnlyList<string>> FromScript(string script, int depth)
    {
        foreach (var words in Script(script))
        {
            foreach (var flat in FromWords(words, depth))
            {
                yield return flat;
            }
        }
    }

    /// <summary>
    /// The shell's structural words, read past as separators: a loop's and a branch's own words
    /// never join the commands its body runs, and neither does a lone brace or a negation.
    /// </summary>
    private static readonly HashSet<string> Structural =
    [
        "if", "then", "elif", "else", "fi", "for", "while", "until", "do", "done", "case", "esac", "in", "!", "{", "}",
    ];

    /// <summary>
    /// The commands the script spells, word by word: quotes read, the body of a substitution or a
    /// subshell read as commands of their own, a redirect's file kept as the word it names, and the
    /// structural words never part of a command.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Script(string script)
    {
        List<string>? words = null;
        var at = 0;

        // Whether a double quote is open, carried across the words the script spells: a quote a
        // command substitution interrupts stays open over it, as the shell reads it.
        var quoted = false;

        while (at < script.Length)
        {
            var character = script[at];

            // Inside an open double quote the shell's structure is not structure: a separator, a
            // parenthesis and a subshell are ordinary characters until the quote closes, while a
            // backtick and a $(...) substitute there as anywhere.
            if (quoted)
            {
                if (character == '$' && at + 1 < script.Length && script[at + 1] == '(')
                {
                    // Read on below, as a substitution.
                }
                else if (character == '`')
                {
                    // Read on below, as a substitution.
                }
                else
                {
                    var inside = Word(script, ref at, ref quoted);

                    if (inside.Length > 0)
                    {
                        (words ??= []).Add(inside);
                    }

                    continue;
                }
            }

            // A command substitution or a subshell: its body is commands of their own, and it gives
            // the command it stands in no word, for what it would give is what it printed.
            if ((character == '$' && at + 1 < script.Length && script[at + 1] == '(') || character == '(')
            {
                var open = character == '$' ? at + 1 : at;

                if (Matching(script, open) is not { } end)
                {
                    at = script.Length;
                    continue;
                }

                foreach (var command in Script(script[(open + 1)..end]))
                {
                    yield return command;
                }

                at = end + 1;
                continue;
            }

            if (character == ')')
            {
                // A closing parenthesis the reader is not inside ends the script it reads.
                break;
            }

            // A backtick substitution: read to its closing backtick, as a command of its own.
            if (character == '`')
            {
                var closing = script.IndexOf('`', at + 1);

                if (closing < 0)
                {
                    break;
                }

                foreach (var command in Script(script[(at + 1)..closing]))
                {
                    yield return command;
                }

                at = closing + 1;
                continue;
            }

            // Separators: each ends the command before it and starts one after it. A redirect's file
            // is kept - the path it names is one a rule may refuse - and a numbered redirect to a
            // number ('2>&1') names no path at all.
            if (character is ';' or '\n' or '\r' or '|' or '&')
            {
                at++;

                if (words is not null)
                {
                    yield return words;
                    words = null;
                }

                continue;
            }

            if (character is ' ' or '\t')
            {
                at++;
                continue;
            }

            var word = Word(script, ref at, ref quoted);

            if (word.Length == 0)
            {
                continue;
            }

            if (Structural.Contains(word))
            {
                if (words is not null)
                {
                    yield return words;
                    words = null;
                }

                continue;
            }

            (words ??= []).Add(word);
        }

        if (words is not null)
        {
            yield return words;
        }
    }

    /// <summary>
    /// One word of a script: single quotes read literally, a backslash escaping a quote, itself and
    /// a space alone, and a redirect operator before it consumed - the file it names is the word, so
    /// the path a command writes is one the policy is asked about. Whether a double quote is open
    /// when the word ends is given back, for a quote a command substitution interrupts stays open
    /// over it, as the shell reads it.
    /// </summary>
    private static string Word(string script, ref int at, ref bool quoted)
    {
        var word = new System.Text.StringBuilder();

        // A redirect glued to its file: '>file', '>>file', '2>file'. A numbered redirect to a number
        // ('2>&1') names no file: the whole operator is consumed and the word is empty. Inside an
        // open double quote no operator is an operator, and the characters are the word's own.
        var opens = !quoted
            && (script[at] is '<' or '>'
                || (char.IsDigit(script[at]) && at + 1 < script.Length && script[at + 1] is '<' or '>'));

        if (opens)
        {
            // The operator's own characters, a doubled one among them ('>>', '2>>'), consumed whole:
            // the word that follows is the file it names.
            var operatorEnd = at + 1;

            while (operatorEnd < script.Length && script[operatorEnd] is '<' or '>')
            {
                operatorEnd++;
            }

            if (operatorEnd < script.Length && script[operatorEnd] == '&')
            {
                at = operatorEnd + 2;
                return string.Empty;
            }

            at = operatorEnd;
        }

        while (at < script.Length)
        {
            var character = script[at];

            // Inside double quotes a separator is an ordinary character - a quoted ';' is an
            // argument, not a second command - while a backtick and a $(...) substitute there too,
            // as the shell reads them.
            if (!quoted && (character is ';' or '\n' or '\r' or '|' or '&' or '(' or ')' or ' ' or '\t'))
            {
                break;
            }

            if (character == '`')
            {
                break;
            }

            if (character == '\'' && !quoted)
            {
                var end = script.IndexOf('\'', at + 1);

                if (end < 0)
                {
                    word.Append(script[(at + 1)..]);
                    at = script.Length;
                }
                else
                {
                    word.Append(script[(at + 1)..end]);
                    at = end + 1;
                }

                // The segment read whole, quotes included: a word may follow it, its own.
                continue;
            }

            if (character == '"')
            {
                quoted = !quoted;
                at++;
                continue;
            }

            // A backslash escapes whatever follows it, and inside double quotes only a quote, a
            // backtick, a dollar, itself and a line end - the shell's own rule, which leaves a
            // Windows path spelled with backslashes itself inside quotes and lets no spelling of a
            // root action escape by a backslash before one of its letters.
            if (character == '\\' && at + 1 < script.Length
                && (!quoted || script[at + 1] is '"' or '`' or '$' or '\\' or '\n' or '\r'))
            {
                word.Append(script[at + 1]);
                at += 2;
                continue;
            }

            if (character == '$' && at + 1 < script.Length && script[at + 1] == '(')
            {
                break;
            }

            word.Append(character);
            at++;
        }

        return word.ToString();
    }

    /// <summary>The index of the parenthesis that closes the one at <paramref name="open"/>, counting the ones inside; null where none closes it.</summary>
    private static int? Matching(string script, int open)
    {
        var depth = 0;

        for (var at = open; at < script.Length; at++)
        {
            if (script[at] == '(')
            {
                depth++;
            }
            else if (script[at] == ')')
            {
                depth--;

                if (depth == 0)
                {
                    return at;
                }
            }
        }

        return null;
    }
}
