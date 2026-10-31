using System.Globalization;
using DssHarness.Core.Agents;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What one agent run of the engine spends, counted from its events as each turn's usage is reported - the one count a
/// run's budget, an advice and the reporter's answer are charged with: every token of every kind the turn used (<see cref="AgentUsage.AllTokens"/>)
/// and its cost at the configuration's prices for the model serving the run then - the one its session started on, or the
/// one a fallback moved it to. A turn whose cost cannot be counted - its model has no price of a kind it used - leaves
/// the run's dollars unknown past what was counted, never zero, naming the price missing (<see cref="FlowDollars"/>). A
/// run budgeted in dollars never comes to one: the runner refuses it, on every client, a model without the prices its
/// client is billed by before any client is taken, so the budget is charged a counted cost or nothing at all. Each turn is
/// handed on as it is counted where the run's events are given - its cost, or why it could not be counted - so a run's
/// budget is charged while its agent works, and what an asking ended part way had spent is still known.
/// </summary>
/// <param name="providers">The configuration's providers, whose models' prices count each turn.</param>
/// <param name="serving">The model the run starts on; null where none resolved, until a session names one.</param>
/// <param name="events">Where each turn is handed on as it is counted - a reason step's, charged to the run's budget at once; null where only the totals are wanted.</param>
public sealed class AgentRunSpend(AgentProviders providers, string? serving, IFlowRunEvents? events = null) : IAgentEventSink
{
    private string? _serving = serving;

    /// <summary>Every token the run's turns used, of every kind.</summary>
    public long Tokens { get; private set; }

    /// <summary>
    /// What the run's turns cost at the configuration's prices, as far as each could be counted, and why the rest could
    /// not be - the price missing, or the model no provider lists - where a turn's could not.
    /// </summary>
    public FlowDollars Dollars { get; private set; }

    /// <summary>Whether any turn's usage was reported: where none was, nothing is known of what the run spent.</summary>
    public bool Reported { get; private set; }

    /// <summary>
    /// What the run spent, as an answer carries it: its dollars - what was counted, and why the rest is not known where a
    /// turn's cost could not be counted - and every token it used; null where no turn's usage was reported, and nothing is
    /// known of what it spent.
    /// </summary>
    public (FlowDollars Dollars, long Tokens)? Spent => Reported ? (Dollars, Tokens) : null;

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
                var uncounted = usd is not null ? null
                    : entry is { } listed ? AgentPrices.UnknownOf(listed.Provider, listed.Model, used.Turn)
                    : $"no provider lists '{_serving ?? "the run's model"}', whose prices the cost is counted at";

                Tokens += tokens;
                Dollars = Dollars.Plus(FlowDollars.Of(usd, uncounted));
                Reported = true;
                events?.Used(tokens, usd, uncounted);
                break;
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// Dollars spent as the engine counts them, part by part - a turn, an agent run, a run's reasoning: what the parts whose
/// cost was counted cost together, and - once a part's could not be counted - why, the first such part's. The whole is
/// then not known past the counted sum, and never read as that sum: the one fold of what the engine's agents spend, an
/// agent run's turns and a run's steps and advices alike (<see cref="Plus"/>).
/// </summary>
/// <param name="Counted">What the parts whose cost was counted cost together, in United States dollars.</param>
/// <param name="Uncounted">Why a part's cost could not be counted - the price missing, the model no provider lists, a part that came with no cost - the first such part's; null where every part's was counted.</param>
public readonly record struct FlowDollars(double Counted, string? Uncounted = null)
{
    /// <summary>One part's dollars: its cost where it was counted, else nothing counted of it and why.</summary>
    /// <param name="usd">The part's cost; null where it could not be counted.</param>
    /// <param name="why">Why it could not be counted, where it could not.</param>
    public static FlowDollars Of(double? usd, string? why)
        => usd is { } counted ? new(counted) : new(0, why is { Length: > 0 } ? why : "a part came with no cost said or counted of it");

    /// <summary>These dollars and <paramref name="part"/>'s together: the counted sums added, and the first reason a part could not be counted kept.</summary>
    /// <param name="part">The part to add.</param>
    public FlowDollars Plus(FlowDollars part) => new(Counted + part.Counted, Uncounted ?? part.Uncounted);

    /// <summary>
    /// The dollars as a person reads them - in status, a report, the budget's question and an ask's answer: the sum, of
    /// <paramref name="budget"/> where one is named, or - where a part's cost could not be counted - at least the counted
    /// sum, and why the rest is not known.
    /// </summary>
    /// <param name="budget">The amount the run may spend, where one is named; null where none is.</param>
    /// <param name="format">How the amounts are written: to the cent by default, finer for a cost that is small.</param>
    public string Said(double? budget = null, string format = "0.00")
    {
        var of = budget is { } named ? $" of {named.ToString(format, CultureInfo.InvariantCulture)}" : string.Empty;
        var counted = Counted.ToString(format, CultureInfo.InvariantCulture);

        return Uncounted is { } why ? $"at least {counted}{of} USD (the rest is not known: {why})" : $"{counted}{of} USD";
    }
}
