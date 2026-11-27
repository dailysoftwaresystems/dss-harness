using DssHarness.Core.Execution;
using DssHarness.Core.Repository;
using DssHarness.Core.Runners;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The run one operation's command reached, once it did: told by the commands of a run (<see cref="FlowService"/>) the
/// moment the run's record is there for them - a resume's or an answer's run found, a start's run made - and read by what
/// the operation leaves at its end (<see cref="FlowOutputLogs"/>), however the command then ended, a defect it threw
/// included, where nothing else would say which run a start without <c>--run</c> named; and the masks each session of the
/// run the command drove kept its records through (<see cref="SessionMasks"/>), which the output logs are masked by.
/// </summary>
/// <remarks>One per operation, as every service of an operation is; what it holds is that operation's alone.</remarks>
public sealed class FlowRunReached
{
    private readonly Lock _gate = new();
    private readonly List<SessionMasks> _sessions = [];
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

    /// <summary>Tells that a session the command drove keeps its records through <paramref name="masks"/>, from its beginning.</summary>
    /// <param name="masks">The session's masks.</param>
    public void Masked(SessionMasks masks)
    {
        ArgumentNullException.ThrowIfNull(masks);

        lock (_gate)
        {
            _sessions.Add(masks);
        }
    }

    /// <summary>
    /// What the command's sessions masked their records of: every secret each held, as one mask - null where they held none
    /// - and, where no session could build the mask of the secrets its configuration appoints, why; null where the command
    /// began no session, so no record of it was masked of anything.
    /// </summary>
    public (ActionValues? Mask, string? Unbuilt)? Masks()
    {
        List<SessionMasks> sessions;

        lock (_gate)
        {
            sessions = [.. _sessions];
        }

        if (sessions is [])
        {
            return null;
        }

        var current = sessions.Select(session => session.Current()).ToList();
        var masks = current.Select(each => each.Mask).OfType<ActionValues>().ToList();

        return (
            masks is [] ? null : masks.Skip(1).Aggregate(masks[0], (whole, mask) => whole.With(mask)),
            current.All(each => each.Unbuilt is not null) ? current[0].Unbuilt : null);
    }
}
