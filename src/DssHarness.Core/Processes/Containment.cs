using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;
using DssHarness.Core.Waiting;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The calls containment is made of, below the platform seam: the C library's session, group,
/// signal and exec calls on Linux and macOS, and the job objects, process creation and console
/// signals of Windows - and the end of what a child started, at a stop and at a contained child's
/// own end, and of a process with its tree, each process it ends kept in what its caller gives it
/// (<see cref="EndedProcesses"/>), for that caller to wait for as it needs.
/// Each side is called only where that system runs, and the choice between them is made here and
/// nowhere above.
/// </summary>
internal static class Containment
{
    /// <summary>How often a wait for an ending looks again - a session asked to end, and what a stop ended (<see cref="EndedProcesses.GoneAsync"/>): a look's pace, never a bound.</summary>
    internal static readonly TimeSpan EndingLook = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Why the step cannot give the containment asked for on this system, or <see langword="null"/>
    /// where it can: full containment on Windows is the job object the engine made and names, and
    /// a job object is Windows' alone.
    /// </summary>
    /// <param name="job">The job named, if one was.</param>
    /// <param name="group">Whether the group alone was asked for.</param>
    public static string? Refusal(string? job, bool group)
    {
        if (OperatingSystem.IsWindows())
        {
            return job is null && !group
                ? $"contain-exec was asked for full containment with no '{ContainExec.JobOption}': on Windows that is a job object, named by the process runner that made it."
                : null;
        }

        return job is not null
            ? $"contain-exec was asked for the job object '{job}', which only Windows has: on Linux and macOS full containment is a session of its own, asked for with no option."
            : null;
    }

    /// <summary>
    /// Places this process where it is asked to be contained, then becomes the program there (Linux
    /// and macOS) or starts it there and waits for it (Windows), saying on <paramref name="status"/>
    /// how far it got.
    /// </summary>
    /// <param name="job">The Windows job object to join, for full containment there.</param>
    /// <param name="group">Whether the group alone is asked for, apart from the terminal within the session.</param>
    /// <param name="scope">Whether the program is become inside Landlock's scope of abstract sockets and signals (<see cref="Landlock.Scope"/>), on Linux.</param>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="status">The channel the process runner reads the start from.</param>
    /// <param name="error">Where a failure of the step after the program started is said.</param>
    /// <returns>
    /// The program's own exit code on Windows; on Linux and macOS no return where the program was
    /// become. <see cref="ContainExec.Failed"/> where the step failed, the failure said on the channel.
    /// </returns>
    public static int Place(string? job, bool group, bool scope, string program, IReadOnlyList<string> arguments, ContainExec.StartStatus status, TextWriter error)
        => OperatingSystem.IsWindows()
            ? PlaceOnWindows(job, program, arguments, status, error)
            : PlaceOnPosix(group, scope, program, arguments, status);

    /// <summary>
    /// Becomes <paramref name="program"/> under this process's id, on Linux and macOS, Landlock's scope entered first where
    /// <paramref name="scope"/> asks it and each of <paramref name="unset"/> taken from the environment it is handed - the
    /// C library's own, which the runtime's copy never writes back - tried again while the file is busy, as any start is.
    /// </summary>
    /// <param name="scope">Whether the program is become inside Landlock's scope of abstract sockets and signals.</param>
    /// <param name="program">The program, by its whole path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <param name="unset">The variables the program is not handed.</param>
    /// <returns>No return where the program was become; else the error number that kept it from being.</returns>
    internal static int Become(bool scope, string program, IReadOnlyList<string> arguments, IReadOnlyList<string> unset)
    {
        ArgumentNullException.ThrowIfNull(unset);

        if (scope && Landlock.Scope() is var refused and not 0)
        {
            return refused;
        }

        foreach (var name in unset)
        {
            if (Posix.Calls().Unset(name) != 0)
            {
                return Marshal.GetLastPInvokeError();
            }
        }

        try
        {
            ProcessRunner.StartWhileBusy(() => throw new Win32Exception(Posix.Calls().Become(program, [program, .. arguments])));
        }
        catch (Win32Exception ex)
        {
            return ex.NativeErrorCode;
        }

        return 0;
    }

