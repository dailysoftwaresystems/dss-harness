namespace DssHarness.Core.Waiting;

/// <summary>
/// What one try came to, which decides whether the run tries again: its answer, a cause that passes by nature, a failure
/// that may change, or one no retry changes.
/// </summary>
/// <remarks>
/// The four kinds are the only ones: no constructor but a private one makes a try, so a kind declared elsewhere could
/// stand only on the copy constructor C# keeps open to a record derived anywhere, and <see cref="Retrying.RunAsync"/>
/// refuses such a try rather than read it as one of the four.
/// </remarks>
/// <typeparam name="T">What a try answers.</typeparam>
public abstract record RetryTry<T>
{
    private RetryTry()
    {
    }

    /// <summary>The try gave its answer, and the run ends <see cref="RetryEndKind.Done"/> with it.</summary>
    /// <param name="Value">The answer.</param>
    public sealed record Done(T Value) : RetryTry<T>;

    /// <summary>
    /// The try met a cause that passes by nature - a live holder, a 429 or a 503, ETXTBSY, LENGTH_MISMATCH, a provider's
    /// pause - and is tried again for as long as the cause lasts, never counted against anything: a cause that passes
    /// says nothing about whether the next try succeeds.
    /// </summary>
    /// <param name="Cause">The cause, as a line says it.</param>
    /// <param name="After">
    /// How long the cause says to wait - a <c>Retry-After</c>, a reset - waited exactly as given, a day as a day, and one
    /// already past as none; <see langword="null"/> where it says nothing, for the doubling wait
    /// (<see cref="RetryWatch.FirstWait"/>).
    /// </param>
    public sealed record Passing(string Cause, TimeSpan? After) : RetryTry<T>;

    /// <summary>
    /// The try failed in a way that may change, and is tried again after the doubling wait only while
    /// <see cref="Digest"/> is new to the run.
    /// </summary>
    /// <param name="Failure">The failure, as a line says it.</param>
    /// <param name="Digest">
    /// What the failure is, with the state it met: the same digest met again is the same failure on the same state,
    /// which no further try changes.
    /// </param>
    public sealed record Failed(string Failure, string Digest) : RetryTry<T>;

    /// <summary>The try failed in a way no retry changes - a 401, NXDOMAIN, a missing file - and the run ends at once.</summary>
    /// <param name="Failure">The failure, as a line says it.</param>
    public sealed record Final(string Failure) : RetryTry<T>;
}

/// <summary>What a run of tries is told as, how it paces the waits no cause names, how long the user lets it last, and its clock.</summary>
/// <param name="Subject">What the run tries for, as a line names it after <c>waits for</c>: <c>the forge's checks</c>.</param>
/// <param name="Tell">Told each wait before a try, as it begins and every <see cref="Waits.SaidEvery"/> while it lasts.</param>
/// <param name="FirstWait">
/// The first wait no cause names the length of - the caller's own pace, or a planned value - doubled each such wait up
/// to <see cref="Waits.SaidEvery"/>, then kept there: a cadence, never a bound, since the tries go on. More than nothing,
/// or the tries would follow each other without a pause; a first wait longer than <see cref="Waits.SaidEvery"/> is kept as
/// given, since the caller's pace is never shortened.
/// </param>
/// <param name="Bound">
/// The bound the user configured - a step's <c>timeoutMinutes</c>, the planned <c>waitMinutes</c>, a budget's minutes -
/// or <see langword="null"/> where none was. Weighed between tries, never cutting one short: a try that must end at the
/// bound waits through <see cref="Waits.ForAsync"/>.
/// </param>
/// <param name="Clock">Measures the run and times its waits.</param>
public sealed record RetryWatch(string Subject, Action<WaitTold> Tell, TimeSpan FirstWait, TimeSpan? Bound, TimeProvider Clock);

/// <summary>How a run of tries ended, what it gave, how many tries it made, and the last failure or cause it met.</summary>
/// <typeparam name="T">What a try answers.</typeparam>
/// <param name="Kind">How it ended.</param>
/// <param name="Value">The answer where it ended <see cref="RetryEndKind.Done"/>; the type's default otherwise.</param>
/// <param name="Tries">How many tries it made, numbered from 1.</param>
/// <param name="Failure">
/// The last failure or passing cause it met, the one it ended on where it ended on one; <see langword="null"/> where it met
/// none.
/// </param>
public sealed record RetryEnd<T>(RetryEndKind Kind, T? Value, int Tries, string? Failure);

/// <summary>How a run of tries ended.</summary>
public enum RetryEndKind
{
    /// <summary>A try gave its answer.</summary>
    Done,

    /// <summary>A try failed in a way no retry changes.</summary>
    Final,

    /// <summary>A try failed as an earlier one had, on the same state: the same digest twice.</summary>
    Repeated,

    /// <summary>The bound the user configured came before the next try.</summary>
    Bound,

    /// <summary>The caller's token ended it.</summary>
    Cancelled,
}
