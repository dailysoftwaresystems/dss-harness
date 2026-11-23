using System.Globalization;
using System.Text.Json.Serialization;
using DssHarness.Core.Agents;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// What one step instance's work measured, written by the engine as it runs and read by the report: when it was ready,
/// how long it waited and for what, how long it ran, and what each of its attempts used - the model that served it, the
/// tokens of every kind, the dollars at the price of that moment, and the tools it called. Held on the instance's record
/// (<see cref="FlowStepRecord.Measure"/>), one owner, kept across a resume, and null until the engine draws the instance:
/// an instance the walk never found ready holds none. Every change of it is one of this record's own: an attempt's entry
/// opened as the attempt starts (<see cref="Running"/>), each turn charged into it (<see cref="Charged"/>), its ending
/// (<see cref="Ended"/>), and every wait settled where its causes change (<see cref="Waiting"/>) - the totals and the
/// run time summed from the entries at each.
/// </summary>
/// <param name="ReadyAt">
/// When everything the instance depends on ended and it was free to be drawn; null where the engine never drew it.
/// </param>
/// <param name="Waited">
/// How long it waited after it was ready, split by the engine's own causes (<see cref="FlowWaits"/> and the words a
/// record's <c>waitsOn</c> holds): the run's limits and claims, its machine's queue, memory or room, a person's answer.
/// Each entry names its cause and its seconds, exact as the clock gave them, and one cause that waited more than once is
/// one entry, summed. What it waited on before it was ready - the steps it depends on - is theirs, never its own.
/// </param>
/// <param name="Ran">The seconds its attempts' runs took, summed - the time its own work ran, exact as the clock gave it; null where none ended.</param>
/// <param name="Paused">
/// The seconds it stood stopped for the run's own pause - a safe pause, a stop, a crash - waiting on the resume;
/// waits on every other cause are <paramref name="Waited"/>'s, and none is counted twice.
/// </param>
/// <param name="Attempts">One entry per attempt the instance made, in order, each opened as its attempt starts.</param>
/// <param name="Advice">
/// What the advisor spent answering the instance's own questions, summed; null where none was asked of it. It is no
/// attempt's own spend - the advisor works in its own records, beside the step - and the run's total counts it with
/// the attempts'.
/// </param>
/// <param name="Totals">The attempts summed: every token, every dollar, the turns and the uncounted reasons; null where it holds no attempt.</param>
/// <param name="WaitingOn">
/// What the instance waits on now, in the words a record's <c>waitsOn</c> holds; null where it waits on nothing. The
/// open period is kept with <paramref name="WaitingSince"/> so a resume closes it as this session would - a wait on a
/// person that spans two sessions is one wait, whole.
/// </param>
/// <param name="WaitingSince">When the open wait began; null where none is open.</param>
public sealed record FlowStepMeasure(
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? ReadyAt = null,
    IReadOnlyList<FlowStepWait>? Waited = null,
    double? Ran = null,
    double? Paused = null,
    IReadOnlyList<FlowStepAttempt>? Attempts = null,
    FlowStepAdvice? Advice = null,
    FlowStepTotals? Totals = null,
    IReadOnlyList<string>? WaitingOn = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? WaitingSince = null)
{
    /// <summary>What is wrong with this measure; null when nothing is.</summary>
    public string? Problem()
        => Attempts?.Select(attempt => attempt.Problem()).FirstOrDefault(problem => problem is not null) is { } attempt ? attempt
            : Attempts is { } numbered && numbered.Select(entry => entry.N).Distinct().Count() != numbered.Count ? "it holds one attempt's entry twice"
            : Waited?.Select(wait => wait.Problem()).FirstOrDefault(problem => problem is not null) is { } wait ? wait
            : Waited is { } causes && causes.Select(entry => entry.Cause).Distinct(StringComparer.Ordinal).Count() != causes.Count ? "it holds one cause's wait twice"
            : Attempts is { Count: > 0 } && Totals is null ? "it holds attempts and no totals"
            : WaitingSince is null != (WaitingOn is null || WaitingOn.Count == 0) ? "an open wait without its time, or a time without its wait"
            : Ran is < 0 ? $"it ran {Ran?.ToString("0.0", CultureInfo.InvariantCulture)} seconds"
            : Paused is < 0 ? $"it stood paused {Paused?.ToString("0.0", CultureInfo.InvariantCulture)} seconds"
            : Advice?.Problem() is { } advice ? $"its advice: {advice}"
            : null;

    /// <summary>The spend this measure holds - its attempts' and its advice's - as the run's total counts it.</summary>
    [JsonIgnore]
    public (FlowDollars Dollars, long Tokens) Spent
    {
        get
        {
            var dollars = new FlowDollars(0);
            var tokens = 0L;

            foreach (var attempt in Attempts ?? [])
            {
                dollars = dollars.Plus(new FlowDollars(attempt.Usd ?? 0, attempt.Uncounted));
                tokens += attempt.Tokens?.AllTokens ?? 0;
            }

            if (Advice is { } advice)
            {
                dollars = dollars.Plus(new FlowDollars(advice.Usd ?? 0, advice.Uncounted));
                tokens += advice.Tokens ?? 0;
            }

            return (dollars, tokens);
        }
    }

    /// <summary>
    /// This measure with its open wait brought to <paramref name="on"/> at <paramref name="at"/>: the period that was
    /// open, where the causes changed, is closed into <see cref="Waited"/> by its causes - or into <see cref="Paused"/>
    /// where the cause was the resume - and the new period opens where there is one to open. The engine settles this at
    /// every place it writes what an instance waits on, so the causes are always the record's own words, and an
    /// unchanged set of causes keeps the period open as it was. Seconds are kept exact as the clock gave them, so a wait
    /// split at every change sums to the whole of it.
    /// </summary>
    /// <param name="on">What the instance waits on now; empty where it waits on nothing.</param>
    /// <param name="at">Now, as the engine's clock says it.</param>
    public FlowStepMeasure Waiting(IReadOnlyList<string> on, DateTimeOffset at)
    {
        ArgumentNullException.ThrowIfNull(on);

        if (WaitingOn is not null && WaitingOn.SequenceEqual(on, StringComparer.Ordinal))
        {
            return this;
        }

        var waited = Waited;
        var paused = Paused;

        if (WaitingOn is { Count: > 0 } causes && WaitingSince is { } since && at > since)
        {
            // The closed period's seconds folded into its causes - each merged with its own earlier periods, so a cause
            // that waited twice is one entry.
            var seconds = (at - since).TotalSeconds;

            foreach (var cause in causes)
            {
                if (cause == FlowWaits.Resume)
                {
                    paused = (paused ?? 0) + seconds;
                    continue;
                }

                var held = waited?.FirstOrDefault(wait => wait.Cause == cause);

                waited = held is { } wait
                    ? [.. waited!.Select(other => ReferenceEquals(other, wait) ? wait with { Seconds = wait.Seconds + seconds } : other)]
                    : [.. waited ?? [], new FlowStepWait(cause, seconds)];
            }
        }

        return this with
        {
            Waited = waited,
            Paused = paused,
            WaitingOn = on.Count > 0 ? on : null,
            WaitingSince = on.Count > 0 ? at : null,
        };
    }

    /// <summary>
    /// This measure with attempt <paramref name="attempt"/> running from <paramref name="at"/>: its entry opened, or -
    /// where the attempt goes on after an answer let it, the same attempt it was - a new run of it opened on its entry,
    /// how its last run ended cleared, for it has not ended yet. The one place an attempt's entry is made, so every turn
    /// it spends from here on is charged into it.
    /// </summary>
    /// <param name="attempt">The attempt, from 1.</param>
    /// <param name="at">When it starts, as the engine's clock says it.</param>
    public FlowStepMeasure Running(int attempt, DateTimeOffset at)
        => With(attempt, entry => entry with
        {
            StartedAt = entry.StartedAt ?? at,
            FinishedAt = null,
            Outcome = null,
            Exit = null,
            Error = null,
            Cause = null,
            Exception = null,
            Runs = entry.Runs is { Count: > 0 } runs && runs[^1].FinishedAt is null ? runs : [.. entry.Runs ?? [], new FlowStepSpan(at)],
        });

    /// <summary>
    /// This measure with one turn of attempt <paramref name="attempt"/> charged into its entry - the one place a turn is
    /// counted in a step's measure, the run's own total charged with the same turn beside it. A turn of an attempt that
    /// is over still reaches that attempt's own entry, for no attempt's spend is another's.
    /// </summary>
    /// <param name="attempt">The attempt the turn belongs to.</param>
    /// <param name="turn">The turn's spend.</param>
    public FlowStepMeasure Charged(int attempt, FlowTurnSpend turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        return With(attempt, entry => entry.Charged(turn));
    }

    /// <summary>
    /// This measure with attempt <paramref name="attempt"/>'s open run closed at <paramref name="at"/> and its entry
    /// refined by <paramref name="ending"/> - how it ended and the account its runner gave of it - every member the
    /// ending does not name left as the entry held it: its spend above all, which only its turns charge. Its
    /// <see cref="FlowStepAttempt.FinishedAt"/> is when its work last stopped: an ending given after it stopped - an
    /// answer that fails an attempt that asked, a stop of one a crash cut off - keeps that time.
    /// </summary>
    /// <param name="attempt">The attempt that ended, or whose run stopped.</param>
    /// <param name="at">When it ended, as the engine's clock says it.</param>
    /// <param name="ending">The ending, applied to the entry once its run is closed.</param>
    public FlowStepMeasure Ended(int attempt, DateTimeOffset at, Func<FlowStepAttempt, FlowStepAttempt> ending)
    {
        ArgumentNullException.ThrowIfNull(ending);

        return With(attempt, entry => ending(entry.Stopped(at)) with
        {
            FinishedAt = entry.IsRunning || entry.Runs is not { Count: > 0 } ? at : entry.Runs[^1].FinishedAt ?? at,
        });
    }

    /// <summary>
    /// This measure with attempt <paramref name="attempt"/>'s open run closed at <paramref name="at"/> - while its
    /// machine holds its work back, which is a wait and not its run - or a run opened at <paramref name="at"/> where none
    /// is open, as its machine lets its work start again: the run time is the time its work ran, the wait the
    /// time its machine kept it.
    /// </summary>
    /// <param name="attempt">The attempt in flight.</param>
    /// <param name="held">Whether its machine holds its work back now.</param>
    /// <param name="at">Now, as the engine's clock says it.</param>
    public FlowStepMeasure Held(int attempt, bool held, DateTimeOffset at)
        => With(attempt, entry => held
            ? entry.Stopped(at)
            : entry.Runs is { Count: > 0 } runs && runs[^1].FinishedAt is null ? entry : entry with { Runs = [.. entry.Runs ?? [], new FlowStepSpan(at)] });

    /// <summary>
    /// This measure with the backoff the engine waits before attempt <paramref name="attempt"/> runs again written onto
    /// its entry: the wait is the failed attempt's own, what its ending cost the run in time before the next began.
    /// </summary>
    /// <param name="attempt">The attempt that will run again.</param>
    /// <param name="seconds">The backoff, in seconds.</param>
    public FlowStepMeasure RetryingIn(int attempt, double seconds)
        => With(attempt, entry => entry with { RetryAfter = seconds });

    /// <summary>
    /// This measure with <paramref name="advice"/>'s spend added to its advice: the advisor's tokens and dollars are
    /// the run's, charged with the attempt's own at the one place a token is counted, and kept here beside them.
    /// </summary>
    /// <param name="advice">What one asking of the advisor spent, as its answer carried it.</param>
    public FlowStepMeasure Advised(FlowAdvice advice)
    {
        ArgumentNullException.ThrowIfNull(advice);

        if (advice.Tokens is null && advice.Usd is null && advice.Uncounted is null)
        {
            return this;
        }

        return this with
        {
            Advice = Advice is { } held
                ? held with { Tokens = (held.Tokens ?? 0) + (advice.Tokens ?? 0), Usd = (held.Usd ?? 0) + (advice.Usd ?? 0), Uncounted = held.Uncounted ?? advice.Uncounted }
                : new FlowStepAdvice(advice.Model, advice.Tokens, advice.Usd, advice.Uncounted),
        };
    }

    /// <summary>
    /// This measure with attempt <paramref name="attempt"/>'s entry changed by <paramref name="change"/> - made where
    /// it holds none yet - and its totals and run time summed again from every entry.
    /// </summary>
    private FlowStepMeasure With(int attempt, Func<FlowStepAttempt, FlowStepAttempt> change)
    {
        var attempts = new List<FlowStepAttempt>(Attempts ?? []);
        var at = attempts.FindIndex(entry => entry.N == attempt);

        if (at >= 0)
        {
            attempts[at] = change(attempts[at]);
        }
        else
        {
            attempts.Add(change(new FlowStepAttempt(attempt)));
            attempts.Sort((one, other) => one.N.CompareTo(other.N));
        }

        var ran = attempts.Sum(entry => entry.RanSeconds);

        return this with
        {
            Attempts = [.. attempts],
            Totals = FlowStepTotals.Of(attempts),
            Ran = attempts.Any(entry => entry.Runs?.Any(run => run.FinishedAt is not null) == true) ? ran : null,
        };
    }
}

