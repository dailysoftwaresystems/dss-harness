using System.Text.Json;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Orchestration;

/// <summary>One line of an orchestrator's log: a command the tool ran on the orchestrator, or on one of its agents.</summary>
/// <param name="At">When it finished.</param>
/// <param name="Command">The command.</param>
/// <param name="Outcome">How it ended: ok, refused, failed, incomplete or interrupted.</param>
/// <param name="ExitCode">Its exit code.</param>
/// <param name="Message">What it said.</param>
/// <param name="Details">The lines it said beneath that, where it said any.</param>
public sealed record OrchestrationEvent(
    DateTimeOffset At,
    string Command,
    string Outcome,
    int ExitCode,
    string Message,
    IReadOnlyList<string>? Details);

/// <summary>
/// Appends to an orchestrator's logs: <c>logs/&lt;name&gt;.jsonl</c>, one JSON object to a line, for the orchestrator
/// and for each of its agents, so a session taking over - on another account, or after the last one ended - reads what
/// was done without asking the one that did it.
/// </summary>
/// <param name="fileSystem">Reads and writes the logs.</param>
/// <param name="clock">When each line is written.</param>
public sealed class OrchestrationLog(IFileSystem fileSystem, TimeProvider clock)
{
    /// <summary>How long one append waits for another process's.</summary>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly TimeProvider _clock = clock;

    /// <summary>Appends what <paramref name="command"/> did to <paramref name="subject"/>, and returns the log's path.</summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="subject">The orchestrator's name, or an agent's.</param>
    /// <param name="command">The command.</param>
    /// <param name="outcome">What it did.</param>
    /// <remarks>
    /// The whole file is written again with the line added, under the one lock every append to it takes, so a line is
    /// never half written and two appends never interleave.
    /// </remarks>
    public string Append(OrchestratorLayout orchestrator, string subject, string command, CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(outcome);

        var path = orchestrator.LogFile(subject);
        var line = JsonSerializer.Serialize(
            new OrchestrationEvent(_clock.GetUtcNow(), command, OutcomeOf(outcome.ExitCode), outcome.ExitCode, outcome.Message, outcome.Details is { Count: > 0 } details ? details : null),
            JsonStateFile.LineOptions);

        MachineWideFile.Update(path, Window, () =>
        {
            var before = _fileSystem.FileExists(path) ? _fileSystem.ReadAllText(path) : string.Empty;
            _fileSystem.WriteAllTextAtomic(path, before + line + "\n");
            return true;
        });

        return path;
    }

    /// <summary>
    /// Appends what <paramref name="command"/> did to <paramref name="subject"/>'s log, and returns <paramref name="outcome"/>
    /// naming the log - or saying that it could not be written: what a command did is never replaced by a log that could
    /// not take a line.
    /// </summary>
    /// <param name="orchestrator">The orchestrator.</param>
    /// <param name="subject">The orchestrator's name, or an agent's.</param>
    /// <param name="command">The command.</param>
    /// <param name="outcome">What it did.</param>
    public CommandOutcome Record(OrchestratorLayout orchestrator, string subject, string command, CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(outcome);

        try
        {
            return outcome with { Details = [.. outcome.Details ?? [], $"log {Append(orchestrator, subject, command, outcome)}"] };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return outcome with { Details = [.. outcome.Details ?? [], $"its log '{orchestrator.LogFile(subject)}' could not take this line: {ex.Message.TrimEnd('.')}"] };
        }
    }

    /// <summary>Every line of the log at <paramref name="path"/>, in order; a line that does not read is refused, naming its number.</summary>
    /// <param name="path">A log.</param>
    /// <exception cref="HarnessException">A line does not read as one (<see cref="HarnessExit.Refused"/>).</exception>
    public IReadOnlyList<OrchestrationEvent> Read(string path)
    {
        if (!_fileSystem.FileExists(path))
        {
            return [];
        }

        var events = new List<OrchestrationEvent>();
        var number = 0;

        foreach (var line in _fileSystem.ReadAllText(path).Split('\n'))
        {
            number++;

            if (line.Length == 0)
            {
                continue;
            }

            try
            {
                events.Add(JsonSerializer.Deserialize<OrchestrationEvent>(line, JsonStateFile.LineOptions)
                    ?? throw new JsonException("it holds null"));
            }
            catch (JsonException ex)
            {
                throw new HarnessException(HarnessExit.Refused, $"'{path}' line {number} does not read as a log's line: {ex.Message.TrimEnd('.')}.");
            }
        }

        return events;
    }

    private static string OutcomeOf(int exitCode) => exitCode switch
    {
        HarnessExit.Success => "ok",
        HarnessExit.Refused => "refused",
        HarnessExit.Incomplete => "incomplete",
        HarnessExit.Cancelled => "interrupted",
        _ => "failed",
    };
}
