using System.Globalization;
using System.Runtime.InteropServices;
using DssHarness.Core.Execution;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Counts the interrupts of one session of a run and says what each means to it: the first asks the engine for a safe
/// pause and starts no clock - the engine may stay for as long as a build takes - and the second, while the handoffs are
/// being written or once the pause is complete alike, asks the engine to stop where it stands. The engine's loop, which
/// alone owns the graph, applies each and ends the session in order; what a stop does with a pause already complete is
/// the engine's own to decide, and how long the stop may take is the terminal's to bound.
/// </summary>
/// <remarks>
/// <para>
/// A terminal's interrupts reach it through <see cref="FlowTerminal"/>; a front end calls <see cref="Pressed"/> from its
/// own key handling. Tests drive it the same way, so the counting is asserted without a terminal.
/// </para>
/// <para>
/// The count is a session's, and the one count of it - the terminal bounds the hard stop by it too: interrupts pressed
/// while no drive of the session is bound - before the first, or between one that ended and the one an answer taken as
/// it ended begins - count for the session and are applied to its engine once bound, each once; only the session's end
/// clears it, so a front end that hands one to one command after another never stops the next session at its first
/// press.
/// </para>
/// </remarks>
/// <param name="stopAsked">Told once the count reaches the second interrupt, the hard stop: how a terminal begins bounding it.</param>
public sealed class FlowInterrupts(Action? stopAsked = null)
{
    private readonly Lock _gate = new();
    private readonly Action? _stopAsked = stopAsked;
    private int _pressed;
    private int _applied;
    private FlowCoordinator? _coordinator;

    /// <summary>How many interrupts the session bound, or about to be, has been given.</summary>
    public int Presses
    {
        get
        {
            lock (_gate)
            {
                return _pressed;
            }
        }
    }

    /// <summary>One interrupt: the first asks the engine for a safe pause; a second and every one after is the hard stop - the engine asked to stop where it stands.</summary>
    /// <returns>How many interrupts the session has been given, this one counted.</returns>
    public int Pressed()
    {
        List<Action> apply;
        int count;

        lock (_gate)
        {
            count = ++_pressed;
            apply = UnappliedLocked();
        }

        foreach (var meaning in apply)
        {
            meaning();
        }

        if (count == 2)
        {
            _stopAsked?.Invoke();
        }

        return count;
    }

    /// <summary>
    /// Binds the interrupts to the drive of the session they count for; called by the service that drives it. Interrupts
    /// pressed while none was bound are this session's, and are applied now: the first asks its pause, a second its stop.
    /// </summary>
    internal void BoundTo(FlowCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);

        List<Action> apply;

        lock (_gate)
        {
            _coordinator = coordinator;
            apply = UnappliedLocked();
        }

        foreach (var meaning in apply)
        {
            meaning();
        }
    }

    /// <summary>Unbinds a drive of the session that ended; the count stays the session's, for another drive of it may follow.</summary>
    internal void Unbound()
    {
        lock (_gate)
        {
            _coordinator = null;
        }
    }

    /// <summary>The session ended: unbound, and its count cleared, so the next session's interrupts are counted afresh.</summary>
    internal void SessionEnded()
    {
        lock (_gate)
        {
            _coordinator = null;
            _pressed = 0;
            _applied = 0;
        }
    }

    /// <summary>What each interrupt not yet applied asks of the bound session's engine, each counted applied; nothing where none is bound. Under the lock the caller holds.</summary>
    private List<Action> UnappliedLocked()
    {
        var apply = new List<Action>();

        if (_coordinator is { } coordinator)
        {
            while (_applied < _pressed)
            {
                _applied++;
                apply.Add(_applied == 1 ? () => coordinator.RequestPause("interrupt") : coordinator.RequestStop);
            }
        }

        return apply;
    }
}

/// <summary>
/// The terminal of a run's session: how the interrupts a terminal sends reach the run. Ctrl+C - and off Windows SIGTERM
/// and a hang-up - are each taken as one interrupt of the session (<see cref="FlowInterrupts"/>) and never end the
/// process: the first asks the safe pause, which starts no clock, and the second the hard stop, which is bounded - where
/// the session has not ended <see cref="PointOfNoReturn.HardStopBound"/> after it, the engine's own drain and the margin
/// for writing its end both passed, the command ends where it stands, and what is left is what a crash leaves.
/// </summary>
/// <remarks>
/// <para>
/// The command line's own token is never the session's: an interrupt reaches the session as one counted here, and a
/// cancellation of the command's token - which the command line's own handling of termination would make at the first
/// Ctrl+C, and which is turned off for the run verbs - is not one. A session handed that token would take the first
/// interrupt for the hard stop.
/// </para>
/// <para>
/// The session's count is the one the terminal reads: an interrupt is counted once, by the session's interrupts, and the
/// drain begins at the second of them, whoever pressed the first - a terminal's signal or a front end's own key.
/// </para>
/// <para>
/// Windows ends a process whose console is closed whatever it answers, so the hang-up and SIGTERM are not taken there:
/// what a closed console leaves is what a crash leaves, and the next session reclaims the run.
/// </para>
/// </remarks>
public sealed class FlowTerminal
{
    private readonly IHarnessOutput _output;
    private readonly string _command;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _drain;
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _gate = new();
    private ITimer? _drainTimer;
    private bool _ended;

