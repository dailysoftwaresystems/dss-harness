using System.Globalization;
using DssHarness.Core.Walks;

namespace DssHarness.Tests;

/// <summary>
/// A step repeated on a state until the loop ends of itself - a step that says it is done, a state that stops changing,
/// a state that comes back - and never at a count of steps.
/// </summary>
public sealed class FixpointTests
{
    /// <summary>A state's digest: its number as written.</summary>
    private static string Digest(int state) => state.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// A state that comes back - 0, 1, 2, then 0 again - ends the loop there as a cycle: every state's digest is kept, so
    /// the loop is never turned round again - the step throws past a thousand steps, which only a loop that does not
    /// know the states it met would reach.
    /// </summary>
    [Fact]
    public void AStateThatRepeats_IsACycle()
    {
        var steps = 0;

        var end = Fixpoint.Run(0, state => ++steps > 1000 ? throw new InvalidOperationException("the cycle was turned round again") : new FixpointStep<int>((state + 1) % 3, Done: false), Digest);

        Assert.Equal(new FixpointEnd<int>(0, 3, Done: false, Cycled: true), end);
        Assert.Equal(3, steps);
    }

    /// <summary>
    /// A step that says the loop is done ends it at the state it gives, though the states would go on changing - the
    /// step throws past a thousand steps, which only a loop that does not hear it would reach.
    /// </summary>
    [Fact]
    public void AStepThatSaysDone_EndsTheLoop()
    {
        var steps = 0;

        var end = Fixpoint.Run(0, state => ++steps > 1000 ? throw new InvalidOperationException("the step's verdict was not heard") : new FixpointStep<int>(state + 1, Done: state + 1 == 5), Digest);

        Assert.Equal(new FixpointEnd<int>(5, 5, Done: true, Cycled: false), end);
    }

    /// <summary>
    /// A step that leaves the state as it was ends the loop at that state, the fixpoint itself: neither done nor a cycle.
    /// </summary>
    [Fact]
    public void AStateThatStopsChanging_IsTheFixpoint()
    {
        var end = Fixpoint.Run(0, state => new FixpointStep<int>(Math.Min(state + 1, 3), Done: false), Digest);

        Assert.Equal(new FixpointEnd<int>(3, 4, Done: false, Cycled: false), end);
    }

    /// <summary>
    /// The loop whose step waits ends as the one that does not - a cycle, the step's verdict - and is stopped by its token
    /// before the next step: cancelled in the second step, it runs no third.
    /// </summary>
    [Fact]
    public async Task RunAsync_EndsAsRunDoes_AndIsCancelledBeforeTheNextStep()
    {
        var token = TestContext.Current.CancellationToken;

        var turns = 0;

        // Each loop's step throws past a thousand steps, so a loop that did not end of itself fails here rather than running for ever.
        var cycled = await Fixpoint.RunAsync(0, (state, _) => ++turns > 1000 ? throw new InvalidOperationException("the cycle was turned round again") : Task.FromResult(new FixpointStep<int>((state + 1) % 3, Done: false)), Digest, token);
        var done = await Fixpoint.RunAsync(0, (state, _) => ++turns > 1000 ? throw new InvalidOperationException("the step's verdict was not heard") : Task.FromResult(new FixpointStep<int>(state + 1, Done: state + 1 == 5)), Digest, token);

        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var steps = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() => Fixpoint.RunAsync(
            0,
            (state, _) =>
            {
                if (++steps == 2)
                {
                    cancellation.Cancel();
                }

                return steps > 1000 ? throw new InvalidOperationException("the token was not looked at") : Task.FromResult(new FixpointStep<int>(state + 1, Done: false));
            },
            Digest,
            cancellation.Token));

        Assert.Equal(new FixpointEnd<int>(0, 3, Done: false, Cycled: true), cycled);
        Assert.Equal(new FixpointEnd<int>(5, 5, Done: true, Cycled: false), done);
        Assert.Equal(8, turns);
        Assert.Equal(2, steps);
    }
}