/// <summary>
/// What the advisor spent answering one step's own questions: its tokens and dollars, charged to the run as any
/// attempt's are and kept on the step's measure beside them - never in an attempt, for the advisor's turns are its
/// own, in its own records, and an attempt's tokens are its stream's alone.
/// </summary>
/// <param name="Model">The model that served the advisor, where one did.</param>
/// <param name="Tokens">Every token its turns used, of every kind; null where none was reported.</param>
/// <param name="Usd">What they cost, as far as each turn could be counted; null where none was reported.</param>
/// <param name="Uncounted">Why a turn's cost could not be counted; null where every turn's was.</param>
public sealed record FlowStepAdvice(string? Model = null, long? Tokens = null, double? Usd = null, string? Uncounted = null)
{
    /// <summary>What is wrong with this advice's spend; null when nothing is.</summary>
    public string? Problem()
        => Tokens is < 0 ? $"its advice used {Tokens?.ToString(CultureInfo.InvariantCulture)} tokens"
            : Usd is < 0 ? $"its advice cost {Usd?.ToString("0.0000", CultureInfo.InvariantCulture)} USD"
            : null;
}

/// <summary>One cause the instance waited on, and how long.</summary>
/// <param name="Cause">The cause, as <see cref="FlowWaits"/> names it.</param>
/// <param name="Seconds">How long it waited on it, exact as the clock gave it.</param>
public sealed record FlowStepWait(string Cause, double Seconds)
{
    /// <summary>What is wrong with this wait; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Cause) ? "a wait names no cause"
            : Seconds < 0 ? $"a wait of '{Cause}' is {Seconds.ToString("0.0", CultureInfo.InvariantCulture)} seconds"
            : null;
}

