using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The graph of a workflow's declaration: every step a node, the recovery's among them, and an edge from each step to
/// what it waits for - its dependencies, a group to each child, a child to what its groups wait for, a handler to its
/// source - walked in declaration order, the first cycle that closes reported as its path.
/// </summary>
public sealed class FlowGraphTests
{
    /// <summary>Parses a definition, refusing a file that does not read: these tests are of the walk over one that does.</summary>
    private static WorkflowDefinition Parse(string text)
        => WorkflowFileParser.Parse("workflows/flow/flow.yml", text);

    /// <summary>A flow whose edges run one way has no cycle, however deep its groups.</summary>
    [Fact]
    public void AFlowThatRunsOneWay_HasNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: build
                type: orient
                dependsOn: [plan]
              - name: review
                steps:
                  - name: audit
                    type: orient
                  - name: verify
                    type: orient
                    dependsOn: [audit]
              - name: fold
                type: orient
                dependsOn: [review]
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>A cycle through dependencies is reported as the path that closes it, in walk order, first-found first.</summary>
    [Fact]
    public void ADependencyCycle_IsReportedAsItsPath()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: a
                type: orient
                dependsOn: [c]
              - name: b
                type: orient
                dependsOn: [a]
              - name: c
                type: orient
                dependsOn: [b]
            """);

        Assert.Equal(["a", "c", "b", "a"], FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A handler waits for its source's ending, as a dependent waits for what it depends on, so its edge runs from the
    /// handler to the source: a source that depends on its own handler - which a step may, to wait for it - waits for
    /// an ending that waits for it, and the deadlock is found.
    /// </summary>
    [Fact]
    public void AHandlerWaitsForItsSource_ASourceThatDependsOnItsHandlerIsACycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: fix
                type: orient
                on:
                  step: build
                  outcome: failure
              - name: build
                type: orient
                dependsOn: [fix]
            """);

        Assert.Equal(["fix", "build", "fix"], FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A handler that also depends on its source waits for it twice over - the validator refuses the dependency, not
    /// a cycle - so both edges run the one way and close none.
    /// </summary>
    [Fact]
    public void AHandlerThatAlsoDependsOnItsSource_IsNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: check
                type: orient
              - name: hold
                type: orient
                dependsOn: [check]
                on:
                  step: check
                  outcome: failure
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A child inherits its group's dependencies as they were found where the group declared them: a member that shares
    /// the dependency's name - a shadowing the validator refuses - is not what the child waits for, so no cycle is drawn
    /// through it.
    /// </summary>
    [Fact]
    public void AnInheritedDependency_IsFoundWhereItsGroupDeclaredIt()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: x
                type: orient
              - name: gate
                dependsOn: [x]
                steps:
                  - name: c
                    type: orient
                  - name: x
                    type: orient
                    dependsOn: [c]
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A dependency and a handler's source name a sibling and nothing else: a name of another scope draws no edge,
    /// as <see cref="FlowScope.SiblingOf"/> finds none - the validator owns that refusal.
    /// </summary>
    [Fact]
    public void ANameOfAnotherScope_IsNoEdge_AndNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: first
                type: orient
                dependsOn: [gate.inner]
              - name: s
                type: orient
                dependsOn: [gate]
              - name: gate
                steps:
                  - name: inner
                    type: orient
                  - name: h
                    type: orient
                    on:
                      step: s
                      outcome: failure
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// The recovery's steps are walked as the flow's are, in a scope of their own: a cycle among them is found, each
    /// node named under <c>recover</c>.
    /// </summary>
    [Fact]
    public void TheRecoverysSteps_AreWalked()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
                onFailure: recover
            recover:
              onSuccess: failed-step
              onFailure: pause
              steps:
                - name: fix
                  type: orient
                  on:
                    step: build
                    outcome: failure
                - name: build
                  type: orient
                  dependsOn: [fix]
            """);

        Assert.Equal(["recover.fix", "recover.build", "recover.fix"], FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A step that names itself - in its dependsOn or its on - draws no edge: the validator says that defect once, in
    /// its own words, and the walk does not say it again as a cycle; a group's self-dependency is no edge of its
    /// children either.
    /// </summary>
    [Fact]
    public void AStepThatNamesItself_IsNoEdge()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: release
                type: orient
                dependsOn: [release]
              - name: self
                type: orient
                on:
                  step: self
                  outcome: failure
              - name: gate
                dependsOn: [gate]
                steps:
                  - name: inner
                    type: orient
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// A <c>then</c> is the one back edge the engine allows and is no edge of this walk: a handler that returns to
    /// an earlier step through <c>then</c> alone is no cycle, whatever its <c>maxVisits</c> bounds.
    /// </summary>
    [Fact]
    public void AThenIsNoEdge_AHandlerThatReturnsThroughItAlone_IsNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: plan
                type: orient
              - name: mend
                type: orient
                on:
                  step: plan
                  outcome: failure
                then: plan
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>A dependency that names nothing the flow declares is no edge here: the validator owns that refusal.</summary>
    [Fact]
    public void ADependencyThatNamesNothing_IsNoEdge_AndNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: alone
                type: orient
                dependsOn: [nobody]
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }

    /// <summary>
    /// Where siblings share a name - a duplicate the validator refuses, not the reader - the walk still answers:
    /// the steps share one node, and no exception is raised into the validator's path.
    /// </summary>
    [Fact]
    public void WhereSiblingsShareAName_TheWalkStillAnswers()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: twin
                type: orient
                dependsOn: [other]
              - name: other
                type: orient
              - name: twin
                type: orient
                dependsOn: [other]
            """);

        Assert.Null(FlowGraph.FindCycle(definition));
    }
}
