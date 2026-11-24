using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;

namespace DssHarness.Tests;

/// <summary>
/// The one owner of how a configuration key's path is written and read back (<see cref="CodeKeyPaths"/>): a name holding a
/// dot or a bracket is one place, quoted in brackets, so a path reads back exactly; a place quoted in single quotes is read
/// as the same place and never written; and .NET's spellings of a path, by a colon or a double underscore, name the same
/// key - where they can.
/// </summary>
public sealed class CodeKeyPathsTests
{
    /// <summary>
    /// A name holding a dot or a bracket is written quoted in brackets, and every other name as before: a plain name holds
    /// none of the characters a path parts its places by, so the written path never splits a name into several places - as
    /// <c>Logging.LogLevel.Microsoft.AspNetCore</c> did, the same text as four nested keys. A key node keeps its own name
    /// wherever that stands alone on a line, so it is reached by its name as its file writes it.
    /// </summary>
    /// <param name="name">The member's name.</param>
    /// <param name="written">The place as a path writes it.</param>
    /// <param name="bracketed">Whether a path holds the place in brackets.</param>
    /// <param name="node">The name a key node of the place is named by.</param>
    [Theory]
    [InlineData("plain", "plain", false, "plain")]
    [InlineData("Microsoft.AspNetCore", "[\"Microsoft.AspNetCore\"]", true, "Microsoft.AspNetCore")]
    [InlineData("[typescript]", "[\"[typescript]\"]", true, "[typescript]")]
    [InlineData("a]", "[\"a]\"]", true, "a]")]
    [InlineData("1", "1", false, "1")]
    [InlineData("1.5", "[\"1.5\"]", true, "1.5")]
    [InlineData("say \"hi\"", "say \"hi\"", false, "say \"hi\"")]
    [InlineData("say \"hi\".", "[\"say \\\"hi\\\".\"]", true, "say \"hi\".")]
    [InlineData(@"a\b", @"a\b", false, @"a\b")]
    [InlineData("", "[\"\"]", true, "[\"\"]")]
    [InlineData(" ", "[\" \"]", true, "[\" \"]")]
    [InlineData("a\nb.c", "[\"a\\nb.c\"]", true, "[\"a\\nb.c\"]")]
    [InlineData("webpages:Version", "webpages:Version", false, "webpages:Version")]
    public void ANameHoldingADotOrABracket_IsWrittenQuotedInBrackets_AndEveryOtherNameAsBefore(string name, string written, bool bracketed, string node)
    {
        Assert.Equal((written, bracketed, node), (CodeKeyPaths.Name(name), CodeKeyPaths.Bracketed(name), CodeKeyPaths.NodeName(name)));
    }

    [Fact]
    public void AnItemsPlace_IsHeldInBrackets_WrittenByItsDigits_AndNamedSo()
    {
        Assert.True(CodeKeyPaths.Bracketed(null));
        Assert.Equal(("[1]", "[1]"), (CodeKeyPaths.Item(1), new DataPlace(null, 1).NodeName));
    }

    /// <summary>
    /// A key whose name holds a dot is one place of its path, read back whole: the path names three places, its last read by
    /// the name it quotes, as a key node of that place is named - and that name is a word a search looks for. A quoted name
    /// that names nothing - an escape no rule writes - and an item are no name: read as written, and no word.
    /// </summary>
    [Fact]
    public void AKeyWhoseNameHoldsADot_IsOnePlace_AndReadBackWhole()
    {
        const string path = "Logging.LogLevel[\"Microsoft.AspNetCore\"]";

        Assert.Equal(path, CodeKeyPaths.Of([("Logging", false), ("LogLevel", false), (CodeKeyPaths.Name("Microsoft.AspNetCore"), true)]));
        Assert.Equal(["Logging", "LogLevel", "[\"Microsoft.AspNetCore\"]"], CodeKeyPaths.Places(path).Select(place => path[place.Start..place.End]));
        Assert.Equal("Microsoft.AspNetCore", CodeKeyPaths.Last(path));
        Assert.Equal("Microsoft.AspNetCore", CodeKeyPaths.LastName(path));

        Assert.Equal(("[0]", "[0]"), (CodeKeyPaths.Last("resources[\"[0]\"]"), CodeKeyPaths.LastName("resources[\"[0]\"]")));
        Assert.Equal(("[\"a\\nb.c\"]", "p"), (CodeKeyPaths.Last("p[\"a\\nb.c\"]"), CodeKeyPaths.LastName("p[\"a\\nb.c\"]")));
        Assert.Equal(("[1]", "limits"), (CodeKeyPaths.Last("limits[1]"), CodeKeyPaths.LastName("limits[1]")));
        Assert.Equal(("[\"\\u0041.b\"]", "p"), (CodeKeyPaths.Last("p[\"\\u0041.b\"]"), CodeKeyPaths.LastName("p[\"\\u0041.b\"]")));
    }

