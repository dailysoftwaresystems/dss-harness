using System.Text.Json.Serialization;
using DssHarness.Core.Results;

namespace DssHarness.Core.Agents;

/// <summary>How a step's run ended.</summary>
public enum AgentRunStatus
{
    /// <summary>The agent finished, and its output reads against its schema where one is named.</summary>
    Completed,

    /// <summary>The agent answered with questions for the person and no output, on a step that may ask.</summary>
    Asked,

    /// <summary>A hard stop.</summary>
    Cancelled,

    /// <summary>Ended by a safe pause, with the agent's handoff or without.</summary>
    HandedOff,

    /// <summary>Never began: the program is missing or does not start as this tool starts it, the key is missing, or the configuration is wrong.</summary>
    NotStarted,

    /// <summary>No progress for longer than the stall bound.</summary>
    Stalled,

    /// <summary>Longer than the duration bound.</summary>
    TimedOut,

    /// <summary>The account is at its limit, and no fallback was left to take over.</summary>
    RateLimited,

    /// <summary>The turn bound was reached.</summary>
    MaxTurns,

    /// <summary>The budget was spent.</summary>
    BudgetSpent,

    /// <summary>The model's context could hold no more.</summary>
    ContextExhausted,

    /// <summary>The answer was cut off by the provider.</summary>
    Truncated,

    /// <summary>The run failed.</summary>
    Failed,

    /// <summary>The output did not read against the schema, after its repairs.</summary>
    InvalidOutput,

    /// <summary>The model declined, and no fallback was left to take over.</summary>
    Refused,

    /// <summary>A message was written by a model the engine did not ask for.</summary>
    WrongModel,
}

/// <summary>Why a run that never began did not.</summary>
public enum AgentNotStartedBecause
{
    /// <summary>The program is missing, or does not start as this tool starts it.</summary>
    ProgramMissing,

    /// <summary>The key is missing.</summary>
    KeyMissing,

    /// <summary>The configuration is wrong.</summary>
    ConfigurationWrong,
}

/// <summary>
/// What one model that served a run did: which model, from which turn. One entry unless a fallback was taken, and then
/// one for each model that served, in order.
/// </summary>
/// <param name="Model">The model.</param>
/// <param name="FromTurn">The turn it began serving on, from one.</param>
public sealed record AgentServedModel(string Model, int FromTurn);

/// <summary>
/// What a run's usage cost, in United States dollars, with where each number is from, kept side by side as
/// <c>cost.reported</c> and <c>cost.counted</c>: what the client said the launch cost, where it says (Claude Code's
/// <c>total_cost_usd</c>), and what this tool counted at the configuration's prices - or, where a price was missing,
/// which one, for a cost no price was there for is unknown and never zero.
/// </summary>
/// <param name="Reported">What the client said the run cost; null where the client says nothing of it, as the two APIs do.</param>
/// <param name="Counted">What this tool counted at the configuration's prices; null where a price it needed was missing.</param>
/// <param name="MissingPrice">The price that was missing, where the cost could not be counted: the model and the key to fill; null where it was counted.</param>
public sealed record AgentCost(decimal? Reported = null, decimal? Counted = null, string? MissingPrice = null)
{
    /// <summary>
    /// The run's cost: the client's own number where it reports one, for it holds what the stream does not show as the
    /// agent's; the counted one where it does not; null where neither is known.
    /// </summary>
    [JsonIgnore]
    public decimal? Usd => Reported ?? Counted;
}

/// <summary>One model's part of what a run used.</summary>
/// <param name="Model">The model.</param>
/// <param name="Usage">What it used.</param>
public sealed record AgentModelUsage(string Model, AgentUsage Usage);

/// <summary>What a run used: in all, and for each model that served it.</summary>
/// <param name="Total">The whole of it.</param>
/// <param name="PerModel">Each model's part, in the order they served.</param>
/// <param name="Cost">What it cost, and where each number is from; null where nothing has said or counted it.</param>
public sealed record AgentUsageReport(AgentUsage Total, IReadOnlyList<AgentModelUsage> PerModel, AgentCost? Cost = null);

/// <summary>The agent's answer to the handoff turn, or why there is none.</summary>
/// <param name="Answer">The answer, masked; null where the agent gave none.</param>
/// <param name="WhyNone">Why there is none; null where it answered.</param>
public sealed record AgentHandoff(string? Answer = null, string? WhyNone = null)
{
    /// <summary>There is an answer.</summary>
    public bool Answered => Answer is not null;
}

