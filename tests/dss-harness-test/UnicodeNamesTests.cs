using System.Globalization;
using System.Runtime.CompilerServices;
using DssHarness.Core.Codebase.Languages.Scanning;

namespace DssHarness.Tests;

/// <summary>
/// The table of Unicode's character names the core embeds (<see cref="UnicodeNames"/>), held to what CPython 3.15.0 reads of a name in a
/// literal's <c>\N{...}</c>: its <c>_getcode</c>, <c>find_syllable</c>, <c>parse_hex_code</c> and <c>find_prefix_id</c> in
/// <c>Modules/unicodedata.c</c>, and the tables <c>Tools/unicode/makeunicodedata.py</c> makes of the same two files of the database, read at
/// the tag v3.15.0. Every stored name and alias is found in any case; a name CPython computes is the character in any case too - a Hangul
/// syllable's from its jamo, any other from its prefix and four to six hex digits without a leading zero, a code inside a range recorded for
/// that prefix alone; and every name it refuses is none. No 3.15 runs here: each row is one a line-by-line port of that source answers the
/// same over the same files, and every name the local CPython 3.14.3 accepts it accepts with the same character - its rule being 3.14's,
/// a part of 3.15's. The counts are the generator's, so a table generated from another version of the database, one generated without its
/// aliases, or a lookup that copies or caps its name is a red test.
/// </summary>
[Collection(MemoryMeasured.Name)]
public sealed class UnicodeNamesTests
{
    /// <summary>
    /// The table embedded in the core is the generator's, of the Unicode Character Database's 17.0.0: the 34,594 names CPython 3.15 stores -
    /// as many as the data lines of NameAliases.txt - and six prefixes whose names it computes from a code, in its order, with the twenty
    /// ranges of the code points they name; a Hangul syllable's is among none, its name made from the jamo. A table regenerated from
    /// another version, or one generated without the aliases, answers other numbers.
    /// </summary>
    [Fact]
    public void TheEmbeddedTable_HoldsUnicodesNamesAndAliasesOfItsVersion()
    {
        Assert.Equal("17.0.0", UnicodeNames.Version);
        Assert.Equal(34594, UnicodeNames.NameCount);
        Assert.Equal(481, UnicodeNames.AliasCount);
        Assert.Equal(
            [
                ("CJK UNIFIED IDEOGRAPH-", 11), ("TANGUT IDEOGRAPH-", 2), ("CJK COMPATIBILITY IDEOGRAPH-", 3), ("EGYPTIAN HIEROGLYPH-", 1),
                ("KHITAN SMALL SCRIPT CHARACTER-", 2), ("NUSHU CHARACTER-", 1),
            ],
            UnicodeNames.Computed.Select(computed => (computed.Prefix, computed.Ranges.Length)));
    }

    /// <summary>
    /// A name the table stores, and an alias of any of its five kinds - a correction, a control's, an alternate, a figment and an
    /// abbreviation are each here - names its character however the case of its letters is written, and in no other way loosely: a
    /// hyphen and a blank are kept as they stand, so two names that differ in one are two characters.
    /// </summary>
    /// <param name="name">The name, as a literal may write it.</param>
    /// <param name="code">The character it names.</param>
    [Theory]
    [InlineData("EM DASH", 0x2014)]
    [InlineData("em dash", 0x2014)]
    [InlineData("Em Dash", 0x2014)]
    [InlineData("eM dAsH", 0x2014)]
    [InlineData("LATIN SMALL LETTER E WITH ACUTE", 0xE9)]
    [InlineData("BYTE ORDER MARK", 0xFEFF)]
    [InlineData("BOM", 0xFEFF)]
    [InlineData("bom", 0xFEFF)]
    [InlineData("NBSP", 0xA0)]
    [InlineData("LF", 0x0A)]
    [InlineData("NULL", 0x00)]
    [InlineData("HORIZONTAL TABULATION", 0x09)]
    [InlineData("LATIN CAPITAL LETTER GHA", 0x1A2)]
    [InlineData("LATIN CAPITAL LETTER OI", 0x1A2)]
    [InlineData("PADDING CHARACTER", 0x80)]
    [InlineData("TANGUT COMPONENT-001", 0x18800)]
    [InlineData("tangut component-001", 0x18800)]
    [InlineData("HANGUL JUNGSEONG O-E", 0x1180)]
    [InlineData("HANGUL JUNGSEONG OE", 0x116C)]
    [InlineData("TIBETAN MARK BKA- SHOG GI MGO RGYAN", 0x0FD0)]
    [InlineData("ARABIC LETTER ALEF WASLA ISOLATED FORM", 0xFB50)]
    public void ANameOrAnAlias_IsFoundInAnyCase_AsCPythonFindsIt(string name, int code)
        => Assert.Equal(code, UnicodeNames.Python(name.AsSpan()));

