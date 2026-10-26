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
/// <para>
/// What .NET reads its own way is written so it reads as I-Regexp's: <c>.</c> is any character but a line feed and a
/// carriage return, a pair of surrogates included; a class takes a pair of surrogates whole or not at all, and a surrogate
/// alone never; and <c>^</c> and <c>$</c> anchor the text's start and its very end, as the standard's compliance suite
/// reads them and as RFC 9485's mapping to ECMAScript leaves them - its grammar would make them characters of their own,
/// which no implementation does. A pattern that is no I-Regexp matches nothing, as the standard asks. A class that names a
/// character past U+FFFF in a range is not taken, the one corner of the grammar .NET's classes cannot hold.
/// </para>
/// <para>
/// .NET's classes and categories read one UTF-16 code unit, and a character past U+FFFF is two, each a surrogate of no
/// category but its own. So the text is matched with each such character led by a code unit that names its general
/// category (<see cref="FirstTag"/>), and a category - alone, in a class, negated - takes a pair of surrogates led by the
/// code unit of any category it holds: whatever the category, four of the parts .NET's linear matcher bounds a pattern by
/// - a pattern names some five hundred - where the characters past U+FFFF it holds, written out, would take up to a hundred
/// alternatives, and a pattern naming a few dozen categories would pass that bound.
/// </para>
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

    /// <summary>
    /// The code unit that leads a character past U+FFFF of the first general category in the text a pattern is matched
    /// against, each other category's the one after it in <see cref="UnicodeCategory"/>'s order: low surrogates, which never
    /// lead a pair, so no character of the text is read as one, and no code unit a class takes below U+FFFF is one.
    /// </summary>
    private const char FirstTag = '\uDC00';

    /// <summary>A pair of surrogates: one character past U+FFFF, which in the text matched follows the code unit naming its category.</summary>
    private const string Pair = @"[\uD800-\uDBFF][\uDC00-\uDFFF]";

    /// <summary>The two letters of each general category, in <see cref="UnicodeCategory"/>'s order.</summary>
    private static readonly string[] CategoryNames = ["Lu", "Ll", "Lt", "Lm", "Lo", "Mn", "Mc", "Me", "Nd", "Nl", "No", "Zs", "Zl", "Zp", "Cc", "Cf", "Cs", "Co", "Pc", "Pd", "Ps", "Pe", "Pi", "Pf", "Po", "Sm", "Sc", "Sk", "So", "Cn"];

    /// <summary>Every general category, as bits of <see cref="UnicodeCategory"/>'s values.</summary>
    private static readonly uint AllCategories = (1u << CategoryNames.Length) - 1;

    /// <summary>A character that is neither a line feed nor a carriage return: one code unit that is no surrogate, or a pair led by its category.</summary>
    private static readonly string AnyButNewline = $@"(?:[^\n\r\uD800-\uDFFF]|{Tags(AllCategories)}{Pair})";

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

        return regex is not null && regex.IsMatch(Tagged(text));
    }

    /// <summary>The .NET pattern <paramref name="pattern"/> is, matched against a text as <see cref="Tagged"/> writes it; or <see langword="null"/> where it is no I-Regexp.</summary>
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

    /// <summary>
    /// <paramref name="text"/> as a pattern is matched against it: each character past U+FFFF led by the code unit naming its
    /// general category (<see cref="FirstTag"/>), a surrogate alone left as it is, which nothing matches; the text itself
    /// where it holds no pair.
    /// </summary>
    private static string Tagged(string text)
    {
        var first = text.AsSpan().IndexOfAnyInRange('\uD800', '\uDBFF');

        if (first < 0)
        {
            return text;
        }

        var tagged = new StringBuilder(text.Length + 16).Append(text, 0, first);

        for (var at = first; at < text.Length; at++)
        {
            if (char.IsHighSurrogate(text[at]) && at + 1 < text.Length && char.IsLowSurrogate(text[at + 1]))
            {
                tagged.Append(Tag(CharUnicodeInfo.GetUnicodeCategory(text, at))).Append(text, at, 2);
                at++;
            }
            else
            {
                tagged.Append(text[at]);
            }
        }

        return tagged.ToString();
    }

    /// <summary>The code unit that leads a character past U+FFFF of <paramref name="category"/> in the text matched.</summary>
    private static char Tag(UnicodeCategory category) => (char)(FirstTag + (int)category);

    /// <summary>A class of the code units that lead the characters past U+FFFF of <paramref name="categories"/>, bits of <see cref="UnicodeCategory"/>'s values, written as runs.</summary>
    private static string Tags(uint categories)
    {
        var tags = new StringBuilder("[");

        for (var category = 0; category < CategoryNames.Length; category++)
        {
            if ((categories & (1u << category)) == 0)
            {
                continue;
            }

            var last = category;

            while (last + 1 < CategoryNames.Length && (categories & (1u << (last + 1))) != 0)
            {
                last++;
            }

            tags.Append(Escaped(Tag((UnicodeCategory)category)));

            if (last > category)
            {
                tags.Append('-').Append(Escaped(Tag((UnicodeCategory)last)));
            }

            category = last;
        }

        return tags.Append(']').ToString();
    }

    /// <summary>One code unit as .NET reads it literally: a letter or digit as it is, any other by its code.</summary>
    private static string Escaped(char character)
        => char.IsAsciiLetterOrDigit(character) ? character.ToString() : $"\\u{(int)character:X4}";

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

        /// <summary>An escape: a single character's, or a category's (<c>\p{..}</c>, <c>\P{..}</c>), a class of it alone.</summary>
        private string ReadEscape()
        {
            if (AtCategory())
            {
                var (member, categories) = ReadCategory();
                return Class(member, categories, negated: false);
            }

            _at++;

            if (_at >= pattern.Length)
            {
                throw new FormatException("'\\' ends the pattern");
            }

            var escaped = pattern[_at++];

            return escaped switch
            {
                'n' => @"\n",
                'r' => @"\r",
                't' => @"\t",
                '(' or ')' or '*' or '+' or '-' or '.' or '?' or '[' or '\\' or ']' or '^' or '{' or '|' or '}' => Literal(escaped.ToString()),
                _ => throw new FormatException($"'\\{escaped}' is no escape"),
            };
        }

        /// <summary>Whether a category's escape (<c>\p{..}</c>, <c>\P{..}</c>) begins here.</summary>
        private bool AtCategory() => At('\\') && _at + 1 < pattern.Length && pattern[_at + 1] is 'p' or 'P';

        /// <summary>
        /// A category's escape (<c>\p{..}</c>, <c>\P{..}</c>): the member of a class .NET reads it as of a code unit below
        /// U+FFFF, and the general categories of the characters past U+FFFF it holds - a <c>\P</c>'s, every one but those it
        /// names.
        /// </summary>
        private (string Member, uint Categories) ReadCategory()
        {
            var escaped = pattern[_at + 1];
            _at += 2;
            Expect('{');

            var start = _at;

            while (_at < pattern.Length && char.IsAsciiLetter(pattern[_at]))
            {
                _at++;
            }

            var name = pattern[start.._at];
            Expect('}');

            var categories = Categories(name) ?? throw new FormatException($"'{name}' is no category");

            return ($@"\{escaped}{{{name}}}", escaped == 'p' ? categories : AllCategories & ~categories);
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
            var categories = 0u;
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

                if (AtCategory())
                {
                    var (member, held) = ReadCategory();
                    members.Append(member);
                    categories |= held;
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

            return Class(members.ToString(), categories, negated);
        }

        /// <summary>
        /// A class of <paramref name="members"/> - code units below U+FFFF, as .NET reads them - and of the characters past
        /// U+FFFF of <paramref name="categories"/>, negated where <paramref name="negated"/> says: a surrogate alone is never
        /// one of it, whatever range holds it, and a character past U+FFFF is - its pair led by the code unit naming its
        /// category - where its category is one of the class's, or, negated, none of them.
        /// </summary>
        private static string Class(string members, uint categories, bool negated)
        {
            var below = negated ? $@"[^{members}\uD800-\uDFFF]" : $@"[{members}-[\uD800-\uDFFF]]";
            var past = negated ? AllCategories & ~categories : categories;

            return past == 0 ? below : $"(?:{below}|{Tags(past)}{Pair})";
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

        /// <summary>
        /// A character as .NET reads it literally: escaped by its code; a pair of surrogates led by the code unit naming its
        /// category, as the text matched holds it, grouped so a quantifier takes all three.
        /// </summary>
        private static string Literal(string character)
            => character.Length == 2
                ? $"(?:{Escaped(Tag(CharUnicodeInfo.GetUnicodeCategory(character, 0)))}{Escaped(character[0])}{Escaped(character[1])})"
                : Escaped(character[0]);

        /// <summary>A class's member: one code unit, escaped by its code; a character past U+FFFF is not taken in a class.</summary>
        private static string ClassMember(string character)
            => character.Length == 1 ? Escaped(character[0]) : throw new FormatException("a class naming a character past U+FFFF is not taken");

        /// <summary>
        /// The general categories <paramref name="name"/> names, as bits of <see cref="UnicodeCategory"/>'s values, where it
        /// is one I-Regexp takes - a letter alone, every category it begins, as <c>L</c>; two, that category, as <c>Lu</c>, but
        /// a surrogate's - and <see langword="null"/> where it names none, as a block's name does.
        /// </summary>
        private static uint? Categories(string name)
        {
            var named = 0u;

            for (var category = 0; category < CategoryNames.Length; category++)
            {
                if (name.Length == 1 ? CategoryNames[category][0] == name[0] : CategoryNames[category] == name)
                {
                    named |= 1u << category;
                }
            }

            return named == 0 || name == "Cs" ? null : named;
        }
    }
}
