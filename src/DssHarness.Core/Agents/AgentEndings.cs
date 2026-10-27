using DssHarness.Core.Execution;

namespace DssHarness.Core.Agents;

/// <summary>
/// The endings every client's run comes to in the same words, whatever its wire: the one owner of each ending's sentence
/// and of what that ending carries beside it, so a step that never began, a model nobody asked for, an answer outside its
/// schema, a declined request and an account at its limit read alike on Claude Code and on the API clients.
/// </summary>
public static class AgentEndings
{
    /// <summary>Why a run whose step names no model never begins: every client tells the model by its id.</summary>
    public const string NoModel = "the step names no model, and its client is told the model by its id";

    /// <summary>Why a run given no policy never begins, and no runtime of its calls opens: no call runs undecided.</summary>
    public const string NoPolicy = "the step was given no policy to decide its calls by, and no call runs undecided";

    /// <summary>Why a run that signs in with a key never begins where its slot holds none.</summary>
    /// <param name="auth">How it signs in.</param>
    public static string NoKey(string auth) => $"the run signs in with auth: {auth}, and the slot holds no value for it";

    /// <summary>A run that never began, and why, masked.</summary>
    /// <param name="client">The client's key, as the configuration names it.</param>
    /// <param name="because">Why it never began.</param>
    /// <param name="why">What kept it from beginning.</param>
    /// <param name="model">The model the step asked for.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult NotStarted(string client, AgentNotStartedBecause because, string why, string? model, ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new(AgentRunStatus.NotStarted, because, Model: model, Error: mask.Redact($"client '{client}' was not started: {why}"));
    }

    /// <summary>A run ended <paramref name="status"/> for <paramref name="error"/>, masked.</summary>
    /// <param name="status">How it ended.</param>
    /// <param name="error">Why.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult Ended(AgentRunStatus status, string error, ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new(status, Error: mask.Redact(error));
    }

    /// <summary>A run ended by a message of a model the engine did not ask for.</summary>
    /// <param name="asked">The model the step asked for.</param>
    /// <param name="named">The model the message names.</param>
    /// <param name="where">Which message it was: <c>its turn 3</c>, <c>its launch</c>.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult WrongModel(string asked, string named, string where, ILineMask mask)
        => Ended(
            AgentRunStatus.WrongModel,
            $"the step asked for '{asked}' and {where} was written by '{named}'; a step's model is its configuration's and the engine's to choose, and neither chose this one",
            mask);

    /// <summary>A run whose answer did not read against its schema after its repairs.</summary>
    /// <param name="repairs">How many repairs were sent.</param>
    /// <param name="problems">What still does not read.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult InvalidOutput(int repairs, IReadOnlyList<string> problems, ILineMask mask)
        => Ended(AgentRunStatus.InvalidOutput, $"the answer did not read against its schema after {repairs} repairs: {string.Join("; ", problems)}", mask);

    /// <summary>A run the model declined, with what the request was of where the provider names it, and the credit it left where it gave one.</summary>
    /// <param name="category">What the declined request was of; null or empty where the provider names nothing.</param>
    /// <param name="said">What was said of it.</param>
    /// <param name="mask">The run's mask.</param>
    /// <param name="credit">The credit the declined request left the step's next model; null where the provider gave none.</param>
    public static AgentRunResult Declined(string? category, string said, ILineMask mask, AgentFallbackCredit? credit = null)
        => Ended(AgentRunStatus.Refused, $"the model declined the request{(category is { Length: > 0 } named ? $" ({named})" : string.Empty)}: {said}", mask) with
        {
            DeclinedFor = category is { Length: > 0 } of ? of : null,
            Credit = credit,
        };

    /// <summary>A run handed off: the agent's answer, or why there is none, masked.</summary>
    /// <param name="answer">The agent's handoff; null where it gave none.</param>
    /// <param name="whyNone">Why there is none, where there is none.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult HandedOff(string? answer, string? whyNone, ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(mask);

        return new(
            AgentRunStatus.HandedOff,
            Handoff: answer is null ? new AgentHandoff(WhyNone: whyNone is null ? null : mask.Redact(whyNone)) : new AgentHandoff(mask.Redact(answer)));
    }

    /// <summary>A run that ended on its account's limit, carrying the limit: whose it is - a model's on a key, a key's whole - where a key is named.</summary>
    /// <param name="who">Who reported it: <c>Claude Code</c>, <c>the provider</c>.</param>
    /// <param name="limit">The limit.</param>
    /// <param name="said">What was said of it.</param>
    /// <param name="mask">The run's mask.</param>
    public static AgentRunResult AtLimit(string who, AgentAccountLimit limit, string said, ILineMask mask)
    {
        ArgumentNullException.ThrowIfNull(limit);

        var whose = limit.Key is not { } key ? string.Empty : limit.Model is { } model ? $" for '{model}' on {key}" : $" on {key}";

        return Ended(
            AgentRunStatus.RateLimited,
            $"{who} reported the account at its limit{whose}, {(limit.BalanceSpent ? "its balance or included usage spent" : "an allowance used up")}{(limit.Lifts is { } lifts ? $" until {lifts:u}" : string.Empty)}: {said}",
            mask) with
        {
            Limit = limit,
        };
    }
}

/// <summary>
/// What a run used, request by request: in all, each model's part, and the model that served it from its first turn -
/// what every client's result says of its usage (<see cref="AgentUsageReport"/>).
/// </summary>
public sealed class AgentUsageTally
{
    private readonly List<AgentModelUsage> _perModel = [];
    private readonly List<AgentServedModel> _served = [];

    /// <summary>The whole of it, as it stands.</summary>
    public AgentUsage Total { get; private set; } = AgentUsage.None;

    /// <summary>The model that served the run from its first turn, and that turn: the first counted.</summary>
    public IReadOnlyList<AgentServedModel> Served => _served;

    /// <summary>Counts what one request of <paramref name="model"/> used, in turn <paramref name="turn"/>.</summary>
    /// <param name="model">The model that served it, as the step asked for it.</param>
    /// <param name="usage">What it used.</param>
    /// <param name="turn">The turn it was of, from one: the first model counted serves from it.</param>
    public void Add(string model, AgentUsage usage, int turn)
    {
        ArgumentNullException.ThrowIfNull(model);
        ArgumentNullException.ThrowIfNull(usage);

        Total = Total.Plus(usage);
        var at = _perModel.FindIndex(part => string.Equals(part.Model, model, StringComparison.Ordinal));

        if (at < 0)
        {
            _perModel.Add(new AgentModelUsage(model, usage));
        }
        else
        {
            _perModel[at] = _perModel[at] with { Usage = _perModel[at].Usage.Plus(usage) };
        }

        if (_served.Count == 0)
        {
            _served.Add(new AgentServedModel(model, turn));
        }
    }

    /// <summary>The run's usage, with what it cost.</summary>
    /// <param name="cost">What it cost, and where each number is from.</param>
    public AgentUsageReport Report(AgentCost? cost) => new(Total, [.. _perModel], cost);
}
