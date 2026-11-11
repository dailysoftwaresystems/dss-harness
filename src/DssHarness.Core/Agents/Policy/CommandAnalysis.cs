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
    /// <param name="splitByItsProgram">
    /// Whether the program is handed its words as one command line that it splits itself, as every program on Windows
    /// is: a Cygwin or MSYS program, and one a C runtime's wildcard expansion is linked into, expands the patterns and
    /// the braces among its words as a shell would, so their characters are read as open ones. Elsewhere a program is
    /// handed its words as they are, and nothing expands them.
    /// </param>
    /// <param name="inputGiven">
    /// Whether the program is given an input this does not see - the text the tool runtime writes to it - which a program
    /// that reads its script from its input would run (<see cref="AnalyzedCommand.InputGiven"/>).
    /// </param>
    /// <exception cref="NotSupportedException">The command holds what this does not read, named.</exception>
    public static IReadOnlyList<AnalyzedCommand> Commands(IReadOnlyList<string> argv, bool splitByItsProgram = false, bool inputGiven = true)
    {
        ArgumentNullException.ThrowIfNull(argv);
        Uncut(argv);

        // An argument list crosses no shell: a variable, a substitution and a cmd percent in one are what they spell. A
        // program that splits its own command line may still expand a pattern or a brace in it, which is read as open.
        IReadOnlyList<string> spelled = [.. argv.Select(word => string.Concat(word.Select(character
            => splitByItsProgram && ExpandedByItsProgram.Contains(character) ? WordQuoting.Open : WordQuoting.Literal)))];

        return Uncut([.. FromWords(argv, spelled, [], 1, [], inputGiven: inputGiven).Where(command => command.Whole.Count > 0)]);
    }

    /// <summary>
    /// The characters a program that splits its own command line expands, as Cygwin's and MSYS's runtimes do and a C
    /// runtime's wildcard expansion: a pattern's - <c>*</c>, <c>?</c>, a bracket - and a brace's with its commas.
    /// </summary>
    private const string ExpandedByItsProgram = "*?[]{},";

    /// <summary>
    /// The characters that name a program by a pattern however they are quoted: PowerShell's call operator finds the
    /// program a quoted path names by its wildcards (<c>&amp; 'C:\Git\cmd\gi?.exe'</c> runs git), and no program's own
    /// name holds one.
    /// </summary>
    private const string ProgramPatterns = "*?[]";

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
    /// The programs whose work is to keep what they start running apart from the command that starts it - past its shell's
    /// end, its hangup or its session - that are files, found by any path to them: <c>nohup</c>, <c>setsid</c>,
    /// <c>daemonize</c>, <c>start-stop-daemon</c>, and <c>at</c> and <c>batch</c> given a job, which their daemon runs.
    /// </summary>
    private static readonly string[] DetachingPrograms = ["nohup", "setsid", "daemonize", "start-stop-daemon", "at", "batch"];

    /// <summary>
    /// PowerShell's cmdlets that keep what they start apart, which PowerShell also finds by a module-qualified name:
    /// <c>Start-Process</c>, and its jobs, <c>Start-Job</c> and <c>Start-ThreadJob</c>, whose block runs on as the job
    /// <c>&amp;</c>'s does.
    /// </summary>
    private static readonly string[] DetachingCmdlets = ["Start-Process", "Start-Job", "Start-ThreadJob"];

    /// <summary>
    /// The shell's and PowerShell's own commands that keep what they start apart, which no path reaches: <c>disown</c>,
    /// Windows' <c>start</c> - cmd's own, and PowerShell's name for <c>Start-Process</c> - <c>saps</c> and <c>sajb</c>,
    /// PowerShell's names for <c>Start-Process</c> and <c>Start-Job</c>, and <see cref="DetachingCmdlets"/>.
    /// </summary>
    private static readonly string[] DetachingCommands = ["disown", "start", "saps", "sajb", .. DetachingCmdlets];

    /// <summary>
    /// The programs and the shells' own commands whose work is to keep what they start running apart from the command
    /// that starts it: <see cref="DetachingPrograms"/>, then <see cref="DetachingCommands"/>.
    /// </summary>
    public static IReadOnlyList<string> Detaching { get; } = [.. DetachingPrograms, .. DetachingCommands];

    /// <summary>A name PowerShell reads as a module's command (<c>Microsoft.PowerShell.Management\Start-Process</c>): a module's name, a backslash, the command.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_.\-]*\\[^\\/]+$")]
    private static partial System.Text.RegularExpressions.Regex ModuleQualified();

    /// <summary>The shells a command may be read by, each expanding its words in its own way.</summary>
    internal enum Reader
    {
        /// <summary>A POSIX shell, or fish: the string's own, or one a command runs.</summary>
        Shell,

        /// <summary>PowerShell, whose cmdlets expand their wildcards themselves, quoted or not.</summary>
        PowerShell,

        /// <summary>cmd.exe.</summary>
        Cmd,
    }

    /// <summary>
    /// The shell that reads <paramref name="analyzed"/>: the innermost of the commands that run it that is a shell
    /// (<see cref="AnalyzedCommand.Wrappers"/>) - <c>pwsh -c</c>'s PowerShell, <c>cmd /c</c>'s cmd.exe, <c>bash -c</c>'s
    /// shell - else the shell the string was given to.
    /// </summary>
    internal static Reader ReadBy(AnalyzedCommand analyzed)
    {
        foreach (var words in analyzed.Wrappers.Reverse())
        {
            var at = EnvironmentOf(words);

            if (at >= words.Count)
            {
                continue;
            }

            var name = Name(words[at]);

            if (name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase))
            {
                return Reader.PowerShell;
            }

            if (name.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            {
                return Reader.Cmd;
            }

            if (IsShell(name))
            {
                return Reader.Shell;
            }
        }

        return Reader.Shell;
    }

    /// <summary>
    /// Whether <paramref name="command"/> leaves a process running after the command it stands in ends: it runs in the
    /// background (<see cref="AnalyzedCommand.Background"/>), PowerShell reads it given <c>-AsJob</c>, or one of
    /// <see cref="Detaching"/> runs at a place a program of it runs (<see cref="RunsAt"/>) - among its words, or among a
    /// wrapper's stripped before them, as <c>nohup</c> is - at and batch where they schedule a job (<see cref="AtJob"/>), not
    /// where they list, remove or print one. A program is matched by the last segment of any path to it, a Windows
    /// program's <c>.exe</c> folded (<c>/usr/bin/setsid</c>); a shell's own command by its bare word alone, for a word with a
    /// directory part or a script's end (<c>./bin/start</c>, <c>scripts\start</c>, <c>start.cmd</c>) names the repository's own
    /// script, never cmd's <c>start</c> - but where PowerShell reads the command, one of <see cref="DetachingCmdlets"/> by a
    /// module-qualified name too, each with any of PowerShell's dashes.
    /// </summary>
    /// <param name="command">The command, as this read it.</param>
    public static bool LeavesAProcessBehind(AnalyzedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var powerShell = ReadBy(command) is Reader.PowerShell;

        bool Detaches(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects)
        {
            for (var at = EnvironmentOf(words); at < words.Count && RunsAt(words, at); at++)
            {
                var word = words[at].TrimStart('@');
                var segment = word.TrimEnd('/', '\\').Replace('\\', '/').Split('/')[^1];
                var program = segment.Length > 4 && segment.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? segment[..^4] : segment;
                var cmdlet = powerShell && (word.IndexOfAny(['/', '\\']) < 0 || ModuleQualified().IsMatch(word))
                    && Is(PowerShellReader.CommandName(word), DetachingCmdlets);

                if (Is(word, DetachingCommands) || cmdlet
                    || (Is(program, DetachingPrograms) && (!Is(program, "at", "batch") || AtJob([.. words.Skip(at)], redirects, program: false).Count > 0)))
                {
                    return true;
                }
            }

            return false;
        }

        // PowerShell's -AsJob - by any start of its name from -as, never set :$false - runs the command as a job.
        static bool AsJob(string word)
            => PowerShellReader.Dashed(word) is ['-', not '-', ..] dashed
                && dashed[1..].Split(':', 2) is var spelled
                && spelled[0].Length >= 2 && "asjob".StartsWith(spelled[0].ToLowerInvariant(), StringComparison.Ordinal)
                && (spelled.Length == 1 || !spelled[1].TrimStart('$', '{').StartsWith("false", StringComparison.OrdinalIgnoreCase));

        return command.Background
            || (powerShell && command.Words.Skip(EnvironmentOf(command.Words) + 1).Any(AsJob))
            || Detaches(command.Words, command.Redirects)
            || command.Wrappers.Any(words => Detaches(words, []));
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
    internal static IReadOnlyList<string> Declarations(IReadOnlyList<string> words, IReadOnlyList<string>? quoted = null)
    {
        quoted ??= [];
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
        // a path that names no env: item). The expansion is read where the shell would make it: a name in quotes is a
        // name, not one a variable gave - each word's quoting taken by where it stands, before any word is passed over.
        var placed = rest.Select((word, index) => (Word: word, Quoted: At(quoted, environment + 1 + index))).ToList();
        var naming = Is(program, "export", "declare", "typeset", "local", "readonly")
            ? placed.Where(each => DeclaredName(each.Word) is { } name && Expansion(name, Within(each.Word, each.Quoted, name)) is not null)
            : Is(program, "set", "setenv", "setx")
                ? placed.TakeWhile(each => each.Word != "--").Where(each => !each.Word.StartsWith('-') && !each.Word.StartsWith('/')).Take(1)
                    .Where(each => Expansion(AssignedName(each.Word), Within(each.Word, each.Quoted, AssignedName(each.Word))) is not null)
            : Is(program, ["Set-Item", "si", "New-Item", "ni", "Set-Content", "sc", "Add-Content", "ac", .. ItemMovers])
                ? placed.Where(each => EnvironmentReference().Match(each.Word) is { Success: true } reference
                    && Expansion(reference.Groups["name"].Value, Within(each.Word, each.Quoted, reference.Groups["name"].Value)) is not null)
            : [];

        if (naming.Select(each => each.Word).FirstOrDefault() is { } unread)
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
    private static List<(string Directory, int Word)> PowerShellDirectories(IReadOnlyList<string> words)
    {
        var directories = new List<(string Directory, int Word)>();

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
                    directories.Add((directory, named.Length == 2 ? at : at + 1));
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
            _ when Is(program, [.. SessionPrograms]) && !AsksASessionNothing(program, rest)
                => "runs or types commands in a terminal session that lives apart from the agent, whose place and state this cannot tell",
            _ when Is(program, "crontab") && !rest.Any(word => word.Length > 1 && word[0] == '-' && word[1] != '-' && word.IndexOfAny(['l', 'r']) > 0) || (Is(program, "crontab") && rest.Contains("-e", StringComparer.Ordinal))
                => "installs a table of commands cron runs apart from the agent",
            _ when Is(program, "launchctl") && rest.Any(word => LaunchdStarts.Contains(word, StringComparer.Ordinal))
                => "has launchd start or schedule a job apart from the agent",
            _ when Is(program, "systemctl") && rest.Any(word => SystemdStarts.Contains(word, StringComparer.Ordinal))
                => "has systemd start or schedule a unit apart from the agent",
            _ => null,
        };
    }

    /// <summary>launchctl's verbs that start, schedule or set up what launchd runs apart from the agent.</summary>
    private static readonly string[] LaunchdStarts = ["load", "bootstrap", "submit", "start", "kickstart", "enable", "asuser", "bsexec", "setenv"];

    /// <summary>systemctl's verbs that start a unit, or set one to start, apart from the agent.</summary>
    private static readonly string[] SystemdStarts =
    [
        "start", "restart", "try-restart", "reload-or-restart", "try-reload-or-restart", "reload", "enable", "reenable", "link", "isolate",
        "set-environment", "import-environment", "edit", "preset", "preset-all", "add-wants", "add-requires", "default", "rescue",
        "emergency",
    ];

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

    /// <summary>
    /// An item of PowerShell's <c>env:</c> drive - <c>env:NAME</c>, <c>Env:\NAME</c>, by its provider's name
    /// (<c>Environment::NAME</c>, <c>Microsoft.PowerShell.Core\Environment::NAME</c>) - a parameter's name glued before it too.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:-[A-Za-z]+:)?(?:env:|(?:Microsoft\.PowerShell\.Core\\)?Environment::)[\\/]?(?<name>[A-Za-z_][A-Za-z0-9_]*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex EnvironmentItem();

    /// <summary>
    /// A word that references PowerShell's <c>env:</c> drive, by the drive or its provider's name, its <c>name</c>
    /// whatever follows - a wildcard or an expansion among it, where an item cmdlet names an env: item by one this
    /// cannot see.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:-[A-Za-z]+:)?(?:env:|(?:Microsoft\.PowerShell\.Core\\)?Environment::)[\\/]?(?<name>.+)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
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
        => [.. ForRules(words, program, null).Select(reading => reading.Words)];

    /// <summary>
    /// The readings of <paramref name="quoted"/>: each word with how it stands to the shell, where the command's own
    /// spelling was kept - a word a reading makes reads as wholly open, refused as an expansion rather than quietly
    /// passed.
    /// </summary>
    /// <param name="words">One command's words, its environment first where it has one.</param>
    /// <param name="program">
    /// Whether the words' first is the command's own program, rather than a word of another program's tried as one: a git
    /// or a gh tried so (<c>grep -rn git --include=*.cs</c>) reads an option it does not know as one word, never refused.
    /// </param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>, or null where none was kept.</param>
    /// <exception cref="NotSupportedException">The command's own git or gh is given an option before its subcommand this does not know, which may take the word after it.</exception>
    public static IReadOnlyList<RuleReading> ForRules(IReadOnlyList<string> words, bool program, IReadOnlyList<string>? quoted)
    {
        ArgumentNullException.ThrowIfNull(words);

        quoted ??= [];
        var readings = new List<RuleReading>();

        // Setting git's configuration through the environment is configuring git, whatever runs
        // with it: an export alone, an env, the words before git itself - what the command declares
        // (Declarations), never a word of data that spells one (grep -rn GIT_CONFIG_COUNT= src), nor
        // another program's last word, an assignment before no program.
        var environment = EnvironmentOf(words);

        var declared = program || environment < words.Count ? Declarations(words, quoted) : [];

        if (declared.FirstOrDefault(SetsGitConfiguration) is { } setting)
        {
            readings.Add(new RuleReading(
                ["git", "config", AssignedName(setting), .. words.Skip(environment)],
                [string.Empty, string.Empty, string.Empty, .. quoted.Skip(environment)]) { Configures = true });
        }

        // Setting where gh reads its configuration from is gh config set of the setter's choosing: its editor, pager and
        // browser among it, each a program any gh command runs.
        if (declared.FirstOrDefault(word => IsAssignment(word) && GhConfigurationNames.Contains(AssignedName(word), StringComparer.OrdinalIgnoreCase)) is { } ghSetting)
        {
            readings.Add(new RuleReading(
                ["gh", "config", "set", AssignedName(ghSetting), .. words.Skip(environment)],
                [string.Empty, string.Empty, string.Empty, string.Empty, .. quoted.Skip(environment)]) { Configures = true });
        }

        var (command, commandQuoted, configured) = Plain(words, program, quoted);

        if (configured || ClonesConfigured(command))
        {
            readings.Add(new RuleReading(["git", "config", .. command.Skip(1)], [string.Empty, string.Empty, .. commandQuoted.Skip(1)]) { Configures = true });
        }

        readings.Add(new RuleReading(command, commandQuoted));

        return readings;
    }

    /// <summary>
    /// A command's words as the rules read them (<see cref="ForRules(IReadOnlyList{string}, bool, IReadOnlyList{string}?)"/>) - its environment left out,
    /// git's and gh's own options before their command dropped - and whether git's configuration is
    /// set on its command line. How each word stands to the shell is read with them, a word made by
    /// the reading itself standing as wholly open.
    /// </summary>
    private static (IReadOnlyList<string> Command, IReadOnlyList<string> Quoted, bool Configured) Plain(IReadOnlyList<string> words, bool program, IReadOnlyList<string> quoted)
    {
        var environment = EnvironmentOf(words);

        if (environment == words.Count)
        {
            return (environment == 0 ? words : [.. words.Skip(environment)], environment == 0 ? quoted : [.. quoted.Skip(environment)], false);
        }

        // git-push, as git's own directory holds each of its commands, is git push.
        if (Name(words[environment]) is { Length: > 4 } dashed && dashed.StartsWith("git-", StringComparison.OrdinalIgnoreCase))
        {
            words = [.. words.Take(environment), "git", dashed[4..], .. words.Skip(environment + 1)];
            quoted = [.. quoted.Take(environment), string.Empty, string.Empty, .. quoted.Skip(environment + 1)];
        }

        if (Is(Name(words[environment]), "gh"))
        {
            var (read, howRead) = GhForRules([.. words.Skip(environment)], program, [.. quoted.Skip(environment)]);

            return (read, howRead, false);
        }

        if (!Is(Name(words[environment]), "git"))
        {
            return (environment == 0 ? words : [.. words.Skip(environment)], environment == 0 ? quoted : [.. quoted.Skip(environment)], false);
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

        return at >= words.Count
            ? ([.. words.Skip(environment)], [.. quoted.Skip(environment)], false)
            : (["git", .. words.Skip(at)], [string.Empty, .. quoted.Skip(at)], config);
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
    /// the path's end that this does not know may take the word after it, and is refused. The path's words are made by
    /// this reading, and stand as wholly open; the rest keep the spelling the shell read them with.
    /// </summary>
    private static (IReadOnlyList<string> Words, IReadOnlyList<string> Quoted) GhForRules(IReadOnlyList<string> words, bool program, IReadOnlyList<string> quoted)
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

        return ([.. path, .. words.Skip(at)], [.. path.Select(_ => string.Empty), .. quoted.Skip(at)]);
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
    /// of their own where the words' own is gone into, so the files they name are decided. A script the words' program
    /// runs reads the input the program does: the one around it (<paramref name="inputGiven"/>), or its own redirect's.
    /// </summary>
    private static IEnumerable<AnalyzedCommand> FromWords(IReadOnlyList<string> words, IReadOnlyList<string> quoted, IReadOnlyList<ShellRedirect> redirects, int depth, IReadOnlyList<string> environment, IReadOnlyList<IReadOnlyList<string>>? outerWrappers = null, bool inputGiven = true, int level = 0, string? joined = null)
    {
        // A program another starts elsewhere, and every script one runs, stands a level deeper than the words' own command.
        var inner = level + 1;

        // What a script the program runs reads as its input: what the program is given around it, or by its own redirect.
        var scriptsInput = inputGiven || redirects.Any(redirect => redirect.Descriptor == 0);

        // Each word with how its characters stand to the shell, kept in step: a word whose spelling was not kept - a
        // mask short of it - reads as wholly open, refused as an expansion rather than quietly passed.
        IReadOnlyList<string> masks = quoted;

        if (masks.Count < words.Count)
        {
            masks = [.. masks, .. Enumerable.Repeat(string.Empty, words.Count - masks.Count)];
        }

        // The assignments this level gives, whose program-valued names run their values.
        var given = new List<string>();

        // Each wrapper's words as they stood before it was stripped, outermost first: what it does to the environment.
        var wrapped = new List<IReadOnlyList<string>>(outerWrappers ?? []);

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
                masks = [.. masks.Skip(assigned)];
            }

            if (words.Count == 0)
            {
                // Assignments alone run nothing; they stay a command of their own, with the redirects the
                // shell makes for them, so what they name is still met by every rule on a path, and a
                // program one names is read.
                if (environment.Count > 0 || redirects.Count > 0)
                {
                    yield return new AnalyzedCommand(environment, redirects) { Wrappers = wrapped, InputGiven = inputGiven, Level = level, Joined = joined };
                }

                foreach (var command in ProgramsNamed(given, depth, environment, wrapped, inner))
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
                // The move is the program's own, and the program stands a level deeper with it.
                level = inner;
                inner = level + 1;
                yield return Moved(directory) with { Level = level };
            }

            // A script the command names, gone into: sh -c 'a && b' and the words after its script, a
            // here-string or here-document a shell reads, cmd /c a & b, powershell -Command and a
            // decoded -EncodedCommand, trap's, watch's, script's and flock's command line.
            if (Into(name, words, masks, redirects, program: true, inputGiven: inputGiven) is { } scripts)
            {
                if (redirects.Count > 0)
                {
                    yield return new AnalyzedCommand([], redirects) { Level = level, Joined = joined };
                }

                foreach (var command in GoneInto(scripts, depth, environment, [.. wrapped, words], scriptsInput, inner))
                {
                    yield return command;
                }

                foreach (var command in ProgramsNamed(given, depth, environment, wrapped, inner))
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

                foreach (var command in FromWords(SplitInto(split), [], redirects, depth + 1, environment, [.. wrapped, words], inputGiven, level, joined))
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
                    yield return new AnalyzedCommand([], redirects) { Level = level, Joined = joined };
                }

                // eval's script runs in the shell that reads it, at the level of the words' own command.
                if (words.Count > 1)
                {
                    foreach (var command in GoneInto([string.Join(' ', words.Skip(1))], depth, environment, [.. wrapped, words], scriptsInput, level))
                    {
                        yield return command;
                    }
                }

                yield break;
            }

            // Wrappers, with their own options and values, stripped until a command is left; env's
            // pairs become the command's environment.
            if (Stripped(name, words, masks) is not { } stripped)
            {
                break;
            }

            wrapped.Add(words);
            (words, masks) = (stripped.Words, stripped.Quoted);
        }

        // The program itself: one named by an expansion the shell makes is not read at all, and a pattern's characters
        // name it by a pattern whatever quotes stand around them (ProgramPatterns).
        if (Expansion(words[0], Opened(words[0], masks.Count > 0 ? masks[0] : null, ProgramPatterns)) is { } expansion)
        {
            throw new NotSupportedException($"'{words[0]}' names the program it runs by {expansion}, which this does not read");
        }

        // An alias gives a name a command line run in its place, which a later command names by the alias alone.
        if ((Is(Name(words[0]), "alias") && words.Skip(1).Any(word => word.Contains('=', StringComparison.Ordinal)))
            || (Is(Name(words[0]), "Set-Alias", "New-Alias", "sal", "nal") && words.Count > 1))
        {
            throw new NotSupportedException($"'{string.Join(' ', words)}' gives a name a command run in its place, which this does not read");
        }

        // A program a program that runs its words starts in another directory - stdbuf's env -C, setsid's tar -C - moves the
        // command's paths there, before the command is read.
        for (var at = 1; at < words.Count && RunsAt(words, at); at++)
        {
            if (MayMove(words[at]))
            {
                foreach (var directory in MovesOf([.. words.Skip(at)]))
                {
                    yield return Moved(directory);
                }
            }
        }

        yield return new AnalyzedCommand(
            environment.Count == 0 ? words : [.. environment, .. words],
            redirects,
            environment.Count == 0 ? masks : [.. environment.Select(_ => string.Empty), .. masks])
        {
            Wrappers = wrapped,
            InputGiven = inputGiven,
            Level = level,
            Joined = joined,
        };

        // A program another runs by a variable: the environment's own, and what the command
        // declares (Declarations).
        foreach (var command in ProgramsNamed([.. given, .. Declarations(words, masks)], depth, environment, [.. wrapped, words], inner))
        {
            yield return command;
        }

        // A program git's own settings name, which git runs through the shell: core.pager, core.sshCommand, an
        // alias's '!' line, as the variables above are read.
        if (GitWords(words) is { } git && GitSettingScripts(git, environment) is { Count: > 0 } settings)
        {
            foreach (var command in GoneInto(settings, depth, environment, [.. wrapped, words], level: inner))
            {
                yield return command;
            }
        }

        // The command line gh alias set gives a name, which a later command calls by that name alone.
        if (GhAliasScript(words) is { } aliased)
        {
            foreach (var command in GoneInto([aliased], depth, environment, [.. wrapped, words], level: inner))
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

            foreach (var command in FromWords(executed, [], redirects, depth + 1, environment, [.. wrapped, words], inputGiven, inner))
            {
                yield return command;
            }
        }

        // The command lines the program runs besides being run itself - sed's e, ssh's remote command, wsl's, at's job -
        // each a script of its own.
        foreach (var script in ScriptsItRuns(words, masks, redirects, program: true, inputGiven))
        {
            foreach (var command in GoneInto([script], depth, environment, [.. wrapped, words], scriptsInput, inner))
            {
                yield return command;
            }
        }

        // A shell another program runs with its script among the command's words - find -exec,
        // ssh, watch - is gone into too: what it runs is a command of its own.
        for (var at = 1; at < words.Count; at++)
        {
            var rest = words.Skip(at).ToList();
            var restMasks = masks.Skip(at).ToList();

            // A program a program that runs its words starts runs its command lines as well (setsid ssh host 'x').
            if (RunsAt(words, at))
            {
                foreach (var script in ScriptsItRuns(rest, restMasks, redirects, program: true, inputGiven))
                {
                    foreach (var command in GoneInto([script], depth, environment, [.. wrapped, words], scriptsInput, inner))
                    {
                        yield return command;
                    }
                }
            }

            // A shell a program that runs its words starts is a command's own (setsid sh, wsl -e sh): one that reads its
            // script from its input is refused as the command's own is.
            if (Into(Name(words[at]), rest, restMasks, redirects, program: RunsAt(words, at), inputGiven: inputGiven) is { } innerScripts)
            {
                foreach (var command in GoneInto(innerScripts, depth, environment, [.. wrapped, words], scriptsInput, inner))
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

                foreach (var command in FromWords(SplitInto(innerSplit), [], redirects, depth + 1, environment, [.. wrapped, rest], inputGiven, inner))
                {
                    yield return command;
                }
            }
        }
    }

    /// <summary>
    /// The names whose value is a program another runs through the shell - git's pager, editor,
    /// ssh, askpass and proxy, its external diff, gh's editor, pager and browser, the pager,
    /// editor and browser any program runs, and the command bash runs before each prompt: set for
    /// a command, each runs a command of the setter's choosing, read as the script it is.
    /// </summary>
    private static readonly string[] ProgramValued =
    [
        "GIT_PAGER", "GIT_EDITOR", "GIT_SEQUENCE_EDITOR", "GIT_SSH", "GIT_SSH_COMMAND", "GIT_ASKPASS", "GIT_PROXY_COMMAND",
        "GIT_EXTERNAL_DIFF", "GH_EDITOR", "GH_PAGER", "GH_BROWSER", "PAGER", "EDITOR", "VISUAL", "BROWSER", "SSH_ASKPASS", "MANPAGER",
        "PROMPT_COMMAND",
    ];

    /// <summary>
    /// The shell's prompts, which an interactive shell expands as it prints each - its substitutions run, as in a double
    /// quote's text: set for a command, each is read as that text.
    /// </summary>
    private static readonly string[] PromptValued = ["PS0", "PS1", "PS2", "PS4"];

    /// <summary>
    /// The commands the program-valued names among <paramref name="assignments"/> run, each value read as a script, and
    /// the substitutions each prompt's (<see cref="PromptValued"/>) runs - and what git's configuration through the
    /// environment sets, which git may run so too: each <c>GIT_CONFIG_VALUE_n</c> of a key git runs as a command line
    /// (<see cref="RunsProgram"/>) or of none named, and each such setting <c>GIT_CONFIG_PARAMETERS</c> holds.
    /// </summary>
    private static IEnumerable<AnalyzedCommand> ProgramsNamed(IReadOnlyList<string> assignments, int depth, IReadOnlyList<string> environment, IReadOnlyList<IReadOnlyList<string>>? wrappers, int level)
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
            else if (PromptValued.Contains(pair[0], StringComparer.Ordinal))
            {
                // A prompt's text in a double quote, its backslashes and quotes its own: what it substitutes runs.
                values.Add($": \"{pair[1].Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"");
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

        return values.Count == 0 ? [] : GoneInto(values, depth, environment, wrappers, level: level);
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

        // git config's own setting of such a key runs later, wherever git reads it - an alias a later command calls by a
        // name no rule reads (git config alias.cf 'credential fill', then git cf) - so it is read where it is set. A key
        // the shell expands may be any, an alias among them: its value is read both ways.
        if (at < words.Count && words[at].Equals("config", StringComparison.OrdinalIgnoreCase) && ConfigSetting(words, at + 1) is (var configKey, var setValue))
        {
            if (RunsProgram(configKey))
            {
                scripts.Add(SettingScript(configKey, setValue));
            }
            else if (Expansion(configKey) is not null)
            {
                scripts.AddRange([SettingScript("alias.x", setValue), SettingScript("core.pager", setValue)]);
            }
        }

        return scripts;
    }

    /// <summary>
    /// The key and the value <c>git config</c> sets with its words from <paramref name="from"/> on: its first two words
    /// that are no option and no option's value, after a <c>set</c> where it names one - none where it reads, unsets,
    /// renames, removes, lists or edits rather than setting, or names no value.
    /// </summary>
    /// <param name="words">A git command's words, its program first.</param>
    /// <param name="from">Where the words after <c>config</c> begin.</param>
    private static (string Key, string Value)? ConfigSetting(IReadOnlyList<string> words, int from)
    {
        var (options, positional) = ConfigWords(words, from);

        if (options.Any(option => GitConfigReads.Contains(option, StringComparer.Ordinal)))
        {
            return null;
        }

        // git config's own verbs: only set sets.
        if (positional.Count > 0 && GitConfigVerbs.Contains(positional[0], StringComparer.Ordinal))
        {
            if (positional[0] != "set")
            {
                return null;
            }

            positional.RemoveAt(0);
        }

        return positional.Count >= 2 ? (positional[0], positional[1]) : null;
    }

    /// <summary>
    /// The words of <c>git config</c> from <paramref name="from"/> on: each option's name - its word to an <c>=</c> - and
    /// every word that is no option and no option's value, in order; every word after a <c>--</c> one of them.
    /// </summary>
    /// <param name="words">A git command's words, its program first.</param>
    /// <param name="from">Where the words after <c>config</c> begin.</param>
    private static (List<string> Options, List<string> Positional) ConfigWords(IReadOnlyList<string> words, int from)
    {
        var (options, positional) = (new List<string>(), new List<string>());

        for (var at = from; at < words.Count; at++)
        {
            var word = words[at];

            if (word == "--")
            {
                positional.AddRange(words.Skip(at + 1));
                break;
            }

            if (word.Length > 1 && word[0] == '-')
            {
                var name = word.Split('=', 2)[0];

                options.Add(name);
                at += GitConfigValued.Contains(name, StringComparer.Ordinal) && !word.Contains('=', StringComparison.Ordinal) ? 1 : 0;
                continue;
            }

            positional.Add(word);
        }

        return (options, positional);
    }

    /// <summary>git config's options that take their value as the next word.</summary>
    private static readonly string[] GitConfigValued = ["-f", "--file", "--blob", "-t", "--type", "--default", "--comment", "--value", "--url"];

    /// <summary>
    /// git config's options that only read - a value got, every value listed - each an action git refuses beside any
    /// other: printed by <c>help side-tasks</c>, open while a run works over the tree (<see cref="GitReadsOnly"/>).
    /// </summary>
    public static IReadOnlyList<string> GitConfigGets { get; } = ["--get", "--get-all", "--get-regexp", "--get-urlmatch", "--get-color", "--get-colorbool", "-l", "--list"];

    /// <summary>git config's options that unset, rename, remove or edit.</summary>
    private static readonly string[] GitConfigUnsets = ["--unset", "--unset-all", "--rename-section", "--remove-section", "-e", "--edit"];

    /// <summary>git config's options that read, unset, rename, remove, list or edit rather than setting a value.</summary>
    private static readonly string[] GitConfigReads = [.. GitConfigGets, .. GitConfigUnsets];

    /// <summary>git config's options that add or replace a value beside the ones a key holds.</summary>
    private static readonly string[] GitConfigAdds = ["--add", "--replace-all"];

    /// <summary>
    /// git config's options that choose its file, the type of its values and what it prints, none of them an action: with
    /// <see cref="GitConfigValued"/>, <see cref="GitConfigReads"/> and <see cref="GitConfigAdds"/>, every option of git
    /// config's this knows, each by its whole name.
    /// </summary>
    private static readonly string[] GitConfigShaping =
    [
        "--global", "--system", "--local", "--worktree", "--bool", "--int", "--bool-or-int", "--bool-or-str", "--path", "--expiry-date",
        "--no-type", "-z", "--null", "--name-only", "--includes", "--no-includes", "--show-origin", "--show-scope", "--fixed-value", "--all",
        "--regexp",
    ];

    /// <summary>git config's verbs, of which <c>set</c> alone sets.</summary>
    private static readonly string[] GitConfigVerbs = ["list", "get", "set", "unset", "rename-section", "remove-section", "edit"];

    /// <summary>git config's verbs that only read: printed by <c>help side-tasks</c>.</summary>
    public static IReadOnlyList<string> GitConfigReadVerbs { get; } = ["list", "get"];

    /// <summary>
    /// The command line <c>gh alias set</c> gives a name, read where it is given, for a later command calls it by a name
    /// no rule reads (<c>gh alias set tk 'auth token'</c>, then <c>gh tk</c>): its expansion as the gh command it is, or,
    /// marked <c>!</c> or given <c>--shell</c>, as the shell script gh runs. Null where the words are no <c>gh alias set</c>.
    /// </summary>
    /// <param name="words">One command's words, its program first.</param>
    /// <exception cref="NotSupportedException">
    /// The words give names commands this does not read: an expansion read from the input, or <c>gh alias import</c>'s
    /// file of them.
    /// </exception>
    private static string? GhAliasScript(IReadOnlyList<string> words)
    {
        if (words.Count < 3 || !Is(Name(words[0]), "gh") || !words[1].Equals("alias", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (words[2].Equals("import", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"'{string.Join(' ', words)}' gives names commands from a file this does not read");
        }

        if (!words[2].Equals("set", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var shell = words.Skip(3).Any(word => word is "-s" or "--shell");
        var positional = words.Skip(3).Where(word => !word.StartsWith('-') || word == "-").ToList();

        if (positional.Count < 2)
        {
            return null;
        }

        if (positional[1] == "-")
        {
            throw new NotSupportedException($"'{string.Join(' ', words)}' gives a name a command read from its input, which this does not read");
        }

        return shell || positional[1].StartsWith('!') ? positional[1].TrimStart('!') : "gh " + positional[1];
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

    /// <summary>
    /// The commands each script runs, one level further in: refused past <see cref="Depth"/>. Each reads the input
    /// <paramref name="inputGiven"/> says the script is given - one it is given where nothing says it is not - and stands
    /// at <paramref name="level"/> and the levels its own subshells add (<see cref="AnalyzedCommand.Level"/>).
    /// </summary>
    private static IEnumerable<AnalyzedCommand> GoneInto(IReadOnlyList<string> scripts, int depth, IReadOnlyList<string> environment, IReadOnlyList<IReadOnlyList<string>>? wrappers, bool inputGiven = true, int level = 1)
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
            foreach (var command in FromScript(script, depth + 1, environment, wrappers, inputGiven, level))
            {
                yield return command;
            }
        }
    }

    /// <summary>The refusal of a script nested past <see cref="Depth"/> levels: a root action inside it would not be seen.</summary>
    private static NotSupportedException Nested(IReadOnlyList<string> words)
        => new($"'{string.Join(' ', words)}' is a script nested past {Depth} levels of going into, which this does not read");

    /// <summary>
    /// What the shell expands in <paramref name="word"/>, as a refusal names it - a substitution, a variable, a
    /// pattern or a brace - or <see langword="null"/> where it expands nothing: each character asked where it stands
    /// to the shell by <paramref name="quoted"/> (<see cref="WordQuoting"/>), so a quoted character is never read as
    /// an expansion the shell would not make. A word whose spelling was not kept - <see langword="null"/>, or a mask
    /// short of the word - reads as wholly open: refused as an expansion rather than quietly passed. A cmd percent is
    /// live inside double quotes as out of them, for the shell never expands one and cmd reads the word past every
    /// quote the shell read; what cmd reads again of a batch argument is <see cref="Processes.BatchFiles"/>' own rule.
    /// A tilde the shell takes from a variable's value at a word's start - <c>~+</c> PWD's, <c>~-</c> OLDPWD's, and
    /// <c>~N</c>, <c>~+N</c> and <c>~-N</c> the directory stack's, every character of it unquoted and nothing but a
    /// separator after it - is a variable, as <c>$PWD</c> is: the string may set the variable to any word at all. A home
    /// (<c>~</c>, <c>~name</c>) is none, read where a path is.
    /// </summary>
    /// <param name="word">The word, its quotes read as the shell reads them.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    internal static string? Expansion(string word, string? quoted = null)
    {
        // A word another program fills in as it runs its command stands as an expansion, whatever the quoting around
        // it says: what fills it in is not the shell, and may give any word at all.
        if (word.Contains(InputWord, StringComparison.Ordinal))
        {
            return "a substitution";
        }

        if (VariableTilde(word, quoted) > 0)
        {
            return "a variable";
        }

        var classes = ClassesOf(word, quoted);

        for (var at = 0; at < word.Length; at++)
        {
            // A dollar that still expands - in the open or inside double quotes - runs what follows it as a
            // substitution; the process substitutions run only in the open, for inside double quotes they are text.
            if (word[at] == '$' && classes[at] != WordQuoting.Literal && at + 1 < word.Length && word[at + 1] == '(')
            {
                return "a substitution";
            }

            if (word[at] is '<' or '>' && classes[at] == WordQuoting.Open
                && at + 1 < word.Length && word[at + 1] == '(' && classes[at + 1] == WordQuoting.Open)
            {
                return "a substitution";
            }

            // A dollar expands wherever it stands but inside single quotes or an escape; a cmd percent there too, for
            // cmd reads the word past every quote the shell read.
            if ((word[at] == '$' || word[at] == '%') && classes[at] != WordQuoting.Literal)
            {
                return "a variable";
            }

            // A glob and a bracket class expand only in the open: inside quotes of any kind the shell hands them on.
            if (word[at] is '*' or '?' && classes[at] == WordQuoting.Open)
            {
                return "a pattern";
            }

            if (word[at] == '[' && classes[at] == WordQuoting.Open
                && word.IndexOf(']', Math.Min(at + 2, word.Length)) is var close && close > at + 1 && classes[close] == WordQuoting.Open)
            {
                return "a pattern";
            }

            // A brace expands only in the open, and only with a comma or a range in it that is open with it.
            if (word[at] == '{' && classes[at] == WordQuoting.Open)
            {
                for (var rest = at + 1; rest < word.Length; rest++)
                {
                    if (word[rest] == ',' && classes[rest] == WordQuoting.Open)
                    {
                        return "a brace";
                    }

                    if (word[rest] == '.' && rest + 1 < word.Length && word[rest + 1] == '.' && classes[rest] == WordQuoting.Open && classes[rest + 1] == WordQuoting.Open)
                    {
                        return "a brace";
                    }
                }
            }
        }

        return DelayedVariable().IsMatch(BlankLiterals(word, classes)) ? "a variable" : null;
    }

    /// <summary><paramref name="word"/> with its literal characters blanked, so a pattern reads only what still expands.</summary>
    private static string BlankLiterals(string word, string classes)
        => string.Create(word.Length, (word, classes), (span, state) =>
        {
            for (var at = 0; at < span.Length; at++)
            {
                span[at] = state.classes[at] == WordQuoting.Literal ? '\u0001' : state.word[at];
            }
        });

    /// <summary>
    /// How each of <paramref name="word"/>'s characters stands to the shell: <paramref name="quoted"/>'s own class per
    /// character, and <see cref="WordQuoting.Open"/> wherever the word's spelling was not kept - a mask short of the
    /// word, or none at all - so a word read without its spelling is refused as an expansion exactly as it was before
    /// any of this was read.
    /// </summary>
    private static string ClassesOf(string word, string? quoted)
    {
        if (quoted is null || quoted.Length < word.Length)
        {
            return new(string.Empty.PadLeft(word.Length, WordQuoting.Open));
        }

        return quoted;
    }

    /// <summary>
    /// How <paramref name="word"/>'s characters stand to the shell (<see cref="ClassesOf"/>), each of
    /// <paramref name="characters"/> among them standing open whatever quotes were around it: where what reads the word
    /// expands those characters past every quote the shell read.
    /// </summary>
    private static string Opened(string word, string? quoted, string characters)
        => string.Create(word.Length, (word, classes: ClassesOf(word, quoted), characters), (span, state) =>
        {
            for (var at = 0; at < span.Length; at++)
            {
                span[at] = state.characters.Contains(state.word[at], StringComparison.Ordinal) ? WordQuoting.Open : state.classes[at];
            }
        });

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
        else if (Is(program, "tar", "gtar", "bsdtar"))
        {
            // The programs tar runs through the shell: a filter for what it extracts, a compressor, a checkpoint's action,
            // a remote shell and the scripts it runs between volumes.
            foreach (var option in Getopt(TarStyled(words).Words, TarOptions).Options.Where(option => option.Value is { Length: > 0 }))
            {
                if (option.Option is "to-command" or "use-compress-program" or "I" or "rsh-command" or "info-script" or "new-volume-script" or "F")
                {
                    yield return ["sh", "-c", option.Value!];
                }
                else if (option.Option == "checkpoint-action" && option.Value!.StartsWith("exec=", StringComparison.Ordinal))
                {
                    yield return ["sh", "-c", option.Value!["exec=".Length..]];
                }
            }
        }
        else if (Is(program, "rsync"))
        {
            // The remote shell rsync connects by, its words split as rsync splits them, the host and rsync's own server after.
            foreach (var option in Getopt(words, RsyncOptions).Options.Where(option => option.Option is "e" or "rsh" && option.Value is { Length: > 0 }))
            {
                yield return [.. SplitWords(option.Value!), InputWord, "rsync", "--server"];
            }
        }
        else if (Is(program, "install"))
        {
            // The program install strips what it installs with, the file it installs after it.
            foreach (var option in Getopt(words, InstallOptions).Options.Where(option => option.Option == "strip-program" && option.Value is { Length: > 0 }))
            {
                yield return [option.Value!, InputWord];
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
        var (command, _, _) = Plain(words, program: false, []);

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
                if (GitOptionValue(GetoptLong(command, at, subcommand), at, spec.Long, spec.Short, spec.GluedOnly, GitOptionsOfTheirOwn(subcommand)) is { } found
                    && (!spec.PathOnly || NamesAPath(found.Value)))
                {
                    // The value's word is read again as an option of its own: where git would not take it as this one's
                    // value - Getopt::Long passing over a prefix it finds ambiguous - it is the option it names.
                    yield return spec.Appends ? ["sh", "-c", found.Value, InputWord] : ["sh", "-c", found.Value];
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
    /// A git subcommand's options whose names begin a name of <see cref="GitExecutedOptions"/>: each an option of its own,
    /// which git reads by its exact name before any abbreviation - send-email's <c>--to</c> and <c>--cc</c>, never its
    /// <c>--to-cmd</c> and <c>--cc-cmd</c>.
    /// </summary>
    private static IReadOnlyList<string> GitOptionsOfTheirOwn(string subcommand)
        => subcommand == "send-email" ? ["to", "cc"] : [];

    /// <summary>
    /// The words as git's option reader would have them where the subcommand reads its options by Perl's Getopt::Long, as
    /// send-email does: a long option written after <c>--</c>, <c>-</c> or <c>+</c> - Getopt::Long's prefixes - and in any
    /// case (<c>+TO-CMD=x</c>) is the <c>--to-cmd=x</c> it is, its value as written - the word at <paramref name="at"/> so
    /// respelled, one prefix taken off as Getopt::Long takes it; every other subcommand's words as they stand.
    /// </summary>
    private static IReadOnlyList<string> GetoptLong(IReadOnlyList<string> command, int at, string subcommand)
    {
        var word = command[at];

        if (subcommand != "send-email" || word.Length < 2 || word[0] is not ('-' or '+') || word == "--")
        {
            return command;
        }

        var named = word[(word.StartsWith("--", StringComparison.Ordinal) ? 2 : 1)..].Split('=', 2);
        var respelled = "--" + named[0].ToLowerInvariant() + (named.Length == 2 ? "=" + named[1] : string.Empty);

        return [.. command.Take(at), respelled, .. command.Skip(at + 1)];
    }

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
    /// prefix or a letter may be another option's too - a value misread is a command refused, never one let through - but
    /// for an option of the subcommand's own that a prefix would reach (<paramref name="theirOwn"/>), which git reads by its
    /// exact name first. Null where the word names none of <paramref name="longNames"/> or <paramref name="shortLetters"/>,
    /// or names one with no value to read.
    /// </summary>
    private static (string Value, int Next)? GitOptionValue(IReadOnlyList<string> words, int at, IReadOnlyList<string> longNames, string shortLetters, bool gluedOnly, IReadOnlyList<string>? theirOwn = null)
    {
        var word = words[at];

        if (word.StartsWith("--", StringComparison.Ordinal) && word.Length > 2)
        {
            var named = word[2..].Split('=', 2);

            if ((theirOwn ?? []).Contains(named[0], StringComparer.Ordinal) || !longNames.Any(name => name.StartsWith(named[0], StringComparison.Ordinal)))
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
    /// A tilde the shell takes from a variable at a word's start, before a separator or the word's end: <c>~+</c> and
    /// <c>~-</c>, and the directory stack's <c>~N</c>, <c>~+N</c> and <c>~-N</c>.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^~(?:[+-]?[0-9]+|[+-])(?=$|[/\\])")]
    private static partial System.Text.RegularExpressions.Regex TildeVariable();

    /// <summary>
    /// How long the tilde at <paramref name="word"/>'s start is that the shell takes from a variable's value
    /// (<see cref="TildeVariable"/>), every character of it unquoted by <paramref name="quoted"/>; 0 where the word begins
    /// with none: the one reading of where such a tilde ends, for <see cref="Expansion"/> and for whatever reads past it.
    /// </summary>
    /// <param name="word">The word, its quotes read as the shell reads them.</param>
    /// <param name="quoted">How each of its characters stands to the shell, or null where its spelling was not kept.</param>
    internal static int VariableTilde(string word, string? quoted)
        => TildeVariable().Match(word) is { Success: true } tilde && ClassesOf(word, quoted)[..tilde.Length].All(character => character == WordQuoting.Open)
            ? tilde.Length
            : 0;

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
    /// GNU env's options: <c>-u</c> and <c>--unset</c> take a name away, <c>-C</c> and <c>--chdir</c> move where its
    /// command starts, <c>-a</c> and <c>--argv0</c> name it, <c>-S</c> and <c>--split-string</c> give its command line;
    /// the signal ones take their signals after an <c>=</c> alone.
    /// </summary>
    private static readonly OptionTable EnvOptions = new(
        "aCSu",
        string.Empty,
        ["argv0", "chdir", "split-string", "unset"],
        ["block-signal", "default-signal", "ignore-signal"],
        ["debug", "help", "ignore-environment", "list-signal-handling", "null", "version"]);

    /// <summary>The short options of env's that take no value: <c>-i</c> starts its command with no environment.</summary>
    private const string EnvFlags = "i0v";

    /// <summary>
    /// env's own words before its command, as GNU env reads them (<see cref="EnvRead"/>): its NAME=value pairs; its options
    /// (<see cref="EnvOptions"/>), a long one named by its whole name or by any prefix that begins it alone, as getopt_long
    /// names it (<see cref="LongOption"/>), each that takes a value taking it glued or the next word's, in a short cluster
    /// too; and <c>-S</c>'s command line, the split string. Its help and its version run nothing, and nor does an option
    /// missing its value, or given one it takes none of, for env refuses it.
    /// </summary>
    /// <exception cref="NotSupportedException">env is given an option it does not name alone - none of its own, or a prefix of more than one - which env refuses where it is GNU's and another env may take as an option of its own.</exception>
    private static EnvRead EnvWords(IReadOnlyList<string> words)
    {
        var pairs = new List<string>();
        var directories = new List<(string Directory, int Word)>();
        var unsets = new List<(string Name, int Word)>();
        var cleared = false;

        EnvRead Read(int at, string? split) => new(pairs, at, split, directories, unsets, cleared);

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
                return Read(at + 1, null);
            }

            if (option == "-")
            {
                cleared = true;
                continue;
            }

            if (option.Length < 2 || option[0] != '-')
            {
                return Read(at, null);
            }

            // The option's name - a long one's as getopt_long names it - with its value glued to it, or null.
            var equals = option.IndexOf('=', StringComparison.Ordinal);
            var (name, glued) = option[1] == '-'
                ? (LongOption(equals < 0 ? option[2..] : option[2..equals], EnvOptions).Name, equals < 0 ? null : option[(equals + 1)..])
                : (option[1].ToString(), option.Length > 2 ? option[2..] : null);

            if (option[1] != '-' && EnvFlags.Contains(name, StringComparison.Ordinal))
            {
                // A cluster of flags, a valued letter ending it with its value glued: each read in turn.
                for (var letter = 1; letter < option.Length; letter++)
                {
                    if (EnvFlags.Contains(option[letter], StringComparison.Ordinal))
                    {
                        cleared |= option[letter] == 'i';
                        continue;
                    }

                    (name, glued) = (option[letter].ToString(), letter + 1 < option.Length ? option[(letter + 1)..] : null);
                    break;
                }

                if (EnvFlags.Contains(name, StringComparison.Ordinal))
                {
                    continue;
                }
            }

            var valued = name.Length == 1 ? EnvOptions.ShortValued.Contains(name, StringComparison.Ordinal) : EnvOptions.LongValued.Contains(name, StringComparer.Ordinal);

            if (!valued && !EnvOptions.LongOptional.Contains(name, StringComparer.Ordinal) && !EnvOptions.LongFlags.Contains(name, StringComparer.Ordinal))
            {
                throw new NotSupportedException($"'{option}' names no option of env's alone - none of them, or the start of more than one - which GNU env refuses and another env may take as one of its own, which this does not read");
            }

            if (name is "help" or "version" || (!valued && glued is not null && option[1] == '-' && EnvOptions.LongFlags.Contains(name, StringComparer.Ordinal)))
            {
                // Its help, its version and a flag given a value run nothing.
                return Read(words.Count, null);
            }

            if (!valued)
            {
                cleared |= name == "ignore-environment";
                continue;
            }

            // A value glued to its option, or the next word; none runs nothing, for env refuses its option.
            var (value, word) = glued is not null ? (glued, at) : at + 1 < words.Count ? (words[at + 1], at + 1) : ((string?)null, at);

            if (value is null)
            {
                return Read(words.Count, null);
            }

            if (name is "S" or "split-string")
            {
                return Read(word + 1, value);
            }

            if (name is "C" or "chdir")
            {
                directories.Add((value, word));
            }
            else if (name is "u" or "unset")
            {
                unsets.Add((value, word));
            }

            at = word;
        }

        return Read(words.Count, null);
    }

    /// <summary>
    /// env's own words, as <see cref="EnvWords"/> reads them.
    /// </summary>
    /// <param name="Pairs">Its NAME=value pairs.</param>
    /// <param name="At">Where its command begins: after the split string where one is given, or past every word where a help or a version runs nothing.</param>
    /// <param name="Split">The split string <c>-S</c> gives, or null.</param>
    /// <param name="Directories">The directories <c>-C</c> and <c>--chdir</c> start its command in, each with the word that names it, in the order given.</param>
    /// <param name="Unsets">The names <c>-u</c> and <c>--unset</c> take away from its command's environment, each with the word that names it.</param>
    /// <param name="Cleared">Whether it starts its command with no environment of its own: <c>-i</c>, <c>-</c>, <c>--ignore-environment</c>.</param>
    private sealed record EnvRead(List<string> Pairs, int At, string? Split, List<(string Directory, int Word)> Directories, List<(string Name, int Word)> Unsets, bool Cleared);

    /// <summary>
    /// What env, as the program <paramref name="words"/> begin with, does to the environment of the command it starts: the
    /// names its <c>-u</c> and <c>--unset</c> take away, each with the word that names it, and whether it clears the
    /// environment whole (<c>-i</c>, <c>-</c>, <c>--ignore-environment</c>); nothing where the program is no env.
    /// </summary>
    /// <param name="words">The words, env's name first.</param>
    internal static (IReadOnlyList<(string Name, int Word)> Unsets, bool Cleared) EnvironmentTakenAway(IReadOnlyList<string> words)
        => words.Count > 0 && Is(Name(words[0]), "env") && EnvWords(words) is var read ? (read.Unsets, read.Cleared) : ([], false);

    /// <summary>
    /// The directories the program <paramref name="words"/> begin with starts what it runs in, or works in - env's
    /// <c>-C</c> and <c>--chdir</c>, PowerShell's <c>-WorkingDirectory</c>, tar's, make's, ninja's and git's <c>-C</c>,
    /// patch's <c>-d</c> (<see cref="OwnDirectories"/>) - each a move the policy follows as it follows a <c>cd</c>
    /// (<see cref="Moved"/>); none where the program moves nothing.
    /// </summary>
    private static IReadOnlyList<string> MovesOf(IReadOnlyList<string> words)
        => [.. MoveWordsOf(words).Select(move => move.Directory)];

    /// <summary>The moves of <see cref="MovesOf"/>, each with the word of <paramref name="words"/> that names its directory.</summary>
    private static IReadOnlyList<(string Directory, int Word)> MoveWordsOf(IReadOnlyList<string> words)
        => words.Count == 0 ? []
            : Is(Name(words[0]), "env") ? EnvWords(words).Directories
            : Is(Name(words[0]), "powershell", "pwsh") ? PowerShellDirectories(words)
            : OwnDirectories(words);

    /// <summary>
    /// The words of <paramref name="command"/> that name a directory its program starts what it runs in, or works in
    /// (<see cref="MovesOf"/>), each followed as a <c>cd</c> before the command (<see cref="Moved"/>), by where each stands:
    /// a word that moves reads nothing where it goes. Read of the command's own program, and of a program a program that
    /// runs its words starts (<c>setsid tar -C x</c>, <c>stdbuf env -C x</c>) - among any other program's words such a word
    /// is that program's data (<c>grep -r x env -C dir</c>), which a move of its own never excuses.
    /// </summary>
    /// <param name="command">A command's words, its environment before them.</param>
    public static IReadOnlySet<int> MoveWords(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var moving = new HashSet<int>();
        var environment = EnvironmentOf(command);

        for (var at = environment; at < command.Count && RunsAt(command, at); at++)
        {
            if (MayMove(command[at]))
            {
                moving.UnionWith(MoveWordsOf([.. command.Skip(at)]).Select(move => at + move.Word));
            }
        }

        return moving;
    }

    /// <summary>
    /// Whether the word of <paramref name="command"/> at <paramref name="at"/> may be a program that runs: the command's own
    /// program, past its environment, or any word after a program that runs the words after it (<see cref="CommandRunners"/>),
    /// whose options and their values this does not read. A word of any other program's is that program's data.
    /// </summary>
    /// <param name="command">A command's words, its environment before them.</param>
    /// <param name="at">Which word.</param>
    public static bool RunsAt(IReadOnlyList<string> command, int at)
    {
        ArgumentNullException.ThrowIfNull(command);

        var environment = EnvironmentOf(command);

        return at == environment || (at > environment && environment < command.Count && RunsItsWords(command[environment]));
    }

    /// <summary>
    /// Whether <paramref name="word"/> names a program that may start what it runs in another directory, or work in one
    /// (<see cref="MovesOf"/>): asked before the words after it are copied for that question, so a long command is not copied
    /// at each of its words.
    /// </summary>
    private static bool MayMove(string word)
        => Is(Name(word), "env", "powershell", "pwsh", "tar", "gtar", "bsdtar", "make", "gmake", "bmake", "ninja", "git", "patch");

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
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>.</param>
    /// <param name="redirects">The redirects the shell makes for the command the words stand in.</param>
    /// <param name="program">Whether the words are a command's own, its program first, rather than another program's arguments.</param>
    /// <param name="scriptFile">Told each word the program reads as its script file, where one does (<see cref="ScriptFilesOf"/>).</param>
    /// <param name="inputGiven">Whether the command's input is given by what stands around it (<see cref="AnalyzedCommand.InputGiven"/>).</param>
    private static IReadOnlyList<string>? Into(string name, IReadOnlyList<string> words, IReadOnlyList<string> quoted, IReadOnlyList<ShellRedirect> redirects, bool program, Action<string>? scriptFile = null, bool inputGiven = true)
    {
        if (Is(name, "fish"))
        {
            return FishScripts(name, words, redirects, program, scriptFile, inputGiven);
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
                    return at + 1 < words.Count ? ScriptFile(words[at + 1], name, redirects, program, quoted: At(quoted, at + 1), seen: scriptFile, inputGiven: inputGiven) : Input(name, redirects, program, inputGiven: inputGiven, seen: scriptFile);
                }

                if (!(option.StartsWith('-') || option.StartsWith('+')))
                {
                    return ScriptFile(option, name, redirects, program, quoted: At(quoted, at), seen: scriptFile, inputGiven: inputGiven);
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

            return Input(name, redirects, program, inputGiven: inputGiven, seen: scriptFile);
        }

        if (Is(name, "source", "."))
        {
            return words.Count > 1 ? ScriptFile(words[1], name, redirects, program, quoted: At(quoted, 1), seen: scriptFile, inputGiven: inputGiven) : null;
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

            return Input(name, redirects, program, CmdAsPosix, inputGiven: inputGiven, seen: scriptFile);
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
                    return Input(name, redirects, program, PowerShellAsPosix, inputGiven: inputGiven, seen: scriptFile);
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
                        : Input(name, redirects, program, PowerShellAsPosix, inputGiven: inputGiven, seen: scriptFile);
                }

                if ("file".StartsWith(parameter, StringComparison.Ordinal))
                {
                    return at + 1 >= words.Count ? null
                        : words[at + 1] == "-" ? Input(name, redirects, program, PowerShellAsPosix, inputGiven: inputGiven, seen: scriptFile)
                        : ScriptFile(words[at + 1], name, redirects, program, PowerShellAsPosix, seen: scriptFile, inputGiven: inputGiven);
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

            return file is not null ? ScriptFile(file, name, redirects, program, PowerShellAsPosix, seen: scriptFile, inputGiven: inputGiven)
                : Input(name, redirects, program, PowerShellAsPosix, inputGiven: inputGiven, seen: scriptFile);
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

        // -x runs the words as they are, each met by the rules on its own program.
        if (Is(name, "watch"))
        {
            return WatchCommandAt(words) is { Exec: false, At: var at } && at < words.Count ? [string.Join(' ', words.Skip(at))] : null;
        }

        return null;
    }

    /// <summary>
    /// A script file a shell is handed: unseen, as help says, where a word names it; the input itself
    /// where it names the input (<c>/dev/stdin</c>, <c>/dev/fd/0</c>), read as <see cref="Input"/>
    /// reads it - and refused where an expansion names it (<c>bash &lt;(curl ...)</c>,
    /// <c>source $F</c>) or another open descriptor does (<c>/dev/fd/3</c>), where the words are a
    /// command's own, for what it runs is what another command printed. The expansion is read where
    /// the shell would make it: a file named in quotes is a file of that name, not one a variable
    /// gave. <paramref name="seen"/> is told the file first, and the file the input is where it names the input.
    /// </summary>
    private static IReadOnlyList<string>? ScriptFile(string file, string name, IReadOnlyList<ShellRedirect> redirects, bool program, Func<string, string>? spelled = null, string? quoted = null, Action<string>? seen = null, bool inputGiven = true)
    {
        seen?.Invoke(file);

        return Folded(file) is "/dev/stdin" or "/dev/fd/0" or "/proc/self/fd/0" ? Input(name, redirects, program, spelled, inputGiven, seen)
            : program && Unread(file, quoted) is { } unread ? throw new NotSupportedException($"'{file}' names the script it runs by {unread}, which this does not read")
            : null;
    }

    /// <summary>
    /// The script files the program <paramref name="words"/> start with reads and runs: a shell's, <c>source</c>'s and
    /// PowerShell's script file, as <see cref="Into"/> reads their words - the file its input is, where it reads its
    /// script from its input (<c>bash &lt; x.sh</c>, <c>sh -s &lt; x.sh</c>, <c>source /dev/stdin &lt; x.sh</c>) - and sed's
    /// <c>-f</c> files - never a word a <c>-c</c> runs as a command line, nor the arguments after a script. None where it
    /// reads none.
    /// </summary>
    /// <param name="words">The program and its words.</param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>.</param>
    /// <param name="redirects">The redirects the shell makes for the command the words stand in.</param>
    internal static IReadOnlyList<string> ScriptFilesOf(IReadOnlyList<string> words, IReadOnlyList<string> quoted, IReadOnlyList<ShellRedirect> redirects)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var name = Name(words[0]);
        var files = new List<string>();

        try
        {
            if (Is(name, "sed", "gsed"))
            {
                return SedOf(words, quoted)?.Files ?? [];
            }

            _ = Into(name, words, quoted, redirects, program: false, files.Add);
        }
        catch (NotSupportedException)
        {
            // Words this cannot read are refused where the command is flattened, before a file of theirs is asked.
        }

        return files;
    }

    /// <summary>
    /// What names a script file by what this does not read - an expansion, or an open descriptor's own name however its
    /// path is spelled (<c>/dev//stdin</c>, <c>/dev/../dev/fd/3</c>), or relative to where the command may stand
    /// (<c>stdin</c>, <c>fd/3</c>) - or null.
    /// </summary>
    private static string? Unread(string file, string? quoted = null)
        => Expansion(file, quoted) ?? (OpenDescriptor().IsMatch(Folded(file)) ? "an open descriptor" : null);

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
    /// script file, unseen as any is. A program given no input of its own reads the one around it
    /// (<paramref name="inputGiven"/>): what an earlier command printed, or the text the tool runtime
    /// writes to it, refused where the words are a command's own; or, where nothing gives it one -
    /// the agent's own command's input - no script at all, null. <paramref name="seen"/> is told the
    /// file its input is, as <see cref="ScriptFile"/> tells it a file a word names.
    /// </summary>
    private static IReadOnlyList<string>? Input(string name, IReadOnlyList<ShellRedirect> redirects, bool program, Func<string, string>? spelled = null, bool inputGiven = true, Action<string>? seen = null)
    {
        // The shell reads what the last redirect of its descriptor 0 gives it: a text, or a file
        // unseen as any script file is; one duplicated from another descriptor, or named by one,
        // is what an earlier command printed.
        switch (redirects.LastOrDefault(redirect => redirect.Descriptor == 0))
        {
            case { Operator: "<<<" } text:
                return [spelled is null ? text.Target : spelled(text.Target)];
            case { Operator: not "<&" } file when Unread(file.Target) is not { } || !program:
                seen?.Invoke(file.Target);
                return null;
            case { } other when program:
                throw new NotSupportedException($"'{name}' reads the script it runs from '{other}', which this does not read");
        }

        return program && inputGiven
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
    /// word as that program's data. The program each runs past its own options is read where a script the string wrote
    /// may be it (<see cref="CommandAt"/>, <see cref="ProgramsAt"/>).
    /// </summary>
    public static IReadOnlyList<string> CommandRunners { get; } =
        ["setsid", "stdbuf", "ionice", "chrt", "taskset", "unshare", "nsenter", "prlimit", "numactl", "cpulimit", "firejail", "flock",
         "torsocks", "proxychains", "proxychains4", "faketime", "strace", "ltrace", "valgrind", "caffeinate", "chroot", "runuser",
         "watch", "ssh", "wsl", "start", "call", "dtach", "abduco", "daemonize"];

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
    private static IReadOnlyList<string>? FishScripts(string name, IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program, Action<string>? scriptFile = null, bool inputGiven = true)
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
            : at < words.Count ? ScriptFile(words[at], name, redirects, program, seen: scriptFile, inputGiven: inputGiven)
            : Input(name, redirects, program, inputGiven: inputGiven, seen: scriptFile);

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

    /// <summary>Whether <paramref name="name"/>, as <see cref="Name"/> reads a program's name, is a shell whose script this reads: one of <see cref="Shells"/>, or fish.</summary>
    internal static bool IsShell(string name) => Is(name, Shells) || Is(name, "fish");

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
    /// characters - each escaped here - and its lone '&amp;' runs the next command once the one
    /// before it ends, spelled ';' - so the reader splits the script where cmd does, and runs
    /// nothing of it in the background.
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
            else if (!quoted && character == '&' && at + 1 < script.Length && script[at + 1] == '&')
            {
                builder.Append("&&");
                at++;
            }
            else if (!quoted && character == '&' && (at == 0 || script[at - 1] is not ('>' or '<')))
            {
                // cmd's lone '&' runs the next command once the one before it ends, as a ';' does - never in the
                // background; a redirect's ('2>&1', '>&2') stays one.
                builder.Append(';');
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
    /// command's words, so a rule its words would meet is not decided without it. How each word
    /// stands to the shell is stripped with them, in step.
    /// </summary>
    private static (IReadOnlyList<string> Words, IReadOnlyList<string> Quoted)? Stripped(string name, IReadOnlyList<string> words, IReadOnlyList<string> quoted)
    {
        if (!Is(name, "env", "nohup", "time", "timeout", "nice", "xargs", "command", "builtin", "exec", "busybox"))
        {
            return null;
        }

        // The wrapper's own spelling from here on: its options are read in the one case each of its
        // kinds writes them.
        name = name.ToLowerInvariant();

        if (name == "env")
        {
            // Its pairs, kept as the command's environment, then the command; -S's command line is read by the
            // flattener above this.
            var read = EnvWords(words);

            // The pairs are spelled as env reads them, made here: their spelling is not kept.
            var envMade = Enumerable.Repeat(string.Empty, read.Pairs.Count);

            return ([.. read.Pairs, .. words.Skip(read.At)], [.. envMade, .. quoted.Skip(read.At)]);
        }

        if (name == "xargs")
        {
            // A replace string - -I's, -i's or --replace's - is where the input goes; without one the input follows the
            // command's words. With no command of its own it echoes what it is handed.
            var (start, replace) = XargsOptions(words);
            IReadOnlyList<string> command = start < words.Count ? [.. words.Skip(start)] : ["echo"];

            // What xargs fills from its input is an expansion, wherever it stands among these words: they are made
            // here, not read from the command line, so their spelling is not kept.
            IReadOnlyList<string> filled = replace is { Length: > 0 }
                ? Filled(command, word => word.Contains(replace, StringComparison.Ordinal), adds: false)
                : Filled(command, word => false, adds: true);

            return (filled, [.. filled.Select(_ => string.Empty)]);
        }

        // A word xargs fills from its input is an expansion wherever the shell would leave it alone, so the words a
        // wrapper leaves keep the spelling it read them with. command -v and -V say what a name is and run nothing: no
        // wrapper, its words each tried as a program still.
        return CommandAt(name, words) is { } at ? ([.. words.Skip(at)], [.. quoted.Skip(at)]) : null;
    }

    /// <summary>
    /// xargs' own options, each a word of its own and each that takes a value with it: where its command begins - the first
    /// word that is none - and the replace string its <c>-I</c>, <c>-i</c> or <c>--replace</c> gives, where one does.
    /// </summary>
    private static (int At, string? Replace) XargsOptions(IReadOnlyList<string> words)
    {
        var at = 1;
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

        return (at, replace);
    }

    /// <summary>
    /// Where the command a wrapper (<see cref="Stripped"/>) or a runner (<see cref="CommandRunners"/>) runs begins among
    /// <paramref name="words"/>, its own name first: past its own options, each with the value it takes, and the words it
    /// reads before the command - timeout's duration, nice's niceness, taskset's mask, chrt's priority, chroot's root,
    /// flock's lock, faketime's time, abduco's session (<see cref="RunnerReadings"/>). Null where it runs no command of those
    /// words on this machine - ssh's runs on its host; wsl's without <c>-e</c> and watch's without <c>-x</c> are command
    /// lines read as scripts of their own (<see cref="ScriptsItRuns"/>, <see cref="Into"/>), and flock's <c>-c</c> and
    /// runuser's without <c>-u</c> run a shell's - where it runs none (<c>command -v</c>, a process's settings by its id),
    /// and where the program is neither. The one reading of a wrapper's and a runner's own options: the wrapper stripped,
    /// and the program a runner runs (<see cref="ProgramsAt"/>), alike.
    /// </summary>
    /// <param name="name">The program's name, as <see cref="Name"/> reads it.</param>
    /// <param name="words">The program and its words.</param>
    /// <exception cref="NotSupportedException">env is given an option it does not name alone (<see cref="EnvWords"/>).</exception>
    private static int? CommandAt(string name, IReadOnlyList<string> words)
    {
        var at = 1;

        if (Is(name, "nohup", "exec", "busybox"))
        {
            return at;
        }

        if (Is(name, "timeout"))
        {
            // Its options, each with its value where it takes one, then the duration the command waits.
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at += words[at] is "--foreground" or "-f" or "-p" || words[at].Contains('=', StringComparison.Ordinal) ? 1
                    : words[at] is "-k" or "-s" or "--kill-after" or "--signal" && at + 1 < words.Count ? 2
                    : 1;
            }

            return at + 1 < words.Count && Duration().IsMatch(words[at]) ? at + 1 : at;
        }

        if (Is(name, "nice"))
        {
            return at < words.Count && (words[at] == "-n" || words[at] == "--adjustment") && at + 1 < words.Count ? at + 2
                : at < words.Count && Niceness().IsMatch(words[at]) ? at + 1
                : at;
        }

        if (Is(name, "time"))
        {
            return at < words.Count && words[at] == "-p" ? at + 1 : at;
        }

        if (Is(name, "command", "builtin"))
        {
            for (; at < words.Count && words[at] is "-v" or "-V" or "-p"; at++)
            {
                if (words[at] is "-v" or "-V")
                {
                    return null;
                }
            }

            return at;
        }

        if (Is(name, "env"))
        {
            return EnvWords(words) is { Split: null } read ? read.At : null;
        }

        if (Is(name, "xargs"))
        {
            return XargsOptions(words).At;
        }

        if (Is(name, "watch"))
        {
            return WatchCommandAt(words) is { Exec: true, At: var watched } ? watched : null;
        }

        if (Is(name, "wsl"))
        {
            return WslCommandAt(words) is { Exec: true, At: var run } ? run : null;
        }

        if (Is(name, "start"))
        {
            return StartOptionsEnd(words, at);
        }

        return RunnerReadings.TryGetValue(Name(name), out var reading) ? reading.CommandAt(words) : null;
    }

    /// <summary>
    /// The words of <paramref name="command"/> that may be a program it runs on this machine: its own, past its environment,
    /// and the one each wrapper or runner among them runs, past that one's own options (<see cref="CommandAt"/>) - for cmd's
    /// start, the first word that is no option of its own and the next, for the first is its window's title where it was
    /// quoted, which the words no longer say. Any other word a runner is given - an option's value, a remote command, a
    /// command line of its own, an argument of the program it runs - is none.
    /// </summary>
    /// <param name="command">A command's words, its environment before them.</param>
    internal static IReadOnlySet<int> ProgramsAt(IReadOnlyList<string> command)
    {
        ArgumentNullException.ThrowIfNull(command);

        var programs = new HashSet<int>();

        for (var at = EnvironmentOf(command); at < command.Count && programs.Add(at);)
        {
            var name = Name(command[at]);
            int? next;

            try
            {
                next = CommandAt(name, [.. command.Skip(at)]);
            }
            catch (NotSupportedException)
            {
                // An env this cannot read is refused where the command is flattened, before a program of its is asked.
                next = null;
            }

            if (next is not { } run)
            {
                break;
            }

            if (Is(name, "start") && at + StartOptionsEnd([.. command.Skip(at)], run + 1) is var titled && titled < command.Count)
            {
                programs.Add(titled);
            }

            at += run;
        }

        return programs;
    }

    /// <summary>
    /// Where cmd's start's own options end, from <paramref name="at"/> on: each a word that begins with <c>/</c>,
    /// <c>/d</c>'s, <c>/node</c>'s and <c>/affinity</c>'s with the value each takes.
    /// </summary>
    private static int StartOptionsEnd(IReadOnlyList<string> words, int at)
    {
        while (at < words.Count && words[at].StartsWith('/'))
        {
            at += words[at].ToLowerInvariant() is "/d" or "/node" or "/affinity" && at + 1 < words.Count ? 2 : 1;
        }

        return at;
    }

    /// <summary>
    /// Where the command watch runs begins among its words - past its own options, each with the value it takes - and
    /// whether its <c>-x</c> runs those words as they are rather than as a shell's command line; null where it runs none,
    /// its help or its version asked. The one reading of watch's own options.
    /// </summary>
    private static (int At, bool Exec)? WatchCommandAt(IReadOnlyList<string> words)
    {
        var at = 1;
        var exec = false;

        while (at < words.Count && words[at].StartsWith('-'))
        {
            if (words[at] is "-h" or "--help" or "-v" or "--version")
            {
                return null;
            }

            exec |= words[at] is "-x" or "--exec";
            at += words[at] is "-n" or "--interval" or "-q" or "--equexit" && at + 1 < words.Count ? 2 : 1;
        }

        return (at, exec);
    }

    /// <summary>A duration as timeout takes it: digits, with a unit and a fraction.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^\d+(\.\d+)?[smhd]?$")]
    private static partial System.Text.RegularExpressions.Regex Duration();

    /// <summary>Whether <paramref name="name"/> is one of <paramref name="programs"/>, without regard to case, as a program's name is read here.</summary>
    private static bool Is(string name, params string[] programs)
        => programs.Any(program => name.Equals(program, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// How the word at <paramref name="at"/> stands to the shell, from <paramref name="quoted"/>, or null where the
    /// word's spelling was not kept: a mask to hand <see cref="Expansion"/> with the word itself.
    /// </summary>
    private static string? At(IReadOnlyList<string> quoted, int at)
        => at >= 0 && at < quoted.Count && quoted[at].Length > 0 ? quoted[at] : null;

    /// <summary>
    /// How the part of the word at <paramref name="at"/> that begins at <paramref name="start"/>, <paramref name="length"/>
    /// characters long, stands to the shell: the word's own mask from <paramref name="quoted"/> cut to the part, or null
    /// where the word's spelling was not kept - a part taken by its place in its own word, never looked up by its text.
    /// </summary>
    private static string? Cut(IReadOnlyList<string> quoted, int at, int start, int length)
        => At(quoted, at) is { } mask && start >= 0 && length >= 0 && start + length <= mask.Length ? mask.Substring(start, length) : null;

    /// <summary>
    /// How each of a command's words the rules read stands to the shell: the environment's words, then the words from
    /// <paramref name="start"/>, as the policy reads the command - each word's own mask where the command's spelling was
    /// kept, and an empty one where it was not, which reads as wholly open.
    /// </summary>
    /// <param name="command">The command, as <see cref="Commands(IReadOnlyList{string}, bool, bool)"/> flattened it.</param>
    /// <param name="start">Which word the command's own program stands at.</param>
    internal static IReadOnlyList<string> QuotedOf(AnalyzedCommand command, int start)
    {
        var quoted = command.Quoted ?? [];

        while (quoted.Count < command.Words.Count)
        {
            quoted = [.. quoted, string.Empty];
        }

        var environment = EnvironmentOf(command.Words);

        return [.. quoted.Take(environment), .. quoted.Skip(start)];
    }

    /// <summary>
    /// <paramref name="part"/> as it stands in <paramref name="word"/>: how the shell reads it there, cut from
    /// <paramref name="quoted"/> at the place it is found, or null where it is not found or its spelling was not kept -
    /// and a word a caller holds without its place reads as wholly open.
    /// </summary>
    private static string? Within(string word, string? quoted, string part)
    {
        if (quoted is null)
        {
            return null;
        }

        var at = word.IndexOf(part, StringComparison.Ordinal);

        return at >= 0 && at + part.Length <= quoted.Length ? quoted[at..(at + part.Length)] : null;
    }

    /// <summary>A niceness as nice takes it: a dash and digits, one word.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^-\d+$")]
    private static partial System.Text.RegularExpressions.Regex Niceness();

    /// <summary>
    /// The commands a script runs, read as the system's own shell reads it: split on its
    /// separators, its command substitutions and subshells read as commands of their own, each
    /// command then flattened again - its own words may name a script or carry a wrapper. Each reads the input the
    /// script is given (<paramref name="inputGiven"/>), or the one what stands around it in the script gives it.
    /// </summary>
    private static IEnumerable<AnalyzedCommand> FromScript(string script, int depth, IReadOnlyList<string> environment, IReadOnlyList<IReadOnlyList<string>>? wrappers = null, bool inputGiven = false, int level = 0)
    {
        foreach (var command in Script(script, 0))
        {
            foreach (var flat in FromWords(command.Words, [.. command.Quoted ?? command.Words.Select(_ => string.Empty)], command.Redirects, depth, environment, wrappers, inputGiven || command.InputGiven, level + command.Level, command.Joined))
            {
                // What a command in the background runs - its own words, a script they name - runs in the background with it.
                yield return command.Background && !flat.Background ? flat with { Background = true } : flat;
            }
        }
    }
}
