using DssHarness.Core.Repository;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run one operation's command reached, once it did: told by the commands of a run (<see cref="FlowService"/>) the
/// moment the run's record is there for them - a resume's or an answer's run found, a start's run made - and read by what
/// the operation leaves at its end (<see cref="FlowOutputLogs"/>), however the command then ended, a defect it threw
/// included, where nothing else would say which run a start without <c>--run</c> named.
/// </summary>
/// <remarks>One per operation, as every service of an operation is; what it holds is that operation's alone.</remarks>
public sealed class FlowRunReached
{
    private readonly Lock _gate = new();
    private (HarnessLayout Repository, OrchestratorLayout Run)? _reached;

    /// <summary>The repository and the run the command reached; null until it reached one.</summary>
    public (HarnessLayout Repository, OrchestratorLayout Run)? Reached
    {
        get
        {
            lock (_gate)
            {
                return _reached;
            }
        }
    }

    /// <summary>Tells that the command reached <paramref name="run"/> of <paramref name="repository"/>; a later one is the one it works.</summary>
    /// <param name="repository">The main checkout's layout, the run's repository.</param>
    /// <param name="run">The run's layout.</param>
    public void Reach(HarnessLayout repository, OrchestratorLayout run)
    {
        ArgumentNullException.ThrowIfNull(repository);
        ArgumentNullException.ThrowIfNull(run);

        lock (_gate)
        {
            _reached = (repository, run);
        }
    }
}