/// <summary>One run of an attempt: from when its work ran to when it stopped - an asking, a pause, its machine holding it back, its end.</summary>
/// <param name="StartedAt">When the run began.</param>
/// <param name="FinishedAt">When it stopped; null while it runs.</param>
public sealed record FlowStepSpan(
    [property: JsonConverter(typeof(FlowTimeConverter))] DateTimeOffset StartedAt,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null)
{
    /// <summary>Its seconds, where it has ended; nothing while it runs.</summary>
    [JsonIgnore]
    public double Seconds => FinishedAt is { } ended && ended > StartedAt ? (ended - StartedAt).TotalSeconds : 0;
}

/// <summary>
/// What one attempt of a step instance used: when it began and ended and the runs it took between, how it ended, what
/// served it and what that cost - model by model - and what it called: the record the report's per-step rows and the
/// budget's answer read.
/// </summary>
/// <param name="N">The attempt's number, from 1.</param>
/// <param name="StartedAt">When this attempt began; null where the engine never started it.</param>
/// <param name="FinishedAt">When it last stopped; null while it runs or where the engine never ended it.</param>
/// <param name="Outcome">How it ended, as the record's state names it (done, failed, paused, stopped); null while it runs.</param>
/// <param name="Exit">The code it ended with; null while it runs.</param>
/// <param name="Error">Why it ended as it did, where the code alone cannot say; null where none or while it runs.</param>
/// <param name="Cause">The cause its ending was recorded with; null where none.</param>
/// <param name="Provider">The provider that served the attempt last; null for a machine step.</param>
/// <param name="Model">The model that served it - the last after any fallback; null for a machine step.</param>
/// <param name="Effort">The effort the model was asked with; null where none.</param>
/// <param name="FallbackFrom">The model the attempt began on and left, where a fallback moved it; null where none did.</param>
/// <param name="FallbackReason">Why it moved: the reason the walk gives.</param>
/// <param name="Tokens">The tokens the attempt's turns used, by kind, summed; null where none was reported.</param>
/// <param name="Usd">What the attempt cost at the prices of the models serving it, as far as each turn could be counted.</param>
/// <param name="Uncounted">Why a part of its cost could not be counted - a model with no price, a launch billed to usage credits, a stream a crash left that does not read; null where every part was.</param>
/// <param name="Turns">The agent's turns; null for a machine step.</param>
/// <param name="Tools">The tool calls the attempt made, by tool name, with how many failed and how many were denied; null for a machine step or where the agent called none.</param>
/// <param name="Compactions">How many times the agent's conversation was compacted; null where none.</param>
/// <param name="RetryAfter">The backoff the engine waited before the attempt ran again; null for the last or the only attempt.</param>
/// <param name="Exception">Where the attempt ended in an error the engine did not expect - an exception out of a step runner, not a verdict: its type, message, stack and causes; null otherwise.</param>
/// <param name="Runs">Each run the attempt took - one, or one more for each answer that let it go on and each time its machine let its work start again - in order, the last open while it runs.</param>
/// <param name="Reports">
/// How many of its turns' usage reports were charged into it: what a resume after a crash completes from, charging only
/// the turns of its stored stream past these, so no turn is counted twice.
/// </param>
/// <param name="Parts">
/// Its spend by the model that served it - one part per model, in the order they first served - each with the provider
/// and the price it was costed at, copied as it was counted: an attempt a fallback moved holds a part for each model,
/// and a price changed in the configuration later never changes what a past part cost.
/// </param>
/// <param name="Legs">What a machine step's legs did in this attempt, each leg's phases with their times and its digest's counts; null for a step that runs no leg.</param>
public sealed record FlowStepAttempt(
    int N,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? StartedAt = null,
    [property: JsonConverter(typeof(FlowTimeConverter)), JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? FinishedAt = null,
    string? Outcome = null,
    int? Exit = null,
    string? Error = null,
    string? Cause = null,
    string? Provider = null,
    string? Model = null,
    string? Effort = null,
    string? FallbackFrom = null,
    string? FallbackReason = null,
    AgentUsage? Tokens = null,
    double? Usd = null,
    string? Uncounted = null,
    int? Turns = null,
    IReadOnlyList<FlowStepToolUse>? Tools = null,
    int? Compactions = null,
    double? RetryAfter = null,
    Results.ExceptionRecord? Exception = null,
    IReadOnlyList<FlowStepSpan>? Runs = null,
    int? Reports = null,
    IReadOnlyList<FlowStepPart>? Parts = null,
    IReadOnlyList<FlowStepLeg>? Legs = null)
{
    /// <summary>The seconds its runs took, summed - the open one counted from nothing until it ends.</summary>
    [JsonIgnore]
    public double RanSeconds => Runs?.Sum(run => run.Seconds) ?? 0;

    /// <summary>Whether a run of it is open: it runs now, or ran when the engine's process ended.</summary>
    [JsonIgnore]
    public bool IsRunning => Runs is { Count: > 0 } runs && runs[^1].FinishedAt is null;

    /// <summary>What is wrong with this attempt; null when nothing is.</summary>
    public string? Problem()
        => N < 1 ? $"its number is {N.ToString(CultureInfo.InvariantCulture)}, and the first is 1"
            : Usd is < 0 ? $"it cost {Usd?.ToString("0.0000", CultureInfo.InvariantCulture)} USD"
            : RetryAfter is < 0 ? $"it waited {RetryAfter?.ToString("0.0", CultureInfo.InvariantCulture)} seconds to run again"
            : Reports is < 0 ? $"it was charged {Reports?.ToString(CultureInfo.InvariantCulture)} usage reports"
            : Runs is { } runs && runs.Take(runs.Count - 1).Any(run => run.FinishedAt is null) ? "a run of it before its last never ended"
            : Runs?.Any(run => run.FinishedAt is { } ended && ended < run.StartedAt) == true ? "a run of it ended before it began"
            : Tools?.Select(tool => tool.Problem()).FirstOrDefault(problem => problem is not null) is { } tool ? tool
            : Parts?.Select(part => part.Problem()).FirstOrDefault(problem => problem is not null) is { } part ? part
            : Legs?.Select(leg => leg.Problem()).FirstOrDefault(problem => problem is not null) is { } leg ? leg
            : null;

    /// <summary>
    /// This attempt with one turn's spend added - its tokens, its dollars and its uncounted reason folded into the
    /// attempt and into the part of the model that served it, the price the turn was costed at copied onto that part, and
    /// one more of its usage reports counted.
    /// </summary>
    /// <param name="turn">The turn's spend.</param>
    public FlowStepAttempt Charged(FlowTurnSpend turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var parts = new List<FlowStepPart>(Parts ?? []);
        var at = parts.FindIndex(part => string.Equals(part.Model, turn.Serving, StringComparison.Ordinal));
        var part = at >= 0 ? parts[at] : new FlowStepPart(turn.Serving);
        part = part with
        {
            Provider = turn.Price?.Provider ?? part.Provider,
            Tokens = (part.Tokens ?? AgentUsage.None).Plus(turn.Usage),
            Usd = (part.Usd ?? 0) + (turn.Usd ?? 0),
            Uncounted = part.Uncounted ?? turn.Uncounted,
            Price = turn.Price ?? part.Price,
        };

        if (at >= 0)
        {
            parts[at] = part;
        }
        else
        {
            parts.Add(part);
        }

        return this with
        {
            Tokens = (Tokens ?? AgentUsage.None).Plus(turn.Usage),
            Usd = (Usd ?? 0) + (turn.Usd ?? 0),
            Uncounted = Uncounted ?? turn.Uncounted,
            Model = turn.Serving ?? Model,
            Provider = turn.Price?.Provider ?? Provider,
            Reports = (Reports ?? 0) + 1,
            Parts = [.. parts],
        };
    }

    /// <summary>
    /// This attempt with the account its runner gave of one run of it added: what served it - the provider, the model, the
    /// effort, where a fallback moved it from and why - and the turns, the tools and the compactions of that run, summed
    /// with the runs before it of the same attempt, and the legs it ran. Never its spend, which its turns alone charge.
    /// </summary>
    /// <param name="account">The run's account, as its runner gave it.</param>
    /// <param name="whole">
    /// Whether the account is of every run of the attempt - a crash's, read back from its whole stream - and stands in
    /// the place of the counts the entry held, rather than being added to them.
    /// </param>
    public FlowStepAttempt Accounted(FlowStepAttempt account, bool whole = false)
    {
        ArgumentNullException.ThrowIfNull(account);

        return this with
        {
            Provider = account.Provider ?? Provider,
            Model = account.Model ?? Model,
            Effort = account.Effort ?? Effort,
            FallbackFrom = FallbackFrom ?? account.FallbackFrom,
            FallbackReason = FallbackFrom is null ? account.FallbackReason : FallbackReason,
            Turns = whole ? account.Turns : Sum(Turns, account.Turns),
            Compactions = whole ? account.Compactions : Sum(Compactions, account.Compactions),
            Tools = whole ? account.Tools : FlowStepToolUse.Merged(Tools, account.Tools),
            Legs = account.Legs ?? Legs,
        };

        static int? Sum(int? held, int? more) => held is null && more is null ? null : (held ?? 0) + (more ?? 0);
    }

    /// <summary>This attempt with its open run, where one is, closed at <paramref name="at"/>.</summary>
    /// <param name="at">When it stopped.</param>
    internal FlowStepAttempt Stopped(DateTimeOffset at)
        => IsRunning
            ? this with { Runs = [.. Runs!.Take(Runs!.Count - 1), Runs![^1] with { FinishedAt = at > Runs![^1].StartedAt ? at : Runs![^1].StartedAt }] }
            : this;
}

/// <summary>
/// One turn's spend as it reaches the engine - as its agent's count says it is reported (<see cref="IFlowRunEvents.Used"/>),
/// or as a resume replays a cut-off attempt's stored stream (<see cref="FlowCutAttempt.Turns"/>): its tokens by kind, its
/// cost at the price of the model that served it or why that could not be counted, the model, and the price it was costed
/// at, copied as it was counted.
/// </summary>
/// <param name="Usage">The turn's tokens, by kind.</param>
/// <param name="Usd">What the turn cost, where it could be counted; null where it could not.</param>
/// <param name="Uncounted">Why it could not be counted, where it could not; null otherwise.</param>
/// <param name="Serving">The model that served the turn - the last after any fallback; null where the caller does not know it.</param>
/// <param name="Price">The price it was costed at - the serving model's entry as the configuration gave it, with its provider; null where no provider lists the model.</param>
public sealed record FlowTurnSpend(AgentUsage Usage, double? Usd, string? Uncounted = null, string? Serving = null, FlowStepPrice? Price = null);

/// <summary>
/// The account a resume reads of an attempt a crash cut off before the engine read its answer: what its runner would have
/// said of it - what served it, its turns, its tools, its compactions - and every turn of its stored stream with its spend,
/// in the order they were reported, of which the engine charges only those past what its record holds charged - or why
/// its stream could not be read whole or priced, which leaves what it spent past its record not known.
/// </summary>
/// <param name="Account">What served it and what it called, over every run of it, as its runner accounts a run; its spend is not in it.</param>
/// <param name="Turns">Every turn its stream reported, in order, each priced as the configuration stands now.</param>
/// <param name="EndedAt">When its stream's last event was written: where the crash left it; null where it was not read.</param>
/// <param name="Unread">Why its stream could not be read whole or priced; null where it was.</param>
public sealed record FlowCutAttempt(FlowStepAttempt Account, IReadOnlyList<FlowTurnSpend> Turns, DateTimeOffset? EndedAt, string? Unread = null)
{
    /// <summary>The account of a stream that could not be read whole or priced, saying why.</summary>
    /// <param name="why">Why, said whole.</param>
    public static FlowCutAttempt NotRead(string why) => new(new FlowStepAttempt(0), [], null, why);
}

/// <summary>
/// One model's part of an attempt's spend: the model, the provider that listed it, its turns' tokens and dollars, why a
/// part of its cost could not be counted, and the price it was costed at, copied as it was counted.
/// </summary>
/// <param name="Model">The model; null where the turns that spent it named none.</param>
/// <param name="Provider">The provider that listed it; null where none did.</param>
/// <param name="Tokens">Its turns' tokens, by kind.</param>
/// <param name="Usd">What they cost, as far as each could be counted.</param>
/// <param name="Uncounted">Why a turn's cost could not be counted; null where every one's was.</param>
/// <param name="Price">The price its turns were costed at; null where no provider lists the model.</param>
public sealed record FlowStepPart(
    string? Model = null,
    string? Provider = null,
    AgentUsage? Tokens = null,
    double? Usd = null,
    string? Uncounted = null,
    FlowStepPrice? Price = null)
{
    /// <summary>What is wrong with this part; null when nothing is.</summary>
    public string? Problem()
        => Usd is < 0 ? $"the part of '{Model ?? "no model"}' cost {Usd?.ToString("0.0000", CultureInfo.InvariantCulture)} USD"
            : null;
}

/// <summary>What one leg of a machine step's attempt did: its verdict and time, how long its machine held it before it was let start, each phase's time, and its digest's counts.</summary>
/// <param name="Leg">The leg, as the configuration names it.</param>
/// <param name="Verdict">The verdict it reached, as a ledger's line says it.</param>
/// <param name="Seconds">Its wall time; null where it never ran.</param>
/// <param name="AdmittedAfter">How long its machine held it before it let it start; null where nothing admitted it.</param>
/// <param name="Phases">Each phase it ran, in order, with its time.</param>
/// <param name="Errors">The errors its failing phase's digest counted; null where it holds no digest.</param>
/// <param name="Warnings">The warnings its digest counted; null where it holds no digest.</param>
/// <param name="FailedTests">The tests its digest counted failed, errored, timed out or crashed; null where it holds no digest.</param>
/// <param name="Unparsed">How many lines of its failing phase that look like errors its digest holds unread by every parser; null where it holds no digest.</param>
public sealed record FlowStepLeg(
    string Leg,
    string Verdict,
    double? Seconds = null,
    double? AdmittedAfter = null,
    IReadOnlyList<FlowStepPhase>? Phases = null,
    int? Errors = null,
    int? Warnings = null,
    int? FailedTests = null,
    int? Unparsed = null)
{
    /// <summary>What <paramref name="line"/>, a leg's line of its ledger, says the leg did.</summary>
    /// <param name="line">The leg's line.</param>
    public static FlowStepLeg Of(Execution.LedgerLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        var digest = line.Digest;

        return new FlowStepLeg(
            line.Leg,
            Execution.Verdicts.Display(line.Verdict),
            line.Duration > TimeSpan.Zero ? line.Duration.TotalSeconds : null,
            line.Admission?.WaitedSeconds,
            line.Phases.Count == 0 ? null : [.. line.Phases.Select(phase => new FlowStepPhase(phase.Phase, phase.Duration.TotalSeconds))],
            digest?.Errors,
            digest?.Warnings,
            digest is null ? null : digest.Failed + digest.Errored + digest.TimedOut + digest.Crashed,
            digest is null ? null : digest.Unparsed?.Count ?? 0);
    }

    /// <summary>What is wrong with this leg's record; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Leg) ? "a leg names no leg"
            : Seconds is < 0 || AdmittedAfter is < 0 || Phases?.Any(phase => phase.Seconds < 0) == true ? $"the leg '{Leg}' holds a negative time"
            : null;
}

/// <summary>One phase of a leg, and its time.</summary>
/// <param name="Phase">The phase, as the leg's ledger names it.</param>
/// <param name="Seconds">Its time.</param>
public sealed record FlowStepPhase(string Phase, double Seconds);

/// <summary>The price an attempt's cost was counted at, as the configuration gave it when it was counted.</summary>
/// <param name="Provider">The provider.</param>
/// <param name="Model">The model.</param>
/// <param name="Currency">The currency the prices are in.</param>
/// <param name="ReadOn">When the numbers were read, as the configuration records it.</param>
/// <param name="InputMiss">Per million input tokens not read from the cache.</param>
/// <param name="InputHit">Per million input tokens read from the cache.</param>
/// <param name="Output">Per million output tokens.</param>
/// <param name="CacheWrite">Per million tokens written to the cache.</param>
/// <param name="CacheWrite1h">Per million tokens written to the one-hour cache.</param>
public sealed record FlowStepPrice(
    string Provider,
    string Model,
    string Currency,
    string? ReadOn = null,
    double? InputMiss = null,
    double? InputHit = null,
    double? Output = null,
    double? CacheWrite = null,
    double? CacheWrite1h = null)
{
    /// <summary>
    /// The price of <paramref name="model"/> under <paramref name="provider"/>, as the configuration gives it now; null
    /// where the model is not listed, which the attempt records as its uncounted reason instead.
    /// </summary>
    public static FlowStepPrice? Of(Configuration.AgentProviderConfig provider, Configuration.AgentModelConfig model)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(model);

        return new FlowStepPrice(
            provider.Name,
            model.Code,
            provider.Currency is { Length: > 0 } currency ? currency : AgentPrices.BudgetCurrency,
            provider.ReadOn,
            model.PriceInputMiss,
            model.PriceInputHit,
            model.PriceOutput,
            model.PriceCacheWrite,
            model.PriceCacheWrite1h);
    }
}

