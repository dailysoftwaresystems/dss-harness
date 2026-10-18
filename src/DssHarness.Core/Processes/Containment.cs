using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DssHarness.Core.Processes;

/// <summary>
/// The calls containment is made of, below the platform seam: the C library's session, group,
/// signal and exec calls on Linux and macOS, and the job objects and process creation of Windows.
/// Each side is called only where that system runs.
/// </summary>
internal static class Containment
{
    /// <summary>
    /// Makes this process the leader of a new session and process group of its own, on Linux and
    /// macOS: no terminal reaches into a session of one's own, and the group is what a whole end
    /// is signalled through.
    /// </summary>
    /// <returns>Whether the session was made; <see langword="false"/> where the process already leads a group.</returns>
    public static bool StartSession()
        => Posix.Calls().StartSession() != -1;

    /// <summary>
    /// Puts this process in a process group of its own, on Linux and macOS, within the session it
    /// is in: an interruption typed at the terminal goes to a group, and this one is not that group.
    /// </summary>
    /// <returns>Whether the group was made.</returns>
    public static bool JoinOwnGroup()
        => Posix.Calls().JoinGroup() == 0;

    /// <summary>
    /// Signals a whole process group, as a contained child is ended: asked to end first, and every
    /// member still standing once <paramref name="grace"/> is spent is killed.
    /// </summary>
    /// <param name="groupId">The group, by the process id of its leader.</param>
    /// <param name="grace">How long the asking signal is given to be obeyed.</param>
    public static void EndGroup(int groupId, TimeSpan grace)
    {
        Posix.Calls().SignalGroup(groupId, Posix.AskToEnd);
        StillStanding(groupId, grace);
        Posix.Calls().SignalGroup(groupId, Posix.Kill);
    }

    /// <summary>Replaces this process with <paramref name="program"/>, on Linux and macOS, under the same process id.</summary>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">Its arguments, the first named to it as itself.</param>
    /// <remarks>
    /// The containment made above holds, and nothing of this process is left to keep, hand over or
    /// end. Of a success there is no return; the number that comes back is the failure the C
    /// library reports, for the caller to say.
    /// </remarks>
    /// <returns>The failure number, where the program could not be become.</returns>
    public static int Become(string program, IReadOnlyList<string> arguments)
        => Posix.Calls().Become(program, [program, .. arguments]);

    /// <summary>
    /// The job object a contained run is held in on Windows, named so the process the engine starts
    /// can join the one made for it, and ending every member when its last handle closes.
    /// </summary>
    /// <param name="name">The job's name, unique to this run of this tool.</param>
    /// <returns>The job's one lasting handle, held by whoever made the job.</returns>
    public static SafeWaitHandle CreateJob(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var job = Windows.CreateJobObjectW(IntPtr.Zero, name);

        if (job.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        // Every member ends when the last handle closes, so what a killed engine leaves behind
        // ends with it however it left it: the handle this returns is disposed by the run that
        // made it, and by the engine's own end.
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
    /// Whether this process joined the job <paramref name="name"/> names, on Windows, so that
    /// everything it starts is born in it.
    /// </summary>
    /// <param name="name">The job's name, as the engine that made it gave it.</param>
    /// <param name="why">Why it could not be joined, where it was not; said by the caller.</param>
    public static bool JoinJob(string name, out int why)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

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

    /// <summary>Ends every process in <paramref name="job"/> at once, on Windows: the container's own stop.</summary>
    /// <param name="job">The job, held by whoever made it.</param>
    public static void Terminate(SafeWaitHandle job)
        => _ = Windows.TerminateJobObject(job, exitCode: unchecked((uint)-1));

    /// <summary>
    /// Starts <paramref name="program"/> on Windows in a process group of its own, inheriting this
    /// process's standard streams, and hands its handle back to wait on.
    /// </summary>
    /// <param name="program">The program, by its resolved path.</param>
    /// <param name="arguments">Its arguments.</param>
    /// <returns>The started process, not waited for here.</returns>
    public static SafeWaitHandle StartInOwnGroup(string program, IReadOnlyList<string> arguments)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(program);

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
    /// <param name="process">The process, as <see cref="StartInOwnGroup"/> started it.</param>
    /// <returns>The process's own exit code.</returns>
    public static int Wait(SafeWaitHandle process)
    {
        if (Windows.WaitForSingleObject(process, Windows.WaitForever) == Windows.WaitFailed)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        return Windows.GetExitCodeProcess(process, out var code) ? unchecked((int)code) : -1;
    }

    /// <summary>Whether a signal has been obeyed by every member of <paramref name="groupId"/> once <paramref name="grace"/> is spent.</summary>
    /// <param name="groupId">The group, by the process id of its leader.</param>
    /// <param name="grace">How long to keep asking.</param>
    private static void StillStanding(int groupId, TimeSpan grace)
    {
        // The signal with no number asks nothing and reports everything: a group it cannot signal
        // is a group with nothing left in it.
        for (var waited = TimeSpan.Zero; waited < grace; waited += GraceSlice)
        {
            if (Posix.Calls().SignalGroup(groupId, 0) == -1)
            {
                return;
            }

            Thread.Sleep(GraceSlice);
        }
    }

    /// <summary>How often a wait for an ending looks again.</summary>
    private static readonly TimeSpan GraceSlice = TimeSpan.FromMilliseconds(20);

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

        private Posix(IntPtr library)
        {
            _startSession = Bound<StartSessionFunction>(library, "setsid");
            _joinGroup = Bound<JoinGroupFunction>(library, "setpgid");
            _signal = Bound<SignalFunction>(library, "kill");
            _become = Bound<BecomeFunction>(library, "execvp");
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int StartSessionFunction();

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int JoinGroupFunction(int process, int group);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int SignalFunction(int process, int signal);

        [UnmanagedFunctionPointer(CallingConvention.Cdecl, SetLastError = true)]
        private delegate int BecomeFunction([MarshalAs(UnmanagedType.LPUTF8Str)] string program, IntPtr arguments);

        /// <summary>The calls, bound; on any Linux or macOS there is a C library holding all four.</summary>
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

        /// <summary>Signals a whole group (by negating its leader's id), as the C library's <c>kill</c> does.</summary>
        public int SignalGroup(int groupId, int signal) => _signal(-groupId, signal);

        /// <summary>
        /// Becomes the program - by its path where one is given, and by the PATH the child is given
        /// where only a name is; of a success there is no return, so this is the failure number.
        /// </summary>
        public int Become(string program, IReadOnlyList<string> arguments)
        {
            // The program's own name first, and nothing after the last: the array the C library
            // reads is ended by a null pointer. Never freed, and never read again by this code: of
            // a success this process is the program, and of a failure this process is ending.
            var spelled = new IntPtr[arguments.Count + 1];

            for (var index = 0; index < arguments.Count; index++)
            {
                spelled[index] = Marshal.StringToHGlobalAnsi(arguments[index]);
            }

            var plain = Marshal.AllocHGlobal(spelled.Length * IntPtr.Size);

            for (var index = 0; index < spelled.Length; index++)
            {
                Marshal.WriteIntPtr(plain, index * IntPtr.Size, spelled[index]);
            }

            return _become(program, plain);
        }
    }

    /// <summary>The Windows side: job objects, and one process started in a group of its own.</summary>
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

        /// <summary>The handle this process was given for <paramref name="standard"/>.</summary>
        public static IntPtr Standard(int standard) => GetStdHandle(standard);

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
