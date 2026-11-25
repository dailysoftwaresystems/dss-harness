using System.Runtime.CompilerServices;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;

namespace DssHarness.Tests;

/// <summary>
/// Process-wide setup for the test run, and the means to use this test assembly as a
/// child process whose behaviour a test controls exactly.
/// </summary>
internal static class TestHost
{
    /// <summary>Selects a child behaviour. Unset in an ordinary test run.</summary>
    internal const string ChildModeVariable = "DSSHARNESS_TEST_CHILD";

    /// <summary>
    /// The directory this tool keeps its per-user state in as the machine gives it to the process, read as a path alone
    /// before <see cref="IsolateUserState"/> names the process a directory of its own: where the machine user's settings
    /// file and records are, which no test reads or writes. Null where the machine names none - a process given no home.
    /// </summary>
    internal static string? MachineUserState { get; private set; }

    /// <summary>
    /// Root of every temporary directory the suite creates, with symbolic links resolved.
    /// </summary>
    /// <remarks>
    /// git reports resolved paths, so an expectation built from an unresolved directory
    /// fails for reasons unrelated to the code under test: macOS keeps its temporary
    /// directory under the /var link. GitHub's Windows runners report the system temporary
    /// directory through an 8.3 short name (RUNNER~1), which git reports in full, so
    /// RUNNER_TEMP, which has no short-name components, is preferred where it exists.
    /// Being short, it also helps the Windows path budget.
    /// </remarks>
    internal static string TemporaryRoot { get; } = CreateTemporaryRoot();

    /// <summary>
    /// The directory this test process keeps this tool's per-user state in, below <see cref="TemporaryRoot"/> and of this
    /// process's own: the one <see cref="UserState.Variable"/> names to the process and every child it starts
    /// (<see cref="IsolateUserState"/>), so every registration a test builds in process and every CLI a test starts keep
    /// this machine's settings file, its dispatch queue, its other records and the isolation's caches here - unless a test
    /// names a directory of its own to a CLI it starts (<see cref="CliRunner.OwnUserState"/>).
    /// </summary>
    internal static string UserStateDirectory { get; } = Path.Combine(TemporaryRoot, $"u{Environment.ProcessId}-{Guid.NewGuid().ToString("N")[..8]}");

    /// <summary>The dotnet host running this suite, so children use the same runtime.</summary>
    internal static string DotnetExecutable { get; } = ResolveDotnet();

    /// <summary>
    /// Takes the terminal's interruption back for the children a test starts, where the runner of this process had it
    /// ignored: a script's background job - a shard run of the suite among them - is started with the interruption
    /// ignored, and a child inherits an ignored signal whole, so the interruption a test sends one would move nothing -
    /// and .NET keeps an interruption it inherited ignored, so no registration the child makes arms it either. This
    /// puts the default back in this process alone, and a test that sends a child the terminal's interruption calls it
    /// first; where the interruption was never ignored, or a registration of this process holds it, nothing changes.
    /// </summary>
    internal static void InterruptionTakenBack()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Answered while the interruption is held ignored for an instant, so nothing a test is doing in that instant
        // is interrupted by it: what comes back says whether it was ignored at all, and is put back where it was.
        var was = signal(Interrupt, Ignored);

