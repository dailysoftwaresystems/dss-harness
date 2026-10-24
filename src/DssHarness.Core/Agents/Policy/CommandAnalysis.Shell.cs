using System.Text;

namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>The word a command's input stands in, where <c>xargs</c> hands it words: an expansion, whatever it would be.</summary>
    internal const string InputWord = "$(xargs)";

    /// <summary>
    /// The commands a script spells, read as the system's own shell reads it: split on its separators; the body of a
    /// command substitution, a backtick, a process substitution and a subshell read as commands of their own, each
    /// before the command it stands in, its text kept in the word it stands in, so the word is the expansion it is; a
    /// comment and a here-document's lines never read as commands, a here-document's or a here-string's text kept as
    /// the input it gives; a redirect kept apart from the command's words, after them, its operator before the file it
    /// names; the shell's reserved words read only where a command begins and only unquoted; a <c>for</c>'s and a
    /// <c>select</c>'s head kept as a command its reserved word leads, so no word of it is a program, and a
    /// <c>case</c>'s patterns never read as commands. What the shell would refuse to read -
    /// a quote, a parenthesis or a backtick that does not close - is refused.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="nesting">How deep in parentheses the script stands: refused past <see cref="Nesting"/>.</param>
    private static IReadOnlyList<IReadOnlyList<string>> Script(string script, int nesting)
        => new ShellReader(script, nesting).Read();

    /// <summary>Whether <paramref name="word"/> is a redirect as the reader keeps one: its operator, then the file it names or the text it gives.</summary>
    internal static bool IsRedirect(string word)
        => word.Length > 0 && word[0] is '<' or '>' && !(word.Length > 1 && word[1] == '(');

    /// <summary>
    /// The text a redirect word gives a command as its input - a here-document's lines or a here-string's word, kept
    /// as <c>&lt;&lt;&lt;text</c> - or null where the word gives none.
    /// </summary>
    internal static string? InputText(string word)
        => word.StartsWith("<<<", StringComparison.Ordinal) ? word[3..] : null;

    /// <summary>
    /// One simple command as the reader builds it: its words, and its redirects kept apart - put after its words once
    /// it is read, so a word's place is the place the program reads it at.
    /// </summary>
    private sealed class Simple
    {
        public List<string> Words { get; } = [];

        public List<string> Redirects { get; } = [];

        public bool IsEmpty => Words.Count == 0 && Redirects.Count == 0;

        public IReadOnlyList<string> Whole() => [.. Words, .. Redirects];
    }

    /// <summary>A here-document a line named, read once the line ends: the command it fills and its place there.</summary>
    private sealed record PendingDocument(Simple Command, int Place, string Delimiter, bool Literal, bool StripsTabs);

    /// <summary>Where a list of commands ends: the script's end, a closing parenthesis, or a case clause's terminator.</summary>
    private enum ListEnd
    {
        Script,
        Parenthesis,
        CaseClause,
    }

    /// <summary>
    /// A shell script read once, front to back, as a shell's own parser reads it: lists of commands, subshells and
    /// substitutions read by going into them, never by guessing where they close.
    /// </summary>
    private sealed class ShellReader(string text, int nesting)
    {
        /// <summary>The words a reserved word of the shell is where a command begins, unquoted.</summary>
        private static readonly HashSet<string> Reserved =
            ["if", "then", "elif", "else", "fi", "while", "until", "do", "done", "{", "}", "!", "time", "coproc", "esac"];

        /// <summary>What the script runs, in the order the shell meets it: a command whose here-documents may still be read.</summary>
        private readonly List<Simple> _read = [];

        /// <summary>The here-documents the line being read named, read once it ends.</summary>
        private readonly List<PendingDocument> _documents = [];

        private int _at;

        /// <summary>How deep in parentheses and substitutions the reader stands: refused past <see cref="Nesting"/>.</summary>
        private int _depth = nesting;

        public IReadOnlyList<IReadOnlyList<string>> Read()
        {
            if (_depth > Nesting)
            {
                throw Deep();
            }

            ReadList(ListEnd.Script);
            ReadDocuments();

            return [.. _read.Where(command => !command.IsEmpty).Select(command => command.Whole())];
        }

        /// <summary>Reads commands until <paramref name="end"/>: the script's end, the parenthesis that closes the list, or a case clause's terminator.</summary>
        private void ReadList(ListEnd end)
        {
            while (true)
            {
                SkipBlanks();

                if (_at >= text.Length)
                {
                    if (end is ListEnd.Parenthesis)
                    {
                        throw new NotSupportedException("a parenthesis in it does not close, which this does not read");
                    }

                    return;
                }

                var character = text[_at];

                if (character is '\n' or '\r')
                {
                    NewLine();
                    continue;
                }

                if (character == '#')
                {
                    SkipComment();
                    continue;
                }

                if (character == ';')
                {
                    // ';;', ';&' and ';;&' end a case clause; outside one, they separate as ';' does.
                    var terminator = At(";;&") ? 3 : At(";;") || At(";&") ? 2 : 1;
                    _at += terminator;

                    if (terminator > 1 && end is ListEnd.CaseClause)
                    {
                        return;
                    }

                    continue;
                }

                if (character == '&' && !At("&>"))
                {
                    _at += At("&&") ? 2 : 1;
                    continue;
                }

                if (character == '|')
                {
                    _at += At("||") || At("|&") ? 2 : 1;
                    continue;
                }

                if (character == ')')
                {
                    _at++;

                    // The parenthesis that closes the list; one nothing opened separates, as a case pattern's would.
                    if (end is ListEnd.Parenthesis)
                    {
                        return;
                    }

                    continue;
                }

                if (end is ListEnd.CaseClause && AtWord("esac"))
                {
                    return;
                }

                if (character == '(')
                {
                    ReadSubshell();
                    continue;
                }

                ReadCommand(end);
            }
        }

        /// <summary>A subshell, read as a list of its own; what follows it is its redirects, and a word there names the program by what the subshell printed.</summary>
        private void ReadSubshell()
        {
            _at++;
            Deeper(() => ReadList(ListEnd.Parenthesis));

            var after = new Simple();
            _read.Add(after);

            while (true)
            {
                SkipBlanks();

                if (_at >= text.Length || text[_at] is '\n' or '\r' or ';' or '|' or ')' or '#' || (text[_at] == '&' && !At("&>")))
                {
                    return;
                }

                if (AtRedirect())
                {
                    ReadRedirect(after);
                    continue;
                }

                throw new NotSupportedException("a subshell names the program a command runs, which this does not read");
            }
        }

        /// <summary>
        /// One command: its environment, its words and its redirects - or a reserved word's own grammar - kept after
        /// the commands its substitutions run, which run before it.
        /// </summary>
        private void ReadCommand(ListEnd end)
        {
            var command = new Simple();
            ReadCommandInto(command, end);
            _read.Add(command);
        }

        private void ReadCommandInto(Simple command, ListEnd end)
        {
            while (true)
            {
                SkipBlanks();

                // Inside [[ ]] a parenthesis groups what it tests, '&&', '||' and '!' join its tests, '<' and '>'
                // compare, and '|' stands in a pattern: each a word of the test, none a separator, a subshell or a
                // redirect.
                if (_at < text.Length && text[_at] is '(' or ')' or '&' or '|' or '<' or '>' && InTest(command))
                {
                    var joined = At("&&") || At("||") ? 2 : 1;
                    command.Words.Add(text.Substring(_at, joined));
                    _at += joined;
                    continue;
                }

                if (_at >= text.Length || text[_at] is '\n' or '\r' or ';' or '|' or ')' || (text[_at] == '&' && !At("&>")))
                {
                    return;
                }

                if (text[_at] == '#')
                {
                    SkipComment();
                    return;
                }

                if (text[_at] == '(')
                {
                    // 'name ()' defines a function, whose body is read as the commands it would run; a parenthesis
                    // anywhere else in a command is read as the subshell it would be.
                    if (command.Words.Count == 1 && command.Redirects.Count == 0 && !IsAssignment(command.Words[0]) && EmptyParentheses())
                    {
                        command.Words.Clear();
                        continue;
                    }

                    ReadSubshell();
                    continue;
                }

                if (AtRedirect())
                {
                    ReadRedirect(command);
                    continue;
                }

                var start = _at;
                var declares = command.Words.Count > 0 && command.Words[0] is "declare" or "typeset" or "local" or "export" or "readonly";
                var (word, quoted) = ReadWord(declares || command.Words.All(IsAssignment));

                // A reserved word where a command begins - unquoted, before any word but the environment's none - is the
                // shell's own, never a command's.
                if (command.Words.Count == 0 && quoted < 0)
                {
                    if (word is "for" or "select")
                    {
                        command.Words.Add(word);
                        ReadLoopHead(command);
                        return;
                    }

                    if (word == "case")
                    {
                        ReadCase();
                        return;
                    }

                    if (word == "function")
                    {
                        SkipBlanks();
                        _ = ReadWord(false);
                        _ = EmptyParentheses();
                        continue;
                    }

                    if (word == "esac" && end is ListEnd.CaseClause)
                    {
                        _at = start;
                        return;
                    }

                    if (Reserved.Contains(word))
                    {
                        if (word == "time")
                        {
                            SkipBlanks();

                            if (AtWord("-p"))
                            {
                                _at += 2;
                            }
                        }

                        continue;
                    }
                }

                command.Words.Add(word);
            }
        }

        /// <summary>
        /// A <c>for</c>'s or a <c>select</c>'s head, kept in <paramref name="head"/> after its reserved word: its name,
        /// <c>in</c> and the words it goes over, which run nothing - yet the paths they name are the ones its body is
        /// handed, so the head stays a command whose words are read as every command's are. Its body follows as commands.
        /// </summary>
        private void ReadLoopHead(Simple head)
        {
            SkipBlanks();

            // The arithmetic form, for ((...)), read as the subshells it spells: what it computes runs nothing of its own.
            if (_at < text.Length && text[_at] == '(')
            {
                head.Words.Clear();
                ReadSubshell();
                return;
            }

            head.Words.Add(ReadWord(false).Word);
            SkipBlanksAndLines();

            if (!AtWord("in"))
            {
                return;
            }

            _at += 2;
            head.Words.Add("in");

            // The words it goes over, up to the separator before its body: their expansions run, and they run nothing.
            while (true)
            {
                SkipBlanks();

                if (_at >= text.Length || text[_at] is '\n' or '\r' or ';' or '&' or '|' or ')')
                {
                    return;
                }

                if (text[_at] == '#')
                {
                    SkipComment();
                    return;
                }

                head.Words.Add(ReadWord(false).Word);
            }
        }

        /// <summary>A <c>case</c>: its word, then each clause - its patterns, none a command, and the commands it runs - to its <c>esac</c>.</summary>
        private void ReadCase()
        {
            SkipBlanks();
            _ = ReadWord(false);
            SkipBlanksAndLines();

            if (!AtWord("in"))
            {
                throw new NotSupportedException("a case names no 'in', which this does not read");
            }

            _at += 2;

            while (true)
            {
                SkipBlanksAndLines();

                if (_at >= text.Length)
                {
                    throw new NotSupportedException("a case does not close, which this does not read");
                }

                if (AtWord("esac"))
                {
                    _at += 4;
                    return;
                }

                if (text[_at] == '(')
                {
                    _at++;
                }

                // The patterns, split by '|', to the parenthesis that closes them.
                while (true)
                {
                    SkipBlanks();

                    if (_at >= text.Length)
                    {
                        throw new NotSupportedException("a case pattern does not close, which this does not read");
                    }

                    if (text[_at] == ')')
                    {
                        _at++;
                        break;
                    }

                    if (text[_at] == '|')
                    {
                        _at++;
                        continue;
                    }

                    if (text[_at] is '\n' or '\r' or ';' or '&' or '(' || AtRedirect())
                    {
                        throw new NotSupportedException("a case pattern does not close, which this does not read");
                    }

                    _ = ReadWord(false);
                }

                Deeper(() => ReadList(ListEnd.CaseClause));
            }
        }

        /// <summary>
        /// One word: its quotes read, <c>$'...'</c> decoded, a backslash escaping what follows it, a line it ends
        /// continued; a substitution - <c>$(...)</c>, a backtick, <c>&lt;(...)</c> - and a parameter's expansion kept as
        /// the text it is, so the word is the expansion it is, each substitution's commands read before it. Where it
        /// may be an assignment, <c>NAME=(...)</c> is read as the array it gives. Where its first quoted character
        /// stands is given back, or -1 where none is: a quoted word is never a reserved one, nor a quoted name an
        /// assignment's.
        /// </summary>
        private (string Word, int Quoted) ReadWord(bool mayAssign)
        {
            var word = new StringBuilder();
            var quoted = -1;
            var start = _at;

            while (_at < text.Length)
            {
                var character = text[_at];

                if (character is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or ')')
                {
                    break;
                }

                if (character is '<' or '>')
                {
                    if (_at + 1 < text.Length && text[_at + 1] == '(')
                    {
                        // A process substitution: its body runs, and the word is a file it names, whatever that is.
                        var open = _at + 1;
                        _at = open + 1;
                        Deeper(() => ReadList(ListEnd.Parenthesis));
                        word.Append(character).Append(text, open, _at - open);
                        continue;
                    }

                    break;
                }

                if (character == '(')
                {
                    if (mayAssign && quoted < 0 && word.Length > 1 && word[^1] == '=' && IsAssignment(word + "x"))
                    {
                        ReadArray(word);
                        continue;
                    }

                    break;
                }

                if (character == '\\')
                {
                    if (_at + 1 >= text.Length)
                    {
                        word.Append(character);
                        _at++;
                    }
                    else if (text[_at + 1] is '\n' or '\r')
                    {
                        SkipContinuation();
                    }
                    else
                    {
                        quoted = quoted < 0 ? word.Length : quoted;
                        word.Append(text[_at + 1]);
                        _at += 2;
                    }

                    continue;
                }

                if (character == '\'')
                {
                    var closing = text.IndexOf('\'', _at + 1);

                    if (closing < 0)
                    {
                        throw new NotSupportedException("a quote in it does not close, which this does not read");
                    }

                    quoted = quoted < 0 ? word.Length : quoted;
                    word.Append(text, _at + 1, closing - _at - 1);
                    _at = closing + 1;
                    continue;
                }

                if (character == '"')
                {
                    quoted = quoted < 0 ? word.Length : quoted;
                    ReadDoubleQuoted(word);
                    continue;
                }

                if (character == '$')
                {
                    if (_at + 1 < text.Length && text[_at + 1] == '\'')
                    {
                        quoted = quoted < 0 ? word.Length : quoted;
                        word.Append(AnsiC(text, ref _at));
                        continue;
                    }

                    if (_at + 1 < text.Length && text[_at + 1] == '"')
                    {
                        _at++;
                        quoted = quoted < 0 ? word.Length : quoted;
                        ReadDoubleQuoted(word);
                        continue;
                    }

                    ReadDollar(word);
                    continue;
                }

                if (character == '`')
                {
                    ReadBacktick(word);
                    continue;
                }

                word.Append(character);
                _at++;
            }

            if (_at == start)
            {
                throw new NotSupportedException(_at < text.Length
                    ? $"'{text[_at]}' stands where a word does, which this does not read"
                    : "it ends where a word is wanted, which this does not read");
            }

            return (word.ToString(), quoted);
        }

        /// <summary>A double-quoted stretch: a backslash escapes only a quote, a backtick, a dollar, itself and a line end; substitutions and expansions read as anywhere.</summary>
        private void ReadDoubleQuoted(StringBuilder word)
        {
            _at++;

            while (true)
            {
                if (_at >= text.Length)
                {
                    throw new NotSupportedException("a quote in it does not close, which this does not read");
                }

                var character = text[_at];

                if (character == '"')
                {
                    _at++;
                    return;
                }

                if (character == '\\' && _at + 1 < text.Length)
                {
                    if (text[_at + 1] is '\n' or '\r')
                    {
                        SkipContinuation();
                        continue;
                    }

                    if (text[_at + 1] is '"' or '`' or '$' or '\\')
                    {
                        word.Append(text[_at + 1]);
                        _at += 2;
                        continue;
                    }
                }

                if (character == '$')
                {
                    ReadDollar(word);
                    continue;
                }

                if (character == '`')
                {
                    ReadBacktick(word);
                    continue;
                }

                word.Append(character);
                _at++;
            }
        }

        /// <summary>
        /// A dollar: a command substitution (<c>$(...)</c>, an arithmetic one among them) read as the commands it runs
        /// and kept as its text; a parameter's expansion in braces kept as its text, what substitutes inside it read;
        /// any other dollar the character it is, the name after it read as the word's own.
        /// </summary>
        private void ReadDollar(StringBuilder word)
        {
            if (_at + 1 < text.Length && text[_at + 1] == '(')
            {
                var open = _at + 1;
                _at = open + 1;
                Deeper(() => ReadList(ListEnd.Parenthesis));
                word.Append('$').Append(text, open, _at - open);
                return;
            }

            if (_at + 1 < text.Length && text[_at + 1] == '{')
            {
                var start = _at;
                _at += 2;
                Deeper(ReadBraced);
                word.Append(text, start, _at - start);
                return;
            }

            word.Append('$');
            _at++;
        }

        /// <summary>A parameter's expansion in braces, to the brace that closes it: quotes, substitutions and expansions inside read as in a word.</summary>
        private void ReadBraced()
        {
            var scratch = new StringBuilder();

            while (true)
            {
                if (_at >= text.Length)
                {
                    throw new NotSupportedException("a brace in it does not close, which this does not read");
                }

                var character = text[_at];

                switch (character)
                {
                    case '}':
                        _at++;
                        return;
                    case '\\':
                        _at = Math.Min(_at + 2, text.Length);
                        break;
                    case '\'':
                        var closing = text.IndexOf('\'', _at + 1);
                        _at = closing < 0 ? throw new NotSupportedException("a quote in it does not close, which this does not read") : closing + 1;
                        break;
                    case '"':
                        ReadDoubleQuoted(scratch);
                        break;
                    case '$':
                        ReadDollar(scratch);
                        break;
                    case '`':
                        ReadBacktick(scratch);
                        break;
                    default:
                        _at++;
                        break;
                }
            }
        }

        /// <summary>A backtick substitution: its body, unescaped as the shell unescapes it, read as a script of its own; kept in the word as the substitution it is.</summary>
        private void ReadBacktick(StringBuilder word)
        {
            var body = new StringBuilder();
            var at = _at + 1;

            while (true)
            {
                if (at >= text.Length)
                {
                    throw new NotSupportedException("a backtick in it does not close, which this does not read");
                }

                if (text[at] == '`')
                {
                    break;
                }

                if (text[at] == '\\' && at + 1 < text.Length && text[at + 1] is '`' or '$' or '\\')
                {
                    body.Append(text[at + 1]);
                    at += 2;
                    continue;
                }

                body.Append(text[at]);
                at++;
            }

            _at = at + 1;
            var inner = new ShellReader(body.ToString(), _depth + 1).Read();
            _read.AddRange(inner.Select(Built));
            word.Append("$(").Append(body).Append(')');
        }

        /// <summary>An array an assignment gives, <c>NAME=(a b c)</c>: its elements read as words, none a command; kept in the word as its text.</summary>
        private void ReadArray(StringBuilder word)
        {
            var start = _at;
            _at++;

            while (true)
            {
                SkipBlanksAndLines();

                if (_at >= text.Length)
                {
                    throw new NotSupportedException("a parenthesis in it does not close, which this does not read");
                }

                if (text[_at] == ')')
                {
                    _at++;
                    word.Append(text, start, _at - start);
                    return;
                }

                if (text[_at] is ';' or '&' or '|' || AtRedirect())
                {
                    throw new NotSupportedException("an array in it does not close, which this does not read");
                }

                _ = ReadWord(false);
            }
        }

        /// <summary>
        /// A redirect: its operator (a descriptor's number before it, <c>&amp;&gt;</c> for both outputs) and the file it
        /// names, kept as one word; a duplicated descriptor names no file; a here-string's word and a here-document's
        /// lines kept as the input text they give, the document's read once its line ends.
        /// </summary>
        private void ReadRedirect(Simple command)
        {
            while (_at < text.Length && char.IsAsciiDigit(text[_at]))
            {
                _at++;
            }

            var operators = new[] { "<<<", "<<-", "<<", "<>", "<&", "<", ">>", ">|", ">&", ">", "&>>", "&>" };
            var spelled = operators.First(At);
            _at += spelled.Length;
            SkipBlanks();

            if (_at >= text.Length || text[_at] is '\n' or '\r' or ';' or '&' or '|' or ')' || AtRedirect())
            {
                throw new NotSupportedException($"a redirect '{spelled}' names nothing, which this does not read");
            }

            var (target, quoted) = ReadWord(false);

            switch (spelled)
            {
                case "<<" or "<<-":
                    command.Redirects.Add("<<<");
                    _documents.Add(new PendingDocument(command, command.Redirects.Count - 1, target, quoted >= 0, spelled == "<<-"));
                    return;
                case "<<<":
                    command.Redirects.Add("<<<" + target);
                    return;
                case "<&" or ">&" when target == "-" || target.All(char.IsAsciiDigit):
                    return;
                default:
                    command.Redirects.Add((spelled is "<&" ? "<" : spelled is ">&" or "&>" or ">|" ? ">" : spelled is "&>>" ? ">>" : spelled) + target);
                    return;
            }
        }

        /// <summary>A line end: the here-documents the line named are read from the lines after it.</summary>
        private void NewLine()
        {
            _at += At("\r\n") ? 2 : 1;
            ReadDocuments();
        }

        /// <summary>
        /// Reads each here-document the line named, in order, from the lines that follow it to its delimiter - the rest of
        /// the script where none closes it, as the shell takes it - each its command's input; a document whose delimiter
        /// was not quoted expands what substitutes in it, which runs.
        /// </summary>
        private void ReadDocuments()
        {
            foreach (var document in _documents)
            {
                var lines = new List<string>();

                while (_at < text.Length)
                {
                    var end = text.IndexOf('\n', _at);
                    var line = (end < 0 ? text[_at..] : text[_at..end]).TrimEnd('\r');
                    _at = end < 0 ? text.Length : end + 1;

                    if (document.StripsTabs)
                    {
                        line = line.TrimStart('\t');
                    }

                    if (line == document.Delimiter)
                    {
                        break;
                    }

                    lines.Add(line);
                }

                var body = string.Join('\n', lines);

                if (!document.Literal)
                {
                    ReadExpansions(body);
                }

                document.Command.Redirects[document.Place] = "<<<" + body;
            }

            _documents.Clear();
        }

        /// <summary>The substitutions of an expanding here-document's text, each read as the commands it runs; nothing else in it runs.</summary>
        private void ReadExpansions(string body)
        {
            var reader = new ShellReader(body, _depth + 1);
            var scratch = new StringBuilder();

            while (reader._at < body.Length)
            {
                switch (body[reader._at])
                {
                    case '\\':
                        reader._at = Math.Min(reader._at + 2, body.Length);
                        break;
                    case '$':
                        reader.ReadDollar(scratch);
                        break;
                    case '`':
                        reader.ReadBacktick(scratch);
                        break;
                    default:
                        reader._at++;
                        break;
                }
            }

            reader.ReadDocuments();
            _read.AddRange(reader._read);
        }

        /// <summary>Whether <paramref name="command"/> is a <c>[[</c> test whose closing <c>]]</c> is still to come.</summary>
        private static bool InTest(Simple command)
            => command.Words.Count > 0 && command.Words[0] == "[[" && command.Words[^1] != "]]";

        /// <summary>Whether the empty parentheses of a function's definition stand here: consumed where they do.</summary>
        private bool EmptyParentheses()
        {
            var at = _at;

            if (at >= text.Length || text[at] != '(')
            {
                return false;
            }

            at++;

            while (at < text.Length && text[at] is ' ' or '\t')
            {
                at++;
            }

            if (at >= text.Length || text[at] != ')')
            {
                return false;
            }

            _at = at + 1;
            return true;
        }

        /// <summary>Whether a redirect begins here: its operator, a descriptor's number before it - but no process substitution, which is a word.</summary>
        private bool AtRedirect()
        {
            var at = _at;

            while (at < text.Length && char.IsAsciiDigit(text[at]))
            {
                at++;
            }

            if (at >= text.Length)
            {
                return false;
            }

            if (text[at] == '&' && at == _at)
            {
                return at + 1 < text.Length && text[at + 1] == '>';
            }

            return text[at] is '<' or '>' && !(at + 1 < text.Length && text[at + 1] == '(');
        }

        /// <summary>Whether <paramref name="spelled"/> stands at the reader's place.</summary>
        private bool At(string spelled) => string.CompareOrdinal(text, _at, spelled, 0, spelled.Length) == 0 && _at + spelled.Length <= text.Length;

        /// <summary>Whether the unquoted word <paramref name="word"/> stands at the reader's place, whole.</summary>
        private bool AtWord(string word)
            => At(word) && (_at + word.Length >= text.Length || text[_at + word.Length] is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or ')' or '(' or '<' or '>');

        private void SkipBlanks()
        {
            while (_at < text.Length)
            {
                if (text[_at] is ' ' or '\t')
                {
                    _at++;
                }
                else if (text[_at] == '\\' && _at + 1 < text.Length && text[_at + 1] is '\n' or '\r')
                {
                    SkipContinuation();
                }
                else
                {
                    return;
                }
            }
        }

        private void SkipBlanksAndLines()
        {
            while (true)
            {
                SkipBlanks();

                if (_at < text.Length && text[_at] is '\n' or '\r')
                {
                    NewLine();
                }
                else if (_at < text.Length && text[_at] == '#')
                {
                    SkipComment();
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>A backslash and the line end after it, which continue the line: both go.</summary>
        private void SkipContinuation()
            => _at += text[_at + 1] == '\r' && _at + 2 < text.Length && text[_at + 2] == '\n' ? 3 : 2;

        /// <summary>A comment, to its line's end; the line end itself stays, a separator.</summary>
        private void SkipComment()
        {
            while (_at < text.Length && text[_at] is not ('\n' or '\r'))
            {
                _at++;
            }
        }

        /// <summary>Reads what <paramref name="read"/> reads one level deeper: refused past <see cref="Nesting"/>, so no depth of parentheses overflows the stack.</summary>
        private void Deeper(Action read)
        {
            if (++_depth > Nesting)
            {
                throw Deep();
            }

            read();
            _depth--;
        }

        private static NotSupportedException Deep() => new($"its parentheses nest past {Nesting} levels, which this does not read");

        /// <summary>A command another reader read whole, as this reader keeps one.</summary>
        private static Simple Built(IReadOnlyList<string> words)
        {
            var command = new Simple();
            command.Words.AddRange(words);
            return command;
        }
    }

    /// <summary>
    /// The text of a <c>$'...'</c> at <paramref name="at"/>, its escapes decoded as bash decodes
    /// them - the named ones, octal, hexadecimal and Unicode - and <paramref name="at"/> moved past
    /// its closing quote.
    /// </summary>
    private static string AnsiC(string script, ref int at)
    {
        var text = new StringBuilder();
        at += 2;

        while (at < script.Length && script[at] != '\'')
        {
            if (script[at] != '\\' || at + 1 >= script.Length)
            {
                text.Append(script[at++]);
                continue;
            }

            var escape = script[at + 1];
            at += 2;

            switch (escape)
            {
                case 'a': text.Append('\a'); break;
                case 'b': text.Append('\b'); break;
                case 'e' or 'E': text.Append('\u001b'); break;
                case 'f': text.Append('\f'); break;
                case 'n': text.Append('\n'); break;
                case 'r': text.Append('\r'); break;
                case 't': text.Append('\t'); break;
                case 'v': text.Append('\v'); break;
                case 'x': text.Append(Coded(script, ref at, 2, 16)); break;
                case 'u': text.Append(Coded(script, ref at, 4, 16)); break;
                case 'U': text.Append(Coded(script, ref at, 8, 16)); break;
                case 'c' when at < script.Length: text.Append((char)(script[at++] & 0x1f)); break;
                case >= '0' and <= '7':
                    at--;
                    text.Append(Coded(script, ref at, 3, 8));
                    break;
                default: text.Append(escape); break;
            }
        }

        if (at >= script.Length)
        {
            throw new NotSupportedException("a quote in it does not close, which this does not read");
        }

        at++;
        return text.ToString();
    }

    /// <summary>The character a run of up to <paramref name="most"/> digits of <paramref name="radix"/> at <paramref name="at"/> codes, the digits consumed.</summary>
    private static string Coded(string script, ref int at, int most, int radix)
    {
        var value = 0L;
        var read = 0;

        while (read < most && at < script.Length && Uri.IsHexDigit(script[at]) && Uri.FromHex(script[at]) < radix)
        {
            value = (value * radix) + Uri.FromHex(script[at]);
            at++;
            read++;
        }

        // A value no character has - past the last, or half of one - is the replacement character.
        return read == 0 ? string.Empty
            : value is > 0x10FFFF or (>= 0xD800 and <= 0xDFFF) ? "�"
            : char.ConvertFromUtf32((int)value);
    }
}
