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
/// id; an operation takes them once, as its own end, and none is handed to it after that.
/// </remarks>
public sealed class DssHarnessHost
{
    private readonly Func<SessionOptions, IServiceProvider> _services;
    private readonly Lock _gate = new();

    /// <summary>
    /// The operations begun and not yet ended, each with the ends handed to it so far: an operation not here takes no
    /// end, since nothing would ever run it.
    /// </summary>
    private readonly Dictionary<OperationId, List<Func<Task>>> _open = [];

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
    /// <exception cref="InvalidOperationException">The options contradict themselves or the front end, refused before any service is built.</exception>
    /// <exception cref="NotSupportedException">An option or a part of the front end this build does not honour yet, refused before any service is built.</exception>
    public CommandSession Begin(SessionOptions options, SessionFrontEnd? frontEnd = null)
    {
        CommandSession.RequireHonoured(options, frontEnd);

        var services = _services(options);

        try
        {
            return new(this, options, frontEnd, services);
        }
        catch
        {
            // A session that could not begin owns nothing it was built: what was built for it goes now.
            (services as IDisposable)?.Dispose();
            throw;
        }
    }

    /// <summary>Opens an operation to the ends its own steps hand it, from its beginning until its own end takes them.</summary>
    /// <param name="operation">The operation beginning.</param>
    internal void Open(OperationId operation)
    {
        lock (_gate)
        {
            _open.Add(operation, []);
        }
    }

    /// <summary>
    /// Hands an operation's end to the operation it is a step of, to be awaited however that operation ended - never
    /// cut short by this one finishing - where that operation is still open to it.
    /// </summary>
    /// <param name="outer">The operation the end belongs to.</param>
    /// <param name="end">The end.</param>
    /// <returns>
    /// Whether <paramref name="outer"/> took the end; false where it has already taken its ends, or is no operation of
    /// this host's, and the end is its own operation's to run at once.
    /// </returns>
    internal bool Defer(OperationId outer, Func<Task> end)
    {
        ArgumentNullException.ThrowIfNull(end);

        lock (_gate)
        {
            if (!_open.TryGetValue(outer, out var ends))
            {
                return false;
            }

            ends.Add(end);
            return true;
        }
    }

    /// <summary>Takes every end handed to an operation, for its own end to await, and closes it to any more.</summary>
    /// <param name="outer">The operation the ends were handed to.</param>
    /// <returns>The ends, in the order they were handed.</returns>
    internal IReadOnlyList<Func<Task>> TakeDeferred(OperationId outer)
    {
        lock (_gate)
        {
            return _open.Remove(outer, out var ends) ? ends : [];
        }
    }
}
