using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Schemas;

namespace DssHarness.Tests;

/// <summary>
/// One expression as a tree: every form the grammar spells, joined and compared as it says, <c>any</c> binding
/// <c>it</c> to the member it tests, and the tree's reads the reads the scanner finds.
/// </summary>
public sealed class FlowExpressionTests
{
    /// <summary>Every form of the grammar parses to the node that says it, a literal holding the value it writes - a string's without its quotes.</summary>
    [Fact]
    public void EveryFormOfTheGrammar_ParsesToItsNode()
    {
        Assert.IsType<FlowExpression.Path>(FlowExpression.Parse("plan.ok"));
        Assert.Equal(new FlowExpression.Literal(true), FlowExpression.Parse("true"));
        Assert.Equal(new FlowExpression.Literal(false), FlowExpression.Parse("false"));
        Assert.Equal(new FlowExpression.Literal(42L), FlowExpression.Parse("42"));
        Assert.Equal(new FlowExpression.Literal(0.5), FlowExpression.Parse("0.5"));
        Assert.Equal(new FlowExpression.Literal("approved"), FlowExpression.Parse("'approved'"));
        Assert.Equal(new FlowExpression.Literal("approved"), FlowExpression.Parse("\"approved\""));
        Assert.Equal(new FlowExpression.Literal(null), FlowExpression.Parse("null"));
        Assert.IsType<FlowExpression.Not>(FlowExpression.Parse("!plan.ok"));
        Assert.IsType<FlowExpression.Empty>(FlowExpression.Parse("empty(plan.findings)"));
        Assert.Equal(new FlowExpression.Placeholder("plan.title"), FlowExpression.Parse("{plan.title}"));
        Assert.IsType<FlowExpression.And>(FlowExpression.Parse("(plan.ok || plan.late) && !empty(plan.findings)"));
    }

    /// <summary>A path carries its keys and its indexes, and the root says where it starts.</summary>
    [Fact]
    public void APathCarriesItsKeysAndItsIndexes()
    {
        var path = (FlowExpression.Path)FlowExpression.Parse("previous.fix.findings[2].severity");

        Assert.Equal("previous", path.Root);
        Assert.Equal("previous.fix.findings[2].severity", path.Whole);
        Assert.Equal(["fix", "findings", "severity"], path.Steps.Where(step => step.IsKey).Select(step => step.Key));
        Assert.Equal([2], path.Steps.Where(step => !step.IsKey).Select(step => step.Index));
    }

    /// <summary>Precedence as the grammar spells it: <c>||</c> loosest, then <c>&amp;&amp;</c>, then comparisons over <c>!</c> over <c>??</c>.</summary>
    [Fact]
    public void OperatorsBindAsTheGrammarSays()
    {
        var either = (FlowExpression.Or)FlowExpression.Parse("a || b && c");

        Assert.Equal("a", ((FlowExpression.Path)either.Terms[0]).Root);
        Assert.IsType<FlowExpression.And>(either.Terms[1]);

        var compared = (FlowExpression.Compare)FlowExpression.Parse("!a == b");

        Assert.IsType<FlowExpression.Not>(compared.Left);
        Assert.IsType<FlowExpression.Path>(compared.Right);
        Assert.Equal("==", compared.Operator);

        var coalesced = (FlowExpression.Compare)FlowExpression.Parse("a ?? b != c");

        Assert.IsType<FlowExpression.Coalesce>(coalesced.Left);
        Assert.Equal("!=", coalesced.Operator);
    }

    /// <summary><c>any(list, test)</c> holds both of its sides, and <c>it</c> inside the test is read as of the member.</summary>
    [Fact]
    public void AnyHoldsItsListAndItsTest_BindingItToTheMember()
    {
        var any = (FlowExpression.Any)FlowExpression.Parse("any(review.findings, it.production && it.severity != 'nit')");

        Assert.Equal("review", ((FlowExpression.Path)any.List).Root);

        var reads = any.Reads();

        Assert.Contains(reads, read => read.Root == "review" && read.InsideAny);
        Assert.Contains(reads, read => read.Root == "it" && read.InsideAny);
    }

