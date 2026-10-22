using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace DssHarness.Tests;

/// <summary>
/// Behaviours this assembly performs when started as a child process. They give the
/// process tests a program whose timing, output and descendants are exactly known,
/// without depending on a shell or on any other tool being installed.
/// </summary>
internal static class TestChild
{
    /// <summary>
    /// Output is written as UTF-8 explicitly, as git writes it. The console's own encoding
    /// on Windows is a legacy code page, which would make a test of the runner's decoding
    /// measure this child instead.
    /// </summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>What the stand-in for a step that reaches no channel says on its error stream.</summary>
    internal const string ReachedNothing = "the step reached no channel";

    internal static int Run(string mode, string[] arguments)
    {
        using var standardOutput = new StreamWriter(Console.OpenStandardOutput(), Utf8NoBom) { AutoFlush = true };
        using var standardError = new StreamWriter(Console.OpenStandardError(), Utf8NoBom) { AutoFlush = true };

        return mode switch
        {
            "echo-args" => EchoArguments(standardOutput, arguments),
            "echo-command-line" => EchoCommandLine(standardOutput, arguments),
            "echo-crlf" => EchoLinesEndedWithCrlf(standardOutput, arguments),
            "echo-stdin" => EchoStandardInput(standardOutput),
            "stdin-to-file" => StandardInputToFile(arguments),
            "read-line-then-watch" => ReadLineThenWatch(standardOutput, arguments),
            "read-lines-for" => ReadLinesFor(standardOutput, arguments),
            "close-input-then-sleep" => CloseInputThenSleep(arguments),
            "sleep" => Sleep(arguments),
            "print-ids" => PrintIds(standardOutput),
            "spawn-orphan" => SpawnOrphan(standardOutput, arguments),
            "spawn-orphan-and-sleep" => SpawnOrphanAndSleep(standardOutput, arguments),
            "spawn-holder" => SpawnHolder(standardOutput, arguments),
            "spawn-orphan-holder" => SpawnOrphanHolder(standardOutput, arguments),
            "own-group-and-sleep" => OwnGroupAndSleep(arguments),
            "own-session-and-sleep" => OwnSessionAndSleep(arguments),
            "ignore-term-and-sleep" => IgnoreTermAndSleep(arguments),
            "note-term-and-sleep" => NoteTermAndSleep(standardOutput, arguments),
            "say-and-exit" => SayAndExit(standardOutput, standardError, arguments),
            "step-reaching-nothing" => StepReachingNothing(standardError),
            "say-pid-and-sleep" => SayPidAndSleep(standardOutput, arguments),
            "say-pid-then" => SayPidThen(standardOutput, arguments),
            "contain-and-wait" => ContainAndWait(standardOutput, arguments),
            "contain-recorded-and-wait" => ContainRecordedAndWait(standardOutput, arguments),
            "bind-and-wait" => BindAndWait(standardOutput, arguments),
            "interrupt-own-console" => InterruptOwnConsole(standardOutput),
            "print-code-page" => PrintCodePage(standardOutput),
            "print-parent" => PrintParent(standardOutput),
            "stream" => Stream(standardOutput, standardError, arguments),
            "flood" => Flood(standardOutput, arguments),
            "flood-error" => Flood(standardError, arguments),
            "flood-both" => Flood(standardOutput, arguments) + Flood(standardError, arguments),
            "print-file" => PrintFile(standardOutput, arguments),
            "print-bytes" => PrintBytes(arguments),
            "spawn-grandchild" => SpawnGrandchild(arguments),
            "last-resort" => LastResort(standardOutput, arguments),
            "print-env" => PrintEnvironment(standardOutput, arguments),
            "write-file" => WriteFile(arguments),
            "watch-process" => WatchProcess(arguments),
            "link-directory" => LinkDirectory(arguments),
            "claude-code" => ClaudeCode(standardOutput, arguments),
            "flow-interrupted" => FlowServiceTests.InterruptedChild(standardOutput, arguments),
            "exit" => int.Parse(arguments[0], CultureInfo.InvariantCulture),
            _ => 99,
        };
    }

