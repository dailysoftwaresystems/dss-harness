using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace DssHarness.Core.Json;

/// <summary>
/// I-Regexp (RFC 9485), the regular expressions JSONPath's <c>match</c> and <c>search</c> take: read by its own grammar and
/// written as .NET's, matched by characters - a pair of surrogates one - and in time linear in the text, never backtracking.
/// </summary>
/// <remarks>
/// What .NET reads its own way is written so it reads as I-Regexp's: <c>.</c> is any character but a line feed and a
/// carriage return, a pair of surrogates included; a class negated takes a pair of surrogates whole; and <c>^</c> and
/// <c>$</c> anchor the text's start and its very end, as the standard's compliance suite reads them and as RFC 9485's
/// mapping to ECMAScript leaves them - its grammar would make them characters of their own, which no implementation does. A pattern that is no I-Regexp matches nothing, as the standard asks. A class that
/// names a character past U+FFFF in a range is not taken, the one corner of the grammar .NET's classes cannot hold.
/// </remarks>
internal static class IRegexp
{
    /// <summary>How many patterns are kept read, each once: a filter asks its pattern of every value it is given.</summary>
    private const int MostKept = 256;

    /// <summary>
    /// The longest pattern read: 4 KiB. A pattern may be a value of the document a query reads, and is written out longer
    /// and kept read; a longer one is no pattern this matches with, as RFC 9485 lets an implementation say.
    /// </summary>
    private const int MostPattern = 4096;

    /// <summary>A character that is neither a line feed nor a carriage return: one code unit that is no surrogate, or a pair.</summary>
    private const string AnyButNewline = @"(?:[^\n\r\uD800-\uDFFF]|[\uD800-\uDBFF][\uDC00-\uDFFF])";

    /// <summary>Any pair of surrogates: one character past U+FFFF, which a negated class takes whole.</summary>
    private const string AnyPair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

    private static readonly ConcurrentDictionary<(string Pattern, bool Whole), Regex?> Read = new();

    /// <summary>Whether <paramref name="text"/> is matched by <paramref name="pattern"/>, whole or somewhere in it; never where the pattern is no I-Regexp.</summary>
    /// <param name="pattern">The I-Regexp.</param>
    /// <param name="text">The text.</param>
    /// <param name="whole">Whether the whole text must match (<c>match</c>), or a part of it (<c>search</c>).</param>
    public static bool Matches(string pattern, string text, bool whole)
    {
        if (pattern.Length > MostPattern)
        {
            return false;
        }

        if (!Read.TryGetValue((pattern, whole), out var regex))
        {
            regex = Compiled(pattern, whole);

            if (Read.Count < MostKept)
            {
                Read.TryAdd((pattern, whole), regex);
            }
        }

        return regex?.IsMatch(text) ?? false;
    }

