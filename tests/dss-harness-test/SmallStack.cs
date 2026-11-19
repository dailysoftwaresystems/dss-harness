using System.Runtime.ExceptionServices;

namespace DssHarness.Tests;

/// <summary>
/// Runs a child's work (<see cref="ChildRun"/>) on a thread of a small stack, as a thread is given by default on Windows,
/// whatever stack the system gave the child's first thread: a walk that recursed once per level of its input passes it a
/// few thousand levels in, on every system alike, so a test of a deep input goes red on such a revert wherever it runs.
/// </summary>
internal static class SmallStack
{
    /// <summary>The thread's stack: a megabyte.</summary>
    internal const int Bytes = 1024 * 1024;

    /// <summary>What <paramref name="work"/> answers, run on a thread of <see cref="Bytes"/>; what it throws is thrown here, as thrown.</summary>
    /// <param name="work">The child's work.</param>
    internal static string Run(Func<string> work)
    {
        string? said = null;
        ExceptionDispatchInfo? thrown = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    said = work();
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

        return said!;
    }
}
