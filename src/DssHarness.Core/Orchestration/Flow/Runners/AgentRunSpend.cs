using DssHarness.Core.Agents;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What one agent run of the engine spends, counted from its events as each turn's usage is reported - the one count a
/// run's budget, an advice and the reporter's answer are charged with: every token of every kind the turn used (<see cref="AgentUsage.AllTokens"/>)
/// and its cost at the configuration's prices for the model serving the run then - the one its session started on, or the
/// one a fallback moved it to. A turn whose cost cannot be counted - its model has no price of a kind it used - leaves
/// the run's dollars unknown, never zero, naming the price missing. A run budgeted in dollars never comes to one: the
/// runner refuses it, on every client, a model without the prices its client is billed by before any client is taken,
/// so the budget is charged a counted cost or nothing at all, and such a turn is handed on with its tokens alone. Each
/// turn is handed on as it is counted where the run's events are given, so a run's budget is charged while its agent
/// works, and what an asking ended part way had spent is still known.
/// </summary>
/// <param name="providers">The configuration's providers, whose models' prices count each turn.</param>
/// <param name="serving">The model the run starts on; null where none resolved, until a session names one.</param>
/// <param name="events">Where each turn is handed on as it is counted - a reason step's, charged to the run's budget at once; null where only the totals are wanted.</param>
public sealed class AgentRunSpend(AgentProviders providers, string? serving, IFlowRunEvents? events = null) : IAgentEventSink
{
    private string? _serving = serving;
    private double _counted;

    /// <summary>Every token the run's turns used, of every kind.</summary>
    public long Tokens { get; private set; }

    /// <summary>What the run's turns cost, at the configuration's prices; null once a turn's cost could not be counted.</summary>
    public double? Usd => Unpriced is null ? _counted : null;

    /// <summary>Why a turn's cost could not be counted - the price missing, or the model no provider lists - where one could not; null otherwise.</summary>
    public string? Unpriced { get; private set; }

    /// <summary>Whether any turn's usage was reported: where none was, nothing is known of what the run spent.</summary>
    public bool Reported { get; private set; }

    /// <summary>
    /// What the run spent, as an answer says it: every token it used, and its dollars - null where a turn's cost could
    /// not be counted; null where no turn's usage was reported, and nothing is known of what it spent.
    /// </summary>
    public (double? Usd, long Tokens)? Spent => Reported ? (Usd, Tokens) : null;

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
                var entry = _serving is { } model ? providers.Of(model) : null;
                var usd = entry is { } priced ? AgentPrices.CostOf(priced.Provider, priced.Model, used.Turn) : null;

                if (usd is null)
                {
                    Unpriced ??= entry is { } listed
                        ? AgentPrices.UnknownOf(listed.Provider, listed.Model, used.Turn)
                        : $"no provider lists '{_serving ?? "the run's model"}', whose prices the cost is counted at";
                }

                Tokens += tokens;
                _counted += usd ?? 0;
                Reported = true;
                events?.Used(tokens, usd ?? 0);
                break;
        }

        return ValueTask.CompletedTask;
    }
}
