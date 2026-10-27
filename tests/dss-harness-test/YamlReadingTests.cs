using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Tests;

/// <summary>
/// The one YAML reader: what it holds a file to only where its reader asks - the bounds the workflow reader asks and
/// the action reader does not - and how it reads a boolean, which every reader shares.
/// </summary>
public sealed class YamlReadingTests
{
    /// <summary>The bounds a test reader asks, worded as a reader words its own.</summary>
    private static readonly YamlBounds Bounds = new(3, "a test file writes each part out", "no test file does");

    /// <summary>
    /// An alias is read as YAML reads it where the reader asks no bounds - the action reader's posture - and refused,
    /// in the reader's own words, with the line it is met at, where the reader asks them: the one difference two readers
    /// of one file may have, and it is the reader's to ask.
    /// </summary>
    [Fact]
    public void AnAlias_IsReadWhereNoBoundsAreAsked_AndRefusedWhereTheyAre()
    {
        const string Text = "first: &shared value\nsecond: *shared\n";
        var unbounded = new List<string>();
        var bounded = new List<string>();

        var read = YamlReading.Root(Text, "one", unbounded);
        var refused = YamlReading.Root(Text, "one", bounded, Bounds);

        Assert.Empty(unbounded);
        Assert.NotNull(read);
        Assert.Equal("value", YamlReading.ScalarOf(read.Children[new YamlScalarNode("second")]));

        Assert.Null(refused);
        Assert.Equal(["line 2, column 9: the alias '*shared' repeats a part of the file by reference; a test file writes each part out."], bounded);
    }

    /// <summary>
    /// Loading says where and why a text is no document of one mapping - its fault's line and column, a bound's, the top
    /// that is no mapping - and the whole file's where it is empty or of several documents: what Root's refusals say.
    /// </summary>
    [Fact]
    public void Loading_SaysWhereAndWhyATextIsNoMapping()
    {
        Assert.NotNull(YamlReading.Load("a: 1\n", "one", Bounds, out var none));
        Assert.Null(none);

        Assert.Null(YamlReading.Load("a: [\n", "one", null, out var broken));
        Assert.Equal((2, 1), (broken!.Line, broken.Column));

        Assert.Null(YamlReading.Load("a: &x 1\nb: *x\n", "one", Bounds, out var alias));
        Assert.Equal((2, 4, "the alias '*x' repeats a part of the file by reference; a test file writes each part out."), (alias!.Line, alias.Column, alias.Why));

        Assert.Null(YamlReading.Load("- a\n", "one", null, out var list));
        Assert.Equal((1, 1, "the file's top level is not a mapping of keys."), (list!.Line, list.Column, list.Why));

        Assert.Null(YamlReading.Load("", "one", null, out var empty));
        Assert.Equal((0, 0, "the file is empty."), (empty!.Line, empty.Column, empty.Why));

        Assert.Null(YamlReading.Load("a: 1\n---\nb: 2\n", "a test file is one document", null, out var several));
        Assert.Equal((0, 0, "the file holds 2 YAML documents; a test file is one document."), (several!.Line, several.Column, several.Why));
    }

    /// <summary>
    /// Nesting past the bound a reader asks is refused before the file is read into nodes, at the place it passes the
    /// bound; nesting to the bound is read, and nesting past it is read where no bounds are asked.
    /// </summary>
    [Fact]
    public void NestingPastTheAskedBound_IsRefusedWhereItPassesIt()
    {
        var atTheBound = new List<string>();
        var pastIt = new List<string>();
        var unbounded = new List<string>();

        Assert.NotNull(YamlReading.Root("a: [[1]]\n", "one", atTheBound, Bounds));
        Assert.Null(YamlReading.Root("a: [[[1]]]\n", "one", pastIt, Bounds));
        Assert.NotNull(YamlReading.Root("a: [[[1]]]\n", "one", unbounded));

        Assert.Empty(atTheBound);
        Assert.Equal(["line 1, column 6: the file nests more than 3 levels deep; no test file does."], pastIt);
        Assert.Empty(unbounded);
    }

    /// <summary>
    /// A boolean is the word true or false as <see cref="bool.TryParse(string, out bool)"/> reads it, in any case; any
    /// other word YAML can mean one by is refused rather than read as false, naming what it was meant to be.
    /// </summary>
    [Theory]
    [InlineData("true", true)]
    [InlineData("False", false)]
    [InlineData("TRUE", true)]
    [InlineData("yes", null)]
    [InlineData("on", null)]
    public void ABoolean_IsTrueOrFalseInAnyCase_AndNothingElse(string written, bool? read)
    {
        var problems = new List<string>();
        var root = YamlReading.Root($"guard: {written}\n", "one", problems)!;

        Assert.Equal(read, YamlReading.RequireBoolean(root.Children[new YamlScalarNode("guard")], "guard", problems));
        Assert.Equal(read is null ? new[] { $"line 1, column 8: 'guard' is '{written}', which is not true or false." } : [], problems);
    }
}
