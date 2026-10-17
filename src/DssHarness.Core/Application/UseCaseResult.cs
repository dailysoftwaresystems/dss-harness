using DssHarness.Core.Results;

namespace DssHarness.Core.Application;

/// <summary>
/// The answer of a use case: what the command answers, and what the same answer knows beyond its lines. The typed
/// boundary between a command's outcome and the facts it carries - a front end, or a step of a run, reads the one record
/// the outcome was composed with and never the text.
/// </summary>
/// <typeparam name="T">The facts the use case attaches to the outcomes it answers with.</typeparam>
/// <param name="Outcome">What the command answers: its code, its summary and its lines.</param>
public sealed record UseCaseResult<T>(CommandOutcome Outcome)
    where T : class
{
    /// <summary>The facts the outcome carries, where it carries them; <see langword="null"/> where it carries none.</summary>
    public T? Facts => Outcome.Facts as T;

    /// <summary>The typed answer of an outcome: its facts read as the use case's own.</summary>
    /// <param name="outcome">The outcome, carrying facts or none.</param>
    /// <returns>The outcome, as its use case's answer.</returns>
    public static UseCaseResult<T> From(CommandOutcome outcome) => new(outcome);
}
