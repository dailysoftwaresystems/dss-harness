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
/// <c>command</c>, <c>builtin</c>, <c>exec</c> and <c>busybox</c>) are stripped with their own
/// options, a directory one starts its command in - <c>env -C</c>, as pwsh's
/// <c>-WorkingDirectory</c> - read as the <c>cd</c> before the command it is; <c>eval</c>'s words
/// are joined into the one script it runs, read as a script is; it goes into
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
    public static IReadOnlyList<AnalyzedCommand> Commands(IReadOnlyList<string> argv)
    {
        ArgumentNullException.ThrowIfNull(argv);
        Uncut(argv);

        return Uncut([.. FromWords(argv, [], 1, []).Where(command => command.Whole.Count > 0)]);
    }

    /// <summary>The commands a shell string runs, however it hides them.</summary>
    /// <param name="shell">The command as Claude Code's shell tool hands it: one string the system's own shell would run.</param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<AnalyzedCommand> Commands(string shell)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(shell);
        Uncut([shell]);

        return Uncut([.. FromScript(shell, 1, []).Where(command => command.Whole.Count > 0)]);
    }

    /// <summary>
    /// Refuses <paramref name="words"/> where one holds a NUL: the system hands a program its name and its arguments cut
    /// at the first, so the command that would run is not the one read here.
    /// </summary>
    /// <exception cref="NotSupportedException">A word holds a NUL.</exception>
    private static void Uncut(IEnumerable<string> words)
    {
        if (words.Any(word => word.Contains('\0', StringComparison.Ordinal)))
        {
            throw new NotSupportedException("it holds a NUL, where the system cuts what a program is handed, which this does not read");
        }
    }

    /// <summary>
    /// <paramref name="commands"/>, refused where a word of one holds a NUL: a word as given holds none, but one a
    /// script decodes may - <c>$'push\x00'</c> is <c>push</c> to the shell that runs it.
    /// </summary>
    /// <exception cref="NotSupportedException">A word holds a NUL.</exception>
    private static IReadOnlyList<AnalyzedCommand> Uncut(IReadOnlyList<AnalyzedCommand> commands)
    {
        Uncut(commands.SelectMany(command => command.Whole));
        return commands;
    }

    /// <summary>
    /// Whether <paramref name="word"/> is an assignment the shell reads before a command -
    /// <c>NAME=value</c> or <c>NAME+=value</c>, which adds to the name's value, a name of letters,
    /// digits and underscores that begins with no digit - which gives the command its environment.
    /// </summary>
    public static bool IsAssignment(string word)
    {
        ArgumentNullException.ThrowIfNull(word);

        var name = word.Contains('=', StringComparison.Ordinal) ? AssignedName(word) : string.Empty;

        return name.Length > 0
            && (char.IsAsciiLetter(name[0]) || name[0] == '_')
            && name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');
    }

    /// <summary>The name an assignment sets: before its <c>=</c>, a <c>+=</c>'s <c>+</c> no part of it.</summary>
    internal static string AssignedName(string assignment)
    {
        var name = assignment.Split('=', 2)[0];

        return name.EndsWith('+') ? name[..^1] : name;
    }

    /// <summary>
    /// The assignments a command makes for what runs with it and after it, each as <c>NAME=value</c>: its own
    /// environment's, and what its program declares - an export or a declaration of its kind, cmd's <c>set</c> (its
    /// value the rest of its line), fish's <c>set</c>, csh's <c>setenv</c> and <c>setx</c> (a name, then its value),
    /// PowerShell's <c>Set-Item</c>, <c>New-Item</c>, <c>Set-Content</c> and <c>Add-Content</c> of an <c>env:</c>
    /// item, and its <c>Copy-Item</c>, <c>Move-Item</c> and <c>Rename-Item</c> onto one (<see cref="ItemMovers"/>),
    /// whose value this cannot see.
    /// </summary>
    internal static IReadOnlyList<string> Declarations(IReadOnlyList<string> words)
    {
        var environment = EnvironmentOf(words);
        var declared = words.Take(environment).ToList();

        if (environment == words.Count)
        {
            return declared;
        }

        var program = Name(words[environment]);
        var rest = words.Skip(environment + 1).ToList();

        // A name an expansion gives is one this cannot see: it may be any, git's configuration or a program's among them.
        // Each program's naming words only: an export's and its kind's assignments, a set's one name (its '--' ends the
        // search, for what follows is a positional, not a name), and an item cmdlet's env: references (never a -Value or
        // a path that names no env: item).
        var naming = Is(program, "export", "declare", "typeset", "local", "readonly") ? rest.Where(word => DeclaredName(word) is { } name && Expansion(name) is not null)
            : Is(program, "set", "setenv", "setx") ? rest.TakeWhile(word => word != "--").Where(word => !word.StartsWith('-') && !word.StartsWith('/')).Take(1).Where(word => Expansion(AssignedName(word)) is not null)
            : Is(program, ["Set-Item", "si", "New-Item", "ni", "Set-Content", "sc", "Add-Content", "ac", .. ItemMovers]) ? rest.Where(word => EnvironmentReference().Match(word) is { Success: true } reference && Expansion(reference.Groups["name"].Value) is not null)
            : [];

        if (naming.FirstOrDefault() is { } unread)
        {
            var named = Is(program, "set", "setenv", "setx") ? AssignedName(unread)
                : EnvironmentReference().Match(unread) is { Success: true } reference ? reference.Groups["name"].Value
                : DeclaredName(unread)!;

            throw new NotSupportedException($"'{unread}' declares a name {Expansion(named)} gives, which this does not read");
        }

        if (Is(program, "export", "declare", "typeset", "local", "readonly"))
        {
            declared.AddRange(rest.Where(IsAssignment));
        }
        else if (Is(program, "set", "setenv", "setx") && rest.TakeWhile(word => word != "--").ToList() is var named
            && named.FindIndex(word => !word.StartsWith('-') && !word.StartsWith('/')) is var at and >= 0)
        {
            declared.Add(named[at].Contains('=', StringComparison.Ordinal)
                ? string.Join(' ', named.Skip(at))
                : $"{named[at]}={string.Join(' ', named.Skip(at + 1))}");
        }
        else if (Is(program, [.. ItemMovers]))
        {
            // An item copied, moved or renamed onto the env: drive takes a value this cannot see.
            for (var index = 0; index < rest.Count; index++)
            {
                if (EnvironmentItem().Match(rest[index]) is { Success: true } moved)
                {
                    declared.Add($"{moved.Groups["name"].Value}=${{PSVALUE}}");

                    if (Is(program, "Rename-Item", "rni", "ren") && rest.Skip(index + 1).FirstOrDefault(word => !word.StartsWith('-')) is { } renamed)
                    {
                        declared.Add($"{renamed}=${{PSVALUE}}");
                    }
                }
            }
        }
        else if (Is(program, "Set-Item", "si", "New-Item", "ni", "Set-Content", "sc", "Add-Content", "ac")
            && rest.FindIndex(word => EnvironmentItem().IsMatch(word)) is var item and >= 0)
        {
            // Its value is -Value's (any prefix of it PowerShell takes, glued after a ':' too), else the next word given.
            var valued = rest.FindIndex(word => word.Length > 2 && word.Split(':', 2)[0] is var parameter
                && "-value".StartsWith(parameter, StringComparison.OrdinalIgnoreCase) && parameter.Length > 2);
            var value = valued < 0 ? rest.Skip(item + 1).FirstOrDefault(word => !word.StartsWith('-'))
                : rest[valued].Contains(':', StringComparison.Ordinal) ? rest[valued].Split(':', 2)[1]
                : valued + 1 < rest.Count ? rest[valued + 1]
                : null;

            declared.Add($"{EnvironmentItem().Match(rest[item]).Groups["name"].Value}={value}");
        }

        return declared;
    }

    /// <summary>Windows PowerShell's parameters that take a value, but its command's, its file's and its version's, which are read apart.</summary>
    private static readonly string[] WindowsPowerShellValued =
        ["psconsolefile", "inputformat", "outputformat", "windowstyle", "configurationname", "executionpolicy", "ep", "encodedarguments", "ea"];

    /// <summary>Windows PowerShell's switches.</summary>
    private static readonly string[] WindowsPowerShellSwitches = ["nologo", "noexit", "sta", "mta", "noprofile", "noninteractive"];

    /// <summary>
    /// pwsh's parameters that take a value, by any prefix of them - but its command's, its file's, its version's and its
    /// working directory's, which are read apart - each with the short names pwsh gives it.
    /// </summary>
    private static readonly string[] PwshValued =
        ["configurationname", "configurationfile", "custompipename", "executionpolicy", "ep", "inputformat", "if", "outputformat", "of",
         "settingsfile", "windowstyle", "encodedarguments", "ea"];

    /// <summary>Whether <paramref name="parameter"/>, as <see cref="PowerShellParameter"/> reads it, is pwsh's <c>-WorkingDirectory</c>: <c>wd</c>, or a prefix of it from <c>wo</c>.</summary>
    private static bool IsWorkingDirectory(string parameter)
        => parameter == "wd" || (parameter.Length >= 2 && "workingdirectory".StartsWith(parameter, StringComparison.Ordinal));

    /// <summary>
    /// The name of the PowerShell parameter <paramref name="word"/> gives, in lower case, as powershell and pwsh read one:
    /// after its <c>-</c>, its <c>/</c> or its <c>--</c>; null where the word is no parameter.
    /// </summary>
    private static string? PowerShellParameter(string word)
        => word.StartsWith("--", StringComparison.Ordinal) ? word[2..].ToLowerInvariant()
            : word.StartsWith('-') || word.StartsWith('/') ? word[1..].ToLowerInvariant()
            : null;

    /// <summary>
    /// The directories powershell's or pwsh's <c>-WorkingDirectory</c> names among <paramref name="words"/> (<c>-wd</c>, any
    /// prefix of it from <c>-wo</c>, its value the next word or glued after a <c>:</c>): sought among every word, past where
    /// its command begins too, so a place it may start in is never missed.
    /// </summary>
    private static List<string> PowerShellDirectories(IReadOnlyList<string> words)
    {
        var directories = new List<string>();

        for (var at = 1; at < words.Count; at++)
        {
            if (PowerShellParameter(words[at]) is not { } parameter)
            {
                continue;
            }

            var named = parameter.Split(':', 2);

            if (IsWorkingDirectory(named[0]))
            {
                if ((named.Length == 2 ? words[at].Split(':', 2)[1] : words.ElementAtOrDefault(at + 1)) is { } directory)
                {
                    directories.Add(directory);
                }
            }
        }

        return directories;
    }

    /// <summary>PowerShell's cmdlets that copy, move or rename an item, with their aliases.</summary>
    private static readonly string[] ItemMovers = ["Copy-Item", "cpi", "copy", "Move-Item", "mi", "move", "Rename-Item", "rni", "ren"];

    /// <summary>
    /// How a command's program runs a command line it composes rather than its words - Start-Process's arguments, WMI's
    /// and the task scheduler's command lines, code Add-Type compiles, a function or an alias an item's text defines, a
    /// method ForEach-Object calls by its name - said; null where it runs none of these.
    /// </summary>
    /// <param name="words">The command's words, its program first.</param>
    public static string? ComposesCommandLine(IReadOnlyList<string> words)
    {
        ArgumentNullException.ThrowIfNull(words);

        if (words.Count == 0)
        {
            return null;
        }

        var program = Name(words[0]);
        var rest = words.Skip(1).ToList();

        return program switch
        {
            _ when Is(program, "Start-Process", "saps", "start") && rest.Count > 1 => "hands its program a command line of its own",
            _ when Is(program, "Add-Type") => "compiles code of its own",
            _ when Is(program, "Invoke-WmiMethod", "iwmi", "Invoke-CimMethod") => "has WMI run a method, a process's start among them",
            _ when Is(program, "wmic") && rest.Any(word => word.Equals("create", StringComparison.OrdinalIgnoreCase)) => "has WMI start a command line",
            _ when (Is(program, "schtasks") && rest.Any(word => word.Equals("/create", StringComparison.OrdinalIgnoreCase) || word.Equals("/change", StringComparison.OrdinalIgnoreCase)))
                || Is(program, "New-ScheduledTaskAction") => "schedules a command line of its own",
            _ when Is(program, ["Set-Item", "si", "New-Item", "ni", "Set-Content", "sc", "Add-Content", "ac", .. ItemMovers]) && rest.Any(word => CodeItem().IsMatch(word))
                => "defines a function or an alias from a text",
            _ when Is(program, "ForEach-Object", "%", "foreach") && CallsByName(rest) => "calls a method by its name",
            _ => null,
        };
    }

    /// <summary>Whether ForEach-Object's words name a method it calls: its -MemberName, or a word by position that is no script block.</summary>
    private static bool CallsByName(IReadOnlyList<string> words)
    {
        for (var at = 0; at < words.Count; at++)
        {
            if (words[at].Length > 2 && "-membername".StartsWith(words[at].Split(':', 2)[0], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (words[at].StartsWith('-'))
            {
                at += words[at].Contains(':', StringComparison.Ordinal) ? 0 : 1;
                continue;
            }

            if (!words[at].StartsWith('$'))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>An item of PowerShell's function: or alias: drive, a parameter's name glued before it too: its text is code.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:-[A-Za-z]+:)?(?:function|alias):", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex CodeItem();

    /// <summary>The name a declaring command's word may set: before its <c>=</c>, or the word itself where it is no option.</summary>
    private static string? DeclaredName(string word)
        => word.Contains('=', StringComparison.Ordinal) ? AssignedName(word)
            : word.StartsWith('-') || word.StartsWith('/') ? null
            : word;

    /// <summary>An item of PowerShell's <c>env:</c> drive - <c>env:NAME</c>, <c>Env:\NAME</c> - a parameter's name glued before it too.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:-[A-Za-z]+:)?env:[\\/]?(?<name>[A-Za-z_][A-Za-z0-9_]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EnvironmentItem();

    /// <summary>A word that references PowerShell's <c>env:</c> drive, its <c>name</c> whatever follows - an expansion among it, where an item cmdlet names an env: item by one this cannot see.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:-[A-Za-z]+:)?env:[\\/]?(?<name>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EnvironmentReference();

    /// <summary>How many of a command's first words are its environment: the assignments before its program.</summary>
    /// <param name="words">One command's words, as <see cref="AnalyzedCommand.Words"/> holds them.</param>
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
    /// <param name="program">
    /// Whether the words' first is the command's own program, rather than a word of another program's tried as one: a git
    /// or a gh tried so (<c>grep -rn git --include=*.cs</c>) reads an option it does not know as one word, never refused.
    /// </param>
    /// <exception cref="NotSupportedException">The command's own git or gh is given an option before its subcommand this does not know, which may take the word after it.</exception>
    public static IReadOnlyList<IReadOnlyList<string>> ForRules(IReadOnlyList<string> words, bool program = true)
    {
        ArgumentNullException.ThrowIfNull(words);

        var readings = new List<IReadOnlyList<string>>();

        // Setting git's configuration through the environment is configuring git, whatever runs
        // with it: an export alone, an env, the words before git itself - what the command declares
        // (Declarations), never a word of data that spells one (grep -rn GIT_CONFIG_COUNT= src), nor
        // another program's last word, an assignment before no program.
        var environment = EnvironmentOf(words);

        var declared = program || environment < words.Count ? Declarations(words) : [];

        if (declared.FirstOrDefault(SetsGitConfiguration) is { } setting)
        {
            readings.Add(["git", "config", AssignedName(setting), .. words.Skip(environment)]);
        }

        // Setting where gh reads its configuration from is gh config set of the setter's choosing: its editor, pager and
        // browser among it, each a program any gh command runs.
        if (declared.FirstOrDefault(word => IsAssignment(word) && GhConfigurationNames.Contains(AssignedName(word), StringComparer.OrdinalIgnoreCase)) is { } ghSetting)
        {
            readings.Add(["gh", "config", "set", AssignedName(ghSetting), .. words.Skip(environment)]);
        }

        var (command, configured) = Plain(words, program);

        if (configured || ClonesConfigured(command))
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
    private static (IReadOnlyList<string> Command, bool Configured) Plain(IReadOnlyList<string> words, bool program)
    {
        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return (environment == 0 ? words : [.. words.Skip(environment)], false);
        }

        // git-push, as git's own directory holds each of its commands, is git push.
        if (Name(words[environment]) is { Length: > 4 } dashed && dashed.StartsWith("git-", StringComparison.OrdinalIgnoreCase))
        {
            words = [.. words.Take(environment), "git", dashed[4..], .. words.Skip(environment + 1)];
        }

        if (Is(Name(words[environment]), "gh"))
        {
            return (GhForRules([.. words.Skip(environment)], program), false);
        }

        if (!Is(Name(words[environment]), "git"))
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

            if (!program)
            {
                at++;
                continue;
            }

            throw new NotSupportedException($"'{option}' is an option of git's own this does not read, which may take the word after it as its value");
        }

        return at >= words.Count ? ([.. words.Skip(environment)], false) : (["git", .. words.Skip(at)], config);
    }

    /// <summary>
    /// Whether a git command configures git where it runs through its subcommand's own options,
    /// beside the global <c>-c</c> <see cref="Plain"/> reads: clone's own <c>-c</c> or
    /// <c>--config</c> of any key but one that only shapes what git prints - its hooks run during the
    /// clone's checkout, its <c>core.sshCommand</c> during its fetch - and clone's and init's
    /// <c>--template</c>, whose directory git copies its hooks from, as <c>GIT_TEMPLATE_DIR</c> does.
    /// So <c>git clone -c core.hooksPath=x url</c> is the <c>git config</c> it is.
    /// </summary>
    /// <param name="command">A git command with its global options dropped, as <see cref="Plain"/> returns it.</param>
    private static bool ClonesConfigured(IReadOnlyList<string> command)
    {
        if (command.Count < 2 || !Is(Name(command[0]), "git"))
        {
            return false;
        }

        var subcommand = Name(command[1]).ToLowerInvariant();

        if (subcommand == "clone" && CloneKeyValues(command, 1).Any(Configures))
        {
            return true;
        }

        return subcommand is "clone" or "init"
            && Enumerable.Range(2, command.Count - 2).Any(at => GitOptionValue(command, at, ["template"], string.Empty, gluedOnly: false) is not null);
    }

    /// <summary>
    /// Each <c>key=value</c> clone's own <c>-c</c> or <c>--config</c> sets, in every form git takes
    /// (the value glued or the next word): the configuration clone writes into the new repository,
    /// read as a global <c>-c</c>'s is. None where <paramref name="subcommand"/> is not a clone.
    /// </summary>
    /// <param name="words">A git command's words, its program first.</param>
    /// <param name="subcommand">Where the subcommand stands among the words.</param>
    private static IEnumerable<string> CloneKeyValues(IReadOnlyList<string> words, int subcommand)
    {
        if (subcommand >= words.Count || !Is(Name(words[subcommand]), "clone"))
        {
            yield break;
        }

        for (var at = subcommand + 1; at < words.Count; at++)
        {
            if (GitOptionValue(words, at, ["config"], "c", gluedOnly: false) is { } found)
            {
                yield return found.Value;
                at = found.Next - 1;
            }
        }
    }

    /// <summary>
    /// gh's own aliases of its commands, as gh 2.89 and gh's manual of 2026-10-26 give them, each to the command it names: a
    /// command's by its alias alone, a subcommand's by its command's name, a space and its alias. gh runs <c>gh pr co</c> as <c>gh pr checkout</c>
    /// and <c>gh ext exec</c> as <c>gh extension exec</c>, and a rule on the one meets the other. An alias gh's
    /// configuration holds (<c>co</c>) is none of these: it is the user's to change.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GhAliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["agent-tasks"] = "agent-task", ["agent"] = "agent-task", ["agents"] = "agent-task", ["at"] = "attestation",
        ["cs"] = "codespace", ["extensions"] = "extension", ["ext"] = "extension", ["rs"] = "ruleset", ["skills"] = "skill",
        ["alias ls"] = "list", ["cache ls"] = "list", ["codespace ls"] = "list", ["config ls"] = "list", ["extension ls"] = "list",
        ["gist new"] = "create", ["gist ls"] = "list", ["gpg-key ls"] = "list", ["issue new"] = "create", ["issue ls"] = "list",
        ["label ls"] = "list", ["org ls"] = "list", ["pr new"] = "create", ["pr ls"] = "list", ["pr co"] = "checkout",
        ["project ls"] = "list", ["release new"] = "create", ["release ls"] = "list", ["repo new"] = "create", ["repo ls"] = "list",
        ["ruleset ls"] = "list", ["run ls"] = "list", ["secret remove"] = "delete", ["secret ls"] = "list", ["ssh-key ls"] = "list",
        ["variable remove"] = "delete", ["variable ls"] = "list", ["workflow ls"] = "list", ["discussion ls"] = "list",
        ["extension uninstall"] = "remove", ["skill add"] = "install", ["skill ls"] = "list", ["skill show"] = "preview",
    };

    /// <summary>
    /// The environment names gh reads its configuration from where they are set - its own directory, and the homes it
    /// finds one in: <c>XDG_CONFIG_HOME</c>'s <c>gh</c>, Windows' <c>AppData</c>'s <c>GitHub CLI</c>, and <c>HOME</c>'s
    /// <c>.config/gh</c> - read so on every system. Set for a command, each is a gh config set of the setter's choosing.
    /// </summary>
    private static readonly string[] GhConfigurationNames = ["GH_CONFIG_DIR", "XDG_CONFIG_HOME", "APPDATA", "HOME"];

    /// <summary>
    /// A gh command as its rules read it: gh, the words of its command's path, each by gh's own name for it
    /// (<see cref="GhAliases"/>) - its flags before the path's end taken out, a flag's value with it - then the rest as it
    /// stands. gh finds its command past any flag, so <c>gh pr -R o/r merge 5</c> is <c>gh pr merge 5</c>. A flag before
    /// the path's end that this does not know may take the word after it, and is refused.
    /// </summary>
    private static IReadOnlyList<string> GhForRules(IReadOnlyList<string> words, bool program)
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
                path.Add(GhAliases.TryGetValue(path.Count == 1 ? word : $"{path[1]} {word}", out var named) ? named : word);
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

            if (!program)
            {
                at++;
                continue;
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
    /// name - the count and its keys and values, the parameters, the files it reads), the homes it
    /// reads its global configuration from (<c>HOME</c>'s <c>.gitconfig</c>, <c>XDG_CONFIG_HOME</c>'s
    /// <c>git/config</c>, and the one git on Windows takes from <c>HOMEDRIVE</c> and <c>HOMEPATH</c> or
    /// <c>USERPROFILE</c> where <c>HOME</c> is not set - read so on every system, as every name here
    /// is), the directory it finds its programs in, and the template it copies hooks from: set for a
    /// command, each is a <c>git -c</c> of the setter's choosing - an alias or a pager in a home's
    /// configuration is one. A name whose value is a program git runs is read as that program's
    /// command line instead (<see cref="ProgramValued"/>).
    /// <c>GIT_CONFIG_NOSYSTEM</c>, which only takes the system's configuration away, is one of the
    /// <c>GIT_CONFIG</c> names all the same: a setting the system holds - a hooks path, a protocol
    /// refused - is configuration too, and taking it away configures git as surely as adding one.
    /// </summary>
    private static readonly string[] GitConfigurationNames = ["GIT_EXEC_PATH", "GIT_TEMPLATE_DIR", "HOME", "XDG_CONFIG_HOME", "HOMEDRIVE", "HOMEPATH", "USERPROFILE"];

    /// <summary>
    /// Whether <paramref name="word"/> sets git's configuration through the environment: <c>NAME=value</c> of such a
    /// name - PowerShell's <c>$env:NAME = value</c> written as one by its reader (<see cref="PowerShellReader"/>).
    /// </summary>
    private static bool SetsGitConfiguration(string word)
    {
        if (!IsAssignment(word))
        {
            return false;
        }

        var name = AssignedName(word);

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
        // on Linux too. An '@' before it only keeps cmd from echoing the command - wherever cmd
        // reads one, after an if's condition too - and is no part of the name.
        var name = word.TrimStart('@').TrimEnd('/', '\\').Replace('\\', '/').Split('/')[^1];

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
    /// script it is. The redirects the shell makes for the words stay with the command they are made for, or a command
    /// of their own where the words' own is gone into, so the files they name are decided.
    /// </summary>
    private static IEnumerable<AnalyzedCommand> FromWords(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, int depth, IReadOnlyList<string> environment)
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
                // Assignments alone run nothing; they stay a command of their own, with the redirects the
                // shell makes for them, so what they name is still met by every rule on a path, and a
                // program one names is read.
                if (environment.Count > 0 || redirects.Count > 0)
                {
                    yield return new AnalyzedCommand(environment, redirects);
                }

                foreach (var command in ProgramsNamed(given, depth, environment))
                {
                    yield return command;
                }

                yield break;
            }

            var name = Name(words[0]);

            // A program that starts what it runs in another directory - env -C, pwsh -WorkingDirectory - moves it there
            // first, as a cd before it would.
            foreach (var directory in MovesOf(words))
            {
                yield return Moved(directory);
            }

            // A script the command names, gone into: sh -c 'a && b' and the words after its script, a
            // here-string or here-document a shell reads, cmd /c a & b, powershell -Command and a
            // decoded -EncodedCommand, trap's, watch's, script's and flock's command line.
            if (Into(name, words, redirects, program: true) is { } scripts)
            {
                if (redirects.Count > 0)
                {
                    yield return new AnalyzedCommand([], redirects);
                }

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
            // runs, the words after it among them - its own options among them, read as env reads them.
            if (Is(name, "env") && SplitString(words) is { } split)
            {
                if (depth > Depth)
                {
                    throw Nested(words);
                }

                foreach (var command in FromWords(SplitInto(split), redirects, depth + 1, environment))
                {
                    yield return command;
                }

                yield break;
            }

            // eval joins its arguments into one script and runs that (PowerShell's Invoke-Expression is its reader's own).
            if (Is(name, "eval"))
            {
                if (redirects.Count > 0)
                {
                    yield return new AnalyzedCommand([], redirects);
                }

                if (words.Count > 1)
                {
                    foreach (var command in GoneInto([string.Join(' ', words.Skip(1))], depth, environment))
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

        // An alias gives a name a command line run in its place, which a later command names by the alias alone.
        if ((Is(Name(words[0]), "alias") && words.Skip(1).Any(word => word.Contains('=', StringComparison.Ordinal)))
            || (Is(Name(words[0]), "Set-Alias", "New-Alias", "sal", "nal") && words.Count > 1))
        {
            throw new NotSupportedException($"'{string.Join(' ', words)}' gives a name a command run in its place, which this does not read");
        }

        // A program among its words that starts what it runs in another directory - stdbuf's env -C - moves the command's
        // paths there, before the command is read.
        for (var at = 1; at < words.Count; at++)
        {
            if (MayMove(words[at]))
            {
                foreach (var directory in MovesOf([.. words.Skip(at)]))
                {
                    yield return Moved(directory);
                }
            }
        }

        yield return new AnalyzedCommand(environment.Count == 0 ? words : [.. environment, .. words], redirects);

        // A program another runs by a variable: the environment's own, and what the command
        // declares (Declarations).
        foreach (var command in ProgramsNamed([.. given, .. Declarations(words)], depth, environment))
        {
            yield return command;
        }

        // A program git's own settings name, which git runs through the shell: core.pager, core.sshCommand, an
        // alias's '!' line, as the variables above are read.
        if (GitWords(words) is { } git && GitSettingScripts(git, environment) is { Count: > 0 } settings)
        {
            foreach (var command in GoneInto(settings, depth, environment))
            {
                yield return command;
            }
        }

        // A command the program runs with words it fills in as it runs it - find -exec, fd -x, rg
        // --pre, parallel - is a command of its own, those words expansions, reading the program's input.
        foreach (var executed in Executed(words))
        {
            if (depth > Depth)
            {
                throw Nested(words);
            }

            foreach (var command in FromWords(executed, redirects, depth + 1, environment))
            {
                yield return command;
            }
        }

        // A shell another program runs with its script among the command's words - find -exec,
        // ssh, watch - is gone into too: what it runs is a command of its own.
        for (var at = 1; at < words.Count; at++)
        {
            var rest = words.Skip(at).ToList();

            if (Into(Name(words[at]), rest, redirects, program: false) is { } inner)
            {
                foreach (var command in GoneInto(inner, depth, environment))
                {
                    yield return command;
                }
            }
            else if (Is(Name(words[at]), "env") && SplitString(rest) is { } innerSplit)
            {
                if (depth > Depth)
                {
                    throw Nested(rest);
                }

                foreach (var command in FromWords(SplitInto(innerSplit), redirects, depth + 1, environment))
                {
                    yield return command;
                }
            }
        }
    }

    /// <summary>
    /// The names whose value is a program another runs through the shell - git's pager, editor,
    /// ssh, askpass and proxy, its external diff, gh's editor, pager and browser, and the pager,
    /// editor and browser any program runs: set for a command, each runs a command of the setter's
    /// choosing, read as the script it is.
    /// </summary>
    private static readonly string[] ProgramValued =
    [
        "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_SSH", "GIT_SSH_COMMAND", "GIT_ASKPASS", "GIT_PROXY_COMMAND",
        "GIT_EXTERNAL_DIFF", "GH_EDITOR", "GH_PAGER", "GH_BROWSER", "PAGER", "EDITOR", "VISUAL", "BROWSER", "SSH_ASKPASS", "MANPAGER",
    ];

    /// <summary>
    /// The commands the program-valued names among <paramref name="assignments"/> run, each value read as a script - and
    /// what git's configuration through the environment sets, which git may run so too: each <c>GIT_CONFIG_VALUE_n</c>
    /// of a key git runs as a command line (<see cref="RunsProgram"/>) or of none named, and each such setting
    /// <c>GIT_CONFIG_PARAMETERS</c> holds.
    /// </summary>
    private static IEnumerable<AnalyzedCommand> ProgramsNamed(IReadOnlyList<string> assignments, int depth, IReadOnlyList<string> environment)
    {
        var pairs = assignments
            .Select(assignment => new[] { AssignedName(assignment), assignment.Split('=', 2)[1] })
            .Where(pair => pair[1].Trim().Length > 0)
            .ToList();
        var values = new List<string>();

        // A += adds to a value set where this does not see it: the command line it makes is unread.
        if (assignments.FirstOrDefault(assignment => assignment.Split('=', 2)[0].EndsWith('+') && ProgramValued.Contains(AssignedName(assignment), StringComparer.OrdinalIgnoreCase)) is { } added)
        {
            throw new NotSupportedException($"'{added}' adds to the command line of a program another runs, which this does not read");
        }

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
                    .SelectMany(command => command.Whole)
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

        var at = 1;

        while (at < words.Count && words[at].StartsWith('-'))
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

            if (setting is not null && setting.Contains('=', StringComparison.Ordinal) && RunsProgram(setting.Split('=', 2)[0]))
            {
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

            at++;
        }

        // clone's own -c/--config run git's programs inside the new repository, as a global -c does: core.sshCommand on its
        // fetch, a hook core.hooksPath names on its checkout. Its keys stand after the subcommand, where the scan above ends.
        foreach (var setting in CloneKeyValues(words, at)
            .Where(setting => setting.Contains('=', StringComparison.Ordinal) && RunsProgram(setting.Split('=', 2)[0])))
        {
            scripts.Add(SettingScript(setting.Split('=', 2)[0], setting.Split('=', 2)[1]));
        }

        return scripts;
    }

    /// <summary>
    /// The command line a git setting's value is: a value with <c>!</c> before it - an alias's, a submodule's update -
    /// without it, for git runs what follows through the shell, and any other alias the git command it names.
    /// </summary>
    private static string SettingScript(string key, string value)
        => value.StartsWith('!') ? value[1..]
            : key.StartsWith("alias.", StringComparison.OrdinalIgnoreCase) ? "git " + value
            : value;

    /// <summary>
    /// Whether git runs the value of <paramref name="key"/> as a command line or a program: its pager, editors, ssh,
    /// askpass, monitor, proxy and credential helpers, an external diff and a diff driver's, a merge driver, a filter's
    /// clean, smudge and process, a tool's, a browser's or a trailer's command and a tool's path, a signing program and
    /// the command that gives its key, a mailer, a remote's upload and receive packs, a submodule's update, and an alias -
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
        "interactive.difffilter", "uploadpack.packobjectshook", "sendemail.sendmailcmd", "sendemail.smtpserver",
        "gpg.ssh.defaultkeycommand",
    ];

    /// <summary>
    /// The ends of git's keys of a section and a subsection (<c>diff.&lt;driver&gt;.textconv</c>) whose value it runs as a
    /// command line.
    /// </summary>
    private static readonly string[] ProgramKeyEnds =
        [".textconv", ".command", ".driver", ".clean", ".smudge", ".process", ".cmd", ".path", ".helper", ".program",
         ".sendmailcmd", ".smtpserver", ".uploadpack", ".receivepack", ".update"];

    /// <summary>The commands each script runs, one level further in: refused past <see cref="Depth"/>.</summary>
    private static IEnumerable<AnalyzedCommand> GoneInto(IReadOnlyList<string> scripts, int depth, IReadOnlyList<string> environment)
    {
        if (depth > Depth)
        {
            throw Nested(scripts);
        }

        // A script that is a word another program fills in as it runs - a file's name find puts in sh -c's place, what
        // xargs reads - is text this cannot read.
        if (scripts.Contains(InputWord))
        {
            throw new NotSupportedException("it runs a script another program fills in as it runs - a file's name, what it reads - which this does not read");
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
            : word.Contains('$', StringComparison.Ordinal) || word.Contains('%', StringComparison.Ordinal) || DelayedVariable().IsMatch(word) ? "a variable"
            : word.IndexOfAny(['*', '?']) >= 0 || (word.IndexOf('[', StringComparison.Ordinal) is var open and >= 0 && word.IndexOf(']', open + 1) > open + 1) ? "a pattern"
            : word.Contains('{', StringComparison.Ordinal) && (word.Contains(',', StringComparison.Ordinal) || word.Contains("..", StringComparison.Ordinal)) ? "a brace"
            : null;

    /// <summary>
    /// The commands <paramref name="words"/>' program runs with words it fills in as it runs them: find's <c>-exec</c>,
    /// <c>-execdir</c>, <c>-ok</c> and <c>-okdir</c> to their <c>;</c> (or the <c>+</c> after a <c>{}</c>), fd's
    /// <c>-x</c> and <c>-X</c> to theirs, rg's <c>--pre</c>, and parallel's command before its <c>:::</c> - each word it
    /// fills in (find's <c>{}</c>; fd's and parallel's <c>{}</c>, <c>{/}</c>, <c>{.}</c> and their kind) read as
    /// <see cref="InputWord"/>, and the one fd, rg and parallel add after the words where they name none - and the program
    /// rg's <c>--hostname-bin</c> names, which it runs with no word of its own.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> Executed(IReadOnlyList<string> words)
    {
        var program = Name(words[0]);

        if (Is(program, "find", "fd", "fdfind"))
        {
            var find = Is(program, "find");

            for (var at = 1; at < words.Count; at++)
            {
                if (!(find ? words[at] is "-exec" or "-execdir" or "-ok" or "-okdir" : words[at] is "-x" or "--exec" or "-X" or "--exec-batch"))
                {
                    continue;
                }

                var executed = new List<string>();

                for (at++; at < words.Count && words[at] != ";" && !(find && words[at] == "+" && executed.Count > 0 && executed[^1] == "{}"); at++)
                {
                    executed.Add(words[at]);
                }

                yield return find ? Filled(executed, word => word.Contains("{}", StringComparison.Ordinal), adds: false) : Filled(executed, Placeholder().IsMatch, adds: true);
            }
        }
        else if (Is(program, "rg"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var pre = words[at] == "--pre" && at + 1 < words.Count ? words[++at]
                    : words[at].StartsWith("--pre=", StringComparison.Ordinal) ? words[at]["--pre=".Length..]
                    : null;

                if (pre is { Length: > 0 })
                {
                    yield return [pre, InputWord];
                }

                // --hostname-bin names the program it runs, with no word of its own, for the host its links name.
                var hostname = words[at] == "--hostname-bin" && at + 1 < words.Count ? words[++at]
                    : words[at].StartsWith("--hostname-bin=", StringComparison.Ordinal) ? words[at]["--hostname-bin=".Length..]
                    : null;

                if (hostname is { Length: > 0 })
                {
                    yield return [hostname];
                }
            }
        }
        else if (Is(program, "parallel"))
        {
            // It joins its command's words and runs them through the shell, each word it fills in quoted - so a stand-in
            // quoted is read as one word, never a substitution. Its options before its command, those valued among
            // them, are read as words of the script: each is tried as the program of the words after it all the same.
            var command = words.Skip(1).TakeWhile(word => !word.StartsWith(":::", StringComparison.Ordinal)).ToList();
            var quoted = $"'{InputWord}'";
            var script = string.Join(' ', command.Select(word => Placeholder().Replace(word, quoted)));

            yield return ["sh", "-c", command.Any(Placeholder().IsMatch) ? script : $"{script} {quoted}"];
        }
        else if (GitWords(words) is { } git)
        {
            foreach (var executed in GitExecuted(git))
            {
                yield return executed;
            }
        }
    }

    /// <summary>
    /// The commands a git subcommand's own options run, each read as the command it is, so a rule
    /// meets what git would run as it meets what <c>find -exec</c> runs: the pack program a
    /// transport runs through the shell (<c>--upload-pack</c> of a fetch, pull, clone, ls-remote or
    /// fetch-pack, <c>--receive-pack</c> of a push or send-pack, their <c>--exec</c> synonyms and
    /// clone's <c>-u</c>, archive's <c>--exec</c> with <c>--remote</c>), difftool's <c>--extcmd</c>
    /// (<c>-x</c>) and grep's <c>--open-files-in-pager</c> (<c>-O</c>, its value glued as git takes
    /// an optional one) - each with the words git appends as <see cref="InputWord"/> - and the value
    /// run alone or with a patch's path appended of rebase's <c>--exec</c> (<c>-x</c>),
    /// filter-branch's filters, send-email's <c>--to-cmd</c>, <c>--cc-cmd</c>, <c>--header-cmd</c>,
    /// <c>--sendmail-cmd</c> and a <c>--smtp-server</c> that names a path. <c>bisect run</c>'s words
    /// git runs as the program and arguments they are; <c>submodule foreach</c>'s as the one script
    /// the shell evaluates. Each a shell-run value is <c>sh -c &lt;value&gt;</c>, read as the script
    /// it is - so a value an expansion gives is refused as any such script is. git's global options
    /// before the subcommand are passed over as <see cref="Plain"/> passes them.
    /// </summary>
    private static IEnumerable<IReadOnlyList<string>> GitExecuted(IReadOnlyList<string> words)
    {
        var (command, _) = Plain(words, program: false);

        if (command.Count < 2 || !Is(Name(command[0]), "git"))
        {
            yield break;
        }

        var subcommand = Name(command[1]).ToLowerInvariant();

        // bisect run names its command as the words after 'run', which git runs as the program and arguments they are.
        if (subcommand == "bisect" && command.ElementAtOrDefault(2) is "run" && command.Count > 3)
        {
            yield return [.. command.Skip(3)];
            yield break;
        }

        // submodule foreach evaluates the words after it as one shell script in each submodule, its --recursive and -q its own.
        if (subcommand == "submodule" && Foreach(command) is { } foreachScript)
        {
            yield return ["sh", "-c", foreachScript];
            yield break;
        }

        // archive runs its --exec only when it fetches from a --remote; without one git ignores it.
        var specs = subcommand == "archive"
                && !Enumerable.Range(2, command.Count - 2).Any(at => GitOptionValue(command, at, ["remote"], string.Empty, gluedOnly: false) is not null)
            ? []
            : GitExecutedOptions(subcommand);

        for (var at = 2; at < command.Count; at++)
        {
            foreach (var spec in specs)
            {
                if (GitOptionValue(command, at, spec.Long, spec.Short, spec.GluedOnly) is { } found
                    && (!spec.PathOnly || NamesAPath(found.Value)))
                {
                    yield return spec.Appends ? ["sh", "-c", found.Value, InputWord] : ["sh", "-c", found.Value];
                    at = found.Next - 1;
                    break;
                }
            }
        }
    }

    /// <summary>
    /// A git command's words with git its program: as they stand where it is git, and a dashed command - git-ls-remote, as
    /// git's own directory holds each of its commands - as the <c>git ls-remote</c> it is; null where the program is no git.
    /// </summary>
    private static IReadOnlyList<string>? GitWords(IReadOnlyList<string> words)
        => words.Count == 0 ? null
            : Is(Name(words[0]), "git") ? words
            : Name(words[0]) is { Length: > 4 } dashed && dashed.StartsWith("git-", StringComparison.OrdinalIgnoreCase) ? ["git", dashed[4..], .. words.Skip(1)]
            : null;

    /// <summary>
    /// A git subcommand's options whose value git runs: each its long names (given without the
    /// <c>--</c>) and the short letters it also goes by, whether only a glued value is read (git's
    /// rule for an option whose argument is optional, as grep's pager is), whether git appends words
    /// of its own to it (<see cref="InputWord"/>), and whether only a value naming a path is a
    /// command (a <c>--smtp-server</c>'s). None where the subcommand runs no option's value.
    /// </summary>
    private static IReadOnlyList<(IReadOnlyList<string> Long, string Short, bool GluedOnly, bool Appends, bool PathOnly)> GitExecutedOptions(string subcommand)
        => subcommand switch
        {
            "ls-remote" or "fetch-pack" => [(["upload-pack", "exec"], "", false, true, false)],
            "fetch" or "pull" => [(["upload-pack"], "", false, true, false)],
            "clone" => [(["upload-pack"], "u", false, true, false)],
            "push" or "send-pack" => [(["receive-pack", "exec"], "", false, true, false)],
            "archive" => [(["exec"], "", false, true, false)],
            "difftool" => [(["extcmd"], "x", false, true, false)],
            "grep" => [(["open-files-in-pager"], "O", true, true, false)],
            "rebase" => [(["exec"], "x", false, false, false)],
            "filter-branch" => [(["setup", "env-filter", "tree-filter", "index-filter", "parent-filter", "msg-filter", "commit-filter", "tag-name-filter"], "", false, false, false)],
            "send-email" => [(["to-cmd", "cc-cmd", "header-cmd"], "", false, true, false), (["sendmail-cmd"], "", false, false, false), (["smtp-server"], "", false, true, true)],
            _ => [],
        };

    /// <summary>
    /// The command <c>git submodule foreach</c> evaluates, the words after <c>foreach</c> joined -
    /// its <c>--recursive</c> and <c>-q</c> no part of it; null where the submodule command is not a
    /// <c>foreach</c>, or names no command.
    /// </summary>
    private static string? Foreach(IReadOnlyList<string> command)
    {
        for (var at = 2; at < command.Count; at++)
        {
            if (command[at] == "foreach")
            {
                var script = command.Skip(at + 1).Where(word => word is not ("--recursive" or "-q" or "--quiet" or "--")).ToList();

                return script.Count > 0 ? string.Join(' ', script) : null;
            }

            if (!command[at].StartsWith('-'))
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Whether a <c>--smtp-server</c>'s value may name a path, which git runs as the mailer: one written whole from a root
    /// (<c>/</c>, a drive's <c>C:\</c>), one the shell makes so (<c>~/bin/m</c>), and one an expansion gives.
    /// </summary>
    private static bool NamesAPath(string value)
        => value.StartsWith('/') || value.StartsWith('~') || Expansion(value) is not null
            || (value.Length > 2 && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] is '\\' or '/');

    /// <summary>
    /// The value a git option carries at <paramref name="at"/>, as git's parse-options reads it: <c>--name value</c> or
    /// <c>--name=value</c> by any prefix of its name, which git takes for the whole where it is no other option's; a short
    /// <c>-x value</c> or <c>-xvalue</c>, and the letter at the end of a cluster or inside one (<c>-dx value</c>,
    /// <c>-dxvalue</c>), the letters before it ones that take no value. Where <paramref name="gluedOnly"/>, only the glued
    /// forms (<c>--name=value</c>, <c>-xvalue</c>), as git reads an option whose argument is optional. Read so where a
    /// prefix or a letter may be another option's too: a value misread is a command refused, never one let through. Null
    /// where the word names none of <paramref name="longNames"/> or <paramref name="shortLetters"/>, or names one with no
    /// value to read.
    /// </summary>
    private static (string Value, int Next)? GitOptionValue(IReadOnlyList<string> words, int at, IReadOnlyList<string> longNames, string shortLetters, bool gluedOnly)
    {
        var word = words[at];

        if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2)
        {
            var named = word[2..].Split('=', 2);

            if (!longNames.Any(name => name.StartsWith(named[0], StringComparison.Ordinal)))
            {
                return null;
            }

            return named.Length == 2 ? (named[1], at + 1)
                : !gluedOnly && at + 1 < words.Count ? (words[at + 1], at + 2)
                : null;
        }

        if (word.Length < 2 || word[0] != '-' || word[1] == '-')
        {
            return null;
        }

        for (var letter = 1; letter < word.Length; letter++)
        {
            if (shortLetters.IndexOf(word[letter]) >= 0)
            {
                return letter + 1 < word.Length ? (word[(letter + 1)..], at + 1)
                    : !gluedOnly && at + 1 < words.Count ? (words[at + 1], at + 2)
                    : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The words a program runs, each it fills in an <see cref="InputWord"/> whole - a word with a name put inside it is
    /// that name's, whatever it holds - and one after them where <paramref name="adds"/> and it names none.
    /// </summary>
    private static List<string> Filled(IReadOnlyList<string> executed, Func<string, bool> fills, bool adds)
    {
        var filled = executed.Select(word => fills(word) ? InputWord : word).ToList();

        if (adds && !executed.Any(fills))
        {
            filled.Add(InputWord);
        }

        return filled;
    }

    /// <summary>A word fd or parallel fills in: <c>{}</c>, <c>{/}</c>, <c>{//}</c>, <c>{.}</c>, <c>{/.}</c>, <c>{1}</c>, <c>{#}</c> and their kind.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\{[^{},\s]*\}")]
    private static partial System.Text.RegularExpressions.Regex Placeholder();

    /// <summary>
    /// The words <c>env -S</c> splits its string into, by env's own rules, not the shell's: apart at
    /// whitespace alone - a ';', a '|' or a '&lt;' a character like any - a single or a double
    /// quote's text kept as it stands, a '#' where a word begins ending the string, and a
    /// <c>${NAME}</c> kept as the expansion it is. A backslash, whose escapes env reads by rules of its own (<c>\c</c> ends
    /// the string, <c>\_</c> parts words), a quote that does not close and a comment a line end
    /// follows are refused.
    /// </summary>
    /// <exception cref="NotSupportedException">The string holds what this does not read, named.</exception>
    private static List<string> SplitWords(string text)
    {
        var words = new List<string>();
        var word = new System.Text.StringBuilder();
        var inWord = false;

        for (var at = 0; at < text.Length; at++)
        {
            var character = text[at];

            if (character == '\\')
            {
                throw new NotSupportedException($"env -S '{text}' holds a backslash, whose escapes env reads by rules of its own, which this does not read");
            }

            if (character is ' ' or '\t' or '\n' or '\r' or '\f' or '\v')
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }

                continue;
            }

            if (character == '#' && !inWord)
            {
                return text.IndexOfAny(['\n', '\r'], at) < 0
                    ? words
                    : throw new NotSupportedException($"env -S '{text}' holds a comment a line end follows, which this does not read");
            }

            if (character is '\'' or '"')
            {
                var close = text.IndexOf(character, at + 1);

                if (close < 0)
                {
                    throw new NotSupportedException($"env -S '{text}' holds a quote that does not close, which this does not read");
                }

                word.Append(text, at + 1, close - at - 1);
                at = close;
                inWord = true;
                continue;
            }

            word.Append(character);
            inWord = true;
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    /// <summary>
    /// A variable cmd expands as it runs a line, where its delayed expansion is on: <c>!NAME!</c>, with its modifiers
    /// (<c>!P:~0!</c>, <c>!P:a=b!</c>) and whatever its name holds.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"![^!\r\n]+!")]
    private static partial System.Text.RegularExpressions.Regex DelayedVariable();

    /// <summary>
    /// The command line <c>env -S</c> (<c>--split-string</c>) takes, the pairs before it and the
    /// words that follow it: null where the words name none. Read before any word of the command
    /// proper, as env reads it (<see cref="EnvWords"/>).
    /// </summary>
    private static (IReadOnlyList<string> Pairs, string Script, IReadOnlyList<string> Tail)? SplitString(IReadOnlyList<string> words)
        => EnvWords(words) is { Split: { } split } read ? (read.Pairs, split, [.. words.Skip(read.At)]) : null;

    /// <summary>
    /// The words env runs once it splits <c>-S</c>'s string: env again, the pairs before the string, the string's own
    /// words and the words after it - for env reads its options on into the words it split, as a <c>-C</c> or an
    /// <c>-i</c> a string holds is env's own (<c>#!/usr/bin/env -S -i bash</c>).
    /// </summary>
    private static IReadOnlyList<string> SplitInto((IReadOnlyList<string> Pairs, string Script, IReadOnlyList<string> Tail) split)
        => ["env", .. split.Pairs, .. SplitWords(split.Script), .. split.Tail];

    /// <summary>
    /// env's own words before its command, as GNU env reads them: its NAME=value pairs; its options - <c>-i</c>,
    /// <c>-0</c>, <c>-v</c> and their long names, the signal ones, and <c>-u</c>, <c>-C</c> and <c>-a</c> with their
    /// values, glued or the next word's, in a short cluster too; and <c>-S</c>'s command line, the split string. At is
    /// where its command begins - after the split string where one is given, or past every word where a help or a
    /// version runs nothing. An option it does not know is where its command begins, as a word this reads. Directories
    /// are those <c>-C</c> and <c>--chdir</c> start its command in, each in the order given.
    /// </summary>
    private static (List<string> Pairs, int At, string? Split, List<string> Directories) EnvWords(IReadOnlyList<string> words)
    {
        var pairs = new List<string>();
        var directories = new List<string>();

        for (var at = 1; at < words.Count; at++)
        {
            var option = words[at];

            if (IsAssignment(option))
            {
                pairs.Add(option);
                continue;
            }

            if (option == "--")
            {
                return (pairs, at + 1, null, directories);
            }

            if (option.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = option.IndexOf('=', StringComparison.Ordinal);
                var named = equals > 0 ? option[..equals] : option;

                if (named == "--split-string")
                {
                    return equals > 0 ? (pairs, at + 1, option[(equals + 1)..], directories)
                        : at + 1 < words.Count ? (pairs, at + 2, words[at + 1], directories)
                        : (pairs, words.Count, null, directories);
                }

                if (named is "--help" or "--version")
                {
                    return (pairs, words.Count, null, directories);
                }

                if (named is "--unset" or "--chdir" or "--argv0")
                {
                    if (named == "--chdir" && (equals > 0 ? option[(equals + 1)..] : words.ElementAtOrDefault(at + 1)) is { } directory)
                    {
                        directories.Add(directory);
                    }

                    at += equals > 0 ? 0 : 1;
                    continue;
                }

                if (named is "--ignore-environment" or "--null" or "--debug" or "--block-signal" or "--default-signal" or "--ignore-signal" or "--list-signal-handling")
                {
                    continue;
                }

                return (pairs, at, null, directories);
            }

            if (option.Length < 2 || option[0] != '-')
            {
                return (pairs, at, null, directories);
            }

            for (var letter = 1; letter < option.Length; letter++)
            {
                switch (option[letter])
                {
                    case 'i' or '0' or 'v':
                        continue;
                    case 'S':
                        return letter + 1 < option.Length ? (pairs, at + 1, option[(letter + 1)..], directories)
                            : at + 1 < words.Count ? (pairs, at + 2, words[at + 1], directories)
                            : (pairs, words.Count, null, directories);
                    case 'u' or 'C' or 'a':
                        if (option[letter] == 'C' && (letter + 1 < option.Length ? option[(letter + 1)..] : words.ElementAtOrDefault(at + 1)) is { } directory)
                        {
                            directories.Add(directory);
                        }

                        at += letter + 1 < option.Length ? 0 : 1;
                        letter = option.Length;
                        continue;
                    default:
                        return (pairs, at, null, directories);
                }
            }
        }

        return (pairs, words.Count, null, directories);
    }

    /// <summary>
    /// The directories the program <paramref name="words"/> begin with starts what it runs in - env's <c>-C</c> and
    /// <c>--chdir</c>, PowerShell's <c>-WorkingDirectory</c> - each a move the policy follows as it follows a <c>cd</c>
    /// (<see cref="Moved"/>); none where the program moves nothing.
    /// </summary>
    private static IReadOnlyList<string> MovesOf(IReadOnlyList<string> words)
        => words.Count == 0 ? []
            : Is(Name(words[0]), "env") ? EnvWords(words).Directories
            : Is(Name(words[0]), "powershell", "pwsh") ? PowerShellDirectories(words)
            : [];

    /// <summary>
    /// Whether <paramref name="word"/> names a program that may start what it runs in another directory (<see cref="MovesOf"/>):
    /// asked before the words after it are copied for that question, so a long command is not copied at each of its words.
    /// </summary>
    private static bool MayMove(string word) => Is(Name(word), "env", "powershell", "pwsh");

    /// <summary>
    /// A move to <paramref name="directory"/>, as the command it is - <c>cd -- directory</c>, before the command it starts
    /// there - so every path after it is read where it lands, as after the shell's own cd, by the one reader of moves.
    /// </summary>
    private static AnalyzedCommand Moved(string directory) => new(["cd", "--", directory], []);

    /// <summary>
    /// The scripts <paramref name="name"/> would run with <paramref name="words"/>, each ready for
    /// the shell reader, where it names one; null where it names none. Where it runs a script this
    /// cannot read - a shell that reads its script from what an earlier command printed, a script
    /// file named by an expansion - it is refused, where <paramref name="program"/> says the words
    /// are a command's own; a word another program takes as its argument (<c>which bash</c>) is
    /// never refused so.
    /// </summary>
    /// <param name="name">The program's name, as <see cref="Name"/> reads it.</param>
    /// <param name="words">The program and its words.</param>
    /// <param name="redirects">The redirects the shell makes for the command the words stand in.</param>
    /// <param name="program">Whether the words are a command's own, its program first, rather than another program's arguments.</param>
    private static IReadOnlyList<string>? Into(string name, IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program)
    {
        if (Is(name, "fish"))
        {
            return FishScripts(name, words, redirects, program);
        }

        // A help or a version runs nothing, where the program reads it as its own option: before what it runs, or, for a
        // program that reads its options wherever they stand (script, flock), anywhere.
        if (Is(name, Shells))
        {
            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option is "--help" or "--version")
                {
                    return null;
                }

                if (option == "--")
                {
                    return at + 1 < words.Count ? ScriptFile(words[at + 1], name, redirects, program) : Input(name, redirects, program);
                }

                if (!(option.StartsWith('-') || option.StartsWith('+')))
                {
                    return ScriptFile(option, name, redirects, program);
                }

                // An option that takes a value in the next word: the value is no script and no option - an o or an O
                // in a short cluster too (-eo pipefail), each taking a word.
                if (ShellValueTaking.Contains(option, StringComparer.Ordinal))
                {
                    at++;
                    continue;
                }

                if (!option.StartsWith("--", StringComparison.Ordinal) && option.Length > 1 && !option.Contains('c', StringComparison.Ordinal)
                    && option.Count(letter => letter is 'o' or 'O') is var valued and > 0)
                {
                    at += valued;
                    continue;
                }

                // The option that carries c takes the next word as its script, alone (-c) or glued
                // into a short cluster (-lc); a long option (--norc among them) is never a short
                // cluster, however many letters it holds. The words after the script go with it
                // (ScriptAndArguments).
                var cluster = !option.StartsWith("--", StringComparison.Ordinal) && option.Length > 1;

                if (option == "-c" || (cluster && option.Contains('c', StringComparison.Ordinal)))
                {
                    return at + 1 < words.Count ? ScriptAndArguments(words[at + 1], words.Skip(at + 2)) : null;
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
            return words.Count > 1 ? ScriptFile(words[1], name, redirects, program) : null;
        }

        if (Is(name, "cmd"))
        {
            for (var at = 1; at < words.Count; at++)
            {
                if (words[at] == "/?")
                {
                    return null;
                }

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
            // Windows PowerShell's command begins at its first word that is neither a parameter nor a
            // parameter's value, its parameters read by their own table; pwsh takes a script file so.
            string? file = null;

            for (var at = 1; at < words.Count; at++)
            {
                var option = words[at];

                if (option == "-")
                {
                    return Input(name, redirects, program, PowerShellAsPosix);
                }

                if (PowerShellParameter(option) is not { } parameter)
                {
                    if (Is(name, "powershell"))
                    {
                        return [PowerShellAsPosix(string.Join(' ', words.Skip(at)))];
                    }

                    file ??= option;
                    continue;
                }

                if (parameter.Length == 0)
                {
                    continue;
                }

                // Help, and pwsh's version, run nothing; Windows PowerShell's -Version names the engine it runs, by its value.
                if (parameter == "?" || "help".StartsWith(parameter, StringComparison.Ordinal)
                    || (Is(name, "pwsh") && "version".StartsWith(parameter, StringComparison.Ordinal)))
                {
                    return null;
                }

                if ("version".StartsWith(parameter, StringComparison.Ordinal))
                {
                    at++;
                    continue;
                }

                if (parameter == "ec" || "encodedcommand".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 < words.Count ? [PowerShellAsPosix(Decode(words[at + 1]))] : null;
                }

                // A '-' as the command or the file is the input, as a '-' alone is.
                if (parameter is "cwa" or "commandwithargs" || "command".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 >= words.Count ? null
                        : words[at + 1] != "-" ? [PowerShellAsPosix(string.Join(' ', words.Skip(at + 1)))]
                        : Input(name, redirects, program, PowerShellAsPosix);
                }

                if ("file".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 >= words.Count ? null
                        : words[at + 1] == "-" ? Input(name, redirects, program, PowerShellAsPosix)
                        : ScriptFile(words[at + 1], name, redirects, program, PowerShellAsPosix);
                }

                // Windows PowerShell's other parameters: a valued one's value passed, a switch alone; one it does not
                // know may take the word after it, where its command would begin.
                if (Is(name, "powershell"))
                {
                    if (WindowsPowerShellValued.Any(valued => valued.StartsWith(parameter, StringComparison.Ordinal)))
                    {
                        at++;
                    }
                    else if (!WindowsPowerShellSwitches.Any(known => known.StartsWith(parameter, StringComparison.Ordinal)))
                    {
                        return program
                            ? throw new NotSupportedException($"'{option}' is an option of Windows PowerShell's own this does not know, which may take the word after it as its value")
                            : null;
                    }
                }
                else if ((IsWorkingDirectory(parameter) || PwshValued.Any(valued => valued.StartsWith(parameter, StringComparison.Ordinal)))
                    && at + 1 < words.Count && PowerShellParameter(words[at + 1]) is null)
                {
                    // pwsh's valued parameters - its working directory, its execution policy - pass their value, which is no
                    // script file: taken for one, the script pwsh reads from its input would go unread. A word that is
                    // itself a parameter is read as one, never passed as a value, so the command it may begin is read.
                    at++;
                }
            }

            return file is not null ? ScriptFile(file, name, redirects, program, PowerShellAsPosix)
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
            if (words.Skip(1).Any(word => word is "--help" or "-h" or "--version" or "-V"))
            {
                return null;
            }

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
                // -x runs the words as they are, each met by the rules on its own program; a help or a version, nothing.
                if (words[at] is "-x" or "--exec" or "-h" or "--help" or "-v" or "--version")
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
    /// A script file a shell is handed: unseen, as help says, where a word names it; the input itself
    /// where it names the input (<c>/dev/stdin</c>, <c>/dev/fd/0</c>), read as <see cref="Input"/>
    /// reads it - and refused where an expansion names it (<c>bash &lt;(curl ...)</c>,
    /// <c>source $F</c>) or another open descriptor does (<c>/dev/fd/3</c>), where the words are a
    /// command's own, for what it runs is what another command printed.
    /// </summary>
    private static IReadOnlyList<string>? ScriptFile(string file, string name, IReadOnlyList<ShellRedirect> redirects, bool program, Func<string, string>? spelled = null)
        => Folded(file) is "/dev/stdin" or "/dev/fd/0" or "/proc/self/fd/0" ? Input(name, redirects, program, spelled)
            : program && Unread(file) is { } unread ? throw new NotSupportedException($"'{file}' names the script it runs by {unread}, which this does not read")
            : null;

    /// <summary>
    /// What names a script file by what this does not read - an expansion, or an open descriptor's own name however its
    /// path is spelled (<c>/dev//stdin</c>, <c>/dev/../dev/fd/3</c>), or relative to where the command may stand
    /// (<c>stdin</c>, <c>fd/3</c>) - or null.
    /// </summary>
    private static string? Unread(string file)
        => Expansion(file) ?? (OpenDescriptor().IsMatch(Folded(file)) ? "an open descriptor" : null);

    /// <summary>A POSIX path with its doubled slashes, its '.' and its '..' folded away, as the system walks it.</summary>
    private static string Folded(string path)
    {
        var parts = new List<string>();

        foreach (var part in path.Split('/'))
        {
            if (part is "" or ".")
            {
                continue;
            }

            if (part == ".." && parts.Count > 0 && parts[^1] != "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part != ".." || !path.StartsWith('/'))
            {
                parts.Add(part);
            }
        }

        return (path.StartsWith('/') ? "/" : string.Empty) + string.Join('/', parts);
    }

    /// <summary>An open descriptor's own name: <c>stdin</c> and its kind, <c>fd/N</c>, under any directory - <c>/dev</c>, <c>/proc/&lt;process&gt;</c> - or none.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"(^|/)(stdin|stdout|stderr|fd/\d+)$")]
    private static partial System.Text.RegularExpressions.Regex OpenDescriptor();

    /// <summary>
    /// The script a program that reads one from its input is given: a here-string's or a
    /// here-document's text, spelled for the shell reader; null where a file is its input - a
    /// script file, unseen as any is. A program given nothing reads what an earlier command printed,
    /// and is refused, where the words are a command's own.
    /// </summary>
    private static IReadOnlyList<string>? Input(string name, IReadOnlyList<ShellRedirect> redirects, bool program, Func<string, string>? spelled = null)
    {
        // The shell reads what the last redirect of its descriptor 0 gives it: a text, or a file
        // unseen as any script file is; one duplicated from another descriptor, or named by one,
        // is what an earlier command printed.
        switch (redirects.LastOrDefault(redirect => redirect.Descriptor == 0))
        {
            case { Operator: "<<<" } text:
                return [spelled is null ? text.Target : spelled(text.Target)];
            case { Operator: not "<&" } file when Unread(file.Target) is not { } || !program:
                return null;
            case { } other when program:
                throw new NotSupportedException($"'{name}' reads the script it runs from '{other}', which this does not read");
        }

        return program
            ? throw new NotSupportedException($"'{name}' reads the script it runs from its input, which this does not read")
            : null;
    }

    /// <summary>
    /// The programs that read their words as data and run none of them - a search's pattern, a printed text, a looked-up
    /// name - whose words are never tried as the program of the words after them: <c>grep -rn http src/</c> fetches
    /// nothing. rg's <c>--pre</c>, the one that runs a command, is read as the command it runs (<see cref="Executed"/>).
    /// </summary>
    public static IReadOnlyList<string> DataPrograms { get; } =
        ["grep", "egrep", "fgrep", "rg", "echo", "printf", "which", "whereis", "type", "cat", "head", "tail", "wc", "ls", "stat", "file",
         "basename", "dirname", "realpath", "readlink"];

    /// <summary>Whether <paramref name="program"/> runs none of its words (<see cref="DataPrograms"/>), by its name in any case.</summary>
    public static bool RunsNoWords(string program) => Is(Name(program), [.. DataPrograms]);

    /// <summary>
    /// The programs that run the words after their own options as a command, which this does not strip - their options
    /// are theirs to read - each tried by the rules on its own program as every word is: a check that holds only where a
    /// program runs (a gh named by none of gh's own) holds past these too, and among any other program's words reads its
    /// word as that program's data.
    /// </summary>
    public static IReadOnlyList<string> CommandRunners { get; } =
        ["setsid", "stdbuf", "ionice", "chrt", "taskset", "unshare", "nsenter", "prlimit", "numactl", "cpulimit", "firejail", "flock",
         "torsocks", "proxychains", "proxychains4", "faketime", "strace", "ltrace", "valgrind", "caffeinate", "chroot", "runuser",
         "watch", "ssh", "wsl", "start", "call"];

    /// <summary>Whether <paramref name="program"/> runs the words after its own options (<see cref="CommandRunners"/>), by its name in any case.</summary>
    public static bool RunsItsWords(string program) => Is(Name(program), [.. CommandRunners]);

    /// <summary>
    /// What fish runs, its options read as fish reads them, getopt's way - a short option's value glued after it or the
    /// next word, a long option's after an '=' or the next word, and a long option named by any prefix that begins it
    /// alone: each <c>-c</c>'s script and each <c>-C</c>'s (<c>--init-command</c>, run before them) and the words
    /// after the options, which a script may run; with no <c>-c</c>, the <c>-C</c> scripts and its script file or its
    /// input. Its help is one more command it runs, the scripts after it still run; its version, or an option missing
    /// its value, runs nothing.
    /// </summary>
    /// <exception cref="NotSupportedException">The command's own fish is given an option this does not know, which may take a value.</exception>
    private static IReadOnlyList<string>? FishScripts(string name, IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program)
    {
        var scripts = new List<string>();
        var commanded = false;
        var at = 1;

        for (; at < words.Count && words[at].Length > 1 && words[at][0] == '-'; at++)
        {
            var word = words[at];

            if (word == "--")
            {
                at++;
                break;
            }

            // Each option the word gives, with the value of one that takes a value.
            var given = new List<(char Letter, string? Value)>();

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = word.IndexOf('=', StringComparison.Ordinal);
                var spelled = equals < 0 ? word[2..] : word[2..equals];
                var named = FishLongOptions.Where(option => option.Name.StartsWith(spelled, StringComparison.Ordinal)).ToList();
                var letter = named.FirstOrDefault(option => option.Name == spelled) is { Name: not null } exact ? exact.Letter
                    : named.Count == 1 ? named[0].Letter
                    : '\0';

                if (letter == '\0')
                {
                    return program
                        ? throw new NotSupportedException($"'{word}' is an option of fish's own this does not read, which may take the word after it as its value")
                        : null;
                }

                given.Add((letter, !FishValued.Contains(letter) ? null
                    : equals >= 0 ? word[(equals + 1)..]
                    : at + 1 < words.Count ? words[++at]
                    : null));
            }
            else
            {
                for (var index = 1; index < word.Length; index++)
                {
                    var letter = word[index];

                    if (FishValued.Contains(letter))
                    {
                        given.Add((letter, index + 1 < word.Length ? word[(index + 1)..] : at + 1 < words.Count ? words[++at] : null));
                        break;
                    }

                    if (!FishFlags.Contains(letter))
                    {
                        return program
                            ? throw new NotSupportedException($"'{word}' gives an option of fish's own this does not read, which may take the word after it as its value")
                            : null;
                    }

                    given.Add((letter, null));
                }
            }

            foreach (var (letter, value) in given)
            {
                if (letter == 'v' || (FishValued.Contains(letter) && value is null))
                {
                    return null;
                }

                if (letter is 'c' or 'C')
                {
                    scripts.Add(value!);
                    commanded |= letter == 'c';
                }
            }
        }

        var read = commanded ? [.. words.Skip(at).Where(word => word != InputWord)]
            : at < words.Count ? ScriptFile(words[at], name, redirects, program)
            : Input(name, redirects, program);

        return scripts.Count > 0 ? [.. scripts, .. read ?? []] : read;
    }

    /// <summary>fish's long options, each with the short option it stands for; one with no short option a letter fish has none of.</summary>
    private static readonly (string Name, char Letter)[] FishLongOptions =
    [
        ("command", 'c'), ("init-command", 'C'), ("features", 'f'), ("debug", 'd'), ("debug-output", 'o'),
        ("debug-stack-frames", 'D'), ("interactive", 'i'), ("login", 'l'), ("no-execute", 'n'), ("print-rusage-self", '1'),
        ("print-debug-categories", '2'), ("profile", 'p'), ("profile-startup", '3'), ("private", 'P'), ("help", 'h'),
        ("version", 'v'), ("no-config", 'N'),
    ];

    /// <summary>fish's options that take a value, as their short letters - and profile-startup's, which has none.</summary>
    private const string FishValued = "cCpdfDo3";

    /// <summary>fish's short options that take no value.</summary>
    private const string FishFlags = "hPilNnv";

    /// <summary>
    /// A shell's script and the words after it - its own $0, $1 and on, which the script may run: read as scripts too, but
    /// a word another program fills in, which the script's own reading decides where it runs it.
    /// </summary>
    private static IReadOnlyList<string> ScriptAndArguments(string script, IEnumerable<string> after)
        => [script, .. after.Where(word => word != InputWord)];

    /// <summary>The shells whose <c>-c</c> takes a script - fish's read on their own (<see cref="FishScripts"/>).</summary>
    private static readonly string[] Shells =
        ["sh", "bash", "zsh", "dash", "ksh", "mksh", "pdksh", "oksh", "ash", "yash", "posh", "rbash", "csh", "tcsh"];

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
            // Its pairs, kept as the command's environment, then the command; -S's command line is read by the
            // flattener above this.
            var read = EnvWords(words);

            return [.. read.Pairs, .. words.Skip(read.At)];
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
                ? Filled(command, word => word.Contains(replace, StringComparison.Ordinal), adds: false)
                : Filled(command, word => false, adds: true);
        }
        else if (name is "command" or "builtin")
        {
            while (at < words.Count && words[at] is "-v" or "-V" or "-p")
            {
                // -v and -V say what a name is and run nothing: no wrapper, its words each tried as a program still.
                if (words[at] is "-v" or "-V")
                {
                    return null;
                }

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
    private static IEnumerable<AnalyzedCommand> FromScript(string script, int depth, IReadOnlyList<string> environment)
    {
        foreach (var command in Script(script, 0))
        {
            foreach (var flat in FromWords(command.Words, command.Redirects, depth, environment))
            {
                yield return flat;
            }
        }
    }
}
