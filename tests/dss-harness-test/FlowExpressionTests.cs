using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// One expression as a tree: every form the grammar spells, joined and compared as it says, <c>any</c> binding
/// <c>it</c> to the member it tests, and the tree's reads the reads the scanner finds.
/// </summary>
public sealed class FlowExpressionTests
{
    /// <summary>Every form of the grammar parses to the node that says it.</summary>
    [Fact]
    public void EveryFormOfTheGrammar_ParsesToItsNode()
    {
        Assert.IsType<FlowExpression.Path>(FlowExpression.Parse("plan.ok"));
        Assert.IsType<FlowExpression.Literal>(FlowExpression.Parse("true"));
        Assert.IsType<FlowExpression.Literal>(FlowExpression.Parse("42"));
        Assert.IsType<FlowExpression.Literal>(FlowExpression.Parse("'approved'"));
        Assert.IsType<FlowExpression.Literal>(FlowExpression.Parse("\"approved\""));
        Assert.IsType<FlowExpression.Literal>(FlowExpression.Parse("null"));
        Assert.IsType<FlowExpression.Not>(FlowExpression.Parse("!plan.ok"));
        Assert.IsType<FlowExpression.Empty>(FlowExpression.Parse("empty(plan.findings)"));
        Assert.IsType<FlowExpression.Placeholder>(FlowExpression.Parse("{plan.title}"));
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
        ];

        foreach (var expression in expressions)
        {
            Assert.Equal(
                [.. WorkflowExpressions.ReadsOf(expression).Select(read => (read.Root, read.Whole, read.Template, read.InsideAny))],
                [.. FlowExpression.Parse(expression).Reads().Select(read => (read.Root, read.Whole, read.Template, read.InsideAny))]);
        }
    }

    /// <summary>
    /// A path with an index reads as one name in the tree where the scanner reads its segments apart - each checked
    /// on its own - so both spell the same names.
    /// </summary>
    [Fact]
    public void AnIndexedPath_ReadsAsOneNameInTheTree()
    {
        var path = (FlowExpression.Path)FlowExpression.Parse("findings[0].severity");

        Assert.Equal(["findings[0].severity"], path.Reads().Select(read => read.Whole));
        Assert.Equal(["findings", "severity"], WorkflowExpressions.ReadsOf("findings[0].severity").Select(read => read.Whole));
    }

    /// <summary>What the grammar does not say is refused, naming the column it stopped at.</summary>
    [Theory]
    [InlineData("plan +", "column 6")]
    [InlineData("(plan.ok", "ends before its ')'")]
    [InlineData("'unclosed", "never closed")]
    [InlineData("plan.ok)", "column 8")]
    [InlineData("@broken", "column 1")]
    [InlineData("previous", "say which one")]
    public void WhatTheGrammarDoesNotSay_IsRefusedNamingTheColumn(string text, string said)
    {
        var problem = Assert.Throws<ArgumentException>(() => FlowExpression.Parse(text));

        Assert.Contains(said, problem.Message, StringComparison.Ordinal);
    }

    /// <summary>A group nests at most 64 deep: a guard, so no file can spend the stack however it is written.</summary>
    [Fact]
    public void AGroupNestsAtMostSixtyFourDeep()
    {
        var deep = new string('(', 65) + "plan.ok" + new string(')', 65);

        Assert.Contains("the expression nests more than 64 groups deep", Assert.Throws<ArgumentException>(() => FlowExpression.Parse(deep)).Message, StringComparison.Ordinal);
    }
}
