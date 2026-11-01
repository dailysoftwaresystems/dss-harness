namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// What a sed command does past the text it edits, as GNU sed reads its words and its script: the command lines its
    /// <c>e</c> command runs, whether it runs as a command what it reads (a bare <c>e</c>, a substitution's <c>e</c>
    /// flag), and the files its <c>w</c> and <c>W</c> commands and a substitution's <c>w</c> flag write - none of them
    /// under <c>--sandbox</c>, which refuses all three.
    /// </summary>
    /// <param name="Executed">The command line each <c>e</c> with one runs through the shell.</param>
    /// <param name="ExecutesInput">Whether it runs as a command the text it reads or a substitution makes, which no word spells.</param>
    /// <param name="Written">The files it writes besides an in-place edit's, as each is spelled.</param>
    /// <param name="Files">The script files its <c>-f</c> and <c>--file</c> name, whose commands it runs as a script's.</param>
    private sealed record SedRead(IReadOnlyList<string> Executed, bool ExecutesInput, IReadOnlyList<string> Written, IReadOnlyList<string> Files);

    /// <summary>
    /// The script of a sed command (<paramref name="words"/>, its name first) as GNU sed takes it - every <c>-e</c> and
    /// <c>--expression</c> joined, a line each, else its first word that is no option - and read (<see cref="SedRead"/>);
    /// null where it runs nothing (its help, its version) or edits under <c>--sandbox</c>. A bare <c>-i</c> takes the word
    /// after it as its suffix to BSD's sed, so the word after that is read as the script it is there too, where it reads as
    /// one. A script file (<c>-f</c>) is not read, as no script file is.
    /// </summary>
    /// <param name="words">The words, sed's name first.</param>
    /// <param name="quoted">How each word's characters stand to the shell, kept in step with <paramref name="words"/>.</param>
    /// <exception cref="NotSupportedException">
    /// The script holds what the shell expands, whose text this cannot know, or what sed's grammar as read here does not
    /// hold.
    /// </exception>
    private static SedRead? SedOf(IReadOnlyList<string> words, IReadOnlyList<string> quoted)
    {
        var expressions = new List<(string Script, int Word)>();
        var operands = new List<(string Word, int At)>();
        var files = new List<string>();
        var sandbox = false;
        var bsdInPlace = false;
        var ended = false;

        for (var at = 1; at < words.Count; at++)
        {
            var word = words[at];

            if (ended || word.Length < 2 || word[0] != '-')
            {
                operands.Add((word, at));
                continue;
            }

            if (word == "--")
            {
                ended = true;
                continue;
            }

            if (word.StartsWith("--", StringComparison.Ordinal))
            {
                var equals = word.IndexOf('=', StringComparison.Ordinal);
                var spelled = equals < 0 ? word : word[..equals];
                var named = SedLongOptions.Where(option => option.StartsWith(spelled, StringComparison.Ordinal)).ToList();
                var option = named.Contains(spelled) ? spelled : named.Count == 1 ? named[0] : spelled;

                if (option is "--help" or "--version")
                {
                    return null;
                }

                sandbox |= option == "--sandbox";

                if (option is "--expression" or "--file" or "--line-length")
                {
                    var value = equals >= 0 ? word[(equals + 1)..] : at + 1 < words.Count ? words[++at] : null;

                    if (option == "--expression" && value is not null)
                    {
                        expressions.Add((value, at));
                    }

                    if (option == "--file" && value is not null)
                    {
                        files.Add(value);
                    }
                }

                continue;
            }

            for (var letter = 1; letter < word.Length; letter++)
            {
                if (word[letter] is 'e' or 'f' or 'l')
                {
                    var glued = letter + 1 < word.Length;
                    var value = glued ? word[(letter + 1)..] : at + 1 < words.Count ? words[++at] : null;

                    if (word[letter] == 'e' && value is not null)
                    {
                        expressions.Add((value, glued ? -1 : at));
                    }

                    if (word[letter] == 'f' && value is not null)
                    {
                        files.Add(value);
                    }

                    break;
                }

                if (word[letter] == 'i')
                {
                    // GNU's -i takes a suffix glued to it alone; BSD's takes the next word, where it stands alone.
                    bsdInPlace |= word == "-i";
                    break;
                }
            }
        }

        if (sandbox)
        {
            return null;
        }

        var scripts = new List<(string Script, string? Quoted, bool Possible)>();

        if (expressions.Count > 0)
        {
            foreach (var (script, word) in expressions)
            {
                scripts.Add((script, word >= 0 ? At(quoted, word) : null, false));
            }
        }
        else if (files.Count == 0 && operands.Count > 0)
        {
            scripts.Add((operands[0].Word, At(quoted, operands[0].At), false));

            if (bsdInPlace && operands.Count > 1)
            {
                scripts.Add((operands[1].Word, At(quoted, operands[1].At), true));
            }
        }

        var executed = new List<string>();
        var written = new List<string>();
        var executesInput = false;

        foreach (var (script, mask, possible) in scripts)
        {
            // A script the shell fills in is one whose text this cannot know: its commands may be any of sed's.
            if (FilledByTheShell(script, mask) && !possible)
            {
                throw new NotSupportedException($"'{script}' is a sed script the shell fills in, which this does not read");
            }

            SedRead read;

            try
            {
                read = SedScript(script);
            }
            catch (NotSupportedException) when (possible)
            {
                // The word read as BSD's script is no script: there it is a file, or sed refuses it and runs nothing.
                continue;
            }

            executed.AddRange(read.Executed);
            written.AddRange(read.Written);
            executesInput |= read.ExecutesInput;
        }

        return new SedRead(executed, executesInput, written, files);
    }

    /// <summary>
    /// Whether the shell fills in part of <paramref name="word"/> before the program is handed it: a substitution, a
    /// backtick, or a variable - a dollar it reads before a name, a brace, a digit or a special parameter - where the
    /// word's characters stand open or in double quotes. A dollar before anything else (<c>s/x$//</c>) is a dollar, and a
    /// word another program fills in stands as one filled in.
    /// </summary>
    private static bool FilledByTheShell(string word, string? quoted)
    {
        if (word.Contains(InputWord, StringComparison.Ordinal))
        {
            return true;
        }

        var classes = ClassesOf(word, quoted);

        for (var at = 0; at < word.Length; at++)
        {
            if (classes[at] == WordQuoting.Literal)
            {
                continue;
            }

            if (word[at] == '`'
                || (word[at] == '$' && at + 1 < word.Length && (char.IsAsciiLetterOrDigit(word[at + 1]) || word[at + 1] is '_' or '{' or '(' or '@' or '*' or '#' or '?' or '-' or '$' or '!')))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>GNU sed's long options, by which any prefix that begins one alone names it.</summary>
    private static readonly string[] SedLongOptions =
    [
        "--expression", "--file", "--in-place", "--line-length", "--null-data", "--zero-terminated", "--quiet", "--silent",
        "--regexp-extended", "--separate", "--sandbox", "--unbuffered", "--debug", "--posix", "--follow-symlinks", "--binary",
        "--help", "--version",
    ];

    /// <summary>
    /// One sed script read as GNU sed reads it: its commands, each after its addresses and its <c>!</c>, separated by a
    /// line's end or a <c>;</c>; a text command's text, a label, a file's name and <c>e</c>'s command line each to the end
    /// of its line; a substitution's and a transliteration's parts to their delimiter, a backslash escaping it.
    /// </summary>
    /// <exception cref="NotSupportedException">The script holds what this grammar does not, which sed itself may read some other way.</exception>
    private static SedRead SedScript(string script)
    {
        var executed = new List<string>();
        var written = new List<string>();
        var executesInput = false;
        var at = 0;

        bool Ended() => at >= script.Length;
        char Current() => script[at];

        string ToLineEnd()
        {
            var end = script.IndexOf('\n', at);
            var text = end < 0 ? script[at..] : script[at..end];
            at = end < 0 ? script.Length : end;
            return text;
        }

        void Blanks()
        {
            while (!Ended() && Current() is ' ' or '\t')
            {
                at++;
            }
        }

        // A part of a substitution or a transliteration, to its delimiter: a backslash escapes what follows it.
        void Part(char delimiter)
        {
            for (; ; at++)
            {
                if (Ended())
                {
                    throw new NotSupportedException($"'{script}' holds a substitution sed does not close, which this does not read");
                }

                if (Current() == '\\')
                {
                    at++;
                    continue;
                }

                if (Current() == delimiter)
                {
                    at++;
                    return;
                }
            }
        }

        void Address()
        {
            if (Ended())
            {
                return;
            }

            if (char.IsAsciiDigit(Current()))
            {
                while (!Ended() && (char.IsAsciiDigit(Current()) || Current() == '~'))
                {
                    at++;
                }
            }
            else if (Current() == '$')
            {
                at++;
            }
            else if (Current() is '/' or '\\')
            {
                var delimiter = Current() == '\\' && at + 1 < script.Length ? script[++at] : '/';
                at++;
                Part(delimiter);

                while (!Ended() && Current() is 'I' or 'M')
                {
                    at++;
                }
            }
            else if (Current() is '+' or '~')
            {
                at++;

                while (!Ended() && char.IsAsciiDigit(Current()))
                {
                    at++;
                }
            }
        }

        while (true)
        {
            while (!Ended() && (char.IsWhiteSpace(Current()) || Current() == ';'))
            {
                at++;
            }

            if (Ended())
            {
                break;
            }

            if (Current() == '#')
            {
                ToLineEnd();
                continue;
            }

            Address();
            Blanks();

            if (!Ended() && Current() == ',')
            {
                at++;
                Blanks();
                Address();
                Blanks();
            }

            while (!Ended() && (Current() == '!' || Current() is ' ' or '\t'))
            {
                at++;
            }

            if (Ended())
            {
                throw new NotSupportedException($"'{script}' holds an address with no command, which this does not read");
            }

            var command = script[at++];

            switch (command)
            {
                case '{' or '}' or '=' or 'd' or 'D' or 'g' or 'G' or 'h' or 'H' or 'n' or 'N' or 'p' or 'P' or 'x' or 'z' or 'F':
                    break;
                case 'l' or 'L' or 'q' or 'Q':
                    Blanks();

                    while (!Ended() && char.IsAsciiDigit(Current()))
                    {
                        at++;
                    }

                    break;
                case 'a' or 'i' or 'c':
                    // The text to its line's end, and on past each line its backslash continues.
                    while (ToLineEnd().EndsWith('\\') && !Ended())
                    {
                        at++;
                    }

                    break;
                case ':' or 'b' or 't' or 'T' or 'v':
                    while (!Ended() && Current() is not ';' and not '\n')
                    {
                        at++;
                    }

                    break;
                case 'r' or 'R':
                    ToLineEnd();
                    break;
                case 'w' or 'W':
                    Blanks();
                    written.Add(ToLineEnd().Trim());
                    break;
                case 'e':
                    Blanks();
                    var line = ToLineEnd().Trim();

                    if (line.Length == 0)
                    {
                        executesInput = true;
                    }
                    else
                    {
                        executed.Add(line);
                    }

                    break;
                case 's' or 'y':
                    if (Ended() || Current() is '\n' or '\\')
                    {
                        throw new NotSupportedException($"'{script}' holds a '{command}' with no delimiter sed reads, which this does not read");
                    }

                    var delimiter = script[at++];
                    Part(delimiter);
                    Part(delimiter);

                    if (command == 'y')
                    {
                        break;
                    }

                    // The substitution's flags: e runs the line it makes as a command, w writes it to a file named to its
                    // line's end.
                    while (!Ended() && Current() is not ';' and not '\n' and not '}' and not '#')
                    {
                        var flag = script[at++];

                        if (flag == 'e')
                        {
                            executesInput = true;
                        }
                        else if (flag == 'w')
                        {
                            Blanks();
                            written.Add(ToLineEnd().Trim());
                        }
                        else if (!(char.IsAsciiDigit(flag) || flag is 'g' or 'p' or 'i' or 'I' or 'm' or 'M' or ' ' or '\t'))
                        {
                            throw new NotSupportedException($"'{script}' gives a substitution the flag '{flag}', which this does not read");
                        }
                    }

                    break;
                default:
                    throw new NotSupportedException($"'{script}' holds the sed command '{command}', which this does not read");
            }
        }

        return new SedRead(executed, executesInput, written, []);
    }
}
