using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using DssHarness.Core.Json;

namespace DssHarness.Tests;

/// <summary>The tool's own JSONPath as its callers use it, beside the standard's own suite (<see cref="JsonPathComplianceTests"/>).</summary>
public sealed class JsonPathTests
{
    /// <summary>A relative query begins at the value it is asked of, and a '$' in its filter names the whole document.</summary>
    [Fact]
    public void ARelativeQuery_BeginsAtItsValue()
    {
        using var document = JsonDocument.Parse("""{ "kind": "a", "items": [ { "kind": "a", "name": "one" }, { "kind": "b", "name": "two" } ] }""");
        var item = document.RootElement.GetProperty("items")[1];

        var path = JsonPath.ParseRelative("@.name");

        Assert.True(path.Relative);
        Assert.Equal("two", Assert.Single(path.Select(item, document.RootElement)).GetString());
        Assert.Equal(["one"], JsonPath.Parse("$.items[?@.kind == $.kind].name").Select(document.RootElement).Select(value => value.GetString()));
        Assert.Empty(JsonPath.ParseRelative("@[?@.kind == $.kind].name").Select(item, document.RootElement));
    }

    /// <summary>A singular query names one value at most: names and indexes alone, written without a blank inside a bracket.</summary>
    [Theory]
    [InlineData("$.a.b", true)]
    [InlineData("$['a'][0]", true)]
    [InlineData("$", true)]
    [InlineData("$[ 'a' ]", false)]
    [InlineData("$.a[*]", false)]
    [InlineData("$..a", false)]
    [InlineData("$['a','b']", false)]
    [InlineData("$[0:1]", false)]
    public void ASingularQuery_IsKnown(string query, bool singular) => Assert.Equal(singular, JsonPath.Parse(query).IsSingular);

    /// <summary>A text that is no query says why and where; a relative one is asked to begin with '@', an absolute with '$'.</summary>
    [Theory]
    [InlineData("$.a[", false, "(character 5 of '$.a[')")]
    [InlineData("@.a", false, "a query begins with '$'")]
    [InlineData("$.a", true, "a query begins with '@'")]
    [InlineData("$[?length(@.*) > 1]", false, "'length' is given a query that may select several values where it takes one value")]
    [InlineData("$[?count(1) > 1]", false, "'count' is given no query where it takes one")]
    [InlineData("$[?match(@.a, 'x')==true]", false, "'match()' answers no value, and is tested, never compared")]
    [InlineData("$[?frobnicate(@)]", false, "'frobnicate' is no function: length, count, match, search, value are")]
    [InlineData("$.a ", false, "a query ends with its last segment, and no blank follows it")]
    public void ATextThatIsNoQuery_SaysWhy(string text, bool relative, string why)
    {
        var problem = JsonPath.Problem(text, relative);

        Assert.NotNull(problem);
        Assert.Contains(why, problem, StringComparison.Ordinal);

        var refused = Assert.Throws<JsonPathException>(() => relative ? JsonPath.ParseRelative(text) : JsonPath.Parse(text));
        Assert.Equal(problem, refused.Message);
        Assert.InRange(refused.Position, 0, text.Length);
    }

    /// <summary>A query's values are the document's own, in its order; its text is the query as written.</summary>
    [Fact]
    public void AQuerysValues_AreTheDocumentsOwn()
    {
        using var document = JsonDocument.Parse("""{ "b": { "x": 1 }, "a": [ 3, 1, 2 ] }""");

        var path = JsonPath.Parse("$..*");

        Assert.Equal("$..*", path.ToString());
        Assert.Equal(["{ \"x\": 1 }", "[ 3, 1, 2 ]", "1", "3", "1", "2"], path.Select(document.RootElement).Select(value => value.GetRawText()));
    }

