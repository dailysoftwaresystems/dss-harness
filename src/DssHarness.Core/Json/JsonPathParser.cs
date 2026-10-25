using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DssHarness.Core.Json;

/// <summary>
/// Reads a JSONPath query by RFC 9535's grammar, character by character, and checks the types of its function calls as it
/// goes (the standard's "well-typedness"): a query that reads is one the evaluator can answer for any document.
/// </summary>
internal sealed class JsonPathParser
{
    /// <summary>The largest index or slice bound the standard takes: 2^53 - 1, the integers a double holds exactly.</summary>
    private const long MostInteger = (1L << 53) - 1;

    /// <summary>Each function the standard defines: what it takes, and the type of what it answers.</summary>
    private static readonly Dictionary<string, (JsonPathType[] Takes, JsonPathType Answers)> Functions = new(StringComparer.Ordinal)
    {
        ["length"] = ([JsonPathType.Value], JsonPathType.Value),
        ["count"] = ([JsonPathType.Nodes], JsonPathType.Value),
        ["match"] = ([JsonPathType.Value, JsonPathType.Value], JsonPathType.Logical),
        ["search"] = ([JsonPathType.Value, JsonPathType.Value], JsonPathType.Logical),
        ["value"] = ([JsonPathType.Nodes], JsonPathType.Value),
    };

    private readonly string _text;
    private int _at;

    private JsonPathParser(string text) => _text = text;

    /// <summary>Reads <paramref name="text"/>, a query from <c>@</c> where <paramref name="relative"/>, else from <c>$</c>.</summary>
    /// <exception cref="JsonPathException">It is no query, or a function in it is not given what it takes.</exception>
    public static JsonPathQuery Read(string text, bool relative)
    {
        ArgumentNullException.ThrowIfNull(text);

        var parser = new JsonPathParser(text);
        var root = relative ? '@' : '$';

        if (!parser.At(root))
        {
            throw parser.Fail($"a query begins with '{root}'");
        }

        var query = parser.ReadQuery();

        if (parser._at < text.Length)
        {
            throw parser.Fail(char.IsWhiteSpace(text[parser._at]) && text.AsSpan(parser._at).IsWhiteSpace()
                ? "a query ends with its last segment, and no blank follows it"
                : "a segment begins with '.', '..' or '['");
        }

        return query;
    }

    /// <summary>A query: its root, <c>$</c> or <c>@</c>, then its segments.</summary>
    private JsonPathQuery ReadQuery()
    {
        var relative = _text[_at] == '@';
        _at++;

        var segments = new List<JsonPathSegment>();

        while (true)
        {
            var before = _at;
            SkipBlank();

            if (At('[') || At('.'))
            {
                segments.Add(ReadSegment());
                continue;
            }

            _at = before;
            return new JsonPathQuery(relative, segments);
        }
    }

    private JsonPathSegment ReadSegment()
    {
        if (AtText(".."))
        {
            _at += 2;

            if (At('['))
            {
                return new JsonPathSegment(true, ReadBracketed().Selectors, Singular: false);
            }

            if (At('*'))
            {
                _at++;
                return new JsonPathSegment(true, [new JsonPathSelector.Wildcard()], Singular: false);
            }

            return AtNameFirst()
                ? new JsonPathSegment(true, [new JsonPathSelector.Name(ReadShorthand())], Singular: false)
                : throw Fail("'..' is followed by a name, '*' or '['");
        }

        if (At('.'))
        {
            _at++;

            if (At('*'))
            {
                _at++;
                return new JsonPathSegment(false, [new JsonPathSelector.Wildcard()], Singular: false);
            }

            return AtNameFirst()
                ? new JsonPathSegment(false, [new JsonPathSelector.Name(ReadShorthand())], Singular: true)
                : throw Fail("'.' is followed by a name or '*'");
        }

        var (selectors, blank) = ReadBracketed();

        // A singular query's bracketed segment holds one name or one index and no blank: the grammar's name-segment and
        // index-segment, which take no blank inside the brackets as a bracketed selection does.
        return new JsonPathSegment(false, selectors, Singular: !blank && selectors is [JsonPathSelector.Name or JsonPathSelector.Index]);
    }

