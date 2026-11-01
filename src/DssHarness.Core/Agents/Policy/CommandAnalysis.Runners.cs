namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// The command lines a program runs besides being run itself, each a script the shell reader reads: sed's <c>e</c>
    /// commands (<see cref="SedOf"/>), ssh's remote command - its words joined as the remote shell is handed them - and
    /// wsl's, the job at and batch schedule. Where it runs a command line this cannot read - sed running what it reads,
    /// ssh and wsl with no command reading one from their input, at's job read from a file or its input - it is refused,
    /// where <paramref name="program"/> says the words are a command's own.
    /// </summary>
    /// <param name="words">The program and its words.</param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>.</param>
    /// <param name="redirects">The redirects the shell makes for the command the words stand in.</param>
    /// <param name="program">Whether the words are a command's own, its program first, rather than another program's arguments.</param>
    private static IReadOnlyList<string> ScriptsItRuns(IReadOnlyList<string> words, IReadOnlyList<string> quoted, IReadOnlyList<ShellRedirect> redirects, bool program)
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
            return SshCommand(words, redirects, program);
        }

        if (Is(name, "wsl"))
        {
            return WslCommand(words, redirects, program);
        }

        return Is(name, "at", "batch") ? AtJob(words, redirects, program) : [];
    }

    /// <summary>ssh's short options that take a value: the rest of their cluster, or the next word.</summary>
    private const string SshValued = "BbcDEeFIiJLlmOoPpQRSWw";

    /// <summary>
    /// The command ssh has the remote shell run: its words after its options and its destination, joined with spaces as ssh
    /// joins them; read from its input where it gives none, as a shell started with no script reads one. None where it runs
    /// no command - <c>-N</c>, a subsystem (<c>-s</c>), a query (<c>-Q</c>), a control command (<c>-O</c>), its
    /// configuration printed (<c>-G</c>), its version - or names no destination.
    /// </summary>
    private static IReadOnlyList<string> SshCommand(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program)
    {
        var at = 1;

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
                if (word[letter] is 'N' or 's' or 'G' or 'V' or 'Q' or 'O')
                {
                    return [];
                }

                if (SshValued.Contains(word[letter], StringComparison.Ordinal))
                {
                    at += letter + 1 < word.Length ? 0 : 1;
                    break;
                }
            }
        }

        if (at >= words.Count)
        {
            return [];
        }

        return at + 1 < words.Count ? [string.Join(' ', words.Skip(at + 1))] : Input("ssh", redirects, program) ?? [];
    }

    /// <summary>
    /// The command wsl has its distribution's shell run: its words after its own options, joined with spaces as wsl joins
    /// them, or after its <c>--</c>; read from its input where it gives none, as a shell started with no script reads one.
    /// None where its <c>-e</c> runs the words after it as they are - each tried as a program as every word is - where it
    /// starts the root user, refused as a root action, or where it manages its distributions (<c>--list</c>,
    /// <c>--shutdown</c> and their kind) and runs no command.
    /// </summary>
    private static IReadOnlyList<string> WslCommand(IReadOnlyList<string> words, IReadOnlyList<ShellRedirect> redirects, bool program)
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
                return at + 1 < words.Count ? [string.Join(' ', words.Skip(at + 1))] : Input("wsl", redirects, program) ?? [];
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

        return Input("wsl", redirects, program) ?? [];
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
