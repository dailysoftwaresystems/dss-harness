using System.Runtime.InteropServices;

namespace DssHarness.Core.Execution;

/// <summary>
/// The process's own interrupts, taken for one command until let go: Ctrl+C everywhere, and off Windows SIGTERM and a
/// hang-up - each cancelled, so it never ends the process, and handed to the command as one interrupt, a hang-up said
/// apart. The one registration of them, for every command that counts its own interrupts
/// (<see cref="PointOfNoReturn.CountsItsOwnInterrupts"/>): a run's session and a side-task alike.
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
