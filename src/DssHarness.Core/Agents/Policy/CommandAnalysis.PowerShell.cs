using System.Text;

namespace DssHarness.Core.Agents.Policy;

public static partial class CommandAnalysis
{
    /// <summary>
    /// The instance methods a PowerShell expression may call, by any case - a string's, a collection's and a date's,
    /// each reading or making a value. Any other, and every static method, may start a program or touch a file no word
    /// spells, and is undecided.
    /// </summary>
    public static IReadOnlyList<string> PowerShellMethods { get; } =
    [
        "ToString", "Trim", "TrimStart", "TrimEnd", "ToLower", "ToUpper", "ToLowerInvariant", "ToUpperInvariant", "Contains",
        "StartsWith", "EndsWith", "Replace", "Split", "Substring", "IndexOf", "LastIndexOf", "IndexOfAny", "LastIndexOfAny",
        "PadLeft", "PadRight", "Insert", "Remove", "Normalize", "ToCharArray", "Equals", "CompareTo", "GetType", "GetHashCode",
        "Where", "ContainsKey", "ContainsValue", "Add", "Clear", "ToArray", "GetValue", "Item", "AddDays",
        "AddHours", "AddMinutes", "AddSeconds", "AddMonths", "AddYears", "ToShortDateString", "ToShortTimeString",
        "ToUniversalTime", "ToLocalTime",
    ];

    /// <summary>
    /// A PowerShell script spelled for the shell reader: read as PowerShell reads it (<see cref="PowerShellReader"/>), each
    /// statement written on a line of its own and each word whole and quoted - its literal text a single quote's, a variable
    /// an expansion, a parenthesized pipeline, a subexpression, a script block and a hashtable a substitution whose
    /// statements are read - so the shell reader takes the words PowerShell hands each program, and nothing of its own.
    /// </summary>
    /// <exception cref="NotSupportedException">The script holds what this does not read, or what PowerShell itself refuses to.</exception>
    private static string PowerShellAsPosix(string script) => new PowerShellReader(script).Read();

    /// <summary>
    /// PowerShell's tokenizer and grammar, as far as what a script runs goes. A statement is a command - a bareword, or the
    /// call operator's program - with its words, a keyword's statement, or an expression: an expression runs no program,
    /// its words kept as data, and an assignment's right side is a statement of its own. A word is read as PowerShell
    /// hands it: a quoted string that begins it ends it, a comma joins what follows it unspaced, a backtick escapes, a
    /// <c>#</c> where a token begins is a comment to its line's end and <c>&lt;#</c> one to its <c>#&gt;</c>, a here-string's
    /// footer stands where its line begins, and PowerShell's own blanks, typographic quotes and dashes are its own.
    /// A command's program is read as any word is, then known by its name: Invoke-Expression - a bareword, the call
    /// operator's string, a module's name before it, an escape inside it - has its one string read as a script of its own,
    /// and one an expansion names is refused by the analysis as any program so named is. What runs code no word spells - a
    /// static method, an instance method off <see cref="PowerShellMethods"/>, a class, a <c>using</c>, the stop-parsing
    /// <c>--%</c>, a script Invoke-Expression is handed by no one string - and what PowerShell refuses to read, are refused.
    /// </summary>
    private sealed partial class PowerShellReader
    {
        /// <summary>The keywords a statement may begin with, read by any case.</summary>
        private static readonly string[] Keywords =
        [
            "if", "elseif", "else", "switch", "foreach", "for", "while", "do", "until", "try", "catch", "finally", "trap",
            "function", "filter", "param", "begin", "process", "end", "dynamicparam", "clean", "return", "throw", "exit",
            "break", "continue", "class", "enum", "using", "data", "configuration", "workflow", "parallel", "sequence",
            "inlinescript",
        ];

        /// <summary>The keywords whose statements declare what this does not read.</summary>
        private static readonly string[] Unread =
            ["class", "enum", "using", "data", "configuration", "workflow", "parallel", "sequence", "inlinescript"];

        /// <summary>The word that stands in a value this cannot know - a variable's, a pipeline's output that runs nothing.</summary>
        private const string Value = "\"${PSVALUE}\"";

        private readonly string _script;
        private readonly int _nesting;
        private int _at;
        private int _depth;

        /// <param name="script">The script.</param>
        /// <param name="nesting">How many Invoke-Expression scripts this one stands in: refused past <see cref="Depth"/>, as a script gone into is.</param>
        public PowerShellReader(string script, int nesting = 0)
        {
            if (nesting > Depth)
            {
                throw new NotSupportedException($"its Invoke-Expression scripts nest past {Depth} levels, which this does not read");
            }

            if (script.Contains('\0', StringComparison.Ordinal))
            {
                throw new NotSupportedException("the PowerShell script holds a NUL, which this does not read");
            }

            _script = script;
            _nesting = nesting;
        }

        /// <summary>The script's statements, one to a line, for the shell reader.</summary>
        public string Read()
        {
            var lines = new List<string>();
            Statements(lines, '\0');
            return string.Join('\n', lines);
        }

        private bool Ended => _at >= _script.Length;

        /// <summary>The character <paramref name="ahead"/> past the reader's place, a typographic quote its plain one; '\0' past the end.</summary>
        private char At(int ahead = 0) => _at + ahead < _script.Length ? Plain(_script[_at + ahead]) : '\0';

        private static char Plain(char character) => character switch
        {
            '\u2018' or '\u2019' or '\u201A' or '\u201B' => '\'',
            '\u201C' or '\u201D' or '\u201E' => '"',
            _ => character,
        };