/// <summary>
/// How a step's run ended, and everything it knows beyond its text: what it wrote, what it used, what it asked, and
/// what it left for whoever goes on.
/// </summary>
/// <param name="Status">How it ended.</param>
/// <param name="NotStartedBecause">Why, where it never began; null otherwise.</param>
/// <param name="FinalText">The agent's answer as text.</param>
/// <param name="StructuredOutput">Its answer against the schema, where one was named and it read; null otherwise.</param>
/// <param name="Notes">What the agent left for whoever goes on, masked.</param>
/// <param name="Session">The session the run worked as, where the client names one.</param>
/// <param name="Model">The model the step asked for.</param>
/// <param name="Usage">What it used and what that cost.</param>
/// <param name="Turns">How many turns it took.</param>
/// <param name="ToolCalls">How many tool calls it made.</param>
/// <param name="Duration">How long it ran.</param>
/// <param name="Denials">How many permissions it was denied.</param>
/// <param name="FilesWritten">How many files it wrote.</param>
/// <param name="WebRequests">How many searches and fetches it asked the web for.</param>
/// <param name="Compactions">How many times its conversation was compacted.</param>
/// <param name="Retries">How many failures were retried.</param>
/// <param name="Handoff">Its answer to the handoff turn, masked, or why there is none; null where none was asked.</param>
/// <param name="Error">Why it failed, where it did; masked.</param>
public sealed record AgentRunResult(
    AgentRunStatus Status,
    AgentNotStartedBecause? NotStartedBecause = null,
    string? FinalText = null,
    string? StructuredOutput = null,
    string? Notes = null,
    string? Session = null,
    string? Model = null,
    AgentUsageReport? Usage = null,
    int Turns = 0,
    int ToolCalls = 0,
    TimeSpan Duration = default,
    int Denials = 0,
    int FilesWritten = 0,
    int WebRequests = 0,
    int Compactions = 0,
    int Retries = 0,
    AgentHandoff? Handoff = null,
    string? Error = null)
{
    /// <summary>What it assumed where a person could have told it; empty where it assumed nothing.</summary>
    public IReadOnlyList<string> Assumptions { get; init; } = [];

    /// <summary>What it asks the person, where the step may ask and it did; empty where it asked nothing.</summary>
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = [];

    /// <summary>Each model that served it, and from which turn; empty where none did.</summary>
    public IReadOnlyList<AgentServedModel> Served { get; init; } = [];

    /// <summary>The files it wrote and read; always filled, empty where it touched none.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// The exit code the run command answers for a step whose agent asked the person and produced no output: its own
    /// per-command code, which the run command's slice wires.
    /// </summary>
    public const int AskedExitCode = 1;

    /// <summary>
    /// The result as a command's outcome: the exit code its status table gives, over a line that says how the run ended.
    /// </summary>
    /// <remarks>
    /// The line is the result's own terse one - status, turns, the model that served - because the result is data a
    /// front end reads, not text a person was meant to; the run command's own lines come with it.
    /// </remarks>
    public CommandOutcome ToOutcome()
    {
        var on = Served is { Count: > 0 } served ? served[0].Model : Model ?? "no model";

        var line = $"{Status} after {Turns} turn{(Turns == 1 ? string.Empty : "s")} on {on}"
            + (Status == AgentRunStatus.Completed || Error is null ? string.Empty : $": {Error}");

        return Status switch
        {
            AgentRunStatus.Completed => CommandOutcome.Ok(line),
            AgentRunStatus.Asked => CommandOutcome.Failed(AskedExitCode, line),
            AgentRunStatus.Cancelled or AgentRunStatus.HandedOff => CommandOutcome.Failed(HarnessExit.Cancelled, line),
            AgentRunStatus.NotStarted => CommandOutcome.Failed(
                NotStartedBecause == AgentNotStartedBecause.ProgramMissing ? HarnessExit.ToolMissing
                    : NotStartedBecause == AgentNotStartedBecause.KeyMissing ? HarnessExit.Refused
                    : HarnessExit.ConfigInvalid,
                line),
            AgentRunStatus.Stalled or AgentRunStatus.TimedOut or AgentRunStatus.RateLimited => CommandOutcome.Failed(HarnessExit.Incomplete, line),
            _ => CommandOutcome.Failed(HarnessExit.CommandFailed, line),
        };
    }
}
