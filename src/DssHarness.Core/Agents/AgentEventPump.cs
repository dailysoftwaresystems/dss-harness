using System.Threading.Channels;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one pump a run has: producers post events to a bounded channel, stamped as they are posted, and this numbers
/// them - <see cref="AgentEvent.Seq"/> - and hands each to the step's record first and then to every listener, serially,
/// in order.
/// </summary>
/// <remarks>
/// <para>
/// The bound is the channel's: a producer that outruns its sinks waits, for a record the sinks keep is never one the
/// pump may drop. A producer stops waiting at the run's stop - the hard stop, or the record lost - so a sink that hangs
/// holds no producer past it.
/// </para>
/// <para>
/// The record is the one sink the run cannot do without. It is handed each event before any listener is, so what a
/// listener does - fail, or take its time - never stands between the event and the record. Where the record fails, it
/// gets no more events, its failure is held in <see cref="RecordFailure"/>, and the run's stop is raised, for a run whose
/// record is lost is not one to go on with; what a callback on that stop raises is held in <see cref="StopFailure"/>.
/// A listener that throws gets no more events, and its exception is held in
/// <see cref="ListenerFailure"/> for after the run - the rule a line handler of a phase already has: what the child said
/// is not lost to what a listener did. A listener still holding an event when the hard stop comes is let go: it gets no
/// more events, and the record goes on without waiting for it.
/// </para>
/// <para>
/// Listeners are attached before the first event is taken; one attached later is not called, and does not exist to the
/// pump. The pump is the only writer of <see cref="AgentEvent.Seq"/>, so no maker of an event can number it, and the two
/// ends of a run are the runner's own: a producer that posts either through <see cref="EmitAsync"/> is refused. The end
/// is made by the drain itself, after every event before it, so it says whether the record holds them all.
/// </para>
/// </remarks>
public sealed class AgentEventPump : IAgentEventSink, IAsyncDisposable
{
    /// <summary>How many events may wait for their sinks before a producer is made to wait with them.</summary>
    public const int Capacity = 1024;

    private readonly Channel<AgentEvent> _incoming = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly IAgentEventSink _record;

    private readonly List<IAgentEventSink> _listeners = [];

    private readonly TimeProvider _clock;

    private readonly CancellationToken _hardStop;

    private readonly CancellationTokenSource _stopping;

    private readonly Task _draining;

    private Task? _recordLost;

    private volatile Func<Exception?, AgentEvent>? _last;

    /// <summary>
    /// Makes the pump, its draining begun: no event is taken until the first producer posts one, so listeners may be
    /// attached before anything is delivered to them.
    /// </summary>
    /// <param name="record">The step's record, handed every event first, and the one sink whose failure stops the run.</param>
    /// <param name="clock">When each event happened, where its maker said not: the moment it was posted.</param>
    /// <param name="hardStop">The run's hard stop, at which a waiting producer stops waiting and a listener still holding an event is let go.</param>
    public AgentEventPump(IAgentEventSink record, TimeProvider clock, CancellationToken hardStop)
    {
        _record = record ?? throw new ArgumentNullException(nameof(record));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _hardStop = hardStop;
        _stopping = CancellationTokenSource.CreateLinkedTokenSource(hardStop);
        _draining = Task.Run(DrainAllAsync);
    }

    /// <summary>The run's stop: the hard stop, or the record lost. What a client is handed as its cancellation.</summary>
    public CancellationToken Stopping => _stopping.Token;

    /// <summary>What the record raised, where it did: the run's record is lost from that event on.</summary>
    public Exception? RecordFailure { get; private set; }

    /// <summary>
    /// What a listener raised, where one did: the first failure, held until the run has ended rather than raised into the
    /// middle of it.
    /// </summary>
    public Exception? ListenerFailure { get; private set; }

    /// <summary>
    /// What a callback on the run's stop raised, where the pump raised that stop for the record lost: held until the run has
    /// ended, as a listener's failure is. A stop the caller raised says its callbacks' failures to the caller.
    /// </summary>
    public Exception? StopFailure { get; private set; }

    /// <summary>
    /// Attaches a listener. Called before the first event is taken: a listener attached later does not exist to the pump.
    /// </summary>
    /// <param name="sink">The listener, called serially, in order, after the record.</param>
    public void Attach(IAgentEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _listeners.Add(sink);
    }

    /// <summary>
    /// Posts one event of a producer's for its sinks, stamped now where its maker set no time, waiting where they have not
    /// taken what waits before it - until the run's stop, at which the wait ends in its cancellation.
    /// </summary>
    /// <param name="event">The event, its <see cref="AgentEvent.Seq"/> still the pump's to give.</param>
    /// <exception cref="InvalidOperationException">The event is one of the run's two ends, which are the runner's own.</exception>
    /// <exception cref="OperationCanceledException">The run stopped while the producer waited for room.</exception>
    public ValueTask EmitAsync(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is RunStarted or RunFinished)
        {
            throw new InvalidOperationException(
                $"'{@event.GetType().Name}' is the runner's own event, which a client never emits: the runner writes each end of every run itself.");
        }

