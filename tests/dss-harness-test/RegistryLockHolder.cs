using System.Runtime.ExceptionServices;
using RepoHarness.Core.Anchors;

namespace RepoHarness.Tests;

/// <summary>
/// Holds the lock on a pair of anchor registries from a thread of its own, as another process would, until disposed. A
/// failure on that thread - the lock not opening, say - is raised to the test, never left on a thread nothing watches,
/// where it would end the test host.
/// </summary>
internal sealed class RegistryLockHolder : IDisposable
{
    private readonly ManualResetEventSlim _held = new();
    private readonly ManualResetEventSlim _release = new();
    private readonly Thread _thread;
    private Exception? _failure;

    private RegistryLockHolder(IAnchorRegistryLock registryLock, AnchorRegistries registries)
    {
        _thread = new Thread(() =>
        {
            try
            {
                registryLock.RunExclusive(registries, () =>
                {
                    _held.Set();
                    _release.Wait(TimeSpan.FromSeconds(60));
                    return 0;
                });
            }
            catch (Exception ex)
            {
                _failure = ex;
                _held.Set();
            }
        });
    }

    /// <summary>Takes the lock on another thread, and returns once it holds it.</summary>
    public static RegistryLockHolder Take(IAnchorRegistryLock registryLock, AnchorRegistries registries, CancellationToken cancellationToken)
    {
        var holder = new RegistryLockHolder(registryLock, registries);
        holder._thread.Start();

        if (!holder._held.Wait(TimeSpan.FromSeconds(60), cancellationToken) || holder._failure is not null)
        {
            holder.Dispose();
            Assert.Fail("The holder never took the lock.");
        }

        return holder;
    }

    /// <summary>Releases the lock, and raises whatever failed on the holder's thread.</summary>
    public void Dispose()
    {
        _release.Set();
        _thread.Join();
        _held.Dispose();
        _release.Dispose();

        if (_failure is { } failure)
        {
            _failure = null;
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }
}