    /// <summary>The .NET pattern <paramref name="pattern"/> is, or <see langword="null"/> where it is no I-Regexp.</summary>
    internal static string? ToDotNet(string pattern)
    {
        try
        {
            var reader = new Reader(pattern);
            var written = reader.ReadAlternatives();

            return reader.Done ? written : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static Regex? Compiled(string pattern, bool whole)
    {
        if (ToDotNet(pattern) is not { } written)
        {
            return null;
        }

        try
        {
            return new Regex(whole ? $@"\A(?:{written})\z" : written, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // A quantifier .NET's linear matcher will not build - one past its bounds - is no pattern this matches with.
            return null;
        }
    }

    /// <summary>Reads I-Regexp by RFC 9485's grammar (section 5.3) and writes .NET's pattern of it.</summary>
    private sealed class Reader(string pattern)
    {
        /// <summary>How deep groups nest at most: a pattern may come from the document a query reads, and each group is read within the one around it.</summary>
        private const int MostDepth = 64;

        private int _at;
        private int _depth;

        public bool Done => _at == pattern.Length;

        /// <summary><c>i-regexp = branch *( "|" branch )</c>.</summary>
        public string ReadAlternatives()
        {
            var written = new StringBuilder(ReadBranch());

            while (At('|'))
            {
                _at++;
                written.Append('|').Append(ReadBranch());
            }

            return written.ToString();
        }

        private string ReadBranch()
        {
            var written = new StringBuilder();

            while (_at < pattern.Length && !At('|') && !At(')'))
            {
                written.Append(ReadAtom()).Append(ReadQuantifier());
            }

            return written.ToString();
        }

        private string ReadQuantifier()
        {
            if (At('*') || At('+') || At('?'))
            {
                return pattern[_at++].ToString();
            }

            if (!At('{'))
            {
                return string.Empty;
            }

            _at++;

            var least = ReadCount();
            var most = least;

            if (At(','))
            {
                _at++;
                most = At('}') ? null : ReadCount();
            }

            Expect('}');

            if (most is { } bound && bound < least)
            {
                throw new FormatException("a quantifier's most is below its least");
            }

            return most is null ? $"{{{least},}}" : most == least ? $"{{{least}}}" : $"{{{least},{most}}}";
        }

        private int? ReadCount()
        {
            var start = _at;

            while (_at < pattern.Length && char.IsAsciiDigit(pattern[_at]))
            {
                _at++;
            }

            return _at > start && int.TryParse(pattern.AsSpan(start, _at - start), NumberStyles.None, CultureInfo.InvariantCulture, out var count)
                ? count
                : throw new FormatException("a quantifier's bound is a number");
        }

        /// <summary><c>atom = NormalChar / charClass / ( "(" i-regexp ")" )</c>.</summary>
        private string ReadAtom()
        {
            var character = pattern[_at];

            switch (character)
            {
                case '(':
                    if (++_depth > MostDepth)
                    {
                        throw new FormatException($"its groups nest deeper than {MostDepth}");
                    }

                    _at++;
                    var inner = ReadAlternatives();
                    Expect(')');
                    _depth--;
                    return $"(?:{inner})";

                case '.':
                    _at++;
                    return AnyButNewline;

                case '^':
                    _at++;
                    return @"\A";

                case '$':
                    _at++;
                    return @"\z";

                case '[':
                    return ReadClass();

                case '\\':
                    return ReadEscape();

                case ')' or '*' or '+' or '?' or ']' or '{' or '|' or '}':
                    throw new FormatException($"'{character}' stands alone");

                default:
                    return Literal(ReadScalar());
            }
        }

        /// <summary>An escape: a single character's, or a category's (<c>\p{..}</c>, <c>\P{..}</c>).</summary>
        private string ReadEscape()
        {
            _at++;

            if (_at >= pattern.Length)
            {
                throw new FormatException("'\\' ends the pattern");
            }

            var escaped = pattern[_at++];

            switch (escaped)
            {
                case 'n': return @"\n";
                case 'r': return @"\r";
                case 't': return @"\t";
                case 'p' or 'P':
                    Expect('{');
                    var start = _at;

                    while (_at < pattern.Length && char.IsAsciiLetter(pattern[_at]))
                    {
                        _at++;
                    }

                    var category = pattern[start.._at];
                    Expect('}');

                    return IsCategory(category) ? $@"\{escaped}{{{category}}}" : throw new FormatException($"'{category}' is no category");

                case '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}':
                    return Literal(escaped.ToString());

                default:
                    throw new FormatException($"'\\{escaped}' is no escape");
            }
        }

        /// <summary><c>charClassExpr = "[" [ "^" ] ( "-" / CCE1 ) *CCE1 [ "-" ] "]"</c>.</summary>
        private string ReadClass()
        {
            _at++;

            var negated = At('^');

            if (negated)
            {
                _at++;
            }

            var members = new StringBuilder();
            var first = true;

            while (true)
            {
                if (At(']') && !first)
                {
                    _at++;
                    break;
                }

                if (At('-'))
                {
                    // A '-' of its own is a member first, and last: anywhere else it is no member.
                    _at++;

                    if (!first && !At(']'))
                    {
                        throw new FormatException("'-' in a class stands first or last");
                    }

                    members.Append(@"\-");
                    first = false;
                    continue;
                }

                if (At('\\') && _at + 1 < pattern.Length && pattern[_at + 1] is 'p' or 'P')
                {
                    members.Append(ReadEscape());
                    first = false;
                    continue;
                }

                var low = ReadClassCharacter();

                if (At('-') && _at + 1 < pattern.Length && pattern[_at + 1] != ']')
                {
                    _at++;
                    var high = ReadClassCharacter();

                    if (char.ConvertToUtf32(high, 0) < char.ConvertToUtf32(low, 0))
                    {
                        throw new FormatException("a range's end is below its start");
                    }

                    members.Append(ClassMember(low)).Append('-').Append(ClassMember(high));
                }
                else
                {
                    members.Append(ClassMember(low));
                }

                first = false;
            }

            return negated
                ? $"(?:[^{members}\\uD800-\\uDFFF]|{AnyPair})"
                : $"[{members}]";
        }

        /// <summary><c>CCchar</c>: a character that is no <c>-</c>, <c>[</c>, <c>\</c> or <c>]</c>, or a single character's escape.</summary>
        private string ReadClassCharacter()
        {
            if (_at >= pattern.Length)
            {
                throw new FormatException("a class is not closed");
            }

            if (At('\\'))
            {
                _at++;

                if (_at >= pattern.Length)
                {
                    throw new FormatException("'\\' ends the pattern");
                }

                var escaped = pattern[_at++];

                return escaped switch
                {
                    'n' => "\n",
                    'r' => "\r",
                    't' => "\t",
                    '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => escaped.ToString(),
                    _ => throw new FormatException($"'\\{escaped}' is no escape"),
                };
            }

            return pattern[_at] is '[' or ']' or '-'
                ? throw new FormatException($"'{pattern[_at]}' in a class is escaped")
                : ReadScalar();
        }

        /// <summary>One character, a pair of surrogates whole; a surrogate alone is no character.</summary>
        private string ReadScalar()
        {
            var character = pattern[_at];

            if (char.IsHighSurrogate(character) && _at + 1 < pattern.Length && char.IsLowSurrogate(pattern[_at + 1]))
            {
                _at += 2;
                return pattern.Substring(_at - 2, 2);
            }

            if (char.IsSurrogate(character))
            {
                throw new FormatException("a surrogate stands alone");
            }

            _at++;
            return character.ToString();
        }

        private void Expect(char character)
        {
            if (!At(character))
            {
                throw new FormatException($"'{character}' is missing");
            }

            _at++;
        }

        private bool At(char character) => _at < pattern.Length && pattern[_at] == character;

        /// <summary>A character as .NET reads it literally: escaped by its code, a pair of surrogates grouped so a quantifier takes both.</summary>
        private static string Literal(string character)
            => character.Length == 2 ? $"(?:{Escaped(character[0])}{Escaped(character[1])})" : Escaped(character[0]);

        /// <summary>A class's member: one code unit, escaped by its code; a character past U+FFFF is not taken in a class.</summary>
        private static string ClassMember(string character)
            => character.Length == 1 ? Escaped(character[0]) : throw new FormatException("a class naming a character past U+FFFF is not taken");

        private static string Escaped(char character)
            => char.IsAsciiLetterOrDigit(character) ? character.ToString() : $"\\u{(int)character:X4}";

        /// <summary>Whether <paramref name="category"/> is one of the general categories I-Regexp takes: <c>L</c>, <c>Lu</c> and their kin, no block.</summary>
        private static bool IsCategory(string category) => category switch
        {
            "L" or "Ll" or "Lm" or "Lo" or "Lt" or "Lu" => true,
            "M" or "Mc" or "Me" or "Mn" => true,
            "N" or "Nd" or "Nl" or "No" => true,
            "P" or "Pc" or "Pd" or "Pe" or "Pf" or "Pi" or "Po" or "Ps" => true,
            "Z" or "Zl" or "Zp" or "Zs" => true,
            "S" or "Sc" or "Sk" or "Sm" or "So" => true,
            "C" or "Cc" or "Cf" or "Cn" or "Co" => true,
            _ => false,
        };
    }
}