    /// <summary>
    /// A name CPython computes is the character it names in any case, its ASCII letters read without regard to theirs, as 3.15 reads
    /// them - where 3.14 read a Hangul syllable's and an ideograph's in upper case alone. A Hangul syllable's is made of its three
    /// jamo, in each column the longest that stands there and one of them none at all; the others - a CJK unified ideograph's, a Tangut
    /// ideograph's, a CJK compatibility ideograph's, an Egyptian hieroglyph's, a Khitan small script character's, a Nushu character's -
    /// of a prefix and the code in hex digits of either case, four below U+10000 and five above.
    /// </summary>
    /// <param name="name">The computed name, in any case.</param>
    /// <param name="code">The character it names.</param>
    [Theory]
    [InlineData("CJK UNIFIED IDEOGRAPH-4E00", 0x4E00)]
    [InlineData("cjk unified ideograph-4E00", 0x4E00)]
    [InlineData("CJK UNIFIED IDEOGRAPH-4e00", 0x4E00)]
    [InlineData("cJk UnIfIeD iDeOgRaPh-4E00", 0x4E00)]
    [InlineData("CJK UNIFIED IDEOGRAPH-3400", 0x3400)]
    [InlineData("CJK UNIFIED IDEOGRAPH-20000", 0x20000)]
    [InlineData("CJK UNIFIED IDEOGRAPH-2EBF0", 0x2EBF0)]
    [InlineData("cjk unified ideograph-2ebf0", 0x2EBF0)]
    [InlineData("CJK UNIFIED IDEOGRAPH-323B0", 0x323B0)]
    [InlineData("TANGUT IDEOGRAPH-17000", 0x17000)]
    [InlineData("tangut ideograph-17000", 0x17000)]
    [InlineData("Tangut Ideograph-17000", 0x17000)]
    [InlineData("TANGUT IDEOGRAPH-187FF", 0x187FF)]
    [InlineData("TANGUT IDEOGRAPH-18D00", 0x18D00)]
    [InlineData("TANGUT IDEOGRAPH-18D1E", 0x18D1E)]
    [InlineData("tangut ideograph-18d1e", 0x18D1E)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-F900", 0xF900)]
    [InlineData("cjk compatibility ideograph-f900", 0xF900)]
    [InlineData("EGYPTIAN HIEROGLYPH-13460", 0x13460)]
    [InlineData("egyptian hieroglyph-13460", 0x13460)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-18B00", 0x18B00)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-18CFF", 0x18CFF)]
    [InlineData("NUSHU CHARACTER-1B170", 0x1B170)]
    [InlineData("nushu character-1b170", 0x1B170)]
    [InlineData("HANGUL SYLLABLE GA", 0xAC00)]
    [InlineData("hangul syllable ga", 0xAC00)]
    [InlineData("Hangul Syllable GA", 0xAC00)]
    [InlineData("HANGUL SYLLABLE ga", 0xAC00)]
    [InlineData("hangul Syllable GAG", 0xAC01)]
    [InlineData("HANGUL SYLLABLE A", 0xC544)]
    [InlineData("hangul syllable a", 0xC544)]
    [InlineData("HANGUL SYLLABLE GGAEGG", 0xAE6A)]
    [InlineData("HANGUL SYLLABLE HIH", 0xD7A3)]
    [InlineData("HANGUL SYLLABLE YI", 0xC758)]
    [InlineData("HANGUL SYLLABLE GGI", 0xB07C)]
    [InlineData("HANGUL SYLLABLE WAE", 0xC65C)]
    [InlineData("HANGUL SYLLABLE GEOSS", 0xAC84)]
    [InlineData("HANGUL SYLLABLE HEL", 0xD5EC)]
    [InlineData("HANGUL SYLLABLE WEONG", 0xC6E1)]
    public void ANameCPythonComputes_IsTheCharacterInAnyCase(string name, int code)
        => Assert.Equal(code, UnicodeNames.Python(name.AsSpan()));

