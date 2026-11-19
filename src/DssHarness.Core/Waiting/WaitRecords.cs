using DssHarness.Core.Execution;

namespace DssHarness.Core.Waiting;

/// <summary>What a wait is told as, how long the user lets it last, and the clock it is measured on.</summary>
/// <param name="Subject">
/// What is waited for, as a line names it after <c>waits for</c>: <c>the run's lock</c>, <c>vcvarsall to end</c>.
/// </param>
/// <param name="Tell">Told where the wait stands, each time in the one form <see cref="WaitTold.Line"/> gives.</param>
/// <param name="Bound">
/// The bound the user configured - a step's <c>timeoutMinutes</c>, <c>stallSeconds</c>, a budget's minutes - or
/// <see langword="null"/> where none was, and the wait then lasts as long as what it waits for. Never a number of the
/// code's own: a slow thing is waited for, and only the person says how long is too long.
/// </param>
/// <param name="Clock">Measures the wait and times its pauses.</param>
public sealed record WaitWatch(string Subject, Action<WaitTold> Tell, TimeSpan? Bound, TimeProvider Clock);

/// <summary>Where a wait stands as it is told: what it waits for, how long it has, who holds it, and what it has to add.</summary>
/// <param name="Subject">What is waited for (<see cref="WaitWatch.Subject"/>).</param>
/// <param name="Waited">How long the wait has lasted so far.</param>
/// <param name="Holder">Who holds what is waited for, as a line names it; <see langword="null"/> where no holder is named.</param>
/// <param name="Progress">
/// What the wait has to add - why it waits and when it tries again, or what became of the holder;
/// <see langword="null"/> where it has nothing to add.
/// </param>
public sealed record WaitTold(string Subject, TimeSpan Waited, string? Holder, string? Progress)
{
    /// <summary>
    /// The one sentence every wait is told in, lowercase and without a full stop, as a progress line that its command
    /// names is: <c>waits for the run's lock, held by pid 4242 (dssharness run)</c> as the wait begins, and
    /// <c>has waited 5m00s for vcvarsall to end</c> once it has lasted, the time as the ledger's table says it. The holder
    /// follows after a comma and the progress after a colon, where there are any:
    /// <c>has waited 2s for the forge's checks: it answered 503; tries again in 4s</c>. A wait said as no time - none, or
    /// under the half second a line rounds to <c>0s</c> - is said as beginning, since <c>has waited 0s</c> says nothing.
    /// A method, not a property, so a record's printing of its members never prints it.
    /// </summary>
    public string Line()
    {
        var said = LedgerReport.FormatDuration(Waited);
        var lead = said is "" or "0s" ? "waits for" : $"has waited {said} for";
        var held = Holder is null ? string.Empty : $", held by {Holder}";
        var progress = Progress is null ? string.Empty : $": {Progress}";

        return $"{lead} {Subject}{held}{progress}";
    }
}

/// <summary>Who holds what a wait waits for: its process, what tells that process from a later one, and its name.</summary>
/// <param name="ProcessId">The holder's process id, as its record gives it.</param>
/// <param name="Stamp">
/// What tells that process from a later one given its id (<see cref="IProcessIdentity.Current"/>), as its record gives it;
/// <see langword="null"/> where the record has none.
/// </param>
/// <param name="Name">The holder as a line names it: <c>pid 4242 (dssharness run)</c>.</param>
public sealed record WaitHolder(int ProcessId, string? Stamp, string Name);

/// <summary>How a wait ended, what it gave, and how long it lasted.</summary>
/// <typeparam name="T">What the wait gives.</typeparam>
/// <param name="Kind">How it ended.</param>
/// <param name="Value">
/// What it gave where it ended <see cref="WaitEndKind.Done"/>: the work's answer, or, for a wait on a holder, whether the
/// holder was found ended (<see cref="Waits.WhileHeldAsync"/>). The type's default where it ended otherwise.
/// </param>
/// <param name="Waited">How long it lasted.</param>
public sealed record WaitEnd<T>(WaitEndKind Kind, T? Value, TimeSpan Waited);

/// <summary>How a wait ended.</summary>
public enum WaitEndKind
{
    /// <summary>What it waited for came: the work ended, or the holder let go or ended.</summary>
    Done,

    /// <summary>The bound the user configured came first.</summary>
    Bound,

    /// <summary>The caller's token ended it.</summary>
    Cancelled,
}
