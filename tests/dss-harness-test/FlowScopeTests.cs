using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// Where the names of a workflow's steps live: a name is looked up among siblings and then the scopes around it,
/// <c>dependsOn</c> and <c>on.step</c> name a sibling and nothing else, a group's member is called from outside it
/// by its path unless a <c>forEach</c> holds it, no inner step shadows a name it can see, and the reserved names
/// are one list a step may take none of.
/// </summary>
public sealed class FlowScopeTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests are of the rules over one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A bare name is looked up among siblings, then the scopes around it; the nearest scope's holds.</summary>
    [Fact]
    public void ABareNameIsLookedUpAmongSiblings_ThenTheScopesAroundIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: top
                type: orient
              - name: gate
                steps:
                  - name: inner
                    type: orient
                    dependsOn: [beside]
                  - name: beside
                    type: orient
              - name: later
                type: orient
                dependsOn: [top]
            """);

        var fromInner = FlowScope.Resolve("top", definition, "gate.inner");

        Assert.NotNull(fromInner);
        Assert.Equal("top", fromInner.Name);

        var fromLater = FlowScope.FromOutside("gate.beside", definition, out var why);

        Assert.NotNull(fromLater);
        Assert.Equal("beside", fromLater.Name);
        Assert.Null(why);
    }

    /// <summary><c>dependsOn</c> and <c>on.step</c> name a sibling and are looked for nowhere else: a name of an outer scope is not found.</summary>
    [Fact]
    public void ASiblingOnlyNameIsNotLookedForAroundIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: top
                type: orient
              - name: gate
                steps:
                  - name: inner
                    type: orient
            """);

        Assert.Null(FlowScope.SiblingOf("top", definition, "gate.inner"));
        Assert.NotNull(FlowScope.SiblingOf("top", definition, "top"));
        Assert.NotNull(FlowScope.SiblingOf("inner", definition, "gate.inner"));
    }

    /// <summary>
    /// From outside a group, <c>group.member</c> is the member - a block's, and a <c>repeat</c>'s as its last
    /// iteration - but a <c>forEach</c>'s member is nothing: it would be one of many, and nothing says which.
    /// </summary>
    [Fact]
    public void FromOutside_AMembersPathIsTheMember_ButAForEachHoldsNothing()
    {
        var block = Parse("""
            name: flow
            steps:
              - name: gate
                steps:
                  - name: inner
                    type: orient
            """);

        var member = FlowScope.FromOutside("gate.inner", block, out var blockWhy);

        Assert.NotNull(member);
        Assert.Equal("inner", member.Name);
        Assert.Null(blockWhy);

        var looped = Parse("""
            name: flow
            steps:
              - name: gate
                forEach: input.things
                steps:
                  - name: inner
                    type: orient
            """);

        var none = FlowScope.FromOutside("gate.inner", looped, out var loopWhy);

        Assert.Null(none);
        Assert.Equal("gate holds a forEach", loopWhy);

        var nested = Parse("""
            name: flow
            steps:
              - name: loop
                forEach: input.things
                steps:
                  - name: block
                    steps:
                      - name: inner
                        type: orient
            """);

        var crossing = FlowScope.FromOutside("loop.block.inner", nested, out var crossingWhy);

        Assert.Null(crossing);
        Assert.Equal("loop holds a forEach", crossingWhy);
    }

    /// <summary>
    /// No shadowing: an inner step may not reuse, by bare name, a name it can see from where it is - a step beside a
    /// group around it, or that group, declared before or after. Siblings of one scope are its own and shadow nothing.
    /// </summary>
    [Fact]
    public void NoInnerStepShadowsANameItCanSee()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: gate
                steps:
                  - name: plan
                    type: orient
                  - name: audit
                    type: orient
              - name: audit
                type: orient
            """);

        var shadowings = FlowScope.Shadowings(definition);

        Assert.Equal([("gate.plan", "plan"), ("gate.audit", "audit")], shadowings);
    }

    /// <summary>The recovery's steps are of the flow's top level: a name it can see, they may not reuse.</summary>
    [Fact]
    public void TheRecoverysStepsReuseNoNameOfTheTopLevel()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: plan
                  type: orient
            """);

        Assert.Equal([("plan", "plan")], FlowScope.Shadowings(definition));
    }

    /// <summary>The reserved names are one list, read once: a step may be named none of them, and anything else it may.</summary>
    [Theory]
    [InlineData("run", true)]
    [InlineData("latest", true)]
    [InlineData("carried", true)]
    [InlineData("plan", false)]
    [InlineData("gate", false)]
    public void TheReservedNamesAreOneList_AStepMayBeNamedNoneOfThem(string name, bool reserved)
        => Assert.Equal(reserved, FlowScope.IsReserved(name));
}