    /// <summary>A regular expression is matched in time linear in its text: a pattern that would backtrack forever answers at once.</summary>
    [Fact]
    public void ARegularExpression_NeverBacktracks()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new[] { new string('a', 50_000) + "!" }));
        var watch = Stopwatch.StartNew();

        Assert.Empty(JsonPath.Parse("$[?match(@, '(a+)+b')]").Select(document.RootElement));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    /// <summary>I-Regexp's own characters are read as I-Regexp reads them, and what is no I-Regexp matches nothing.</summary>
    [Theory]
    [InlineData("a.c", "abc", true)]
    [InlineData("a.c", "a\nc", false)]
    [InlineData("[^a]", "\U0001F600", true)]
    [InlineData("\\p{Lu}+", "ABC", true)]
    [InlineData("\\p{IsBasicLatin}", "a", false)]
    [InlineData("a{2,3}", "aaa", true)]
    [InlineData("a{3,2}", "aaa", false)]
    [InlineData("(a|b)*c", "ababc", true)]
    [InlineData("\\d", "1", false)]
    [InlineData("[a-]", "-", true)]
    [InlineData("[b-a]", "a", false)]
    [InlineData("a)", "a", false)]
    public void IRegexp_IsReadAsItsGrammarSays(string pattern, string text, bool matches)
        => Assert.Equal(matches, IRegexp.Matches(pattern, text, whole: true));

    /// <summary>
    /// A category is a character's, one past U+FFFF too - which .NET's own classes read as two halves of no category - as
    /// RFC 9485 reads it: alone, in a class and negated, so MATHEMATICAL BOLD CAPITAL A is a letter and nothing else.
    /// </summary>
    [Theory]
    [InlineData("$[?match(@, '\\\\p{L}')]", true)]
    [InlineData("$[?match(@, '[^\\\\p{L}]')]", false)]
    [InlineData("$[?search(@, '\\\\P{L}')]", false)]
    [InlineData("$[?match(@, '\\\\p{Lu}')]", true)]
    [InlineData("$[?match(@, '\\\\P{Ll}')]", true)]
    [InlineData("$[?search(@, '\\\\p{C}')]", false)]
    [InlineData("$[?match(@, '[\\\\p{N}\\\\p{L}]')]", true)]
    [InlineData("$[?match(@, '[^\\\\P{L}]')]", true)]
    [InlineData("$[?search(@, '[퀀-]')]", false)]
    public void ACategory_IsTheWholeCharactersPastUFFFF(string query, bool selected)
    {
        using var document = JsonDocument.Parse("""[ "𝐀" ]""");

        Assert.Equal(selected ? ["\U0001D400"] : [], JsonPath.Parse(query).Select(document.RootElement).Select(value => value.GetString()));
    }

    /// <summary>
    /// A character past U+FFFF is in each category, and out of it, as a character of the same category below U+FFFF is -
    /// whose categories .NET's own classes read - for every category I-Regexp names, alone, negated and in a negated class.
    /// </summary>
    [Fact]
    public void ACharacterPastUFFFF_IsInTheCategoriesOfItsKind()
    {
        string[] names = ["L", "Ll", "Lm", "Lo", "Lt", "Lu", "M", "Mc", "Me", "Mn", "N", "Nd", "Nl", "No", "P", "Pc", "Pd", "Pe", "Pf", "Pi", "Po", "Ps", "Z", "Zl", "Zp", "Zs", "S", "Sc", "Sk", "So", "C", "Cc", "Cf", "Cn", "Co"];
        var first = new Dictionary<UnicodeCategory, int>();

        for (var point = 0x10000; point <= 0x10FFFF; point++)
        {
            first.TryAdd(CharUnicodeInfo.GetUnicodeCategory(point), point);
        }

        foreach (var (category, astral) in first)
        {
            var bmp = (char)Enumerable.Range(0, 0x10000).First(point => point is < 0xD800 or > 0xDFFF && CharUnicodeInfo.GetUnicodeCategory(point) == category);

            foreach (var pattern in names.SelectMany(name => new[] { $"\\p{{{name}}}", $"\\P{{{name}}}", $"[^\\p{{{name}}}]" }))
            {
                Assert.True(
                    IRegexp.Matches(pattern, bmp.ToString(), whole: true) == IRegexp.Matches(pattern, char.ConvertFromUtf32(astral), whole: true),
                    $"'{pattern}' reads U+{astral:X} ({category}) otherwise than U+{(int)bmp:X4}");
            }
        }

        // The categories that have characters past U+FFFF, whatever version of Unicode the runtime holds, were compared.
        Assert.Superset(
            new HashSet<UnicodeCategory> { UnicodeCategory.UppercaseLetter, UnicodeCategory.LowercaseLetter, UnicodeCategory.OtherLetter, UnicodeCategory.NonSpacingMark, UnicodeCategory.DecimalDigitNumber, UnicodeCategory.OtherSymbol, UnicodeCategory.Format, UnicodeCategory.PrivateUse, UnicodeCategory.OtherNotAssigned },
            first.Keys.ToHashSet());
    }

    /// <summary>
    /// A pattern of hundreds of categories is built and matched in time: a category is a few parts of .NET's pattern however
    /// many characters past U+FFFF it holds, which written out would take a hundred alternatives each and pass the most
    /// .NET's linear matcher builds at a few dozen.
    /// </summary>
    [Fact]
    public void APatternOfHundredsOfCategories_IsMatchedInTime()
    {
        var watch = Stopwatch.StartNew();

        Assert.True(IRegexp.Matches(string.Concat(Enumerable.Repeat("\\p{L}", 400)), string.Concat(Enumerable.Repeat("\U0001D400", 400)), whole: true));
        Assert.True(IRegexp.Matches("[\\p{L}\\p{N}_]{1,300}", string.Concat(Enumerable.Repeat("\U0001D400\U0001D7CE_", 100)), whole: true));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    /// <summary>A character past U+FFFF is still one character: written in a pattern, repeated, and matched by '.' alone.</summary>
    [Theory]
    [InlineData("\U0001D400", "\U0001D400", true)]
    [InlineData("\U0001D400+", "\U0001D400\U0001D400", true)]
    [InlineData("a.b", "a\U0001D400b", true)]
    [InlineData("a..b", "a\U0001D400b", false)]
    [InlineData("[^a]\U0001D400", "\U0001D400\U0001D400", true)]
    [InlineData("\\p{Co}", "\U000F0000", true)]
    [InlineData("\\p{Cn}", "\U000E0080", true)]
    public void ACharacterPastUFFFF_IsOneCharacter(string pattern, string text, bool matches)
        => Assert.Equal(matches, IRegexp.Matches(pattern, text, whole: true));

    /// <summary>'^' and '$' anchor the text's very start and end - a line feed at its end is the text's - as match is the whole text.</summary>
    [Theory]
    [InlineData("a$", "a\n", false, false)]
    [InlineData("a", "a\n", true, false)]
    [InlineData("^a", "ba", false, false)]
    [InlineData("a", "ba", false, true)]
    public void IRegexpAnchors_AreTheTextsEnds(string pattern, string text, bool whole, bool matches)
        => Assert.Equal(matches, IRegexp.Matches(pattern, text, whole));

    /// <summary>Values compare as the standard says: objects by their members, arrays by each item in order, strings by scalar values, a string's length in scalar values.</summary>
    [Fact]
    public void Values_CompareAsTheStandardSays()
    {
        using var objects = JsonDocument.Parse("""[ { "x": { "a": 1 }, "y": { "a": 1, "b": 2 } }, { "x": { "a": 1 }, "y": { "a": 1 } } ]""");
        Assert.Single(JsonPath.Parse("$[?@.x == @.y]").Select(objects.RootElement));

        using var arrays = JsonDocument.Parse("""[ { "x": [ 1, 2 ], "y": [ 1 ] }, { "x": [ 1 ], "y": [ 1, 2 ] }, { "x": [ 1, 2 ], "y": [ 1, 2 ] } ]""");
        Assert.Single(JsonPath.Parse("$[?@.x == @.y]").Select(arrays.RootElement));

        using var texts = JsonDocument.Parse("""[ "\uD83D\uDE00", "ab" ]""");
        Assert.Equal(["\U0001F600"], JsonPath.Parse("$[?length(@) == 1]").Select(texts.RootElement).Select(value => value.GetString()));
        Assert.Equal(["\U0001F600"], JsonPath.Parse("$[?@ > '\\uFFFF']").Select(texts.RootElement).Select(value => value.GetString()));
    }

    /// <summary>A pattern is read to 4 KiB at most: a document's value as long as it likes is no pattern.</summary>
    [Theory]
    [InlineData(4096, true)]
    [InlineData(4097, false)]
    public void IRegexpPatterns_AreReadToTheirMost(int length, bool matches)
        => Assert.Equal(matches, IRegexp.Matches("[" + new string('a', length - 2) + "]", "a", whole: true));

    /// <summary>Two objects compare in time, each member found by its name, however many they hold.</summary>
    [Fact]
    public void ObjectsOfManyMembers_CompareInTime()
    {
        var members = Enumerable.Range(0, 80_000).Select(at => $"\"m{at}\": {at}").ToList();
        using var document = JsonDocument.Parse($$"""[ { "x": { {{string.Join(", ", members)}} }, "y": { {{string.Join(", ", Enumerable.Reverse(members))}} } } ]""");
        var watch = Stopwatch.StartNew();

        Assert.Single(JsonPath.Parse("$[?@.x == @.y]").Select(document.RootElement));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"took {watch.Elapsed}");
    }

    /// <summary>Groups one beside another are as deep as one: the bound is of how deep they nest, not of how many there are.</summary>
    [Fact]
    public void IRegexpGroupsBesideEachOther_AreNoDeeper()
        => Assert.True(IRegexp.Matches(string.Concat(Enumerable.Repeat("(a)", 100)), new string('a', 100), whole: true));

    /// <summary>Two objects are equal where they hold as many members, each name an equal value: a name given twice is its last value's.</summary>
    [Theory]
    [InlineData("""{ "a": 1, "a": 2 }""", """{ "a": 2 }""", false)]
    [InlineData("""{ "a": 1, "a": 1 }""", """{ "a": 1, "b": 2 }""", false)]
    [InlineData("""{ "a": 1, "a": 2 }""", """{ "a": 2, "a": 2 }""", true)]
    public void ObjectsWithANameGivenTwice_CompareByTheirLastValue(string x, string y, bool equal)
    {
        using var document = JsonDocument.Parse($$"""[ { "x": {{x}}, "y": {{y}} } ]""");

        Assert.Equal(equal ? 1 : 0, JsonPath.Parse("$[?@.x == @.y]").Select(document.RootElement).Count);
    }

    /// <summary>Groups nest 64 deep at most: a pattern a document holds may nest them as deep as it likes, and deeper is none.</summary>
    [Theory]
    [InlineData(64, true)]
    [InlineData(65, false)]
    [InlineData(100_000, false)]
    public void IRegexpGroups_NestAsDeepAsTheBoundAtMost(int depth, bool matches)
        => Assert.Equal(matches, IRegexp.Matches(new string('(', depth) + "a" + new string(')', depth), "a", whole: true));
}
