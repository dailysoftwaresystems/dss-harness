using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>
/// What an action's input takes - its pattern held to the whole value, its items counted between separators - and the one
/// rule every input is given its value by and held to it, before a run's legs are placed and as each leg's runner starts.
/// </summary>
public sealed class ActionInputsTests
{
    /// <summary>
    /// An item is the text between separators with white space trimmed from either end, and one left empty is not counted:
    /// a run of spaces parts as one space does, a doubled, leading or trailing separator adds nothing, a tab parts nothing
    /// a space does not, a value with no separator is one item and an empty or blank one none.
    /// </summary>
    [Theory]
    [InlineData(" ", "a.c b.c c.c", 3)]
    [InlineData(" ", "a.c   b.c", 2)]
    [InlineData(" ", "  a.c b.c  ", 2)]
    [InlineData(" ", "a.c\tb.c", 1)]
    [InlineData(" ", "a.c", 1)]
    [InlineData(",", "a, b,, c,", 3)]
    [InlineData(",", "a b", 1)]
    [InlineData(",", "", 0)]
    [InlineData(",", " , ", 0)]
    [InlineData("\n", "a\nb\n\n", 2)]
    [InlineData(" | ", "a | b|c", 2)]
    public void AnInputsItems_AreTheTrimmedTextBetweenSeparators_EmptyOnesNotCounted(string separator, string value, int items)
        => Assert.Equal(items, new ActionInput("sources", null, false, null) { Separator = separator }.Items(value));

    /// <summary>
    /// A pattern is held to the whole value - a match inside it is no match - as one group, so an alternation is held whole
    /// too, and case and all; a value it takes is refused for nothing.
    /// </summary>
    [Theory]
    [InlineData("[0-9]+", "12", null)]
    [InlineData("[0-9]+", "12a", "is '12a', which its pattern '[0-9]+' does not match")]
    [InlineData("[0-9]+", "a12", "is 'a12', which its pattern '[0-9]+' does not match")]
    [InlineData("a|ab", "ab", null)]
    [InlineData("fast|full", "fastest", "is 'fastest', which its pattern 'fast|full' does not match")]
    [InlineData("[a-z]+", "ABC", "is 'ABC', which its pattern '[a-z]+' does not match")]
    [InlineData("(?i)[a-z]+", "ABC", null)]
    [InlineData("[a-z]*", "", null)]
    [InlineData("[a-z]+", "", "is '', which its pattern '[a-z]+' does not match")]
    public void AnInputsPattern_IsHeldToTheWholeValue(string pattern, string value, string? refusal)
        => Assert.Equal(refusal, new ActionInput("mode", null, false, null) { Pattern = pattern }.Refusal(value));

    /// <summary>
    /// A list's items are held to its bounds, the fewest and the most each alone or together, an empty value holding none;
    /// a count inside them is refused for nothing, and a refusal names the count, the separator and the bound.
    /// </summary>
    [Theory]
    [InlineData(null, 32, "a b c", null)]
    [InlineData(null, 2, "a b c", "holds 3 item(s) parted by ' ', and it takes at most 2")]
    [InlineData(2, null, "a", "holds 1 item(s) parted by ' ', and it takes at least 2")]
    [InlineData(1, null, "  ", "holds 0 item(s) parted by ' ', and it takes at least 1")]
    [InlineData(1, 3, "a  b c", null)]
    [InlineData(1, 3, "a b c d", "holds 4 item(s) parted by ' ', and it takes at most 3")]
    public void AListsItems_AreHeldToItsBounds(int? fewest, int? most, string value, string? refusal)
        => Assert.Equal(refusal, new ActionInput("sources", null, false, null) { Separator = " ", MinItems = fewest, MaxItems = most }.Refusal(value));

    /// <summary>A separator a message cannot show as itself - a line break, a tab - is quoted as its escape.</summary>
    [Fact]
    public void ASeparatorThatIsALineBreak_IsQuotedAsItsEscape()
        => Assert.Equal(
            "holds 3 item(s) parted by '\\r\\n', and it takes at most 2",
            new ActionInput("sources", null, false, null) { Separator = "\r\n", MaxItems = 2 }.Refusal("a\r\nb\r\nc"));

