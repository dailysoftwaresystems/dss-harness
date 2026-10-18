using System.Globalization;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Containment: what an agent starts is ended whole, and a signal to the parent does not reach it.
/// The child is placed before it runs a line of its own, through the tool's own
/// <c>contain-exec</c> step, on every system the suite runs on.
/// </summary>
public sealed class ContainExecTests
{
    /// <summary>
    /// A contained child on Linux and macOS is a session and group of its own: the terminal's
    /// signals go to a foreground group of the session the terminal belongs to, and a session of
    /// one's own is not it - which is what keeps a contained agent alive to be asked for its
    /// handoff when a first Ctrl+C is typed.
    /// </summary>
    [Fact]
    public async Task AContainedChild_OnLinuxAndMacOS_IsASessionAndGroupOfItsOwn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session and group are the C library's, and Windows has the job object instead.");

        var (ids, result) = await RunChild("print-ids", contained: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal((ids["pid"], ids["pid"]), (ids["pgid"], ids["sid"]));
    }

    /// <summary>
    /// A child apart from the terminal is in a group of its own, within the session it was started
    /// in: an interruption typed at the terminal goes to a group, and that group is not this
    /// child's - while the session, and what outlives a stopped run, stay where they were.
    /// </summary>
    [Fact]
    public async Task AChildApartFromTheTerminal_IsAGroupOfItsOwn_InTheSessionItWasStartedIn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the group of a leg is spelled by the job object there, and read from the process table.");