    /// <summary>Where an expression parses, its reads here are the reads the scanner finds: the two cannot drift.</summary>
    [Fact]
    public void TheTreesReads_AreTheReadsTheScannerFinds()
    {
        string[] expressions =
        [
            "plan.ok",
            "plan.ok && build.passed",
            "plan.ok || build.passed || test.passed",
            "!empty(plan.findings) && plan.ok",
            "previous.fix.ok",
            "input.request",
            "run.status == 'late'",
            "review.severity != 'low'",
            "plan.title ?? 'untitled'",
            "{plan.title} == 'x'",
            "any(review.findings, it.severity != 'low')",
            "empty(input.request) || run.leftover",
            "source.code == 0",
            "findings[0].severity == 'high'",
            "any(review.findings[2].notes, it.ok) && plan.items[0][1]",
        ];

        foreach (var expression in expressions)
        {
            Assert.Equal(
                [.. WorkflowExpressions.ReadsOf(expression).Select(read => (read.Root, read.Whole, read.Template, read.InsideAny))],
                [.. FlowExpression.Parse(expression).Reads().Select(read => (read.Root, read.Whole, read.Template, read.InsideAny))]);
        }
    }

    /// <summary>
    /// A path with an index is one read, in the tree and in the scanner alike: its whole name keeps the index, its
    /// root is its first name, and its names - what a lookup walks - leave the index out.
    /// </summary>
    [Fact]
    public void AnIndexedPath_IsOneRead_WhoseRootIsAName()
    {
        var tree = Assert.Single(FlowExpression.Parse("findings[0].severity").Reads());
        var scanned = Assert.Single(WorkflowExpressions.ReadsOf("findings[0].severity"));

        Assert.Equal(new WorkflowExpressions.Read("findings", "findings[0].severity", Template: false, InsideAny: false), tree);
        Assert.Equal(tree, scanned);
        Assert.Equal(["findings", "severity"], scanned.Names);
    }

    /// <summary>
    /// A path is written whole: a space before its dot or its bracket ends it, so what follows is no part of an
    /// expression - in a folded scalar too - and the tree never reads as one name what the scanner reads as two.
    /// </summary>
    [Theory]
    [InlineData("review .findings", "column 8: '.' is not part of an expression")]
    [InlineData("review\n.findings", "column 8: '.' is not part of an expression")]
    [InlineData("findings [0]", "column 10: '[' is not part of an expression")]
    public void APathWithASpaceInIt_IsRefused(string text, string problem)
        => Assert.Equal(problem, FlowExpression.GrammarProblem(text));

    /// <summary>
    /// An index is a whole number from 0, written in digits alone: a sign, a space or a leading zero would let one
    /// place in a list be written two ways, and is refused naming the column of its bracket.
    /// </summary>
    [Theory]
    [InlineData("findings[+1]")]
    [InlineData("findings[ 1]")]
    [InlineData("findings[1 ]")]
    [InlineData("findings[01]")]
    [InlineData("findings[-1]")]
    [InlineData("findings[x]")]
    [InlineData("findings[1")]
    public void AnIndexWrittenAnyOtherWay_IsRefused(string text)
        => Assert.Equal("the index at column 9 is not a whole number from 0, written in digits alone with no leading zero", FlowExpression.GrammarProblem(text));

    /// <summary>What the grammar does not say is refused, naming where it stopped, every refusal said whole.</summary>
    [Theory]
    [InlineData("plan +", "column 6: '+' is not part of an expression")]
    [InlineData("(plan.ok", "the expression ends before its ')' at column 9")]
    [InlineData("'unclosed", "the quote at column 1 is never closed")]
    [InlineData("plan.ok)", "column 8: ')' is not part of an expression")]
    [InlineData("@broken", "column 1: '@' does not start a value")]
    [InlineData("previous", "column 1: 'previous' names a step's work; say which one")]
    [InlineData("plan &&", "the expression ends at column 8 where a value was to be read")]
    public void WhatTheGrammarDoesNotSay_IsRefusedNamingTheColumn(string text, string said)
    {
        var problem = Assert.Throws<ArgumentException>(() => FlowExpression.Parse(text));

        Assert.Equal("text", problem.ParamName);
        Assert.Equal(said, FlowExpression.GrammarProblem(text));
    }