    /// <summary>
    /// Ends a contained child's whole session on Linux and macOS - or, where <paramref name="group"/>, the whole process
    /// group a child apart from the terminal leads within its starter's session: every process in it, and while the
    /// child stands every one in its tree, is asked to end - the group as one, each member outside
    /// the group on its own - and waited for for as long as any of them stands, a zombie standing for none, told every five
    /// minutes (<see cref="Waits.SaidEvery"/>); every one still standing once they have all gone or <paramref name="bound"/> - the
    /// bound its run was configured with - is spent is killed, and waited for until it has gone or is a zombie, so nothing
    /// this end killed still holds what the child's output is read from once it returns.
    /// </summary>
    /// <param name="leader">The child, which leads the session and the group, or the group alone, by its own process id.</param>
    /// <param name="read">Reads the machine's process table.</param>
    /// <param name="bound">
    /// What is left of the bound the run that started it was configured with - its time limit - where SIGKILL follows the
    /// asking; none where it was given none, so a member that refuses to be asked is waited for, told, for as long as it
    /// stands; and no time where that bound has passed - a run that was killed passed it with its own end.
    /// </param>
    /// <param name="tell">Told of the wait every five minutes.</param>
    /// <param name="clock">What the wait is measured and told by: the system's, or a test's.</param>
    /// <param name="ends">How what is killed is ended and seen gone: the machine's own, or a test's.</param>
    /// <param name="group">Whether the child leads a process group alone, in a session that is not its own.</param>
    /// <returns>
    /// Whether anything was still in it to end, and what could not be reached, and why - none where
    /// it was read each time it was looked for. A table that cannot be read - on macOS it is read through
    /// <c>ps</c>, which a session that has used up every process it may start leaves unable to start -
    /// ends nothing less than the group: the group is signalled by its id, which needs no table, and
    /// only what left it goes unreached. The leader need not stand: what it led is read by its id,
    /// which no later process is given while a session or a group still carries it.
    /// </returns>
    /// <remarks>
    /// <para>
    /// The members are read before anything is signalled. A member that ends first takes nothing
    /// with it from the session, whose id every process it started keeps when it is handed to
    /// another parent - but it takes its children out of its tree, and a stop that walked the tree
    /// after asking found nothing left to walk: a test under <c>timeout</c>, which moves itself to a
    /// group of its own, outlived the stop and held the output the run was waiting to read to its
    /// end. The tree is read beside the session for what left the session while still the child's.
    /// </para>
    /// <para>
    /// What is killed at the end is read again, so what was started while the members were being
    /// asked goes too; a process the first reading found is killed only where the second finds it
    /// under the same id and start, so an id given to another process meanwhile is left alone. Each
    /// is held still (SIGSTOP) as it is found - the group as one before the table is read again - so
    /// none starts another or goes before it is killed, and kept by its id and start read while it is
    /// held, then killed - the group as one first, while it stands, so a member no reading showed goes
    /// with it, and every other by its own id (<see cref="HeldStill.Kill"/>) - and waited for
    /// (<see cref="EndedProcesses.GoneAsync"/>). A member is ended, for the wait before the bound, once
    /// nothing answers to its id or what does is a zombie (<see cref="ProcessGoing.HasEnded"/>), which
    /// nothing may ever reap, and the group once nothing in it can be signalled or every member it is
    /// listed to hold is a zombie. A
    /// session with nothing in it is not read again, nor signalled. A process that left
    /// both the session and the tree - a daemon that made a session of its own and whose parent
    /// has gone - is reached by nothing here. A group is ended the same way, by its own members and
    /// the leader's tree, never by the session it shares with the process that started it.
    /// </para>
    /// </remarks>
    public static async Task<HeldEnding> EndSessionAsync(
        int leader,
        Func<CancellationToken, Task<ProcessTableReading>> read,
        TimeSpan? bound,
        Action<WaitTold> tell,
        TimeProvider clock,
        ProcessEnds ends,
        bool group = false)
    {
        ArgumentNullException.ThrowIfNull(read);
        ArgumentNullException.ThrowIfNull(tell);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(ends);

        var calls = Posix.Calls();

        // What the leader leads: its session, or its group alone - never the session it shares with its starter.
        bool In(int process) => (group ? calls.GroupOf(process) : calls.SessionOf(process)) == leader;

        var held = group ? "the process group a child apart from the terminal led" : "the contained child's session";
        var (first, unread) = await ReadAsync(read).ConfigureAwait(false);
        var members = first is null ? [] : Members(first, leader, In);

        if (members.Count == 0 && !GroupHolds(leader))
        {
            return unread is null ? new HeldEnding(Found: false, null) : new HeldEnding(Found: true, Unreached(unread, held, "its group had already ended"));
        }

        calls.SignalGroup(leader, Posix.AskToEnd);

        foreach (var member in members.Where(member => calls.GroupOf(member.Id) != leader))
        {
            calls.Signal(member.Id, Posix.AskToEnd);
        }

        // The signal with no number asks nothing and reports everything: a group or a process it cannot signal is one that
        // has ended, and one it can has ended all the same where it is a zombie, which nothing may ever reap - the group
        // once every member it is listed to hold is one. Waited for while any of it stands, and made to at the bound.
        bool MemberEnded(int process) => calls.Signal(process, 0) == -1 || ProcessGoing.HasEnded(process);

        bool GroupEnded()
            => calls.SignalGroup(leader, 0) == -1
                || (ends.Members(leader).ToList() is { Count: > 0 } listed && listed.All(ProcessGoing.HasEnded));

        await Waits.ForAsync(
            async cancellationToken =>
            {
                while (!(members.All(member => MemberEnded(member.Id)) && GroupEnded()))
                {
                    await Task.Delay(EndingLook, cancellationToken).ConfigureAwait(false);
                }

                return true;
            },
            new WaitWatch($"{held} ({leader.ToString(System.Globalization.CultureInfo.InvariantCulture)}) to end once asked", tell, bound, clock),
            CancellationToken.None).ConfigureAwait(false);

        // What still stands is made to end, held still first - the group as one before it is listed, then each member by
        // its own id, and each member outside the group as the table finds it - so none starts another, or goes and has
        // its id given out, before it is killed: the group as one, then every other by its id.
        using var ended = new EndedProcesses(ends);
        var holding = new HeldStill(calls, ended, failed: null);
        HeldEnding ending;

        try
        {
            _ = holding.HoldGroup(leader);

            ending = unread is not null
                ? new HeldEnding(Found: true, Unreached(unread, held, "its group was ended"))
                : await HeldAgainAsync().ConfigureAwait(false);
        }
        finally
        {
            // Whatever reading the table again met, nothing held still is left so.
            holding.Kill();
        }

        await ended.GoneAsync($"{held} ({leader.ToString(System.Globalization.CultureInfo.InvariantCulture)}) to go once made to", tell, clock).ConfigureAwait(false);

        return ending;

        async Task<HeldEnding> HeldAgainAsync()
        {
            var (again, unreadAgain) = await ReadAsync(read).ConfigureAwait(false);

            if (again is null)
            {
                // Still a member of the session - or the group - is still one of the run's, whoever its
                // parent: only what left it can no longer be told from another process under the same id.
                foreach (var member in members.Where(member => In(member.Id)))
                {
                    _ = holding.Hold(member.Id);
                }

                return new HeldEnding(
                    Found: true,
                    group
                        ? $"the process table could not be read again once {held} had been asked to end ({unreadAgain}): "
                            + "its group was ended, and what of its tree had left the group was not reached"
                        : $"the process table could not be read again once {held} had been asked to end ({unreadAgain}): "
                            + "its group and what the first reading found still in the session were ended, and what was started meanwhile outside the group, "
                            + "and what of its tree had left the session, was not reached");
            }

            // Known by id and start alone: a process handed to another parent, or one that became
            // another program, is still the one the first reading found.
            var known = members.Select(member => (member.Id, member.StartedUtc)).ToHashSet();

            foreach (var member in again.Processes.Where(process => known.Contains((process.Id, process.StartedUtc)) || In(process.Id)))
            {
                _ = holding.Hold(member.Id);
            }

            return new HeldEnding(Found: true, null);
        }
    }

    /// <summary>
    /// Whether the process group <paramref name="leader"/> leads still holds a process, on Linux and macOS: a group with
    /// nothing left in it cannot be signalled, and the signal with no number asks nothing of one that can.
    /// </summary>
    /// <param name="leader">The id of the group, its leader's whether or not the leader stands.</param>
    public static bool GroupHolds(int leader) => Posix.Calls().SignalGroup(leader, 0) != -1;

    /// <summary>
    /// Ends <paramref name="root"/> and its whole tree - and, where <paramref name="group"/>, on Linux and macOS, the process
    /// group it leads, which reaches what its tree handed on - keeping each process it ends in <paramref name="ended"/>, for
    /// its caller to wait for as it needs (<see cref="EndedProcesses.GoneAsync"/>): the one walk that ends a process with its
    /// tree - for a stop, which waits until every process kept has gone; for <c>--now</c>, which waits for
    /// <paramref name="root"/> alone; and for a host agent's last resort and a child whose start could not be read, which
    /// wait for none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On Linux and macOS each process is held still (SIGSTOP) before its children are taken: held, it starts no other,
    /// and a child of it is never reaped and its id given out before the walk acts on it (<see cref="HoldDescendants"/>).
    /// Every process of the tree is held so, then the group - as one before it is listed, and each member by its own id -
    /// and only then each is kept by its id and start and killed (<see cref="HeldStill.Kill"/>): the group as one first,
    /// while it stands, so a member its listing did not show goes with it, and every other process by its own id, the root
    /// last. A root the runtime has seen end is not walked: its children were handed on, and its id may be another's.
    /// </para>
    /// <para>
    /// On Windows, where nothing is held still, each process is ended as it is found - the root first, so it starts no
    /// other - and kept by a handle, which keeps its id its own: the root by a copy of <paramref name="handle"/>, the one it
    /// was created with, where its caller started it - which ends and waits for it whatever its own security refuses
    /// another - and opened by its id where its caller knows it by that alone. A child is one Windows' list of every
    /// process names as created by a kept process, created no earlier than it, and the very process the list named, as its
    /// handle's creation time says, so an id taken since the list was read is never ended. A process of the tree that
    /// cannot be ended - one running as another user, or one whose own security refuses this process its end - is passed
    /// over, its failure said once, and still has its children taken, from the reading that found it - the root's from the
    /// first - held while they are by what asks when it was created. The list is read again until it names nothing new, and again once every process kept has
    /// gone (<see cref="EndedProcesses.GoneAsync"/>), for a child one made while it was being ended.
    /// </para>
    /// </remarks>
    /// <param name="root">The process whose tree ends.</param>
    /// <param name="group">Whether the group it leads ends too, on Linux and macOS.</param>
    /// <param name="ended">What keeps each process ended.</param>
    /// <param name="handle">
    /// The handle the runtime got as this process created <paramref name="root"/> (<see cref="Process.SafeHandle"/>), where it
    /// did: on Windows the root is kept by a copy of it. <see langword="null"/> where <paramref name="root"/> is known by its
    /// id alone, and opened by it.
    /// </param>
    /// <exception cref="AggregateException">A process of the tree or the group could not be ended: every other was.</exception>
    public static void EndTree(Process root, bool group, EndedProcesses ended, SafeProcessHandle? handle = null)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(ended);

