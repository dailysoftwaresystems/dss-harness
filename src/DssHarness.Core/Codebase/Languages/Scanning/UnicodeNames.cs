using System.Text;

namespace DssHarness.Core.Codebase.Languages.Scanning;

/// <summary>
/// A prefix whose names CPython computes from a code, as the table holds it (<see cref="UnicodeNames.Computed"/>): the
/// characters of each of its ranges are named by the prefix and four to six hex digits of the code.
/// </summary>
/// <param name="Prefix">What each of its names begins with.</param>
/// <param name="Ranges">Its ranges of code points, as the table holds them: each the first and the last of a run of characters the database gives the prefix's names.</param>
internal sealed record UnicodeComputed(string Prefix, (int First, int Last)[] Ranges);

/// <summary>
/// The names of Unicode's characters: the names and the aliases of the Unicode Character Database's <see cref="Version"/> and the
/// prefixes whose names CPython computes from a code, embedded in the core as one table
/// (<c>Codebase/Languages/Scanning/unicode-names.bin</c>, made from the database's files by <c>.github/scripts/unicode-names.cs</c>)
/// and read into memory once a process, the first time one is asked for. The one owner of what the name in a Python literal's
/// <c>\N{...}</c> names (<see cref="Python"/>, asked by <see cref="ScanLiteral"/>): nothing is fetched at run time and no machine's
/// ICU is read - the tool runs under invariant globalization - so the same name is the same character everywhere, whichever Unicode
/// a machine carries. Which names are stored and which computed is CPython 3.15's own rule, and the generator keeps it: a Hangul
/// syllable's name is made from its jamo, held here; every other computed name from its prefix and a code, both in the table.
/// </summary>
internal static class UnicodeNames
{
    /// <summary>The table's name among the core's embedded resources (<c>DssHarness.Core.csproj</c>).</summary>
    private const string Resource = "DssHarness.Core.UnicodeNames";

    /// <summary>What a Hangul syllable's name begins with, its blank included: the first prefix CPython looks for, in any case.</summary>
    private const string Hangul = "HANGUL SYLLABLE ";

    /// <summary>
    /// The short names of the jamo a Hangul syllable begins with, by the number its code is made from: the leading column of the
    /// syllables' names in the Unicode Standard's table of them, the one no consonant stands for - of <c>HANGUL SYLLABLE A</c> -
    /// among them.
    /// </summary>
    private static readonly string[] Leading = ["G", "GG", "N", "D", "DD", "R", "M", "B", "BB", "S", "SS", "", "J", "JJ", "C", "K", "T", "P", "H"];

    /// <summary>The short names of the vowel jamo of a Hangul syllable, by the number its code is made from.</summary>
    private static readonly string[] Vowel = ["A", "AE", "YA", "YAE", "EO", "E", "YEO", "YE", "O", "WA", "WAE", "OE", "YO", "U", "WEO", "WE", "WI", "YU", "EU", "YI", "I"];

    /// <summary>
    /// The short names of the jamo a Hangul syllable ends with, by the number its code is made from, the one no consonant stands
    /// for first.
    /// </summary>
    private static readonly string[] Trailing = ["", "G", "GG", "GS", "N", "NJ", "NH", "D", "L", "LG", "LM", "LB", "LS", "LT", "LP", "LH", "M", "B", "BS", "S", "SS", "NG", "J", "C", "K", "T", "P", "H"];

    private static Table? _table;

    private static Table Built => LazyInitializer.EnsureInitialized(ref _table, Read);

    /// <summary>The table as this process built it: one object for every ask, that a test can see it is built once.</summary>
    public static object Made => Built;

    /// <summary>The version of the Unicode Character Database the names are of, as the table says of itself.</summary>
    public static string Version => Built.Version;

    /// <summary>
    /// How many code points the table holds a name of: those CPython stores. Every other name is computed - a Hangul syllable's
    /// from its jamo (<see cref="Python"/>), the rest from the prefixes (<see cref="Computed"/>).
    /// </summary>
    public static int NameCount => Built.NameCount;

    /// <summary>How many aliases the table holds, beside the names.</summary>
    public static int AliasCount => Built.AliasCount;