    /// <summary>
    /// Writes <c>arguments[1]</c> into the file <c>arguments[0]</c> names, for a step that has to
    /// actually produce something.
    /// </summary>
    /// <remarks>
    /// Exits zero either way. A step that declares an output and does not write it is the case the
    /// harness has to notice by looking, so this child must be able to succeed at doing nothing.
    /// </remarks>
    private static int WriteFile(string[] arguments)
    {
        if (arguments.Length >= 2)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(arguments[0]) ?? ".");
            File.WriteAllText(arguments[0], arguments[1]);
        }

        // An optional exit code, so a step can produce exactly what it declared and still fail.
        // That is the case a caller has to tell apart from a step that produced nothing.
        return arguments.Length >= 3 ? int.Parse(arguments[2], CultureInfo.InvariantCulture) : 0;
    }

    /// <summary>
    /// Makes <c>arguments[0]</c> a directory holding one file and a link, <c>latest</c>, to the
    /// directory <c>arguments[1]</c>: what a step that keeps a directory of runs with a pointer to the
    /// newest one produces.
    /// </summary>
    private static int LinkDirectory(string[] arguments)
    {
        Directory.CreateDirectory(arguments[0]);
        File.WriteAllText(Path.Combine(arguments[0], "kept.txt"), "kept");
        Directory.CreateSymbolicLink(Path.Combine(arguments[0], "latest"), arguments[1]);
        return 0;
    }

    /// <summary>Writes each argument as a line of its own ended with CRLF, as a Windows program writes one.</summary>
    private static int EchoLinesEndedWithCrlf(TextWriter output, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            output.Write(argument + "\r\n");
        }

        return 0;
    }

    /// <summary>Writes each argument on its own line between brackets, so an empty one is visible.</summary>
    private static int EchoArguments(TextWriter output, string[] arguments)
    {
        foreach (var argument in arguments)
        {
            output.Write("[" + argument + "]\n");
        }

        return 0;
    }

    /// <summary>
    /// Writes the arguments on one line, each between brackets and a space after the one before: what a
    /// witness, read a line at a time, needs to see that two arguments came side by side.
    /// </summary>
    private static int EchoCommandLine(TextWriter output, string[] arguments)
    {
        output.Write(string.Join(' ', arguments.Select(argument => "[" + argument + "]")) + "\n");
        return 0;
    }

    /// <summary>
    /// Reads standard input to its end and writes it back between brackets. Read as UTF-8 bytes,
    /// for the reason output is written that way, and as they came: a byte order mark it began with
    /// is written back, never taken for how to read the rest, as a child reading its input as bytes
    /// would read it as text.
    /// </summary>
    private static int EchoStandardInput(TextWriter output)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom, detectEncodingFromByteOrderMarks: false);
        output.Write("[" + input.ReadToEnd() + "]\n");
        return 0;
    }

    /// <summary>
    /// Reads standard input to its end and writes it into the file <c>arguments[0]</c> names: what a child that saw the end
    /// of its input leaves behind, where its output would not be read.
    /// </summary>
    private static int StandardInputToFile(string[] arguments)
    {
        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        File.WriteAllText(arguments[0], input.ReadToEnd(), Utf8NoBom);
        return 0;
    }

    /// <summary>
    /// Reads one line and writes it back, then waits the given milliseconds for the end of its input:
    /// "ended" when the parent closed it, "held" when it was still open after the wait. The reader is
    /// deliberately left undisposed, since a read may still be pending when this process exits.
    /// </summary>
    private static int ReadLineThenWatch(TextWriter output, string[] arguments)
    {
        var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        output.Write("[" + input.ReadLine() + "]\n");

        var reading = Task.Run(input.Read);
        var ended = reading.Wait(int.Parse(arguments[0], CultureInfo.InvariantCulture)) && reading.Result < 0;

        output.Write(ended ? "ended\n" : "held\n");
        return 0;
    }

    /// <summary>
    /// Takes a line of its input, closes its own end of it, then goes on for the given milliseconds: what a carrier
    /// that stops taking what it is written, and does not exit, looks like to whoever writes to it. The line first,
    /// because whoever writes to it writes a request before any beat. On Windows only: elsewhere the runtime
    /// holds a second descriptor of its input, so closing the first leaves the pipe whole, and a shell that closes its
    /// own is what stands in.
    /// </summary>
    private static int CloseInputThenSleep(string[] arguments)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 98;
        }

        const int StandardInputHandle = -10;

        using (var input = Console.OpenStandardInput())
        {
            while (input.ReadByte() is not (-1 or '\n'))
            {
            }
        }

        CloseHandle(GetStdHandle(StandardInputHandle));
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern nint GetStdHandle(int handle);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    /// <summary>
    /// Reads lines for the given milliseconds, then writes back each it read: what a child that listens for a beat on
    /// its input hears. The reader is left undisposed, since a read may still be pending when this process exits.
    /// </summary>
    private static int ReadLinesFor(TextWriter output, string[] arguments)
    {
        var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);
        var read = new System.Collections.Concurrent.ConcurrentQueue<string>();

        _ = Task.Run(() =>
        {
            while (input.ReadLine() is { } line)
            {
                read.Enqueue(line);
            }
        });

        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));

        foreach (var line in read)
        {
            output.Write("[" + line + "]\n");
        }

        return 0;
    }

    /// <summary>
    /// Writes <c>started</c> and the process <c>arguments[1]</c> names into the file <c>arguments[0]</c> names,
    /// then waits for that process to end and adds <c>ended</c>: what caffeinate -w does with the process it is
    /// given, for a keepAwake that holds a machine awake exactly as long as that process lives.
    /// </summary>
    private static int WatchProcess(string[] arguments)
    {
        var id = int.Parse(arguments[1], CultureInfo.InvariantCulture);

        Directory.CreateDirectory(Path.GetDirectoryName(arguments[0]) ?? ".");
        File.WriteAllText(arguments[0], $"started {id}\n");

        for (var waited = 0; waited < 1200 && Running(id); waited++)
        {
            Thread.Sleep(100);
        }

        File.AppendAllText(arguments[0], "ended\n");
        return 0;

        static bool Running(int id)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }
    }

    private static int Sleep(string[] arguments)
    {
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    /// Writes this process's own id, the id of its process group and of its session: what a test of
    /// containment reads, since where a process stands is the group and session it is in. The
    /// session and group calls are the C library's, and the ids have no spelling on Windows.
    /// </summary>
    private static int PrintIds(TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.Write($"pid={getpid()} pgid={getpgid(0)} sid={getsid(0)}\n");
            return 0;
        }

        return 98;
    }

    /// <summary>
    /// Starts a sleeper of its own, writes its process id, and exits at once: what a test of
    /// containment ends reads, since the sleeper's parent is gone and only the container it was
    /// born in can reach it. On Windows the sleeper inherits no handle: were it to hold the pipe
    /// this process writes to, whoever reads that pipe would wait for the sleeper's own end.
    /// The sleeper is <c>sleep</c> unless <c>arguments[1]</c> names another behaviour, which is
    /// given the milliseconds and every argument after it.
    /// </summary>
    private static int SpawnOrphan(TextWriter output, string[] arguments)
    {
        var milliseconds = arguments.Length > 0 ? arguments[0] : "300000";
        var mode = arguments.Length > 1 ? arguments[1] : "sleep";
        string[] passed = [milliseconds, .. arguments.Skip(2)];

        if (OperatingSystem.IsWindows())
        {
            var commandLine = $"\"{TestHost.DotnetExecutable}\" exec \"{TestHost.AssemblyPath}\" {Core.Processes.DetachedProcessLauncher.CommandLine(passed)}";
            var startup = new StartupInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<StartupInfo>() };

            // The mode travels in the environment the sleeper inherits; set here and taken back at
            // once, it is in the sleeper's and in no other's.
            Environment.SetEnvironmentVariable(TestHost.ChildModeVariable, mode);

            try
            {
                if (CreateProcessW(
                        null,
                        [.. commandLine, '\0'],
                        IntPtr.Zero,
                        IntPtr.Zero,
                        inheritHandles: false,
                        CreateNoWindow | NewProcessGroup,
                        IntPtr.Zero,
                        null,
                        ref startup,
                        out var started))
                {
                    output.Write($"orphan={started.ProcessId}\n");
                    return 0;
                }

                return 99;
            }
            finally
            {
                Environment.SetEnvironmentVariable(TestHost.ChildModeVariable, null);
            }
        }

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);

        foreach (var argument in passed)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[TestHost.ChildModeVariable] = mode;

        using var sleeper = Process.Start(start)
            ?? throw new InvalidOperationException("The sleeper did not start.");

        output.Write($"orphan={sleeper.Id}\n");

        return 0;
    }

    /// <summary>A console of its own, with no window.</summary>
    private const uint CreateNoWindow = 0x08000000;

    /// <summary>A process group of its own.</summary>
    private const uint NewProcessGroup = 0x00000200;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        char[] commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct StartupInfo
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

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr Process;
        public IntPtr Thread;
        public int ProcessId;
        public int ThreadId;
    }

    /// <summary>
    /// Starts, through a middle process that exits at once, a sleeper of its own in this process's
    /// group, writes the sleeper's process id, and goes on sleeping: a stop that reaches the group
    /// ends the sleeper with this process, where a stop that walks the tree cannot - the middle is
    /// gone, the sleeper is no one's child, and the group is the only thing that holds them both.
    /// <c>arguments[2]</c> and after, where given, name the sleeper's behaviour and its arguments,
    /// as <c>spawn-orphan</c> takes them.
    /// </summary>
    private static int SpawnOrphanAndSleep(TextWriter output, string[] arguments)
    {
        var milliseconds = arguments.Length > 0 ? arguments[0] : "300000";

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add(milliseconds);

        foreach (var argument in arguments.Skip(2))
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[TestHost.ChildModeVariable] = "spawn-orphan";

        using var middle = Process.Start(start)
            ?? throw new InvalidOperationException("The middle process did not start.");

        // The middle writes the sleeper's id and exits; read to its end so it is not held.
        output.Write(middle.StandardOutput.ReadToEnd());

        middle.WaitForExit();

        Thread.Sleep(int.Parse(arguments.Length > 1 ? arguments[1] : "300000", CultureInfo.InvariantCulture));

        return 0;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getpid();

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getpgid(int process);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getsid(int process);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int setpgid(int process, int group);

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int setsid();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);

    /// <summary>
    /// Starts a holder - this assembly as the behaviour <c>arguments[2]</c> names, given every
    /// argument after it - that inherits this process's own streams, writes its process id once it
    /// stands where it puts itself, and then exits or sleeps as <c>arguments[0]</c> says, for the
    /// milliseconds <c>arguments[1]</c> gives: what a test of containment reads, since the holder
    /// keeps the pipes whoever started this process reads, and that reading ends only with it.
    /// </summary>
    private static int SpawnHolder(TextWriter output, string[] arguments)
    {
        using var holder = StartInheriting(arguments[2], arguments[3..]);

        // A holder that leaves this group or session is named once it has, so a test acting on
        // its id acts on a process that stands apart.
        var apart = Until(() => arguments[2] switch
        {
            "own-group-and-sleep" => getpgid(holder.Id) == holder.Id,
            "own-session-and-sleep" => getsid(holder.Id) == holder.Id,
            _ => true,
        });

        if (!apart)
        {
            return 96;
        }

        output.Write($"holder={holder.Id}\n");

        if (string.Equals(arguments[0], "sleep", StringComparison.Ordinal))
        {
            Thread.Sleep(int.Parse(arguments[1], CultureInfo.InvariantCulture));
        }

        return 0;
    }

    /// <summary>
    /// Starts, through a middle that exits once it has started it, a holder of this process's own
    /// streams, writes <c>orphaned</c> once the middle has gone, and sleeps for the milliseconds
    /// <c>arguments[0]</c> gives: the holder is no one's child, so no walk of this process's tree
    /// reaches it, and the pipes it keeps are the ones whoever started this process reads.
    /// </summary>
    private static int SpawnOrphanHolder(TextWriter output, string[] arguments)
    {
        var milliseconds = arguments.Length > 0 ? arguments[0] : "300000";

        using (var middle = StartInheriting("spawn-holder", ["exit", "0", "sleep", milliseconds]))
        {
            middle.WaitForExit();
        }

        output.Write("orphaned\n");
        Thread.Sleep(int.Parse(milliseconds, CultureInfo.InvariantCulture));

        return 0;
    }

    /// <summary>Moves to a process group of its own, as <c>timeout</c> does, and sleeps; the group call is the C library's.</summary>
    private static int OwnGroupAndSleep(string[] arguments)
    {
        if (OperatingSystem.IsWindows() || setpgid(0, 0) != 0)
        {
            return 98;
        }

        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>Makes a session of its own, as a daemon does, and sleeps; the session call is the C library's.</summary>
    private static int OwnSessionAndSleep(string[] arguments)
    {
        if (OperatingSystem.IsWindows() || setsid() == -1)
        {
            return 98;
        }

        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    /// Refuses the signal that asks it to end, writes the file <c>arguments[1]</c> names once it
    /// does, and sleeps for the milliseconds <c>arguments[0]</c> gives: only the signal that cannot
    /// be refused ends it, and the file says when a test may send the other.
    /// </summary>
    private static int IgnoreTermAndSleep(string[] arguments)
    {
        using var refusing = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            context => context.Cancel = true);

        File.WriteAllText(arguments[1], "ready");
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));

        return 0;
    }

    /// <summary>
    /// Writes <c>asked</c> into the file <c>arguments[0]</c> names when it is asked to end, and then
    /// ends as asked; writes <c>ready</c> once it listens, and sleeps for the milliseconds
    /// <c>arguments[1]</c> gives.
    /// </summary>
    private static int NoteTermAndSleep(TextWriter output, string[] arguments)
    {
        using var noting = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM,
            _ => File.WriteAllText(arguments[0], "asked"));

        output.Write("ready\n");
        Thread.Sleep(int.Parse(arguments[1], CultureInfo.InvariantCulture));

        return 0;
    }

    /// <summary>Writes <c>arguments[0]</c> on standard output and <c>arguments[1]</c> on standard error, each as a line, and exits with the code <c>arguments[2]</c> gives.</summary>
    private static int SayAndExit(TextWriter output, TextWriter error, string[] arguments)
    {
        output.Write(arguments[0] + "\n");
        error.Write(arguments[1] + "\n");

        return int.Parse(arguments[2], CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Stands in for the step that places a contained child, taking its command line and reaching no channel: says so on
    /// standard error and ends with the step's own code, starting nothing.
    /// </summary>
    private static int StepReachingNothing(TextWriter error)
    {
        error.Write(ReachedNothing + "\n");

        return Core.Processes.ContainExec.Failed;
    }

    /// <summary>Writes its own process id and sleeps for the milliseconds <c>arguments[0]</c> gives.</summary>
    private static int SayPidAndSleep(TextWriter output, string[] arguments)
    {
        output.Write($"pid={Environment.ProcessId}\n");
        Thread.Sleep(int.Parse(arguments[0], CultureInfo.InvariantCulture));

        return 0;
    }

    /// <summary>Writes its own process id, then does what the behaviour <c>arguments[0]</c> names with the arguments after it.</summary>
    private static int SayPidThen(TextWriter output, string[] arguments)
    {
        output.Write($"pid={Environment.ProcessId}\n");

        return Run(arguments[0], arguments[1..]);
    }

    /// <summary>
    /// Starts two sleepers through a process runner of its own, which starts contained children
    /// through the tool <c>arguments[0]</c> names - one contained, one as children always were -
    /// writes its own id and both of theirs on one line, and waits for them: what a test that signals this
    /// process's own group reads, since the one started as always is in that group and the
    /// contained one is not.
    /// </summary>
    private static int ContainAndWait(TextWriter output, string[] arguments)
    {
        var runner = new Core.Processes.ProcessRunner(new Core.Platform.HostPlatform(), Core.Platform.FilePermissionsFactory.Create(), arguments[0]);
        var containedId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var contained = runner.RunAsync(Saying(containedId) with { Contained = true });
        var ordinary = runner.RunAsync(Saying(ordinaryId));

        if (Task.WhenAny(Task.WhenAll(containedId.Task, ordinaryId.Task), contained, ordinary).GetAwaiter().GetResult() is not Task<string[]>)
        {
            return 95;
        }

        output.Write($"parent={Environment.ProcessId} contained={containedId.Task.Result} ordinary={ordinaryId.Task.Result}\n");
        Task.WaitAll(contained, ordinary);

        return 0;

        Core.Processes.ProcessRequest Saying(TaskCompletionSource<string> id) => TestHost.ChildRequest("say-pid-and-sleep", arguments[1]) with
        {
            OnOutputLine = line =>
            {
                if (line.StartsWith("pid=", StringComparison.Ordinal))
                {
                    id.TrySetResult(line["pid=".Length..]);
                }
            },
        };
    }

    /// <summary>
    /// Starts two sleepers for <c>arguments[1]</c> milliseconds through a process runner of its own, which places bound
    /// children through the tool <c>arguments[0]</c> names - one bound to this process's life, one as children always
    /// were - writes its own id and both of theirs on one line, and waits for them: the process a test kills where it
    /// stands, so the one bound is seen to end with it and the other to outlive it.
    /// </summary>
    private static int BindAndWait(TextWriter output, string[] arguments)
    {
        var runner = new Core.Processes.ProcessRunner(new Core.Platform.HostPlatform(), Core.Platform.FilePermissionsFactory.Create(), arguments[0]);
        var boundId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ordinaryId = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bound = runner.RunAsync(Saying(boundId) with { Bound = true });
        var ordinary = runner.RunAsync(Saying(ordinaryId));

        if (Task.WhenAny(Task.WhenAll(boundId.Task, ordinaryId.Task), bound, ordinary).GetAwaiter().GetResult() is not Task<string[]>)
        {
            return 95;
        }

        output.Write($"parent={Environment.ProcessId} bound={boundId.Task.Result} ordinary={ordinaryId.Task.Result}\n");
        Task.WaitAll(bound, ordinary);

        return 0;

        Core.Processes.ProcessRequest Saying(TaskCompletionSource<string> id) => TestHost.ChildRequest("say-pid-and-sleep", arguments[1]) with
        {
            OnOutputLine = line =>
            {
                if (line.StartsWith("pid=", StringComparison.Ordinal))
                {
                    id.TrySetResult(line["pid=".Length..]);
                }
            },
        };
    }

    /// <summary>
    /// Starts a sleeper for <c>arguments[2]</c> milliseconds contained, through a process runner of its own that starts
    /// contained children through the tool <c>arguments[0]</c> names, its session recorded in the step directory
    /// <c>arguments[1]</c> as an orchestrator's step records it; writes its own id and the sleeper's on one line once the
    /// record is written, and waits for it: the process a test kills, so the record names a session whose owner has gone.
    /// </summary>
    private static int ContainRecordedAndWait(TextWriter output, string[] arguments)
    {
        var platform = new Core.Platform.HostPlatform();
        var permissions = Core.Platform.FilePermissionsFactory.Create();
        var runner = new Core.Processes.ProcessRunner(platform, permissions, arguments[0]);
        var sessions = new Core.Orchestration.ChildSessions(
            new Core.FileSystem.PhysicalFileSystem(permissions),
            new Core.Execution.ProcessIdentity(platform),
            new Core.Processes.LeftSessions(platform, Core.Platform.ProcessTableFactory.Create(platform, runner)),
            new Core.Output.ConsoleHarnessOutput(TextWriter.Null, TextWriter.Null, verbose: false, home: null),
            new Core.Orchestration.EndedChildSessions());
        var said = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Its lines are read only once it is said to have started, and it is said to have started only once its record
        // is written: its id heard, the record names it.
        var contained = runner.RunAsync(TestHost.ChildRequest("say-pid-and-sleep", arguments[2]) with
        {
            FileName = TestHost.AssemblyProgram,
            Arguments = [arguments[2]],
            Contained = true,
            SessionRecord = sessions.For(arguments[1]),
            OnOutputLine = line =>
            {
                if (line.StartsWith("pid=", StringComparison.Ordinal))
                {
                    said.TrySetResult(line["pid=".Length..]);
                }
            },
        });

        if (Task.WhenAny(said.Task, contained).GetAwaiter().GetResult() != said.Task)
        {
            return 95;
        }

        output.Write($"owner={Environment.ProcessId} contained={said.Task.Result}\n");
        contained.GetAwaiter().GetResult();

        return 0;
    }

    /// <summary>
    /// Sends the console's interruption to every process of the console it runs in, itself among
    /// them, and writes whether it was interrupted: a process in a group of its own on Windows has
    /// that interruption turned off, and one that shares its starter's group has it as its starter
    /// does. The console calls are Windows'.
    /// </summary>
    private static int InterruptOwnConsole(TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            return 98;
        }

        using var reached = new ManualResetEventSlim();
        using var listening = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGINT,
            context =>
            {
                context.Cancel = true;
                reached.Set();
            });

        if (!GenerateConsoleCtrlEvent(0, 0))
        {
            output.Write($"not sent: {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}\n");
            return 97;
        }

        output.Write(reached.Wait(TimeSpan.FromSeconds(3)) ? "interrupted\n" : "not interrupted\n");

        return 0;
    }

    /// <summary>Writes the code page its console's output is written in, as the runtime reads it.</summary>
    private static int PrintCodePage(TextWriter output)
    {
        output.Write($"{Console.OutputEncoding.CodePage}\n");
        return 0;
    }

    /// <summary>
    /// Writes the process id of the process that started it: on Linux and macOS as the C library
    /// tells it, and on Windows as the process's own basic information records it.
    /// </summary>
    private static int PrintParent(TextWriter output)
    {
        if (!OperatingSystem.IsWindows())
        {
            output.Write($"parent={getppid()}\n");
            return 0;
        }

        var information = default(BasicInformation);

        if (NtQueryInformationProcess(GetCurrentProcess(), 0, ref information, System.Runtime.InteropServices.Marshal.SizeOf<BasicInformation>(), out _) != 0)
        {
            return 97;
        }

        output.Write($"parent={information.InheritedFromUniqueProcessId}\n");
        return 0;
    }

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getppid();

    [System.Runtime.InteropServices.DllImport("ntdll.dll", ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern int NtQueryInformationProcess(IntPtr process, int informationClass, ref BasicInformation information, int length, out int returned);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", ExactSpelling = true)]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>PROCESS_BASIC_INFORMATION: what Windows records of a process, the id of the one that started it among it.</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BasicInformation
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    /// <summary>Starts this assembly as <paramref name="mode"/>, inheriting this process's own streams.</summary>
    private static Process StartInheriting(string mode, string[] arguments)
    {
        var start = new ProcessStartInfo(TestHost.DotnetExecutable) { UseShellExecute = false };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);

        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[TestHost.ChildModeVariable] = mode;

        return Process.Start(start) ?? throw new InvalidOperationException($"'{mode}' did not start.");
    }

    /// <summary>Whether <paramref name="condition"/> came to hold within thirty seconds, looking again every twenty milliseconds.</summary>
    private static bool Until(Func<bool> condition)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(30); waited += TimeSpan.FromMilliseconds(20))
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(20));
        }

        return false;
    }

    /// <summary>
    /// Writes one line, then waits for the parent to prove it received that line while
    /// this process is still running. If output were delivered only at exit, the signal
    /// would never arrive, and the exit code says so.
    /// </summary>
    private static int Stream(TextWriter output, TextWriter error, string[] arguments)
    {
        var signal = arguments[0];

        output.Write("first\n");

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!File.Exists(signal))
        {
            if (DateTime.UtcNow > deadline)
            {
                return 3;
            }

            Thread.Sleep(20);
        }

        error.Write("problem\n");
        output.Write("second\n");
        return 0;
    }

    /// <summary>
    /// Writes <c>arguments[0]</c> lines of <c>arguments[1]</c> characters each, each <see cref="FloodLine"/>, then - where
    /// <c>arguments[2]</c> is given - one line of that many characters, <see cref="GiantLine"/>, with no line feed after it:
    /// what a child that floods its output writes, as fast as its pipe takes it. A test can then say what every line should
    /// be without holding any of them.
    /// </summary>
    private static int Flood(TextWriter output, string[] arguments)
    {
        var count = long.Parse(arguments[0], CultureInfo.InvariantCulture);
        var length = int.Parse(arguments[1], CultureInfo.InvariantCulture);
        var giant = arguments.Length > 2 ? long.Parse(arguments[2], CultureInfo.InvariantCulture) : 0;

        // Written a megabyte at a time: the writer flushes every write, and a write per line would measure this
        // process's system calls rather than the reader's.
        var chunk = new StringBuilder(FloodChunk + length + 1);

        for (var index = 0L; index < count; index++)
        {
            chunk.Append(FloodLine(index, length)).Append('\n');

            if (chunk.Length >= FloodChunk)
            {
                output.Write(chunk);
                chunk.Clear();
            }
        }

        output.Write(chunk);

        for (var written = 0L; written < giant; written += FloodChunk)
        {
            output.Write(GiantLine((int)Math.Min(FloodChunk, giant - written)));
        }

        return 0;
    }

    /// <summary>Writes the text of the file <c>arguments[0]</c> names, as it is: output longer than a command line can carry.</summary>
    private static int PrintFile(TextWriter output, string[] arguments)
    {
        output.Write(File.ReadAllText(arguments[0], Utf8NoBom));
        return 0;
    }

    /// <summary>
    /// Writes the bytes of the file <c>arguments[0]</c> names to standard output as they are, a byte order mark among them
    /// included, for a test of output a caller reads byte for byte.
    /// </summary>
    private static int PrintBytes(string[] arguments)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(File.ReadAllBytes(arguments[0]));
        return 0;
    }

    /// <summary>How many characters <see cref="Flood"/> writes at a time.</summary>
    private const int FloodChunk = 1024 * 1024;

    /// <summary>Line <paramref name="index"/> of a flood: its number, then as many dots as make it <paramref name="length"/> long.</summary>
    internal static string FloodLine(long index, int length)
    {
        var number = index.ToString("D12", CultureInfo.InvariantCulture);

        return number + new string('.', Math.Max(0, length - number.Length));
    }

    /// <summary><paramref name="length"/> characters of the one line a flood ends with, which never ends.</summary>
    internal static string GiantLine(int length) => new('g', length);

    /// <summary>Starts a sleeping grandchild, records its process id, then sleeps as well.</summary>
    private static int SpawnGrandchild(string[] arguments)
    {
        var processIdFile = arguments[0];
        var sleepMilliseconds = arguments[1];

        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add(sleepMilliseconds);
        start.Environment[TestHost.ChildModeVariable] = "sleep";

        using var grandchild = Process.Start(start)
            ?? throw new InvalidOperationException("The grandchild process did not start.");

        // Written to a temporary name and renamed, so the parent never reads a half-written id.
        var partial = processIdFile + ".partial";
        File.WriteAllText(partial, grandchild.Id.ToString(CultureInfo.InvariantCulture));
        File.Move(partial, processIdFile);

        Thread.Sleep(int.Parse(sleepMilliseconds, CultureInfo.InvariantCulture));
        return 0;
    }

    /// <summary>
    /// Starts a sleeping child, says its id, then does what a host's agent does at its last resort, on this machine's
    /// own process table: ends what it started, says what that came to, and ends itself with the code
    /// <c>arguments[0]</c> names.
    /// </summary>
    private static int LastResort(TextWriter output, string[] arguments)
    {
        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add("120000");
        start.Environment[TestHost.ChildModeVariable] = "sleep";

        using var sleeper = Process.Start(start)
            ?? throw new InvalidOperationException("The sleeping child did not start.");

        output.Write(sleeper.Id.ToString(CultureInfo.InvariantCulture) + "\n");

        var platform = new DssHarness.Core.Platform.HostPlatform();
        var table = DssHarness.Core.Platform.ProcessTableFactory.Create(
            platform,
            new DssHarness.Core.Processes.ProcessRunner(platform, DssHarness.Core.Platform.FilePermissionsFactory.Create()));

        // Longer than an agent gives it: a busy machine's table is read slowly, and this is no test of that budget.
        var resort = new DssHarness.Core.Hosts.HostAgentLastResort(table, platform, TimeSpan.FromMinutes(5));

        output.Write(resort.EndStartedAsync().GetAwaiter().GetResult() + "\n");
        output.Flush();
        resort.End(int.Parse(arguments[0], CultureInfo.InvariantCulture));

        return 99;
    }

    private static int PrintEnvironment(TextWriter output, string[] arguments)
    {
        output.Write((Environment.GetEnvironmentVariable(arguments[0]) ?? "<unset>") + "\n");
        return 0;
    }

    /// <summary>
    /// The fake <c>claude</c>: a child that speaks the protocol the probe recorded (<c>fixtures/claude-code</c>), started
    /// with the launch's argument list and reading it as the real one does. Each message begins a turn that asks the
    /// host about a read and waits for the answer: an allow runs the read; a deny does not, and the turn's result counts
    /// the denial; an <c>error</c> answer fails the call, counted as a denial too; no answer is waited for, as long as it
    /// takes - and an answer in a shape the recording does not know (a flat envelope) is not heard at all, which is what
    /// the real child does. The input closing while an ask waits fails the call, ends the turn and then the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the probe measured of the launch, on every version it ran: <c>--model</c> is run as written and named in
    /// <c>system/init</c> and every message, a model the provider does not know ending each turn in an API error; only
    /// <c>--permission-prompt-tool stdio</c> sends an ask to the host (without it the call is denied on the spot, a
    /// <c>system/permission_denied</c> line saying so); <c>--max-turns</c> ends the turn whose tool call would need one
    /// more, <c>error_max_turns</c>; <c>--max-budget-usd</c> ends the turn whose first request spent past it while its
    /// ask is still waiting, <c>error_max_budget_usd</c>, and every turn after it at once; the window
    /// <c>CLAUDE_CODE_MAX_CONTEXT_TOKENS</c> sets ends a turn whose context would pass it at <c>blocking_limit</c>, and
    /// every turn after it (nothing is compacted); an interrupted turn ends <c>error_during_execution</c> at
    /// <c>aborted_streaming</c>. A completed turn's result carries the schema's envelope twice, as text in <c>result</c>
    /// and as its JSON in <c>structured_output</c>; an error subtype carries <c>errors</c> and no <c>result</c>. The
    /// process exits 1 where its last turn ended in error, 0 otherwise.
    /// </para>
    /// <para>
    /// The shapes are the curated fixture's, with ids that stand in as it does; its own measures - ten thousand tokens of
    /// context and a cent of cost for each turn's request - are the fake's alone. Nothing here runs anything, reads any
    /// file, or asks the network: the one live test that recorded these shapes is the machine's own and runs only where a
    /// person opted in - a continuous-integration runner has no login and never sets the variable that would ask for it.
    /// </para>
    /// </remarks>
    private static int ClaudeCode(TextWriter output, string[] arguments)
    {
        if (Environment.GetEnvironmentVariable(FakeClaude.ScriptVariable) is { Length: > 0 } script)
        {
            return FakeClaude.Run(output, arguments, script);
        }

        using var input = new StreamReader(Console.OpenStandardInput(), Utf8NoBom);

        string? Line() => input.ReadLine();

        void Say(string line)
        {
            output.Write(line + "\n");
            output.Flush();
        }

        string? Option(string name)
        {
            var at = Array.IndexOf(arguments, name);
            return at >= 0 && at + 1 < arguments.Length ? arguments[at + 1] : null;
        }

        var model = Option("--model") ?? "claude-sonnet-5-5";
        var known = model is "claude-fable-5-1" or "claude-opus-5-5" or "claude-sonnet-5-5" or "claude-haiku-5-5";
        var asksTheHost = Option("--permission-prompt-tool") == "stdio";
        var maxTurns = int.TryParse(Option("--max-turns"), NumberStyles.None, CultureInfo.InvariantCulture, out var turns) ? turns : int.MaxValue;
        var budget = Option("--max-budget-usd");
        var limit = decimal.TryParse(budget, NumberStyles.Number, CultureInfo.InvariantCulture, out var usd) ? usd : decimal.MaxValue;
        var window = int.TryParse(Environment.GetEnvironmentVariable("CLAUDE_CODE_MAX_CONTEXT_TOKENS"), NumberStyles.None, CultureInfo.InvariantCulture, out var tokens) ? tokens : int.MaxValue;

        var session = "00000000-0000-4000-8000-000000000001";
        var ask = "00000000-0000-4000-8000-000000000002";
        var call = "toolu_000000000000000000000001";
        var context = 0;
        var spent = 0m;
        var waiting = false;
        var lastWasError = false;

        string Cost() => spent.ToString(CultureInfo.InvariantCulture);

        void ToolResult(bool ok, string content)
            => Say("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"" + content
                + "\",\"is_error\":" + (ok ? "false" : "true") + ",\"tool_use_id\":\"" + call + "\"}]},\"parent_tool_use_id\":null,\"session_id\":\""
                + session + "\",\"uuid\":\"00000000-0000-4000-8000-000000000004\",\"timestamp\":\"2026-10-18T00:00:00.000Z\"}");

        // A turn's result line; the members a subtype carries are the recording's: a success carries `result`, an error `errors`.
        void Result(string subtype, string reason, bool error, string stopReason, int numberOfTurns, string denials, string? result, string? errors, string? structured = null)
        {
            lastWasError = error;
            Say("{\"duration_api_ms\":12,\"stop_reason\":" + stopReason + ",\"session_id\":\"" + session
                + "\",\"total_cost_usd\":" + Cost() + ",\"usage\":{\"input_tokens\":6,\"output_tokens\":40,\"cache_read_input_tokens\":100,\"cache_creation_input_tokens\":0,\"server_tool_use\":{\"web_search_requests\":0,\"web_fetch_requests\":0}},\"modelUsage\":{},\"permission_denials\":"
                + denials + ",\"terminal_reason\":\"" + reason + "\",\"is_error\":" + (error ? "true" : "false") + ",\"num_turns\":"
                + numberOfTurns.ToString(CultureInfo.InvariantCulture) + ",\"subtype\":\"" + subtype + "\""
                + (result is null ? "" : ",\"result\":" + result) + (structured is null ? "" : ",\"structured_output\":" + structured)
                + (errors is null ? "" : ",\"errors\":[\"" + errors + "\"]")
                + ",\"type\":\"result\",\"duration_ms\":80,\"uuid\":\"00000000-0000-4000-8000-000000000003\",\"queued_turn_count\":0,\"result_index\":0}");
        }

        // The message the provider never wrote: a failure Claude Code tells the stream in an assistant line of its own.
        void Synthetic(string error, string text)
            => Say("{\"type\":\"assistant\",\"message\":{\"id\":\"00000000-0000-4000-8000-000000000008\",\"model\":\"<synthetic>\",\"role\":\"assistant\",\"stop_reason\":\"stop_sequence\",\"stop_sequence\":\"\",\"type\":\"message\",\"usage\":{\"input_tokens\":0,\"output_tokens\":0},\"content\":[{\"type\":\"text\",\"text\":\""
                + text + "\"}]},\"parent_tool_use_id\":null,\"session_id\":\"" + session + "\",\"uuid\":\"00000000-0000-4000-8000-000000000009\",\"error\":\"" + error + "\",\"is_api_error_message\":true}");

        void BudgetSpent(string stopReason)
            => Result("error_max_budget_usd", "budget_exhausted", error: true, stopReason, 1, "[]", null, "Reached maximum budget ($" + budget + ")");

        // The turn every message gets: a read held for the host's decision, unless the launch's own bounds end it first.
        void Turn(string? message)
        {
            Say("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"" + (message ?? "") + "\"},\"parent_tool_use_id\":null,\"uuid\":\"00000000-0000-4000-8000-000000000005\",\"timestamp\":\"2026-10-18T00:00:00.000Z\",\"isReplay\":true}");
            Say("{\"type\":\"system\",\"subtype\":\"status\",\"status\":\"requesting\",\"session_id\":\"" + session + "\",\"uuid\":\"00000000-0000-4000-8000-000000000010\"}");

            if (spent > limit)
            {
                BudgetSpent("null");
                return;
            }

            if (!known)
            {
                var refusal = "There's an issue with the selected model (" + model + "). It may not exist or you may not have access to it. Run --model to pick a different model.";
                Synthetic("model_not_found", refusal);
                Result("success", "api_error", error: true, "\"stop_sequence\"", 1, "[]", "\"" + refusal + "\"", null);
                return;
            }

            context += 10_000;

            if (context > window)
            {
                Synthetic("invalid_request", "Prompt is too long");
                Result("success", "blocking_limit", error: true, "\"stop_sequence\"", 1, "[]", "\"Prompt is too long\"", null);
                return;
            }

            spent += 0.01m;
            Say("{\"type\":\"stream_event\",\"event\":{\"type\":\"message_start\",\"message\":{\"model\":\"" + model + "\",\"usage\":{\"input_tokens\":6}}},\"session_id\":\"" + session + "\"}");
            Say("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"tool_use\",\"id\":\"" + call + "\",\"name\":\"Read\",\"input\":{}}},\"session_id\":\"" + session + "\"}");
            Say("{\"type\":\"stream_event\",\"event\":{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"input_json_delta\",\"partial_json\":\"{\\\"file_path\\\": \\\"a.txt\\\"}\"}},\"session_id\":\"" + session + "\"}");
            Say("{\"type\":\"assistant\",\"message\":{\"model\":\"" + model + "\",\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"usage\":{\"input_tokens\":6,\"output_tokens\":20},\"content\":[{\"type\":\"tool_use\",\"id\":\"" + call + "\",\"name\":\"Read\",\"input\":{\"file_path\":\"a.txt\"}}]},\"session_id\":\"" + session + "\"}");

            if (!asksTheHost)
            {
                // Without the prompt tool nothing is put to the host: the ask rule's call is denied on the spot.
                var denied = "Claude requested permissions to use Read, but you haven't granted it yet.";
                Say("{\"type\":\"system\",\"subtype\":\"permission_denied\",\"tool_name\":\"Read\",\"tool_use_id\":\"" + call + "\",\"decision_reason_type\":\"rule\",\"message\":\"" + denied + "\",\"uuid\":\"00000000-0000-4000-8000-000000000011\",\"session_id\":\"" + session + "\"}");
                Ended(denied, "unread");
                return;
            }

            // It asks, and waits: nothing more is written until the host answers in the recorded shape.
            Say("{\"type\":\"control_request\",\"request_id\":\"" + ask + "\",\"request\":{\"subtype\":\"can_use_tool\",\"tool_name\":\"Read\",\"display_name\":\"Read\",\"input\":{\"file_path\":\"a.txt\"},\"description\":\"a.txt\",\"decision_reason_type\":\"rule\",\"tool_use_id\":\"" + call + "\"}}");

            if (spent > limit)
            {
                // The budget is read after the request that spent it: the turn ends with its ask unanswered, and the read never runs.
                BudgetSpent("\"tool_use\"");
                return;
            }

            waiting = true;
        }

        // The turn's end after its read was decided: the tool result, the schema's envelope, and the result counting a denial where there was one.
        void Ended(string? failure, string answer)
        {
            waiting = false;
            ToolResult(ok: failure is null, failure ?? "1\\talpha\\n2\\t");

            if (failure is null && maxTurns <= 1)
            {
                Result("error_max_turns", "max_turns", error: true, "\"tool_use\"", 2, "[]", null, "Reached maximum number of turns (" + maxTurns.ToString(CultureInfo.InvariantCulture) + ")");
                return;
            }

            if (failure is null)
            {
                Say("{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"[structured-output-enforce] You MUST call the StructuredOutput tool to complete this request. Call this tool now.\"}]},\"session_id\":\"" + session + "\"}");
            }

            Say("{\"type\":\"assistant\",\"message\":{\"model\":\"" + model + "\",\"role\":\"assistant\",\"stop_reason\":\"tool_use\",\"usage\":{\"input_tokens\":6,\"output_tokens\":20},\"content\":[{\"type\":\"tool_use\",\"id\":\"toolu_000000000000000000000002\",\"name\":\"StructuredOutput\",\"input\":{\"answer\":\"" + answer + "\"}}]},\"session_id\":\"" + session + "\"}");
            ToolResult(ok: true, "Structured output provided successfully");
            Result(
                "success",
                "completed",
                error: false,
                "\"stop_sequence\"",
                2,
                failure is null ? "[]" : "[{\"tool_name\":\"Read\",\"tool_use_id\":\"" + call + "\",\"tool_input\":{\"file_path\":\"a.txt\"}}]",
                "\"{\\\"answer\\\":\\\"" + answer + "\\\"}\"",
                null,
                "{\"answer\":\"" + answer + "\"}");
        }

        // A member's text, where the member is there and is text; null for anything else, as the real child reads it.
        static string? Text(System.Text.Json.Nodes.JsonNode? node)
            => node is System.Text.Json.Nodes.JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

        while (Line() is { } line)
        {
            System.Text.Json.Nodes.JsonNode? node;

            try
            {
                node = System.Text.Json.Nodes.JsonNode.Parse(line);
            }
            catch (System.Text.Json.JsonException)
            {
                // What the real child does with a line it cannot read: nothing, and the stream goes on.
                continue;
            }

            var kind = Text(node?["type"]);
            var sub = Text(node?["request"]?["subtype"]);
            var requestId = Text(node?["request_id"]);

            if (kind == "control_request" && sub == "initialize")
            {
                Say("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + requestId
                    + "\",\"response\":{\"elided\":\"the commands, agents, output styles, models and account of a machine\"}}}");
                Say("{\"type\":\"system\",\"subtype\":\"init\",\"cwd\":\"<scratch>\",\"session_id\":\"" + session
                    + "\",\"tools\":[\"Bash\",\"Edit\",\"Glob\",\"Grep\",\"NotebookEdit\",\"Read\",\"StructuredOutput\",\"WebFetch\",\"WebSearch\",\"Write\"],\"mcp_servers\":[],\"model\":\"" + model
                    + "\",\"permissionMode\":\"default\",\"apiKeySource\":\"none\",\"claude_code_version\":\"fake\",\"output_style\":\"default\",\"capabilities\":[\"interrupt_receipt_v1\",\"interrupt_cancel_queued_v1\"],\"uuid\":\"00000000-0000-4000-8000-000000000006\"}");
            }
            else if (kind == "user")
            {
                Turn(Text(node?["message"]?["content"]));
            }
            else if (kind == "control_request" && sub == "interrupt")
            {
                waiting = false;
                Say("{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"" + requestId + "\",\"response\":{}}}");
                Result("error_during_execution", "aborted_streaming", error: true, "null", 1, "[]", null, "[ede_diagnostic] result_type=user last_content_type=n/a stop_reason=null");
            }
            else if (kind == "control_response" && waiting)
            {
                // The answer an ask waits for, in the envelope the probe recorded: a success whose own response names
                // the ask inside it and carries the behavior one level further in, or an error naming the ask beside
                // its error. Any other shape - the id or the behavior outside, no subtype - is not heard, which is the
                // silence the real child keeps.
                var response = node?["response"];
                var subtype = Text(response?["subtype"]);
                var behavior = Text(response?["response"]?["behavior"]);

                if (Text(response?["request_id"]) != ask)
                {
                    continue;
                }

                if (subtype == "success" && behavior == "allow")
                {
                    Ended(null, "alpha");
                }
                else if (subtype == "success" && behavior == "deny")
                {
                    Ended("denied by the host", "denied");
                }
                else if (subtype == "error" && Text(response?["error"]) is { } error)
                {
                    Ended("Tool permission request failed: Error: " + error, "failed");
                }
            }
        }

        // The input is gone: an ask still waiting fails as the real child fails it, the turn ends around it, and so does the process.
        if (waiting)
        {
            Ended("Tool permission request failed: AbortError: Tool permission stream closed before response received", "unread");
        }

        return lastWasError ? 1 : 0;
    }
}