    /// <summary>
    /// Nothing nests more than 64 deep - a group, a '!', an empty(...) and an any(...) inside another, and each name of
    /// a path after its first, go one deeper each - so no file can spend the stack however it is written: 64 is read,
    /// and one more is refused.
    /// </summary>
    [Theory]
    [InlineData(64, null)]
    [InlineData(65, "the expression nests more than 64 deep")]
    public void NothingNestsMoreThanSixtyFourDeep(int levels, string? problem)
    {
        Assert.Equal(64, FlowExpression.MostNesting);
        Assert.Equal(problem, FlowExpression.GrammarProblem(new string('(', levels) + "plan" + new string(')', levels)));
        Assert.Equal(problem, FlowExpression.GrammarProblem(new string('!', levels - 1) + "plan.ok"));
        Assert.Equal(problem, FlowExpression.GrammarProblem("plan" + string.Concat(Enumerable.Repeat(".ok", levels))));
        Assert.Equal(problem, FlowExpression.GrammarProblem(string.Concat(Enumerable.Repeat("empty(", levels)) + "plan" + new string(')', levels)));
    }

    /// <summary>A plan's output as the operator tests read it: a text, a text or null, a boolean, a whole number, a list of objects, an object.</summary>
    private static readonly IReadOnlyDictionary<string, JsonSchemaSubset.Reading> Plan = new Dictionary<string, JsonSchemaSubset.Reading>(StringComparer.Ordinal)
    {
        ["plan.title"] = JsonSchemaSubset.Reading.Of("string"),
        ["plan.maybe"] = JsonSchemaSubset.Reading.Of("string", "null"),
        ["plan.ok"] = JsonSchemaSubset.Reading.Of("boolean"),
        ["plan.flag"] = JsonSchemaSubset.Reading.Of("boolean", "null"),
        ["plan.count"] = JsonSchemaSubset.Reading.Of("integer"),
        ["plan.items"] = JsonSchemaSubset.Read(JsonNode.Parse("""
            {"type":"array","items":{"type":"object","additionalProperties":false,"properties":{"title":{"type":"string"},"ok":{"type":"boolean"}}}}
            """)!),
        ["plan.meta"] = JsonSchemaSubset.Reading.Of("object"),
    };

    /// <summary>Every misuse <paramref name="expression"/> makes, its paths read as <see cref="Plan"/> says and any other as anything.</summary>
    private static IReadOnlyList<string> Misuses(string expression)
    {
        var problems = new List<string>();

        _ = FlowExpression.Parse(expression).TypeOf(path => Plan.GetValueOrDefault(path.Whole) ?? JsonSchemaSubset.Reading.Anything, problems.Add);
        return problems;
    }

    /// <summary>
    /// Each operator is held to what it takes when the definition is loaded, as part 04's table says: '!', '&amp;&amp;'
    /// and '||' a boolean, '==' and '!=' no list and no object - two scalars of different kinds are unequal, never an
    /// error - and any a list and a test that yields a boolean for each item, whose 'it' is an item of that list. '??'
    /// and empty(...) take anything. A value that may be what the operator takes - one that may be null, or whose kind
    /// nothing says - is the run's to read.
    /// </summary>
    [Fact]
    public void EachOperator_IsHeldToWhatItTakes()
    {
        Assert.Equal(
            ["applies ! to 'plan.title', which is a text; ! takes a boolean, and nothing is read as true or as false but true and false"],
            Misuses("!plan.title"));
        Assert.Equal(
            ["applies ! to 'null', which is null; ! takes a boolean, and nothing is read as true or as false but true and false"],
            Misuses("!null"));
        Assert.Equal(
            [
                "joins 'plan.title', which is a text, with &&; && joins booleans, and nothing is read as true or as false but true and false",
                "joins 'plan.items', which is a list, with ||; || joins booleans, and nothing is read as true or as false but true and false",
            ],
            Misuses("plan.title && plan.ok || plan.items"));
        Assert.Equal(
            [
                "compares 'plan.items', which is a list, with ==; == compares two scalars, or null, and a list or an object is never one",
                "compares 'plan.meta', which is an object, with ==; == compares two scalars, or null, and a list or an object is never one",
            ],
            Misuses("plan.items == plan.meta"));
        Assert.Equal(
            ["looks into 'plan.title' with any, and it is a text; any looks into a list"],
            Misuses("any(plan.title, plan.ok)"));
        Assert.Equal(
            ["tests each item of 'plan.items' with 'it.title', which is a text; the test of any yields a boolean for each item"],
            Misuses("any(plan.items, it.title)"));
        Assert.Equal(
            ["reads 'it.titel', and each item of 'plan.items' has no property 'titel'"],
            Misuses("any(plan.items, it.titel == 'x')"));

        // What is never refused when the definition is loaded: scalars of different kinds, null, a value that may be
        // null or anything, '??' and empty(...) of anything, and an item's own boolean.
        Assert.Empty(Misuses("plan.count != 'three' && plan.maybe == null && !plan.flag && !unknown.anything"));
        Assert.Empty(Misuses("!(plan.title ?? true) && !empty(plan.items) && empty(plan.meta) && any(plan.items, it.ok)"));
    }

