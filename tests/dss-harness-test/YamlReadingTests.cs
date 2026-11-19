using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Tests;

/// <summary>
/// The one YAML reader: what it holds a file to only where its reader asks - the rules the workflow reader asks and the
/// action reader does not - that it reads a file however deep it nests, each alias as the node its anchor names, and how
/// it reads a boolean, which every reader shares.
/// </summary>
public sealed class YamlReadingTests
{
    /// <summary>The rules a test reader asks, worded as a reader words its own.</summary>
    private static readonly YamlRules Rules = new("a test file writes each part out");

    /// <summary>
    /// An alias is read as YAML reads it where the reader asks no rule of aliases - the action reader's posture - and
    /// refused, in the reader's own words, with the line it is met at, where the reader asks one, as the workflow reader
    /// does: the one difference two readers of one file may have, and it is the reader's to ask.
    /// </summary>
    [Fact]
    public void AWorkflowsAliasRefusal_StillHolds()
    {
        const string Text = "first: &shared value\nsecond: *shared\n";
        var unruled = new List<string>();
        var aliasesRead = new List<string>();
        var refusing = new List<string>();

        var read = YamlReading.Root(Text, "one", unruled);
        var readUnderRules = YamlReading.Root(Text, "one", aliasesRead, new YamlRules(NoAlias: null));
        var refused = YamlReading.Root(Text, "one", refusing, Rules);

        Assert.Empty(unruled);
        Assert.Equal("value", YamlReading.ScalarOf(read!.Children[new YamlScalarNode("second")]));
        Assert.Empty(aliasesRead);
        Assert.NotNull(readUnderRules);

        Assert.Null(refused);
        Assert.Equal(["line 2, column 9: the alias '*shared' repeats a part of the file by reference; a test file writes each part out."], refusing);
    }

    /// <summary>
    /// An alias is the node its anchor names - the very node, never a copy of it - so what an anchor names is read once,
    /// however many aliases name it.
    /// </summary>
    [Fact]
    public void AnAlias_IsItsAnchorsNode()
    {
        var problems = new List<string>();
        var root = YamlReading.Root("first: &shared {a: 1}\nsecond: *shared\nthird: *shared\n", "one", problems)!;

        Assert.Empty(problems);
        Assert.IsType<YamlMappingNode>(root.Children[new YamlScalarNode("first")]);
        Assert.Same(root.Children[new YamlScalarNode("first")], root.Children[new YamlScalarNode("second")]);
        Assert.Same(root.Children[new YamlScalarNode("first")], root.Children[new YamlScalarNode("third")]);
    }

    /// <summary>
    /// Loading says where and why a text is no document of one mapping - its fault's line and column, a rule's, the top
    /// that is no mapping - and the whole file's where it is empty or of several documents: what Root's refusals say.
    /// </summary>
    [Fact]
    public void Loading_SaysWhereAndWhyATextIsNoMapping()
    {
        Assert.NotNull(YamlReading.Load("a: 1\n", "one", Rules, out var none));
        Assert.Null(none);

        Assert.Null(YamlReading.Load("a: [\n", "one", null, out var broken));
        Assert.Equal((2, 1), (broken!.Line, broken.Column));

        Assert.Null(YamlReading.Load("a: &x 1\nb: *x\n", "one", Rules, out var alias));
        Assert.Equal((2, 4, "the alias '*x' repeats a part of the file by reference; a test file writes each part out."), (alias!.Line, alias.Column, alias.Why));

        Assert.Null(YamlReading.Load("- a\n", "one", null, out var list));
        Assert.Equal((1, 1, "the file's top level is not a mapping of keys."), (list!.Line, list.Column, list.Why));

        Assert.Null(YamlReading.Load("", "one", null, out var empty));
        Assert.Equal((0, 0, "the file is empty."), (empty!.Line, empty.Column, empty.Why));

        Assert.Null(YamlReading.Load("a: 1\n---\nb: 2\n", "a test file is one document", null, out var several));
        Assert.Equal((0, 0, "the file holds 2 YAML documents; a test file is one document."), (several!.Line, several.Column, several.Why));

        // A key written twice is said where the second is; a fault the reader says no place of is said of the whole file,
        // never of a line it would make up.
        Assert.Null(YamlReading.Load("a: 1\nb: 2\na: 3\n", "one", null, out var twice));
        Assert.Equal((3, 1, "the file is not valid YAML (Duplicate key a)."), (twice!.Line, twice.Column, twice.Why));

        Assert.Null(YamlReading.Load("a: 1\nb: !<> c\n", "one", null, out var placeless));
        Assert.Equal((0, 0, "the file does not read as YAML, at a place its reader does not say (Tag value must not be empty. (Parameter 'value'))."), (placeless!.Line, placeless.Column, placeless.Why));
    }

