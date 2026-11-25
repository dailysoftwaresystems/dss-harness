using DssHarness.Core.Waiting;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// What a stop ended - a child's tree, its group, a job's members, what a session's end killed - each process kept as it
/// is ended, and waited for until it has gone (<see cref="GoneAsync"/>), so nothing the stop ended is still holding what
/// it held - the write end of the child's output among it - once the stop returns. Disposed, it lets go of what it kept.
/// </summary>
/// <remarks>
/// Kept by what keeps a process from being taken for another: on Windows a handle, which keeps its id its own and is
/// signalled only once its handle table has run down; on Linux and macOS its id and its start
/// (<see cref="Execution.ProcessIdentity.StampOf"/>), read while it was held still, so a later process given its id is never
/// waited for in its place.
/// </remarks>
/// <param name="ends">How each kept process is ended and seen gone: the machine's own, or a test's.</param>
internal sealed class EndedProcesses(ProcessEnds ends) : IDisposable
{
    private readonly ProcessEnds _ends = ends;
    private readonly List<KeptProcess> _kept = [];
    private readonly Dictionary<int, KeptProcess> _byId = [];

    /// <summary>What finds, and ends, what a source of these holds that is not kept yet, saying how many it found: Windows' trees and jobs.</summary>
    private readonly List<Func<int>> _looksAgain = [];

    /// <summary>The process kept under <paramref name="processId"/>, or <see langword="null"/> where none is.</summary>
    /// <param name="processId">The process's id.</param>
    public KeptProcess? Kept(int processId) => _byId.GetValueOrDefault(processId);

    /// <summary>Keeps <paramref name="process"/>, ended or about to be; one kept under its id already is not kept again, and is disposed.</summary>
    /// <param name="process">The process.</param>
    /// <returns>Whether it was kept.</returns>
    public bool Keep(KeptProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!_byId.TryAdd(process.Id, process))
        {
            process.Dispose();
            return false;
        }

        _kept.Add(process);
        return true;
    }

    /// <summary>Ends <paramref name="process"/>, kept: at once, as the machine ends a process, or as a test stands in for it.</summary>
    /// <param name="process">The process.</param>
    public void End(KeptProcess process) => _ends.End(process);

    /// <summary>Ends every member of <paramref name="job"/> at once, on Windows, as the machine ends a job, or as a test stands in for it.</summary>
    /// <param name="job">The job.</param>
    public void EndJob(SafeWaitHandle job) => _ends.EndJob(job);

    /// <summary>
    /// Has <paramref name="look"/> asked again once every process kept has gone: it finds, keeps and ends what its source
    /// holds that is not kept yet - a child one of them made while it was being ended - and says how many it found.
    /// </summary>
    /// <param name="look">The look.</param>
    public void LookAgainWith(Func<int> look)
    {
        ArgumentNullException.ThrowIfNull(look);

        _looksAgain.Add(look);
    }

    /// <summary>
    /// Waits until every process kept has gone - on Windows its handle signalled; on Linux and macOS no process carrying
    /// its id with its start, or that process a zombie, whose descriptors are closed, whether or not anything reaps it -
    /// and its sources, looked at again, find nothing more; told every five minutes, with no bound: a process the stop
    /// ended goes, and nothing is taken for gone before it has.
    /// </summary>
    /// <param name="subject">What the wait is told as: what it waits for to go.</param>
    /// <param name="tell">Told of the wait every five minutes.</param>
    /// <param name="clock">What the wait is measured and told by.</param>
    public async Task GoneAsync(string subject, Action<WaitTold> tell, TimeProvider clock)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(tell);
        ArgumentNullException.ThrowIfNull(clock);

        if (_kept.Count == 0 && _looksAgain.Count == 0)
        {
            return;
        }

        var standing = new List<KeptProcess>(_kept);

        await Waits.ForAsync(
            async stop =>
            {
                while (true)
                {
                    standing.RemoveAll(_ends.Gone);

                    if (standing.Count == 0)
                    {
                        var before = _kept.Count;

                        foreach (var look in _looksAgain)
                        {
                            _ = look();
                        }

                        if (_kept.Count == before)
                        {
                            return true;
                        }

                        standing.AddRange(_kept.Skip(before));
                        continue;
                    }

                    await Task.Delay(Containment.EndingLook, stop).ConfigureAwait(false);
                }
            },
            new WaitWatch(subject, tell, null, clock),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Lets go of every process kept.</summary>
    public void Dispose()
    {
        foreach (var process in _kept)
        {
            process.Dispose();
        }
    }
}

/// <summary>
/// One process a stop ended, kept so its end is waited for: on Windows by a handle, with when it was created; on Linux and
/// macOS by its id and start.
/// </summary>
internal sealed class KeptProcess : IDisposable
{
    /// <summary>Keeps a process of Windows by <paramref name="handle"/>, which keeps its id its own.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="handle">Its handle, owned from here: to end it, wait for its end and ask when it was created.</param>
    /// <param name="created">When it was created, as a file time; the latest there is where that could not be read, so no process is taken for its child.</param>
    public KeptProcess(int id, SafeProcessHandle handle, long created)
    {
        ArgumentNullException.ThrowIfNull(handle);

        (Id, Handle, Created) = (id, handle, created);
    }

    /// <summary>Keeps a process of Linux or macOS by its id and <paramref name="stamp"/>, its start read while it was held still.</summary>
    /// <param name="id">Its id.</param>
    /// <param name="stamp">Its start, as <see cref="Execution.ProcessIdentity.StampOf"/> reads it.</param>
    public KeptProcess(int id, string stamp)
    {
        ArgumentException.ThrowIfNullOrEmpty(stamp);

        (Id, Stamp) = (id, stamp);
    }

    /// <summary>Its id.</summary>
    public int Id { get; }

    /// <summary>Its handle, on Windows; <see langword="null"/> elsewhere.</summary>
    public SafeProcessHandle? Handle { get; }

    /// <summary>When it was created, as a file time, on Windows.</summary>
    public long Created { get; }

    /// <summary>Its start, on Linux and macOS; <see langword="null"/> on Windows.</summary>
    public string? Stamp { get; }

    /// <summary>Lets go of its handle, where it has one.</summary>
    public void Dispose() => Handle?.Dispose();
}

/// <summary>
/// How a process a stop keeps is ended, and seen to have gone: the machine's own (<see cref="Machine"/>) - or a test's,
/// standing in for a process whose end is slow, which no real process can be made to be on demand.
/// </summary>
internal class ProcessEnds
{
    /// <summary>The machine's own: a process ended at once, and gone as the system says it.</summary>
    public static ProcessEnds Machine { get; } = new();

    /// <summary>Ends <paramref name="process"/> at once: TerminateProcess on Windows, SIGKILL on Linux and macOS.</summary>
    /// <param name="process">The process, kept.</param>
    public virtual void End(KeptProcess process) => Containment.EndNow(process);

    /// <summary>Ends every member of <paramref name="job"/> at once, on Windows.</summary>
    /// <param name="job">The job.</param>
    public virtual void EndJob(SafeWaitHandle job) => Containment.Terminate(job);

    /// <summary>Whether <paramref name="process"/> has gone, asked of the system now and never waited on.</summary>
    /// <param name="process">The process, kept.</param>
    public virtual bool Gone(KeptProcess process) => Containment.Gone(process);
}
