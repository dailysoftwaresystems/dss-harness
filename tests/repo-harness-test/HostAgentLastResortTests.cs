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
    /// which held the same id, as Windows goes on naming a parent that has gone.
    /// </summary>
    [Fact]
    public async Task TheLastResort_EndsWhatTheProcessStarted_AndNothingItDidNot()
    {
        const int Parent = 424242;
        var parentStarted = DateTimeOffset.UtcNow.AddMinutes(-1);

        using var started = Sleeping();
        using var earlier = Sleeping();
        using var anothers = Sleeping();

        try
        {
            var table = new ListedProcesses(
                new SampledProcess(started.Id, Parent, "child", parentStarted.AddSeconds(5), null),
                new SampledProcess(earlier.Id, Parent, "child", parentStarted.AddSeconds(-5), null),
                new SampledProcess(anothers.Id, Parent + 1, "child", parentStarted.AddSeconds(5), null),
                new SampledProcess(Parent, 1, "agent", parentStarted, null),

                // One that ended since it was listed is passed over, and the rest still go.
                new SampledProcess(int.MaxValue - 7, Parent, "gone", parentStarted.AddSeconds(5), null));

            var ended = await HostAgentLastResort.EndStartedByAsync(table, Parent, parentStarted, Token);

            Assert.Equal([started.Id], ended);
            Assert.True(started.WaitForExit(TimeSpan.FromSeconds(30)), "The process it started was left running.");
            Assert.False(earlier.HasExited, "A process some earlier holder of the id started was ended.");
            Assert.False(anothers.HasExited, "A process another started was ended.");
        }
        finally
        {
            foreach (var process in new[] { started, earlier, anothers })
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
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
        public Task<ProcessTableReading> ReadAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ProcessTableReading(processes, null));
    }
}
