using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The sessions and groups one operation's sweeps ended (<see cref="ChildSessions.SweepAsync"/>), and the one writer of what its
/// documents say of them: <c>ended</c>, written into every document the operation answers with - its own, and the
/// stopped form of it where it stopped or threw after a sweep - by its session, so no command over an orchestrator, one
/// added later included, answers without it.
/// </summary>
/// <remarks>
/// One per operation, as every service of an operation is. Where a sweep ended nothing, no document has <c>ended</c>, and
/// each is exactly what it always was.
/// </remarks>
public sealed class EndedChildSessions : IDocumentFacts
{
    /// <summary>The member each document names the sessions and groups with.</summary>
    public const string Member = "ended";

    private readonly Lock _gate = new();

    private readonly List<EndedChildSession> _ended = [];

    /// <summary>Each session or group ended so far, in the order it was ended.</summary>
    public IReadOnlyList<EndedChildSession> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _ended];
            }
        }
    }

    /// <inheritdoc/>
    public bool HasAny
    {
        get
        {
            lock (_gate)
            {
                return _ended.Count > 0;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One object per session or process group: its <c>orchestrator</c>, its <c>agent</c> (null for a step no agent owns),
    /// its <c>step</c>, its <c>session</c> (the leader's id, a group's as a session's), whether it was a <c>group</c> - of
    /// what a machine step's legs start - its <c>program</c>, the <c>owner</c> that had gone, and what of it was
    /// <c>unreached</c> (null where everything in it was).
    /// </remarks>
    public void WriteInto(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var list = new JsonArray();

        foreach (var session in All)
        {
            list.Add(new JsonObject
            {
                ["orchestrator"] = session.Orchestrator,
                ["agent"] = session.Agent,
                ["step"] = session.Step,
                ["session"] = session.Session,
                ["group"] = session.Group,
                ["program"] = session.Program,
                ["owner"] = session.Owner,
                ["unreached"] = session.Unreached,
            });
        }

        document[Member] = list;
    }

    /// <summary>What <paramref name="ended"/> holds, counted by kind - the sessions, then the process groups - as one line names them.</summary>
    /// <param name="ended">What a sweep ended.</param>
    public static string Counted(IReadOnlyList<EndedChildSession> ended)
    {
        ArgumentNullException.ThrowIfNull(ended);

        return ReportText.Listed([
            .. new[] { false, true }
                .Select(group => (Group: group, Count: ended.Count(session => session.Group == group)))
                .Where(kind => kind.Count > 0)
                .Select(kind => ReportText.Counted(kind.Count, LeftSessions.Noun(kind.Group))),
        ]);
    }

    /// <summary>Keeps a session or a group a sweep of this operation ended.</summary>
    /// <param name="session">The session or the group.</param>
    internal void Add(EndedChildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            _ended.Add(session);
        }
    }
}