    /// <summary>
    /// The prefixes whose names CPython computes from a code, in the order it looks for them: a CJK unified ideograph's, a Tangut
    /// ideograph's, a CJK compatibility ideograph's, an Egyptian hieroglyph's, a Khitan small script character's, a Nushu character's.
    /// A Hangul syllable's is not among them: it is computed from the jamo, and looked for first (<see cref="Python"/>).
    /// </summary>
    public static IReadOnlyList<UnicodeComputed> Computed => Built.Computed;

    /// <summary>
    /// The code point the name <paramref name="name"/> stands for, as CPython 3.15 reads a name in a literal's <c>\N{...}</c> - its
    /// reading of a name asked for no named sequence, which a literal never names - or <see langword="null"/> where it stands for
    /// none, where CPython refuses the literal. The first prefix the name begins with decides, each compared without regard to the
    /// case of its ASCII letters (<see cref="AsciiCase"/>): <c>HANGUL SYLLABLE </c>, then each of the table's (<see cref="Computed"/>).
    /// A name no prefix claims is looked up among the names and the aliases the table stores, by the same case rule and in no other
    /// way loose - its blanks and hyphens exact, and a character outside ASCII matching nothing, since every name of the table is
    /// ASCII. A Hangul syllable - <c>HANGUL SYLLABLE GGAEGG</c> - is computed from its three jamo, the short names in each column the
    /// longest that stand there, the three together the whole name. A name of any other computed prefix - <c>CJK UNIFIED
    /// IDEOGRAPH-2EBF0</c>, <c>TANGUT IDEOGRAPH-17000</c>, <c>NUSHU CHARACTER-1B170</c> - is the prefix and four to six hex digits in
    /// either case, a leading zero refused, naming a code no greater than every character's inside a range recorded for that prefix
    /// alone. Once a prefix claims a name there is no way back to the names stored, and a named sequence is none of these. A lookup
    /// reads the name as it is written, once, and allocates nothing, a name of any length alike.
    /// </summary>
    /// <param name="name">The name between the braces, as it is written.</param>
    public static int? Python(ReadOnlySpan<char> name)
    {
        var table = Built;

        if (AsciiCase.Starts(name, Hangul))
        {
            return Syllable(name[Hangul.Length..]);
        }

        foreach (var computed in table.Computed)
        {
            if (AsciiCase.Starts(name, computed.Prefix))
            {
                return Digits(name[computed.Prefix.Length..]) is { } code && Holds(computed.Ranges, code) ? code : null;
            }
        }

        return table.Named.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(name, out var found) ? found : null;
    }

    /// <summary>
    /// The Hangul syllable the jamo name after the prefix, as CPython's <c>find_syllable</c> reads them, a column at a time: in each
    /// the longest short name that stands there, and the name of no consonant - <c>HANGUL SYLLABLE A</c>'s first, <c>HANGUL SYLLABLE
    /// GA</c>'s last - where no other does; the syllable's code where all three matched and are the whole of the rest, else none.
    /// </summary>
    /// <param name="name">What follows the prefix, to the name's end.</param>
    private static int? Syllable(ReadOnlySpan<char> name)
    {
        var leading = Jamo(ref name, Leading);
        var vowel = Jamo(ref name, Vowel);
        var trailing = Jamo(ref name, Trailing);

        return leading >= 0 && vowel >= 0 && trailing >= 0 && name.IsEmpty ? 0xAC00 + ((leading * 21) + vowel) * 28 + trailing : null;
    }

    /// <summary>
    /// Which of the short names <paramref name="names"/> stands first in <paramref name="rest"/> - the longest that does, as CPython
    /// matches each column of a Hangul syllable's name, the name of no consonant among them where no other stands there - and
    /// <paramref name="rest"/> past it; -1, and <paramref name="rest"/> as it was, where none does.
    /// </summary>
    /// <param name="rest">What is left of the name, to its end.</param>
    /// <param name="names">A column's short names, by the number its code is made from.</param>
    private static int Jamo(ref ReadOnlySpan<char> rest, string[] names)
    {
        var found = -1;
        var length = -1;

        for (var at = 0; at < names.Length; at++)
        {
            if (names[at].Length > length && AsciiCase.Starts(rest, names[at]))
            {
                (found, length) = (at, names[at].Length);
            }
        }

        if (found >= 0)
        {
            rest = rest[length..];
        }

        return found;
    }