    /// <summary>
    /// An input's value is what --input gave it, else what the runner's .env holds under its name, else its default -
    /// a step's own inputs by the same rule, kept by step - and an input with none of them has no value.
    /// </summary>
    [Fact]
    public void AnInputsValue_IsTheCommandLinesThenTheEnvsThenItsDefault()
    {
        var resolved = ActionInputs.Resolve(
            File(
                [new("typed", "default", false, null), new("read", "default", false, null), new("fallen", "default", false, null), new("none", null, false, null)],
                [new("own", null, false, null)]),
            Given(("typed", "given"), ("own", "mine")),
            Given(("typed", "env"), ("read", "env")));

        Assert.Equal(Given(("typed", "given"), ("read", "env"), ("fallen", "default")), resolved.Action);
        Assert.Equal(Given(("own", "mine")), Assert.Single(resolved.Steps, step => step.Key == "build").Value);
        Assert.Empty(resolved.Missing);
        Assert.Empty(resolved.Refused);
    }

    /// <summary>
    /// A required input with no value is missing - named with its step where it is a step's own - and each value its input
    /// refuses is said with where it came from: the command line, the runner's .env or the default.
    /// </summary>
    [Fact]
    public void ARequiredInputWithNoValue_IsMissing_AndAValueItsInputRefuses_IsSaidWithWhereItCameFrom()
    {
        var digits = new ActionInput("typed", "1", false, null) { Pattern = "[0-9]+" };

        var resolved = ActionInputs.Resolve(
            File(
                [new("root", null, true, null), digits, digits with { Name = "read" }, digits with { Name = "fallen", Default = "x" }],
                [new("size", null, true, null)]),
            Given(("typed", "one")),
            Given(("read", "two")));

        Assert.Equal(["root", "size (step 'build')"], resolved.Missing);
        Assert.Equal(
            [
                "input 'typed', given with --input, is 'one', which its pattern '[0-9]+' does not match",
                "input 'read', from the runner's .env, is 'two', which its pattern '[0-9]+' does not match",
                "input 'fallen', its default, is 'x', which its pattern '[0-9]+' does not match",
            ],
            resolved.Refused);
    }

    /// <summary>
    /// For steps another machine runs, whose .env is its own, only what --input gave is resolved and held to its input -
    /// it reaches every machine alike - and an input given nothing is that machine's to resolve: never missing here,
    /// never given this machine's value or its default.
    /// </summary>
    [Fact]
    public void ForAnotherMachine_OnlyWhatTheCommandLineGaveIsResolvedAndHeld()
    {
        var resolved = ActionInputs.Resolve(
            File([new("root", null, true, null), new("size", "big", false, null) { Pattern = "[0-9]+" }, new("mode", null, false, null) { Pattern = "fast|full" }], []),
            Given(("mode", "slow")),
            values: null);

        Assert.Equal(Given(("mode", "slow")), resolved.Action);
        Assert.Empty(resolved.Missing);
        Assert.Equal(["input 'mode', given with --input, is 'slow', which its pattern 'fast|full' does not match"], resolved.Refused);
    }

    /// <summary>
    /// Every missing input and every refused value is refused in one usage error, the missing first, with where a value
    /// could have come from, and a secret a quoted value holds masked; with none, nothing is refused.
    /// </summary>
    [Fact]
    public void MissingAndRefusedInputs_AreRefusedTogether_AsAUsageError()
    {
        ActionInputs.Require("compile.yml", "/repo/.harness-config/runner/.env", [], [], text => text);

        var refusal = Assert.Throws<HarnessException>(() => ActionInputs.Require(
            "compile.yml",
            "/repo/.harness-config/runner/.env",
            ["root", "size (step 'build')"],
            ["input 'token', given with --input, is 'hunter2', which its pattern '[0-9]+' does not match", "input 'sources', given with --input, holds 3 item(s) parted by ' ', and it takes at most 2"],
            text => text.Replace("hunter2", "***", StringComparison.Ordinal)));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(
            "'compile.yml' requires input(s) root, size (step 'build'), and none was given with --input, found in "
                + "/repo/.harness-config/runner/.env or declared as a default, so its steps would run with nothing where a value "
                + "belongs. 'compile.yml' declares what its inputs take, and this run's are not that: input 'token', given with "
                + "--input, is '***', which its pattern '[0-9]+' does not match; input 'sources', given with --input, holds 3 "
                + "item(s) parted by ' ', and it takes at most 2. Nothing was run.",
            refusal.Message);
    }

    /// <summary>An action whose inputs are <paramref name="inputs"/>, with one step, 'build', declaring <paramref name="own"/>.</summary>
    private static ActionFile File(IReadOnlyList<ActionInput> inputs, IReadOnlyList<ActionInput> own)
    {
        var file = ActionKit.Parse("compile.yml", "steps:\n  - name: build\n    run: cmake --build build\n");

        return file with { Inputs = inputs, Steps = [file.Steps[0] with { Inputs = own }] };
    }

    private static Dictionary<string, string> Given(params (string Name, string Value)[] pairs)
        => pairs.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
}