    /// <summary>
    /// .NET's JSON configuration provider joins a key's names by a colon and escapes nothing, so a dot inside a name stays a
    /// dot: <c>Logging:LogLevel:Microsoft.AspNetCore</c> is the spelling of the key named <c>Microsoft.AspNetCore</c>, and
    /// <c>Logging__LogLevel__Microsoft.AspNetCore</c> the environment variable's - not of four nested keys. Each such
    /// spelling reads back as the path; a name that does not stand alone, holds the separator of its own, or is written
    /// where the spelling would read back as an item has none.
    /// </summary>
    [Fact]
    public void AKeyPathWithADottedName_IsSpeltAsDotNetsConfigurationReadsIt_AndANameHoldingASeparatorHasNone()
    {
        const string path = "Logging.LogLevel[\"Microsoft.AspNetCore\"]";

        Assert.Equal(["Logging:LogLevel:Microsoft.AspNetCore", "Logging__LogLevel__Microsoft.AspNetCore"], CodeKeyPaths.Spellings(path));
        Assert.Equal("Logging__LogLevel__Microsoft.AspNetCore", CodeKeyPaths.Environment(path));
        Assert.Equal(["resources:[0]", "resources__[0]"], CodeKeyPaths.Spellings("resources[\"[0]\"]"));
        Assert.Equal(["m:a__b.c"], CodeKeyPaths.Spellings("m[\"a__b.c\"]"));

        Assert.Empty(CodeKeyPaths.Spellings("appSettings[\"a.b:c\"]"));
        Assert.Empty(CodeKeyPaths.Spellings("p[\"\"].x"));
        Assert.Empty(CodeKeyPaths.Spellings("p[\"a\\nb.c\"]"));
        Assert.Empty(CodeKeyPaths.Spellings("p.1"));

        Assert.Equal(path, CodeKeyPaths.Dotted("Logging:LogLevel:Microsoft.AspNetCore"));
        Assert.Equal(path, CodeKeyPaths.Dotted("Logging__LogLevel__Microsoft.AspNetCore"));
        Assert.Equal("resources[\"[0]\"]", CodeKeyPaths.Dotted("resources:[0]"));
    }

    /// <summary>
    /// A string that already holds a place quoted in brackets is a path, never .NET's spelling of one: split at the colon a
    /// quoted name holds, <c>appSettings["a.b:c"]</c> would be read as junk places.
    /// </summary>
    [Fact]
    public void AStringThatHoldsAQuotedPlace_IsNoDotNetSpelling()
    {
        Assert.Null(CodeKeyPaths.Dotted("appSettings[\"a.b:c\"]"));
        Assert.Null(CodeKeyPaths.Dotted("x[\"y\"]:z"));
    }

