using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The graph of a workflow's declaration: every step a node, each dependency an edge, a group's hold on each child,
/// the dependencies a child inherits, and an <c>on</c> from a source to its handler - walked in declaration order,
/// the first cycle that closes reported as its path.
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

        var cycle = FlowGraph.FindCycle(definition);

        Assert.NotNull(cycle);
        Assert.Equal(["a", "c", "b", "a"], cycle);
    }

    /// <summary>
    /// An <c>on</c> is an edge from the source to its handler: a handler that waits for its own source closes a
    /// cycle of two, though each dependency names an earlier sibling.
    /// </summary>
    [Fact]
    public void AnOnEdgeRunsFromTheSourceToItsHandler_ACycleThroughItIsFound()
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

        var cycle = FlowGraph.FindCycle(definition);

        Assert.NotNull(cycle);
        Assert.Equal(["check", "hold", "check"], cycle);
    }

    /// <summary>
    /// A child inherits its group's dependencies, and an <c>on</c> runs from the source to its handler: a group
    /// that waits for a step, holding a handler of that step's ending, closes a cycle through them - a flow the
    /// rules of dependencies alone would never make, and the walk refuses for the engine.
    /// </summary>
    [Fact]
    public void AChildInheritsItsGroupsDependencies_AnOnEdgeThroughThemMakesACycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: s
                type: orient
              - name: gate
                dependsOn: [s]
                steps:
                  - name: h
                    type: orient
                    on:
                      step: s
                      outcome: failure
            """);

        var cycle = FlowGraph.FindCycle(definition);

        Assert.NotNull(cycle);
        Assert.Equal(["s", "gate.h", "s"], cycle);
    }

    /// <summary>
    /// A dependency that names a step of another scope - a member of a group from outside it - is not resolved and
    /// draws no edge: the validator owns that refusal, and the walk answers over the file as it was read.
    /// </summary>
    [Fact]
    public void ADependencyNamingAnotherScopesMember_IsNoEdge_AndNoCycle()
    {
        var definition = Parse("""
            name: flow
            steps:
              - name: first
                type: orient
                dependsOn: [gate.inner]
              - name: gate
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
    /// the last declaration is the one the edges draw to, and no exception is raised into the validator's path.
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
