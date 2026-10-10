using System.Runtime.InteropServices;

namespace RepoHarness.Core.Hosts;

/// <summary>
/// A session's hang-up, taken as a request to stop rather than as the end of the process where it stands - where the
/// system leaves that to the process, which Linux and macOS do.
/// </summary>
public static class HangUp
{
    /// <summary>
    /// From now until what is returned is disposed, a hang-up of this process's session - SIGHUP - cancels
    /// <paramref name="source"/> and does not end the process: what it started is stopped by the cancellation, where
    /// ending at once would leave it running under nobody.
    /// </summary>
    /// <param name="source">What a hang-up cancels.</param>
    /// <remarks>
    /// On Windows the sign is the console closing, and the process ends all the same: the system ends it as soon as the
    /// sign is answered, or five seconds on, with every process in that console. What the cancellation stops there is
    /// only what stops as it is cancelled, so nothing is promised of it: a host there is told its asker has gone by the
    /// end of its input, and by its beat.
    /// </remarks>
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
