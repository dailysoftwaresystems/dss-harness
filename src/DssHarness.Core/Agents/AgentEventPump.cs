using System.Threading.Channels;

namespace DssHarness.Core.Agents;

/// <summary>
/// The one pump a run has: producers post events to a bounded channel, and this numbers them - <see cref="AgentEvent.Seq"/>,
/// and <see cref="AgentEvent.At"/> where its maker set none - and hands each to every sink, serially, in order.
/// </summary>
/// <remarks>
/// <para>
/// The bound is the channel's: a producer that outruns its sinks waits, for a record the sinks keep is never one the
/// pump may drop. One sink that throws gets no more events - the others keep theirs, and the stored stream keeps its
/// record - and its exception is raised once the run has ended, which is the rule a line handler of a phase already
/// has: what the child said is not lost to what a listener did.
/// </para>
/// <para>
/// Sinks are attached before the first event is taken; a sink attached later is not called, and does not exist to the
/// pump. The pump is the only writer of <see cref="AgentEvent.Seq"/>, so no maker of an event can number it.
/// </para>
/// </remarks>
public sealed class AgentEventPump : IAgentEventSink, IAsyncDisposable
{
    /// <summary>How many events may wait for their sinks before a producer is made to wait with them.</summary>
    public const int Capacity = 1024;

    private readonly Channel<AgentEvent> _incoming = Channel.CreateBounded<AgentEvent>(
        new BoundedChannelOptions(Capacity) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });

    private readonly List<IAgentEventSink> _live = [];

    private readonly TimeProvider _clock;

    private readonly Task _draining;

    /// <summary>
    /// Makes the pump, its draining begun: no event is taken until the first producer posts one, so sinks may be
    /// attached before anything is delivered to them.
    /// </summary>
    /// <param name="clock">When each event happened, where its maker said not.</param>
    public AgentEventPump(TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _draining = Task.Run(DrainAllAsync);
    }

    /// <summary>The pump's own draining of its channel, which ends once <see cref="Complete"/> is called and every posted event has been handed to its sinks.</summary>
    public Task Drained => _draining;

    /// <summary>
    /// Attaches a sink. Called before the first event is taken: a sink attached later does not exist to the pump.
    /// </summary>
    /// <param name="sink">The sink, called serially, in order.</param>
    public void Attach(IAgentEventSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _live.Add(sink);
    }

    /// <summary>Posts one event for its sinks, waiting where they have not taken what waits before it.</summary>
    /// <param name="event">The event, its <see cref="AgentEvent.Seq"/> and <see cref="AgentEvent.At"/> still the pump's to give.</param>
    public ValueTask EmitAsync(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
        return _incoming.Writer.WriteAsync(@event);
    }

    /// <summary>Says no more events will be posted: the draining ends once what was posted has been handed over.</summary>
    public void Complete() => _incoming.Writer.TryComplete();

    /// <summary>
    /// What a sink raised, where one did: the first failure, held until the run has ended rather than raised into the
    /// middle of it. <see cref="DisposeAsync"/> raises it once the draining is done.
    /// </summary>
    public Exception? Failure { get; private set; }

    /// <summary>
    /// Ends the pumping and raises what a sink raised, now that the run has ended: the first failure, as one listener's
    /// is raised where a phase's line handlers each raised one.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Complete();
        await _draining.ConfigureAwait(false);

        if (Failure is { } failed)
        {
            throw failed;
        }
    }

    /// <summary>Takes each event as it comes, numbers it, and hands it to every sink that has not failed.</summary>
    private async Task DrainAllAsync()
    {
        var seq = 0L;

        await foreach (var posted in _incoming.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var numbered = posted with
            {
                At = posted.At == default ? _clock.GetUtcNow() : posted.At,
                Seq = ++seq,
            };

            for (var index = _live.Count - 1; index >= 0; index--)
            {
                try
                {
                    await _live[index].EmitAsync(numbered).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // It gets no more events, and its failure is the run's once the run has ended; the sinks that did
                    // not fail keep the record whole.
                    Failure ??= ex;
                    _live.RemoveAt(index);
                }
            }
        }
    }
}
