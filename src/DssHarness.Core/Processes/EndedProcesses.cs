using DssHarness.Core.Waiting;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// What an end of processes ended - a stop's: a child's tree, its group, a job's members, what a session's end killed; and
/// the tree <c>--now</c> and a host agent's last resort end - each process kept as it is ended, for the end's caller to
/// wait until it has gone (<see cref="GoneAsync"/>) where it needs: a stop and a contained child's end do, so nothing the
/// stop ended is still holding what it held - the write end of the child's output among it - once the stop returns;
/// <c>--now</c> waits for the process it was asked to end alone, and a host agent's last resort and a child whose start
/// could not be read for none. Disposed, it lets go of what it kept.
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

    /// <summary>What the ends could not end, in the order met (<see cref="Refused"/>).</summary>
    private readonly List<Exception> _refused = [];

    /// <summary>
    /// What the ends kept here could not end, and why, each said once in the order it was met: a process of a tree the
    /// walk passed over (<see cref="Containment.EndTree"/>) - in its first reading and in every look again
    /// (<see cref="GoneAsync"/>) - a reading of Windows' list that failed, a process of a group or a tree on Linux or macOS
    /// that could not be held or killed. Whole once <see cref="GoneAsync"/> has returned: what a stop says it could not
    /// end (<see cref="ProcessResult.Uncontained"/>).
    /// </summary>
    public IReadOnlyList<Exception> Refused => _refused;

    /// <summary>Says <paramref name="refusal"/> among what the ends could not end (<see cref="Refused"/>).</summary>
    /// <param name="refusal">What could not be ended, and why.</param>
    public void Refuse(Exception refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        _refused.Add(refusal);
    }

    /// <summary>Windows' list of every process, as the machine keeps it or as a test stands in for it (<see cref="ProcessEnds.Listed"/>).</summary>
    public IReadOnlyList<Execution.ListedProcess>? Listed() => _ends.Listed();

    /// <summary>When the process <paramref name="handle"/> holds was made, as the walk orders it (<see cref="ProcessEnds.Made"/>).</summary>
    /// <param name="id">The process's id.</param>
    /// <param name="handle">The process, held with the right to ask when it was created.</param>
    public CreationOrder Made(int id, SafeProcessHandle handle) => _ends.Made(id, handle);

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

    /// <summary>Ends every process of the group <paramref name="group"/> at once, on Linux and macOS, as the machine ends a group, or as a test stands in for it.</summary>
    /// <param name="group">The group's id.</param>
    public void EndGroup(int group) => _ends.EndGroup(group);

    /// <summary>
    /// The processes the group <paramref name="group"/> is listed to hold, on Linux and macOS, as the machine lists them, or
    /// as a test stands in for it; <see langword="null"/> where they could not be listed.
    /// </summary>
    /// <param name="group">The group's id.</param>
    public IReadOnlyList<int>? MembersOf(int group) => _ends.Members(group);

    /// <summary>Holds the process <paramref name="id"/> names still, on Linux and macOS, as the machine holds one, or as a test stands in for it: 0, or the error number.</summary>
    /// <param name="id">The process's id.</param>
    public int Hold(int id) => _ends.Hold(id);

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
/// How a process a stop keeps is held, ended, and seen to have gone, a group listed and ended, every process listed, and
/// Windows' list of every process and each process's order read: the machine's own (<see cref="Machine"/>) - or a test's,
/// standing in for a process whose end is slow, a listing that misses a member or that cannot be read, a process the
/// system lists that no hold reaches yet, or a clock that stepped between a creator's creation and its child's, which no
/// real process, listing or clock can be made to be on demand.
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

    /// <summary>Ends every process of the group <paramref name="group"/> at once, on Linux and macOS: SIGKILL to the group as one.</summary>
    /// <param name="group">The group's id.</param>
    public virtual void EndGroup(int group) => Containment.EndGroupNow(group);

    /// <summary>
    /// The processes the group <paramref name="group"/> is listed to hold, on Linux and macOS, read now
    /// (<see cref="Containment.MembersOf"/>); <see langword="null"/> where the system would not list them.
    /// </summary>
    /// <param name="group">The group's id.</param>
    public virtual IReadOnlyList<int>? Members(int group) => Containment.MembersOf(group);

    /// <summary>
    /// The id of every process the system lists, on Linux and macOS, read in place now (<see cref="Containment.EveryProcess"/>),
    /// as a session's end reads the system again once it holds the group: with no program started; <see langword="null"/>
    /// where the system would not list them.
    /// </summary>
    public virtual IReadOnlyList<int>? Every() => Containment.EveryProcess();

    /// <summary>
    /// Holds the process <paramref name="id"/> names still at once, on Linux and macOS (<see cref="Containment.HoldNow"/>):
    /// 0 where it was signalled, else the error number.
    /// </summary>
    /// <param name="id">The process's id.</param>
    public virtual int Hold(int id) => Containment.HoldNow(id);

    /// <summary>Whether <paramref name="process"/> has gone, asked of the system now and never waited on.</summary>
    /// <param name="process">The process, kept.</param>
    public virtual bool Gone(KeptProcess process) => Containment.Gone(process);

    /// <summary>
    /// Windows' list of every process, each with the id of its creator and when it was created, read now
    /// (<see cref="Execution.SystemProcessList.Every()"/>), as the walk of a tree and the chain of this process's parents
    /// read it; <see langword="null"/> where it could not be read.
    /// </summary>
    public virtual IReadOnlyList<Execution.ListedProcess>? Listed() => Execution.SystemProcessList.Every();

    /// <summary>
    /// When the process <paramref name="handle"/> holds - the one <paramref name="id"/> names - was made, on Windows, as
    /// the walk of a tree orders a creator and its child: its creation time and its sequence number, read from the handle
    /// (<see cref="Containment.OrderOf(SafeProcessHandle)"/>).
    /// </summary>
    /// <param name="id">The process's id.</param>
    /// <param name="handle">The process, held with the right to ask when it was created.</param>
    public virtual CreationOrder Made(int id, SafeProcessHandle handle) => Containment.OrderOf(handle);
}
