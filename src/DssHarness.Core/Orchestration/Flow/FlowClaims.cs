using DssHarness.Core.Execution;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>How much of a tree a step holds while it runs, as the engine names it.</summary>
/// <remarks>
/// The lattice is the run lock's own: write takes the tree whole and excludes everything on it; run and read are shared,
/// run under the one fixed variant <c>run</c> (leg steps on one tree are serialized) and read under a variant of its own
/// (readers coexist), so a reader and a runner share a tree and a fold waits behind both. The strings the step types'
/// table holds are this class's source: it reads them and never repeats what they say.
/// </remarks>
public enum FlowClaimKind
{
    /// <summary>The tree, taken whole: nothing else runs, reads or writes on it while this stands.</summary>
    Write,

    /// <summary>The tree shared with readers and with the other runners under the same variant: a build, a test, a run or a sweep.</summary>
    Run,

    /// <summary>The tree shared with runners and with the other readers: a reviewer or an orient, which change nothing.</summary>
    Read,
}

/// <summary>One tree a step holds while it runs, and how much of it.</summary>
/// <param name="Tree">Which tree: the main tree, or the agent's a step's <c>in</c> or <c>of</c> names.</param>
/// <param name="Kind">How much of it.</param>
public sealed record FlowClaim(string Tree, FlowClaimKind Kind)
{
    /// <summary>
    /// Whether this claim and <paramref name="other"/> cannot stand at once: the run lock's lattice. Write takes the
    /// tree whole and excludes everything on it; run and read are shared variants - run under the one fixed variant
    /// <c>run</c>, so leg steps on one tree are serialized, and read under a variant of its own, so readers coexist -
    /// and two shares conflict only where they are the same variant, so readers and runners share a tree, and a fold
    /// waits behind both.
    /// </summary>
    /// <param name="other">The other claim.</param>
    public bool Conflicts(FlowClaim other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(Tree, other.Tree, StringComparison.Ordinal))
        {
            return false;
        }

        return Kind == FlowClaimKind.Write
            || other.Kind == FlowClaimKind.Write
            || (Kind == FlowClaimKind.Run && other.Kind == FlowClaimKind.Run);
    }
}

/// <summary>
/// The claims a step holds on trees while it runs, read from the strings the step types' table carries
/// (<see cref="WorkflowStepTypes.StepType.ClaimsOf"/>) - the one place that says what a type holds - and from whether
/// a <c>run</c> step's runner writes the tree, which the runner's own facts say and the definition's check holds to
/// its place.
/// </summary>
public static class FlowClaims
{
    /// <summary>The main tree, as a claim names it.</summary>
    public const string Main = "main";

    /// <summary>
    /// The claims <paramref name="step"/> holds while it runs: each string its type's table carries, read for the tree
    /// it names - the main tree, or the tree of the agent the step's <c>in</c> or <c>of</c> names - and the forge's
    /// remote for a push and a pull request. A <c>run</c> step whose runner writes the tree holds <em>write</em> on it
    /// and not run; a string the cases do not name holds nothing.
    /// </summary>
    /// <param name="step">The step as it was declared.</param>
    /// <param name="runners">What the engine asks of the runners a workflow names.</param>
    /// <param name="agent">
    /// The agent the step's <c>in</c> or <c>of</c> names, as the run resolved it: the one that step's instance of the
    /// create-agent made, so each item of a <c>forEach</c> claims its own agent's tree and never a sibling's. Null where
    /// the step names none, or the run has made none for it yet - the name the step wrote is then the tree, which is
    /// one tree for every instance that names it.
    /// </param>
    public static IReadOnlyList<FlowClaim> Of(WorkflowStep step, IFlowRunnerFacts runners, string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(runners);

        var writesTree = step.Type == "run" && step.Runner is { } runner && runners.WritesTree(runner);
        var tree = step.In is null && step.Of is null ? Main : agent ?? step.In ?? step.Of!;
        var claims = new List<FlowClaim>();

        foreach (var held in TypeOf(step)?.ClaimsOf ?? [])
        {
            switch (held)
            {
                case "read main":
                    claims.Add(new(Main, FlowClaimKind.Read));
                    break;
                case "write main":
                    claims.Add(new(Main, FlowClaimKind.Write));
                    break;
                case "run(tree)" when !writesTree:
                    claims.Add(new(tree, FlowClaimKind.Run));
                    break;
                case "write(tree) where its runner says writesTree" when writesTree:
                    claims.Add(new(tree, FlowClaimKind.Write));
                    break;
                case "write the agent's tree":
                case "write its target":
                    claims.Add(new(tree, FlowClaimKind.Write));
                    break;
                case "write the agent's tree where its access is write" when step.Access == "write":
                    claims.Add(new(tree, FlowClaimKind.Write));
                    break;
                case "the remote resource":
                    // A push and a pull request hold the forge's remote between them, one at a time: a claim of its
                    // own, named apart from every tree.
                    claims.Add(new("the remote", FlowClaimKind.Write));
                    break;
                default:
                    // A string the cases above do not name - a conditional one whose condition does not hold - holds
                    // nothing.
                    break;
            }
        }

        return claims;
    }

    /// <summary>The step's type, where it names one.</summary>
    private static WorkflowStepTypes.StepType? TypeOf(WorkflowStep step)
        => step.Type is null ? null : WorkflowStepTypes.Find(step.Type);
}

/// <summary>
/// What the engine asks of the runners a workflow names, before any step runs: whether a <c>run</c> step's runner
/// writes the tree, which changes the claim the step holds on it. The definition's own check reads the same fact of the
/// configuration; the engine reads it here, at admission, from the one seam a front end can back with the
/// configuration's own runners.
/// </summary>
public interface IFlowRunnerFacts
{
    /// <summary>
    /// Whether a run of the runner <paramref name="runner"/> writes the tree it works in (<c>writesTree</c>): the runner
    /// says so, or a runner its run checks name does.
    /// </summary>
    /// <param name="runner">A runner's name, as a <c>run</c> step names it.</param>
    bool WritesTree(string runner);
}

/// <summary>
/// The runner facts of one run, read from the configuration it runs under: a run of a runner writes the tree where
/// <see cref="Configuration.HarnessConfig.WritingRunners"/> names any - the one answer the <c>run</c> step's runner works
/// in place by, and the definition's check refuses on the main tree.
/// </summary>
/// <param name="config">The configuration the run runs under.</param>
public sealed class ConfiguredRunnerFacts(Configuration.HarnessConfig config) : IFlowRunnerFacts
{
    /// <inheritdoc/>
    public bool WritesTree(string runner) => config.WritingRunners(runner).Count > 0;
}