    /// <summary>
    /// The code the digits after a computed prefix's name write, as CPython's <c>parse_hex_code</c> reads them: four to six hex
    /// digits in either case, a leading zero refused, the code no greater than every character's; <see langword="null"/> where they
    /// write none.
    /// </summary>
    /// <param name="digits">What follows the prefix, to the name's end.</param>
    private static int? Digits(ReadOnlySpan<char> digits)
    {
        if (digits.Length is < 4 or > 6 || digits[0] == '0')
        {
            return null;
        }

        var code = 0;

        foreach (var digit in digits)
        {
            if (digit is not (>= '0' and <= '9' or >= 'A' and <= 'F' or >= 'a' and <= 'f'))
            {
                return null;
            }

            code = (code * 16) + (digit <= '9' ? digit - '0' : (digit | 0x20) - 'a' + 10);
        }

        return code <= 0x10FFFF ? code : null;
    }

    /// <summary>Whether a range of <paramref name="ranges"/> holds the code <paramref name="code"/>: a computed prefix names a code by its own ranges alone, as CPython's <c>find_prefix_id</c> finds the prefix a range was recorded for.</summary>
    private static bool Holds((int First, int Last)[] ranges, int code)
    {
        foreach (var (first, last) in ranges)
        {
            if (code >= first && code <= last)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The embedded table, read: a table that is not the generator's throws, since the build embeds it and no file of a
    /// repository writes it.
    /// </summary>
    private static Table Read()
    {
        using var stream = typeof(UnicodeNames).Assembly.GetManifestResourceStream(Resource)
            ?? throw new InvalidOperationException("the table of Unicode's character names is not embedded in this build");

        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        var reading = new Reading(bytes);

        if (!reading.Is("DSUN"u8) || reading.Byte() != 2)
        {
            throw Malformed("its magic or its format byte is not the generator's");
        }

        var version = reading.Text();
        var words = new string[reading.Number()];

        for (var at = 0; at < words.Length; at++)
        {
            words[at] = reading.Text();
        }

        // The names are by code point, each the difference from the one before; an alias's code is written whole, then its kind.
        var named = new Dictionary<string, int>(AsciiCase.Alone);
        var joined = new StringBuilder();
        var names = reading.Number();
        var code = 0;

        for (var at = 0; at < names; at++)
        {
            code += reading.Number();
            named.Add(Joined(ref reading, words, joined), code);
        }

        var aliases = reading.Number();

        for (var at = 0; at < aliases; at++)
        {
            code = reading.Number();
            _ = reading.Byte();
            named.Add(Joined(ref reading, words, joined), code);
        }

        // The prefixes whose names are computed from a code, in CPython's order: each its name, then its ranges.
        var computed = new UnicodeComputed[reading.Number()];

        for (var at = 0; at < computed.Length; at++)
        {
            var prefix = reading.Text();

            if (prefix.Length == 0)
            {
                throw Malformed("a prefix is written with no name");
            }

            var ranges = new (int First, int Last)[reading.Number()];

            for (var range = 0; range < ranges.Length; range++)
            {
                ranges[range] = (reading.Number(), reading.Number());
            }

            computed[at] = new UnicodeComputed(prefix, ranges);
        }

        if (!reading.Ended)
        {
            throw Malformed("bytes stand past its last range");
        }

        return new Table(version, named, names, aliases, computed);
    }

    /// <summary>The name the next record of the table writes: how many words it is made of, then each one's place among <paramref name="words"/>.</summary>
    private static string Joined(ref Reading reading, string[] words, StringBuilder joined)
    {
        joined.Clear();
        var count = reading.Number();

        for (var at = 0; at < count; at++)
        {
            var index = reading.Number();

            if (index >= words.Length)
            {
                throw Malformed($"a name is made of word {index}, past the table's {words.Length}");
            }

            if (at > 0)
            {
                joined.Append(' ');
            }

            joined.Append(words[index]);
        }

        return joined.ToString();
    }

    /// <summary>What a read past the table's end, or a byte no record should hold, throws.</summary>
    private static InvalidOperationException Malformed(string why) => new($"the table of Unicode's character names embedded in this build does not read back: {why}");

    /// <summary>
    /// The table the generator wrote: the names and the aliases by their name in upper case, one namespace - Unicode lets no two
    /// of them be alike - and the prefixes whose names are computed from a code, with their ranges.
    /// </summary>
    private sealed record Table(string Version, Dictionary<string, int> Named, int NameCount, int AliasCount, UnicodeComputed[] Computed);

    /// <summary>
    /// How the table's names are compared and hashed: alike where their ASCII letters differ in case alone, and never otherwise -
    /// a character outside ASCII equal to none, where an ignore-case comparison of the runtime's folds a long s to an S - so a
    /// name is looked up as it is written, with no copy of it upper-cased first, however long it is. The rule of every comparison
    /// a name is read by: the stored ones, and the prefix and the jamo of a computed one (<see cref="Starts(ReadOnlySpan{char}, string)"/>).
    /// </summary>
    private sealed class AsciiCase : IEqualityComparer<string>, IAlternateEqualityComparer<ReadOnlySpan<char>, string>
    {
        public static readonly AsciiCase Alone = new();

        public bool Equals(string? one, string? two) => one is not null && two is not null && Equals(one.AsSpan(), two);

        public int GetHashCode(string name) => GetHashCode(name.AsSpan());

        public bool Equals(ReadOnlySpan<char> written, string stored)
        {
            if (written.Length != stored.Length)
            {
                return false;
            }

            for (var at = 0; at < written.Length; at++)
            {
                if (Upper(written[at]) != stored[at])
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Whether <paramref name="text"/> begins with <paramref name="prefix"/>, the same length of it read by the rule (<see cref="Equals(ReadOnlySpan{char}, string)"/>).</summary>
        public static bool Starts(ReadOnlySpan<char> text, string prefix)
            => text.Length >= prefix.Length && Alone.Equals(text[..prefix.Length], prefix);

        public int GetHashCode(ReadOnlySpan<char> written)
        {
            var hash = new HashCode();

            foreach (var character in written)
            {
                hash.Add(Upper(character));
            }

            return hash.ToHashCode();
        }

        /// <summary>The name written as the table keeps one, its ASCII letters in upper case: asked where a key is to be stored by an alternate one, which the table never does.</summary>
        public string Create(ReadOnlySpan<char> written)
            => string.Create(written.Length, written, static (into, from) =>
            {
                for (var at = 0; at < from.Length; at++)
                {
                    into[at] = Upper(from[at]);
                }
            });

        private static char Upper(char character) => character is >= 'a' and <= 'z' ? (char)(character - 32) : character;
    }

    /// <summary>The table's bytes as they are read, in the generator's order: a byte, a number - each an LEB128 varint - or a text at a time.</summary>
    private ref struct Reading(ReadOnlySpan<byte> bytes)
    {
        private ReadOnlySpan<byte> _rest = bytes;

        /// <summary>Whether every byte is read.</summary>
        public bool Ended => _rest.IsEmpty;

        /// <summary>Whether what stands first is <paramref name="magic"/>, read past where it is.</summary>
        public bool Is(ReadOnlySpan<byte> magic)
        {
            if (!_rest.StartsWith(magic))
            {
                return false;
            }

            _rest = _rest[magic.Length..];
            return true;
        }

        /// <summary>The next byte.</summary>
        public byte Byte()
        {
            if (_rest.IsEmpty)
            {
                throw Malformed("it ends before every record of it is read");
            }

            var first = _rest[0];
            _rest = _rest[1..];
            return first;
        }

        /// <summary>The next number: its low seven bits a byte, the next byte's high bit saying another follows.</summary>
        public int Number()
        {
            var value = 0;

            for (var shift = 0; ; shift += 7)
            {
                var next = Byte();

                if (shift == 28 && next > 7)
                {
                    throw Malformed("a number is wider than an integer holds");
                }

                value |= (next & 0x7F) << shift;

                if (next < 0x80)
                {
                    return value;
                }
            }
        }

        /// <summary>The next text: its length in bytes, then as many, ASCII.</summary>
        public string Text()
        {
            var length = Number();

            if (length > _rest.Length)
            {
                throw Malformed("a text ends past the table's end");
            }

            var text = Encoding.ASCII.GetString(_rest[..length]);
            _rest = _rest[length..];
            return text;
        }
    }
}