        var failed = new List<Exception>();

        if (OperatingSystem.IsWindows())
        {
            EndTreeOnWindows(root, handle, ended, failed);
        }
        else
        {
            EndTreeOnPosix(root, group, ended, failed);
        }

        if (failed.Count > 0)
        {
            throw new AggregateException($"Not every process of the tree of pid {root.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)} could be ended.", failed);
        }
    }

    /// <summary>Ends a kept process at once, as the machine does: TerminateProcess on Windows, SIGKILL on Linux and macOS.</summary>
    /// <param name="process">The process.</param>
    public static void EndNow(KeptProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.Handle is { } handle)
        {
            // A process already in its exit refuses it: it goes all the same.
            _ = Windows.TerminateProcess(handle, unchecked((uint)-1));
        }
        else
        {
            _ = Posix.Calls().Signal(process.Id, Posix.Kill);
        }
    }

    /// <summary>Kills every process of the group <paramref name="group"/> at once, on Linux and macOS: SIGKILL to the group as one.</summary>
    /// <param name="group">The group's id, its leader's.</param>
    public static void EndGroupNow(int group) => _ = Posix.Calls().SignalGroup(group, Posix.Kill);

    /// <summary>
    /// Whether a kept process has gone, asked now: on Windows its handle signalled, which comes only once its handle table
    /// has run down; on Linux and macOS no process carrying its id with its start, or that process a zombie
    /// (<see cref="ProcessGoing.HasEnded"/>).
    /// </summary>
    /// <param name="process">The process.</param>
    public static bool Gone(KeptProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (process.Handle is { } handle)
        {
            // A handle that cannot be waited on has nothing left to wait for.
            return Windows.WaitForSingleObject(handle, 0) != Windows.WaitTimeout;
        }

        return !Identity.IsSame(process.Id, process.Stamp!) || ProcessGoing.HasEnded(process.Id);
    }

    /// <summary>What tells a process of Linux or macOS from a later one given its id: its start.</summary>
    private static readonly ProcessIdentity Identity = new(new HostPlatform());

    /// <summary>What Linux says of its processes, under <c>/proc</c>.</summary>
    private static readonly ProcessKernel Kernel = new(new HostPlatform());

    /// <summary>The Windows half of <see cref="EndTree"/>.</summary>
    private static void EndTreeOnWindows(Process root, SafeProcessHandle? handle, EndedProcesses ended, List<Exception> failed)
    {
        var tree = new WindowsTree(ended);

        if (tree.Took(root.Id, handle, failed))
        {
            _ = tree.Walk(failed);
        }
        else
        {
            // A root that cannot be ended still has its children ended, as every process of the tree that cannot be does
            // (WindowsTree.Walk): held by what asks when it was created while they are taken from the list's first reading.
            using var read = Opened(root.Id, Windows.ReadCreation, failed: null);

            if (read is null)
            {
                return;
            }

            _ = tree.Walk(failed, (root.Id, CreatedOf(read)));
        }

        ended.LookAgainWith(() => tree.Walk(failed: null));
    }

    /// <summary>
    /// One tree a stop ends on Windows: the processes of it kept, and the walk that finds, keeps and ends the children of
    /// any of them not kept yet.
    /// </summary>
    private sealed class WindowsTree(EndedProcesses ended)
    {
        /// <summary>The tree's processes, by id, each with when it was created: each kept, here or by another of what the stop ended.</summary>
        private readonly Dictionary<int, long> _members = [];

        /// <summary>
        /// The processes of the tree that could not be ended - by id and when each was created, as the list named it - each
        /// tried once, its failure said once, and its children taken from the reading that found it and from no later one.
        /// </summary>
        private readonly HashSet<(int Id, long Created)> _passed = [];

        /// <summary>
        /// Keeps and ends the root - by a copy of <paramref name="handle"/>, the one it was created with, where its caller
        /// holds that, which ends and waits for it whatever its own security refuses another; else opened by its id - and
        /// whether it was kept.
        /// </summary>
        /// <param name="id">The root's id, its own while the runtime holds the process.</param>
        /// <param name="handle">The handle the root was created with, where its caller started it; <see langword="null"/> where its id alone is known.</param>
        /// <param name="failed">Where a process that could not be ended is said.</param>
        public bool Took(int id, SafeProcessHandle? handle, List<Exception> failed)
        {
            if (ended.Kept(id) is not { } kept)
            {
                var held = (handle is null ? null : Copied(handle)) ?? Opened(id, Windows.EndAndWait, failed);

                if (held is null)
                {
                    return false;
                }

                kept = new KeptProcess(id, held, CreatedOf(held));

                _ = ended.Keep(kept);
                ended.End(kept);
            }

            _members[id] = kept.Created;
            return true;
        }

        /// <summary>
        /// Reads Windows' list of every process, keeps and ends each child of a member not kept yet - and, in its first
        /// reading, of <paramref name="root"/> - and reads it again until it names none. A child that cannot be ended is
        /// passed over, its failure said, and its own children taken from the same reading, as its tree's: held while they
        /// are by what asks when it was created, so its id stays its own, and walked from in that reading alone, since one
        /// that stands may go on starting others for as long as it stands.
        /// </summary>
        /// <param name="failed">Where a process that could not be ended is said; <see langword="null"/> where nothing is said.</param>
        /// <param name="root">
        /// A root that could not be ended, by its id and when it was created, held by the caller while the walk takes its
        /// children: walked from in the first reading alone.
        /// </param>
        /// <returns>How many it found.</returns>
        public int Walk(List<Exception>? failed, (int Id, long Created)? root = null)
        {
            var found = 0;

            while (true)
            {
                if (SystemProcessList.Every() is not { } listed)
                {
                    failed?.Add(new InvalidOperationException("Windows' list of every process could not be read, so what the tree started was not reached"));
                    return found;
                }

                var byCreator = listed.ToLookup(process => process.ParentId);
                var walking = new Queue<(int Id, long Created)>(_members.Select(member => (Id: member.Key, Created: member.Value)));
                var passedOver = new List<SafeProcessHandle>();
                var round = 0;

                if (root is { } unended)
                {
                    walking.Enqueue(unended);
                    root = null;
                }

                try
                {
                    while (walking.TryDequeue(out var creator))
                    {
                        foreach (var child in byCreator[creator.Id])
                        {
                            // A process created before its named creator was made by an earlier holder of that id.
                            if (_members.ContainsKey(child.Id) || _passed.Contains((child.Id, child.Created)) || child.Created < creator.Created)
                            {
                                continue;
                            }

                            if (ended.Kept(child.Id) is null)
                            {
                                if (Opened(child.Id, Windows.EndAndWait, failed) is not { } handle)
                                {
                                    _ = _passed.Add((child.Id, child.Created));

                                    if (Read(child) is { } read)
                                    {
                                        passedOver.Add(read);
                                        walking.Enqueue((child.Id, child.Created));
                                    }

                                    continue;
                                }

                                if (CreatedOf(handle) != child.Created)
                                {
                                    // Another process has the id since the list was read.
                                    handle.Dispose();
                                    continue;
                                }

                                var kept = new KeptProcess(child.Id, handle, child.Created);

                                _ = ended.Keep(kept);
                                ended.End(kept);
                                round++;
                            }

                            _members[child.Id] = child.Created;
                            walking.Enqueue((child.Id, child.Created));
                        }
                    }
                }
                finally
                {
                    foreach (var read in passedOver)
                    {
                        read.Dispose();
                    }
                }

                if (round == 0)
                {
                    return found;
                }

                found += round;
            }
        }

        /// <summary>
        /// A handle to <paramref name="listed"/> that asks when it was created, where it is the very process the list named;
        /// <see langword="null"/> where none could be opened, or the id is another's since.
        /// </summary>
        private static SafeProcessHandle? Read(ListedProcess listed)
        {
            if (Opened(listed.Id, Windows.ReadCreation, failed: null) is not { } read)
            {
                return null;
            }

            if (CreatedOf(read) != listed.Created)
            {
                read.Dispose();
                return null;
            }

            return read;
        }
    }

    /// <summary>
    /// A handle to the process <paramref name="id"/> names, on Windows, with <paramref name="rights"/>;
    /// <see langword="null"/> where none could be opened - one that has gone, or one whose own security refuses this
    /// process those rights, which is said in <paramref name="failed"/>.
    /// </summary>
    private static SafeProcessHandle? Opened(int id, uint rights, List<Exception>? failed)
    {
        var handle = Windows.OpenProcess(rights, inheritHandle: false, id);

        if (!handle.IsInvalid)
        {
            return handle;
        }

        var error = Marshal.GetLastPInvokeError();
        handle.Dispose();

        if (error != Windows.InvalidParameter)
        {
            failed?.Add(new Win32Exception(error, $"pid {id.ToString(System.Globalization.CultureInfo.InvariantCulture)} could not be ended: {new Win32Exception(error).Message}"));
        }

        return null;
    }

    /// <summary>
    /// A copy of <paramref name="handle"/>, a process's, holding every right it holds - granted as the handle was got, so
    /// nothing the process has said of its own security since is asked again; <see langword="null"/> where it could not be
    /// copied.
    /// </summary>
    private static SafeProcessHandle? Copied(SafeProcessHandle handle)
    {
        var self = Windows.GetCurrentProcess();

        if (Windows.DuplicateHandle(self, handle, self, out var copy, desiredAccess: 0, inheritHandle: false, Windows.SameAccess))
        {
            return copy;
        }

        copy.Dispose();
        return null;
    }

    /// <summary>When the process <paramref name="handle"/> holds was created, as a file time; the latest there is where it could not be read.</summary>
    private static long CreatedOf(SafeProcessHandle handle)
        => Windows.GetProcessTimes(handle, out var created, out _, out _, out _) ? created : long.MaxValue;

    /// <summary>The Linux and macOS half of <see cref="EndTree"/>.</summary>
    private static void EndTreeOnPosix(Process root, bool group, EndedProcesses ended, List<Exception> failed)
    {
        var holding = new HeldStill(Posix.Calls(), ended, failed);

        try
        {
            if (!HasExited(root) && holding.Hold(root.Id))
            {
                HoldDescendants(holding);
            }

            if (group)
            {
                _ = holding.HoldGroup(root.Id);
            }
        }
        finally
        {
            // Whatever the walk met, nothing it held still is left so.
            holding.Kill();
        }
    }

    /// <summary>
    /// What an end holds still on Linux and macOS before it kills any of it - each process by its own id, and a group as
    /// one with each member it is listed to hold - and the one order all of it is killed in (<see cref="Kill"/>): the walk
    /// of a tree's and a session's end alike.
    /// </summary>
    /// <param name="calls">The C library's calls.</param>
    /// <param name="ended">What keeps each process killed, and lists a group's members.</param>
    /// <param name="failed">Where a process that could not be held is said; <see langword="null"/> where nothing is said.</param>
    private sealed class HeldStill(Posix calls, EndedProcesses ended, List<Exception>? failed)
    {
        private readonly HashSet<int> _tried = [];
        private int? _group;

        /// <summary>Every process held, in the order it was: the first - the root, the leader - killed last.</summary>
        public List<int> Processes { get; } = [];

        /// <summary>Whether holding the process <paramref name="id"/> names was tried already, held or not.</summary>
        /// <param name="id">The process's id.</param>
        public bool Tried(int id) => _tried.Contains(id);

        /// <summary>
        /// Holds the process <paramref name="id"/> names still (SIGSTOP); whether it was held now - one tried already, or
        /// gone, is not, and one that may not be signalled is said in what fails.
        /// </summary>
        /// <param name="id">The process's id.</param>
        public bool Hold(int id)
        {
            if (!_tried.Add(id))
            {
                return false;
            }

            if (calls.Signal(id, Posix.HoldStill) == -1)
            {
                var error = Marshal.GetLastPInvokeError();

                if (error != Posix.NoSuchProcess)
                {
                    failed?.Add(new Win32Exception(error, $"pid {id.ToString(System.Globalization.CultureInfo.InvariantCulture)} could not be ended: {new Win32Exception(error).Message}"));
                }

                return false;
            }

            Processes.Add(id);
            return true;
        }

        /// <summary>
        /// Holds the group <paramref name="group"/> still as one, then each member it is listed to hold by its own id, its
        /// leader first: whether the group stood. Held as one, none of it starts another or goes, so the group stands -
        /// its id its own - until it is killed.
        /// </summary>
        /// <param name="group">The group's id, its leader's.</param>
        public bool HoldGroup(int group)
        {
            if (calls.SignalGroup(group, Posix.HoldStill) == -1)
            {
                return false;
            }

            _group = group;

            foreach (var member in ended.MembersOf(group).OrderBy(member => member != group))
            {
                _ = Hold(member);
            }

            return true;
        }

        /// <summary>
        /// Keeps and kills all that is held, each start read while it is held, so it is that very process's. The group,
        /// where one is held, is killed as one first (SIGKILL), while it stands - so a member its listing did not show, one
        /// <c>/proc</c> hides or whose reading failed, goes with it and is never left held still - and each member of it
        /// is killed by the group alone: once the group is killed a member's id may be given out again, so it is never
        /// signalled by that id after. Every other process held is killed by its own id, from the last held to the first,
        /// so the root goes last. One whose start cannot be read is killed and not kept.
        /// </summary>
        public void Kill()
        {
            var stamps = Processes.ConvertAll(Identity.StampOf);
            var byGroup = Processes.ConvertAll(id => _group is { } group && calls.GroupOf(id) == group);

            if (_group is { } held)
            {
                ended.EndGroup(held);
            }

            for (var index = Processes.Count - 1; index >= 0; index--)
            {
                if (stamps[index] is { Length: > 0 } stamp && new KeptProcess(Processes[index], stamp) is var kept && ended.Keep(kept))
                {
                    if (!byGroup[index])
                    {
                        ended.End(kept);
                    }
                }
                else if (!byGroup[index])
                {
                    _ = calls.Signal(Processes[index], Posix.Kill);
                }
            }
        }
    }

    /// <summary>Whether the runtime has seen <paramref name="process"/> end, or holds no process at all.</summary>
    private static bool HasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>
    /// Holds still every descendant of what <paramref name="holding"/> holds, on Linux and macOS, its own children taken
    /// only once it is held: a child of a process held still is never reaped, and its id never given out, before the walk
    /// acts on it.
    /// </summary>
    /// <remarks>
    /// macOS lists the children of one process at a time, asked once that process is held, so what it lists is that
    /// process's own. Linux lists every process at once: read once a round, a child is taken where it is still the child of
    /// the held process the reading named, as asked again now, and the table read again until a round takes none - a round
    /// that takes none was read once every process held was, so it named every child any of them has. Each round is one
    /// reading of the table, however deep the tree.
    /// </remarks>
    private static void HoldDescendants(HeldStill holding)
    {
        var held = holding.Processes;

        if (OperatingSystem.IsMacOS())
        {
            // The list grows as each child is held, and is walked to its end.
            for (var index = 0; index < held.Count; index++)
            {
                foreach (var child in Posix.Listed(Posix.ByParent, held[index]))
                {
                    _ = holding.Hold(child);
                }
            }

            return;
        }

        while (true)
        {
            var before = held.Count;
            var byParent = Proc(ProcStat.ParentField).ToLookup(process => (int)process.Value, process => process.Id);
            var walking = new Queue<int>(held);

            while (walking.TryDequeue(out var parent))
            {
                foreach (var child in byParent[parent])
                {
                    if (!holding.Tried(child) && FieldOf(child, ProcStat.ParentField) == parent && holding.Hold(child))
                    {
                        walking.Enqueue(child);
                    }
                }
            }

            if (held.Count == before)
            {
                return;
            }
        }
    }

    /// <summary>
    /// The ids of the processes the group <paramref name="group"/> is listed to hold, on Linux and macOS, read now: one
    /// Linux's <c>/proc</c> hides - a member of another user's under <c>hidepid</c> - or whose reading failed is not among
    /// them, which the group's kill as one reaches all the same.
    /// </summary>
    /// <param name="group">The group's id.</param>
    internal static IEnumerable<int> MembersOf(int group)
        => OperatingSystem.IsMacOS()
            ? Posix.Listed(Posix.ByGroup, group)
            : Proc(ProcStat.GroupField).Where(process => process.Value == group).Select(process => process.Id);

    /// <summary>Every process Linux's <c>/proc</c> lists, with <paramref name="field"/> of its <c>stat</c>: one pass over the table.</summary>
    private static List<(int Id, long Value)> Proc(int field)
    {
        var found = new List<(int Id, long Value)>();

        foreach (var id in Kernel.Listed())
        {
            if (FieldOf(id, field) is { } value)
            {
                found.Add((id, value));
            }
        }

        return found;
    }

    /// <summary><paramref name="field"/> of the <c>stat</c> Linux keeps of the process <paramref name="id"/> names, read now; none where it has gone.</summary>
    private static long? FieldOf(int id, int field)
    {
        try
        {
            return ProcStat.Number(ProcStat.FieldsAfterName(Kernel.Stat(id)), field);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Ends every process in <paramref name="job"/> at once, on Windows, keeping each by a handle in <paramref name="ended"/>,
    /// which waits for each to go: the job's members are listed and kept before it is ended - each checked to be in the
    /// job by its own handle, so an id taken since the list was read is never kept, and kept by one that waits for its end
    /// and asks when it was created, all the job's own end leaves to ask of it, so a member whose own security refuses
    /// another its end is waited for all the same - and listed again once each kept one has gone, the job ended again for
    /// any found then, made while it was being ended.
    /// </summary>
    /// <param name="job">The job, held by whoever made it.</param>
    /// <param name="ended">What keeps each process ended.</param>
    public static void EndJob(SafeWaitHandle job, EndedProcesses ended)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(ended);

        _ = KeepMembers(job, ended);
        ended.EndJob(job);
        ended.LookAgainWith(
            () =>
            {
                var found = KeepMembers(job, ended);

                if (found > 0)
                {
                    ended.EndJob(job);
                }

                return found;
            });
    }

    /// <summary>Keeps every member of <paramref name="job"/> not kept yet; how many it kept.</summary>
    private static int KeepMembers(SafeWaitHandle job, EndedProcesses ended)
    {
        var kept = 0;

        foreach (var id in Members(job).Where(id => ended.Kept(id) is null))
        {
            if (Opened(id, Windows.WaitAndRead, failed: null) is not { } handle)
            {
                continue;
            }

            if (!Windows.IsProcessInJob(handle, job, out var member) || !member)
            {
                handle.Dispose();
                continue;
            }

            if (ended.Keep(new KeptProcess(id, handle, CreatedOf(handle))))
            {
                kept++;
            }
        }

        return kept;
    }

    /// <summary>The ids of the processes <paramref name="job"/> holds, as Windows lists them now; none where it will not say.</summary>
    /// <param name="job">The job.</param>
    internal static List<int> Members(SafeWaitHandle job)
    {
        var room = Windows.IdListOffset + IntPtr.Size;

        while (true)
        {
            var list = Marshal.AllocHGlobal(room);

            try
            {
                if (Windows.QueryInformationJobObject(job, Windows.JobObjectBasicProcessIdList, list, (uint)room, out _))
                {
                    var listed = Marshal.ReadInt32(list, sizeof(int));

                    return [.. Enumerable.Range(0, listed).Select(index => (int)Marshal.ReadIntPtr(list, Windows.IdListOffset + (index * IntPtr.Size)))];
                }

                if (Marshal.GetLastPInvokeError() != Windows.MoreData)
                {
                    return [];
                }

                // Room for every member the job says it holds, and twice what was given where it says no more.
                var assigned = Marshal.ReadInt32(list);
                var given = (room - Windows.IdListOffset) / IntPtr.Size;

                room = checked(Windows.IdListOffset + (Math.Max(assigned, given * 2) * IntPtr.Size));
            }
            finally
            {
                Marshal.FreeHGlobal(list);
            }
        }
    }

    /// <summary>The process table, or why it could not be read: a reading that fails takes nothing from the stop it was taken for.</summary>
    private static async Task<(ProcessTableReading? Reading, string? Unread)> ReadAsync(Func<CancellationToken, Task<ProcessTableReading>> read)
    {
        try
        {
            return (await read(CancellationToken.None).ConfigureAwait(false), null);
        }
        catch (Exception ex) when (ex is ProgramStartException or IOException or UnauthorizedAccessException or Win32Exception)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>What a session or a group ended without its table did not reach, and why.</summary>
    private static string Unreached(string why, string held, string ended)
        => $"the process table could not be read to end {held} ({why}): {ended}, "
            + "and what it started outside the group was not reached";

    /// <summary>
    /// Every process of <paramref name="reading"/> in the session or the group <paramref name="leader"/>
    /// leads, and while the leader stands every one in its tree: the leader itself among them.
    /// </summary>
    /// <remarks>
    /// The tree is read only while its root still leads what it led. One that has ended is no
    /// one's parent - what it started was handed on - and its id may already be another process's,
    /// whose tree this run never started.
    /// </remarks>
    private static List<SampledProcess> Members(ProcessTableReading reading, int leader, Func<int, bool> led)
    {
        var byId = reading.Processes.GroupBy(process => process.Id).ToDictionary(same => same.Key, same => same.First());
        var stands = led(leader);

        return [.. byId.Values.Where(process => led(process.Id) || (stands && ProcessSampler.InHarnessTree(process, byId, leader)))];
    }

    /// <summary>
    /// The job object a contained run is held in on Windows, named so the process the engine starts
    /// can join the one made for it, and ending every member when its last handle closes.
    /// </summary>
    /// <param name="name">The job's name, unique to this run of this tool.</param>
    /// <returns>The job's one lasting handle, held by whoever made the job.</returns>
    /// <exception cref="Win32Exception">The job could not be made or given its limit.</exception>
    public static SafeWaitHandle CreateJob(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var job = Windows.CreateJobObjectW(IntPtr.Zero, name);

        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Every member ends when the last handle closes, so what a killed engine leaves behind on
        // Windows ends with it however it left it: the handle this returns is disposed by the run
        // that made it, and by the engine's own end.
        var extended = new Windows.JobObjectExtendedLimitInformation
        {
            Basic = new() { LimitFlags = Windows.KillOnJobClose },
        };

        if (!Windows.SetInformationJobObject(job, Windows.JobObjectExtendedLimit, ref extended, (uint)Marshal.SizeOf<Windows.JobObjectExtendedLimitInformation>()))
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            job.Dispose();
            throw error;
        }

        return job;
    }

    /// <summary>
    /// Ends every process in <paramref name="job"/> at once, on Windows: the machine's own end of a job, which
    /// <see cref="EndJob"/> makes once it has kept each member.
    /// </summary>
    /// <param name="job">The job, held by whoever made it.</param>
    public static void Terminate(SafeWaitHandle job)
        => _ = Windows.TerminateJobObject(job, exitCode: unchecked((uint)-1));

    /// <summary>
    /// The Linux and macOS half of the step: a session and group of its own, or the group alone,
    /// then Landlock's scope where it is asked, then the program become under the same process id - tried again while the
    /// file is busy, as any start is - and the failure said where it could not be.
    /// </summary>
    private static int PlaceOnPosix(bool group, bool scope, string program, IReadOnlyList<string> arguments, ContainExec.StartStatus status)
    {
        var calls = Posix.Calls();

        if (group ? calls.JoinGroup() != 0 : calls.StartSession() == -1)
        {
            status.Failed(group ? ContainExec.Stage.Group : ContainExec.Stage.Session, Marshal.GetLastPInvokeError());
            return ContainExec.Failed;
        }

        // Entered on this thread, the one the program is become from, so the program and all it starts are inside the domain.
        if (scope && Landlock.Scope() is var refused and not 0)
        {
            status.Failed(ContainExec.Stage.Scope, refused);
            return ContainExec.Failed;
        }

        // Said before the program is become, which closes the channel: a channel closed after this
        // line and nothing more is a program that is running. The start said with it is this process's,
        // which the program it becomes keeps: read here, while it stands, it is what a record of the
        // child tells it from a later process by, however soon the program ends.
        status.Placed(new ProcessIdentity(new HostPlatform()).Current);

        try
        {
            ProcessRunner.StartWhileBusy(() => throw new Win32Exception(calls.Become(program, [program, .. arguments])));
        }
        catch (Win32Exception ex)
        {
            status.Failed(ContainExec.Stage.Start, ex.NativeErrorCode);
        }

        return ContainExec.Failed;
    }

    /// <summary>
    /// The Windows half of the step: the job joined where one is named, the program started in a
    /// process group of its own, the start said, and the program waited for with no console signal
    /// ending the step before it.
    /// </summary>
    private static int PlaceOnWindows(string? job, string program, IReadOnlyList<string> arguments, ContainExec.StartStatus status, TextWriter error)
    {
        if (job is { } named && !JoinJob(named, out var why))
        {
            status.Failed(ContainExec.Stage.Job, why);
            return ContainExec.Failed;
        }

        SafeWaitHandle started;

        try
        {
            started = StartInOwnGroup(program, arguments);
        }
        catch (Win32Exception ex)
        {
            status.Failed(ContainExec.Stage.Start, ex.NativeErrorCode);
            return ContainExec.Failed;
        }

        using (started)
        {
            status.Placed();
            status.Dispose();

            // The step stands in for the program and ends when it does: an interruption sent to its
            // console reaches the program by the program's own group, where it reaches it at all.
            Windows.IgnoreInterruptions();

            try
            {
                return Wait(started);
            }
            catch (Win32Exception ex)
            {
                error.WriteLine($"contain-exec could not learn how '{program}', which it started, ended: {ex.Message}");
                return ContainExec.Failed;
            }
        }
    }

    /// <summary>
    /// Whether this process joined the job <paramref name="name"/> names, on Windows, so that
    /// everything it starts is born in it.
    /// </summary>
    /// <param name="name">The job's name, as the engine that made it gave it.</param>
    /// <param name="why">Why it could not be joined, where it was not.</param>
    private static bool JoinJob(string name, out int why)
    {
        var job = Windows.OpenJobObjectW(Windows.JobAllAccess, inheritHandle: false, name);

        if (job.IsInvalid)
        {
            // Read before anything else of this method can set its own last error.
            why = Marshal.GetLastPInvokeError();
            return false;
        }

        try
        {
            // Membership outlives the handle: this one is only how the joining is done, and closing
            // it takes nothing from the job, whose lasting handle the engine holds.
            var joined = Windows.AssignProcessToJobObject(job, Windows.GetCurrentProcess());

            why = joined ? 0 : Marshal.GetLastPInvokeError();

            return joined;
        }
        finally
        {
            job.Dispose();
        }
    }

    /// <summary>
    /// Starts <paramref name="program"/> on Windows in a process group of its own, inheriting this
    /// process's standard streams, and hands its handle back to wait on.
    /// </summary>
    private static SafeWaitHandle StartInOwnGroup(string program, IReadOnlyList<string> arguments)
    {
        // The streams this process was given are what the program reads and writes: they are the
        // process runner's own pipes, handed through, so the one owner of reading a child's output
        // keeps reading the child it read before.
        var startup = new Windows.StartupInfo
        {
            Size = Marshal.SizeOf<Windows.StartupInfo>(),
            Flags = Windows.UseStandardHandles,
            StandardInput = Windows.Standard(Windows.StandardInput),
            StandardOutput = Windows.Standard(Windows.StandardOutput),
            StandardError = Windows.Standard(Windows.StandardError),
        };

        // A path holds no quote, so the program's name is quoted as it is: it is read back by other
        // rules than the arguments after it.
        var commandLine = $"\"{program}\" {DetachedProcessLauncher.CommandLine(arguments)}";

        if (!Windows.CreateProcessW(
                null,
                [.. commandLine, '\0'],
                IntPtr.Zero,
                IntPtr.Zero,
                inheritHandles: true,
                Windows.NewProcessGroup,
                IntPtr.Zero,
                null,
                ref startup,
                out var started))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        new SafeWaitHandle(started.Thread, ownsHandle: true).Dispose();

        return new SafeWaitHandle(started.Process, ownsHandle: true);
    }

    /// <summary>Waits for <paramref name="process"/> to end and says how it ended.</summary>
    /// <returns>The process's own exit code.</returns>
    /// <exception cref="Win32Exception">The wait, or the reading of the exit code, failed: the step's own failure, never the program's code.</exception>
    private static int Wait(SafeWaitHandle process)
    {
        if (Windows.WaitForSingleObject(process, Windows.WaitForever) == Windows.WaitFailed
            || !Windows.GetExitCodeProcess(process, out var code))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return unchecked((int)code);
    }

    /// <summary>How the C library is reached, bound once: the session, group, signal, environment and exec calls.</summary>
    private sealed class Posix
    {
        /// <summary>The signal that asks a process to end.</summary>
        public const int AskToEnd = 15;

        /// <summary>The signal a process cannot refuse.</summary>
        public const int Kill = 9;

        /// <summary>ESRCH: no process, or no group, carries the id signalled.</summary>
        public const int NoSuchProcess = 3;

        /// <summary><c>PROC_PGRP_ONLY</c>: libproc's listing of the processes in one group.</summary>
        public const uint ByGroup = 2;

        /// <summary><c>PROC_PPID_ONLY</c>: libproc's listing of the processes one process is the parent of.</summary>
        public const uint ByParent = 6;

        /// <summary>
        /// SIGSTOP: the signal that holds a process still where it stands, which it cannot refuse - numbered 17 on macOS and
        /// 19 on Linux.
        /// </summary>
        public static int HoldStill => OperatingSystem.IsMacOS() ? 17 : 19;

        /// <summary>
        /// The ids libproc lists as <paramref name="type"/> asks of <paramref name="of"/>, on macOS - every one of them, the
        /// room it is read into doubled until the listing fits in it with room to spare.
        /// </summary>
        /// <param name="type">What is listed: <see cref="ByParent"/> or <see cref="ByGroup"/>.</param>
        /// <param name="of">The parent, or the group.</param>
        public static List<int> Listed(uint type, int of)
        {
            // Asked with no room, the listing says how many bytes it would take.
            var bytes = ListPids(type, (uint)of, null, 0);

            while (bytes > 0)
            {
                var room = new int[Math.Max(bytes / sizeof(int), 1) * 2];
                var listed = ListPids(type, (uint)of, room, room.Length * sizeof(int));

                if (listed <= 0)
                {
                    return [];
                }

                if (listed < room.Length * sizeof(int))
                {
                    return [.. room.Take(listed / sizeof(int)).Where(id => id > 0)];
                }

                bytes = listed;
            }

            return [];
        }

        /// <summary><c>proc_listpids</c>: the ids of the processes of one kind, in bytes written; with no room, the bytes the listing would take.</summary>
        [DllImport("libc", EntryPoint = "proc_listpids", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
        private static extern int ListPids(uint type, uint typeInfo, int[]? buffer, int bufferSize);

        /// <summary>The names the C library is looked for under, in this order, the C library of Linux among them and the system library of macOS last.</summary>
        public static readonly IReadOnlyList<string> Libraries = ["libc.so.6", "libc.so", "libc", "libSystem.dylib"];

        private static Posix? _bound;

        private readonly StartSessionFunction _startSession;
        private readonly JoinGroupFunction _joinGroup;
        private readonly SignalFunction _signal;
        private readonly BecomeFunction _become;
        private readonly IdOfFunction _sessionOf;
        private readonly IdOfFunction _groupOf;
        private readonly UnsetFunction _unset;

        private Posix(IntPtr library)
        {
            _startSession = Bound<StartSessionFunction>(library, "setsid");
            _joinGroup = Bound<JoinGroupFunction>(library, "setpgid");
            _signal = Bound<SignalFunction>(library, "kill");
            // Not execvp: given a file with no interpreter line, that runs it under /bin/sh where a
            // start made any other way fails, and the program is always a whole path by now.
            _become = Bound<BecomeFunction>(library, "execv");
            _sessionOf = Bound<IdOfFunction>(library, "getsid");
            _groupOf = Bound<IdOfFunction>(library, "getpgid");
            _unset = Bound<UnsetFunction>(library, "unsetenv");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int UnsetFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int StartSessionFunction();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int JoinGroupFunction(int process, int group);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int SignalFunction(int process, int signal);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int BecomeFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string program, IntPtr arguments);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int IdOfFunction(int process);

        /// <summary>The calls, bound; on any Linux or macOS there is a C library holding all seven.</summary>
        public static Posix Calls()
        {
            if (_bound is { } bound)
            {
                return bound;
            }

            foreach (var name in Libraries)
            {
                if (NativeLibrary.TryLoad(name, out var library) && NativeLibrary.TryGetExport(library, "setsid", out _))
                {
                    return _bound = new Posix(library);
                }
            }

            throw new InvalidOperationException($"no C library loads as {string.Join(", ", Libraries.SkipLast(1))} or {Libraries[^1]}");
        }

        /// <summary>One call of <paramref name="library"/>, by name.</summary>
        private static T Bound<T>(IntPtr library, string name)
            where T : Delegate
            => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        /// <summary>The new session, as the leader's process id; -1 of a process already leading one.</summary>
        public int StartSession() => _startSession();

        /// <summary>The group of its own; 0 where it was made.</summary>
        public int JoinGroup() => _joinGroup(0, 0);

        /// <summary>Signals a whole group (by negating its leader's id), as the C library's <c>kill</c> does; -1 where nothing could be signalled.</summary>
        public int SignalGroup(int groupId, int signal) => _signal(-groupId, signal);

        /// <summary>Signals one process; -1 where it could not be signalled.</summary>
        public int Signal(int process, int signal) => _signal(process, signal);

        /// <summary>The session <paramref name="process"/> is in, by its leader's id; -1 where it cannot be told.</summary>
        public int SessionOf(int process) => _sessionOf(process);

        /// <summary>The group <paramref name="process"/> is in, by its leader's id; -1 where it cannot be told.</summary>
        public int GroupOf(int process) => _groupOf(process);

        /// <summary>Takes <paramref name="name"/> from the environment a program this process becomes is handed; 0 where it is gone, -1 where it could not be taken.</summary>
        public int Unset(string name) => _unset(name);

        /// <summary>
        /// Becomes the program by its whole path, its own name first among <paramref name="arguments"/>,
        /// with nothing looked up and nothing run in its place; of a success there is no return, so
        /// this is the failure number the C library reports.
        /// </summary>
        public int Become(string program, IReadOnlyList<string> arguments)
        {
            // The array the C library reads is ended by a null pointer. Read by nothing once the
            // program is become, and freed where it could not be, before it is tried again.
            var spelled = new IntPtr[arguments.Count + 1];
            var plain = Marshal.AllocHGlobal(spelled.Length * IntPtr.Size);

            try
            {
                for (var index = 0; index < arguments.Count; index++)
                {
                    spelled[index] = Marshal.StringToCoTaskMemUTF8(arguments[index]);
                }

                for (var index = 0; index < spelled.Length; index++)
                {
                    Marshal.WriteIntPtr(plain, index * IntPtr.Size, spelled[index]);
                }

                _ = _become(program, plain);

                return Marshal.GetLastPInvokeError();
            }
            finally
            {
                foreach (var argument in spelled.Where(argument => argument != IntPtr.Zero))
                {
                    Marshal.FreeCoTaskMem(argument);
                }

                Marshal.FreeHGlobal(plain);
            }
        }
    }

    /// <summary>The Windows side: job objects, one process started in a group of its own, and the console's signals.</summary>
    private static class Windows
    {
        /// <summary>The limit flag that ends a job's members when its last handle closes.</summary>
        public const uint KillOnJobClose = 0x00002000;

        /// <summary>The class of information a job's limits are set through.</summary>
        public const int JobObjectExtendedLimit = 9;

        /// <summary>Every right a job object takes.</summary>
        public const uint JobAllAccess = 0x1F001F;

        /// <summary>A process group of its own, which an interruption typed where it was started does not reach.</summary>
        public const uint NewProcessGroup = 0x00000200;

        /// <summary>The startup flag that says the three handles below are the process's streams.</summary>
        public const int UseStandardHandles = 0x00000100;

        /// <summary>Standard input, as a number the handle call takes.</summary>
        public const int StandardInput = -10;

        /// <summary>Standard output, as a number the handle call takes.</summary>
        public const int StandardOutput = -11;

        /// <summary>Standard error, as a number the handle call takes.</summary>
        public const int StandardError = -12;

        /// <summary>The wait that ends when the process does.</summary>
        public const uint WaitForever = unchecked((uint)-1);

        /// <summary>The wait that failed.</summary>
        public const uint WaitFailed = unchecked((uint)-1);

        /// <summary>The wait that found the object not yet signalled.</summary>
        public const uint WaitTimeout = 0x102;

        /// <summary>PROCESS_QUERY_LIMITED_INFORMATION: the right that asks when a process was created, and whether it is in a job.</summary>
        public const uint ReadCreation = 0x1000;

        /// <summary>The rights a member of a job a stop keeps is opened with: to wait for its end and to ask when it was created - the job's own end ends it.</summary>
        public const uint WaitAndRead = ReadCreation | 0x0010_0000;

        /// <summary>The rights a process of a tree a stop keeps is opened with: to end it, to wait for its end, and to ask when it was created.</summary>
        public const uint EndAndWait = 0x0001 | WaitAndRead;

        /// <summary>DUPLICATE_SAME_ACCESS: a handle copied holding every right the handle copied holds.</summary>
        public const uint SameAccess = 0x2;

        /// <summary>ERROR_INVALID_PARAMETER: what opening a process that has gone answers.</summary>
        public const int InvalidParameter = 87;

        /// <summary>ERROR_MORE_DATA: a listing that outgrew the room it was given.</summary>
        public const int MoreData = 234;

        /// <summary><c>JobObjectBasicProcessIdList</c>: the ids of a job's members.</summary>
        public const int JobObjectBasicProcessIdList = 3;

        /// <summary>Where a job's listing of its members begins its ids: after the two counts, aligned for a pointer.</summary>
        public const int IdListOffset = 8;

        /// <summary>The console's interruption, Ctrl+C.</summary>
        private const uint Interrupt = 0;

        /// <summary>The console's break, Ctrl+Break.</summary>
        private const uint Break = 1;

        /// <summary>
        /// The handler that takes the console's interruption and break as dealt with, held for the
        /// life of the process so the collector never frees what the console calls.
        /// </summary>
        private static readonly ConsoleSignalHandler Ignored = signal => signal is Interrupt or Break;

        /// <summary>A console signal's handler: whether it dealt with the signal, so none after it is asked.</summary>
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate bool ConsoleSignalHandler(uint signal);

        /// <summary>The handle this process was given for <paramref name="standard"/>.</summary>
        public static IntPtr Standard(int standard) => GetStdHandle(standard);

        /// <summary>
        /// Takes every interruption and break sent to this process's console as dealt with: handlers
        /// are asked from the last added, so the ones the command line added before are never asked.
        /// </summary>
        public static void IgnoreInterruptions() => _ = SetConsoleCtrlHandler(Ignored, add: true);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeWaitHandle CreateJobObjectW(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(
            SafeWaitHandle job,
            int informationClass,
            ref JobObjectExtendedLimitInformation information,
            uint length);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeWaitHandle OpenJobObjectW(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, string name);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeWaitHandle job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeWaitHandle job, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateProcess(SafeProcessHandle process, uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(
            IntPtr sourceProcess,
            SafeProcessHandle source,
            IntPtr targetProcess,
            out SafeProcessHandle target,
            uint desiredAccess,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
            uint options);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeWaitHandle job, int informationClass, IntPtr information, uint length, out uint returned);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsProcessInJob(SafeProcessHandle process, SafeWaitHandle job, [MarshalAs(UnmanagedType.Bool)] out bool result);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        internal static extern IntPtr GetStdHandle(int standard);

        [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetConsoleCtrlHandler(ConsoleSignalHandler handler, [MarshalAs(UnmanagedType.Bool)] bool add);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(
            string? applicationName,
            char[] commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfo startupInfo,
            out ProcessInformation processInformation);

        /// <summary>STARTUPINFOW: the process's streams, where the flag below says they are its own.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            public int Size;
            public IntPtr Reserved;
            public IntPtr Desktop;
            public IntPtr Title;
            public int X;
            public int Y;
            public int Width;
            public int Height;
            public int Columns;
            public int Rows;
            public int FillAttribute;
            public int Flags;
            public short ShowWindow;
            public short ReservedSize;
            public IntPtr ReservedBytes;
            public IntPtr StandardInput;
            public IntPtr StandardOutput;
            public IntPtr StandardError;
        }

        /// <summary>PROCESS_INFORMATION: the new process and its first thread.</summary>
        [StructLayout(LayoutKind.Sequential)]
        internal struct ProcessInformation
        {
            public IntPtr Process;
            public IntPtr Thread;
            public int ProcessId;
            public int ThreadId;
        }

        /// <summary>The limits a job object is given.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public nuint MinimumWorkingSetSize;
            public nuint MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public nuint Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        /// <summary>The limits a job object is given, with what is counted beside them.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation Basic;
            public IoCounters Counted;
            public nuint ProcessMemoryLimit;
            public nuint JobMemoryLimit;
            public nuint JobMemoryPeak;
            public nuint ProcessMemoryPeak;
        }

        /// <summary>What a job object counts of the input and output of its members.</summary>
        [StructLayout(LayoutKind.Sequential)]
        public struct IoCounters
        {
            public ulong ReadOperations;
            public ulong WriteOperations;
            public ulong OtherOperations;
            public ulong ReadTransferred;
            public ulong WriteTransferred;
            public ulong OtherTransferred;
        }
    }
}
