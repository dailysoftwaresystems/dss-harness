namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The sentence a step that left no output is said with wherever the run fills the place its output would have filled -
/// a prompt's placeholder, the graph a reason step was to be served: the step, named as its reader names it, and why it
/// left nothing. The one owner of the sentence and of its reasons, which the engine reads off the instance it holds and
/// the graph's serving off the step's record, so the two never say the same ending two ways.
/// </summary>
public static class FlowNoOutput
{
    /// <summary>Why a step read as <c>previous</c> left nothing in the first iteration of its loop: there is none before it.</summary>
    public const string NoIterationBefore = "there is no iteration before this one";

    /// <summary>The sentence: <paramref name="step"/>, named as its reader names it, left no output, and <paramref name="why"/>.</summary>
    /// <param name="step">The step, as the reader names it.</param>
    /// <param name="why">Why it left none (<see cref="Why(FlowInstanceState?, bool, string?, string?, string?)"/>).</param>
    public static string Sentence(string step, string why) => $"(step '{step}' left no output: {why})";

    /// <summary>
    /// Why a step left no output, by the state its newest visit stands in - every state a step stands in: it has not run
    /// where none was made; skipped, failed - passed over or not - or never reached, each with what its record says;
    /// stopped before it ended; not ended yet where it has not started, runs or waits; and ended done having written none.
    /// </summary>
    /// <param name="state">The state of the step's newest visit; null where the run made none.</param>
    /// <param name="passedOver">Whether a failure of it was passed over.</param>
    /// <param name="skippedBecause">Why it was skipped, as its record says.</param>
    /// <param name="error">Its error, as its record says.</param>
    /// <param name="notRunBecause">Why the run never reached it, as its record says.</param>
    public static string Why(FlowInstanceState? state, bool passedOver, string? skippedBecause, string? error, string? notRunBecause)
        => state switch
        {
            null => "it has not run",
            FlowInstanceState.Skipped => $"skipped: {skippedBecause ?? "its when read false"}",
            FlowInstanceState.Failed when passedOver => $"it failed and was passed over: {error ?? "it failed"}",
            FlowInstanceState.Failed => $"it failed: {error ?? "it failed"}",
            FlowInstanceState.NotRun => $"the run never reached it: {notRunBecause ?? "its when read false"}",
            FlowInstanceState.Stopped => "it was stopped before it ended",
            FlowInstanceState.Done => "it ended done and wrote none",
            FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Paused => "it has not ended",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "a state no step stands in"),
        };

    /// <summary>Why the visit <paramref name="instance"/> left no output, as the engine holds it; null for none made.</summary>
    /// <param name="instance">The step's newest visit, or null where the run made none.</param>
    internal static string Why(FlowInstance? instance)
        => instance is null
            ? Why(state: null, passedOver: false, null, null, null)
            : Why(instance.State, instance.PassedOver, instance.SkippedBecause, instance.Error, instance.NotRunBecause);

    /// <summary>Why the step whose record is <paramref name="record"/> left no output, as the run's state holds it; null for none made.</summary>
    /// <param name="record">The record of the step's newest visit, or null where the run made none.</param>
    public static string Why(FlowStepRecord? record)
        => record is null
            ? Why(state: null, passedOver: false, null, null, null)
            : Why(
                FlowRecordNames.Of<FlowInstanceState>(record.State),
                string.Equals(record.Failure, FlowStepRecord.NameOf(FlowFailure.PassedOver), StringComparison.Ordinal),
                record.SkippedBecause,
                record.Error,
                record.NotRunBecause);
}
