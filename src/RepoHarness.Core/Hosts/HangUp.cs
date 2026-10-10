using System.Runtime.InteropServices;

namespace RepoHarness.Core.Hosts;

/// <summary>A session's hang-up, taken as a request to stop rather than as the end of the process where it stands.</summary>
public static class HangUp
{
    /// <summary>
    /// From now until what is returned is disposed, a hang-up of this process's session - SIGHUP, or the console closing
    /// on Windows - cancels <paramref name="source"/> and does not end the process: what it started is stopped by the
    /// cancellation, where ending at once would leave it running under nobody.
    /// </summary>
    /// <param name="source">What a hang-up cancels.</param>
    public static IDisposable Cancels(CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);

        return PosixSignalRegistration.Create(PosixSignal.SIGHUP, signal =>
        {
            signal.Cancel = true;

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // What it would have cancelled has finished.
            }
        });
    }
}
