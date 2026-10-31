namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One ask for a safe pause: who asked (an <c>interrupt</c> typed at the terminal, the <c>command</c>
/// <c>pause-orchestrator</c>, a <c>frontEnd</c>, the engine itself for the <c>budget</c>, or a reason step whose every
/// account is at its limit or whose provider's service failed - <c>limits</c>), and whether the machine steps in
/// progress are stopped too.
/// </summary>
/// <param name="Cause">Who asked for the pause.</param>
/// <param name="All">Whether the machine steps in progress are stopped as well as left to finish.</param>
public sealed record FlowPauseRequest(string Cause, bool All)
{
    /// <summary>Every cause a pause is asked by, as the run's state names it.</summary>
    public static readonly IReadOnlyList<string> Causes = ["interrupt", "command", "frontEnd", "budget", "limits"];
}

/// <summary>
/// The handoff a reason step stopped while reasoning leaves: its text - the agent's own account where it answered,
/// the engine's from what it holds where it did not - who wrote it, and why the engine wrote one where it did.
/// </summary>
/// <param name="Text">The handoff's text, as the step's next attempt is given it.</param>
/// <param name="By">Who wrote it: <c>agent</c> or <c>engine</c>.</param>
/// <param name="Why">Why the engine wrote it, where it did: the bound the handoff turn was given, the run stopped, or the engine's process ended.</param>
public sealed record FlowHandoff(string Text, string By, string? Why = null)
{
    /// <summary>
    /// The handoff the engine writes where the agent gave none - the one way it writes one: its first line says so and
    /// why (<see cref="FlowHandoffRecord.EngineMark"/>), the rest is what it holds. Why is said on that one line, its
    /// breaks and its closing stop taken out, so what <see cref="FlowHandoffRecord.Of"/> reads back from the text is the
    /// record this handoff is written with.
    /// </summary>
    /// <param name="why">Why the engine wrote it: the bound passed, the turn failed and with what, the run stopped, the engine's process ended.</param>
    /// <param name="body">What it holds beyond why, on the lines after; null where nothing.</param>
    public static FlowHandoff ByEngine(string why, string? body = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        var said = string.Join(' ', why.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');

        return new FlowHandoff($"{FlowHandoffRecord.EngineMark}{said}.{(body is null ? string.Empty : "\n" + body)}", "engine", said);
    }
}

/// <summary>
/// What a run's reasoning has spent against its budget: what the clients reported in tokens and dollars, and the time
/// reason steps ran - each counted for reason steps alone, an advice among them. Its dollars are the one fold of what
/// the engine's agents spend (<see cref="FlowDollars"/>): a part whose cost could not be counted leaves them not known
/// past what was, never charged as nothing.
/// </summary>
public sealed class FlowBudgetSpent
{
    /// <summary>What the reason steps cost, as far as each part's cost could be counted, and why the rest is not known where it is not.</summary>
    public FlowDollars Dollars { get; internal set; }

    /// <summary>What the reason steps cost, in United States dollars, as far as each part's cost could be counted.</summary>
    public double Usd => Dollars.Counted;

    /// <summary>Why a part's cost could not be counted - the first such part's - so the dollars are not known past <see cref="Usd"/>; null where every part's was.</summary>
    public string? Uncounted => Dollars.Uncounted;

    /// <summary>Every token the clients reported: input, cache write, cache read and output.</summary>
    public long Tokens { get; internal set; }

    /// <summary>The time reason steps ran, in minutes: two that reason at once count twice.</summary>
    public double Minutes { get; internal set; }

    /// <summary>Charges one part of the run's reasoning - a turn of a reason step, an advice - by the one fold of its dollars.</summary>
    /// <param name="tokens">The tokens it used.</param>
    /// <param name="dollars">What it cost, or why that could not be counted.</param>
    internal void Charge(long tokens, FlowDollars dollars)
    {
        Tokens += tokens;
        Dollars = Dollars.Plus(dollars);
    }
}

/// <summary>
/// The bound in dollars an agent of the run is held to: its own, or what is left of the run's budget before its reserve
/// where that is less - the one place the two meet, for a reason step's agent and the advisor's alike - so one long
/// launch cannot spend through the reserve, which is what the handoffs are written in.
/// </summary>
public static class FlowRunBudget
{
    /// <summary>The bound the agent is held to; null where neither its own definition nor the run bounds it.</summary>
    /// <param name="own">The agent's own bound, as its definition says it; null where it says none.</param>
    /// <param name="left">What is left of the run's budget in dollars before its reserve; null where the run budgets no dollars.</param>
    public static decimal? Bound(decimal? own, double? left)
        => left is not { } run ? own
            : (decimal)Math.Round(Math.Max(0, run), 6, MidpointRounding.ToZero) is var share && (own is not { } mine || share < mine) ? share
            : own;

    /// <summary>
    /// Whether the run's share, and not the agent's own bound, is what ended an agent at its budget: where the run's is
    /// less than its own, or the agent has none - so the run pauses for its budget rather than asking the step's own
    /// question.
    /// </summary>
    /// <param name="own">The agent's own bound, as its definition says it; null where it says none.</param>
    /// <param name="left">What is left of the run's budget in dollars before its reserve; null where the run budgets no dollars.</param>
    public static bool RunsShareBinds(decimal? own, double? left)
        => left is not null && Bound(own, left) != own;
}

/// <summary>
/// A safe pause the run is under or was ended by: what asked for it, when it began, whether the machine steps were
/// stopped too, and every handoff it wrote.
/// </summary>
public sealed class FlowPauseState
{
    /// <summary>Who asked for the pause, one of <see cref="FlowPauseRequest.Causes"/>.</summary>
    public string Cause { get; internal init; }

    /// <summary>
    /// Whether the machine steps in progress were stopped too - by the pause's own request, or by a later request
    /// that said <c>all</c> after one that did not.
    /// </summary>
    public bool All { get; internal set; }

    /// <summary>When the pause began.</summary>
    public DateTimeOffset BeganAt { get; internal init; }

    /// <summary>When the pause completed - its last handoff written and everything it left running recorded; null while it records.</summary>
    public DateTimeOffset? CompletedAt { get; internal set; }

    /// <summary>Every step that handed off, in the order the handoffs were written, by instance id.</summary>
    public IReadOnlyList<string> Handoffs => _handoffs;

    internal readonly List<string> _handoffs = [];

    internal FlowPauseState(string cause, bool all, DateTimeOffset beganAt)
    {
        Cause = cause;
        All = all;
        BeganAt = beganAt;
    }
}
