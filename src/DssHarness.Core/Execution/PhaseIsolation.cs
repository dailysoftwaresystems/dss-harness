using DssHarness.Core.Processes;

namespace DssHarness.Core.Execution;

/// <summary>
/// The operating system's isolation every phase a leg's work starts runs inside, for as long as that work runs: entered by
/// the run of a leg of a run's machine step on this machine (<see cref="Runs.LegRunRequest.Isolation"/>), read by
/// <see cref="PhaseRunner"/> for each phase that names none of its own and is not left running. The work of one leg holds
/// its own: legs run at once, each in a flow of its own, and what one enters never reaches another's.
/// </summary>
/// <remarks>
/// Held for the work alone - the configure, build and test commands, a runner's steps, a sweep's arms - and never for a
/// set-up's lines, which the configuration gives and which bring up what outlives the leg, nor for the tool's own git and
/// transports, which start no phase.
/// </remarks>
public static class PhaseIsolation
{
    private static readonly AsyncLocal<ProcessIsolation?> Ambient = new();

    /// <summary>The isolation the phases started here run inside; null where none is held.</summary>
    public static ProcessIsolation? Current => Ambient.Value;

    /// <summary>Holds <paramref name="isolation"/> for every phase started in this flow until the answer is disposed.</summary>
    /// <param name="isolation">The isolation.</param>
    /// <returns>What puts back the isolation held before.</returns>
    public static IDisposable Enter(ProcessIsolation isolation)
    {
        ArgumentNullException.ThrowIfNull(isolation);

        var before = Ambient.Value;
        Ambient.Value = isolation;

        return new Left(before);
    }

    private sealed class Left(ProcessIsolation? before) : IDisposable
    {
        public void Dispose() => Ambient.Value = before;
    }
}
