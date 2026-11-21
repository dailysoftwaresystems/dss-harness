using System.Runtime.InteropServices;

namespace DssHarness.Core.Execution;

/// <summary>
/// The process's own interrupts, taken for one command until let go: Ctrl+C everywhere, and off Windows SIGTERM and a
/// hang-up - each cancelled, so it never ends the process, and handed to the command as one interrupt, a hang-up said
/// apart. The one registration of them: for every command that counts its own interrupts
/// (<see cref="PointOfNoReturn.CountsItsOwnInterrupts"/>), a run's session and a side-task alike, by <see cref="Take"/>;
/// and for every other, by <see cref="StopAtTheFirst"/>, the first cancelling it and the second the system's hard stop.
/// </summary>
/// <remarks>
/// Windows ends a process whose console is closed whatever it answers, so the hang-up and SIGTERM are not taken there:
/// what a closed console leaves is what a crash leaves.
/// </remarks>
public sealed class TerminalSignals : IDisposable
{
    private readonly IReadOnlyList<PosixSignalRegistration> _registrations;

    private TerminalSignals(IReadOnlyList<PosixSignalRegistration> registrations) => _registrations = registrations;

    /// <summary>Takes the process's interrupts until the answer is disposed.</summary>
    /// <param name="interrupted">Told of each Ctrl+C, and off Windows of each SIGTERM.</param>
    /// <param name="hungUp">Told of each hang-up, off Windows.</param>
    public static TerminalSignals Take(Action interrupted, Action hungUp)
    {
        ArgumentNullException.ThrowIfNull(interrupted);
        ArgumentNullException.ThrowIfNull(hungUp);

        var registrations = new List<PosixSignalRegistration> { PosixSignalRegistration.Create(PosixSignal.SIGINT, Taken(interrupted)) };

        if (!OperatingSystem.IsWindows())
        {
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Taken(interrupted)));
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGHUP, Taken(hungUp)));
        }

        return new TerminalSignals(registrations);
    }

    /// <summary>
    /// Takes the process's interrupts for a command that does not count its own, until the answer is disposed: the first -
    /// Ctrl+C, and off Windows SIGTERM - cancels <paramref name="stop"/>, after telling <paramref name="stopping"/>, and the
    /// process goes on until the command's work has stopped, however long that takes; every one after it is the hard stop
    /// the system gives any process, which ends this one where it stands. A hang-up is left to the system as well: the
    /// terminal a line would be said to is gone.
    /// </summary>
    /// <param name="stop">Cancelled at the first interrupt: the command's own token.</param>
    /// <param name="stopping">Told of the first interrupt, before <paramref name="stop"/> is cancelled.</param>
    public static TerminalSignals StopAtTheFirst(CancellationTokenSource stop, Action stopping)
    {
        ArgumentNullException.ThrowIfNull(stop);
        ArgumentNullException.ThrowIfNull(stopping);

        var interrupts = 0;

        void Interrupted(PosixSignalContext signal)
        {
            if (Interlocked.Increment(ref interrupts) > 1)
            {
                // Left to the system: the process ends as an interrupt ends any process.
                return;
            }

            signal.Cancel = true;
            stopping();

            try
            {
                stop.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The command ended as the interrupt came: there is nothing left to stop.
            }
        }

        var registrations = new List<PosixSignalRegistration> { PosixSignalRegistration.Create(PosixSignal.SIGINT, Interrupted) };

        if (!OperatingSystem.IsWindows())
        {
            registrations.Add(PosixSignalRegistration.Create(PosixSignal.SIGTERM, Interrupted));
        }

        return new TerminalSignals(registrations);
    }

    /// <summary>Lets the process's interrupts go: from now on the process takes them as its own default.</summary>
    public void Dispose()
    {
        foreach (var registration in _registrations)
        {
            registration.Dispose();
        }
    }

    /// <summary>A signal taken as <paramref name="told"/>: the process is not ended by it.</summary>
    private static Action<PosixSignalContext> Taken(Action told)
        => signal =>
        {
            signal.Cancel = true;
            told();
        };
}
