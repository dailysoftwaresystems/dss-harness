using System.ComponentModel;
using System.Runtime.InteropServices;
using DssHarness.Core.Execution;
using DssHarness.Core.Platform;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The calls containment is made of, below the platform seam: the C library's session, group,
/// signal and exec calls on Linux and macOS, and the job objects, process creation and console
/// signals of Windows.
/// Each side is called only where that system runs, and the choice between them is made here and
/// nowhere above.
/// </summary>
internal static class Containment
{
    /// <summary>How often a wait for an ending looks again.</summary>
    private static readonly TimeSpan GraceSlice = TimeSpan.FromMilliseconds(20);

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
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">The program's arguments.</param>
    /// <param name="status">The channel the process runner reads the start from.</param>
    /// <param name="error">Where a failure of the step after the program started is said.</param>
    /// <returns>
    /// The program's own exit code on Windows; on Linux and macOS no return where the program was
    /// become. <see cref="ContainExec.Failed"/> where the step failed, the failure said on the channel.
    /// </returns>
    public static int Place(string? job, bool group, string program, IReadOnlyList<string> arguments, ContainExec.StartStatus status, TextWriter error)
        => OperatingSystem.IsWindows()
            ? PlaceOnWindows(job, program, arguments, status, error)
            : PlaceOnPosix(group, program, arguments, status);

    /// <summary>
    /// Ends a contained child's whole session on Linux and macOS - or, where <paramref name="group"/>, the whole process
    /// group a child apart from the terminal leads within its starter's session: every process in it, and while the
    /// child stands every one in its tree, is asked to end - the group as one, each member outside
    /// the group on its own - and every one still standing once they have all gone or
    /// <paramref name="grace"/> is spent is killed.
    /// </summary>
    /// <param name="leader">The child, which leads the session and the group, or the group alone, by its own process id.</param>
    /// <param name="read">Reads the machine's process table.</param>
    /// <param name="grace">How long the asking signal is given to be obeyed.</param>
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
    /// under the same id and start, so an id given to another process meanwhile is left alone. A
    /// session with nothing in it is not read again, nor signalled. A process that left
    /// both the session and the tree - a daemon that made a session of its own and whose parent
    /// has gone - is reached by nothing here. A group is ended the same way, by its own members and
    /// the leader's tree, never by the session it shares with the process that started it.
    /// </para>
    /// </remarks>
    public static async Task<HeldEnding> EndSessionAsync(int leader, Func<CancellationToken, Task<ProcessTableReading>> read, TimeSpan grace, bool group = false)
    {
        ArgumentNullException.ThrowIfNull(read);

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

        // The signal with no number asks nothing and reports everything: a group or a process it
        // cannot signal is one that has ended.
        for (var waited = TimeSpan.Zero;
             waited < grace && !(calls.SignalGroup(leader, 0) == -1 && members.All(member => calls.Signal(member.Id, 0) == -1));
             waited += GraceSlice)
        {
            await Task.Delay(GraceSlice).ConfigureAwait(false);
        }

        calls.SignalGroup(leader, Posix.Kill);

        if (unread is not null)
        {
            return new HeldEnding(Found: true, Unreached(unread, held, "its group was ended"));
        }

        var (again, unreadAgain) = await ReadAsync(read).ConfigureAwait(false);

        if (again is null)
        {
            // Still a member of the session - or the group - is still one of the run's, whoever its
            // parent: only what left it can no longer be told from another process under the same id.
            foreach (var member in members.Where(member => In(member.Id)))
            {
                calls.Signal(member.Id, Posix.Kill);
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
            calls.Signal(member.Id, Posix.Kill);
        }

        return new HeldEnding(Found: true, null);
    }

    /// <summary>
    /// Whether the process group <paramref name="leader"/> leads still holds a process, on Linux and macOS: a group with
    /// nothing left in it cannot be signalled, and the signal with no number asks nothing of one that can.
    /// </summary>
    /// <param name="leader">The id of the group, its leader's whether or not the leader stands.</param>
    public static bool GroupHolds(int leader) => Posix.Calls().SignalGroup(leader, 0) != -1;

    /// <summary>
    /// Kills every process in the group <paramref name="leader"/> leads, on Linux and macOS, at once: what a stop of a child
    /// that leads a group of its own reaches beside its tree - a process its tree handed on, still in the group.
    /// </summary>
    /// <param name="leader">The id of the group.</param>
    public static void KillGroup(int leader) => Posix.Calls().SignalGroup(leader, Posix.Kill);

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

    /// <summary>Ends every process in <paramref name="job"/> at once, on Windows: the container's own stop.</summary>
    /// <param name="job">The job, held by whoever made it.</param>
    public static void Terminate(SafeWaitHandle job)
        => _ = Windows.TerminateJobObject(job, exitCode: unchecked((uint)-1));

    /// <summary>
    /// The Linux and macOS half of the step: a session and group of its own, or the group alone,
    /// then the program become under the same process id - tried again while the file is busy, as
    /// any start is - and the failure said where it could not be.
    /// </summary>
    private static int PlaceOnPosix(bool group, string program, IReadOnlyList<string> arguments, ContainExec.StartStatus status)
    {
        var calls = Posix.Calls();

        if (group ? calls.JoinGroup() != 0 : calls.StartSession() == -1)
        {
            status.Failed(group ? ContainExec.Stage.Group : ContainExec.Stage.Session, Marshal.GetLastPInvokeError());
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

    /// <summary>How the C library is reached, bound once: the session, group, signal and exec calls.</summary>
    private sealed class Posix
    {
        /// <summary>The signal that asks a process to end.</summary>
        public const int AskToEnd = 15;

        /// <summary>The signal a process cannot refuse.</summary>
        public const int Kill = 9;

        /// <summary>The names the C library is looked for under, in this order, the C library of Linux among them and the system library of macOS last.</summary>
        public static readonly IReadOnlyList<string> Libraries = ["libc.so.6", "libc.so", "libc", "libSystem.dylib"];

        private static Posix? _bound;

        private readonly StartSessionFunction _startSession;
        private readonly JoinGroupFunction _joinGroup;
        private readonly SignalFunction _signal;
        private readonly BecomeFunction _become;
        private readonly IdOfFunction _sessionOf;
        private readonly IdOfFunction _groupOf;

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
        }

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

        /// <summary>The calls, bound; on any Linux or macOS there is a C library holding all six.</summary>
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
