using System.Collections.Concurrent;
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
    /// success and not a failure: a holder that ended can never let it go, so refusing would lock its
    /// subject until the machine restarts. Taking it promises nothing of what that holder left, which is
    /// each subject's own to tell: a registry, a record, a ledger or an installed program is written by
    /// replacing a whole file, so nothing of theirs is half written; the main tree's lock guards work of
    /// many files, and a fold or a command that ended part way left the tree as it stopped, the fold's
    /// own records saying what it wrote (<c>MainTreeWriteLock</c>).
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

    /// <summary>
    /// Takes the mutex <paramref name="name"/> on a thread of its own, waiting as long as <paramref name="timeout"/>
    /// allows and <paramref name="cancellationToken"/> leaves it, for work that awaits while it holds it.
    /// </summary>
    /// <param name="name">The name, from <see cref="NameFor"/>.</param>
    /// <param name="subject">What is being locked, named in a refusal to open it.</param>
    /// <param name="timeout">How long the whole wait may last; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for as long as the token leaves it.</param>
    /// <param name="cancellationToken">Ends the wait where it has not taken the mutex.</param>
    /// <returns>
    /// The hold, which says how long it waited (<see cref="MachineWideHold.Waited"/>) and lets the mutex go when it is
    /// disposed, from whichever thread disposes it; or <see langword="null"/> where the wait ended - the token fired, or
    /// the whole of <paramref name="timeout"/> passed - without taking it.
    /// </returns>
    /// <exception cref="HarnessException">The mutex could not be opened, as <see cref="Open(string, string)"/> says.</exception>
    /// <remarks>
    /// A mutex is released by the thread that took it, and work that awaits may go on on another, so the hold keeps
    /// a thread of its own that takes the mutex, waits to be let go, and releases it (<see cref="MachineWideHold"/>).
    /// That thread blocks for the whole of the wait, a slice of at most a second at a time, so the token is seen within a
    /// second of firing; the caller awaits the hold, so nothing of the pool is held while it waits. A mutex whose holder
    /// ended holding it is taken, as <see cref="Wait(Mutex, TimeSpan)"/> takes one.
    /// </remarks>
    public static Task<MachineWideHold?> HoldAsync(string name, string subject, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);

        return MachineWideHold.TakeAsync(name, () => Open(name, subject), timeout, cancellationToken);
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
    /// <summary>How long a slice of a waiting take waits before the take looks again at its token and its bound.</summary>
    internal static readonly TimeSpan Slice = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The takes a test waits to see begin waiting, by the mutex's name: empty outside tests, so a take that met a holder
    /// finds nothing to tell (<see cref="WaitBegins"/>).
    /// </summary>
    private static readonly ConcurrentDictionary<string, TaskCompletionSource> Watched = new(StringComparer.Ordinal);

    private readonly Thread _holder;
    private readonly ManualResetEventSlim _letGo;
    private int _released;

    private MachineWideHold(Thread holder, ManualResetEventSlim letGo, TimeSpan waited)
    {
        _holder = holder;
        _letGo = letGo;
        Waited = waited;
    }

    /// <summary>
    /// How long it took to take the mutex: zero where its first look took it, held by nobody; where that look met a
    /// holder, the time from it to the take, never zero - so a wait however short is told from none.
    /// </summary>
    public TimeSpan Waited { get; }

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
    /// Completes once a take of the mutex <paramref name="name"/> has met a holder and begins to wait for it: what a test
    /// awaits before it lets its own hold go, so the take it lets in has waited whatever the machine's load, never a span
    /// of the clock. Asked before the take begins, and answered once, by the first take of that name to wait.
    /// </summary>
    /// <param name="name">The mutex's name, from <see cref="MachineWideMutex.NameFor"/>.</param>
    internal static Task WaitBegins(string name)
        => Watched.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

    /// <summary>
    /// Opens a mutex through <paramref name="open"/> on a thread of its own and takes it where nobody holds it; or
    /// <see langword="null"/> where another does.
    /// </summary>
    /// <param name="open">Opens the mutex, refusing as <see cref="MachineWideMutex.Open(string, string)"/> does.</param>
    /// <exception cref="HarnessException">The mutex could not be opened.</exception>
    /// <remarks>
    /// It blocks on the holding thread's own answer, which that thread gives without the pool: a take that never waits
    /// answers in the moment its thread looks, however busy the pool is.
    /// </remarks>
    internal static MachineWideHold? TryTake(Func<Mutex> open)
    {
        ArgumentNullException.ThrowIfNull(open);

        var (taken, holder, letGo) = Start(name: null, open, TimeSpan.Zero, CancellationToken.None);

        try
        {
            return Kept(taken.Task.GetAwaiter().GetResult(), holder, letGo);
        }
        catch
        {
            Closed(holder, letGo);
            throw;
        }
    }

    /// <summary>
    /// Opens a mutex through <paramref name="open"/> on a thread of its own and takes it, waiting as long as
    /// <paramref name="timeout"/> allows and <paramref name="cancellationToken"/> leaves it; or <see langword="null"/>
    /// where the wait ended without taking it.
    /// </summary>
    /// <param name="name">The mutex's name, which a test watching for the take to wait names (<see cref="WaitBegins"/>).</param>
    /// <param name="open">Opens the mutex, refusing as <see cref="MachineWideMutex.Open(string, string)"/> does.</param>
    /// <param name="timeout">The whole of the wait; <see cref="System.Threading.Timeout.InfiniteTimeSpan"/> for as long as the token leaves it.</param>
    /// <param name="cancellationToken">Ends the wait where it has not taken the mutex.</param>
    /// <exception cref="HarnessException">The mutex could not be opened.</exception>
    internal static async Task<MachineWideHold?> TakeAsync(string name, Func<Mutex> open, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(open);

        var (taken, holder, letGo) = Start(name, open, timeout == System.Threading.Timeout.InfiniteTimeSpan ? TimeSpan.MaxValue : timeout, cancellationToken);

        try
        {
            return Kept(await taken.Task.ConfigureAwait(false), holder, letGo);
        }
        catch
        {
            Closed(holder, letGo);
            throw;
        }
    }

    /// <summary>Starts the holding thread of one take: its answer, the thread, and what lets the mutex go once taken.</summary>
    private static (TaskCompletionSource<MachineWideHold?> Taken, Thread Holder, ManualResetEventSlim LetGo) Start(
        string? name,
        Func<Mutex> open,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var taken = new TaskCompletionSource<MachineWideHold?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var letGo = new ManualResetEventSlim();
        var holder = new Thread(() => HoldFor(name, open, taken, letGo, timeout, cancellationToken))
        {
            IsBackground = true,
            Name = "machine-wide hold",
        };

        holder.Start();

        return (taken, holder, letGo);
    }

    /// <summary>The hold, where the take took the mutex; where it did not, its thread ended and its signal closed, and null.</summary>
    private static MachineWideHold? Kept(MachineWideHold? hold, Thread holder, ManualResetEventSlim letGo)
    {
        if (hold is null)
        {
            Closed(holder, letGo);
        }

        return hold;
    }

    /// <summary>Waits for the holding thread of a take that holds nothing to end, and closes its signal.</summary>
    private static void Closed(Thread holder, ManualResetEventSlim letGo)
    {
        holder.Join();
        letGo.Dispose();
    }

    /// <summary>
    /// The holding thread: opens the mutex, takes it where nobody holds it and where the wait still allows it, says how
    /// long it waited, and where it did, keeps it until it is let go. Its first look takes a free mutex at once; one that
    /// meets a holder begins to wait, a slice at a time, and tells a test watching for it.
    /// </summary>
    private static void HoldFor(
        string? name,
        Func<Mutex> open,
        TaskCompletionSource<MachineWideHold?> taken,
        ManualResetEventSlim letGo,
        TimeSpan timeout,
        CancellationToken cancellationToken)
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
            var started = Stopwatch.GetTimestamp();

            for (var looked = false; ; looked = true)
            {
                bool held;

                try
                {
                    var left = timeout - Stopwatch.GetElapsedTime(started);
                    held = MachineWideMutex.Wait(mutex, !looked ? TimeSpan.Zero : left > Slice ? Slice : left > TimeSpan.Zero ? left : TimeSpan.Zero);
                }
                catch (Exception ex)
                {
                    taken.SetException(ex);
                    return;
                }

                var waited = Stopwatch.GetElapsedTime(started);

                if (held)
                {
                    taken.SetResult(new MachineWideHold(Thread.CurrentThread, letGo, !looked ? TimeSpan.Zero : waited > TimeSpan.Zero ? waited : TimeSpan.FromTicks(1)));
                    letGo.Wait();
                    mutex.ReleaseMutex();
                    return;
                }

                if (cancellationToken.IsCancellationRequested || waited >= timeout)
                {
                    taken.SetResult(null);
                    return;
                }

                if (!looked && name is not null && !Watched.IsEmpty && Watched.TryRemove(name, out var watching))
                {
                    watching.TrySetResult();
                }
            }
        }
    }
}