/// <summary>The tool calls one attempt made, by tool name.</summary>
/// <param name="Tool">The tool, as the agent named it.</param>
/// <param name="Calls">How many times the agent called it, each counted as it began.</param>
/// <param name="Failed">How many of its calls ran and failed, each counted as it ended - a denied one never among them.</param>
/// <param name="Denied">How many times a call of it was denied, by the policy or the client's own.</param>
public sealed record FlowStepToolUse(string Tool, int Calls, int Failed, int Denied = 0)
{
    /// <summary>What is wrong with this use; null when nothing is.</summary>
    public string? Problem()
        => string.IsNullOrWhiteSpace(Tool) ? "a tool use names no tool"
            : Calls < 0 || Failed < 0 || Denied < 0 || Failed > Calls ? $"the tool '{Tool}' was called {Calls.ToString(CultureInfo.InvariantCulture)} time(s), {Failed.ToString(CultureInfo.InvariantCulture)} of them failing and {Denied.ToString(CultureInfo.InvariantCulture)} denied"
            : null;

    /// <summary>Two lists of uses as one, each tool's counts summed, in the order of the tools' names; null where both are.</summary>
    /// <param name="held">The uses held.</param>
    /// <param name="more">The uses to add.</param>
    public static IReadOnlyList<FlowStepToolUse>? Merged(IReadOnlyList<FlowStepToolUse>? held, IReadOnlyList<FlowStepToolUse>? more)
        => held is null ? more
            : more is null ? held
            : [.. held.Concat(more)
                .GroupBy(use => use.Tool, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .Select(group => new FlowStepToolUse(group.Key, group.Sum(use => use.Calls), group.Sum(use => use.Failed), group.Sum(use => use.Denied)))];
}

/// <summary>What the attempts of one instance used, summed: every token, every dollar, the turns and the uncounted reasons.</summary>
/// <param name="Tokens">The tokens every attempt used, by kind; null where none was reported.</param>
/// <param name="Usd">What they cost, as far as each part could be counted; null where nothing was counted.</param>
/// <param name="Uncounted">Why the rest could not be counted; null where every part was.</param>
/// <param name="Turns">The turns every agent attempt made; null where the instance ran no agent.</param>
public sealed record FlowStepTotals(
    AgentUsage? Tokens = null,
    double? Usd = null,
    string? Uncounted = null,
    int? Turns = null)
{
    /// <summary>The totals of <paramref name="attempts"/>, as the record holds them: each member null where no attempt holds any of it.</summary>
    public static FlowStepTotals Of(IReadOnlyList<FlowStepAttempt> attempts)
    {
        ArgumentNullException.ThrowIfNull(attempts);

        var tokens = attempts.Where(attempt => attempt.Tokens is not null).Select(attempt => attempt.Tokens!).Aggregate(AgentUsage.None, (sum, use) => sum.Plus(use));
        var usd = attempts.Sum(attempt => attempt.Usd ?? 0);
        var uncounted = attempts.Select(attempt => attempt.Uncounted).FirstOrDefault(reason => reason is not null);
        var turns = attempts.Sum(attempt => attempt.Turns ?? 0);

        return new FlowStepTotals(
            attempts.Any(attempt => attempt.Tokens is not null) ? tokens : null,
            attempts.Any(attempt => attempt.Usd is not null) ? usd : null,
            uncounted,
            turns > 0 ? turns : null);
    }
}
