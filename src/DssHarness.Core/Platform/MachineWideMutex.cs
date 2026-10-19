using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using DssHarness.Core.Results;

namespace DssHarness.Core.Platform;

/// <summary>
/// The machine-wide lock two processes of this tool contend for.
/// </summary>
/// <remarks>
/// A named mutex is the lock .NET offers on Windows, Linux and macOS alike, and everything here that
/// must serialise a read-decide-write across processes takes one: the anchor registries, the run
/// lock, the log owner, the change of an installed program. One implementation, because the two
/// things that go wrong with a mutex — a name that two different subjects share, and an abandoned
/// one read as a failure — go wrong the same way wherever it is used, and a second copy would only
/// fix one of them.
/// </remarks>
public static class MachineWideMutex
{
    /// <summary>How many times opening a mutex is tried before it is given up on.</summary>
    internal const int Attempts = 5;

    /// <summary>How long the first retry waits; each after it waits that much longer than the one before.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromMilliseconds(25);

    /// <summary>
    /// The name one subject contends under, however its paths are spelled.
    /// </summary>
    /// <param name="prefix">What kind of subject this is, so two kinds never share a name.</param>
    /// <param name="paths">The paths the subject is, in any order.</param>
    /// <param name="ignoreCase">
    /// Whether the paths' case is part of their identity. False on a filesystem that distinguishes
    /// case; true elsewhere, where two spellings name one file and must therefore take one lock.
    /// </param>
    /// <remarks>
    /// Global, so a run started in another terminal session contends for the same subject rather
    /// than quietly getting a lock of its own.
    /// </remarks>
    public static string NameFor(string prefix, IEnumerable<string> paths, bool ignoreCase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentNullException.ThrowIfNull(paths);

        var key = string.Join('\n', paths
            .Select(Path.GetFullPath)
            .Select(path => ignoreCase ? path.ToUpperInvariant() : path)
            .Order(StringComparer.Ordinal));

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));

        return $@"Global\dss-harness-{prefix}-" + Convert.ToHexStringLower(hash, 0, 16);
    }

    /// <summary>Opens the named mutex.</summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal.</param>
    /// <exception cref="HarnessException">
    /// The mutex belongs to another user on this machine, or could not be opened in <see cref="Attempts"/>
    /// tries. Reported rather than worked around: the alternative is proceeding without the lock, which is
    /// the state the lock exists to prevent.
    /// </exception>
    /// <remarks>
    /// An open the system failed with an I/O error is tried again, <see cref="Attempts"/> times in all and a
    /// little later each time. The one measured is a race: on Linux and macOS the runtime keeps a named
    /// mutex under a directory it creates the first time one is opened, and processes opening theirs at
    /// that moment race to create it. In WSL, 16 processes started together on a fresh
    /// <c>/tmp/.dotnet</c>, twenty times over, failed 54 and then 66 of their 320 openings, the runtime
    /// saying a stat of the directory found nothing - a consumer saw its mkdir find one already there -
    /// and none with the retry: the loser finds the directory the next time it looks. Without it, the leg
    /// whose run lost the race ended with an internal error, reported as a host that could not be reached.
    /// An error that lasts - a full disk, a directory nobody may write - costs the tries, a quarter of a
    /// second, and is then said as unavailable rather than as a refusal: this machine cannot run anything
    /// until it opens, and a machine running a leg for another is one host of many, whose trouble is its own.
    /// </remarks>
    public static Mutex Open(string name, string subject)
        => Open(name, subject, () => new Mutex(initiallyOwned: false, name), Thread.Sleep);

    /// <summary>Opens a mutex through <paramref name="create"/>, pausing through <paramref name="pause"/> between tries.</summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal.</param>
    /// <param name="create">Opens the mutex.</param>
    /// <param name="pause">Waits between two tries.</param>
    /// <exception cref="HarnessException">The mutex belongs to another user, or could not be opened in <see cref="Attempts"/> tries.</exception>
    internal static Mutex Open(string name, string subject, Func<Mutex> create, Action<TimeSpan> pause)
    {
        ArgumentNullException.ThrowIfNull(create);
        ArgumentNullException.ThrowIfNull(pause);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return create();
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"The lock '{name}' on {subject} belongs to another user on this machine, so nothing it guards was read or changed.",
                    ex);
            }
            catch (IOException) when (attempt < Attempts)
            {
                pause(Backoff * attempt);
            }
            catch (IOException ex)
            {
                throw new HarnessException(
                    HarnessExit.HostUnavailable,
                    $"The lock '{name}' on {subject} could not be opened in {Attempts} tries, the last saying: "
                    + $"{ex.Message.TrimEnd('.')}. Nothing it guards was read or changed, and nothing can run on this machine until "
                    + "it opens: fix what the system said, and run the command again.",
                    ex);
            }
        }
    }

    /// <summary>Waits for the mutex, treating an abandoned one as taken.</summary>
    /// <param name="mutex">The mutex.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <returns>Whether it was taken, and so must be released.</returns>
    /// <remarks>
    /// <para>
    /// A previous holder that exited while holding it passes ownership to this thread, which is a
    /// success and not a failure: everything this lock protects is written by replacing a whole
    /// file, so nothing it left can be half written.
    /// </para>
    /// <para>
    /// The time is kept by a clock that never steps. On Linux the runtime ends a wait for a named mutex at a deadline on
    /// the wall clock - a robust pthread mutex's timed lock takes no other - and a WSL clock, measured stepping back by
    /// 24.8 seconds and forward again by as much every five seconds, passes that deadline at once: a wait half a second
    /// old ended as though the whole window had passed, and the update was refused as one another process had held the
    /// file through. A wait ended early is waited again for what is left. A step back lengthens a wait by as much, which
    /// only delays giving up on a mutex nobody lets go of.
    /// </para>
    /// </remarks>
    public static bool Wait(Mutex mutex, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(mutex);

        var started = Stopwatch.GetTimestamp();

        return Wait(
            slice =>
            {
                try
                {
                    return mutex.WaitOne(slice);
                }
                catch (AbandonedMutexException)
                {
                    return true;
                }
            },
            timeout,
            () => Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Takes the mutex <paramref name="name"/> where nobody holds it, for work that awaits while it holds it; or
    /// <see langword="null"/> at once where another holds it.
    /// </summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal to open it.</param>
    /// <returns>The hold, which lets the mutex go when it is disposed, from whichever thread disposes it.</returns>
    /// <exception cref="HarnessException">The mutex could not be opened, as <see cref="Open(string, string)"/> says.</exception>
    /// <remarks>
    /// A mutex is released by the thread that took it, and work that awaits may go on on another, so the hold keeps
    /// a thread of its own that takes the mutex, waits to be let go, and releases it (<see cref="MachineWideHold"/>).
    /// A mutex whose holder ended holding it is taken, as <see cref="Wait(Mutex, TimeSpan)"/> takes one.
    /// </remarks>
    public static MachineWideHold? TryHold(string name, string subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return MachineWideHold.TryTake(() => Open(name, subject));
    }

    /// <summary>Waits through <paramref name="waitFor"/>, for as long as <paramref name="timeout"/> allows by <paramref name="elapsed"/>.</summary>
    /// <param name="waitFor">Waits for the mutex for at most the time it is given, and says whether it was taken.</param>
    /// <param name="timeout">How long to wait.</param>
    /// <param name="elapsed">How long has passed since the wait began.</param>
    /// <returns>Whether it was taken.</returns>
    internal static bool Wait(Func<TimeSpan, bool> waitFor, TimeSpan timeout, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(waitFor);
        ArgumentNullException.ThrowIfNull(elapsed);

        while (true)
        {
            var left = timeout - elapsed();

            if (waitFor(left > TimeSpan.Zero ? left : TimeSpan.Zero))
            {
                return true;
            }

            if (elapsed() >= timeout)
            {
                return false;
            }
        }
    }
}

/// <summary>
/// A machine-wide mutex held for work that awaits: taken and released by a thread of the hold's own, which keeps it until
/// the hold is disposed.
/// </summary>
/// <remarks>
/// A mutex belongs to the thread that took it, which alone can release it, and the continuation of an await may run on any
/// thread: a mutex taken before an await and released after it is released by a thread that does not hold it, which
/// throws and leaves the mutex held until the process ends. So the thread that takes it here does nothing else, and
/// releases it when it is let go. It is a background thread, so a process that ends holding the mutex is not kept
/// alive by it: the system then passes the mutex on as one its holder ended holding, and the next to wait takes it.
/// </remarks>
public sealed class MachineWideHold : IDisposable
{
    private readonly Thread _holder;
    private readonly ManualResetEventSlim _letGo;
    private int _released;

    private MachineWideHold(Thread holder, ManualResetEventSlim letGo)
    {
        _holder = holder;
        _letGo = letGo;
    }

    /// <summary>Whether the hold has been let go, after which it guards nothing.</summary>
    public bool Released => Volatile.Read(ref _released) != 0;

    /// <summary>Lets the mutex go, and returns once it has been released.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0)
        {
            return;
        }

        _letGo.Set();
        _holder.Join();
        _letGo.Dispose();
    }

    /// <summary>
    /// Opens a mutex through <paramref name="open"/> on a thread of its own and takes it where nobody holds it; or
    /// <see langword="null"/> where another does.
    /// </summary>
    /// <param name="open">Opens the mutex, refusing as <see cref="MachineWideMutex.Open(string, string)"/> does.</param>
    /// <exception cref="HarnessException">The mutex could not be opened.</exception>
    internal static MachineWideHold? TryTake(Func<Mutex> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        var taken = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() => Hold(open, taken, letGo))
        {
            IsBackground = true,
            Name = "machine-wide hold",
        };

        holder.Start();

        try
        {
            if (taken.Task.GetAwaiter().GetResult())
            {
                return new MachineWideHold(holder, letGo);
            }
        }
        catch
        {
            holder.Join();
            letGo.Dispose();
            throw;
        }

        holder.Join();
        letGo.Dispose();

        return null;
    }

    /// <summary>
    /// The holding thread: opens the mutex, says whether it took it, and where it did, keeps it until it is let go.
    /// </summary>
    private static void Hold(Func<Mutex> open, TaskCompletionSource<bool> taken, ManualResetEventSlim letGo)
    {
        Mutex mutex;

        try
        {
            mutex = open();
        }
        catch (Exception ex)
        {
            taken.SetException(ex);
            return;
        }

        using (mutex)
        {
            bool held;

            try
            {
                held = MachineWideMutex.Wait(mutex, TimeSpan.Zero);
            }
            catch (Exception ex)
            {
                taken.SetException(ex);
                return;
            }

            taken.SetResult(held);

            if (held)
            {
                letGo.Wait();
                mutex.ReleaseMutex();
            }
        }
    }
}