        var (ids, result) = await RunChild("print-ids", apart: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ids["pid"], ids["pgid"]);
        Assert.Equal(GetSidOfThisProcess(), ids["sid"]);
    }

    /// <summary>
    /// A child started as children always were is in the group and session of the one that started
    /// it: containment is asked for, never imposed, and a leg typed by hand runs as it always ran.
    /// </summary>
    [Fact]
    public async Task AChildStartedAsChildrenAlwaysWere_IsInThisProcessGroupAndSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the group of a leg is spelled by the job object there, and read from the process table.");

        var (ids, result) = await RunChild("print-ids");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GetGroupOfThisProcess(), ids["pgid"]);
        Assert.Equal(GetSidOfThisProcess(), ids["sid"]);
    }

    /// <summary>
    /// A contained child on Windows cannot outlive the job that holds it: the run ends, the job's
    /// last handle closes with it, and what the child left behind - a sleeper whose parent is gone,
    /// reachable by no tree - ends with the job. The engine's own end closes the same handle.
    /// </summary>
    [Fact]
    public async Task AContainedChild_OnWindows_CannotOutliveTheJobThatHoldsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job object is Windows' own container.");

        var (_, result, orphan) = await RunWithOrphan(contained: true);

        Assert.Equal(0, result.ExitCode);
        Assert.True(AwaitGone(orphan), $"the sleeper {orphan} outlived the job that held it");
    }

    /// <summary>
    /// And an ordinary child's leftovers are left: nothing ends them but the one that started them,
    /// which is what containment is asked for and never imposed.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryChildsLeftovers_AreLeftAsTheyAlwaysWere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job object is Windows' own container.");

        var (_, result, orphan) = await RunWithOrphan();

        Assert.Equal(0, result.ExitCode);
        Assert.True(StillAlive(orphan), $"the sleeper {orphan} was ended by nothing that was asked to");

        // What the test started, the test ends.
        TryEnding(orphan);
    }

    /// <summary>
    /// A contained child stopped by its bound is ended through its container: the whole job goes at
    /// once, and the wait for the child ends.
    /// </summary>
    [Fact]
    public async Task AContainedChild_StoppedByItsBound_IsEndedThroughItsContainer()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job's ending of its members is Windows' own.");

        using var temp = new TempDirectory();
        var runner = ContainingRunner();
        var started = DateTime.UtcNow;

        var result = await runner.RunAsync(
            Child("sleep", temp.Combine("stopped.log"), "60000") with { Contained = true, Timeout = TimeSpan.FromSeconds(2) },
            TestContext.Current.CancellationToken);

        Assert.True(result.TimedOut);
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), $"stopping through the job took {DateTime.UtcNow - started}");
    }

    /// <summary>
    /// A contained child stopped on Linux and macOS is ended through its group, which reaches what
    /// a tree-walk cannot: the sleeper this child started stays in the group while its parent
    /// lives, and ends with the group when the bound stops the run - where the tree the child is
    /// the root of holds only the child itself, the sleeper being a grandchild no walk from the
    /// child reaches once the group's own leader is gone.
    /// </summary>
    [Fact]
    public async Task AContainedChild_StoppedOnLinuxAndMacOS_EndsItsWholeGroup()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the group's end by signal is the C library's; Windows ends the job instead.");

        using var temp = new TempDirectory();
        var runner = ContainingRunner();
        var started = DateTime.UtcNow;
        var orphan = 0;

        var result = await runner.RunAsync(
            new ProcessRequest
            {
                FileName = TestHost.ChildRequest("spawn-orphan-and-sleep").FileName,
                Arguments = [.. TestHost.ChildRequest("spawn-orphan-and-sleep", "300000", "300000").Arguments],
                Environment = TestHost.ChildRequest("spawn-orphan-and-sleep").Environment,
                Contained = true,
                Timeout = TimeSpan.FromSeconds(2),
                OnOutputLine = line =>
                {
                    if (line.StartsWith("orphan=", StringComparison.Ordinal))
                    {
                        orphan = int.Parse(line["orphan=".Length..], CultureInfo.InvariantCulture);
                    }
                },
            },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, orphan);
        Assert.True(result.TimedOut);
        Assert.True(AwaitGone(orphan), $"the sleeper {orphan} outlived the group that held it");
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(30), $"stopping through the group took {DateTime.UtcNow - started}");
    }

    /// <summary>
    /// The step itself, as the tool runs it: the program's own streams and exit code come through,
    /// a program that is not there is said and ends with the step's own code, and a command line
    /// with no program after '--' is a usage error. The root help does not list it - nobody types it.
    /// </summary>
    [Fact]
    public async Task TheStepItself_HandsOnStreamsAndCode_AndIsRefusedWhereItHasNoProgram()
    {
        var version = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.GroupOption, ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, version.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(version.StandardOutput), "the program's own output came through the step's streams");

        var missing = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.GroupOption, ContainExec.Separator, "a-program-that-is-not-there"],
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainExec.Failed, missing.ExitCode);
        Assert.Contains("could not", missing.StandardError, StringComparison.Ordinal);

        var empty = await CliRunner.RunAsync([ContainExec.CommandName], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, empty.ExitCode);
        Assert.Contains("no program", empty.StandardError, StringComparison.Ordinal);

        var both = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.JobOption, "a-name", ContainExec.GroupOption, ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(ContainExec.Failed, both.ExitCode);
        Assert.Contains("it takes one", both.StandardError, StringComparison.Ordinal);

        var help = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(ContainExec.CommandName, help.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A child apart from the terminal runs through the same step on Windows, as on Linux and
    /// macOS: the group of its own is made by the step, and the run reads the child and its code
    /// through it as through any other.
    /// </summary>
    [Fact]
    public async Task AChildApartFromTheTerminal_OnWindows_RunsThroughTheStepAndEndsWithItsOwnCode()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the step's own system is named here; elsewhere the group is the session's.");

        using var temp = new TempDirectory();
        var runner = ContainingRunner();

        var result = await runner.RunAsync(
            Child("sleep", temp.Combine("apart.log"), "300") with { ApartFromTerminal = true },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Duration < TimeSpan.FromSeconds(30), $"the run through the step took {result.Duration}");
    }

    /// <summary>Runs one child behaviour, contained or apart, and reads the ids it printed.</summary>
    private static async Task<(Dictionary<string, int> Ids, ProcessResult Result)> RunChild(string mode, bool contained = false, bool apart = false)
    {
        using var temp = new TempDirectory();
        var runner = ContainingRunner();
        var result = await runner.RunAsync(
            Child(mode, temp.Combine("ids.log")) with { Contained = contained, ApartFromTerminal = apart },
            TestContext.Current.CancellationToken);

        var ids = result.StandardOutput
            .Split((char[]?)[], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Contains('=', StringComparison.Ordinal))
            .Select(token => token.Split('='))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], CultureInfo.InvariantCulture));

        return (ids, result);
    }

    /// <summary>Runs one child behaviour that starts a sleeper and exits, and reads the sleeper's id.</summary>
    private static async Task<(Dictionary<string, int> Ids, ProcessResult Result, int Orphan)> RunWithOrphan(bool contained = false)
    {
        using var temp = new TempDirectory();
        var runner = ContainingRunner();
        var result = await runner.RunAsync(
            Child("spawn-orphan", temp.Combine("orphan.log"), "300000") with { Contained = contained },
            TestContext.Current.CancellationToken);

        var orphan = int.Parse(
            result.StandardOutput.Split('=', StringSplitOptions.RemoveEmptyEntries)[^1].Trim(),
            CultureInfo.InvariantCulture);

        return ([], result, orphan);
    }

    /// <summary>The process runner, starting contained children through the tool the build makes.</summary>
    private static ProcessRunner ContainingRunner()
        => new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram);

    /// <summary>A request that runs this assembly as a child, as the process tests do.</summary>
    private static ProcessRequest Child(string mode, string logFile, params string[] arguments)
    {
        var request = TestHost.ChildRequest(mode, arguments);

        return new ProcessRequest
        {
            FileName = request.FileName,
            Arguments = request.Arguments,
            Environment = request.Environment,
            WorkingDirectory = request.WorkingDirectory,
        };
    }

    /// <summary>Whether <paramref name="process"/> is gone, looking for as long as a container takes to end it.</summary>
    private static bool AwaitGone(int process)
    {
        for (var waited = TimeSpan.Zero; waited < TimeSpan.FromSeconds(10); waited += TimeSpan.FromMilliseconds(100))
        {
            if (!StillAlive(process))
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(100));
        }

        return false;
    }

    /// <summary>Whether <paramref name="process"/> is running now.</summary>
    private static bool StillAlive(int process)
    {
        try
        {
            using var found = System.Diagnostics.Process.GetProcessById(process);
            return !found.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Ends <paramref name="process"/> where the test started it, taking its tree with it.</summary>
    private static void TryEnding(int process)
    {
        try
        {
            using var found = System.Diagnostics.Process.GetProcessById(process);
            found.Kill(entireProcessTree: true);
        }
        catch (ArgumentException)
        {
            // Already gone.
        }
    }

    /// <summary>This process's own group, on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getpgid(int process);

    /// <summary>This process's own session, on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getsid(int process);

    private static int GetGroupOfThisProcess() => getpgid(0);

    private static int GetSidOfThisProcess() => getsid(0);
}
