using System.Text.Json.Nodes;
using DssHarness.Core.Application;

namespace DssHarness.Core.Orchestration;

/// <summary>
/// The sessions one operation's sweeps ended (<see cref="ChildSessions.SweepAsync"/>), and the one writer of what its
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
    /// <summary>The member each document names the sessions with.</summary>
    public const string Member = "ended";

    private readonly Lock _gate = new();

    private readonly List<EndedChildSession> _ended = [];

    /// <summary>Each session ended so far, in the order it was ended.</summary>
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
    /// One object per session: its <c>orchestrator</c>, its <c>agent</c> (null for a step no agent owns), its <c>step</c>,
    /// its <c>session</c>, its <c>program</c>, the <c>owner</c> that had gone, and what of it was <c>unreached</c> (null
    /// where everything in it was).
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
                ["program"] = session.Program,
                ["owner"] = session.Owner,
                ["unreached"] = session.Unreached,
            });
        }

        document[Member] = list;
    }

    /// <summary>Keeps a session a sweep of this operation ended.</summary>
    /// <param name="session">The session.</param>
    internal void Add(EndedChildSession session)
    {
        ArgumentNullException.ThrowIfNull(session);

        lock (_gate)
        {
            _ended.Add(session);
        }
    }
}