        return PostAsync(@event, _stopping.Token);
    }

    /// <summary>
    /// Posts the runner's own beginning of a run, waiting for room however the run stopped: the beginning of an attempt is
    /// what tells it from the one before, and a stop is no reason to leave it out.
    /// </summary>
    /// <param name="event">The event.</param>
    internal ValueTask PostOwnAsync(AgentEvent @event) => PostAsync(@event, CancellationToken.None);

    /// <summary>
    /// Ends the run with the runner's own last event: no more are posted, and once every event posted before it has been
    /// handed over - so that whether the record was lost is known whole - <paramref name="last"/> makes the end from that,
    /// and it is handed out last. Where the record fails on the end itself, the end is made again, so that a listener is
    /// handed the end the run's result says.
    /// </summary>
    /// <param name="last">The run's end, made from what the record raised; null where the record holds every event.</param>
    internal async Task FinishAsync(Func<Exception?, AgentEvent> last)
    {
        _last = last ?? throw new ArgumentNullException(nameof(last));
        await CompleteAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// Says no more events will be posted, and returns once what was posted has been handed to the sinks that are left and
    /// a stop the pump raised has run its course. A failure stays in its property: nothing here raises one.
    /// </summary>
    public async Task CompleteAsync()
    {
        _incoming.Writer.TryComplete();
        await _draining.ConfigureAwait(false);

        if (Interlocked.Exchange(ref _recordLost, null) is { } stopped)
        {
            try
            {
                await stopped.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A callback on the stop threw - the client's, or whoever holds the token - and it is kept as a listener's
                // failure is, for after the run, never raised into the middle of the run's own ending.
                StopFailure ??= ex;
            }
        }
    }

    /// <summary>Ends the pumping, once what was posted has been handed over. A failure stays in its property.</summary>
    public async ValueTask DisposeAsync()
    {
        await CompleteAsync().ConfigureAwait(false);
        _stopping.Dispose();
    }

    /// <summary>Stamps an event as of now, where its maker did not, and posts it; only a wait for room observes the token.</summary>
    /// <param name="event">The event.</param>
    /// <param name="stopping">What ends a wait for room.</param>
    private ValueTask PostAsync(AgentEvent @event, CancellationToken stopping)
    {
        var stamped = Stamped(@event);

        return _incoming.Writer.TryWrite(stamped) ? ValueTask.CompletedTask : _incoming.Writer.WriteAsync(stamped, stopping);
    }

    /// <summary>The event stamped as of now, where its maker set no time.</summary>
    /// <param name="event">The event.</param>
    private AgentEvent Stamped(AgentEvent @event) => @event.At == default ? @event with { At = _clock.GetUtcNow() } : @event;

    /// <summary>
    /// Takes each event as it comes, numbers it, and hands it to the record and then to every listener left - and, once the
    /// last is taken, the run's end, where the runner finishes the run.
    /// </summary>
    private async Task DrainAllAsync()
    {
        var seq = 0L;

        await foreach (var posted in _incoming.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var numbered = posted with { Seq = ++seq };

            await RecordAsync(numbered).ConfigureAwait(false);
            await ListenersAsync(numbered).ConfigureAwait(false);
        }

        if (_last is { } last)
        {
            var end = Stamped(last(RecordFailure)) with { Seq = ++seq };
            var lostBefore = RecordFailure;

            await RecordAsync(end).ConfigureAwait(false);

            if (!ReferenceEquals(lostBefore, RecordFailure))
            {
                end = last(RecordFailure) with { At = end.At, Seq = end.Seq };
            }

            await ListenersAsync(end).ConfigureAwait(false);
        }
    }

    /// <summary>Hands one event to the record, unless the record is lost already; where it fails now, the run is stopped.</summary>
    /// <param name="event">The event, numbered.</param>
    private async Task RecordAsync(AgentEvent @event)
    {
        if (RecordFailure is not null)
        {
            return;
        }

        try
        {
            await _record.EmitAsync(@event).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // The record is lost from here on, and a line half written is not one to write after: it gets no more events,
            // and the run is stopped - its stop raised off this loop, so that nothing the stop sets going waits on the
            // loop that raised it.
            RecordFailure = ex;
            _recordLost = _stopping.CancelAsync();
        }
    }

    /// <summary>Hands one event to every listener left, in the order they were attached.</summary>
    /// <param name="event">The event, numbered.</param>
    private async Task ListenersAsync(AgentEvent @event)
    {
        for (var index = 0; index < _listeners.Count;)
        {
            index = await HandAsync(_listeners[index], @event).ConfigureAwait(false) ? index + 1 : RemoveAt(index);
        }
    }

    /// <summary>
    /// Hands one event to one listener: true where it took it, false where it is to get no more - it failed, or it still
    /// held the event when the hard stop came.
    /// </summary>
    /// <param name="listener">The listener.</param>
    /// <param name="event">The event.</param>
    private async Task<bool> HandAsync(IAgentEventSink listener, AgentEvent @event)
    {
        Task? taking = null;

        try
        {
            var handed = listener.EmitAsync(@event);

            if (handed.IsCompleted)
            {
                handed.GetAwaiter().GetResult();
                return true;
            }

            taking = handed.AsTask();
            await taking.WaitAsync(_hardStop).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (taking is { IsCompleted: false })
        {
            // The hard stop came while it held the event: it is let go, and whatever it raises later is seen to so that
            // it is never raised unobserved.
            _ = taking.ContinueWith(
                static abandoned => _ = abandoned.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return false;
        }
        catch (Exception ex)
        {
            // It gets no more events, and its failure is the run's once the run has ended; the record is whole.
            ListenerFailure ??= ex;
            return false;
        }
    }

    /// <summary>Removes the listener at <paramref name="index"/>, and answers where the next one now stands.</summary>
    /// <param name="index">The listener's place.</param>
    private int RemoveAt(int index)
    {
        _listeners.RemoveAt(index);
        return index;
    }
}