    /// <summary>
    /// A name written with some of its scopes ends a path only where one of the path's places begins: never inside a
    /// quoted name, whose dot parts nothing, and never inside a place's own text.
    /// </summary>
    /// <param name="path">A qualified name.</param>
    /// <param name="tail">The last places of it.</param>
    /// <param name="ends">Whether the path ends with them at a place.</param>
    [Theory]
    [InlineData("Logging.LogLevel[\"Microsoft.AspNetCore\"]", "[\"Microsoft.AspNetCore\"]", true)]
    [InlineData("Logging.LogLevel[\"Microsoft.AspNetCore\"]", "LogLevel[\"Microsoft.AspNetCore\"]", true)]
    [InlineData("Logging.LogLevel[\"Microsoft.AspNetCore\"]", "AspNetCore\"]", false)]
    [InlineData("a[\"x.b\"].c", "b\"].c", false)]
    [InlineData("Shapes.Canvas.this[]", "Canvas.this[]", true)]
    [InlineData("limits[1].value", "[1].value", true)]
    [InlineData("a.bc", "c", false)]
    [InlineData("a.b", "a.b", false)]
    public void ANameWrittenWithSomeOfItsPlaces_EndsAPathOnlyWhereAPlaceBegins(string path, string tail, bool ends)
        => Assert.Equal(ends, CodeKeyPaths.EndsWithPlaces(path, tail));

    /// <summary>
    /// A path typed with its places quoted needlessly - all of them in brackets - is read as the writer writes it, so one
    /// written form is compared wherever a path is; a path written as the writer writes it, and a name code declares with
    /// brackets of its own, is left as it is.
    /// </summary>
    [Fact]
    public void AQuotedPlaceANameNeedsNot_IsReadAsTheWriterWritesIt()
    {
        Assert.Equal("Logging.LogLevel[\"Microsoft.AspNetCore\"]", CodeKeyPaths.Canonical("[\"Logging\"][\"LogLevel\"][\"Microsoft.AspNetCore\"]"));

        foreach (var path in new[] { "Logging.LogLevel[\"Microsoft.AspNetCore\"]", "limits[1].value", "p[\"\"].x", "Shapes.Canvas.this[]", ".btn" })
        {
            Assert.Equal(path, CodeKeyPaths.Canonical(path));
        }
    }

    /// <summary>
    /// A language's own separators are read as dots outside a quoted name alone: inside one they are the name's own
    /// characters, as a path writes them.
    /// </summary>
    [Fact]
    public void ALanguagesSeparators_AreReadAsDots_OutsideAQuotedNameAlone()
    {
        Assert.Equal("a.b[\"c::d\"].e", CodeKeyPaths.OutsideQuotedNames("a::b[\"c::d\"].e", run => run.Replace("::", ".", StringComparison.Ordinal)));

        Assert.Equal("Vec.operator[]", CodebaseGraphQueries.Dotted("Vec::operator[]"));
        Assert.Equal("Foo.Bar", CodebaseGraphQueries.Dotted(@"\Foo\Bar"));
        Assert.Equal("Foo", CodebaseGraphQueries.Dotted("::Foo"));
        Assert.Equal(".header", CodebaseGraphQueries.Dotted(".header"));
        Assert.Equal("Logging.LogLevel[\"a\\b::c.d\"]", CodebaseGraphQueries.Dotted("Logging.LogLevel[\"a\\b::c.d\"]"));
    }

    /// <summary>
    /// The loose form of a path writes its quoted names as they are, after a dot, as an agent composes a path from the
    /// names a file shows; it is no path, for it does not read back, and the queries alone read it. A name that does not
    /// stand alone, and an item, are kept as written.
    /// </summary>
    [Fact]
    public void APathsLooseForm_WritesItsQuotedNamesAsTheyAre()
    {
        Assert.Equal("Logging.LogLevel.Microsoft.AspNetCore", CodeKeyPaths.Unescaped("Logging.LogLevel[\"Microsoft.AspNetCore\"]"));
        Assert.Equal(".defaults.image", CodeKeyPaths.Unescaped("[\".defaults\"].image"));
        Assert.Equal("Logging.LogLevel.Microsoft.AspNetCore", CodeKeyPaths.Unescaped("Logging.LogLevel['Microsoft.AspNetCore']"));

        foreach (var path in new[] { "p[\"\"].x", "limits[1].value", "p[\"a\\nb.c\"]", "Order.Total" })
        {
            Assert.Equal(path, CodeKeyPaths.Unescaped(path));
        }
    }