        _ = signal(Interrupt, was == Ignored ? Default : was);
    }

    /// <summary>The terminal's interruption, SIGINT, as Linux and macOS number it.</summary>
    private const int Interrupt = 2;

    /// <summary>What makes a signal end a process as it always did.</summary>
    private static readonly nint Default = 0;

    /// <summary>What makes a signal move nothing.</summary>
    private static readonly nint Ignored = 1;

    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern nint signal(int number, nint action);

    /// <summary>This test assembly, which doubles as the child process.</summary>
    internal static string AssemblyPath => typeof(TestHost).Assembly.Location;

    /// <summary>
    /// The program the build makes beside this test assembly, started by its whole path: the same child, for a test that
    /// needs a program no lookup on a PATH finds, and whose mode the environment it is given selects.
    /// </summary>
    internal static string AssemblyProgram => Path.ChangeExtension(AssemblyPath, OperatingSystem.IsWindows() ? ".exe" : null);

    /// <summary>The child behaviour this process was started in, by a test; null in an ordinary test run.</summary>
    internal static string? ChildMode
        => Environment.GetEnvironmentVariable(ChildModeVariable) is { Length: > 0 } mode ? mode : null;

    [ModuleInitializer]
    internal static void Initialize()
    {
        // A child does its work from the entry point (TestProgram), never from here: while this runs, no other thread can
        // run this assembly's code. It is left as its parent made it.
        if (ChildMode is not null)
        {
            return;
        }

        IsolateGitConfiguration();
        IsolateUserState();
        HangWatch.Start();

        // Threads at once for the work the suite's many tests hand the pool, so a test that blocks one - a double that
        // sleeps, a sync wait - never leaves a timer's callback queued behind the pool's slow growth: on a busy runner
        // that once ran timers seconds late, and a bound that had passed read as unspent.
        ThreadPool.GetMinThreads(out var workers, out var completions);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), completions);
    }

    /// <summary>
    /// Makes a program that really starts and exits zero, named <paramref name="name"/> in
    /// <paramref name="directory"/>, and returns its path.
    /// </summary>
    internal static string StartableProgram(string directory, string name)
    {
        Directory.CreateDirectory(directory);

        if (OperatingSystem.IsWindows())
        {
            // A standalone system program, copied under a name no PATH can know.
            var copy = Path.Combine(directory, name + ".exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "hostname.exe"), copy);
            return copy;
        }

        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>A request that runs this assembly as a child in <paramref name="mode"/>.</summary>
    internal static ProcessRequest ChildRequest(string mode, params string[] arguments) => new()
    {
        FileName = DotnetExecutable,
        Arguments = ["exec", AssemblyPath, .. arguments],
        Environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ChildModeVariable] = mode,
        },
    };

    /// <summary>
    /// How <paramref name="request"/> - a child of this assembly, as <see cref="ChildRequest"/> makes it - is started by a
    /// test itself, as a process of the test's own rather than through a runner: its program, its arguments and its
    /// environment, the mode among them. One place, so no such start loses the mode and runs the test runner instead,
    /// which prints its help and ends at once.
    /// </summary>
    /// <param name="request">The child, as a runner would be given it.</param>
    internal static System.Diagnostics.ProcessStartInfo StartOf(ProcessRequest request)
    {
        var start = new System.Diagnostics.ProcessStartInfo(request.FileName) { UseShellExecute = false };

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            start.Environment[name] = value;
        }

        return start;
    }

    /// <summary>
    /// That <paramref name="place"/> - a file or directory of this tool's per-user state, as a registration a test built
    /// or a CLI a test started named it - is in the test process's own state (<see cref="UserStateDirectory"/>), strictly
    /// below <see cref="TemporaryRoot"/>, and nowhere in the machine user's (<see cref="MachineUserState"/>).
    /// </summary>
    /// <param name="place">Where the tool keeps it.</param>
    internal static void IsKeptInTheProcesssOwnState(string place)
    {
        var comparison = new HostPlatform().PathComparison;

        Assert.True(
            PathContainment.IsStrictlyInside(TemporaryRoot, place, comparison) && PathContainment.IsInsideOrSame(UserStateDirectory, place, comparison),
            $"'{place}' is not in this test process's own state, '{UserStateDirectory}'.");
        Assert.False(
            MachineUserState is { } machine && PathContainment.IsInsideOrSame(machine, place, comparison),
            $"'{place}' is in the machine user's own state, '{MachineUserState}'.");
    }

    /// <summary>
    /// Gives this process a directory of its own for this tool's per-user state (<see cref="UserStateDirectory"/>), in the
    /// variable of the tool's own that names it (<see cref="UserState.Variable"/>), which every child inherits: the
    /// registrations a test builds in process and every CLI a test starts keep the tool's state there, never in the
    /// machine user's, whose settings file declares what no test may speak for and whose records every command of theirs
    /// shares - unless a test names a directory of its own to a CLI it starts. The home, the user data and the user cache
    /// stay the machine's, so every reader of <c>~</c> and every other program's data are as the machine has them.
    /// Removed as the process ends.
    /// </summary>
    private static void IsolateUserState()
    {
        try
        {
            MachineUserState = UserState.Directory;
        }
        catch (DirectoryNotFoundException)
        {
            // A process given no home names no state of the machine user's: nothing of theirs is read either way.
        }

        Directory.CreateDirectory(UserStateDirectory);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(UserStateDirectory, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A CLI still ending holds a file of it: the temporary root keeps it, as it keeps what any test left.
            }
        };

        Environment.SetEnvironmentVariable(UserState.Variable, UserStateDirectory);
    }

    /// <summary>
    /// Points every git process the run starts at an empty configuration. A developer's
    /// global or system settings (commit signing, a global excludes file, core.autocrlf,
    /// hook paths) otherwise change what these tests observe, so a suite that passes on
    /// one machine fails on another for reasons unrelated to the code under test.
    /// </summary>
    private static void IsolateGitConfiguration()
    {
        var emptyConfiguration = Path.Combine(TemporaryRoot, "isolated.gitconfig");

        try
        {
            if (!File.Exists(emptyConfiguration))
            {
                File.WriteAllText(emptyConfiguration, string.Empty);
            }
        }
        catch (IOException)
        {
            // Another test process created it at the same moment: an empty file either way.
        }

        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", emptyConfiguration);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");

        // git searches upwards for a repository. Stopping it at the temporary root means a
        // test asserting "not a repository" cannot be answered by some repository that
        // happens to enclose the temporary directory on one machine.
        Environment.SetEnvironmentVariable("GIT_CEILING_DIRECTORIES", TemporaryRoot);
    }

    private static string CreateTemporaryRoot()
    {
        var root = Path.Combine(
            Environment.GetEnvironmentVariable("RUNNER_TEMP") is { Length: > 0 } runnerTemp
                ? runnerTemp
                : Path.GetTempPath(),
            "dh-test");

        Directory.CreateDirectory(root);
        return ResolveLinks(root);
    }

    /// <summary>Follows every symbolic link along <paramref name="path"/>, which must exist.</summary>
    private static string ResolveLinks(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var resolved = Path.GetPathRoot(full)!;

        foreach (var segment in full[resolved.Length..].Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            resolved = Path.Combine(resolved, segment);

            if (new DirectoryInfo(resolved).ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                resolved = Path.TrimEndingDirectorySeparator(target.FullName);
            }
        }

        return resolved;
    }

    private static string ResolveDotnet()
    {
        var fromSdk = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromSdk) && File.Exists(fromSdk))
        {
            return fromSdk;
        }

        var current = Environment.ProcessPath;
        if (current is not null
            && string.Equals(Path.GetFileNameWithoutExtension(current), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return current;
        }

        return "dotnet";
    }
}