    /// <summary>A bracketed selection, and whether a blank stood anywhere inside its brackets.</summary>
    private (IReadOnlyList<JsonPathSelector> Selectors, bool Blank) ReadBracketed()
    {
        _at++;

        var blank = SkipBlank();
        var selectors = new List<JsonPathSelector>();

        while (true)
        {
            selectors.Add(ReadSelector());
            blank |= SkipBlank();

            if (At(','))
            {
                _at++;
                blank |= SkipBlank();
                continue;
            }

            if (At(']'))
            {
                _at++;
                return (selectors, blank);
            }

            throw Fail("a selector is followed by ',' or ']'");
        }
    }

    private JsonPathSelector ReadSelector()
    {
        if (At('\'') || At('"'))
        {
            return new JsonPathSelector.Name(ReadString());
        }

        if (At('*'))
        {
            _at++;
            return new JsonPathSelector.Wildcard();
        }

        if (At('?'))
        {
            _at++;
            SkipBlank();
            return new JsonPathSelector.Filter(ReadLogicalOr());
        }

        return ReadIndexOrSlice();
    }

    /// <summary>An index, or a slice: <c>[start S] ":" S [end S] [":" [S step]]</c>.</summary>
    private JsonPathSelector ReadIndexOrSlice()
    {
        var start = ReadInteger();
        var afterStart = _at;
        SkipBlank();

        if (!At(':'))
        {
            _at = afterStart;

            return start is { } index
                ? new JsonPathSelector.Index(index)
                : throw Fail("a selector is a name in quotes, '*', an index, a slice or a filter ('?')");
        }

        _at++;
        SkipBlank();

        var end = ReadInteger();
        var afterEnd = _at;
        SkipBlank();

        if (!At(':'))
        {
            _at = afterEnd;
            return new JsonPathSelector.Slice(start, end, null);
        }

        _at++;
        SkipBlank();

        return new JsonPathSelector.Slice(start, end, ReadInteger());
    }

