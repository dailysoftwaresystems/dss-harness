using DssHarness.Core.Agents;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What one agent run of the engine spends, counted from its events as each turn's usage is reported - the one count a
/// run's budget, an advice and the reporter's answer are charged with: every token of every kind the turn used
/// (<see cref="AgentUsage.AllTokens"/>) and its cost at the configuration's prices for the model serving the run then -
/// the one its session started on, or the one a fallback moved it to. A turn of a model with no price adds nothing to
/// the dollars a budget is charged: a run budgeted in dollars is refused such a model before it starts - and makes what
/// the run cost unknown, never zero, where it is said (<see cref="Spent"/>). Each turn is handed on as it is counted
/// where the run's events are given, so a run's budget is charged while its agent works, and what an asking ended part
/// way had spent is still known.
/// </summary>
/// <param name="providers">The configuration's providers, whose models' prices count each turn.</param>
/// <param name="serving">The model the run starts on; null where none resolved, until a session names one.</param>
/// <param name="events">Where each turn is handed on as it is counted - a reason step's, charged to the run's budget at once; null where only the totals are wanted.</param>
public sealed class AgentRunSpend(AgentProviders providers, string? serving, IFlowRunEvents? events = null) : IAgentEventSink
{
    private string? _serving = serving;

    /// <summary>Every token the run's turns used, of every kind.</summary>
    public long Tokens { get; private set; }

    /// <summary>What the run's turns cost, at the configuration's prices.</summary>
    public double Usd { get; private set; }

    /// <summary>Whether any turn's usage was reported: where none was, nothing is known of what the run spent.</summary>
    public bool Reported { get; private set; }

    /// <summary>Whether a turn's cost was unknown: a kind it used holds no price, or no model serving it was known.</summary>
    public bool Unpriced { get; private set; }

    /// <summary>
    /// What the run spent, as an answer says it: every token it used, and its dollars - null where a turn's cost was
    /// unknown; null where no turn's usage was reported, and nothing is known of what it spent.
    /// </summary>
    public (double? Usd, long Tokens)? Spent => Reported ? (Unpriced ? null : Usd, Tokens) : null;

    /// <inheritdoc/>
    public ValueTask EmitAsync(AgentEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        switch (@event)
        {
            case SessionStarted started:
                _serving = started.Model;
                break;

            case FallbackStarted moved:
                _serving = moved.ToModel;
                break;

            case UsageReported used:
                var tokens = used.Turn.AllTokens;
                var priced = _serving is { } model && providers.Of(model) is { } entry ? AgentPrices.CostOf(entry.Provider, entry.Model, used.Turn) : null;
                var usd = priced ?? 0;

                Tokens += tokens;
                Usd += usd;
                Reported = true;
                Unpriced |= priced is null;
                events?.Used(tokens, usd);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
