namespace DssHarness.Core.Walks;

/// <summary>
/// Repeats a step on a state until the loop ends of itself: where a step says it is done, where a step leaves the state
/// as it was, or where a state comes back - its digest one met before - which is a cycle. No count of steps ends it: every
/// state's digest is kept, so a loop that would turn for ever ends at its first repeat, and one that only goes on runs as
/// long as it changes something new.
/// </summary>
public static class Fixpoint
{
    /// <summary>Runs <paramref name="step"/> from <paramref name="start"/> until the loop ends of itself.</summary>
    /// <typeparam name="TState">The state the step moves.</typeparam>
    /// <param name="start">The state the loop begins from.</param>
    /// <param name="step">One step: the next state, and whether the step says the loop is done.</param>
    /// <param name="digestOf">What tells two states apart: two states of one digest are one state.</param>
    public static FixpointEnd<TState> Run<TState>(TState start, Func<TState, FixpointStep<TState>> step, Func<TState, string> digestOf)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(digestOf);

        var loop = new FixpointLoop<TState>(start, digestOf);

        while (true)
        {
            if (loop.Take(step(loop.State)) is { } end)
            {
                return end;
            }
        }
    }

    /// <summary>Runs <paramref name="step"/>, which waits, from <paramref name="start"/> as <see cref="Run"/> runs one.</summary>
    /// <typeparam name="TState">The state the step moves.</typeparam>
    /// <param name="start">The state the loop begins from.</param>
    /// <param name="step">One step: the next state, and whether the step says the loop is done.</param>
    /// <param name="digestOf">What tells two states apart: two states of one digest are one state.</param>
    /// <param name="cancellationToken">Stops the loop before its next step, and is handed to <paramref name="step"/>.</param>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled before the loop ended.</exception>
    public static async Task<FixpointEnd<TState>> RunAsync<TState>(TState start, Func<TState, CancellationToken, Task<FixpointStep<TState>>> step, Func<TState, string> digestOf, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(digestOf);

        var loop = new FixpointLoop<TState>(start, digestOf);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (loop.Take(await step(loop.State, cancellationToken).ConfigureAwait(false)) is { } end)
            {
                return end;
            }
        }
    }
}

/// <summary>What one step of a loop gives (<see cref="Fixpoint"/>).</summary>
/// <typeparam name="TState">The state the step moves.</typeparam>
/// <param name="State">The state after the step.</param>
/// <param name="Done">Whether the step says the loop is done - its own verdict, such as a change applied - which ends it at this state.</param>
public sealed record FixpointStep<TState>(TState State, bool Done);

/// <summary>
/// How a loop ended (<see cref="Fixpoint"/>): where a step said it was done, where a state came back, or - neither - where
/// a step left the state as it was, the fixpoint itself.
/// </summary>
/// <typeparam name="TState">The state the steps moved.</typeparam>
/// <param name="State">The state the loop ended at: the last step's.</param>
/// <param name="Steps">How many steps ran.</param>
/// <param name="Done">Whether the last step said the loop was done.</param>
/// <param name="Cycled">Whether the last step came back to a state met before the one it stepped from: a cycle, which no further step would leave.</param>
public sealed record FixpointEnd<TState>(TState State, int Steps, bool Done, bool Cycled);

/// <summary>
/// A loop as far as it has run (<see cref="Fixpoint"/>): its state, its steps and every state's digest - the one place
/// the rule of a loop's end is kept, for the loop that waits and the one that does not.
/// </summary>
/// <typeparam name="TState">The state the steps move.</typeparam>
internal sealed class FixpointLoop<TState>
{
    private readonly Func<TState, string> _digestOf;
    private readonly HashSet<string> _met = new(StringComparer.Ordinal);
    private string _digest;
    private int _steps;

    /// <summary>A loop at <paramref name="start"/>, no step run.</summary>
    public FixpointLoop(TState start, Func<TState, string> digestOf)
    {
        _digestOf = digestOf;
        State = start;
        _digest = digestOf(start);
        _met.Add(_digest);
    }

    /// <summary>The state the next step moves.</summary>
    public TState State { get; private set; }

    /// <summary>
    /// Takes one step's answer: the loop's end where the step said it was done, left the state as it was or came back to a
    /// state met before, and <see langword="null"/> where it moved to a state not met yet, which the next step moves.
    /// </summary>
    public FixpointEnd<TState>? Take(FixpointStep<TState> step)
    {
        ArgumentNullException.ThrowIfNull(step);

        _steps++;

        if (step.Done)
        {
            return new FixpointEnd<TState>(step.State, _steps, Done: true, Cycled: false);
        }

        var digest = _digestOf(step.State);

        if (string.Equals(digest, _digest, StringComparison.Ordinal))
        {
            return new FixpointEnd<TState>(step.State, _steps, Done: false, Cycled: false);
        }

        if (!_met.Add(digest))
        {
            return new FixpointEnd<TState>(step.State, _steps, Done: false, Cycled: true);
        }

        State = step.State;
        _digest = digest;
        return null;
    }
}
