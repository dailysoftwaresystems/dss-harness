using System.Collections.Concurrent;

namespace DssHarness.Core.Execution;

/// <summary>
/// One engine session's own scope, from the moment the engine holds its run to the session's end (<see cref="Begin"/>):
/// what the session finds once and holds true for as long as it holds the run, each fact kept by its one owner
/// (<see cref="Of{T}"/>) - where the run's main checkout and each agent's tree are (<see cref="Repository.SessionTrees"/>),
/// the machine's gh login (<see cref="Ci.GhLogins"/>) - and handed to every layer the session's work reaches, its step
/// runners and the agent and worktree services they ask, rather than found again at each.
/// </summary>
/// <remarks>
/// <para>
/// The scope flows with the work the session starts, as .NET's execution context does - every step runner, every service a
/// runner asks, every task started from them - and with nothing else: a command another session or another caller of the
/// same registrations runs beside it never sees it, so a fact one session holds is never another's answer.
/// </para>
/// <para>
/// It ends with the session (<see cref="Dispose"/>): work the session started that outlives it - a runner let go of past
/// its bound - asks again from then on, for nothing holds the run any more and a fact may have moved.
/// </para>
/// </remarks>
public sealed class EngineSession : IDisposable
{
    private static readonly AsyncLocal<EngineSession?> Held = new();

    private readonly ConcurrentDictionary<Type, object> _facts = new();
    private int _ended;

    private EngineSession()
    {
    }

    /// <summary>
    /// Begins a session's scope for the work that follows it in the calling flow, and for every task that work starts;
    /// one begun inside another stands in its place for that work, and the other's is the caller's again once the flow
    /// that began it returns.
    /// </summary>
    public static EngineSession Begin()
    {
        var session = new EngineSession();
        Held.Value = session;

        return session;
    }

    /// <summary>
    /// The owner of <typeparamref name="T"/> the session the calling work belongs to holds, made by <paramref name="make"/>
    /// at its first asking and the same for every asking after it; null where the work belongs to no session, or to one
    /// that has ended - where every fact is found anew.
    /// </summary>
    /// <param name="make">Makes the owner, once per session.</param>
    /// <typeparam name="T">The owner's type, one per session.</typeparam>
    public static T? Of<T>(Func<T> make)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(make);

        return Held.Value is { } session && Volatile.Read(ref session._ended) == 0
            ? (T)session._facts.GetOrAdd(typeof(T), _ => make())
            : null;
    }

    /// <summary>Ends the scope: every fact it held is found anew by whatever asks from now on.</summary>
    public void Dispose()
    {
        Volatile.Write(ref _ended, 1);
        _facts.Clear();
    }
}
