using DssHarness.Core.Results;

namespace DssHarness.Core.Application;

/// <summary>
/// The answer of a use case: what the command answers, and what the same answer knows beyond its lines. The typed
/// boundary between a command's outcome and the facts it carries - a front end, or a step of a run, reads the one record
/// the outcome was composed with and never the text.
/// </summary>
/// <typeparam name="T">The facts the use case attaches to the outcomes it answers with.</typeparam>
public sealed record UseCaseResult<T>
    where T : class
{
    private UseCaseResult(CommandOutcome outcome, T? facts)
    {
        Outcome = outcome;
        Facts = facts;
    }

    /// <summary>What the command answers: its code, its summary and its lines.</summary>
    public CommandOutcome Outcome { get; }

    /// <summary>The facts the outcome carries, where it carries them; <see langword="null"/> where it carries none.</summary>
    public T? Facts { get; }

    /// <summary>The typed answer of an outcome: its facts read as the use case's own.</summary>
    /// <param name="outcome">The outcome, carrying facts or none.</param>
    /// <returns>The outcome, as its use case's answer.</returns>
    /// <exception cref="InvalidOperationException">
    /// The outcome carries facts of another type than the use case's: a defect where it was composed, said rather than
    /// read as an answer that carries none.
    /// </exception>
    public static UseCaseResult<T> From(CommandOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(outcome);

        return outcome.Facts switch
        {
            null => new(outcome, null),
            T facts => new(outcome, facts),
            var other => throw new InvalidOperationException(
                $"The outcome '{outcome.Message}' carries facts of type '{other.GetType().Name}', and its use case answers with '{typeof(T).Name}'."),
        };
    }
}
