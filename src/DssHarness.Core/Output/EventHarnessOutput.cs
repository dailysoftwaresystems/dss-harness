using DssHarness.Core.Platform;
using System.Threading.Channels;

namespace DssHarness.Core.Output;

/// <summary>
/// The output of one operation: the eight members that write - a line of the harness's own, a piece of the answer, a
/// child's line - each become an event, and <see cref="Publish"/> for what only an event can say.
/// </summary>
/// <remarks>
/// Every event is stamped, under one lock, with the operation it belongs to and the sequence and time it happened -
/// the stamp is the publisher's, never the maker's, so no event can invent its place. The console's sink is called
/// under that same lock, as today's writer is, so lines keep their order on the terminal. Any other sink is fed from a
/// bounded queue drained by a task of its own, in sequence order: where the queue is full an event is dropped and
/// counted, and the sink is told so by <see cref="EventsDropped"/> where the gap was - after the last event it was
/// given before it and before the first after it, stamped with this operation and the sequence and time of the last
/// event it stands for - so a slow sink never holds a writer and never mistakes silence for completeness. What is
/// dropped once the queue is closed is told when the sink has been given the rest.
/// </remarks>
public sealed class EventHarnessOutput : IHarnessOutput, IDisposable
{
    /// <summary>How many events a sink that is not the console may lag behind, at most.</summary>
    public const int QueueLimit = 4096;

    /// <summary>How long disposing waits for a lagging sink, at most, where its output was given no other limit.</summary>
    public static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    private readonly TextRunEvents? _console;
    private readonly IRunEvents _sink;
    private readonly HomeShorthand _home;
    private readonly OperationId? _operation;
    private readonly OperationId? _parent;
    private readonly bool _verbose;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _drainLimit;
    private readonly Lock _gate = new();
    private readonly Channel<Queued>? _queued;
    private readonly Task? _draining;
    private long _sequence;

    /// <summary>How many events were dropped since the last one the queue took, the last of them at <see cref="_lastDropped"/>.</summary>
    private int _dropped;

    /// <summary>The sequence and time of the last event dropped, which the notice of the gap is stamped with.</summary>
    private (long Sequence, DateTimeOffset At) _lastDropped;

    /// <summary>Whether the sink has been told the last of what this output will ever tell it.</summary>
    private bool _drained;

    private bool _sinkDetached;
    private Exception? _sinkFault;
    private bool _abandoned;

    /// <summary>
    /// The output of an operation whose sink is the console: lines as they have always been written, called under the
    /// publisher's lock.
    /// </summary>
    /// <param name="console">The console's renderer.</param>
    /// <param name="operation">The operation every event is stamped with, where it has an id.</param>
    /// <param name="parent">The operation this one belongs to, where there is one.</param>
    public EventHarnessOutput(TextRunEvents console, OperationId? operation = null, OperationId? parent = null)
    {
        ArgumentNullException.ThrowIfNull(console);

        _console = console;
        _sink = console;
        _home = HomeShorthand.None;
        _operation = operation;
        _parent = parent;
        _verbose = console.Verbose;
        _clock = TimeProvider.System;
        _drainLimit = DrainLimit;
    }

    /// <summary>
    /// The output of an operation whose sink is not the console: a front end of its own, fed from a bounded queue by a
    /// task that never holds a writer.
    /// </summary>
    /// <param name="sink">The front end's sink, called serially in sequence order.</param>
    /// <param name="operation">The operation every event is stamped with.</param>
    /// <param name="parent">The operation this one belongs to, where there is one.</param>
    /// <param name="clock">When events happen.</param>
    /// <param name="drainLimit">How long disposing waits for the sink to be given what is queued, at most; <see cref="DrainLimit"/> where not given.</param>
    public EventHarnessOutput(IRunEvents sink, OperationId operation, OperationId? parent = null, TimeProvider? clock = null, TimeSpan? drainLimit = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _home = HomeShorthand.None;
        _operation = operation;
        _parent = parent;
        _clock = clock ?? TimeProvider.System;
        _drainLimit = drainLimit ?? DrainLimit;
        _queued = Channel.CreateBounded<Queued>(new BoundedChannelOptions(QueueLimit) { SingleReader = true });
        _draining = Task.Run(() => DrainAsync(_queued.Reader, sink));
    }

    /// <summary>The operation every event this output publishes is stamped with; <see langword="null"/> where it was given none.</summary>
    public OperationId? Operation => _operation;

    /// <summary>The operation this one belongs to, which every event this output publishes names; <see langword="null"/> where there is none.</summary>
    public OperationId? Parent => _parent;

    /// <inheritdoc/>
    public bool IsVerbose => _verbose;

    /// <inheritdoc/>
    public bool IsDataOnly => _console?.IsDataOnly ?? false;

    /// <summary>
    /// What the sink threw, where it threw: it was called no more after it, and no writer and no exit code was the worse
    /// for it - kept here, so its failure is the front end's to read rather than lost. <see langword="null"/> where it
    /// threw nothing.
    /// </summary>
    public Exception? SinkFault
    {
        get
        {
            lock (_gate)
            {
                return _sinkFault;
            }
        }
    }

    /// <summary>
    /// Whether disposing gave up on the sink: it had not been given everything queued within the drain limit, and was
    /// called no more - what it had not been given by then never reaches it.
    /// </summary>
    public bool Abandoned
    {
        get
        {
            lock (_gate)
            {
                return _abandoned;
            }
        }
    }

