namespace DssHarness.Core.Execution;

/// <summary>
/// Cancelling what may already be let go of: a source cancelled from a timer, a signal or another thread can be disposed
/// by the time it is cut, its work finished, and there is then nothing left to cut.
/// </summary>
internal static class Cancellations
{
    /// <summary>Cancels <paramref name="source"/> where it is still held; one already disposed is left as it is.</summary>
    /// <param name="source">The source.</param>
    public static void TryCancel(this CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // What it would have cancelled has finished.
        }
    }

    /// <summary>Cancels <paramref name="source"/> where it is still held, its callbacks run off this thread; one already disposed is left as it is.</summary>
    /// <param name="source">The source.</param>
    public static async Task TryCancelAsync(this CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        try
        {
            await source.CancelAsync().ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // What it would have cancelled has finished.
        }
    }
}