    /// <summary>The terminal of one session of <paramref name="command"/>.</summary>
    /// <param name="output">Says what each interrupt asked, as a line of <paramref name="command"/>.</param>
    /// <param name="command">The run verb the session is of.</param>
    /// <param name="clock">How the drain after the second interrupt is timed.</param>
    /// <param name="drain">How long the session may take to end after the second interrupt; <see cref="PointOfNoReturn.HardStopBound"/> where not given.</param>
    public FlowTerminal(IHarnessOutput output, string command, TimeProvider? clock = null, TimeSpan? drain = null)
    {
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _command = command ?? throw new ArgumentNullException(nameof(command));
        _clock = clock ?? TimeProvider.System;
        _drain = drain ?? PointOfNoReturn.HardStopBound;
        Interrupts = new FlowInterrupts(BeginDrain);
    }

    /// <summary>The session's interrupts: the one count of them, which the terminal bounds the hard stop by.</summary>
    public FlowInterrupts Interrupts { get; }

    /// <summary>
    /// Runs <paramref name="session"/> with the terminal's interrupts counted for it: handed the interrupts and no token
    /// of the command line's. Its outcome where it ends; where it has not ended within the drain after the second
    /// interrupt, the hard stop's own outcome (130), the session left where it stands.
    /// </summary>
    /// <param name="session">The session: run, resume or answer, given the interrupts and the token it is driven by.</param>
    /// <param name="commandToken">The command line's own token, which is deliberately not handed on: see the remarks.</param>
    /// <param name="bindSignals">Whether the process's own signals are taken as interrupts; false for a front end with keys of its own, and for tests.</param>
    public async Task<CommandOutcome> RunAsync(Func<FlowInterrupts, CancellationToken, Task<CommandOutcome>> session, CancellationToken commandToken, bool bindSignals = true)
    {
        ArgumentNullException.ThrowIfNull(session);

        // Not the session's: the run verbs count their own interrupts, and a cancellation of the command line's token is
        // not one of them.
        _ = commandToken;

        using var signals = bindSignals ? Signals() : null;

        try
        {
            var running = session(Interrupts, CancellationToken.None);
            var ended = await Task.WhenAny(running, _drained.Task).ConfigureAwait(false);

            if (ended == running)
            {
                return await running.ConfigureAwait(false);
            }

            return CommandOutcome.Failed(
                HarnessExit.Cancelled,
                $"the run did not stop within {Seconds(_drain)} s of the second interrupt, and the command ends where it stands: what is left is what a crash leaves, "
                + $"and {FlowService.ResumeCommand} reclaims the run and goes on from its records");
        }
        finally
        {
            lock (_gate)
            {
                _ended = true;
                _drainTimer?.Dispose();
            }
        }
    }

    /// <summary>One interrupt of the terminal: counted for the session, and said by its place in the session's count.</summary>
    internal void Interrupted()
    {
        var count = Interrupts.Pressed();

        if (count == 1)
        {
            _output.Warn(_command, "interrupted: a safe pause was asked - the agents hand off and the machine steps finish, then the run is paused; a second interrupt stops it where it stands");
        }
        else if (count == 2)
        {
            _output.Warn(_command, $"interrupted again: the run is stopped where it stands, and the command ends within {Seconds(_drain)} s whatever is left");
        }
    }

    /// <summary>The session's second interrupt, whoever pressed it: the drain of the hard stop begins.</summary>
    private void BeginDrain()
    {
        lock (_gate)
        {
            if (!_ended)
            {
                _drainTimer ??= _clock.CreateTimer(_ => _drained.TrySetResult(), null, _drain, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Takes the process's interrupts for this session until disposed: Ctrl+C everywhere, and off Windows SIGTERM and a hang-up.</summary>
    private Registrations Signals()
    {
        var registrations = new List<PosixSignalRegistration> { PosixSignalRegistration.Create(PosixSignal.SIGINT, Taken) };

        if (!OperatingSystem.IsWindows())
        {
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Taken));
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, Taken));
        }

        return new Registrations(registrations);
    }

    /// <summary>A signal taken as an interrupt: the process is not ended by it.</summary>
    private void Taken(PosixSignalContext signal)
    {
        signal.Cancel = true;
        Interrupted();
    }

    private static string Seconds(TimeSpan span) => span.TotalSeconds.ToString("0", CultureInfo.InvariantCulture);

    /// <summary>The signal registrations of one session, let go together.</summary>
    private sealed class Registrations(IReadOnlyList<PosixSignalRegistration> registrations) : IDisposable
    {
        public void Dispose()
        {
            foreach (var registration in registrations)
            {
                registration.Dispose();
            }
        }
    }
}
