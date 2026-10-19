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
    /// it names - the main tree, or the agent's the step's <c>in</c> or <c>of</c> names. A <c>run</c> step whose runner
    /// writes the tree holds <em>write</em> on it and not run, and a string that names no tree holds none.
    /// </summary>
    /// <param name="step">The step as it was declared.</param>
    /// <param name="runners">What the engine asks of the runners a workflow names.</param>
    public static IReadOnlyList<FlowClaim> Of(WorkflowStep step, IFlowRunnerFacts runners)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(runners);

        var writesTree = step.Type == "run" && step.Runner is { } runner && runners.WritesTree(runner);
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
                    claims.Add(new(TreeOf(step), FlowClaimKind.Run));
                    break;
                case "write(tree) where its runner says writesTree" when writesTree:
                    claims.Add(new(TreeOf(step), FlowClaimKind.Write));
                    break;
                case "write the agent's tree":
                case "write its target":
                    if (TreeOf(step) is { } target)
                    {
                        claims.Add(new(target, FlowClaimKind.Write));
                    }

                    break;
                case "write the agent's tree where its access is write" when step.Access == "write":
                    if (TreeOf(step) is { } own)
                    {
                        claims.Add(new(own, FlowClaimKind.Write));
                    }

                    break;
                case "the remote resource":
                    // A push and a pull request hold the forge's remote between them: one at a time.
                    claims.Add(new("the remote", FlowClaimKind.Write));
                    break;
                default:
                    // A claim that names no tree - a remote resource, a step the engine reaches another way - holds
                    // nothing here; the strings that name trees are the cases above.
                    break;
            }
        }

        return claims;
    }

    /// <summary>The tree the step works in: the agent its <c>in</c> or <c>of</c> names, or the main tree where it names none.</summary>
    private static string TreeOf(WorkflowStep step)
        => step.In ?? step.Of ?? Main;

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
    /// <summary>Whether the runner <paramref name="runner"/> writes the tree it works in (<c>writesTree</c>).</summary>
    /// <param name="runner">A runner's name, as a <c>run</c> step names it.</param>
    bool WritesTree(string runner);
}
