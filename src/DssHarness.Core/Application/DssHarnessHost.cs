using DssHarness.Core.Output;

namespace DssHarness.Core.Application;

/// <summary>
/// Begins the operations of this tool, one session each: one provider, one output, one id, one run - which is what
/// the command line has always given every invocation, and what a graphical front end gives every command it runs and
/// every step an orchestrator run starts.
/// </summary>
/// <remarks>
/// One per process, holding nothing of any operation: machine-wide state - the run lock, admission - is shared on
/// purpose and already excludes by host, tree and variant. The ends that operations begun as steps of a run leave to
/// their run are routed here, by the run's id, so a session need know nothing of the one it is a step of beyond its
/// id.
/// </remarks>
public sealed class DssHarnessHost
{
    private readonly Func<SessionOptions, IServiceProvider> _services;
    private readonly Lock _gate = new();
    private readonly Dictionary<OperationId, List<Func<Task>>> _deferred = [];

    /// <summary>Begins the host.</summary>
    /// <param name="services">
    /// Builds the services of an operation, as the command line's own root does today; a root of this tool's own
    /// replaces it from there.
    /// </param>
    public DssHarnessHost(Func<SessionOptions, IServiceProvider>? services = null)
    {
        _services = services ?? (_ => throw new InvalidOperationException("this host was built with no services."));
    }

    /// <summary>Begins one operation.</summary>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <param name="frontEnd">What the front end supplies: where the operation writes, who it may ask, and its document's stopped form.</param>
    /// <returns>The operation's session.</returns>
    public CommandSession Begin(SessionOptions options, SessionFrontEnd? frontEnd = null)
        => new(this, options, frontEnd, _services(options));

    /// <summary>
    /// Hands an operation's end to the operation it is a step of, to be awaited however that operation ended - never
    /// cut short by this one finishing.
    /// </summary>
    /// <param name="outer">The operation the end belongs to.</param>
    /// <param name="end">The end.</param>
    internal void Defer(OperationId outer, Func<Task> end)
    {
        ArgumentNullException.ThrowIfNull(end);

        lock (_gate)
        {
            if (!_deferred.TryGetValue(outer, out var ends))
            {
                ends = [];
                _deferred.Add(outer, ends);
            }

            ends.Add(end);
        }
    }

    /// <summary>Takes every end handed to an operation, for its own end to await.</summary>
    /// <param name="outer">The operation the ends were handed to.</param>
    /// <returns>The ends, in the order they were handed.</returns>
    internal IReadOnlyList<Func<Task>> TakeDeferred(OperationId outer)
    {
        lock (_gate)
        {
            return _deferred.Remove(outer, out var ends) ? ends : [];
        }
    }
}