    /// <inheritdoc/>
    /// <exception cref="ObjectDisposedException">
    /// Published to a sink that is not the console once it has been told the last of this output - after
    /// <see cref="Dispose"/> drained it - where nothing could ever tell it the event was missed.
    /// </exception>
    public void Publish(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_drained, this);

            var stamped = @event with
            {
                Operation = _operation,
                Parent = _parent,
                Sequence = ++_sequence,
                At = _clock.GetUtcNow(),
            };

            if (_console is not null)
            {
                // As today's writer does: the whole of the line is written under the one lock, so lines keep their
                // order on the terminal whatever else is publishing.
                _console.Emit(stamped);
                return;
            }

            // A sink that is not the console is never held by a writer: its events wait in a bounded queue, taken in
            // the same order they were stamped, and where that is full they are dropped and counted - the gap carried
            // by the next one the queue takes, to be told just before it, where it was.
            if (_queued!.Writer.TryWrite(new Queued(stamped, Gap())))
            {
                _dropped = 0;
            }
            else
            {
                _dropped++;
                _lastDropped = (stamped.Sequence, stamped.At);
            }
        }
    }

    /// <inheritdoc/>
    public void Ok(string command, string message) => Text(LineKind.Ok, command, message);

    /// <inheritdoc/>
    public void Fail(string command, string message) => Text(LineKind.Fail, command, message);

    /// <inheritdoc/>
    public void Warn(string command, string message) => Text(LineKind.Warn, command, message);

    /// <inheritdoc/>
    public void Info(string command, string message) => Text(LineKind.Info, command, message);

    /// <inheritdoc/>
    public void Detail(string command, string message) => Text(LineKind.Detail, command, message);

    /// <inheritdoc/>
    public void Data(string text) => Publish(new DataWritten(text));

    /// <inheritdoc/>
    public void Raw(string line) => Publish(new PassthroughLine(IsError: false, line));

    /// <inheritdoc/>
    public void RawError(string line) => Publish(new PassthroughLine(IsError: true, line));

    /// <inheritdoc/>
    public string Shown(string text) => _console?.Shown(text) ?? _home.Shown(text);

    /// <inheritdoc/>
    public IDisposable DataOnly() => _console?.DataOnly() ?? NoScope;

    /// <inheritdoc/>
    /// <remarks>
    /// Closes the queue and waits, for the drain limit at most, for the sink to be given what is in it and what was
    /// dropped meanwhile; a sink still behind then is abandoned (<see cref="Abandoned"/>) and called no more.
    /// </remarks>
    public void Dispose()
    {
        if (_queued is null || _draining is null)
        {
            return;
        }

        Complete();

        if (!_draining.Wait(_drainLimit))
        {
            lock (_gate)
            {
                _abandoned = true;
                _sinkDetached = true;
            }
        }
    }

    /// <summary>
    /// Closes the queue: what is in it is still given to the sink, and what is published from here until the sink has
    /// been given it is dropped and counted, and told after it.
    /// </summary>
    internal void Complete() => _queued?.Writer.TryComplete();

    private void Text(LineKind kind, string command, string message) => Publish(new TextLine(kind, command, message));

    /// <summary>
    /// The gap the events dropped since the last one the queue took leave, where any were: stamped with this operation and
    /// with the sequence and time of the last of them, so it falls in sequence order where they did. Asked under the lock.
    /// </summary>
    private EventsDropped? Gap()
        => _dropped == 0
            ? null
            : new EventsDropped(_dropped)
            {
                Operation = _operation,
                Parent = _parent,
                Sequence = _lastDropped.Sequence,
                At = _lastDropped.At,
            };

    /// <summary>Feeds the sink from the queue, in order, for as long as the operation runs, and then what was dropped last.</summary>
    private async Task DrainAsync(ChannelReader<Queued> reader, IRunEvents sink)
    {
        await foreach (var (@event, gap) in reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (gap is not null)
            {
                // Told where the gap was: after what the sink was given before it, before what it is given next.
                EmitTo(sink, gap);
            }

            EmitTo(sink, @event);
        }

        EventsDropped? last;

        lock (_gate)
        {
            // What no event the queue took carries - dropped at its end, or once it was closed - is told last, and
            // nothing is published to the sink after it.
            last = Gap();
            _dropped = 0;
            _drained = true;
        }

        if (last is not null)
        {
            EmitTo(sink, last);
        }
    }

    /// <summary>Hands one event to the sink, which is never allowed to end the operation or hold its writer.</summary>
    private void EmitTo(IRunEvents sink, RunEvent @event)
    {
        lock (_gate)
        {
            if (_sinkDetached)
            {
                return;
            }
        }

        try
        {
            sink.Emit(@event);
        }
        catch (Exception ex)
        {
            // A sink that throws is called no more: its failure is the front end's, not this operation's, and no exit
            // code changes for it - but what it threw is kept for the front end to read.
            lock (_gate)
            {
                _sinkDetached = true;
                _sinkFault ??= ex;
            }
        }
    }

    /// <summary>An event waiting for the sink, with the gap that comes before it where events were dropped there.</summary>
    private readonly record struct Queued(RunEvent Event, EventsDropped? Gap);

    /// <summary>The scope of a sink the document redirection says nothing to.</summary>
    private static readonly IDisposable NoScope = new EmptyScope();

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
