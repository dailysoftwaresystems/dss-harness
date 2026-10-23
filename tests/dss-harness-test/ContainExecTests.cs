using System.ComponentModel;
using System.Diagnostics;
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
    /// <summary>The longest any run here is let take: a bound on a hang, never a measure of one.</summary>
    private static readonly TimeSpan LongBound = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A contained child on Linux and macOS is a session and group of its own: the terminal's
    /// signals go to a foreground group of the session the terminal belongs to, and a session of
    /// one's own is not it - which is what keeps a contained agent alive to be asked for its
    /// handoff when a first Ctrl+C is typed.
    /// </summary>
    [Fact]
    public async Task AContainedChild_OnLinuxAndMacOS_IsASessionAndGroupOfItsOwn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session and group are the C library's; Windows has the job object, which the job tests here hold.");

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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows a child apart from the terminal starts as any other, which AChildApartFromTheTerminal_IsStartedByThisProcessItself holds.");

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
        Assert.SkipWhen(OperatingSystem.IsWindows(), "groups and sessions are the C library's; on Windows an ordinary child's leftovers are held by AnOrdinaryChildsLeftovers_AreLeftAsTheyAlwaysWere.");

        var (ids, result) = await RunChild("print-ids");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(GetGroupOfThisProcess(), ids["pgid"]);
        Assert.Equal(GetSidOfThisProcess(), ids["sid"]);
    }

    /// <summary>
    /// A contained child on Windows cannot outlive the job that holds it: the run ends, the job ends
    /// with it, and what the child left behind - a sleeper whose parent is gone, reachable by no
    /// tree - ends with the job. The engine's own end closes the same handle.
    /// </summary>
    [Fact]
    public async Task AContainedChild_OnWindows_CannotOutliveTheJobThatHoldsIt()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job object is Windows' own container; on Linux and macOS a contained child's leftovers end with its session.");

        var (result, orphan) = await RunWithOrphan(contained: true);

        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.True(orphan.AwaitGone(), $"the sleeper {orphan} outlived the job that held it");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// And an ordinary child's leftovers are left: nothing ends them but the one that started them,
    /// which is what containment is asked for and never imposed.
    /// </summary>
    [Fact]
    public async Task AnOrdinaryChildsLeftovers_AreLeftAsTheyAlwaysWere()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the sleeper is started out of every tree by Windows' own call; on Linux and macOS AChildStartedAsChildrenAlwaysWere_IsInThisProcessGroupAndSession holds an ordinary child.");

        var (result, orphan) = await RunWithOrphan();

        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.True(orphan.Running, $"the sleeper {orphan} was ended by nothing that was asked to");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// A bound child on Windows ends with the process that started it, killed where it stood - a hard kill, which runs
    /// nothing of that process's own - while a child started as children always were outlives it: what a dispatcher killed
    /// by a consumer left running for minutes, its legs' test runners each holding a heavy leg's room.
    /// </summary>
    [Fact]
    public async Task ABoundChild_OnWindows_EndsWithTheProcessThatStartedIt_KilledWhereItStood()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job a bound child joins is Windows' own; on Linux and macOS a bound child starts as any other, which ABoundChild_OnLinuxAndMacOS_StartsAsAnyOther holds.");

        var ids = new TaskCompletionSource<Dictionary<string, int>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create()).RunAsync(
            Child("bind-and-wait", CliRunner.CliProgram, "300000") with
            {
                OnOutputLine = line =>
                {
                    if (line.StartsWith("parent=", StringComparison.Ordinal))
                    {
                        ids.TrySetResult(Ids(line));
                    }
                },
            },
            TestContext.Current.CancellationToken);

        var said = await ids.Task.WaitAsync(LongBound, TestContext.Current.CancellationToken);
        var bound = LearnedProcess.Of(said["bound"]);
        var ordinary = LearnedProcess.Of(said["ordinary"]);

        try
        {
            using (var parent = Process.GetProcessById(said["parent"]))
            {
                parent.Kill(entireProcessTree: false);
            }

            Assert.True(bound.AwaitGone(), $"the bound sleeper {bound} outlived the process that started it");
            Assert.True(ordinary.Running, $"the ordinary sleeper {ordinary} was ended by nothing that was asked to");
        }
        finally
        {
            bound.TryEnding();
            ordinary.TryEnding();
        }

        // Only now: the ordinary sleeper holds the output its parent inherited, which this run reads to its end.
        await running.WaitAsync(LongBound, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// A bound child's end, unlike a contained one's, ends nothing: what it left running lives on until the process that
    /// started it ends - a server its build started stays for the next phase - and so does what a stopped one left.
    /// </summary>
    [Fact]
    public async Task ABoundChildsLeftovers_OnWindows_OutliveItsOwnEnd()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the sleeper is started out of every tree by Windows' own call; on Linux and macOS a bound child starts as any other.");

        var result = await ContainingRunner().RunAsync(
            Child("spawn-orphan", "300000") with { Bound = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);
        var orphan = LearnedProcess.In(result.StandardOutput.Trim(), "orphan=") ?? LearnedProcess.None;

        try
        {
            Assert.Equal(0, result.ExitCode);
            Assert.True(orphan.Running, $"the sleeper {orphan} was ended by its bound parent's own end");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// A bound child stopped is stopped as any other child is - by its tree - and what it left outside its tree lives on
    /// until the process that started it ends: its job is ended by nothing but that.
    /// </summary>
    [Fact]
    public async Task ABoundChildStopped_OnWindows_LeavesWhatItLeftOutsideItsTreeToItsJob()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the sleeper is started out of every tree by Windows' own call; on Linux and macOS a bound child starts as any other.");

        var orphan = LearnedProcess.None;

        try
        {
            var (raised, _) = await RunAndStopOn(
                Child("spawn-orphan-and-sleep", "300000", "300000") with { Bound = true },
                line => (orphan = LearnedProcess.In(line, "orphan=") ?? orphan).Id != 0);

            Assert.IsType<ProcessStoppedException>(raised);
            Assert.True(orphan.Running, $"the sleeper {orphan} was ended by its bound parent's stop");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// What a bound child leaves running that holds its output holds the run no longer than the bound, on every system,
    /// and lives on: it outlives the child by design - a server its build started, kept for the next phase - and the run,
    /// reading to the end the leftover held off, waited for as long as it lived, the run's time limit and its caller's stop
    /// reaching nothing once the child had gone.
    /// </summary>
    [Fact]
    public async Task ABoundChildsLeftoverHoldingItsOutput_HoldsTheRunNoLongerThanTheBound()
    {
        var holder = LearnedProcess.None;

        try
        {
            var result = await ContainingRunner().RunAsync(
                Child("spawn-holder", "exit", "0", "sleep", "300000") with
                {
                    Bound = true,
                    OnOutputLine = line => holder = LearnedProcess.In(line, "holder=") ?? holder,
                },
                TestContext.Current.CancellationToken).WaitAsync(ProcessRunner.DrainBound + TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

            Assert.Equal((0, false), (result.ExitCode, result.TimedOut));
            Assert.Equal($"holder={holder}\n", result.StandardOutput);
            Assert.True(holder.Running, $"the leftover {holder} was ended by its bound parent's end");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A bound child stopped is not held by what it left outside its tree holding its output: the stop ends its tree, the
    /// leftover lives on, and the run ends within the bound once the child has gone - never once the leftover has.
    /// </summary>
    [Fact]
    public async Task ABoundChildStopped_IsNotHeldByALeftoverOutsideItsTreeHoldingItsOutput()
    {
        var holder = LearnedProcess.None;

        try
        {
            var (raised, since) = await RunAndStopOn(
                Child("spawn-orphan-holder", "300000") with
                {
                    Bound = true,
                    OnOutputLine = line => holder = LearnedProcess.In(line, "holder=") ?? holder,
                },
                line => string.Equals(line, "orphaned", StringComparison.Ordinal));

            Assert.IsType<ProcessStoppedException>(raised);
            Assert.True(since < ProcessRunner.DrainBound + TimeSpan.FromMinutes(1), $"the stopped run waited {since} for the leftover's end");
            Assert.True(holder.Running, $"the leftover {holder} outside the stopped child's tree was ended by the stop");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// On Linux and macOS a bound child starts as any other, in the group and session of the one that started it: nothing
    /// there ends a child with the process that started it, and an interrupt typed at the terminal still reaches it.
    /// </summary>
    [Fact]
    public async Task ABoundChild_OnLinuxAndMacOS_StartsAsAnyOther()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "on Windows a bound child joins the job the process holds, which ABoundChild_OnWindows_EndsWithTheProcessThatStartedIt_KilledWhereItStood holds.");

        var result = await ContainingRunner().RunAsync(
            Child("print-ids") with { Bound = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);
        var ids = Ids(result.StandardOutput);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal((GetGroupOfThisProcess(), GetSidOfThisProcess()), (ids["pgid"], ids["sid"]));
    }

    /// <summary>
    /// A bound child is read as any other: its own streams and exit code, and said to have started before its first line.
    /// </summary>
    [Fact]
    public async Task ABoundChild_HandsOnItsOwnStreamsAndExitCode()
    {
        var heard = new List<string>();

        var result = await ContainingRunner().RunAsync(
            Child("say-and-exit", "said out", "said err", "17") with
            {
                Bound = true,
                OnStarted = () => Heard(heard, "started"),
                OnOutputLine = line => Heard(heard, line),
            },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.Equal((17, false), (result.ExitCode, result.TimedOut));
        Assert.Equal(("said out\n", "said err\n"), (result.StandardOutput, result.StandardError));
        Assert.Equal(["started", "said out"], heard);
    }

    /// <summary>
    /// A child held in a job is given build servers of its own where its environment names none - the compiler's
    /// program-database server at an endpoint named for the job, and no node of the build engine kept for a later build -
    /// and keeps those its environment names, in whatever case.
    /// </summary>
    [Fact]
    public void AChildHeldInAJob_IsGivenBuildServersOfItsOwn_WhereItsEnvironmentNamesNone()
    {
        var bare = new Dictionary<string, string?>(StringComparer.Ordinal);
        ProcessRunner.OwnServers(bare, "dssharness-contain-x");

        Assert.Equal(
            ["MSBUILDDISABLENODEREUSE=1", "_MSPDBSRV_ENDPOINT_=dssharness-contain-x"],
            bare.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal));

        var named = new Dictionary<string, string?>(StringComparer.Ordinal) { ["_mspdbsrv_endpoint_"] = "mine", ["MSBuildDisableNodeReuse"] = "0" };
        ProcessRunner.OwnServers(named, "dssharness-contain-x");

        Assert.Equal(["MSBuildDisableNodeReuse=0", "_mspdbsrv_endpoint_=mine"], named.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// What the step says of a start is read as it said it: placed - with the start of the process that becomes the
    /// program, where it said one - and what failed after, the start kept; a line that is neither says nothing.
    /// </summary>
    [Fact]
    public void WhatTheStepSays_IsReadWithTheStartItSaid()
    {
        Assert.Equal(new ContainExec.StartHeard(true, null, 0, "boot-1:42"), ContainExec.Heard("placed boot-1:42\n"));
        Assert.Equal(new ContainExec.StartHeard(true, null, 0), ContainExec.Heard("placed\n"));
        Assert.Equal(new ContainExec.StartHeard(true, ContainExec.Stage.Start, 2, "boot-1:42"), ContainExec.Heard("placed boot-1:42\nfailed start 2\n"));
        Assert.Equal(new ContainExec.StartHeard(false, null, 0), ContainExec.Heard("placed two words\n"));
    }

    /// <summary>
    /// A run its caller stops once its child has started is thrown as the runner's own stop, contained or not - a
    /// cancellation every catch of one still takes, carrying what the container could not reach as data: nothing, where
    /// it reached all it holds or there was none, said as any cancellation is.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AStoppedRun_IsThrownAsAStop_CarryingWhatItsContainerCouldNotReach(bool contained)
    {
        var (raised, _) = await RunAndStopOn(
            Child("say-pid-and-sleep", "300000") with { Contained = contained },
            line => LearnedProcess.In(line, "pid=") is not null);

        var stopped = Assert.IsType<ProcessStoppedException>(raised);

        Assert.Equal((null, new OperationCanceledException().Message), (stopped.Uncontained, stopped.Message));
        Assert.True(stopped.CancellationToken.IsCancellationRequested);
    }

    /// <summary>
    /// An endpoint named for a job of this tool's own - inherited by a harness a bound phase of another started - is
    /// replaced by the child's own job's, in whatever case it is named: a server started there under the inner job would
    /// end with that job while the outer run's builds still used it. The build engine's switch it inherits stands.
    /// </summary>
    [Fact]
    public void AnEndpointOfAnotherJobOfThisTool_IsReplacedByTheChildsOwnJobs()
    {
        var inherited = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["_mspdbsrv_endpoint_"] = $"{ProcessRunner.JobPrefix}outer",
            ["MSBUILDDISABLENODEREUSE"] = "1",
        };

        ProcessRunner.OwnServers(inherited, $"{ProcessRunner.JobPrefix}inner");

        Assert.Equal(
            ["MSBUILDDISABLENODEREUSE=1", $"_mspdbsrv_endpoint_={ProcessRunner.JobPrefix}inner"],
            inherited.Select(pair => $"{pair.Key}={pair.Value}").Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// On Windows the job a bound child is held in is the process's, never the runner's that started it: each operation is
    /// given a runner of its own, and the bound children of every runner the container builds share the one job the
    /// process holds for its whole life - one job and one handle, however many operations ran, and never one closed with
    /// an operation's runner, ending what its children left for the next.
    /// </summary>
    [Fact]
    public async Task BoundChildrenOfEveryOperationsRunner_OnWindows_ShareTheOneJobTheProcessHolds()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job a bound child joins is Windows' own; on Linux and macOS a bound child starts as any other.");

        var token = TestContext.Current.CancellationToken;
        var endpoints = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => ContainingRunner().RunAsync(Child("print-env", ProcessRunner.ProgramDatabaseEndpoint) with { Bound = true }, token)))
            .WaitAsync(LongBound, token);
        var scanned = await ContainingRunner(new QuietProcessTable()).RunAsync(Child("print-env", ProcessRunner.ProgramDatabaseEndpoint) with { Bound = true }, token)
            .WaitAsync(LongBound, token);

        Assert.StartsWith(ProcessRunner.JobPrefix, endpoints[0].StandardOutput, StringComparison.Ordinal);
        Assert.Equal([endpoints[0].StandardOutput, endpoints[0].StandardOutput], [endpoints[1].StandardOutput, scanned.StandardOutput]);
    }

    /// <summary>
    /// On Windows the bound children of a runner given a job maker of its own share the one job it makes at the first of
    /// them, whichever start together - each given the compiler's server named for that job and no build node kept - while
    /// a contained child is given a job of its own; and the job is held apart from any child's start, so a collection ends
    /// none of them.
    /// </summary>
    [Fact]
    public async Task BoundChildren_OnWindows_ShareTheOneJobTheirRunnerMakes_AndHoldsAcrossACollection()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job a bound child joins is Windows' own; on Linux and macOS a bound child starts as any other.");

        var made = 0;
        var runner = new ProcessRunner(
            new HostPlatform(),
            FilePermissionsFactory.Create(),
            CliRunner.CliProgram,
            processTable: null,
            makeJob: name =>
            {
                _ = Interlocked.Increment(ref made);
                return Containment.CreateJob(name);
            });
        var token = TestContext.Current.CancellationToken;

        var endpoints = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => runner.RunAsync(Child("print-env", ProcessRunner.ProgramDatabaseEndpoint) with { Bound = true }, token)));
        var nodes = await runner.RunAsync(Child("print-env", ProcessRunner.NodeReuseOff) with { Bound = true }, token).WaitAsync(LongBound, token);
        var contained = await runner.RunAsync(Child("print-env", ProcessRunner.ProgramDatabaseEndpoint) with { Contained = true }, token).WaitAsync(LongBound, token);

        // The runner's one job for every bound child, and the contained child's own.
        Assert.Equal(2, made);
        Assert.Single(endpoints.Select(result => result.StandardOutput).Distinct(StringComparer.Ordinal));
        Assert.StartsWith("dssharness-contain-", endpoints[0].StandardOutput, StringComparison.Ordinal);
        Assert.Equal("1\n", nodes.StandardOutput);
        Assert.NotEqual(endpoints[0].StandardOutput, contained.StandardOutput);

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var said = new TaskCompletionSource<LearnedProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sleeping = runner.RunAsync(
            Child("say-pid-and-sleep", "300000") with { Bound = true, OnOutputLine = line => _ = LearnedProcess.In(line, "pid=") is { } id && said.TrySetResult(id) },
            stop.Token);
        var sleeper = await said.Task.WaitAsync(LongBound, token);

        try
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.True(sleeper.Running, $"the bound sleeper {sleeper} was ended by a collection: its job was held by nothing");
            Assert.Equal(2, made);
        }
        finally
        {
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sleeping.WaitAsync(LongBound, token));
            sleeper.TryEnding();
        }
    }

    /// <summary>
    /// On Windows a bound child whose job cannot be made never runs, saying why as any start's failure does, and
    /// the next bound child asks for the job again: a job that could not be made is never kept as one.
    /// </summary>
    [Fact]
    public async Task ABoundChildWhoseJobCannotBeMade_OnWindows_NeverRuns_AndTheNextAsksForItAgain()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the job a bound child joins is Windows' own; on Linux and macOS a bound child starts as any other.");

        var asked = 0;
        var runner = new ProcessRunner(
            new HostPlatform(),
            FilePermissionsFactory.Create(),
            CliRunner.CliProgram,
            processTable: null,
            makeJob: name => Interlocked.Increment(ref asked) == 1 ? throw new Win32Exception(5) : Containment.CreateJob(name));
        var token = TestContext.Current.CancellationToken;

        var raised = await Assert.ThrowsAsync<ProgramStartException>(
            () => runner.RunAsync(ByItsPath("say-and-exit", "said out", "said err", "0") with { Bound = true }, token).WaitAsync(LongBound, token));
        var ran = await runner.RunAsync(ByItsPath("say-and-exit", "said out", "said err", "0") with { Bound = true }, token).WaitAsync(LongBound, token);

        Assert.Equal(
            $"'{TestHost.AssemblyProgram}' could not be started: the job object that would contain it could not be made: {new Win32Exception(5).Message}",
            raised.Message);
        Assert.Equal((0, "said out\n"), (ran.ExitCode, ran.StandardOutput));
        Assert.Equal(2, asked);
    }

    /// <summary>
    /// A child started through the step is read as any other: its own output on each stream and its
    /// own exit code come through, never the step's, and it is said to have started before the first
    /// line it wrote is handed on - contained or apart from the terminal, on every system.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AChildStartedThroughTheStep_HandsOnItsOwnStreamsAndExitCode(bool contained)
    {
        var heard = new List<string>();

        var result = await ContainingRunner().RunAsync(
            Child("say-and-exit", "said out", "said err", "17") with
            {
                Contained = contained,
                ApartFromTerminal = !contained,
                OnStarted = () => Heard(heard, "started"),
                OnOutputLine = line => Heard(heard, line),
            },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.Equal((17, false), (result.ExitCode, result.TimedOut));
        Assert.Equal(("said out\n", "said err\n"), (result.StandardOutput, result.StandardError));
        Assert.Equal(["started", "said out"], heard);
    }

    /// <summary>
    /// A program named by a path where there is none, started through the step, is one that never
    /// ran - said as any start says it, never as the step's own end - and is never said to have
    /// started.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AProgramThatIsNotThere_StartedThroughTheStep_IsOneThatNeverRan(bool contained)
    {
        using var temp = new TempDirectory();
        var program = temp.Combine("not-there");
        var started = false;

        var raised = await Assert.ThrowsAsync<ExecutableNotFoundException>(() => ContainingRunner().RunAsync(
            new ProcessRequest { FileName = program, Contained = contained, ApartFromTerminal = !contained, OnStarted = () => started = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken));

        Assert.Equal($"Executable '{program}' does not exist.", raised.Message);
        Assert.False(started, "a program that never ran was said to have started");
    }

    /// <summary>
    /// A program that is there but cannot be started is one that never ran, the system's own reason
    /// said as any start says it - never the step's exit code, which a program could end with too -
    /// and is never said to have started.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AProgramThatCannotStart_StartedThroughTheStep_IsOneThatNeverRan_ForTheSystemsReason(bool contained)
    {
        Assert.SkipWhen(!contained && OperatingSystem.IsWindows(), "on Windows a child apart from the terminal starts as any other, whose failures to start ProcessRunnerTests hold.");

        using var temp = new TempDirectory();

        await NeverRunsAsADirectStartDoesNot(NotAProgram(temp), contained);
    }

    /// <summary>
    /// An executable file with no interpreter line started through the step fails as a start made
    /// directly fails, for the system's reason, on Linux and macOS: the step becomes the program by
    /// its whole path and nothing else, where the C library's PATH-searching call would have run the
    /// file under <c>/bin/sh</c> and so started, contained, what no other start starts.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnExecutableWithNoInterpreterLine_StartedThroughTheStep_IsOneThatNeverRan_AsADirectStartIs(bool contained)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "an interpreter line, and a shell run in its place, are Linux's and macOS's.");

        using var temp = new TempDirectory();
        var program = temp.Combine("no-interpreter-line");
        File.WriteAllText(program, "exit 0\n");

        // Always so past the skip above, which the platform analyser does not read.
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(program, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await NeverRunsAsADirectStartDoesNot(program, contained);
    }

    /// <summary>
    /// A program still held open for writing as the step becomes it is tried again, as any start
    /// is: Linux refuses a moment's "busy" start, and the step's start is no less a start for being
    /// made inside the child.
    /// </summary>
    [Fact]
    public async Task AProgramBeingWritten_StartedThroughTheStep_IsStartedOnceItIsNot()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "Linux is the system that refuses to start a program held open for writing.");

        using var temp = new TempDirectory();
        var program = TestHost.StartableProgram(temp.Path, "busy");
        var writer = new FileStream(program, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);

        // Released while the step is still trying: the tool itself starts in well under a second,
        // and the step tries again for two.
        var released = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
                await writer.DisposeAsync();
            },
            TestContext.Current.CancellationToken);

        try
        {
            var result = await ContainingRunner().RunAsync(
                new ProcessRequest { FileName = program, Contained = true },
                TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
        }
        finally
        {
            await released;
        }
    }

    /// <summary>
    /// What a contained child leaves holding its output when it ends of itself ends with it, at
    /// once, on every system: the job goes as the child does on Windows, and its session on Linux
    /// and macOS. Left for the run's own end, a server started in the background kept the reading
    /// of the output waiting for its own end.
    /// </summary>
    [Fact]
    public async Task AContainedChildsLeftover_EndsWithIt_AndTheOutputItHeldEnds()
    {
        var holder = LearnedProcess.None;
        var since = new Stopwatch();

        try
        {
            var result = await ContainingRunner().RunAsync(
                Child("spawn-holder", "exit", "0", "sleep", "300000") with
                {
                    Contained = true,
                    OnOutputLine = line =>
                    {
                        if (LearnedProcess.In(line, "holder=") is { } id)
                        {
                            holder = id;
                            since.Start();
                        }
                    },
                },
                TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

            since.Stop();

            Assert.Equal(0, result.ExitCode);
            Assert.NotEqual(0, holder.Id);
            Assert.True(holder.AwaitGone(), $"the holder {holder} outlived the contained child that left it");
            Assert.True(since.Elapsed < ProcessRunner.DrainBound, $"the run waited {since.Elapsed} for the leftover's end");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A contained child that is stopped takes with it what it left outside its tree, at once, on
    /// every system: a holder of the output whose parent has gone, which no walk of the child's tree
    /// reaches, is ended by the job on Windows and by the session on Linux and macOS - and the run
    /// does not wait for it.
    /// </summary>
    [Fact]
    public async Task AContainedChild_Stopped_EndsWhatItLeftOutsideItsTree_AndTheOutputItHeldEnds()
    {
        var holder = LearnedProcess.None;

        try
        {
            var (raised, since) = await RunAndStopOn(
                Child("spawn-orphan-holder", "300000") with
                {
                    Contained = true,
                    OnOutputLine = line => holder = LearnedProcess.In(line, "holder=") ?? holder,
                },
                line => string.Equals(line, "orphaned", StringComparison.Ordinal));

            Assert.IsType<ProcessStoppedException>(raised);
            Assert.NotEqual(0, holder.Id);
            Assert.True(holder.AwaitGone(), $"the holder {holder} outlived the stopped child's container");
            Assert.True(since < ProcessRunner.DrainBound, $"the stopped run waited {since} for the holder's end");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A contained child stopped on Linux and macOS is ended through its session, which reaches what
    /// a tree-walk cannot: the sleeper this child started through a middle that has gone is no one's
    /// child, and ends with the session when the run is stopped.
    /// </summary>
    [Fact]
    public async Task AContainedChild_StoppedOnLinuxAndMacOS_EndsItsWholeSession()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session's end by signal is the C library's; on Windows the job ends, which AContainedChild_Stopped_EndsWhatItLeftOutsideItsTree_AndTheOutputItHeldEnds holds.");

        var orphan = LearnedProcess.None;

        try
        {
            var (raised, _) = await RunAndStopOn(
                Child("spawn-orphan-and-sleep", "300000", "300000") with { Contained = true },
                line => (orphan = LearnedProcess.In(line, "orphan=") ?? orphan).Id != 0);

            Assert.IsType<ProcessStoppedException>(raised);
            Assert.True(orphan.AwaitGone(), $"the sleeper {orphan} outlived the session that held it");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// A contained child's descendant that moved to a group of its own - as <c>timeout</c> does - is
    /// ended with it at a stop on Linux and macOS: the session holds it, read before anything is
    /// signalled. Signalled first, the child's group ended the child, the descendant was handed to
    /// another parent, and a walk of the tree afterwards found nothing to end while the descendant
    /// held the output.
    /// </summary>
    [Fact]
    public async Task AContainedChildsDescendantInAGroupOfItsOwn_IsEndedWithItAtAStop_OnLinuxAndMacOS()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "groups are the C library's; on Windows every descendant is born in the job, which AContainedChild_Stopped_EndsWhatItLeftOutsideItsTree_AndTheOutputItHeldEnds holds.");

        var holder = LearnedProcess.None;

        try
        {
            var (raised, since) = await RunAndStopOn(
                Child("spawn-holder", "sleep", "300000", "own-group-and-sleep", "300000") with { Contained = true },
                line => (holder = LearnedProcess.In(line, "holder=") ?? holder).Id != 0);

            Assert.IsType<ProcessStoppedException>(raised);
            Assert.True(holder.AwaitGone(), $"the descendant {holder} in a group of its own outlived the stop");
            Assert.True(since < ProcessRunner.DrainBound, $"the stopped run waited {since} for the descendant's end");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A contained child stopped on Linux and macOS is asked to end before it is made to: the signal
    /// that asks comes first, so a program that tidies up on it is given the chance.
    /// </summary>
    [Fact]
    public async Task AContainedChild_StoppedOnLinuxAndMacOS_IsAskedToEndBeforeItIsMade()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the signal that asks is the C library's; Windows ends a job's members at once.");

        using var temp = new TempDirectory();
        var noted = temp.Combine("asked.txt");

        var (raised, _) = await RunAndStopOn(
            Child("note-term-and-sleep", noted, "300000") with { Contained = true },
            line => string.Equals(line, "ready", StringComparison.Ordinal));

        Assert.IsType<ProcessStoppedException>(raised);
        Assert.Equal("asked", File.ReadAllText(noted));
    }

    /// <summary>
    /// What a contained child left that refuses to be asked is killed once the grace is spent, on
    /// Linux and macOS: the asking signal is a request, and the run's stop is not.
    /// </summary>
    [Fact]
    public async Task AContainedChildsLeftoverThatRefusesToBeAsked_IsKilledOnceTheGraceIsSpent()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the signal that asks is the C library's; Windows ends a job's members at once.");

        using var temp = new TempDirectory();
        var ready = temp.Combine("ready.txt");
        var orphan = LearnedProcess.None;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var run = ContainingRunner().RunAsync(
                Child("spawn-orphan-and-sleep", "300000", "300000", "ignore-term-and-sleep", ready) with
                {
                    Contained = true,
                    Timeout = LongBound,
                    OnOutputLine = line => orphan = LearnedProcess.In(line, "orphan=") ?? orphan,
                },
                stop.Token);

            // Stopped once the leftover refuses the asking, which it says by the file.
            Assert.True(Until(() => orphan.Id != 0 && File.Exists(ready)), "the leftover never said it refuses to be asked");
            await stop.CancelAsync();

            Assert.IsType<ProcessStoppedException>(await Record.ExceptionAsync(() => run.WaitAsync(LongBound, TestContext.Current.CancellationToken)));
            Assert.True(orphan.AwaitGone(), $"the leftover {orphan}, refusing to be asked, outlived the grace");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// A child apart from the terminal is stopped as any other child is, by its tree and at once:
    /// it is no contained child, and no grace is given to a program that refuses to be asked - a
    /// leg's build is cancelled as it always was.
    /// </summary>
    [Fact]
    public async Task AChildApartFromTheTerminal_IsStoppedByItsTree_AtOnce()
    {
        using var temp = new TempDirectory();
        var ready = temp.Combine("ready.txt");
        var child = LearnedProcess.None;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);

        try
        {
            var run = ContainingRunner().RunAsync(
                Child("say-pid-then", "ignore-term-and-sleep", "300000", ready) with
                {
                    ApartFromTerminal = true,
                    Timeout = LongBound,
                    OnOutputLine = line => child = LearnedProcess.In(line, "pid=") ?? child,
                },
                stop.Token);

            Assert.True(Until(() => child.Id != 0 && File.Exists(ready)), "the child never said it refuses to be asked");

            var since = Stopwatch.StartNew();
            await stop.CancelAsync();

            Assert.IsType<ProcessStoppedException>(await Record.ExceptionAsync(() => run.WaitAsync(LongBound, TestContext.Current.CancellationToken)));
            Assert.True(since.Elapsed < ProcessRunner.SessionGrace, $"stopping a child apart from the terminal took {since.Elapsed}");
            Assert.True(child.AwaitGone(), $"the child {child} outlived its stop");
        }
        finally
        {
            child.TryEnding();
        }
    }

    /// <summary>
    /// A process group a sweep ends where the process table cannot be read is ended by its id, which needs no table, and
    /// says what was not reached as a group's: where the table cannot be read at all, and where it reads once and not
    /// again once the group was asked to end. One already ended, its table unread, is said as one that could not be told
    /// to have nothing left - never taken for one that held nothing.
    /// </summary>
    [Theory]
    [InlineData("never")]
    [InlineData("once")]
    [InlineData("ended")]
    public async Task AGroupLeftRunning_IsEndedByItsId_WhereTheTableCannotBeRead_SayingWhatWasNotReached(string read)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a group is read from the process table on Linux and macOS; Windows records none to end.");

        using var group = Process.Start(TestHost.StartOf(Child("own-group-and-sleep", "300000")))!;

        try
        {
            Assert.True(Until(() => GroupOf(group.Id) == group.Id), $"the process {group.Id} never led a group of its own");

            if (read == "ended")
            {
                group.Kill();
                await group.WaitForExitAsync(TestContext.Current.CancellationToken);
            }

            var platform = new HostPlatform();
            IProcessTable table = read == "once"
                ? new ReadOnce(ProcessTableFactory.Create(platform, ContainingRunner()), UnreadableTable.Raising)
                : UnreadableTable.Raising;

            var ending = await new LeftSessions(platform, table).EndAsync(group.Id, group: true).WaitAsync(LongBound, TestContext.Current.CancellationToken);

            Assert.Equal(
                new HeldEnding(
                    Found: true,
                    read switch
                    {
                        "never" => UnreadableTable.Unreached("its group was ended", group: true),
                        "ended" => UnreadableTable.Unreached("its group had already ended", group: true),
                        _ => "the process table could not be read again once the process group a child apart from the terminal led had been asked "
                            + $"to end ({UnreadableTable.PsNotStarted}): its group was ended, and what of its tree had left the group was not reached",
                    }),
                ending);
            Assert.True(group.WaitForExit(LongBound), $"the group {group.Id} outlived its end");
        }
        finally
        {
            if (!group.HasExited)
            {
                group.Kill();
            }
        }
    }

    /// <summary>
    /// A contained child that ends of itself on Linux or macOS where the process table cannot be
    /// read - on macOS it is read through <c>ps</c>, which a session that has used up its processes
    /// leaves unable to start - still has its group ended, which needs no table, and the run ends
    /// as the child did, saying what was not reached: never in the table's failure.
    /// </summary>
    [Fact]
    public async Task AContainedChildsGroup_IsEndedAtItsOwnEnd_WhereTheTableCannotBeRead_AndTheRunSaysWhatWasNotReached()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session is read from the process table on Linux and macOS; Windows ends the job, which needs none.");

        var holder = LearnedProcess.None;

        try
        {
            var result = await ContainingRunner(UnreadableTable.Raising).RunAsync(
                Child("spawn-holder", "exit", "0", "sleep", "300000") with
                {
                    Contained = true,
                    OnOutputLine = line => holder = LearnedProcess.In(line, "holder=") ?? holder,
                },
                TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

            Assert.Equal((0, false), (result.ExitCode, result.TimedOut));
            Assert.Equal(UnreadableTable.Unreached("its group was ended"), result.Uncontained);
            Assert.True(holder.AwaitGone(), $"the holder {holder} in the child's group outlived the child's end");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A contained child stopped on Linux or macOS where the process table cannot be read is stopped
    /// all the same: its group - an orphan in it among them - is asked to end and then made to, and
    /// the stop carries what was not reached, and says it, instead of failing with the table.
    /// </summary>
    [Fact]
    public async Task AContainedChild_StoppedWhereTheTableCannotBeRead_IsStoppedWithItsGroup_AndTheStopSaysWhatWasNotReached()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session is read from the process table on Linux and macOS; Windows ends the job, which needs none.");

        var orphan = LearnedProcess.None;

        try
        {
            var (raised, _) = await RunAndStopOn(
                Child("spawn-orphan-and-sleep", "300000", "300000") with { Contained = true },
                line => (orphan = LearnedProcess.In(line, "orphan=") ?? orphan).Id != 0,
                ContainingRunner(UnreadableTable.Raising));

            var stopped = Assert.IsType<ProcessStoppedException>(raised);

            Assert.Equal(
                (UnreadableTable.Unreached("its group was ended"), $"The run was stopped, and {UnreadableTable.Unreached("its group was ended")}."),
                (stopped.Uncontained, stopped.Message));
            Assert.True(orphan.AwaitGone(), $"the orphan {orphan} in the stopped child's group outlived the stop");
        }
        finally
        {
            orphan.TryEnding();
        }
    }

    /// <summary>
    /// A stop ends the child by its tree whatever ending its container met: a container whose end
    /// fails in a way nothing foresaw still leaves the child itself ended, so the wait for it ends.
    /// </summary>
    [Fact]
    public async Task AContainedChild_IsEndedByItsTree_WhereEndingItsContainerFails()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "the session is read from the process table on Linux and macOS; Windows ends the job, which needs none.");

        var child = LearnedProcess.None;

        try
        {
            var (raised, _) = await RunAndStopOn(
                Child("say-pid-and-sleep", "300000") with { Contained = true },
                line => (child = LearnedProcess.In(line, "pid=") ?? child).Id != 0,
                ContainingRunner(new UnreadableTable(new InvalidOperationException("nothing foresaw this"))));

            Assert.Equal("nothing foresaw this", Assert.IsType<InvalidOperationException>(raised).Message);
            Assert.True(child.AwaitGone(), $"the child {child} outlived a stop whose container's end failed");
        }
        finally
        {
            child.TryEnding();
        }
    }

    /// <summary>
    /// What left both a contained child's session and its tree - a daemon that made a session of
    /// its own and whose parent has gone - is reached by nothing, and holds the run's output no
    /// longer than the bound: the run ends once the bound is spent, with the output read so far.
    /// </summary>
    [Fact]
    public async Task WhatLeftAContainedChildsSessionAndTree_HoldsItsOutputNoLongerThanTheBound()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "sessions are the C library's; on Windows nothing born in the job leaves it.");

        var holder = LearnedProcess.None;

        try
        {
            var result = await ContainingRunner().RunAsync(
                Child("spawn-holder", "exit", "0", "own-session-and-sleep", "300000") with
                {
                    Contained = true,
                    OnOutputLine = line => holder = LearnedProcess.In(line, "holder=") ?? holder,
                },
                TestContext.Current.CancellationToken).WaitAsync(ProcessRunner.DrainBound + TimeSpan.FromMinutes(1), TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal($"holder={holder}\n", result.StandardOutput);
            Assert.True(holder.Running, $"the daemon {holder} was reached by what reaches nothing outside the session and the tree");
        }
        finally
        {
            holder.TryEnding();
        }
    }

    /// <summary>
    /// A signal to the parent's whole group does not reach its contained child on Linux and macOS:
    /// the parent, started apart from the terminal, starts one child contained and one as children
    /// always were, and a SIGINT to the parent's group - what a terminal sends at Ctrl+C - ends the
    /// parent and the child in its group, and leaves the contained one running.
    /// </summary>
    [Fact]
    public async Task ASignalToTheParentsGroup_DoesNotReachItsContainedChild_OnLinuxAndMacOS()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "groups and their signals are the C library's; AContainedChild_OnWindows_IsInAGroupOfItsOwn_ThatTheConsolesInterruptDoesNotReach holds Windows.");

        var ids = new TaskCompletionSource<(int Parent, LearnedProcess Contained, LearnedProcess Ordinary)>(TaskCreationOptions.RunContinuationsAsynchronously);
        (int Parent, LearnedProcess Contained, LearnedProcess Ordinary) said = default;

        try
        {
            var run = ContainingRunner().RunAsync(
                Child("contain-and-wait", CliRunner.CliProgram, "300000") with
                {
                    ApartFromTerminal = true,
                    Timeout = LongBound,
                    OnOutputLine = line =>
                    {
                        var words = line.Split(' ').Select(word => word.Split('=')).ToDictionary(pair => pair[0], pair => int.Parse(pair[1], CultureInfo.InvariantCulture));
                        ids.TrySetResult((words["parent"], LearnedProcess.Of(words["contained"]), LearnedProcess.Of(words["ordinary"])));
                    },
                },
                TestContext.Current.CancellationToken);

            said = await ids.Task.WaitAsync(LongBound, TestContext.Current.CancellationToken);

            Assert.Equal(0, kill(-said.Parent, Interrupt));

            await run.WaitAsync(LongBound, TestContext.Current.CancellationToken);

            Assert.True(said.Ordinary.AwaitGone(), $"the child {said.Ordinary} in its parent's group outlived the signal to the group");
            Assert.True(said.Contained.Running, $"the contained child {said.Contained} was reached by the signal to its parent's group");
        }
        finally
        {
            said.Contained.TryEnding();
            said.Ordinary.TryEnding();
        }
    }

    /// <summary>
    /// A contained child on Windows is in a process group of its own, which the console's
    /// interruption does not reach: the child sends Ctrl+C to every process of the console it shares
    /// with the step, and is not interrupted. The same child started as any other is interrupted by
    /// its own, which says this process lets the interruption through at all - a process started
    /// with it turned off hands that on to every child, and then no group shows anything.
    /// </summary>
    [Fact]
    public async Task AContainedChild_OnWindows_IsInAGroupOfItsOwn_ThatTheConsolesInterruptDoesNotReach()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "the console's interruption is Windows'; ASignalToTheParentsGroup_DoesNotReachItsContainedChild_OnLinuxAndMacOS holds Linux and macOS.");

        var runner = ContainingRunner();
        var ordinary = await runner.RunAsync(Child("interrupt-own-console"), TestContext.Current.CancellationToken)
            .WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.SkipUnless(
            ordinary is { ExitCode: 0, StandardOutput: "interrupted\n" },
            $"this process was started with the console's interruption turned off, which its children inherit: '{ordinary.StandardOutput.Trim()}'.");

        var contained = await runner.RunAsync(Child("interrupt-own-console") with { Contained = true }, TestContext.Current.CancellationToken)
            .WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.Equal((0, "not interrupted\n"), (contained.ExitCode, contained.StandardOutput));
    }

    /// <summary>
    /// A child apart from the terminal is this process's own child, on every system: on Windows it
    /// starts as any other, with no step between, and on Linux and macOS the step it starts through
    /// becomes it under the same process id.
    /// </summary>
    [Fact]
    public async Task AChildApartFromTheTerminal_IsStartedByThisProcessItself()
    {
        var result = await ContainingRunner().RunAsync(
            Child("print-parent") with { ApartFromTerminal = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.Equal((0, $"parent={Environment.ProcessId}\n"), (result.ExitCode, result.StandardOutput));
    }

    /// <summary>
    /// The step leaves the console as it found it: on Windows the program it starts shares its
    /// console, and a contained child writes its output in the code page an ordinary child writes
    /// it in, as on every other system.
    /// </summary>
    [Fact]
    public async Task TheStep_LeavesTheConsoleAsItFoundIt()
    {
        var runner = ContainingRunner();
        var ordinary = await runner.RunAsync(Child("print-code-page"), TestContext.Current.CancellationToken)
            .WaitAsync(LongBound, TestContext.Current.CancellationToken);
        var contained = await runner.RunAsync(Child("print-code-page") with { Contained = true }, TestContext.Current.CancellationToken)
            .WaitAsync(LongBound, TestContext.Current.CancellationToken);

        Assert.Equal((0, ordinary.StandardOutput), (contained.ExitCode, contained.StandardOutput));
    }

    /// <summary>
    /// The step itself refuses a command line it cannot run, each refusal said whole on its error
    /// stream: no program after '--' is a usage error, and both levels at once, a job with no name
    /// or no channel to say the start on end with the step's own code. The root help does not list
    /// it - nobody types it.
    /// </summary>
    [Fact]
    public async Task TheStepItself_IsRefused_WhereItsCommandLineCannotBeRun()
    {
        var empty = await CliRunner.RunAsync([ContainExec.CommandName], TestContext.Current.CancellationToken);

        Assert.Equal((HarnessExit.UsageError, $"contain-exec was given no program after '--'.{Environment.NewLine}"), (empty.ExitCode, empty.StandardError));

        var both = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.JobOption, "a-name", ContainExec.GroupOption, ContainExec.StatusOption, "a-channel", ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal((ContainExec.Failed, $"contain-exec was asked for both '--job' and '--group'; it takes one.{Environment.NewLine}"), (both.ExitCode, both.StandardError));

        var unnamed = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.JobOption, string.Empty, ContainExec.StatusOption, "a-channel", ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ContainExec.Failed, $"contain-exec was asked for a job with no name; '--job' takes the name the process runner gave it.{Environment.NewLine}"),
            (unnamed.ExitCode, unnamed.StandardError));

        var unsaid = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.GroupOption, ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ContainExec.Failed, $"contain-exec was given no '--status'; it takes the channel the process runner reads the start from.{Environment.NewLine}"),
            (unsaid.ExitCode, unsaid.StandardError));
        Assert.Equal(string.Empty, unsaid.StandardOutput);

        var named = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.GroupOption, ContainExec.StatusOption, "a-channel", ContainExec.Separator, "dotnet", "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ContainExec.Failed, "contain-exec was given 'dotnet', which is no whole path; the process runner names every program by its whole path."
                + Environment.NewLine),
            (named.ExitCode, named.StandardError));

        var help = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.DoesNotContain(ContainExec.CommandName, help.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// On Windows the step refuses full containment with no job named: that is a job object, which
    /// the process runner makes and names. Linux and macOS refuse the other way round
    /// (<see cref="TheStepOnLinuxAndMacOS_RefusesAJobObject"/>).
    /// </summary>
    [Fact]
    public async Task TheStepOnWindows_RefusesFullContainmentWithNoJob()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "full containment is the job object on Windows alone; TheStepOnLinuxAndMacOS_RefusesAJobObject holds the other systems.");

        var refused = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.StatusOption, "a-channel", ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ContainExec.Failed, "contain-exec was asked for full containment with no '--job': on Windows that is a job object, named by the process runner that made it."
                + Environment.NewLine),
            (refused.ExitCode, refused.StandardError));
    }

    /// <summary>
    /// On Linux and macOS the step refuses a job object, which only Windows has, rather than pass it
    /// over: full containment there is a session of its own, asked for with no option. Windows
    /// refuses the other way round (<see cref="TheStepOnWindows_RefusesFullContainmentWithNoJob"/>).
    /// </summary>
    [Fact]
    public async Task TheStepOnLinuxAndMacOS_RefusesAJobObject()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "a job object is Windows' own; TheStepOnWindows_RefusesFullContainmentWithNoJob holds Windows.");

        var refused = await CliRunner.RunAsync(
            [ContainExec.CommandName, ContainExec.JobOption, "a-name", ContainExec.StatusOption, "a-channel", ContainExec.Separator, TestHost.DotnetExecutable, "--version"],
            TestContext.Current.CancellationToken);

        Assert.Equal(
            (ContainExec.Failed, "contain-exec was asked for the job object 'a-name', which only Windows has: on Linux and macOS full containment is a session of its own, "
                + $"asked for with no option.{Environment.NewLine}"),
            (refused.ExitCode, refused.StandardError));
    }

    /// <summary>
    /// On Windows a contained child whose job object cannot be made is a program that never ran, the system's reason said
    /// as any start's failure is: nothing is started outside the container it was to have. The job is made to fail as the
    /// system fails it, by an object of another kind holding the job's name.
    /// </summary>
    [Fact]
    public async Task AContainedChildWhoseJobCannotBeMade_OnWindows_NeverRuns_SayingWhy()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "a job object is Windows' own; on Linux and macOS the step makes the child's session itself, and AProgramThatCannotStart_StartedThroughTheStep_IsOneThatNeverRan_ForTheSystemsReason holds what fails there.");

        // ERROR_INVALID_HANDLE: what the system answers a job asked for under a name an object of another kind holds.
        const int NameHeldByAnotherKind = 6;

        using var temp = new TempDirectory();
        var written = temp.Combine("written");
        var started = false;
        var runner = new ProcessRunner(
            new HostPlatform(),
            FilePermissionsFactory.Create(),
            CliRunner.CliProgram,
            processTable: null,
            makeJob: name =>
            {
                using var holder = new Mutex(initiallyOwned: false, name);
                return Containment.CreateJob(name);
            });

        var raised = await Assert.ThrowsAsync<ProgramStartException>(() => runner.RunAsync(
            ByItsPath("write-file", written, "ran") with { Contained = true, OnStarted = () => started = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken));

        Assert.Equal(TestHost.AssemblyProgram, raised.FileName);
        Assert.Equal(
            $"'{TestHost.AssemblyProgram}' could not be started: the job object that would contain it could not be made: {new Win32Exception(NameHeldByAnotherKind).Message}",
            raised.Message);
        Assert.Equal(NameHeldByAnotherKind, Assert.IsType<Win32Exception>(raised.InnerException).NativeErrorCode);
        Assert.False(started, "a program whose container could not be made was said to have started");
        Assert.False(File.Exists(written), "a program whose container could not be made ran");
    }

    /// <summary>
    /// The step waits for its channel no longer than its bound: a channel it cannot reach - nobody listens on it - ends
    /// the step with its own code, saying so on its error stream, once the bound is spent. The step's own bound is thirty
    /// seconds; a shorter one stands in for it here.
    /// </summary>
    [Fact]
    public void TheStep_GivesUpAChannelItCannotReach_OnceItsBoundIsSpent()
    {
        var channel = ProcessRunner.StatusName(OperatingSystem.IsWindows());
        var bound = TimeSpan.FromMilliseconds(300);
        using var error = new StringWriter();
        var waited = Stopwatch.StartNew();

        var code = ContainExec.Run(job: null, group: true, channel, CliRunner.CliProgram, ["--version"], error, bound);

        waited.Stop();

        Assert.Equal(
            (ContainExec.Failed, $"contain-exec could not reach the channel '{channel}' it says the start on: The operation has timed out.{Environment.NewLine}"),
            (code, error.ToString()));

        // Waited for as long as the bound, and given up then: never for the step's own thirty seconds. The wait counts the
        // bound by the system's tick, which can end it a few milliseconds before a stopwatch says the bound is spent.
        Assert.InRange(waited.Elapsed, bound - TimeSpan.FromMilliseconds(50), bound + TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// A step that ends without reaching its channel said nothing of the start: the run is a program that never ran,
    /// said with the step's own code and the first line of its error stream - never read as the program's end, and never
    /// said to have started.
    /// </summary>
    [Fact]
    public async Task AStepThatEndsWithoutReachingItsChannel_IsAStartThatFailed_SayingWhatTheStepSaid()
    {
        var started = false;

        // This assembly's program stands in for the step, and the environment the step hands on selects what it does.
        var runner = new ProcessRunner(new HostPlatform(), FilePermissionsFactory.Create(), TestHost.AssemblyProgram);

        var raised = await Assert.ThrowsAsync<ProgramStartException>(() => runner.RunAsync(
            ByItsPath("step-reaching-nothing") with { Contained = true, OnStarted = () => started = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken));

        Assert.Equal(TestHost.AssemblyProgram, raised.FileName);
        Assert.Equal(
            $"'{TestHost.AssemblyProgram}' could not be started: contain-exec ended with {ContainExec.Failed} before it started it: {TestChild.ReachedNothing}",
            raised.Message);
        Assert.Null(raised.InnerException);
        Assert.False(started, "a program whose step reached no channel was said to have started");
    }

    /// <summary>Runs one child behaviour, contained or apart, and reads the ids it printed.</summary>
    private static async Task<(Dictionary<string, int> Ids, ProcessResult Result)> RunChild(string mode, bool contained = false, bool apart = false)
    {
        var result = await ContainingRunner().RunAsync(
            Child(mode) with { Contained = contained, ApartFromTerminal = apart },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

        return (Ids(result.StandardOutput), result);
    }

    /// <summary>The ids a child behaviour printed, each as <c>name=id</c>.</summary>
    private static Dictionary<string, int> Ids(string printed)
        => printed
            .Split((char[]?)[], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Contains('=', StringComparison.Ordinal))
            .Select(token => token.Split('='))
            .ToDictionary(parts => parts[0], parts => int.Parse(parts[1], CultureInfo.InvariantCulture));

    /// <summary>Runs one child behaviour that starts a sleeper out of every tree and exits, and reads the sleeper's id.</summary>
    private static async Task<(ProcessResult Result, LearnedProcess Orphan)> RunWithOrphan(bool contained = false)
    {
        var result = await ContainingRunner().RunAsync(
            Child("spawn-orphan", "300000") with { Contained = contained },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken);

        return (result, LearnedProcess.In(result.StandardOutput.Trim(), "orphan=") ?? LearnedProcess.None);
    }

    /// <summary>
    /// Runs <paramref name="request"/> and stops it - as a caller that gives up does, through the same stop a bound
    /// makes - once a line <paramref name="stopOn"/> picks has arrived: a run held to a short bound instead would have
    /// it cover the starts before that line, which a slow machine spends more than any short bound on.
    /// </summary>
    /// <returns>What the run raised, and how long it took to end once it was stopped.</returns>
    private static async Task<(Exception? Raised, TimeSpan SinceStop)> RunAndStopOn(ProcessRequest request, Func<string, bool> stopOn, ProcessRunner? runner = null)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var since = new Stopwatch();

        var raised = await Record.ExceptionAsync(() => (runner ?? ContainingRunner()).RunAsync(
            request with
            {
                Timeout = LongBound,
                OnOutputLine = line =>
                {
                    request.OnOutputLine?.Invoke(line);

                    if (!since.IsRunning && stopOn(line))
                    {
                        since.Start();
                        _ = stop.CancelAsync();
                    }
                },
            },
            stop.Token).WaitAsync(LongBound, TestContext.Current.CancellationToken));

        return (raised, since.Elapsed);
    }

    /// <summary>
    /// The process runner, starting contained children through the tool the build makes, and reading the machine's own
    /// process table unless <paramref name="table"/> is given in its place.
    /// </summary>
    private static ProcessRunner ContainingRunner(IProcessTable? table = null)
        => table is null
            ? new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram)
            : new(new HostPlatform(), FilePermissionsFactory.Create(), CliRunner.CliProgram, table);
    /// <summary>
    /// Starts <paramref name="program"/> directly and then through the step, contained or apart from the terminal, and
    /// holds the second to the first: a program that never ran either way, for the same reason the system gave, said as
    /// any start says it - never the step's own exit code, which a program could end with too - and never said to have
    /// started.
    /// </summary>
    private static async Task NeverRunsAsADirectStartDoesNot(string program, bool contained)
    {
        var started = false;

        // The number the system refuses this file with, as a start made here directly hears it.
        var direct = await Assert.ThrowsAsync<ProgramStartException>(() => ContainingRunner().RunAsync(
            new ProcessRequest { FileName = program },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken));
        var refused = Assert.IsType<Win32Exception>(direct.InnerException).NativeErrorCode;

        var raised = await Assert.ThrowsAsync<ProgramStartException>(() => ContainingRunner().RunAsync(
            new ProcessRequest { FileName = program, Contained = contained, ApartFromTerminal = !contained, OnStarted = () => started = true },
            TestContext.Current.CancellationToken).WaitAsync(LongBound, TestContext.Current.CancellationToken));

        Assert.Equal(program, raised.FileName);
        Assert.Equal($"'{program}' could not be started: {new Win32Exception(refused).Message}", raised.Message);
        Assert.Equal(refused, Assert.IsType<Win32Exception>(raised.InnerException).NativeErrorCode);
        Assert.False(started, "a program that never ran was said to have started");
    }

    /// <summary>A request that runs this assembly as a child, as the process tests do.</summary>
    private static ProcessRequest Child(string mode, params string[] arguments)
        => TestHost.ChildRequest(mode, arguments);

    /// <summary>
    /// A request that runs this assembly as a child through the program the build makes beside it, named by its whole
    /// path: what a refusal says of the program is then the path the request gave, with no lookup between.
    /// </summary>
    private static ProcessRequest ByItsPath(string mode, params string[] arguments)
        => TestHost.ChildRequest(mode, arguments) with { FileName = TestHost.AssemblyProgram, Arguments = arguments };

    /// <summary>
    /// A file that is there and is no program this system starts: on Windows text under a program's extension, which is
    /// no program for this machine, and elsewhere a script that may not be run.
    /// </summary>
    private static string NotAProgram(TempDirectory temp)
    {
        if (OperatingSystem.IsWindows())
        {
            var text = temp.Combine("not-a-program.exe");
            File.WriteAllText(text, "not a program");
            return text;
        }

        var script = temp.Combine("not-runnable");
        File.WriteAllText(script, "#!/bin/sh\nexit 0\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return script;
    }

    /// <summary>Notes <paramref name="what"/> in <paramref name="heard"/>, which two threads note into.</summary>
    private static void Heard(List<string> heard, string what)
    {
        lock (heard)
        {
            heard.Add(what);
        }
    }

    /// <summary>Whether <paramref name="condition"/> came to hold within the long bound, looking again every fifty milliseconds.</summary>
    private static bool Until(Func<bool> condition)
    {
        for (var waited = TimeSpan.Zero; waited < LongBound; waited += TimeSpan.FromMilliseconds(50))
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(TimeSpan.FromMilliseconds(50));
        }

        return false;
    }

    /// <summary>The terminal's interruption, SIGINT, as Linux and macOS number it.</summary>
    private const int Interrupt = 2;

    /// <summary>Signals a process, or a whole group by its negated id, on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int kill(int process, int signal);

    /// <summary>This process's own group, on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getpgid(int process);

    /// <summary>This process's own session, on Linux and macOS.</summary>
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int getsid(int process);

    /// <summary>A process table that cannot be read, raising what it is given on every reading.</summary>
    private sealed class UnreadableTable(Exception raised) : IProcessTable
    {
        /// <summary>What a table read through a <c>ps</c> that cannot start raises, as the process runner words it.</summary>
        public const string PsNotStarted = "'ps' could not be started: Resource temporarily unavailable";

        /// <summary>A table whose program could not start, as macOS's does in a session that has used up its processes.</summary>
        public static UnreadableTable Raising => new(new ProgramStartException("ps", PsNotStarted));

        /// <summary>What a session - or a group - ended without this table says it did not reach, <paramref name="ended"/> said of its group.</summary>
        public static string Unreached(string ended, bool group = false)
            => $"the process table could not be read to end {(group ? "the process group a child apart from the terminal led" : "the contained child's session")} ({PsNotStarted}): {ended}, "
                + "and what it started outside the group was not reached";

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromException<ProcessTableReading>(raised);
    }

    private static int GetGroupOfThisProcess() => getpgid(0);

    private static int GroupOf(int process) => getpgid(process);

    /// <summary>A process table read once from <paramref name="first"/>, and from <paramref name="then"/> ever after.</summary>
    private sealed class ReadOnce(IProcessTable first, IProcessTable then) : IProcessTable
    {
        private int _reads;

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => (Interlocked.Increment(ref _reads) == 1 ? first : then).ReadAsync(cancellationToken);
    }

    private static int GetSidOfThisProcess() => getsid(0);
}
