using System.Diagnostics;
using System.Runtime.InteropServices;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>What a host's agent does when what it started will not stop, and how it takes a hang-up.</summary>
public sealed class HostAgentLastResortTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// The agent's last resort ends every process the table lists as started by the one named, and no other: not one
    /// another process started, and not one listed as its child that started before it did - some earlier process's,
    /// which held the same id, as Windows goes on naming a parent that has gone. Both starts are read from the one
    /// table, whatever its clock counts from: a time of day, or - as on Linux - the seconds since the machine started,
    /// which held against this process's own start as the runtime tells it read every child as older than its parent.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheLastResort_EndsWhatTheProcessStarted_AndNothingItDidNot_HoweverTheTableDatesThem(bool sinceTheMachineStarted)
    {
        const int Parent = 424242;
        var parentStarted = sinceTheMachineStarted ? DateTimeOffset.UnixEpoch.AddSeconds(86_400) : DateTimeOffset.UtcNow.AddMinutes(-1);

        using var started = Sleeping();
        using var earlier = Sleeping();
        using var anothers = Sleeping();
        using var undated = Sleeping();

        try
        {
            var table = new ListedProcesses(
                new SampledProcess(started.Id, Parent, "child", parentStarted.AddSeconds(5), null),
                new SampledProcess(earlier.Id, Parent, "child", parentStarted.AddSeconds(-5), null),
                new SampledProcess(anothers.Id, Parent + 1, "child", parentStarted.AddSeconds(5), null),
                new SampledProcess(Parent, 1, "agent", parentStarted, null),

                // One the table could not date is the agent's all the same: only a start known to be earlier clears it.
                new SampledProcess(undated.Id, Parent, "child", null, null),

                // One that ended since it was listed is passed over, and the rest still go.
                new SampledProcess(int.MaxValue - 7, Parent, "gone", parentStarted.AddSeconds(5), null));

            var outcome = await HostAgentLastResort.EndStartedByAsync(table, HostDoubles.Platform(PlatformId.Linux, "x64"), Parent, Token);

            Assert.Equal(new[] { started.Id, undated.Id }.Order(), outcome.Ended);
            Assert.Empty(outcome.Left);
            Assert.Null(outcome.Unlisted);
            Assert.True(started.WaitForExit(TimeSpan.FromSeconds(30)), "The process it started was left running.");
            Assert.True(undated.WaitForExit(TimeSpan.FromSeconds(30)), "The process it started, which the table could not date, was left running.");
            Assert.False(earlier.HasExited, "A process some earlier holder of the id started was ended.");
            Assert.False(anothers.HasExited, "A process another started was ended.");
        }
        finally
        {
            foreach (var process in new[] { started, earlier, anothers, undated })
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }

    /// <summary>
    /// A table that could not be read whole names no parents: nothing is ended, and why is said rather than passed over
    /// as a process that had started nothing.
    /// </summary>
    [Fact]
    public async Task ATableThatCouldNotBeReadWhole_EndsNothing_AndSaysWhy()
    {
        using var mine = Sleeping();

        try
        {
            var table = new ListedProcesses(new SampledProcess(mine.Id, null, "child", null, null)) { Degraded = "the query was refused." };

            var outcome = await HostAgentLastResort.EndStartedByAsync(table, HostDoubles.Platform(PlatformId.Linux, "x64"), Environment.ProcessId, Token);

            Assert.Equal("what it started could not be listed - the query was refused - so none of it was ended", outcome.Said);
            Assert.Empty(outcome.Ended);
            Assert.False(mine.HasExited);
        }
        finally
        {
            mine.Kill(entireProcessTree: true);
        }
    }

    /// <summary>
    /// The console a process runs in is none of what it started. Windows lists that - a conhost - as the child of a
    /// process started with a console of its own, and ends what runs in a console once it has ended: so it is left
    /// running, and not counted, where everything else the process started goes. Anywhere else a program of that name is
    /// a program like any other.
    /// </summary>
    [Theory]
    [InlineData(PlatformId.Windows, false)]
    [InlineData(PlatformId.Linux, true)]
    [InlineData(PlatformId.MacOs, true)]
    public async Task TheConsoleWindowsRunsAProcessIn_IsNoneOfWhatItStarted_AndIsLeftRunning(PlatformId machine, bool ended)
    {
        const int Parent = 424242;
        var parentStarted = DateTimeOffset.UtcNow.AddMinutes(-1);

        using var console = Sleeping();
        using var started = Sleeping();

        try
        {
            var table = new ListedProcesses(
                new SampledProcess(Parent, 1, "agent", parentStarted, null),
                new SampledProcess(console.Id, Parent, "conhost", parentStarted.AddMilliseconds(20), @"\??\C:\WINDOWS\system32\conhost.exe 0x4"),
                new SampledProcess(started.Id, Parent, "child", parentStarted.AddSeconds(5), null));

            var outcome = await HostAgentLastResort.EndStartedByAsync(table, HostDoubles.Platform(machine, "x64"), Parent, Token);

            Assert.Equal(ended ? new[] { console.Id, started.Id }.Order() : [started.Id], outcome.Ended);
            Assert.True(started.WaitForExit(TimeSpan.FromSeconds(30)), "The process it started was left running.");
            Assert.Equal(ended, console.WaitForExit(ended ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(500)));
        }
        finally
        {
            foreach (var process in new[] { console, started })
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
        }
    }

    /// <summary>
    /// A process the table lists as started by this one that has ended since is passed over, and not counted among what
    /// was ended: the program a table is read through is one, on Windows, in every reading.
    /// </summary>
    [Fact]
    public async Task AProcessThatHadAlreadyEnded_IsNotCountedAmongWhatWasEnded()
    {
        const int Parent = 424242;

        using var over = Sleeping();

        over.Kill(entireProcessTree: true);
        Assert.True(over.WaitForExit(TimeSpan.FromSeconds(30)));

        // Held open by this test, so the system still knows the id: what a reader not yet let go of leaves behind.
        var table = new ListedProcesses(
            new SampledProcess(Parent, 1, "agent", null, null),
            new SampledProcess(over.Id, Parent, "reader", null, null));

        var outcome = await HostAgentLastResort.EndStartedByAsync(table, HostDoubles.Platform(PlatformId.Linux, "x64"), Parent, Token);

        Assert.Equal("nothing it started was still running", outcome.Said);
    }

    /// <summary>What the last resort came to is said as what it did: each process ended by its id, each it could not end, or that it found none.</summary>
    [Fact]
    public void WhatTheLastResortCameTo_IsSaidAsWhatItDid()
    {
        Assert.Equal("nothing it started was still running", new LastResortOutcome([], [], null).Said);
        Assert.Equal("1 process it started was ended, with what that started (pid 7)", new LastResortOutcome([7], [], null).Said);
        Assert.Equal(
            "2 processes it started were ended, each with what it started (pid 7, pid 9); 1 it started could not be ended (pid 8)",
            new LastResortOutcome([7, 9], [8], null).Said);
        Assert.Equal("1 it started could not be ended (pid 8)", new LastResortOutcome([], [8], null).Said);
    }

    /// <summary>
    /// The last resort itself, on this machine's own process table, as a host's agent uses it: a process that started
    /// another ends that one, says so, and ends itself with the code it was given. Whatever a machine's table dates its
    /// processes by, and however it names their parents, is this machine's here - which is what no scripted table shows.
    /// </summary>
    [Fact]
    public async Task TheLastResort_OnThisMachinesOwnTable_EndsARealChildOfTheProcessThatAsks_ThenThatProcess()
    {
        var result = await new HarnessFactory().ProcessRunner.RunAsync(
            TestHost.ChildRequest("last-resort", "7") with { Timeout = TimeSpan.FromMinutes(8) },
            Token);

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        Assert.False(result.TimedOut, "The process that asked never ended.");
        Assert.Equal(7, result.ExitCode);
        Assert.Equal(2, lines.Length);

        var child = int.Parse(lines[0], System.Globalization.CultureInfo.InvariantCulture);

        Assert.Equal($"1 process it started was ended, with what that started (pid {child})", lines[1]);
        Assert.True(Ended(child), $"The process it started, pid {child}, was left running.");
    }

    /// <summary>Whether process <paramref name="id"/> has ended, waited for as long as an ending takes.</summary>
    private static bool Ended(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);

            return process.WaitForExit(TimeSpan.FromSeconds(30));
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    /// <summary>
    /// A hang-up of the session cancels what it is told to and does not end the process: what the agent started is then
    /// stopped by the cancellation, where ending at once would leave it running under nobody.
    /// </summary>
    [Fact]
    public async Task AHangUp_CancelsWhatItIsToldTo_AndDoesNotEndTheProcess()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows hangs a process up by closing its console, which a test cannot do to itself.");

        using var source = new CancellationTokenSource();
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = source.Token.Register(() => cancelled.TrySetResult());

        using (HangUp.Cancels(source))
        {
            using var kill = Process.Start(new ProcessStartInfo("kill") { ArgumentList = { "-HUP", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) } })!;
            await kill.WaitForExitAsync(Token);

            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(30), Token);
        }

        Assert.True(source.IsCancellationRequested);
    }

    /// <summary>A process of this test's own that sleeps for a minute, unless ended first.</summary>
    private static Process Sleeping()
    {
        var request = TestHost.ChildRequest("sleep", "60000");
        var start = new ProcessStartInfo(request.FileName) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };

        foreach (var argument in request.Arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in request.Environment)
        {
            start.Environment[name] = value;
        }

        return Process.Start(start)!;
    }

    /// <summary>A process table that lists what a test says it does.</summary>
    private sealed class ListedProcesses(params SampledProcess[] processes) : IProcessTable
    {
        /// <summary>Why the reading is less than it should be, where a test says it is.</summary>
        public string? Degraded { get; init; }

        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading(processes, Degraded));
    }
}