        /// <summary>PowerShell's blanks: its own, the no-break space and the next-line among them, and every Unicode separator past 255.</summary>
        private static bool IsBlank(char character)
            => character is ' ' or '\t' or '\f' or '\v' or '\u0085' or '\u00A0' || (character > 255 && char.IsSeparator(character));

        private static bool IsNewline(char character) => character is '\r' or '\n';

        /// <summary>PowerShell's dashes, an en dash, an em dash and a horizontal bar among them.</summary>
        private static bool IsDash(char character) => character is '-' or '\u2013' or '\u2014' or '\u2015';

        private static bool IsNameChar(char character) => char.IsLetterOrDigit(character) || character == '_';

        /// <summary>Text quoted for the shell reader, whole and literal.</summary>
        private static string Literal(string text) => "'" + text.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

        /// <summary>Passes blanks, line continuations and comments - where a token begins - and newlines too where <paramref name="newlines"/>.</summary>
        private void Skip(bool newlines)
        {
            while (!Ended)
            {
                var character = At();

                if (IsBlank(character) || (newlines && IsNewline(character)))
                {
                    _at++;
                }
                else if (character == '`' && IsNewline(At(1)))
                {
                    _at += At(1) == '\r' && At(2) == '\n' ? 3 : 2;
                }
                else if (character == '#')
                {
                    while (!Ended && !IsNewline(At()))
                    {
                        _at++;
                    }
                }
                else if (character == '<' && At(1) == '#')
                {
                    var end = _script.IndexOf("#>", _at + 2, StringComparison.Ordinal);
                    _at = end >= 0 ? end + 2 : throw new NotSupportedException("a PowerShell block comment is not closed, which PowerShell refuses to read");
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>Reads what <paramref name="read"/> reads one level deeper: refused past <see cref="Nesting"/>.</summary>
        private T Deeper<T>(Func<T> read)
        {
            if (++_depth > Nesting)
            {
                throw new NotSupportedException($"its parentheses nest past {Nesting} levels, which this does not read");
            }

            var result = read();
            _depth--;
            return result;
        }

        /// <summary>Reads statements into <paramref name="lines"/> up to <paramref name="close"/>, which it passes; '\0', to the script's end.</summary>
        private void Statements(List<string> lines, char close)
        {
            while (true)
            {
                Skip(newlines: true);

                if (At() == ';')
                {
                    _at++;
                    continue;
                }

                if (Ended)
                {
                    if (close != '\0')
                    {
                        throw new NotSupportedException($"a PowerShell block is not closed by its '{close}', which PowerShell refuses to read");
                    }

                    return;
                }

                if (At() == close)
                {
                    _at++;
                    return;
                }

                var before = _at;
                Statement(lines, close);

                if (_at == before)
                {
                    throw new NotSupportedException($"PowerShell's '{At()}' where a statement begins, which this does not read");
                }
            }
        }

        /// <summary>One statement: its pipelines, joined by <c>&amp;&amp;</c> and <c>||</c>, each a line; a job's <c>&amp;</c> after them.</summary>
        private void Statement(List<string> lines, char close)
        {
            // A loop's label (:outer foreach ...) names the statement after it.
            if (At() == ':' && IsNameChar(At(1)))
            {
                _at++;

                while (IsNameChar(At()))
                {
                    _at++;
                }

                Skip(newlines: false);
            }

            while (true)
            {
                if (Pipeline(lines, close) is { } pipeline)
                {
                    lines.Add(pipeline);
                }

                // && and || join the pipelines, across a line's end too (PowerShell reads a chain on past its newline).
                var chained = _at;
                Skip(newlines: true);

                if ((At() == '&' && At(1) == '&') || (At() == '|' && At(1) == '|'))
                {
                    _at += 2;
                    Skip(newlines: true);
                    continue;
                }

                _at = chained;
                Skip(newlines: false);

                if (At() == '&')
                {
                    _at++;
                }

                return;
            }
        }

        /// <summary>Whether the statement read ends here: its line, its separator, its block's close or the script's end.</summary>
        private bool Ends(char close)
            => Ended || IsNewline(At()) || At() is ';' or ')' or '}' or '|' or '&' || At() == close;

        /// <summary>A pipeline, its elements joined; null for a keyword's statement, whose own lines are its reading.</summary>
        private string? Pipeline(List<string> lines, char close)
        {
            if (Keyword() is { } keyword)
            {
                KeywordStatement(keyword, lines, close);
                return null;
            }

            var elements = new List<string> { CommandBegins() ? Command() : Expression(lines, close) };

            while (true)
            {
                // A pipe joins the next command, across a line's end too (PowerShell reads a pipeline on past its newline).
                var piped = _at;
                Skip(newlines: true);

                if (!(At() == '|' && At(1) != '|'))
                {
                    _at = piped;
                    break;
                }

                _at++;
                Skip(newlines: true);
                elements.Add(Command());
            }

            return string.Join(" | ", elements);
        }

        /// <summary>The keyword a statement begins with, passed; null where it begins with none.</summary>
        private string? Keyword()
        {
            var end = _at;

            while (end < _script.Length && char.IsAsciiLetter(_script[end]))
            {
                end++;
            }

            var after = end < _script.Length ? Plain(_script[end]) : '\0';

            if (end == _at || !Keywords.Contains(_script[_at..end], StringComparer.OrdinalIgnoreCase)
                || !(after == '\0' || IsBlank(after) || IsNewline(after) || after is '(' or '{' or ';' or ')' or '}'))
            {
                return null;
            }

            var keyword = _script[_at..end].ToLowerInvariant();
            _at = end;
            return keyword;
        }

        /// <summary>
        /// A keyword's statement: a <c>return</c>'s, a <c>throw</c>'s and an <c>exit</c>'s pipeline, a <c>foreach</c>'s loop
        /// pipeline, and every other's conditions and blocks, read as an expression's tokens.
        /// </summary>
        private void KeywordStatement(string keyword, List<string> lines, char close)
        {
            if (Unread.Contains(keyword))
            {
                throw new NotSupportedException($"PowerShell's '{keyword}', which this does not read");
            }

            Skip(newlines: false);

            if (keyword is "return" or "throw" or "exit")
            {
                if (!Ends(close))
                {
                    Deeper(() =>
                    {
                        Statement(lines, close);
                        return 0;
                    });
                }

                return;
            }

            if (keyword == "foreach")
            {
                Foreach(lines);
            }

            lines.Add(Expression(lines, close, assigns: false, keyword: true));
        }

        /// <summary>A <c>foreach</c>'s head: its variable set, its <c>in</c> and the pipeline it walks read to its <c>)</c>.</summary>
        private void Foreach(List<string> lines)
        {
            while (IsDash(At()))
            {
                Bare(new StringBuilder());
                Skip(newlines: false);
            }

            if (At() != '(')
            {
                return;
            }

            _at++;
            Skip(newlines: true);

            // Its variable is no statement, but it is set to each item: an environment variable's a setting of a value this
            // cannot know, a function's or an alias's code. Its 'in' and the pipeline after it are read as one.
            if (At() == '$')
            {
                var begun = _at;
                var variable = Variable();

                if (CodeVariable().IsMatch(_script[begun.._at]))
                {
                    throw CodeSetting();
                }

                if (EnvironmentName(variable) is { } name)
                {
                    lines.Add(name + "=" + Value);
                }
            }

            Deeper(() =>
            {
                Statements(lines, ')');
                return 0;
            });
        }

        /// <summary>
        /// Whether a command begins here: a bareword that is no number, the call operator, or a dot - dot-sourcing where
        /// a blank follows, a relative path (<c>./x</c>, <c>..\x</c>) otherwise - but a number's fraction (<c>.5</c>).
        /// </summary>
        private bool CommandBegins()
        {
            var character = At();

            return character switch
            {
                '&' => At(1) != '&',
                '.' => !char.IsAsciiDigit(At(1)),
                '$' or '@' or '(' or '[' or '\'' or '"' or '{' or ',' or '!' or '+' or '>' or '<' => false,
                _ when IsDash(character) => false,
                _ when char.IsAsciiDigit(character) => !IsNumber(),
                _ => !Ended,
            };
        }

        /// <summary>Whether a number begins here, ending where a token ends: digits, a fraction, an exponent, a hex, a suffix, a multiplier.</summary>
        private bool IsNumber()
        {
            var end = _at;
            var length = _script.Length;

            if (At() == '0' && At(1) is 'x' or 'X')
            {
                end += 2;

                while (end < length && char.IsAsciiHexDigit(_script[end]))
                {
                    end++;
                }
            }
            else
            {
                while (end < length && (char.IsAsciiDigit(_script[end]) || _script[end] == '.'))
                {
                    end++;
                }

                if (end < length && _script[end] is 'e' or 'E' && end + 1 < length && (char.IsAsciiDigit(_script[end + 1]) || _script[end + 1] is '+' or '-'))
                {
                    end += 2;

                    while (end < length && char.IsAsciiDigit(_script[end]))
                    {
                        end++;
                    }
                }
            }

            while (end < length && _script[end] is 'l' or 'L' or 'd' or 'D' or 'u' or 'U' or 'y' or 'Y' or 'n' or 'N' or 's' or 'S')
            {
                end++;
            }

            if (end + 1 < length && "kmgtp".Contains(char.ToLowerInvariant(_script[end]), StringComparison.Ordinal) && char.ToLowerInvariant(_script[end + 1]) == 'b')
            {
                end += 2;
            }

            return end == length || !IsNameChar(_script[end]);
        }

        /// <summary>
        /// A command: its program - a bareword, or the call operator's or dot-sourcing's value - its words and redirects;
        /// Invoke-Expression's, by whatever name it is called, its script read (<see cref="InvokeExpression"/>).
        /// </summary>
        private string Command()
        {
            var words = new List<string>();
            var redirects = new List<string>();

            if (At() == '&' || (At() == '.' && IsBlank(At(1))))
            {
                // The call operator runs the value after it, dot-sourcing too, in the caller's scope: a script block's
                // statements are the command's own.
                _at++;
                Skip(newlines: false);

                if (At() == '{')
                {
                    words.Add($"'echo' {Block()}");
                }
            }

            while (true)
            {
                Skip(newlines: false);
                var character = At();

                if (Ended || IsNewline(character) || character is ';' or '|' or '&' or ')' or '}')
                {
                    break;
                }

                if (IsDash(character) && At(1) == '-' && At(2) == '%' && (_at + 3 == _script.Length || IsBlank(At(3)) || IsNewline(At(3))))
                {
                    throw new NotSupportedException("PowerShell's stop-parsing token '--%' hands the rest of its line on as it stands, which this does not read");
                }

                if (Redirect() is { } redirect)
                {
                    redirects.Add(redirect);
                    continue;
                }

                var before = _at;
                words.Add(Word());

                if (_at == before)
                {
                    throw new NotSupportedException($"PowerShell's '{character}' among a command's words, which this does not read");
                }

                // The program read as any word is, then by its name: Invoke-Expression however it is named - a bareword,
                // the call operator's string, a module's name before it, an escape inside it - runs its one string.
                if (words.Count == 1 && NamesInvokeExpression(words[0]))
                {
                    return string.Join(' ', [InvokeExpression(), .. redirects]);
                }
            }

            if (words.Count == 0)
            {
                throw new NotSupportedException("a PowerShell command with no program, which PowerShell refuses to read");
            }

            // PowerShell's own aliases a sign spells, by the cmdlets they are: % and ? are no variable and no pattern.
            words[0] = words[0] switch
            {
                "'%'" => "'ForEach-Object'",
                "'?'" => "'Where-Object'",
                _ => words[0],
            };

            return string.Join(' ', words.Concat(redirects));
        }

        /// <summary>
        /// Whether a command's program word, as this reads it (<see cref="Word"/>), names Invoke-Expression: its literal
        /// text, by its last segment as a module-qualified name ends (<c>Microsoft.PowerShell.Utility\Invoke-Expression</c>),
        /// is <c>iex</c> or <c>Invoke-Expression</c> in any case and with any of PowerShell's dashes. A program word an
        /// expansion names is no literal, and the analysis refuses it as it refuses any program an expansion names.
        /// </summary>
        private static bool NamesInvokeExpression(string word)
            => LiteralText(word) is { } text
                && text.Replace('\\', '/').Split('/')[^1] is var name
                && string.Concat(name.Select(character => IsDash(character) ? '-' : character)) is var dashed
                && (dashed.Equals("iex", StringComparison.OrdinalIgnoreCase) || dashed.Equals("Invoke-Expression", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The text a word is, where it is literal text alone - one or more of <see cref="Literal"/>'s quoted runs and
        /// escaped quotes, glued - as the shell reader would hand it a program; null where any of it is an expansion.
        /// </summary>
        private static string? LiteralText(string word)
        {
            if (!LiteralRuns().IsMatch(word))
            {
                return null;
            }

            var text = new StringBuilder();

            foreach (System.Text.RegularExpressions.Match run in LiteralRun().Matches(word))
            {
                text.Append(run.Value == "\\'" ? "'" : run.Value[1..^1]);
            }

            return text.ToString();
        }

        /// <summary>A word that is quoted literal runs and escaped quotes alone, as <see cref="Literal"/> writes them.</summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"^(?:'[^']*'|\\')+$")]
        private static partial System.Text.RegularExpressions.Regex LiteralRuns();

        /// <summary>One quoted literal run, or one escaped quote, of a word <see cref="LiteralRuns"/> matches.</summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"'[^']*'|\\'")]
        private static partial System.Text.RegularExpressions.Regex LiteralRun();

        /// <summary>
        /// Invoke-Expression's command, its name passed: the script it runs - its -Command's value, else its one argument -
        /// a single string's text read as a script of its own, its statements a substitution the shell reader reads. A
        /// value this cannot see (a variable's, an expression's, the pipeline's), more than one, and none at all are refused.
        /// </summary>
        private string InvokeExpression()
        {
            var scripts = new List<string>();

            while (true)
            {
                Skip(newlines: false);

                if (Ended || IsNewline(At()) || At() is ';' or '|' or '&' or ')' or '}')
                {
                    break;
                }

                var before = _at;

                if (IsDash(At()))
                {
                    // An option: -Command's value is the script (glued after its ':' too); any other, read and passed.
                    _at++;
                    var name = _at;

                    while (IsNameChar(At()))
                    {
                        _at++;
                    }

                    if (_at > name && "command".StartsWith(_script[name.._at], StringComparison.OrdinalIgnoreCase))
                    {
                        if (At() == ':')
                        {
                            _at++;
                        }

                        Skip(newlines: false);
                        scripts.Add(Word());
                        continue;
                    }

                    _at = before;
                    Word();
                    continue;
                }

                scripts.Add(Word());

                if (_at == before)
                {
                    throw new NotSupportedException($"PowerShell's '{At()}' among Invoke-Expression's words, which this does not read");
                }
            }

            if (scripts.Count != 1 || !SingleQuoted().IsMatch(scripts[0]))
            {
                throw new NotSupportedException("PowerShell's Invoke-Expression runs a script no one string here spells, which this does not read");
            }

            var script = scripts[0][1..^1].Replace("'\\''", "'", StringComparison.Ordinal);
            var read = new PowerShellReader(script, _nesting + 1).Read();
            return read.Length == 0 ? "'echo'" : "'echo' $(\n" + read + "\n)";
        }

        /// <summary>A word that is one literal's text, quoted whole: what a single string, and a double one with nothing expanded, read as.</summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"^'(?:[^']|'\\'')*'$")]
        private static partial System.Text.RegularExpressions.Regex SingleQuoted();

        /// <summary>
        /// A redirect where one begins - its stream, <c>&gt;</c> or <c>&gt;&gt;</c>, and its file, <c>$null</c> the null
        /// device; or a stream merged into another - spelled as the shell spells it; null where none begins.
        /// </summary>
        private string? Redirect()
        {
            var stream = At() is '*' or (>= '1' and <= '6') && At(1) == '>' ? At() : '\0';
            var at = stream == '\0' ? 0 : 1;

            if (At(at) != '>')
            {
                return null;
            }

            var append = At(at + 1) == '>';
            var spelled = (stream is '\0' or '*' or '1' ? string.Empty : stream.ToString()) + (append ? ">>" : ">");

            if (!append && At(at + 1) == '&' && At(at + 2) is '1' or '2')
            {
                var merged = $"{(stream is '\0' or '*' ? '2' : stream)}>&{At(at + 2)}";
                _at += at + 3;
                return merged;
            }

            _at += at + (append ? 2 : 1);
            Skip(newlines: false);

            if (Ended || IsNewline(At()) || At() is ';' or '|' or '&' or ')' or '}')
            {
                throw new NotSupportedException("a PowerShell redirect names no file, which PowerShell refuses to read");
            }

            if (At() == '$' && _script.AsSpan(_at).StartsWith("$null", StringComparison.OrdinalIgnoreCase) && !IsNameChar(At(5)))
            {
                _at += 5;
                return spelled + "/dev/null";
            }

            return spelled + Word();
        }

        /// <summary>A word, as PowerShell hands it a program: its primaries, a comma joining what follows it unspaced.</summary>
        private string Word()
        {
            var word = new StringBuilder();

            if (At() == ',')
            {
                word.Append(Literal(","));
                _at++;
            }

            while (true)
            {
                if (!Ended && !IsBlank(At()) && !IsNewline(At()) && At() is not (';' or '|' or '&' or ')' or '}' or ','))
                {
                    Primary(word);
                }

                if (At() != ',')
                {
                    return word.ToString();
                }

                word.Append(Literal(","));
                _at++;

                if (Ended || IsBlank(At()) || IsNewline(At()) || At() is ';' or '|' or '&' or ')' or '}' or '#')
                {
                    return word.ToString();
                }
            }
        }

        /// <summary>One primary of a word: a string, a here-string, a value, a splat, a variable with its members, or a bareword.</summary>
        private void Primary(StringBuilder word)
        {
            switch (At())
            {
                case '\'':
                    word.Append(Single());
                    return;
                case '"':
                    word.Append(Expandable());
                    return;
                case '@' when At(1) is '\'' or '"':
                    word.Append(HereString());
                    return;
                case '@' when At(1) == '(':
                    _at++;
                    word.Append(Parenthesized());
                    Members(word);
                    return;
                case '@' when At(1) == '{':
                    word.Append(HashTable());
                    Members(word);
                    return;
                case '@' when IsNameChar(At(1)):
                    _at++;
                    word.Append(Splat());
                    return;
                case '$' when At(1) == '(':
                    _at++;
                    word.Append(Parenthesized());
                    Members(word);
                    Bare(word);
                    return;
                case '$':
                    word.Append(Variable());
                    Members(word);
                    Bare(word);
                    return;
                case '(':
                    word.Append(Parenthesized());
                    Members(word);
                    return;
                case '{':
                    word.Append(Block());
                    return;
                default:
                    Bare(word);
                    return;
            }
        }

        /// <summary>
        /// A bareword's run, into <paramref name="word"/>: its literal text with its escapes read, and the strings,
        /// variables and subexpressions glued into it, to where a token ends. A dash that begins it is PowerShell's dash.
        /// </summary>
        private void Bare(StringBuilder word)
        {
            var literal = new StringBuilder();

            while (!Ended)
            {
                var character = At();

                if (IsBlank(character) || IsNewline(character) || character is ';' or '|' or '&' or '(' or ')' or '{' or '}' or ',' or '>' or '<')
                {
                    break;
                }

                if (character == '`')
                {
                    if (_at + 1 == _script.Length || IsNewline(At(1)))
                    {
                        break;
                    }

                    literal.Append(Escaped());
                }
                else if (character is '\'' or '"')
                {
                    Flush(word, literal);
                    word.Append(character == '\'' ? Single() : Expandable());
                }
                else if (character == '$' && At(1) == '(')
                {
                    Flush(word, literal);
                    _at++;
                    word.Append(Parenthesized());
                }
                else if (character == '$' && VariableBegins(At(1)))
                {
                    Flush(word, literal);
                    word.Append(Variable());
                }
                else
                {
                    literal.Append(word.Length == 0 && literal.Length == 0 && IsDash(character) ? '-' : _script[_at]);
                    _at++;
                }
            }

            Flush(word, literal);
        }

        private static void Flush(StringBuilder word, StringBuilder literal)
        {
            if (literal.Length > 0)
            {
                word.Append(Literal(literal.ToString()));
                literal.Clear();
            }
        }

        /// <summary>A backtick's escape, passed: a sequence its character (<c>`n</c> a newline, <c>`u{67}</c> a g), any other the character itself.</summary>
        private string Escaped()
        {
            var escaped = _script[_at + 1];
            _at += 2;

            if (escaped == 'u' && At() == '{')
            {
                var end = _script.IndexOf('}', _at);

                if (end < 0 || !int.TryParse(_script.AsSpan(_at + 1, end - _at - 1), System.Globalization.NumberStyles.AllowHexSpecifier, null, out var point)
                    || point is < 0 or > 0x10FFFF || point is >= 0xD800 and <= 0xDFFF)
                {
                    throw new NotSupportedException("a PowerShell `u{...} escape names no character, which PowerShell refuses to read");
                }

                _at = end + 1;
                return char.ConvertFromUtf32(point);
            }

            return escaped switch
            {
                '0' => "\0",
                'a' => "\a",
                'b' => "\b",
                'e' => "\u001b",
                'f' => "\f",
                'n' => "\n",
                'r' => "\r",
                't' => "\t",
                'v' => "\v",
                _ => escaped.ToString(),
            };
        }

        /// <summary>A single-quoted string, passed: literal, a doubled quote one quote.</summary>
        private string Single()
        {
            _at++;
            var text = new StringBuilder();

            while (true)
            {
                if (Ended)
                {
                    throw new NotSupportedException("a PowerShell single quote is not closed, which PowerShell refuses to read");
                }

                if (At() == '\'')
                {
                    _at++;

                    if (At() != '\'')
                    {
                        return Literal(text.ToString());
                    }
                }

                text.Append(_script[_at]);
                _at++;
            }
        }

        /// <summary>A double-quoted string, passed: its text with its escapes read, a doubled quote one quote, its variables and subexpressions expansions.</summary>
        private string Expandable()
        {
            _at++;
            var word = new StringBuilder();
            var literal = new StringBuilder();

            while (true)
            {
                if (Ended)
                {
                    throw new NotSupportedException("a PowerShell double quote is not closed, which PowerShell refuses to read");
                }

                if (At() == '"')
                {
                    _at++;

                    if (At() != '"')
                    {
                        break;
                    }

                    literal.Append('"');
                    _at++;
                    continue;
                }

                Expanded(word, literal);
            }

            Flush(word, literal);
            return word.Length == 0 ? "''" : word.ToString();
        }

        /// <summary>One character of an expandable string's text, into <paramref name="word"/>: an escape, a subexpression, a variable, or itself.</summary>
        private void Expanded(StringBuilder word, StringBuilder literal)
        {
            if (At() == '`' && _at + 1 < _script.Length)
            {
                literal.Append(Escaped());
            }
            else if (At() == '$' && At(1) == '(')
            {
                Flush(word, literal);
                _at++;
                word.Append(Parenthesized());
            }
            else if (At() == '$' && VariableBegins(At(1)))
            {
                Flush(word, literal);
                word.Append(Variable());
            }
            else
            {
                literal.Append(_script[_at]);
                _at++;
            }
        }

        /// <summary>
        /// A here-string, passed: its header alone on its line, its text to the line its quote and '@ begin - a single
        /// quote's literal, a double quote's expandable, its quotes its text.
        /// </summary>
        private string HereString()
        {
            var quote = At(1);
            var line = _script.IndexOf('\n', _at + 2);

            if (line < 0 || _script[(_at + 2)..line].Any(header => !(IsBlank(header) || header == '\r')))
            {
                throw new NotSupportedException("a PowerShell here-string's header is not alone on its line, which PowerShell refuses to read");
            }

            _at = line + 1;
            var word = new StringBuilder();
            var literal = new StringBuilder();

            while (true)
            {
                if (Ended)
                {
                    throw new NotSupportedException("a PowerShell here-string is not closed, which PowerShell refuses to read");
                }

                if (_script[_at - 1] == '\n' && At() == quote && At(1) == '@')
                {
                    _at += 2;
                    break;
                }

                if (quote == '\'')
                {
                    literal.Append(_script[_at]);
                    _at++;
                }
                else
                {
                    Expanded(word, literal);
                }
            }

            // The newline before its footer is the footer's.
            var text = literal.ToString();
            literal.Clear().Append(text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text.EndsWith('\n') ? text[..^1] : text);
            Flush(word, literal);
            return word.Length == 0 ? "''" : word.ToString();
        }

        private static bool VariableBegins(char character) => IsNameChar(character) || character is '{' or '$' or '?' or '^';

        /// <summary>
        /// A variable, passed - <c>$name</c>, a scope's or a drive's (<c>$env:NAME</c>), <c>${...}</c>, <c>$$</c>,
        /// <c>$?</c>, <c>$^</c> - as an expansion the shell reader keeps as one: its name where the shell spells one alike,
        /// else <see cref="Value"/>.
        /// </summary>
        private string Variable()
        {
            _at++;
            string name;

            if (At() == '{')
            {
                var text = new StringBuilder();
                _at++;

                while (At() != '}')
                {
                    if (Ended)
                    {
                        throw new NotSupportedException("a PowerShell ${...} is not closed, which PowerShell refuses to read");
                    }

                    if (At() == '`' && _at + 1 < _script.Length)
                    {
                        _at++;
                    }

                    text.Append(_script[_at]);
                    _at++;
                }

                _at++;
                name = text.ToString();
            }
            else if (At() is '$' or '?' or '^')
            {
                name = At().ToString();
                _at++;
            }
            else
            {
                var start = _at;

                while (IsNameChar(At()))
                {
                    _at++;
                }

                // A scope's or a drive's name before its variable's: $env:PATH, $global:x.
                if (At() == ':' && At(1) != ':' && IsNameChar(At(1)))
                {
                    _at++;

                    while (IsNameChar(At()))
                    {
                        _at++;
                    }
                }

                name = _script[start.._at];

                if (name.Length == 0)
                {
                    return Literal("$");
                }
            }

            var parts = name.Split(':');
            var plain = parts.Length <= 2 && parts.All(part => part.Length > 0 && part.All(character => char.IsAsciiLetterOrDigit(character) || character == '_'));

            return name is "$" or "?" ? $"\"${name}\""
                : plain && parts.Length == 2 ? $"\"${parts[0]}:{parts[1]}\""
                : plain ? $"\"${{{name}}}\""
                : Value;
        }

        /// <summary>A splat, passed after its '@': the variable whose words it hands on, an expansion.</summary>
        private string Splat()
        {
            var start = _at;

            while (IsNameChar(At()))
            {
                _at++;
            }

            var name = _script[start.._at];
            return name.All(character => char.IsAsciiLetterOrDigit(character) || character == '_') ? $"\"${{{name}}}\"" : Value;
        }

        /// <summary>A parenthesized pipeline or subexpression, passed from its '(': a substitution of its statements.</summary>
        private string Parenthesized()
        {
            _at++;
            return Substitution(')');
        }

        /// <summary>
        /// A hashtable, passed from its '@{': each key, to its '=', an expression, and each value a statement of its own,
        /// which may run - a substitution of what they hold.
        /// </summary>
        private string HashTable()
        {
            _at += 2;
            var lines = new List<string>();

            Deeper(() =>
            {
                while (true)
                {
                    Skip(newlines: true);

                    if (At() == ';')
                    {
                        _at++;
                        continue;
                    }

                    if (Ended)
                    {
                        throw new NotSupportedException("a PowerShell hashtable is not closed by its '}', which PowerShell refuses to read");
                    }

                    if (At() == '}')
                    {
                        _at++;
                        return 0;
                    }

                    lines.Add(Expression(lines, '=', assigns: false));

                    if (At() != '=')
                    {
                        throw new NotSupportedException("a PowerShell hashtable's key stands without its '=', which PowerShell refuses to read");
                    }

                    _at++;
                    Skip(newlines: true);
                    Statement(lines, '}');
                }
            });

            return lines.Count == 0 ? Value : "$(\n" + string.Join('\n', lines) + "\n)";
        }

        /// <summary>A script block, passed from its '{': a substitution of its statements, each of which may run.</summary>
        private string Block()
        {
            _at++;
            return Substitution('}');
        }

        private string Substitution(char close)
        {
            var inner = new List<string>();

            Deeper(() =>
            {
                Statements(inner, close);
                return 0;
            });

            return inner.Count == 0 ? Value : "$(\n" + string.Join('\n', inner) + "\n)";
        }

        /// <summary>
        /// A value's members, into <paramref name="word"/>, null-conditional ones too: a property, an index, and a method's
        /// call - an instance method of <see cref="PowerShellMethods"/>, its arguments read; any other, one a string, a
        /// variable or an expression names, and every static method, refused.
        /// </summary>
        private void Members(StringBuilder word)
        {
            while (true)
            {
                // A member's operator: '.', the null-conditional '?.', or the static '::'.
                var member = At() == '.' ? 1 : (At() == '?' && At(1) == '.') || (At() == ':' && At(1) == ':') ? 2 : 0;
                var typeMember = At() == ':';
                var named = member > 0 && (char.IsLetter(At(member)) || At(member) == '_');

                if (named)
                {
                    var start = _at;
                    _at += member;

                    while (IsNameChar(At()))
                    {
                        _at++;
                    }

                    var name = _script[(start + member).._at];
                    word.Append(Literal(_script[start.._at]));

                    if (At() == '(')
                    {
                        if (typeMember || !PowerShellMethods.Contains(name, StringComparer.OrdinalIgnoreCase))
                        {
                            throw new NotSupportedException($"PowerShell's call of the {(typeMember ? "static " : string.Empty)}method '{name}', which may run what no word here spells");
                        }

                        word.Append(Arguments(')'));
                    }
                }
                else if (member > 0 && At(member) is '\'' or '"' or '$' or '(')
                {
                    // A member named by a string, a variable or an expression: any member, and its call any method.
                    _at += member;
                    word.Append(At() switch { '\'' => Single(), '"' => Expandable(), '(' => Parenthesized(), _ => Variable() });

                    if (At() == '(')
                    {
                        throw new NotSupportedException("PowerShell's call of a method a string, a variable or an expression names, which may run what no word here spells");
                    }
                }
                else if (At() == '[' || (At() == '?' && At(1) == '['))
                {
                    _at += At() == '?' ? 1 : 0;
                    word.Append(Arguments(']'));
                }
                else
                {
                    return;
                }
            }
        }

        /// <summary>
        /// A method's arguments, an attribute's or an index's, passed from its open to its <paramref name="close"/>: each an
        /// expression, the blocks and pipelines among them read - a substitution of their data and statements.
        /// </summary>
        private string Arguments(char close)
        {
            _at++;
            var lines = new List<string>();

            Deeper(() =>
            {
                while (true)
                {
                    Skip(newlines: true);

                    if (Ended)
                    {
                        throw new NotSupportedException($"a PowerShell '{close}' is missing, which PowerShell refuses to read");
                    }

                    if (At() == close)
                    {
                        _at++;
                        return 0;
                    }

                    if (At() is ',' or ';')
                    {
                        _at++;
                        continue;
                    }

                    var before = _at;
                    lines.Add(Expression(lines, close));

                    if (_at == before)
                    {
                        throw new NotSupportedException($"PowerShell's '{At()}' among an expression's, which this does not read");
                    }
                }
            });

            return lines.Count == 0 ? Value : "$(\n" + string.Join('\n', lines) + "\n)";
        }

        /// <summary>
        /// A type literal, passed: its brackets balanced, its text a literal's, its attributes' arguments - a script block
        /// among them, which a validation runs - a substitution of what they hold.
        /// </summary>
        private string Type()
        {
            var word = new StringBuilder();
            var literal = new StringBuilder();
            var depth = 0;

            do
            {
                switch (At())
                {
                    case '\0' when Ended:
                        throw new NotSupportedException("a PowerShell '[' is not closed, which PowerShell refuses to read");
                    case '[':
                        depth++;
                        literal.Append('[');
                        _at++;
                        break;
                    case ']':
                        depth--;
                        literal.Append(']');
                        _at++;
                        break;
                    case '(':
                        Flush(word, literal);
                        word.Append(Arguments(')'));
                        break;
                    case '\'':
                        Flush(word, literal);
                        word.Append(Single());
                        break;
                    case '"':
                        Flush(word, literal);
                        word.Append(Expandable());
                        break;
                    default:
                        literal.Append(_script[_at]);
                        _at++;
                        break;
                }
            }
            while (depth > 0);

            Flush(word, literal);
            return word.ToString();
        }

        /// <summary>
        /// An expression statement - or a keyword's - to its end: a line of data, <c>echo</c> and its strings and values,
        /// so each path it names is still met and each statement it holds is read; an assignment's right side read as a
        /// statement of its own, and an environment variable's setting written as the assignment it is.
        /// </summary>
        private string Expression(List<string> lines, char close, bool assigns = true, bool keyword = false)
        {
            var data = new List<string> { "'echo'" };
            var environments = new List<string>();
            var code = false;
            var primaries = 0;
            var constructed = false;

            while (true)
            {
                Skip(newlines: false);
                var character = At();

                if (Ended || IsNewline(character) || character is ';' or ')' or '}' or ']' or '|' or '&' || character == close)
                {
                    break;
                }

                // A keyword's construct ends where its parenthesis or block does: a bareword after it begins a statement of
                // its own (param($x) git push) - an else, a catch or a do's while is read as a keyword's statement too.
                if (keyword && constructed && char.IsLetter(character))
                {
                    break;
                }

                constructed = false;

                if (assigns && primaries >= 1 && Assignment() is { } assignment)
                {
                    if (code)
                    {
                        throw CodeSetting();
                    }

                    Skip(newlines: true);
                    Assigned(environments, primaries == 1, assignment, lines, close);
                    break;
                }

                if (Redirect() is { } redirect)
                {
                    data.Add(redirect);
                    continue;
                }

                switch (character)
                {
                    case '\'' or '"':
                        var text = new StringBuilder(character == '\'' ? Single() : Expandable());
                        Members(text);
                        data.Add(text.ToString());
                        primaries++;
                        break;
                    case '@' when At(1) is '\'' or '"':
                        data.Add(HereString());
                        primaries++;
                        break;
                    case '@' when At(1) == '{':
                        var table = new StringBuilder(HashTable());
                        Members(table);
                        data.Add(table.ToString());
                        primaries++;
                        constructed = true;
                        break;
                    case '@' when At(1) == '(':
                    case '$' when At(1) == '(':
                    case '(' or '{':
                        var value = new StringBuilder();
                        _at += character is '@' or '$' ? 1 : 0;
                        value.Append(At() == '(' ? Parenthesized() : Block());
                        Members(value);
                        data.Add(value.ToString());
                        primaries++;
                        constructed = true;
                        break;
                    case '$':
                        var begun = _at;
                        var named = Variable();
                        code |= CodeVariable().IsMatch(_script[begun.._at]);
                        var variable = new StringBuilder(named);
                        Members(variable);

                        // An environment variable, an assignment's target where one follows: each target is set.
                        if (EnvironmentName(named) is { } name)
                        {
                            environments.Add(name);
                        }

                        data.Add(variable.ToString());
                        primaries++;
                        break;
                    case '[':
                        var type = new StringBuilder(Type());
                        Members(type);
                        data.Add(type.ToString());
                        constructed = true;
                        break;
                    case '`':
                        _at += _at + 1 < _script.Length ? 2 : 1;
                        break;
                    default:
                        // An operator, a number, or a bareword an expression holds - a function's name, a switch's option.
                        _at++;

                        if (IsNameChar(character) || IsDash(character))
                        {
                            while (!Ended && IsNameChar(At()))
                            {
                                _at++;
                            }
                        }

                        break;
                }
            }

            return string.Join(' ', data);
        }

        /// <summary>
        /// The environment variable a variable's expansion names whole - <c>$env:NAME</c>, <c>${env:NAME}</c>, by any case -
        /// where the shell spells the name alike; else null.
        /// </summary>
        private static string? EnvironmentName(string variable)
            => variable.StartsWith("\"$env:", StringComparison.OrdinalIgnoreCase) && variable["\"$env:".Length..^1] is var name && IsAssignment(name + "=x")
                ? name
                : null;

        private static NotSupportedException CodeSetting() => new("PowerShell's setting of a function's or an alias's text, which this does not read");

        /// <summary>A variable of PowerShell's function: or alias: drive: what is set to it is code.</summary>
        [System.Text.RegularExpressions.GeneratedRegex(@"^\$\{?(?:function|alias):", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
        private static partial System.Text.RegularExpressions.Regex CodeVariable();

        /// <summary>
        /// An assignment's operator, passed: <c>+=</c> or <c>=</c>; null where none stands here. Another's sign - <c>??=</c>'s,
        /// <c>*=</c>'s - is passed as an expression's operator before its <c>=</c>, which sets the value as any does.
        /// </summary>
        private string? Assignment()
        {
            foreach (var spelled in new[] { "+=", "=" })
            {
                if (_script.AsSpan(_at).StartsWith(spelled, StringComparison.Ordinal))
                {
                    _at += spelled.Length;
                    return spelled;
                }
            }

            return null;
        }

        /// <summary>
        /// An assignment's right side, read as a statement; each environment variable among its <paramref name="targets"/>
        /// written as the setting it is too - a lone target's one word's value, else a value this cannot know.
        /// </summary>
        private void Assigned(List<string> targets, bool alone, string assignment, List<string> lines, char close)
        {
            if (alone && targets.Count == 1 && (At() is '\'' or '"' || (At() == '$' && At(1) != '(') || (At() == '@' && At(1) is '\'' or '"')))
            {
                var value = Word();
                Skip(newlines: false);

                if (Ends(close))
                {
                    lines.Add(targets[0] + assignment + value);
                    return;
                }

                // More follows the word: the target takes a value this cannot know, and the word, read once, is data
                // whose substitutions are still read - the rest of the right side read on from it.
                lines.Add(targets[0] + assignment + Value);
                lines.Add("'echo' " + value);
            }
            else
            {
                lines.AddRange(targets.Select(name => name + assignment + Value));
            }

            if (!Ends(close))
            {
                Deeper(() =>
                {
                    Statement(lines, close);
                    return 0;
                });
            }
        }
    }
}
