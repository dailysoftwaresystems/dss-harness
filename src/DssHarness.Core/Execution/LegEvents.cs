using DssHarness.Core.Hosts;
using DssHarness.Core.Output;

namespace DssHarness.Core.Execution;

/// <summary>
/// What is about to run, and where, before any of it starts - unless the machine that dispatched it here says so
/// itself.
/// </summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Message">What is starting, across which machines.</param>
public sealed record LegsAnnounced(string Command, string Message) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms() => [new(LineKind.Info, Command, Message)];
}

/// <summary>A leg is starting, and where - unless the machine that dispatched it here says so itself.</summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Leg">The leg, as the configuration names it.</param>
/// <param name="Host">The host that runs it, as the placement names it.</param>
/// <param name="WorksIn">
/// Where this machine's leg works, as its starting line says it (<see cref="LegPlan.WorksIn"/>), or empty for a leg
/// another machine runs.
/// </param>
public sealed record LegStarting(string Command, string Leg, HostId Host, string WorksIn = "") : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
        => [new(LineKind.Info, Command, $"{Leg}: starting on {Host}{WorksIn}")];
}

/// <summary>
/// A leg moved on: started, synced, built, testing, finished. One line, so the progress of a run with eight legs still
/// fits on a screen.
/// </summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Leg">The leg.</param>
/// <param name="Message">What it is now doing.</param>
public sealed record LegProgressed(string Command, string Leg, string Message) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms() => [new(LineKind.Info, Command, $"{Leg}: {Message}")];
}

/// <summary>
/// A leg's verdict, said as its ledger records it - unless the machine that dispatched it here says so itself, as it
/// records it from this one's ledger.
/// </summary>
/// <param name="Command">The command the run reports under.</param>
/// <param name="Entry">The leg's line.</param>
public sealed record LegFinished(string Command, LegEntry Entry) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
    {
        var said = LedgerReport.Marked(
            Entry.Detail,
            Entry.Tree,
            [],
            Entry.Compilers,
            Entry.DeveloperEnvironment,
            Entry.SetUps,
            Entry.Admission,
            testCountNote: null);

        yield return new(LineKind.Info, Command, $"{Entry.Leg}: {Verdicts.Display(Entry.Verdict)}{(said.Length > 0 ? $" ({said})" : string.Empty)}");
    }
}

/// <summary>
/// A line a leg's phase printed, echoed to the terminal only when detail was asked for and tagged with the leg and
/// phase that wrote it: legs run at the same time, so without the tag the terminal carries several children's output
/// woven together with nothing to tell one from another. The log files keep the line as the child wrote it.
/// </summary>
/// <param name="Leg">The leg that is running the phase.</param>
/// <param name="Phase">The phase, as the run names it.</param>
/// <param name="Line">The line as the child wrote it.</param>
/// <param name="IsError">Whether the line is the child's error output.</param>
public sealed record PhaseLine(string Leg, string Phase, string Line, bool IsError) : RunEvent
{
    /// <inheritdoc/>
    public override IEnumerable<TextForm> TextForms()
        => [new(IsError ? LineKind.RawError : LineKind.Raw, null, $"{Leg}/{Phase}: {Line}", VerboseOnly: true)];
}