    /// <summary>An integer as an index or a slice takes one - no leading zero, no <c>-0</c>, within 2^53 - 1 - or none where none begins here.</summary>
    private long? ReadInteger()
    {
        var start = _at;
        var digits = At('-') ? _at + 1 : _at;

        if (digits >= _text.Length || !char.IsAsciiDigit(_text[digits]))
        {
            if (digits > start)
            {
                throw Fail("'-' is followed by a digit");
            }

            return null;
        }

        _at = digits;

        if (_text[_at] == '0')
        {
            _at++;

            if (digits > start)
            {
                throw Fail("'-0' is no index");
            }

            if (AtDigit())
            {
                throw Fail("an integer has no leading zero");
            }

            return 0;
        }

        while (AtDigit())
        {
            _at++;
        }

        return long.TryParse(_text.AsSpan(start, _at - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && Math.Abs(value) <= MostInteger
            ? value
            : throw Fail($"'{_text[start.._at]}' is past the largest integer a query takes, 2^53 - 1");
    }

    /// <summary>A string literal in single or double quotes, its escapes read.</summary>
    private string ReadString()
    {
        var quote = _text[_at];
        var builder = new StringBuilder();
        _at++;

        while (true)
        {
            if (_at >= _text.Length)
            {
                throw Fail("a string is not closed");
            }

            var character = _text[_at];

            if (character == quote)
            {
                _at++;
                return builder.ToString();
            }

            if (character == '\\')
            {
                ReadEscape(quote, builder);
                continue;
            }

            if (character < 0x20)
            {
                throw Fail("a control character in a string is written as an escape");
            }

            ReadScalar(builder);
        }
    }

    /// <summary>One escape of a string in <paramref name="quote"/>s, into <paramref name="builder"/>.</summary>
    private void ReadEscape(char quote, StringBuilder builder)
    {
        _at++;

        if (_at >= _text.Length)
        {
            throw Fail("a string is not closed");
        }

        var escaped = _text[_at];
        _at++;

        switch (escaped)
        {
            case 'b': builder.Append('\b'); return;
            case 'f': builder.Append('\f'); return;
            case 'n': builder.Append('\n'); return;
            case 'r': builder.Append('\r'); return;
            case 't': builder.Append('\t'); return;
            case '/': builder.Append('/'); return;
            case '\\': builder.Append('\\'); return;
            case '\'' when quote == '\'': builder.Append('\''); return;
            case '"' when quote == '"': builder.Append('"'); return;
            case 'u': break;
            default:
                _at--;
                throw Fail($"'\\{escaped}' is no escape of a string in {quote}");
        }

        var unit = ReadHex();

        if (char.IsLowSurrogate(unit))
        {
            throw Fail("a low surrogate escaped stands after a high one");
        }

        if (!char.IsHighSurrogate(unit))
        {
            builder.Append(unit);
            return;
        }

        if (!AtText("\\u"))
        {
            throw Fail("a high surrogate escaped is followed by a low one, escaped");
        }

        _at += 2;

        var low = ReadHex();

        if (!char.IsLowSurrogate(low))
        {
            throw Fail("a high surrogate escaped is followed by a low one, escaped");
        }

        builder.Append(unit).Append(low);
    }

    /// <summary>Four hexadecimal digits, the code unit they name.</summary>
    private char ReadHex()
    {
        if (_at + 4 > _text.Length || !ushort.TryParse(_text.AsSpan(_at, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var unit))
        {
            throw Fail("'\\u' is followed by four hexadecimal digits");
        }

        _at += 4;

        return (char)unit;
    }

    /// <summary>One character as written, a pair of surrogates whole; a surrogate alone is no character.</summary>
    private void ReadScalar(StringBuilder builder)
    {
        var character = _text[_at];

        if (char.IsHighSurrogate(character) && _at + 1 < _text.Length && char.IsLowSurrogate(_text[_at + 1]))
        {
            builder.Append(character).Append(_text[_at + 1]);
            _at += 2;
            return;
        }

        if (char.IsSurrogate(character))
        {
            throw Fail("a surrogate stands alone");
        }

        builder.Append(character);
        _at++;
    }

    /// <summary>A member's name written after a dot: a letter, <c>_</c> or a character past ASCII, then those or digits.</summary>
    private string ReadShorthand()
    {
        var builder = new StringBuilder();

        while (_at < _text.Length)
        {
            var character = _text[_at];

            if (char.IsAsciiLetter(character) || character == '_' || (char.IsAsciiDigit(character) && builder.Length > 0) || character >= 0x80)
            {
                ReadScalar(builder);
                continue;
            }

            break;
        }

        return builder.ToString();
    }

    private JsonPathLogical ReadLogicalOr()
    {
        var parts = new List<JsonPathLogical> { ReadLogicalAnd() };

        while (true)
        {
            var before = _at;
            SkipBlank();

            if (!AtText("||"))
            {
                _at = before;
                return parts.Count == 1 ? parts[0] : new JsonPathLogical.Or(parts);
            }

            _at += 2;
            SkipBlank();
            parts.Add(ReadLogicalAnd());
        }
    }

    private JsonPathLogical ReadLogicalAnd()
    {
        var parts = new List<JsonPathLogical> { ReadBasic() };

        while (true)
        {
            var before = _at;
            SkipBlank();

            if (!AtText("&&"))
            {
                _at = before;
                return parts.Count == 1 ? parts[0] : new JsonPathLogical.And(parts);
            }

            _at += 2;
            SkipBlank();
            parts.Add(ReadBasic());
        }
    }

    /// <summary>A parenthesized expression, a test or a comparison - each of the first two negated by a leading <c>!</c>.</summary>
    private JsonPathLogical ReadBasic()
    {
        if (At('!'))
        {
            _at++;
            SkipBlank();

            return new JsonPathLogical.Not(At('(') ? ReadParenthesized() : AsTest(ReadOperand()));
        }

        if (At('('))
        {
            return ReadParenthesized();
        }

        var left = ReadOperand();
        var before = _at;
        SkipBlank();

        if (ReadComparisonOperator() is not { } comparison)
        {
            _at = before;
            return AsTest(left);
        }

        SkipBlank();

        return new JsonPathLogical.Comparison(AsComparable(left), comparison, AsComparable(ReadOperand()));
    }

    private JsonPathLogical ReadParenthesized()
    {
        _at++;
        SkipBlank();

        var inner = ReadLogicalOr();
        SkipBlank();

        if (!At(')'))
        {
            throw Fail("a parenthesized expression is closed by ')'");
        }

        _at++;

        return inner;
    }

    /// <summary>A query, a literal or a function's call: what a comparison compares, a test tests, or a function is given.</summary>
    private Operand ReadOperand()
    {
        if (_at >= _text.Length)
        {
            throw Fail("a query, a literal or a function's call is missing");
        }

        var character = _text[_at];

        if (character is '@' or '$')
        {
            return new Operand.Query(ReadQuery());
        }

        if (character is '\'' or '"')
        {
            return new Operand.Literal(JsonSerializer.SerializeToElement(ReadString()));
        }

        if (character == '-' || char.IsAsciiDigit(character))
        {
            return new Operand.Literal(ReadNumber());
        }

        if (!char.IsAsciiLetterLower(character))
        {
            throw Fail("a query, a literal or a function's call is expected");
        }

        var start = _at;

        while (_at < _text.Length && (char.IsAsciiLetterLower(_text[_at]) || char.IsAsciiDigit(_text[_at]) || _text[_at] == '_'))
        {
            _at++;
        }

        var name = _text[start.._at];

        if (At('('))
        {
            return new Operand.Function(ReadFunction(name, start));
        }

        return name switch
        {
            "true" => new Operand.Literal(JsonSerializer.SerializeToElement(true)),
            "false" => new Operand.Literal(JsonSerializer.SerializeToElement(false)),
            "null" => new Operand.Literal(JsonSerializer.SerializeToElement<object?>(null)),
            _ => throw Fail($"'{name}' is no literal, and no function's call: a call is its name and '(' with no blank between"),
        };
    }

    /// <summary>A number literal: <c>(int / "-0") [frac] [exp]</c>, as JSON writes one.</summary>
    private JsonElement ReadNumber()
    {
        var start = _at;

        if (At('-'))
        {
            _at++;
        }

        if (!AtDigit())
        {
            throw Fail("'-' is followed by a digit");
        }

        if (_text[_at] == '0')
        {
            _at++;

            if (AtDigit())
            {
                throw Fail("a number has no leading zero");
            }
        }
        else
        {
            while (AtDigit())
            {
                _at++;
            }
        }

        if (At('.'))
        {
            _at++;
            ReadDigits("'.' in a number is followed by a digit");
        }

        if (At('e') || At('E'))
        {
            _at++;

            if (At('+') || At('-'))
            {
                _at++;
            }

            ReadDigits("an exponent is a digit or more");
        }

        using var number = JsonDocument.Parse(_text.AsMemory(start, _at - start));
        return number.RootElement.Clone();
    }

    private void ReadDigits(string missing)
    {
        if (!AtDigit())
        {
            throw Fail(missing);
        }

        while (AtDigit())
        {
            _at++;
        }
    }

    /// <summary>A function's call, from its <c>(</c>: its arguments read and each held to the type the function takes.</summary>
    private JsonPathFunction ReadFunction(string name, int start)
    {
        if (!Functions.TryGetValue(name, out var function))
        {
            _at = start;
            throw Fail($"'{name}' is no function: {string.Join(", ", Functions.Keys)} are");
        }

        _at++;
        SkipBlank();

        var arguments = new List<JsonPathArgument>();

        if (!At(')'))
        {
            while (true)
            {
                arguments.Add(ReadArgument());
                SkipBlank();

                if (At(','))
                {
                    _at++;
                    SkipBlank();
                    continue;
                }

                if (At(')'))
                {
                    break;
                }

                throw Fail("an argument is followed by ',' or ')'");
            }
        }

        _at++;

        if (arguments.Count != function.Takes.Length)
        {
            throw Fail($"'{name}' takes {function.Takes.Length} argument{(function.Takes.Length == 1 ? string.Empty : "s")}, and is given {arguments.Count}");
        }

        for (var at = 0; at < arguments.Count; at++)
        {
            if (Mismatch(arguments[at], function.Takes[at]) is { } problem)
            {
                throw Fail($"'{name}' is given {problem}");
            }
        }

        return new JsonPathFunction(name, arguments, function.Answers);
    }

    /// <summary>
    /// One argument: a literal, a query or a function's call alone, else a logical expression - which one that is followed
    /// by a comparison, <c>&amp;&amp;</c> or <c>||</c>, or begins with <c>!</c> or <c>(</c>, is.
    /// </summary>
    private JsonPathArgument ReadArgument()
    {
        var start = _at;

        if (!At('!') && !At('('))
        {
            var operand = ReadOperand();
            var after = _at;
            SkipBlank();

            if (!AtComparisonOperator() && !AtText("&&") && !AtText("||"))
            {
                _at = after;

                return operand switch
                {
                    Operand.Literal literal => new JsonPathArgument.Literal(literal.Value),
                    Operand.Query query => new JsonPathArgument.Query(query.Of),
                    Operand.Function function => new JsonPathArgument.Function(function.Call),
                    _ => throw new InvalidOperationException("An operand is a literal, a query or a function's call."),
                };
            }
        }

        _at = start;
        return new JsonPathArgument.Logical(ReadLogicalOr());
    }

    /// <summary>Why <paramref name="argument"/> is not of <paramref name="type"/>, or <see langword="null"/> where it is.</summary>
    private static string? Mismatch(JsonPathArgument argument, JsonPathType type) => (type, argument) switch
    {
        (JsonPathType.Value, JsonPathArgument.Literal) => null,
        (JsonPathType.Value, JsonPathArgument.Query query) => query.Of.IsSingular ? null : "a query that may select several values where it takes one value",
        (JsonPathType.Value, JsonPathArgument.Function function) => function.Call.Result == JsonPathType.Value ? null : $"'{function.Call.Name}()', which answers no value, where it takes a value",
        (JsonPathType.Value, JsonPathArgument.Logical) => "a logical expression where it takes a value",
        (JsonPathType.Nodes, JsonPathArgument.Query) => null,
        (JsonPathType.Nodes, JsonPathArgument.Function function) => function.Call.Result == JsonPathType.Nodes ? null : $"'{function.Call.Name}()', which answers no list of values, where it takes a query",
        (JsonPathType.Nodes, _) => "no query where it takes one",
        (JsonPathType.Logical, JsonPathArgument.Literal) => "a literal where it takes a logical expression",
        (JsonPathType.Logical, JsonPathArgument.Function function) => function.Call.Result == JsonPathType.Value ? $"'{function.Call.Name}()', which answers a value, where it takes a logical expression" : null,
        _ => null,
    };

    /// <summary>A test: a query that selects anything, or a function whose answer is logical or a list of values.</summary>
    private JsonPathLogical AsTest(Operand operand) => operand switch
    {
        Operand.Query query => new JsonPathLogical.Exists(query.Of),
        Operand.Function { Call.Result: JsonPathType.Value } function => throw Fail($"'{function.Call.Name}()' answers a value, which is compared, never tested"),
        Operand.Function function => new JsonPathLogical.Test(function.Call),
        _ => throw Fail("a literal is compared, never tested"),
    };

    /// <summary>What a comparison compares: a literal, a singular query, or a function whose answer is a value.</summary>
    private JsonPathComparable AsComparable(Operand operand) => operand switch
    {
        Operand.Literal literal => new JsonPathComparable.Literal(literal.Value),
        Operand.Query { Of.IsSingular: true } query => new JsonPathComparable.Query(query.Of),
        Operand.Query => throw Fail("a query compared selects one value at most: names and indexes alone, no blank inside a bracket"),
        Operand.Function { Call.Result: JsonPathType.Value } function => new JsonPathComparable.Function(function.Call),
        Operand.Function function => throw Fail($"'{function.Call.Name}()' answers no value, and is tested, never compared"),
        _ => throw new InvalidOperationException("An operand is a literal, a query or a function's call."),
    };

    private string? ReadComparisonOperator()
    {
        foreach (var comparison in new[] { "==", "!=", "<=", ">=", "<", ">" })
        {
            if (AtText(comparison))
            {
                _at += comparison.Length;
                return comparison;
            }
        }

        return null;
    }

    private bool AtComparisonOperator() => AtText("==") || AtText("!=") || At('<') || At('>');

    /// <summary>Passes the blanks the grammar takes - space, tab, line feed, carriage return - and says whether there were any.</summary>
    private bool SkipBlank()
    {
        var start = _at;

        while (_at < _text.Length && _text[_at] is ' ' or '\t' or '\n' or '\r')
        {
            _at++;
        }

        return _at > start;
    }

    private bool At(char character) => _at < _text.Length && _text[_at] == character;

    private bool AtText(string text) => _at + text.Length <= _text.Length && _text.AsSpan(_at, text.Length).SequenceEqual(text);

    private bool AtDigit() => _at < _text.Length && char.IsAsciiDigit(_text[_at]);

    /// <summary>Whether a member's name may begin here: a letter, <c>_</c>, or a character past ASCII.</summary>
    private bool AtNameFirst() => _at < _text.Length && (char.IsAsciiLetter(_text[_at]) || _text[_at] == '_' || _text[_at] >= 0x80);

    private JsonPathException Fail(string why) => new($"{why} (character {_at + 1} of '{_text}')", _at);

    /// <summary>What an operand is, before where it stands decides what it must be.</summary>
    private abstract record Operand
    {
        internal sealed record Literal(JsonElement Value) : Operand;

        internal sealed record Query(JsonPathQuery Of) : Operand;

        internal sealed record Function(JsonPathFunction Call) : Operand;
    }
}
