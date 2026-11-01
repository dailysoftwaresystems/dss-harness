namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// The command lines a program runs besides being run itself, each a script the shell reader reads: sed's <c>e</c>
    /// commands (<see cref="SedOf"/>), ssh's remote command - its words joined as the remote shell is handed them - and
    /// the commands its options run (<see cref="SshCommand"/>), wsl's and a distribution's own launcher's
    /// (<see cref="DistributionLauncher"/>), the job at and batch schedule, and the strings a program runs as a shell's
    /// script (<see cref="StringRunners"/>). Where it runs a command line this cannot read - sed running what it reads,
    /// ssh and wsl with no command reading one from their input, at's job read from a file or its input - it is refused,
    /// where <paramref name="program"/> says the words are a command's own.
    /// </summary>
    /// <param name="words">The program and its words.</param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>.</param>
    /// <param name="redirects">The redirects the shell makes for the command the words stand in.</param>
    /// <param name="program">Whether the words are a command's own, its program first, rather than another program's arguments.</param>
    /// <param name="inputGiven">Whether the command's input is given by what stands around it (<see cref="AnalyzedCommand.InputGiven"/>).</param>
    private static IReadOnlyList<string> ScriptsItRuns(IReadOnlyList<string> words, IReadOnlyList<string> quoted, IReadOnlyList<ShellRedirect> redirects, bool program, bool inputGiven = true)
    {
        if (words.Count == 0)
        {
            return [];
        }

        var name = Name(words[0]);

        if (Is(name, "sed", "gsed"))
        {
            SedRead? read;

            try
            {
                read = SedOf(words, quoted);
            }
            catch (NotSupportedException) when (!program)
            {
                return [];
            }

            if (read is { ExecutesInput: true } && program)
            {
                throw new NotSupportedException($"'{string.Join(' ', words)}' runs as a command what it reads or what its substitution makes, which this does not read");
            }

            return read?.Executed ?? [];
        }

        if (Is(name, "ssh"))
        {
            return SshCommand(words, redirects, program, inputGiven);
        }

        if (Is(name, "wsl"))
        {
            return WslCommand(words, redirects, program, inputGiven);
        }

        if (DistributionLauncher().IsMatch(name))
        {
            return LauncherCommand(words, redirects, program, inputGiven);
        }

        if (StringsRun(words) is { Count: > 0 } strings)
        {
            return strings;
        }

        return Is(name, "at", "batch") ? AtJob(words, redirects, program) : [];
    }

    /// <summary>
    /// A program that runs a string it is given as a shell's script - <c>sh -c</c>'s, <c>cmd /c</c>'s on Windows - by an
    /// option or as each of its operands.
    /// </summary>
    /// <param name="Programs">Its names.</param>
    /// <param name="Subcommands">The subcommands that run the strings - its first word that is no option - or none where it runs them itself.</param>
    /// <param name="Letters">Its short options whose value is a script.</param>
    /// <param name="Names">Its long options whose value is a script.</param>
    /// <param name="Operands">Whether each word that is no option is a script.</param>
    /// <param name="OperandsBy">The short option that makes each word that is no option a script, where one does.</param>
    private sealed record StringRunner(string[] Programs, string[] Subcommands, string Letters, string[] Names, bool Operands, char? OperandsBy = null);

    /// <summary>
    /// The programs that run a string they are given as a shell's script, each read as the script it is: npx's and npm
    /// exec's <c>-c</c> and <c>--call</c>, each of concurrently's and hyperfine's commands - hyperfine's <c>--prepare</c>,
    /// <c>--setup</c>, <c>--cleanup</c> and <c>--conclude</c> among them - watchexec's command, nodemon's <c>--exec</c>,
    /// entr's utility with its <c>-s</c>, and cargo watch's <c>-s</c> and <c>--shell</c>.
    /// </summary>
    private static readonly StringRunner[] StringRunners =
    [
        new(["npx"], [], "c", ["call"], Operands: false),
        new(["npm"], ["exec", "x"], "c", ["call"], Operands: false),
        new(["concurrently"], [], string.Empty, ["teardown"], Operands: true),
        new(["hyperfine"], [], "psc", ["prepare", "setup", "cleanup", "conclude"], Operands: true),
        new(["watchexec"], [], string.Empty, [], Operands: true),
        new(["nodemon"], [], "x", ["exec"], Operands: false),
        new(["entr"], [], string.Empty, [], Operands: false, OperandsBy: 's'),
        new(["cargo"], ["watch"], "s", ["shell"], Operands: false),
    ];

    /// <summary>
    /// The scripts a program of <see cref="StringRunners"/> runs, each read as a POSIX shell's and as cmd's, which runs it
    /// on Windows: an option's value glued after it or the next word, and its operands where they are scripts - every
    /// word that is no option, an unknown option's value among them, for a word read as a script that runs none runs
    /// nothing more. None past a <c>--</c>, whose words are a command's own, and none for any other program.
    /// </summary>
    private static List<string> StringsRun(IReadOnlyList<string> words)
    {
        var name = Name(words[0]);

        if (StringRunners.FirstOrDefault(runner => Is(name, runner.Programs)) is not { } runner)
        {
            return [];
        }

        var at = 1;

        if (runner.Subcommands.Length > 0)
        {
            while (at < words.Count && words[at].StartsWith('-'))
            {
                at++;
            }

            if (at >= words.Count || !runner.Subcommands.Contains(words[at], StringComparer.Ordinal))
            {
                return [];
            }

            at++;
        }

        var scripts = new List<string>();
        var operands = new List<string>();
        var operandsRun = runner.Operands;

        for (; at < words.Count; at++)
        {
            var word = words[at];

            if (word == "--")
            {
                break;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = word.IndexOf('=', StringComparison.Ordinal);

                if (runner.Names.Contains(equals < 0 ? word[2..] : word[2..equals], StringComparer.Ordinal))
                {
                    if (equals >= 0)
                    {
                        scripts.Add(word[(equals + 1)..]);
                    }
                    else if (at + 1 < words.Count)
                    {
                        scripts.Add(words[++at]);
                    }
                }

                continue;
            }

            if (word.Length > 1 && word[0] == '-')
            {
                for (var letter = 1; letter < word.Length; letter++)
                {
                    operandsRun |= word[letter] == runner.OperandsBy;

                    if (runner.Letters.Contains(word[letter], StringComparison.Ordinal))
                    {
                        if (letter + 1 < word.Length)
                        {
                            scripts.Add(word[(letter + 1)..]);
                        }
                        else if (at + 1 < words.Count)
                        {
                            scripts.Add(words[++at]);
                        }

                        break;
                    }
                }

                continue;
            }

            operands.Add(word);
        }

        if (operandsRun)
        {
            scripts.AddRange(operands);
        }

        return [.. scripts.SelectMany(script => new[] { script, CmdAsPosix(script) }).Distinct(StringComparer.Ordinal)];
    }

    /// <summary>
    /// A WSL distribution's own launcher - <c>ubuntu</c>, <c>ubuntu2204.exe</c>, <c>debian</c>, <c>kali</c>,
    /// <c>opensuse-*</c>, <c>sles-*</c> and their kind - which starts the distribution's shell as wsl does.
    /// </summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"^(?:ubuntu[0-9.]*|debian|kali(?:-linux)?|opensuse[\w.-]*|sles[\w.-]*|oraclelinux[\w.]*|fedoraremix|almalinux[\w.-]*|pengwin)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex DistributionLauncher();

    /// <summary>
    /// The command a distribution's own launcher (<see cref="DistributionLauncher"/>) has its shell run, read as wsl's:
    /// the words after its <c>run</c> or its <c>-c</c>, joined with spaces; given none, the script its shell reads from its
    /// input, as wsl's alone (<see cref="Input"/>). None where it installs, configures or helps.
    /// </summary>
    private static IReadOnlyList<string> LauncherCommand(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program, bool inputGiven)
        => words.Count == 1 || (words[1] is "run" or "-c" && words.Count == 2) ? Input("wsl", redirects, program, inputGiven: inputGiven) ?? []
            : words[1] is "run" or "-c" ? [string.Join(' ', words.Skip(2))]
            : [];

    /// <summary>ssh's short options that take a value: the rest of their cluster, or the next word.</summary>
    private const string SshValued = "BbcDEeFIiJLlmOoPpQRSWw";

    /// <summary>The settings ssh runs a command by, given by its <c>-o</c>: each run by the local shell, the remote command on the host.</summary>
    private static readonly string[] SshCommandSettings = ["ProxyCommand", "LocalCommand", "KnownHostsCommand", "RemoteCommand"];

    /// <summary>
    /// The commands ssh runs: those its <c>-o</c> settings name (<see cref="SshCommandSettings"/>) - <c>Name=value</c> or
    /// <c>Name value</c>, in any case; a configuration file its <c>-F</c> names, other than none, refused where the words
    /// are a command's own - and the command it has the remote shell run: its words after its options and its
    /// destination, joined with spaces as ssh joins them; where it gives none, the script the remote login shell reads
    /// from ssh's input, read as a shell's is (<see cref="Input"/>). None where it connects nowhere - its configuration
    /// printed (<c>-G</c>), a query (<c>-Q</c>), a control command (<c>-O</c>), its version - and no remote command where
    /// it runs none - <c>-N</c>, a subsystem (<c>-s</c>) - or names no destination; and with no command, none where its
    /// input reaches no shell: <c>-n</c> and <c>-f</c> read it from nothing, and <c>-W</c> hands it to a port.
    /// </summary>
    private static IReadOnlyList<string> SshCommand(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program, bool inputGiven)
    {
        var at = 1;

        // -n and -f give ssh no input, and -W hands its input to a port rather than a shell.
        var noShellInput = false;
        var noCommand = false;
        var settings = new List<string>();

        for (; at < words.Count; at++)
        {
            var word = words[at];

            if (word == "--")
            {
                at++;
                break;
            }

            if (word.Length < 2 || word[0] != '-')
            {
                break;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                if (word[letter] is 'G' or 'V' or 'Q' or 'O')
                {
                    return [];
                }

                noCommand |= word[letter] is 'N' or 's';
                noShellInput |= word[letter] is 'n' or 'f' or 'W';

                if (SshValued.Contains(word[letter], StringComparison.Ordinal))
                {
                    var value = letter + 1 < word.Length ? word[(letter + 1)..] : words.ElementAtOrDefault(at + 1);

                    // A configuration file names the commands it runs as -o does, which this does not read: none but
                    // the empty one is read.
                    if (word[letter] == 'F' && program && value is not ("/dev/null" or "none" or "NUL"))
                    {
                        throw new NotSupportedException($"'{string.Join(' ', words)}' reads its settings from '{value}', whose ProxyCommand, LocalCommand and their kind run commands, which this does not read");
                    }

                    if (word[letter] == 'o' && value is not null
                        && SshCommandSettings.FirstOrDefault(setting => value.TrimStart().StartsWith(setting, StringComparison.OrdinalIgnoreCase)) is { } setting
                        && value.TrimStart()[setting.Length..] is var rest && rest.Length > 0 && (rest[0] == '=' || char.IsWhiteSpace(rest[0])))
                    {
                        settings.Add(rest.TrimStart().TrimStart('=').TrimStart());
                    }

                    at += letter + 1 < word.Length ? 0 : 1;
                    break;
                }
            }
        }

        if (at >= words.Count || noCommand)
        {
            return settings;
        }

        if (at + 1 < words.Count)
        {
            return [.. settings, string.Join(' ', words.Skip(at + 1))];
        }

        return noShellInput ? settings : [.. settings, .. Input("ssh", redirects, program, inputGiven: inputGiven) ?? []];
    }

    /// <summary>
    /// The command wsl has its distribution's shell run: its words after its own options, joined with spaces as wsl joins
    /// them, or after its <c>--</c>; read from its input where it gives none, as a shell started with no script reads one
    /// (<see cref="Input"/>).
    /// None where its <c>-e</c> runs the words after it as they are - each tried as a program as every word is - where it
    /// starts the root user, refused as a root action, or where it manages its distributions (<c>--list</c>,
    /// <c>--shutdown</c> and their kind) and runs no command.
    /// </summary>
    private static IReadOnlyList<string> WslCommand(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program, bool inputGiven)
    {
        for (var at = 1; at < words.Count; at++)
        {
            var word = words[at];

            // -e runs its words as they are; and a wsl that starts the root user runs nothing an agent may, refused as a
            // root action before what it runs is read.
            if (word is "-e" or "--exec"
                || (word is "-u" or "--user" && at + 1 < words.Count && words[at + 1].Equals("root", StringComparison.OrdinalIgnoreCase))
                || word.Equals("--user=root", StringComparison.OrdinalIgnoreCase))
            {
                return [];
            }

            if (word.StartsWith("--", StringComparison.Ordinal) && word.Contains('=', StringComparison.Ordinal))
            {
                continue;
            }

            if (word == "--")
            {
                return at + 1 < words.Count ? [string.Join(' ', words.Skip(at + 1))] : Input("wsl", redirects, program, inputGiven: inputGiven) ?? [];
            }

            if (word is "-d" or "--distribution" or "-u" or "--user" or "--cd" or "--shell-type" or "--distribution-id")
            {
                at++;
                continue;
            }

            if (word is "~" or "--system")
            {
                continue;
            }

            if (word.StartsWith('-'))
            {
                return [];
            }

            return [string.Join(' ', words.Skip(at))];
        }

        return Input("wsl", redirects, program, inputGiven: inputGiven) ?? [];
    }

    /// <summary>
    /// The job at and batch schedule: the text a here-document or a here-string gives them. None where they schedule
    /// nothing - a job listed, removed or printed, help, a version.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// The command's own at reads its job from a file (<c>-f</c>, a redirect) or from what an earlier command printed,
    /// which this does not read.
    /// </exception>
    private static IReadOnlyList<string> AtJob(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program)
    {
        for (var at = 1; at < words.Count; at++)
        {
            var word = words[at];

            if (word.Length < 2 || word[0] != '-' || word.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                if (word[letter] is 'l' or 'r' or 'd' or 'c' or 'V' or 'h')
                {
                    return [];
                }

                if (word[letter] == 'f')
                {
                    return program ? throw new NotSupportedException($"'{string.Join(' ', words)}' schedules the job a file holds, which this does not read") : [];
                }

                if (word[letter] is 'q' or 't')
                {
                    at += letter + 1 < word.Length ? 0 : 1;
                    break;
                }
            }
        }

        return redirects.LastOrDefault(redirect => redirect.Descriptor == 0) switch
        {
            { Operator: "<<<" } text => [text.Target],
            _ when program => throw new NotSupportedException($"'{string.Join(' ', words)}' schedules a job it reads from a file or from what an earlier command printed, which this does not read"),
            _ => [],
        };
    }

    /// <summary>
    /// The terminal sessions' programs, which run what they are given in a session that lives apart from the agent - its
    /// own process tree, its own environment - and type into one already running, whose place and state this cannot tell.
    /// </summary>
    private static readonly string[] SessionPrograms = ["tmux", "byobu", "byobu-tmux", "byobu-screen", "screen", "zellij"];

    /// <summary>tmux's commands that start, run and type nothing: its listings, its queries, its kills.</summary>
    private static readonly string[] SessionQueries =
    [
        "ls", "list-sessions", "list-windows", "lsw", "list-panes", "lsp", "list-clients", "lsc", "list-buffers", "lsb",
        "list-commands", "lscm", "list-keys", "lsk", "has-session", "has", "kill-session", "kill-server", "kill-window", "killw",
        "kill-pane", "killp", "capture-pane", "capturep", "show-options", "show", "show-buffer", "showb", "info", "server-info",
    ];

    /// <summary>
    /// Whether a terminal session's program (<see cref="SessionPrograms"/>) is given what starts, runs or types nothing: its
    /// help or its version, screen's and zellij's listings, tmux's listings, queries and kills (<see cref="SessionQueries"/>)
    /// - none with a word that holds a format's <c>#(...)</c>, which tmux runs, or a configuration file it reads.
    /// </summary>
    private static bool AsksASessionNothing(string program, IReadOnlyList<string> rest)
    {
        if (rest.Any(word => word.Contains("#(", StringComparison.Ordinal) || word == ";" || word.EndsWith(';')))
        {
            return false;
        }

        if (rest.Any(word => word is "--help" or "-h" or "-V" or "--version" or "-v" or "-version"))
        {
            return true;
        }

        if (Is(program, "screen", "byobu-screen"))
        {
            return rest.Any(word => word is "-ls" or "-list") && !rest.Any(word => word.StartsWith("-X", StringComparison.Ordinal));
        }

        if (Is(program, "zellij"))
        {
            return rest.FirstOrDefault(word => !word.StartsWith('-')) is "list-sessions" or "ls" or "kill-session" or "k" or "kill-all-sessions" or "ka" or "delete-session" or "d" or "delete-all-sessions" or "da";
        }

        for (var at = 0; at < rest.Count; at++)
        {
            if (rest[at] is "-L" or "-S" or "-T")
            {
                at++;
                continue;
            }

            if (rest[at].StartsWith('-'))
            {
                // -f reads a configuration file, which runs what it holds; -c runs a shell command.
                if (rest[at].IndexOfAny(['f', 'c']) > 0)
                {
                    return false;
                }

                continue;
            }

            return SessionQueries.Contains(rest[at], StringComparer.Ordinal);
        }

        return false;
    }
}
