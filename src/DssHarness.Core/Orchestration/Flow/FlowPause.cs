namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One ask for a safe pause: who asked (an <c>interrupt</c> typed at the terminal, the <c>command</c>
/// <c>pause-orchestrator</c>, a <c>frontEnd</c>, or the engine itself for the <c>budget</c>), and whether the machine
/// steps in progress are stopped too.
/// </summary>
/// <param name="Cause">Who asked for the pause.</param>
/// <param name="All">Whether the machine steps in progress are stopped as well as left to finish.</param>
public sealed record FlowPauseRequest(string Cause, bool All)
{
    /// <summary>Every cause a pause is asked by, as the run's state names it.</summary>
    public static readonly IReadOnlyList<string> Causes = ["interrupt", "command", "frontEnd", "budget"];
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
/// reason steps ran - each counted for reason steps alone.
/// </summary>
public sealed class FlowBudgetSpent
{
    /// <summary>What the reason steps cost, in United States dollars.</summary>
    public double Usd { get; internal set; }

    /// <summary>Every token the clients reported: input, cache write, cache read and output.</summary>
    public long Tokens { get; internal set; }

    /// <summary>The time reason steps ran, in minutes: two that reason at once count twice.</summary>
    public double Minutes { get; internal set; }
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
