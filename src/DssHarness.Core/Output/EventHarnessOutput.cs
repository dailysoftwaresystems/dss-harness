using DssHarness.Core.Platform;
using DssHarness.Core.Waiting;

namespace DssHarness.Core.Output;

/// <summary>
/// The output of one operation: the eight members that write - a line of the harness's own, a piece of the answer, a
/// child's line - each become an event, and <see cref="Publish"/> for what only an event can say.
/// </summary>
/// <remarks>
/// Every event is stamped, under one lock, with the operation it belongs to and the sequence and time it happened -
/// the stamp is the publisher's, never the maker's, so no event can invent its place. The console's sink is called
/// under that same lock, as today's writer is, so lines keep their order on the terminal. Any other sink is given every
/// event, in sequence order, by a task of its own, so a slow sink never holds a writer: what it has not yet been given
/// waits in memory up to <see cref="HeldInMemory"/> events, and past that in the operation's event file
/// (<see cref="EventSpill"/>), read back in order as the sink takes it - none is ever dropped. Disposing waits for the
/// sink to be given the last of them, however long it takes, said every five minutes.
/// </remarks>
public sealed class EventHarnessOutput : IHarnessOutput, IDisposable
{
    /// <summary>
    /// How many events wait in memory for a sink that is not the console before the rest wait in the operation's event
    /// file: a page of waiting, not a bound, since every event past it waits on disk and is given in its turn.
    /// </summary>
    public const int HeldInMemory = 4096;

    private readonly TextRunEvents? _console;
    private readonly IRunEvents _sink;
    private readonly HomeShorthand _home;
    private readonly OperationId? _operation;
    private readonly OperationId? _parent;
    private readonly bool _verbose;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <summary>The events a queued sink has not been given, in memory, oldest first: every one older than any that waits on disk.</summary>
    private readonly Queue<RunEvent>? _held;

    /// <summary>One count for each event that waits, and one for the close: what the task that feeds the sink waits on.</summary>
    private readonly SemaphoreSlim? _ready;

    /// <summary>Makes the event file the first time an event waits there.</summary>
    private readonly Func<EventSpill>? _spillMade;

    private readonly Task? _draining;
    private EventSpill? _spill;
    private long _sequence;

    /// <summary>Whether the output has been closed: the sink is given what waits, and then the last of it.</summary>
    private bool _closed;

    /// <summary>Whether the sink has been told the last of what this output will ever tell it.</summary>
    private bool _drained;

    private bool _sinkDetached;
    private Exception? _sinkFault;

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
    /// The output of an operation whose sink is not the console: a front end of its own, given every event by a task that
    /// never holds a writer.
    /// </summary>
    /// <param name="sink">The front end's sink, called serially in sequence order.</param>
    /// <param name="operation">The operation every event is stamped with.</param>
    /// <param name="parent">The operation this one belongs to, where there is one.</param>
    /// <param name="clock">When events happen.</param>
    /// <param name="spill">
    /// Where events the sink lags behind wait past <see cref="HeldInMemory"/>: the operation's event file, made the first
    /// time one waits there; one in this machine's temporary directory, removed with the output, where none is given.
    /// </param>
    public EventHarnessOutput(IRunEvents sink, OperationId operation, OperationId? parent = null, TimeProvider? clock = null, Func<EventSpill>? spill = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        _sink = sink;
        _home = HomeShorthand.None;
        _operation = operation;
        _parent = parent;
        _clock = clock ?? TimeProvider.System;
        _held = new Queue<RunEvent>();
        _ready = new SemaphoreSlim(0);
        _spillMade = spill ?? (() => EventSpill.Temporary(operation));
        _draining = Task.Run(() => DrainAsync(sink));
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
    /// How many events wait for the sink set down for the operation's event file (<see cref="EventSpill"/>) rather than
    /// held in memory, at this moment: none while the sink keeps up.
    /// </summary>
    public long WaitingOnDisk
    {
        get
        {
            lock (_gate)
            {
                return _spill?.Waiting ?? 0;
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

            // A sink that is not the console is never held by a writer: its events wait, in the order they were stamped,
            // in memory while few wait, and past that on disk - every one after the first that waits there going there
            // too, so the order holds - and the task that feeds the sink takes them as it can.
            if ((_spill?.Waiting ?? 0) == 0 && _held!.Count < HeldInMemory)
            {
                _held.Enqueue(stamped);
            }
            else
            {
                (_spill ??= _spillMade!()).Add(stamped);
            }

            _ready!.Release();
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
    /// Closes the output and waits for the sink to be given every event that waits, from memory and from the event file,
    /// however long that takes - said every five minutes to whoever the operation tells its waits to - and then removes
    /// the event file.
    /// </remarks>
    public void Dispose()
    {
        if (_draining is null)
        {
            return;
        }

        Complete();

        var draining = _draining;
        _ = Waits.ForAsync(
            async _ =>
            {
                await draining.ConfigureAwait(false);
                return true;
            },
            new WaitWatch("the front end to be given the operation's last events", Waits.Told, null, _clock),
            CancellationToken.None).GetAwaiter().GetResult();

        lock (_gate)
        {
            _spill?.Dispose();
        }
    }

    /// <summary>
    /// Closes the output: what waits is still given to the sink, and what is published from here until the sink has been
    /// given it is given after it; once the sink has been told the last of it, a publish is refused.
    /// </summary>
    internal void Complete()
    {
        lock (_gate)
        {
            if (_ready is null || _closed)
            {
                return;
            }

            _closed = true;
            _ready.Release();
        }
    }

    private void Text(LineKind kind, string command, string message) => Publish(new TextLine(kind, command, message));

    /// <summary>Feeds the sink every event in order, from memory and then from the event file, until the output is closed and nothing waits.</summary>
    private async Task DrainAsync(IRunEvents sink)
    {
        while (true)
        {
            await _ready!.WaitAsync().ConfigureAwait(false);

            RunEvent? next;

            lock (_gate)
            {
                // Memory first: every event there is older than any that waits on disk.
                next = _held!.Count > 0 ? _held.Dequeue() : _spill is { Waiting: > 0 } spill ? spill.Take() : null;

                if (next is null)
                {
                    // The close's own count, with nothing left: nothing is given after this.
                    if (_closed)
                    {
                        _drained = true;
                        return;
                    }

                    continue;
                }
            }

            EmitTo(sink, next);
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

    /// <summary>The scope of a sink the document redirection says nothing to.</summary>
    private static readonly IDisposable NoScope = new EmptyScope();

    private sealed class EmptyScope : IDisposable
    {
        public void Dispose()
        {
        }
    }
}
