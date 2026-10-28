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

    /// <summary>
    /// Never began: the program is missing or does not start as this tool starts it, the key is missing, the configuration
    /// is wrong, or this tool itself runs elevated.
    /// </summary>
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

    /// <summary>
    /// This tool itself runs elevated, as root or an administrator: no agent starts while it does, for every rule of the
    /// agents' policy assumes a user's powers.
    /// </summary>
    Elevated,
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

/// <summary>
/// An account at its limit: what a request is billed to - a provider's key, or a client's login - has no more to give
/// for now, as the provider said it.
/// </summary>
/// <param name="BalanceSpent">Whether what is spent is its balance or its included usage, which time alone does not give back.</param>
/// <param name="Lifts">When the limit lifts, where the provider says; null where it does not.</param>
/// <param name="Key">The key whose limit it is, by its name and never its value; null for a client's login.</param>
/// <param name="Model">
/// The model whose limit on that key it is - a rate limit is a model's on a key - where the limit is no other model's;
/// null where it is the key's whole, every model it signs: its balance, its plan.
/// </param>
public sealed record AgentAccountLimit(bool BalanceSpent, DateTimeOffset? Lifts = null, string? Key = null, string? Model = null);

/// <summary>
/// What a declined request leaves the step's next model, where the provider gives it: a token the same request sent
/// again on that model carries, so what the declined one had cached is billed as read rather than written again.
/// </summary>
/// <param name="Token">The provider's token, opaque, good for minutes.</param>
/// <param name="PrefillClaim">Whether the request sent again may carry what the declined answer wrote, for the next model to go on from.</param>
public sealed record AgentFallbackCredit(string Token, bool PrefillClaim);

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
/// <param name="Notes">
/// What the agent left for whoever goes on, masked: the notes of an answer's envelope, where the run answered in one; null
/// otherwise - a run that did not reach its answer leaves none, and its handoff stands in their place.
/// </param>
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
    /// <summary>What it assumed where a person could have told it, masked, as its answer said it; empty where it assumed nothing.</summary>
    public IReadOnlyList<AgentAssumption> Assumptions { get; init; } = [];

    /// <summary>What it asks the person, where the step may ask and it did; empty where it asked nothing.</summary>
    public IReadOnlyList<AgentQuestion> Questions { get; init; } = [];

    /// <summary>Each model that served it, and from which turn; empty where none did.</summary>
    public IReadOnlyList<AgentServedModel> Served { get; init; } = [];

    /// <summary>The files it wrote and read; always filled, empty where it touched none.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    /// <summary>
    /// The keys the provider refused while it ran, by their names and never their values, in the order they were met:
    /// each passed over for the rest of the run, which the engine records. Empty where none was refused.
    /// </summary>
    public IReadOnlyList<string> KeysRefused { get; init; } = [];

    /// <summary>
    /// A session file a launch left all the same, told to keep none, which the client removed - its own launch's, named
    /// by the session id the client chose, and nothing else; empty where none was left.
    /// </summary>
    public IReadOnlyList<string> SessionFilesRemoved { get; init; } = [];

    /// <summary>
    /// The account's limit, where the run ended on one (<see cref="AgentRunStatus.RateLimited"/>): whether its balance or
    /// included usage is spent, and when it lifts where the provider says. Null for every other ending.
    /// </summary>
    public AgentAccountLimit? Limit { get; init; }

    /// <summary>What a declined request was of, where the run ended <see cref="AgentRunStatus.Refused"/> and the provider names it.</summary>
    public string? DeclinedFor { get; init; }

    /// <summary>
    /// The credit a declined request left for the step's next model, where the run ended <see cref="AgentRunStatus.Refused"/>
    /// and the provider gave one: what the engine's fallback sends the same request again with.
    /// </summary>
    public AgentFallbackCredit? Credit { get; init; }

    /// <summary>
    /// What the container of a child the client started could not reach when that child ended, and why - one entry for
    /// each child whose container missed some of what it held, for what that child started may still run. Empty where
    /// every child was ended whole.
    /// </summary>
    public IReadOnlyList<string> Uncontained { get; init; } = [];

    /// <summary>
    /// The provider's service failed past the client's own tries - unreachable, down, or refusing the account - which is
    /// the provider's state and no model's answer: the run is neither retried on its model nor moved to another, for
    /// every model of the step is reached through the same service. False for every other failure, and written only
    /// where it is true.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServiceFailed { get; init; }

    /// <summary>
    /// What is wrong with this result, as its own fields say it; null when nothing is. Each field that only one ending
    /// carries is held to it - why it never began to <see cref="AgentRunStatus.NotStarted"/>, an account's limit to
    /// <see cref="AgentRunStatus.RateLimited"/>, what a declined request was of to <see cref="AgentRunStatus.Refused"/>, an
    /// answer read against its schema to <see cref="AgentRunStatus.Completed"/>, a handoff to
    /// <see cref="AgentRunStatus.HandedOff"/>, questions to <see cref="AgentRunStatus.Asked"/> - and each ending that
    /// carries one says it, and notes and assumptions to an answer that read - <see cref="AgentRunStatus.Completed"/> or
    /// <see cref="AgentRunStatus.Asked"/>; a run that completed says no error, no count is below naught, every list is
    /// written, none holds a null, and each assumption and question holds together by its record's own rule
    /// (<see cref="AgentAssumption.Problems"/>, <see cref="AgentQuestion.Problems"/>).
    /// </summary>
    public string? Problem()
    {
        var lists = new (string Name, string Item, IEnumerable<object?>? List)[]
        {
            ("assumptions", "an assumption", Assumptions), ("questions", "a question", Questions), ("served", "a model that served it", Served),
            ("files", "a file", Files), ("keysRefused", "a key refused", KeysRefused), ("sessionFilesRemoved", "a session file removed", SessionFilesRemoved),
            ("uncontained", "an uncontained child", Uncontained),
        };

        var counts = new (string Name, int Count)[]
        {
            ("turns", Turns), ("toolCalls", ToolCalls), ("denials", Denials), ("filesWritten", FilesWritten),
            ("webRequests", WebRequests), ("compactions", Compactions), ("retries", Retries),
        };

        return !Enum.IsDefined(Status) ? $"its status {(int)Status} is no ending a run has"
            : lists.FirstOrDefault(list => list.List is null) is { Name: { } unwritten } ? $"its {unwritten} is null, and that list is always written"
            : lists.FirstOrDefault(list => list.List!.Any(item => item is null)) is { Item: { } nulled } ? $"it names {nulled} as null"
            : counts.FirstOrDefault(count => count.Count < 0) is { Name: { } below } named ? $"its {below} is {named.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)}, and a count is never below naught"
            : Duration < TimeSpan.Zero ? $"its duration is {Duration}, and a run never ran for less than naught"
            : Status == AgentRunStatus.NotStarted && NotStartedBecause is null ? "it never began, and says not why"
            : Status != AgentRunStatus.NotStarted && NotStartedBecause is not null ? $"it ended {Status}, and says why it never began"
            : NotStartedBecause is { } because && !Enum.IsDefined(because) ? $"why it never began, {(int)because}, is no reason a run never begins"
            : Status == AgentRunStatus.RateLimited && Limit is null ? "it ended RateLimited, and names no account's limit"
            : Status != AgentRunStatus.RateLimited && Limit is not null ? $"it ended {Status}, and names an account's limit, which only a run that ended RateLimited does"
            : Status != AgentRunStatus.Refused && DeclinedFor is not null ? $"it ended {Status}, and names what a declined request was of, which only a run that ended Refused does"
            : Status != AgentRunStatus.Refused && Credit is not null ? $"it ended {Status}, and holds a declined request's credit, which only a run that ended Refused does"
            : Status != AgentRunStatus.Completed && StructuredOutput is not null ? $"it ended {Status}, and holds an answer read against its schema, which only a run that completed does"
            : Status == AgentRunStatus.HandedOff && Handoff is null ? "it ended HandedOff, and says not whether its agent answered the handoff"
            : Status != AgentRunStatus.HandedOff && Handoff is not null ? $"it ended {Status}, and holds a handoff, which only a run that handed off does"
            : Handoff is { Answer: not null, WhyNone: not null } ? "its handoff holds both an answer and why there is none, and it holds one of them"
            : Handoff is { Answer: null, WhyNone: null } ? "its handoff holds neither an answer nor why there is none, and it holds one of them"
            : Status == AgentRunStatus.Asked && Questions.Count == 0 ? "it ended Asked, and asks nothing"
            : Status != AgentRunStatus.Asked && Questions.Count > 0 ? $"it ended {Status}, and asks the person, which only a run that ended Asked does"
            : Status is not (AgentRunStatus.Completed or AgentRunStatus.Asked) && Notes is not null ? $"it ended {Status}, and holds notes, which only a run whose answer read does"
            : Status is not (AgentRunStatus.Completed or AgentRunStatus.Asked) && Assumptions.Count > 0 ? $"it ended {Status}, and holds assumptions, which only a run whose answer read does"
            : AgentAssumption.ProblemIn(Assumptions) is { } assumed ? $"its {assumed}"
            : AgentQuestion.ProblemIn(Questions) is { } asked ? $"its {asked}"
            : Status == AgentRunStatus.Completed && Error is not null ? "it completed, and says why it failed"
            : Status is not (AgentRunStatus.Failed or AgentRunStatus.Stalled) && ServiceFailed ? $"it ended {Status}, and says the provider's service failed, which only a run that ended Failed or Stalled does"
            : Served.FirstOrDefault(served => served.FromTurn < 1) is { } early ? $"it names '{early.Model}' as serving from turn {early.FromTurn.ToString(System.Globalization.CultureInfo.InvariantCulture)}, and turns are counted from one"
            : null;
    }

    /// <summary>
    /// This run as one that failed for <paramref name="error"/>: what only another ending carries - why it never began, an
    /// account's limit, what a declined request was of and its credit, an answer read against its schema, its notes, what it
    /// assumed, its questions, its handoff -
    /// is not a failed run's, and goes; what it did and what it used stay.
    /// </summary>
    /// <param name="error">Why it failed, masked.</param>
    public AgentRunResult FailedFor(string error)
        => this with
        {
            Status = AgentRunStatus.Failed,
            NotStartedBecause = null,
            StructuredOutput = null,
            Notes = null,
            Handoff = null,
            Assumptions = [],
            Questions = [],
            Limit = null,
            DeclinedFor = null,
            Credit = null,
            Error = error,
        };

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
                NotStartedBecause switch
                {
                    AgentNotStartedBecause.ProgramMissing => HarnessExit.ToolMissing,
                    AgentNotStartedBecause.KeyMissing or AgentNotStartedBecause.Elevated => HarnessExit.Refused,
                    _ => HarnessExit.ConfigInvalid,
                },
                line),
            AgentRunStatus.Stalled or AgentRunStatus.TimedOut or AgentRunStatus.RateLimited => CommandOutcome.Failed(HarnessExit.Incomplete, line),
            _ => CommandOutcome.Failed(HarnessExit.CommandFailed, line),
        };
    }
}
