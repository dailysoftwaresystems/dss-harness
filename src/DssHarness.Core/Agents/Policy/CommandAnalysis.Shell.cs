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
    /// the input it gives; a redirect kept apart from the command's words as its own, its operator and the file it
    /// names; the shell's reserved words read only where a command begins and only unquoted; a <c>for</c>'s and a
    /// <c>select</c>'s head kept as a command its reserved word leads, so no word of it is a program, and a
    /// <c>case</c>'s patterns never read as commands. What the shell would refuse to read -
    /// a quote, a parenthesis or a backtick that does not close - is refused.
    /// </summary>
    /// <param name="script">The script.</param>
    /// <param name="nesting">How deep in parentheses the script stands: refused past <see cref="Nesting"/>.</param>
    private static IReadOnlyList<AnalyzedCommand> Script(string script, int nesting)
        => new ShellReader(script, nesting).Read();

    /// <summary>A descriptor bash gives a redirect of its own, its number kept in the variable named: <c>{name}</c>, right before the operator.</summary>
    [System.Text.RegularExpressions.GeneratedRegex(@"\G\{[A-Za-z_][A-Za-z0-9_]*\}")]
    private static partial System.Text.RegularExpressions.Regex NamedDescriptor();

    /// <summary>
    /// One simple command as the reader builds it: its words - each with how its characters stand to the shell, one of
    /// <see cref="WordQuoting"/> per character, so an expansion is read only where the shell would expand it - and its
    /// redirects kept apart, so a word's place is the place the program reads it at.
    /// </summary>
    private sealed class Simple
    {
        public List<string> Words { get; } = [];

        public List<string> Quoted { get; } = [];

        public List<ShellRedirect> Redirects { get; } = [];

        public bool IsEmpty => Words.Count == 0 && Redirects.Count == 0;
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

        /// <summary>
        /// Whether the reader stands where a double quote's rules hold - in one, in arithmetic, in an expanding here-document
        /// - where a single quote is a character like any, and what substitutes inside it runs.
        /// </summary>
        private bool _quoted;

        /// <summary>Whether an <c>esac</c> was read where a command begins, which ends the case clause read and the case.</summary>
        private bool _caseClosed;

        /// <summary>
        /// The places an arithmetic's reading began at and did not close as one: what it is depends on its place alone, so
        /// each is read once - nested ones that do not close would each be read again by every one around them.
        /// </summary>
        private readonly HashSet<int> _notArithmetic = [];

        public IReadOnlyList<AnalyzedCommand> Read()
        {
            if (_depth > Nesting)
            {
                throw Deep();
            }

            ReadList(ListEnd.Script);
            ReadDocuments();

            return [.. _read.Where(command => !command.IsEmpty).Select(command => new AnalyzedCommand([.. command.Words], [.. command.Redirects], [.. command.Quoted]))];
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

                if (end is ListEnd.CaseClause && (_caseClosed || AtWord("esac")))
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

        /// <summary>
        /// A subshell, read as a list of its own - or an arithmetic command, <c>((...))</c>, read as the arithmetic it is;
        /// what follows either is its redirects, and a word there names the program by what the subshell printed.
        /// </summary>
        private void ReadSubshell()
        {
            var start = _at;
            _at += 2;

            if (!At(start, "((") || !ReadArithmetic(bracket: false))
            {
                _at = start + 1;
                Deeper(() => ReadList(ListEnd.Parenthesis));
            }

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
                    command.Quoted.Add(new string(WordQuoting.Open, joined));
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
                var (word, quoted, howRead) = ReadWord(declares || command.Words.All(IsAssignment));

                // A reserved word where a command begins - unquoted, before any word but the environment's none - is the
                // shell's own, never a command's.
                if (command.Words.Count == 0 && quoted < 0)
                {
                    if (word is "for" or "select")
                    {
                        command.Words.Add(word);
                        command.Quoted.Add(howRead);
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

                    // Read here as the word it is - a line continued inside it among its spellings - it closes the case.
                    if (word == "esac" && end is ListEnd.CaseClause)
                    {
                        _caseClosed = true;
                        return;
                    }

                    if (Reserved.Contains(word))
                    {
                        if (word == "time")
                        {
                            SkipBlanks();

                            if (AtWord("-p"))
                            {
                                _at = WordEnd("-p");
                            }
                        }

                        continue;
                    }
                }

                command.Words.Add(word);
                command.Quoted.Add(howRead);
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
                head.Quoted.Clear();
                ReadSubshell();
                return;
            }

            var (name, _, howRead) = ReadWord(false);
            head.Words.Add(name);
            head.Quoted.Add(howRead);
            SkipBlanksAndLines();

            if (!AtWord("in"))
            {
                return;
            }

            _at = WordEnd("in");
            head.Words.Add("in");
            head.Quoted.Add(new string(WordQuoting.Open, 2));

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

                var (over, _, read) = ReadWord(false);
                head.Words.Add(over);
                head.Quoted.Add(read);
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

            _at = WordEnd("in");

            while (true)
            {
                SkipBlanksAndLines();

                if (_caseClosed)
                {
                    _caseClosed = false;
                    return;
                }

                if (_at >= text.Length)
                {
                    throw new NotSupportedException("a case does not close, which this does not read");
                }

                if (AtWord("esac"))
                {
                    _at = WordEnd("esac");
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
        /// assignment's. How each of its characters stands to the shell is given back with it - one of
        /// <see cref="WordQuoting"/> per character, in step - so what the shell would expand in it is read where it
        /// stands and nowhere else.
        /// </summary>
        private (string Word, int Quoted, string HowRead) ReadWord(bool mayAssign)
        {
            var word = new StringBuilder();
            var howRead = new StringBuilder();
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
                        howRead.Append(WordQuoting.Open, 1 + _at - open);
                        continue;
                    }

                    break;
                }

                if (character == '(')
                {
                    if (mayAssign && quoted < 0 && word.Length > 1 && word[^1] == '=' && IsAssignment(word + "x"))
                    {
                        ReadArray(word, howRead);
                        continue;
                    }

                    break;
                }

                if (character == '\\')
                {
                    if (_at + 1 >= text.Length)
                    {
                        word.Append(character);
                        howRead.Append(WordQuoting.Literal);
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
                        howRead.Append(WordQuoting.Literal);
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
                    howRead.Append(WordQuoting.Literal, closing - _at - 1);
                    _at = closing + 1;
                    continue;
                }

                if (character == '"')
                {
                    quoted = quoted < 0 ? word.Length : quoted;
                    ReadDoubleQuoted(word, howRead);
                    continue;
                }

                if (character == '$')
                {
                    if (_at + 1 < text.Length && text[_at + 1] == '\'')
                    {
                        quoted = quoted < 0 ? word.Length : quoted;
                        var decoded = AnsiC(text, ref _at);
                        word.Append(decoded);
                        howRead.Append(WordQuoting.Literal, decoded.Length);
                        continue;
                    }

                    if (_at + 1 < text.Length && text[_at + 1] == '"')
                    {
                        _at++;
                        quoted = quoted < 0 ? word.Length : quoted;
                        ReadDoubleQuoted(word, howRead);
                        continue;
                    }

                    ReadDollar(word, howRead);
                    continue;
                }

                if (character == '`')
                {
                    ReadBacktick(word, howRead);
                    continue;
                }

                word.Append(character);
                howRead.Append(WordQuoting.Open);
                _at++;
            }

            if (_at == start)
            {
                throw new NotSupportedException(_at < text.Length
                    ? $"'{text[_at]}' stands where a word does, which this does not read"
                    : "it ends where a word is wanted, which this does not read");
            }

            return (word.ToString(), quoted, howRead.ToString());
        }

        /// <summary>A double-quoted stretch: a backslash escapes only a quote, a backtick, a dollar, itself and a line end; substitutions and expansions read as anywhere.</summary>
        private void ReadDoubleQuoted(StringBuilder word, StringBuilder howRead)
        {
            var quoted = _quoted;
            _quoted = true;
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
                    _quoted = quoted;
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
                        howRead.Append(WordQuoting.Literal);
                        _at += 2;
                        continue;
                    }
                }

                if (character == '$')
                {
                    ReadDollar(word, howRead);
                    continue;
                }

                if (character == '`')
                {
                    ReadBacktick(word, howRead);
                    continue;
                }

                word.Append(character);
                howRead.Append(WordQuoting.DoubleQuoted);
                _at++;
            }
        }

        /// <summary>
        /// A dollar: a command substitution (<c>$(...)</c>, an arithmetic one among them) read as the commands it runs
        /// and kept as its text; a parameter's expansion in braces kept as its text, what substitutes inside it read;
        /// any other dollar the character it is, the name after it read as the word's own.
        /// </summary>
        private void ReadDollar(StringBuilder word, StringBuilder howRead)
        {
            // An arithmetic expansion, $((...)) or $[...], read as the arithmetic it is - where it closes as one.
            if (At(_at, "$((") || At(_at, "$["))
            {
                var start = _at;
                _at += text[_at + 1] == '[' ? 2 : 3;

                if (ReadArithmetic(bracket: text[start + 1] == '['))
                {
                    AppendExpansion(word, howRead, text.Substring(start, _at - start));
                    return;
                }

                _at = start;
            }

            if (_at + 1 < text.Length && text[_at + 1] == '(')
            {
                // A substitution is a script of its own, whatever quote it stands in.
                var open = _at + 1;
                var quoted = _quoted;
                _at = open + 1;
                _quoted = false;
                Deeper(() => ReadList(ListEnd.Parenthesis));
                _quoted = quoted;
                AppendExpansion(word, howRead, "$" + text.Substring(open, _at - open));
                return;
            }

            if (_at + 1 < text.Length && text[_at + 1] == '{')
            {
                var start = _at;
                _at += 2;
                Deeper(ReadBraced);
                AppendExpansion(word, howRead, text.Substring(start, _at - start));
                return;
            }

            AppendExpansion(word, howRead, "$");
            _at++;
        }

        /// <summary>
        /// An expansion's text, kept in the word as the expansion it is: how it stands to the shell is where it was
        /// read from - wholly open in the open, and inside double quotes its first character still live (a dollar or a
        /// backtick opens it) with the rest literal, for the shell expands there exactly that far.
        /// </summary>
        private void AppendExpansion(StringBuilder word, StringBuilder howRead, string spelled)
        {
            word.Append(spelled);
            howRead.Append(_quoted ? WordQuoting.DoubleQuoted : WordQuoting.Open);

            if (spelled.Length > 1)
            {
                howRead.Append(WordQuoting.Literal, spelled.Length - 1);
            }
        }

        /// <summary>
        /// The arithmetic of a <c>$((...))</c>, a <c>((...))</c> or a <c>$[...]</c>, from after its opening to past its close:
        /// its substitutions and expansions read as anywhere, nothing else in it a command, a redirect or a here-document -
        /// its <c>&lt;&lt;</c> shifts. Its close is found as the shell finds it, a quote's parenthesis none; its text is
        /// read as a double quote's, so a single quote's substitution runs. Nested past <see cref="Nesting"/>, refused.
        /// False, with nothing of it read, where it does not close as arithmetic (<c>$( (a) )</c> is a substitution of a
        /// subshell), as the shell falls back so too.
        /// </summary>
        private bool ReadArithmetic(bool bracket)
        {
            if (_notArithmetic.Contains(_at))
            {
                return false;
            }

            if (++_depth > Nesting)
            {
                throw Deep();
            }

            // A line end inside a substitution reads the documents pending before it: the fall-back reads them again.
            var (at, read, pending, quoted, depth, single) = (_at, _read.Count, _documents.ToList(), _quoted, 0, false);
            var scratch = new StringBuilder();
            var scratchHowRead = new StringBuilder();
            _quoted = true;

            while (_at < text.Length)
            {
                var character = text[_at];

                if (character == '\'')
                {
                    single = !single;
                    _at++;
                }
                else if (character == '$')
                {
                    ReadDollar(scratch, scratchHowRead);
                }
                else if (character == '`')
                {
                    ReadBacktick(scratch, scratchHowRead);
                }
                else if (single)
                {
                    _at++;
                }
                else if (character == (bracket ? '[' : '('))
                {
                    depth++;
                    _at++;
                }
                else if (character == (bracket ? ']' : ')'))
                {
                    if (depth > 0)
                    {
                        depth--;
                        _at++;
                    }
                    else if (bracket || At(_at, "))"))
                    {
                        _at += bracket ? 1 : 2;
                        (_quoted, _depth) = (quoted, _depth - 1);
                        return true;
                    }
                    else
                    {
                        break;
                    }
                }
                else if (character == '"')
                {
                    ReadDoubleQuoted(scratch, scratchHowRead);
                }
                else
                {
                    _at += character == '\\' ? 2 : 1;
                }
            }

            (_quoted, _depth) = (quoted, _depth - 1);
            _notArithmetic.Add(at);
            _at = at;
            _read.RemoveRange(read, _read.Count - read);
            _documents.Clear();
            _documents.AddRange(pending);
            return false;
        }

        /// <summary>Whether <paramref name="spelled"/> stands at <paramref name="at"/>.</summary>
        private bool At(int at, string spelled) => at + spelled.Length <= text.Length && string.CompareOrdinal(text, at, spelled, 0, spelled.Length) == 0;

        /// <summary>A parameter's expansion in braces, to the brace that closes it: quotes, substitutions and expansions inside read as in a word.</summary>
        private void ReadBraced()
        {
            var scratch = new StringBuilder();
            var scratchHowRead = new StringBuilder();

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
                    case '\'' when !_quoted:
                        var closing = text.IndexOf('\'', _at + 1);
                        _at = closing < 0 ? throw new NotSupportedException("a quote in it does not close, which this does not read") : closing + 1;
                        break;
                    case '"':
                        ReadDoubleQuoted(scratch, scratchHowRead);
                        break;
                    case '$':
                        ReadDollar(scratch, scratchHowRead);
                        break;
                    case '`':
                        ReadBacktick(scratch, scratchHowRead);
                        break;
                    default:
                        _at++;
                        break;
                }
            }
        }

        /// <summary>A backtick substitution: its body, unescaped as the shell unescapes it, read as a script of its own; kept in the word as the substitution it is.</summary>
        private void ReadBacktick(StringBuilder word, StringBuilder howRead)
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
            AppendExpansion(word, howRead, "$(" + body + ")");
        }

        /// <summary>An array an assignment gives, <c>NAME=(a b c)</c>: its elements read as words, none a command; kept in the word as its text.</summary>
        private void ReadArray(StringBuilder word, StringBuilder howRead)
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
                    howRead.Append(WordQuoting.Open, _at - start);
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
        /// names, kept as the command's redirect with the descriptor it sets; a duplicated descriptor names no file, and is
        /// kept only where it is duplicated onto the input, which a shell may read its script from; a here-string's word and a here-document's
        /// lines kept as the input text they give, the document's read once its line ends.
        /// </summary>
        private void ReadRedirect(Simple command)
        {
            var (operatorAt, given) = RedirectHere() ?? throw new InvalidOperationException("no redirect begins here");
            _at = operatorAt;
            var operators = new[] { "<<<", "<<-", "<<", "<>", "<&", "<", ">>", ">|", ">&", ">", "&>>", "&>" };
            var spelled = operators.First(At);
            _at += spelled.Length;
            SkipBlanks();

            if (_at >= text.Length || text[_at] is '\n' or '\r' or ';' or '&' or '|' or ')' || AtRedirect())
            {
                throw new NotSupportedException($"a redirect '{spelled}' names nothing, which this does not read");
            }

            var (target, quoted, _) = ReadWord(false);
            var descriptor = given ?? (spelled[0] == '<' ? 0 : 1);
            var source = target.EndsWith('-') ? target[..^1] : target;

            switch (spelled)
            {
                case "<<" or "<<-":
                    command.Redirects.Add(new ShellRedirect(descriptor, "<<<", string.Empty));
                    _documents.Add(new PendingDocument(command, command.Redirects.Count - 1, target, quoted >= 0, spelled == "<<-"));
                    return;
                case "<<<":
                    command.Redirects.Add(new ShellRedirect(descriptor, "<<<", target));
                    return;
                case "<&" or ">&" when source.All(char.IsAsciiDigit):
                    if (descriptor == 0 && source.Length > 0)
                    {
                        command.Redirects.Add(new ShellRedirect(0, "<&", source));
                    }

                    return;
                default:
                    command.Redirects.Add(new ShellRedirect(descriptor, spelled is "<&" ? "<" : spelled is ">&" or "&>" or ">|" ? ">" : spelled is "&>>" ? ">>" : spelled, target));
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

                document.Command.Redirects[document.Place] = document.Command.Redirects[document.Place] with { Target = body };
            }

            _documents.Clear();
        }

        /// <summary>The substitutions of an expanding here-document's text, each read as the commands it runs; nothing else in it runs.</summary>
        private void ReadExpansions(string body)
        {
            var reader = new ShellReader(body, _depth + 1) { _quoted = true };
            var scratch = new StringBuilder();
            var scratchHowRead = new StringBuilder();

            while (reader._at < body.Length)
            {
                switch (body[reader._at])
                {
                    case '\\':
                        reader._at = Math.Min(reader._at + 2, body.Length);
                        break;
                    case '$':
                        reader.ReadDollar(scratch, scratchHowRead);
                        break;
                    case '`':
                        reader.ReadBacktick(scratch, scratchHowRead);
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

        /// <summary>Whether a redirect begins here: its operator, a descriptor before it - but no process substitution, which is a word.</summary>
        private bool AtRedirect() => RedirectHere() is not null;

        /// <summary>
        /// Where the operator of a redirect beginning here stands, and the descriptor before it - a number an int holds,
        /// or a <c>{name}</c> the shell gives one of its own (<see cref="int.MaxValue"/> here); null where no redirect
        /// begins here, as where digits past an int's reach are a word, as bash reads them.
        /// </summary>
        private (int Operator, int? Descriptor)? RedirectHere()
        {
            var at = _at;
            int? descriptor = null;

            if (NamedDescriptor().Match(text, at) is { Success: true } named)
            {
                at += named.Length;
                descriptor = int.MaxValue;
            }
            else
            {
                while (at < text.Length && char.IsAsciiDigit(text[at]))
                {
                    at++;
                }

                if (at > _at)
                {
                    if (!int.TryParse(text.AsSpan(_at, at - _at), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number))
                    {
                        return null;
                    }

                    descriptor = number;
                }
            }

            if (at >= text.Length)
            {
                return null;
            }

            if (text[at] == '&' && at == _at)
            {
                return at + 1 < text.Length && text[at + 1] == '>' ? (at, null) : null;
            }

            return text[at] is '<' or '>' && !(at + 1 < text.Length && text[at + 1] == '(') ? (at, descriptor) : null;
        }

        /// <summary>Whether <paramref name="spelled"/> stands at the reader's place.</summary>
        private bool At(string spelled) => string.CompareOrdinal(text, _at, spelled, 0, spelled.Length) == 0 && _at + spelled.Length <= text.Length;

        /// <summary>Whether the unquoted word <paramref name="word"/> stands at the reader's place, whole.</summary>
        private bool AtWord(string word) => WordEnd(word) >= 0;

        /// <summary>
        /// Where the unquoted word <paramref name="word"/> standing whole at the reader's place ends - a line continued
        /// inside it or after it, which the shell joins before it reads the word, passed over - or -1 where it does not.
        /// </summary>
        private int WordEnd(string word)
        {
            var at = _at;

            foreach (var letter in word)
            {
                at += Continuation(at);

                if (at >= text.Length || text[at] != letter)
                {
                    return -1;
                }

                at++;
            }

            at += Continuation(at);

            return at >= text.Length || text[at] is ' ' or '\t' or '\n' or '\r' or ';' or '&' or '|' or ')' or '(' or '<' or '>' ? at : -1;
        }

        /// <summary>How many characters the continued lines at <paramref name="at"/> take: each a backslash and the line end after it.</summary>
        private int Continuation(int at)
        {
            var start = at;

            while (at + 1 < text.Length && text[at] == '\\' && (text[at + 1] == '\n' || (text[at + 1] == '\r' && at + 2 < text.Length && text[at + 2] == '\n')))
            {
                at += text[at + 1] == '\n' ? 2 : 3;
            }

            return at - start;
        }

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
        private static Simple Built(AnalyzedCommand read)
        {
            var command = new Simple();
            command.Words.AddRange(read.Words);
            command.Quoted.AddRange(read.Quoted ?? []);
            command.Redirects.AddRange(read.Redirects);
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
