namespace DssHarness.Core.Application;

/// <summary>
/// What an operation writes at its every end, once its work has stopped (<see cref="CommandSession"/>) - its output logs,
/// where they were asked for (<see cref="Orchestration.Flow.FlowOutputLogs"/>): a front end that stops the work - a
/// terminal's second interrupt - says it is still to come, and where, so the wait for it is never taken for the stop's.
/// </summary>
/// <remarks>Registered once per operation, as everything of an operation is: what it holds is that operation's alone.</remarks>
public interface IEndWrites
{
    /// <summary>The file the operation writes at its end, once it was held to be one that can be written; null where it writes none.</summary>
    string? AtTheEnd { get; }
}