    /// <summary>
    /// A mapping nested twenty thousand levels deep is read whole, every level a mapping and the scalar at its bottom
    /// read, on a stack of a megabyte - which the representation model's recursion, read where it stands, passes a few
    /// thousand levels in. Twenty thousand, not more, because the parser takes time that grows with the square of how
    /// deep brackets nest.
    /// </summary>
    [Fact]
    public async Task AMappingTwentyThousandDeep_Loads()
    {
        Assert.Equal(new ChildRunAnswer(0, "20001 1", ""), await ChildRun.MethodAsync(typeof(YamlReadingTests), nameof(MappingTwentyThousandDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AMappingTwentyThousandDeep_Loads"/>: how many mappings deep the read file goes, and what its bottom holds.</summary>
    internal static string MappingTwentyThousandDeep() => SmallStack.Run(() =>
    {
        var problems = new List<string>();
        var root = YamlReading.Root($"a: {string.Concat(Enumerable.Repeat("{a: ", 20_000))}1{new string('}', 20_000)}\n", "one", problems);
        var levels = 0;
        YamlNode? node = root;

        while (node is YamlMappingNode mapping)
        {
            levels++;
            node = mapping.Children[new YamlScalarNode("a")];
        }

        Assert.Empty(problems);
        return $"{levels} {YamlReading.ScalarOf(node!)}";
    });

    /// <summary>
    /// A list nested a hundred thousand levels deep is read whole on a stack of a megabyte, which the representation
    /// model's recursion, read where it stands, passes a few thousand levels in.
    /// </summary>
    [Fact]
    public async Task AListAHundredThousandDeep_Loads()
    {
        Assert.Equal(new ChildRunAnswer(0, "100000 1", ""), await ChildRun.MethodAsync(typeof(YamlReadingTests), nameof(ListAHundredThousandDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AListAHundredThousandDeep_Loads"/>: how many lists deep the read value goes, and what its bottom holds.</summary>
    internal static string ListAHundredThousandDeep() => SmallStack.Run(() =>
    {
        var problems = new List<string>();
        var root = YamlReading.Root($"a:\n  {string.Concat(Enumerable.Repeat("- ", 100_000))}1\n", "one", problems);
        var levels = 0;
        YamlNode? node = root!.Children[new YamlScalarNode("a")];

        while (node is YamlSequenceNode sequence)
        {
            levels++;
            node = Assert.Single(sequence.Children);
        }

        Assert.Empty(problems);
        return $"{levels} {YamlReading.ScalarOf(node!)}";
    });

    /// <summary>
    /// A text that stops reading as YAML a hundred thousand levels deep is refused in the parser's own words, at the place
    /// it stops - never a crash, on a stack of a megabyte: the representation model recurses that deep before it meets the
    /// fault, so its depth is counted to the place reading stopped, not dropped with what follows.
    /// </summary>
    [Fact]
    public async Task AListThatStopsReadingAHundredThousandDeep_IsRefusedInTheParsersWords()
    {
        Assert.Equal(
            new ChildRunAnswer(0, "line 3, column 1: the file is not valid YAML (While parsing a node, did not find expected node content.).", ""),
            await ChildRun.MethodAsync(typeof(YamlReadingTests), nameof(ListThatStopsReadingAHundredThousandDeep), TestContext.Current.CancellationToken));
    }

    /// <summary>The child of <see cref="AListThatStopsReadingAHundredThousandDeep_IsRefusedInTheParsersWords"/>: the refusal.</summary>
    internal static string ListThatStopsReadingAHundredThousandDeep() => SmallStack.Run(() =>
    {
        var problems = new List<string>();

        Assert.Null(YamlReading.Root($"a:\n  {string.Concat(Enumerable.Repeat("- ", 100_000))}[\n", "one", problems));
        return Assert.Single(problems);
    });

    /// <summary>
    /// An alias in a key that names a mapping or a list is refused where it is met, before the file is read: the model
    /// hashes a key by walking all of it, so such a key would be walked again at each alias, and one whose node holds
    /// itself - a list or a mapping that names itself - for ever, past any stack. An alias of a scalar in a key is read.
    /// </summary>
    [Fact]
    public async Task AKeyThatHoldsWhatAnAliasNames_IsRefusedBeforeItIsRead()
    {
        const string Why = "makes part of a key of a mapping or a list; a key is a plain name, and such a key would be read by walking all it names again at each alias - without end where it holds itself - so the file is not read.";

        Assert.Equal(
            new ChildRunAnswer(0, $"line 1, column 7: the alias '*x' {Why}\nline 1, column 10: the alias '*x' {Why}\nline 2, column 4: the alias '*x' {Why}", ""),
            await ChildRun.MethodAsync(typeof(YamlReadingTests), nameof(KeysThatHoldWhatAnAliasNames), TestContext.Current.CancellationToken));

        var problems = new List<string>();
        var root = YamlReading.Root("a: &k name\n*k : 2\n", "one", problems)!;

        Assert.Empty(problems);
        Assert.Equal([("a", "name"), ("name", "2")], root.Children.Select(pair => (YamlReading.ScalarOf(pair.Key), YamlReading.ScalarOf(pair.Value))));
    }

    /// <summary>
    /// The child of <see cref="AKeyThatHoldsWhatAnAliasNames_IsRefusedBeforeItIsRead"/>: the refusals of a list key that
    /// holds itself, a mapping key that holds itself, and a key that holds a mapping anchored before it.
    /// </summary>
    internal static string KeysThatHoldWhatAnAliasNames() => SmallStack.Run(() =>
    {
        var problems = new List<string>();

        Assert.Null(YamlReading.Root("? &x [*x]\n: 1\n", "one", problems));
        Assert.Null(YamlReading.Root("? &x {k: *x}\n: 1\n", "one", problems));
        Assert.Null(YamlReading.Root("a: &x {k: 1}\n? [*x]\n: 1\n", "one", problems));
        return string.Join('\n', problems);
    });

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
