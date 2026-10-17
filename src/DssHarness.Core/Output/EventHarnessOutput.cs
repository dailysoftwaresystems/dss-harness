using DssHarness.Core.Platform;
using System.Threading.Channels;

namespace DssHarness.Core.Output;

/// <summary>
/// The output of one operation: the twelve members the harness has always written, each become an event, and
/// <see cref="Publish"/> for what only an event can say.
/// </summary>
/// <remarks>
/// Every event is stamped, under one lock, with the operation it belongs to and the sequence and time it happened -
/// the stamp is the publisher's, never the maker's, so no event can invent its place. The console's sink is called
/// under that same lock, as today's writer is, so lines keep their order on the terminal. Any other sink is fed from a
/// bounded queue drained by a task of its own, in sequence order: where the queue is full an event is dropped and
/// counted, and the next one the sink is given is preceded by <see cref="EventsDropped"/>, so a slow sink never holds
/// a writer and never mistakes silence for completeness.
/// </remarks>
public sealed class EventHarnessOutput : IHarnessOutput, IDisposable
{
    /// <summary>How many events a sink that is not the console may lag behind, at most.</summary>
    public const int QueueLimit = 4096;

    /// <summary>How long disposing waits for a lagging sink, at most.</summary>
    public static readonly TimeSpan DrainLimit = TimeSpan.FromSeconds(5);

    private readonly TextRunEvents? _console;
    private readonly IRunEvents _sink;
    private readonly HomeShorthand _home;
    private readonly OperationId? _operation;
    private readonly OperationId? _parent;
    private readonly bool _verbose;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Channel<RunEvent>? _queued;
    private readonly Task? _draining;
    private long _sequence;
    private int _dropped;
    private bool _sinkDetached;

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
    }

    /// <summary>
    /// The output of an operation whose sink is not the console: a front end of its own, fed from a bounded queue by a
    /// task that never holds a writer.
    /// </summary>
    /// <param name="sink">The front end's sink, called serially in sequence order.</param>
    /// <param name="operation">The operation every event is stamped with.</param>
    /// <param name="parent">The operation this one belongs to, where there is one.</param>
    /// <param name="clock">When events happen.</param>
    public EventHarnessOutput(IRunEvents sink, OperationId operation, OperationId? parent = null, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _home = HomeShorthand.None;
        _operation = operation;
        _parent = parent;
        _clock = clock ?? TimeProvider.System;
        _queued = Channel.CreateBounded<RunEvent>(new BoundedChannelOptions(QueueLimit) { SingleReader = true });
        _draining = Task.Run(() => DrainAsync(_queued.Reader, sink));
    }

    /// <inheritdoc/>
    public bool IsVerbose => _verbose;

    /// <inheritdoc/>
    public bool IsDataOnly => _console?.IsDataOnly ?? false;

    /// <inheritdoc/>
    public void Publish(RunEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        lock (_gate)
        {
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
            // the same order they were stamped, and where that is full they are dropped and counted, to be said
            // before the next one that does reach it.
            if (_queued is not null && !_queued.Writer.TryWrite(stamped))
            {
                _dropped++;
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
    public void Dispose()
    {
        if (_queued is null || _draining is null)
        {
            return;
        }

        _queued.Writer.TryComplete();
        _draining.Wait(DrainLimit);
    }

    private void Text(LineKind kind, string command, string message) => Publish(new TextLine(kind, command, message));

    /// <summary>Feeds the sink from the queue, in order, for as long as the operation runs.</summary>
    private async Task DrainAsync(ChannelReader<RunEvent> reader, IRunEvents sink)
    {
        await foreach (var @event in reader.ReadAllAsync().ConfigureAwait(false))
        {
            int dropped;

            lock (_gate)
            {
                dropped = _dropped;
                _dropped = 0;
            }

            if (dropped > 0)
            {
                // Said before the next one the sink is given, so it knows what it missed before it is asked to make
                // sense of what came after it.
                EmitTo(sink, new EventsDropped(dropped));
            }

            EmitTo(sink, @event);
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
        catch
        {
            // A sink that throws is swallowed once, and never called again: its failure is the front end's, not this
            // operation's, and no exit code changes for it.
            lock (_gate)
            {
                _sinkDetached = true;
            }
        }
    }

    /// <summary>The scope of a sink the document redirection says nothing to.</summary>
    private static readonly IDisposable NoScope = new EmptyScope();

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