    /// <summary>
    /// What an expression yields is typed by its operators: every comparison, test and join a boolean, a literal its own
    /// kind, a placeholder a text, and '??' any side but the last where that side is not null, and the last as it is.
    /// </summary>
    [Fact]
    public void WhatAnExpressionYields_IsTypedByItsOperators()
    {
        static IReadOnlyList<string>? Kinds(string expression)
            => FlowExpression.Parse(expression).TypeOf(path => Plan.GetValueOrDefault(path.Whole) ?? JsonSchemaSubset.Reading.Anything, _ => { }).Kinds;

        Assert.Equal(["boolean"], Kinds("plan.title == 'x' || any(plan.items, it.ok)"));
        Assert.Equal(["boolean"], Kinds("!empty(plan.title)"));
        Assert.Equal(["string"], Kinds("plan.maybe ?? 'none'"));
        Assert.Equal(["string", "boolean", "null"], Kinds("plan.maybe ?? plan.flag ?? null"));
        Assert.Equal(["string"], Kinds("{plan.count}"));
        Assert.Equal(["integer"], Kinds("7"));
        Assert.Equal(["number"], Kinds("0.5"));
        Assert.Equal(["null"], Kinds("null"));
        Assert.Equal(["object", "array", "string", "integer", "number", "boolean"], Kinds("unknown.anything ?? 'x'"));
    }

    /// <summary>
    /// An expression is written back from its tree as one would write it - spaced, and parenthesized only where the
    /// grammar needs it - so a refusal quotes a part of it readably, and what it writes reads back as the same tree.
    /// </summary>
    [Theory]
    [InlineData("a||b&&c", "a || b && c")]
    [InlineData("(a || b) && c", "(a || b) && c")]
    [InlineData("!(a == b)", "!(a == b)")]
    [InlineData("!a ?? b", "!a ?? b")]
    [InlineData("(!a) ?? b", "(!a) ?? b")]
    [InlineData("(a ?? b) == 'x'", "a ?? b == 'x'")]
    [InlineData("any(review.findings,it.severity!='low')", "any(review.findings, it.severity != 'low')")]
    [InlineData("empty( x ) && {plan.title} == \"it's\"", "empty(x) && {plan.title} == \"it's\"")]
    [InlineData("a.b[2].c != 0.5 && d == null && e != true", "a.b[2].c != 0.5 && d == null && e != true")]
    public void AnExpression_IsWrittenBackAsItReads(string text, string written)
    {
        Assert.Equal(written, FlowExpression.Parse(text).Written);
        Assert.Equal(written, FlowExpression.Parse(written).Written);
    }

    /// <summary>
    /// The scanner reads a path of a hundred thousand names in one pass, as one read: it never recurses once for each
    /// name, so no text can spend the stack of the process that reads it.
    /// </summary>
    [Fact]
    public void TheScanner_ReadsAPathOfAnyLength_InOnePass()
    {
        var path = "plan" + string.Concat(Enumerable.Repeat(".ok", 99_999));

        var read = Assert.Single(WorkflowExpressions.ReadsOf(path));

        Assert.Equal("plan", read.Root);
        Assert.Equal(100_000, read.Names.Count);
    }
}
