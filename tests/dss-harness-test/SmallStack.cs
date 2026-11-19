using System.Runtime.ExceptionServices;

namespace DssHarness.Tests;

/// <summary>
/// Runs work on a thread of a small stack, as a thread is given by default on Windows, whatever stack the system gave the
/// first thread. A depth test's child (<see cref="ChildRun"/>) does its reading here, so a reading that recursed once per
/// level overflows on every system alike: a main thread's own stack is eight megabytes on Linux and macOS, and a recursion
/// of 100,000 levels of small frames could fit in it there and pass.
/// </summary>
internal static class SmallStack
{
    /// <summary>
    /// The thread's stack, a megabyte: room for any iterative work and for the runtime's own beneath it, and none for a
    /// recursion of the 100,000 levels a depth test reads, which would need frames of about ten bytes.
    /// </summary>
    internal const int Bytes = 1024 * 1024;

    /// <summary>What <paramref name="work"/> answers, run on a thread of <see cref="Bytes"/> of stack; what it throws is thrown here, as it was thrown.</summary>
    /// <param name="work">The work.</param>
    internal static T Run<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var answer = default(T);
        ExceptionDispatchInfo? thrown = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    answer = work();
                }
                catch (Exception ex)
                {
                    thrown = ExceptionDispatchInfo.Capture(ex);
                }
            },
            Bytes);

        thread.Start();
        thread.Join();
        thrown?.Throw();

        return answer!;
    }
}