    /// <summary>
    /// Each range the table records for a computed prefix names the code points of its own ends: the ranges are what the generator
    /// read of the database, 17.0.0's two new ones among them - a CJK extension and the whole of the Tangut ideographs.
    /// </summary>
    /// <param name="prefix">The computed prefix.</param>
    /// <param name="first">The range's first code point.</param>
    /// <param name="last">Its last.</param>
    [Theory]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x3400, 0x4DBF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x4E00, 0x9FFF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x20000, 0x2A6DF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2A700, 0x2B73F)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2B740, 0x2B81D)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2B820, 0x2CEAD)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2CEB0, 0x2EBE0)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2EBF0, 0x2EE5D)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x30000, 0x3134A)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x31350, 0x323AF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x323B0, 0x33479)]
    [InlineData("TANGUT IDEOGRAPH-", 0x17000, 0x187FF)]
    [InlineData("TANGUT IDEOGRAPH-", 0x18D00, 0x18D1E)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xF900, 0xFA6D)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xFA70, 0xFAD9)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0x2F800, 0x2FA1D)]
    [InlineData("EGYPTIAN HIEROGLYPH-", 0x13460, 0x143FA)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18B00, 0x18CD5)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18CFF, 0x18CFF)]
    [InlineData("NUSHU CHARACTER-", 0x1B170, 0x1B2FB)]
    public void EachRangeOfAComputedPrefix_DecodesItsEnds(string prefix, int first, int last)
    {
        Assert.Equal(first, UnicodeNames.Python(Computed(prefix, first).AsSpan()));
        Assert.Equal(last, UnicodeNames.Python(Computed(prefix, last).AsSpan()));
    }

    /// <summary>
    /// A code just outside every range of a computed prefix names no character under that prefix, whatever it names under another: each
    /// side of each range that has a gap beside it - among them a code that is another prefix's, as the Tangut supplement's first is
    /// under the Khitan prefix's nose.
    /// </summary>
    /// <param name="prefix">The computed prefix.</param>
    /// <param name="code">A code point outside every range of the prefix, the nearest one to a range's end.</param>
    [Theory]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x33FF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x4DC0)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x4DFF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0xA000)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x1FFFF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2A6E0)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2A6FF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2B81E)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2B81F)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2CEAE)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2CEAF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2EBE1)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2EBEF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2EE5E)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x2FFFF)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x3134B)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x3134F)]
    [InlineData("CJK UNIFIED IDEOGRAPH-", 0x3347A)]
    [InlineData("TANGUT IDEOGRAPH-", 0x16FFF)]
    [InlineData("TANGUT IDEOGRAPH-", 0x18800)]
    [InlineData("TANGUT IDEOGRAPH-", 0x18CFF)]
    [InlineData("TANGUT IDEOGRAPH-", 0x18D1F)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xF8FF)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xFA6E)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xFA6F)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0xFADA)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0x2F7FF)]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-", 0x2FA1E)]
    [InlineData("EGYPTIAN HIEROGLYPH-", 0x1345F)]
    [InlineData("EGYPTIAN HIEROGLYPH-", 0x143FB)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18AFF)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18CD6)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18CFE)]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-", 0x18D00)]
    [InlineData("NUSHU CHARACTER-", 0x1B16F)]
    [InlineData("NUSHU CHARACTER-", 0x1B2FC)]
    public void ACodeJustOutsideEveryRangeOfAComputedPrefix_DecodesNoName(string prefix, int code)
        => Assert.Null(UnicodeNames.Python(Computed(prefix, code).AsSpan()));

    /// <summary>
    /// A name CPython refuses names nothing: one misspelt, spaced or hyphenated otherwise than its character's name; one holding a
    /// character outside ASCII, which is never folded towards a letter - a long s is no S; a named sequence, which <c>lookup()</c>
    /// alone accepts; a computed name written otherwise than CPython writes one - a leading zero, fewer than four or more than six
    /// hex digits, a digit that is none, a prefix spaced or hyphenated otherwise, a code that lies in no range of the prefix that
    /// matched, though it lie in another prefix's; a Hangul syllable short of its jamo or with a letter left over.
    /// </summary>
    /// <param name="name">The name refused.</param>
    [Theory]
    [InlineData("EM-DASH")]
    [InlineData("EM_DASH")]
    [InlineData("EMDASH")]
    [InlineData("EM  DASH")]
    [InlineData(" EM DASH")]
    [InlineData("EM DASH ")]
    [InlineData("LATIN SMALL LETTER \u017fHARP S")]
    [InlineData("<control>")]
    [InlineData("\u017f")]
    [InlineData("A\u00A0B")]
    [InlineData("LATIN CAPITAL LETTER A WITH MACRON AND GRAVE")]
    [InlineData("CJK UNIFIED IDEOGRAPH-04E00")]
    [InlineData("cjk unified ideograph-04e00")]
    // A computed name written otherwise than CPython writes one: under the prefix that matched, it is refused whatever lies
    // behind - a leading zero; fewer than four or more than six digits; a code in no range of that prefix, though in another's.
    [InlineData("CJK UNIFIED IDEOGRAPH-030000")]
    [InlineData("TANGUT IDEOGRAPH-017000")]
    [InlineData("NUSHU CHARACTER-01B170")]
    [InlineData("CJK UNIFIED IDEOGRAPH-004E00")]
    [InlineData("CJK UNIFIED IDEOGRAPH-4E0")]
    [InlineData("CJK UNIFIED IDEOGRAPH-")]
    [InlineData("CJK UNIFIEDIDEOGRAPH-4E00")]
    [InlineData("CJK UNIFIED IDEOGRAPH-4E00G")]
    [InlineData("CJK UNIFIED IDEOGRAPH-100000")]
    [InlineData("CJK UNIFIED IDEOGRAPH-200000")]
    [InlineData("CJK UNIFIED IDEOGRAPH-110000")]
    [InlineData("CJK UNIFIED IDEOGRAPH-1234567")]
    [InlineData("CJK UNIFIED IDEOGRAPH-100003400")]
    [InlineData("TANGUT IDEOGRAPH-1700")]
    [InlineData("TANGUT IDEOGRAPH-18800")]
    [InlineData("CJK UNIFIED IDEOGRAPH-E000")]
    [InlineData("CJK UNIFIED IDEOGRAPH-F900")]
    [InlineData("CJK UNIFIED IDEOGRAPH-18D1E")]
    [InlineData("CJK UNIFIED IDEOGRAPH-1B170")]
    [InlineData("CJK UNIFIED IDEOGRAPH-13460")]
    [InlineData("TANGUT IDEOGRAPH-4E00")]
    [InlineData("TANGUT IDEOGRAPH-18D1F")]
    [InlineData("TANGUT IDEOGRAPH-16FFF")]
    [InlineData("NUSHU CHARACTER-18B00")]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-1B170")]
    [InlineData("CJK COMPATIBILITY IDEOGRAPH-4E00")]
    [InlineData("EGYPTIAN HIEROGLYPH-1345F")]
    [InlineData("KHITAN SMALL SCRIPT CHARACTER-18CD6")]
    [InlineData("NUSHU CHARACTER-1B-170")]
    [InlineData("NUSHU CHARACTER_1B170")]
    [InlineData("NUSHU CHARACTER 1B170")]
    [InlineData("NUSHU CHARACTERS-1B170")]
    [InlineData("EGYPTIAN HIEROGLYPH 13460")]
    [InlineData("CJK UNIFIED IDEOGRAPH-\u00e9E00")]
    [InlineData("CJK UNIFIED IDEOGRAPH-4\uFF25E00")]
    [InlineData("cjk unified ideograph-4e0g")]
    [InlineData("HANGUL SYLLABLE ")]
    [InlineData("HANGUL SYLLABLE GAX")]
    [InlineData("HANGUL SYLLABLE GA ")]
    [InlineData("HANGUL SYLLABLE GG")]
    [InlineData("HANGUL SYLLABLE D")]
    [InlineData("HANGUL SYLLABLE WEONGS")]
    [InlineData("HANGUL SYLLABLE A ")]
    [InlineData("hangul syllable gax")]
    [InlineData("hangul syllable gg")]
    [InlineData("Hangul Syllable D")]
    [InlineData("HANGUL SYLLABLE GA\u00e9")]
    [InlineData("HANGUL SYLLABLE\u017fGA")]
    [InlineData("")]
    public void ANameCPythonRefuses_IsNone(string name)
        => Assert.Null(UnicodeNames.Python(name.AsSpan()));

    /// <summary>
    /// A name longer than any the table holds is a miss by lookup and never by a cap: CPython refuses 89 letters, one more than the
    /// longest name of all, and a name of a hundred thousand is refused the same way, not cut short, not answered for its length.
    /// </summary>
    [Fact]
    public void ANameLongerThanAnyTheTableHolds_IsNone_HoweverLong()
    {
        foreach (var length in new[] { 89, 256, 257, 1_000, 100_000 })
        {
            Assert.Null(UnicodeNames.Python(new string('A', length)));
        }
    }

    /// <summary>
    /// The table is built once a process and looked up as a name is written: two asks read one object, and a million lookups - of a
    /// name the table holds, and of one longer than any it does - allocate nothing. A lookup that copied its name to upper-case it
    /// first, or took a buffer sized by it, would allocate at every ask, and a pathological name would cost its length twice.
    /// </summary>
    [Fact]
    public void TheTable_IsBuiltOnce_AndALookupReadsTheNameAlone()
    {
        var table = UnicodeNames.Made;

        Assert.Same(table, UnicodeNames.Made);
        Assert.Equal(0x2014, UnicodeNames.Python("em dash".AsSpan()));
        Assert.Same(table, UnicodeNames.Made);

        // Warmed until the runtime has compiled the lookup in every tier it compiles it in - which allocates a little, once, on
        // this thread - so that what is counted at last is the lookup alone.
        var (allocated, found) = Settled("em dash", 1_000_000);

        Assert.Equal(0x2014L * 1_000_000, found);
        Assert.Equal(0L, allocated);

        foreach (var length in new[] { 300, 70_000, 100_000 })
        {
            (allocated, found) = Settled(new string('a', length), 1_000);
            Assert.Equal(0L, found);
            Assert.Equal(0L, allocated);
        }
    }

    /// <summary>
    /// What looking <paramref name="name"/> up <paramref name="times"/> times allocated on this thread and the code points it named,
    /// asked again - at most ten times - while the runtime's own compiling of the loop costs a little on the thread: a lookup that
    /// allocates at every ask never comes to allocate nothing.
    /// </summary>
    private static (long Allocated, long Found) Settled(string name, int times)
    {
        var last = (-1L, 0L);

        for (var attempt = 0; attempt < 10 && last.Item1 != 0; attempt++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var found = LookedUp(name, times);
            last = (GC.GetAllocatedBytesForCurrentThread() - before, found);
        }

        return last;
    }

    /// <summary>The sum of the code points <paramref name="name"/> names over <paramref name="times"/> lookups, in a method of its own so the runtime compiles the loop apart from the test's.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long LookedUp(string name, int times)
    {
        long found = 0;

        for (var at = 0; at < times; at++)
        {
            found += UnicodeNames.Python(name.AsSpan()) ?? 0;
        }

        return found;
    }

    /// <summary>The name a computed prefix gives the code <paramref name="code"/>: the prefix and the code in capital hex digits, as the database would spell it.</summary>
    private static string Computed(string prefix, int code) => $"{prefix}{code.ToString("X", CultureInfo.InvariantCulture)}";
}
