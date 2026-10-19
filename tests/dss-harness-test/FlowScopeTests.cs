using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// Where the names of a workflow's steps live: a name is looked up among siblings and then the scopes around it,
/// <c>dependsOn</c> and <c>on.step</c> name a sibling and nothing else, a read is made from the reader's own scope and
/// goes down into a group's members unless a <c>forEach</c> holds them, a group's loop reads its own members, the
/// recovery's steps see each other and then the flow's top level, no inner step shadows a name it can see, a step's
/// name is spelt one way, and the reserved names are one list a step may take none of.
/// </summary>
public sealed class FlowScopeTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests are of the rules over one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>
    /// A bare name is looked up among siblings, then the scopes around it, the nearest scope's holding; a group's member
    /// is invisible outside the group by bare name.
    /// </summary>
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
                  - name: beside
                    type: orient
            """);

        Assert.Equal("top", FlowScope.Resolve("top", definition, "gate")?.Path);
        Assert.Equal("gate.beside", FlowScope.Resolve("beside", definition, "gate")?.Path);
        Assert.Null(FlowScope.Resolve("beside", definition, scope: null));
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

        Assert.Null(FlowScope.SiblingOf("top", definition, "gate"));
        Assert.Equal("top", FlowScope.SiblingOf("top", definition, scope: null)?.Path);
        Assert.Equal("gate.inner", FlowScope.SiblingOf("inner", definition, "gate")?.Path);
    }

    /// <summary>
    /// A read goes down into a group's members while its names name one - a block's member is the member, the names
    /// after it the member's output - but a <c>forEach</c>'s member read from outside it is nothing, at any depth of
    /// the path: it would be one of many, and nothing says which. From inside the <c>forEach</c>, it is the reader's
    /// own item's.
    /// </summary>
    [Fact]
    public void AReadGoesDownIntoAGroupsMembers_ButNotIntoAForEachFromOutsideIt()
    {
        var block = Parse("""
            name: flow
            steps:
              - name: gate
                steps:
                  - name: inner
                    type: orient
            """);

        var member = FlowScope.Read(["gate", "inner", "ok"], block, scope: null, out var blockWhy);

        Assert.NotNull(member);
        Assert.Equal("gate.inner", member.Value.Step.Path);
        Assert.Equal(["ok"], member.Value.After);
        Assert.Null(blockWhy);

        var looped = Parse("""
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

        Assert.Null(FlowScope.Read(["loop", "block", "inner"], looped, scope: null, out var crossingWhy));
        Assert.Equal("'loop' runs a forEach: read from outside it, a member is one of many, and nothing says which", crossingWhy);

        // The forEach's own outcome is no member of it, and is read from outside as any group's is.
        Assert.Equal("loop", FlowScope.Read(["loop", "outcome"], looped, scope: null, out _)?.Step.Path);

        var inside = FlowScope.Read(["loop", "block", "inner"], looped, "loop.block", out var insideWhy);

        Assert.Equal("loop.block.inner", inside?.Step.Path);
        Assert.Null(insideWhy);
    }

    /// <summary>
    /// A read is made from the reader's own scope, not from the flow's top: inside a group, a sibling block's member is
    /// read by the block's bare name and the member's.
    /// </summary>
    [Fact]
    public void AReadIsMadeFromTheReadersScope()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: outer
                steps:
                  - name: review
                    steps:
                      - name: correctness
                        type: orient
                  - name: after
                    type: orient
            """);

        var read = FlowScope.Read(["review", "correctness", "ok"], definition, "outer", out var why);

        Assert.Equal("outer.review.correctness", read?.Step.Path);
        Assert.Equal(["ok"], read?.After);
        Assert.Null(why);
        Assert.Null(FlowScope.Read(["review", "correctness", "ok"], definition, scope: null, out _));
    }

    /// <summary>
    /// A group's loop reads what its iterations produced from within the group: its own members by bare name - as the
    /// shipped flow's <c>rounds</c> reads <c>self-audit</c> in its until - and nothing nested deeper inside them.
    /// </summary>
    [Fact]
    public void AGroupsLoopReadsItsOwnMembers_FromWithinIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: rounds
                repeat:
                  until: self-audit.clean
                  maxIterations: 3
                steps:
                  - name: review
                    steps:
                      - name: correctness
                        type: orient
                  - name: self-audit
                    type: orient
            """);

        Assert.Equal("rounds.self-audit", FlowScope.Resolve("self-audit", definition, "rounds")?.Path);
        Assert.Null(FlowScope.Resolve("self-audit", definition, scope: null));
        Assert.Null(FlowScope.Resolve("correctness", definition, "rounds"));
    }

    /// <summary>
    /// Where inside a group a step of a name is - one of its members, or one nested deeper, wherever a bare name reaches
    /// it or not - every one of them with its path, in declaration order; nothing outside the group, and nothing in a
    /// group whose path merely begins the same.
    /// </summary>
    [Fact]
    public void WhereInsideAGroupAStepOfANameIs_EveryOneWithItsPath()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: correctness
                type: orient
              - name: rounds
                repeat:
                  until: a.correctness.ok
                  maxIterations: 3
                steps:
                  - name: a
                    steps:
                      - name: correctness
                        type: orient
                  - name: b
                    steps:
                      - name: deep
                        steps:
                          - name: correctness
                            type: orient
              - name: rounds-two
                steps:
                  - name: correctness
                    type: orient
            """);

        Assert.Equal(
            ["rounds.a.correctness", "rounds.b.deep.correctness"],
            FlowScope.Inside("correctness", definition, "rounds").Select(found => found.Path));
        Assert.Equal(["rounds.b"], FlowScope.Inside("b", definition, "rounds").Select(found => found.Path));
        Assert.Empty(FlowScope.Inside("ghost", definition, "rounds"));
    }

    /// <summary>
    /// The recovery's own steps are a scope of their own: they see each other, then the flow's top level by bare name
    /// and its members by their paths; nothing of the flow sees them by bare name.
    /// </summary>
    [Fact]
    public void TheRecoverysStepsSeeEachOther_ThenTheFlowsTopLevel()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
                onFailure: recover
              - name: gate
                steps:
                  - name: inner
                    type: orient
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: diagnose
                  type: orient
                - name: remedies
                  steps:
                    - name: fix
                      type: orient
            """);

        Assert.Equal("recover.diagnose", FlowScope.SiblingOf("diagnose", definition, FlowScope.Recovery)?.Path);
        Assert.Equal("recover.diagnose", FlowScope.Resolve("diagnose", definition, "recover.remedies")?.Path);
        Assert.Equal("plan", FlowScope.Resolve("plan", definition, "recover.remedies")?.Path);
        Assert.Null(FlowScope.SiblingOf("plan", definition, FlowScope.Recovery));
        Assert.Equal("gate.inner", FlowScope.Read(["gate", "inner"], definition, FlowScope.Recovery, out _)?.Step.Path);
        Assert.Null(FlowScope.Resolve("diagnose", definition, scope: null));
    }

    /// <summary>Where siblings share a name - a duplicate the validator refuses - the first declared is the one a name finds.</summary>
    [Fact]
    public void WhereSiblingsShareAName_TheFirstDeclaredIsFound()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: twin
                type: orient
              - name: twin
                type: test
            """);

        Assert.Same(definition.Steps[0], FlowScope.Resolve("twin", definition, scope: null)?.Step);
        Assert.Same(definition.Steps[0], FlowScope.SiblingOf("twin", definition, scope: null)?.Step);
    }

    /// <summary>
    /// No shadowing: an inner step may not reuse, by bare name, a name it can see from where it is - a step beside a
    /// group around it, or that group, declared before or after - and each is said with the step it hides. Siblings of
    /// one scope are its own and shadow nothing.
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
                  - name: gate
                    type: orient
              - name: audit
                type: orient
            """);

        Assert.Equal([("gate.plan", "plan"), ("gate.audit", "audit"), ("gate.gate", "gate")], FlowScope.Shadowings(definition));
    }

    /// <summary>The recovery's steps are of the flow's top level: a name it can see, they may not reuse, and each is named by its own path.</summary>
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
                - name: remedies
                  steps:
                    - name: plan
                      type: orient
            """);

        Assert.Equal([("recover.plan", "plan"), ("recover.remedies.plan", "recover.plan")], FlowScope.Shadowings(definition));
    }

    /// <summary>The scope a step sits in is its group's path, the recovery's for one of its own steps, and none at the top level.</summary>
    [Theory]
    [InlineData("gate.inner", "gate")]
    [InlineData("recover.fix", "recover")]
    [InlineData("top", null)]
    public void TheScopeAStepSitsIn_IsItsGroupsPath(string path, string? scope)
        => Assert.Equal(scope, FlowScope.ScopeOf(path));

    /// <summary>
    /// A step's name is a lower-case letter, then lower-case letters, digits and dashes, and nothing after them - not
    /// even a line's end, which a pattern anchored with <c>$</c> lets through.
    /// </summary>
    [Theory]
    [InlineData("build", true)]
    [InlineData("build-2", true)]
    [InlineData("build\n", false)]
    [InlineData("Build", false)]
    [InlineData("2build", false)]
    [InlineData("a.b", false)]
    [InlineData("", false)]
    public void AStepsName_IsSpeltOneWay(string name, bool wellFormed)
        => Assert.Equal(wellFormed, FlowScope.IsWellFormedName(name));

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