    /// <summary>
    /// A place quoted in single quotes is read as the place the double quotes write - in every reader: its places, its last
    /// name, .NET's spellings, the written form, a path's ending and a separator's reading - and never written, the writer
    /// keeping one form. Its own mark is escaped inside it, a backslash and the line breaks as the double-quoted rule
    /// escapes them, and the other mark is an ordinary character; both marks may stand in one path. Each form is strict: an
    /// escape the single-quoted rule never writes names nothing.
    /// </summary>
    [Fact]
    public void ASingleQuotedPlace_IsReadAsTheDoubleQuotedOne_ItsMarkEscaped_InAPathOfBothMarks_AndNeverWritten()
    {
        const string single = "Logging.LogLevel['Microsoft.AspNetCore']";

        Assert.Equal(["Logging", "LogLevel", "['Microsoft.AspNetCore']"], CodeKeyPaths.Places(single).Select(place => single[place.Start..place.End]));
        Assert.Equal("Microsoft.AspNetCore", CodeKeyPaths.Last(single));
        Assert.Equal("Microsoft.AspNetCore", CodeKeyPaths.LastName(single));
        Assert.Equal("Logging.LogLevel[\"Microsoft.AspNetCore\"]", CodeKeyPaths.Canonical(single));
        Assert.Equal(["Logging:LogLevel:Microsoft.AspNetCore", "Logging__LogLevel__Microsoft.AspNetCore"], CodeKeyPaths.Spellings(single));

        Assert.Equal("it's.on", CodeKeyPaths.LastName("Flags['it\\'s.on']"));
        Assert.Equal("Flags[\"it's.on\"]", CodeKeyPaths.Canonical("Flags['it\\'s.on']"));
        Assert.Equal("m[\"say \\\"hi\\\".\"]", CodeKeyPaths.Canonical("m['say \"hi\".']"));
        Assert.Equal(@"a\b.c", CodeKeyPaths.LastName(@"m['a\\b.c']"));

        const string written = "[\"[typescript]\"][\"editor.defaultFormatter\"]";

        Assert.Equal(written, CodeKeyPaths.Canonical("['[typescript]'][\"editor.defaultFormatter\"]"));
        Assert.Equal(written, CodeKeyPaths.Canonical("[\"[typescript]\"]['editor.defaultFormatter']"));
        Assert.Equal(["['[typescript]']", "[\"editor.defaultFormatter\"]"], CodeKeyPaths.Places("['[typescript]'][\"editor.defaultFormatter\"]").Select(place => "['[typescript]'][\"editor.defaultFormatter\"]"[place.Start..place.End]));
        Assert.Equal(["[\"[typescript]\"]", "['editor.defaultFormatter']"], CodeKeyPaths.Places("[\"[typescript]\"]['editor.defaultFormatter']").Select(place => "[\"[typescript]\"]['editor.defaultFormatter']"[place.Start..place.End]));

        Assert.Null(CodeKeyPaths.Dotted("appSettings['a.b:c']"));
        Assert.True(CodeKeyPaths.EndsWithPlaces("Logging.LogLevel[\"Microsoft.AspNetCore\"]", "['Microsoft.AspNetCore']"));
        Assert.True(CodeKeyPaths.EndsWithPlaces("Logging.LogLevel[\"Microsoft.AspNetCore\"]", "LogLevel['Microsoft.AspNetCore']"));
        Assert.Equal("a.b['c::d'].e", CodeKeyPaths.OutsideQuotedNames("a::b['c::d'].e", run => run.Replace("::", ".", StringComparison.Ordinal)));

        Assert.Equal("['a\\\"b.c']", CodeKeyPaths.Last("m['a\\\"b.c']"));
        Assert.Equal("m", CodeKeyPaths.LastName("m['a\\\"b.c']"));

        Assert.Equal("[\"it's.x\"]", CodeKeyPaths.Name("it's.x"));
    }
}
