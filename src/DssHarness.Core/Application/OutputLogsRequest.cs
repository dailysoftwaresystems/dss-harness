namespace DssHarness.Core.Application;

/// <summary>
/// What a front end asks of an operation's output logs (<see cref="SessionOptions.OutputLogs"/>), which the session
/// writes at its every end (<see cref="Orchestration.Flow.FlowOutputLogs"/>).
/// </summary>
/// <param name="Path">Where the zip is written, as the front end was given it: absolute, or relative to the directory the process stands in.</param>
/// <param name="CommandLine">The command line as it was typed, the tool's name first: what the zip's manifest says was run.</param>
public sealed record OutputLogsRequest(string Path, IReadOnlyList<string> CommandLine);
