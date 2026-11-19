using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Waiting;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// A program the harness asks a question of is waited for to its own end - told every five minutes it lasts, ended only
/// by the caller's token - and never cut off at a number of the code's own: a vcvarsall.bat that takes four minutes on a
/// cold disk, a ctest listing forty thousand tests, or an install of two hours is waited for.
/// </summary>
public sealed class ProgramWaitTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A wait of any length is waited to its end, told each time five minutes more of it have passed.</summary>
    [Fact]
    public async Task AProgram_IsWaitedForToItsEnd_ToldEveryFiveMinutes()
    {
        var clock = new TimerClock();
        var told = new List<string>();
        var ends = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var waited = ProgramWait.ToItsEndAsync(_ => ends.Task, "vcvarsall.bat to end", line => told.Add(line.Line()), clock, Token);

        for (var five = 0; five < 3; five++)
        {
            await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
            clock.Advance(Waits.SaidEvery);
        }

        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, Token);
        Assert.False(waited.IsCompleted);

        ends.SetResult(7);

        Assert.Equal(7, await waited.WaitAsync(ClockDrive.Within, Token));
        Assert.Equal(
            ["has waited 5m00s for vcvarsall.bat to end", "has waited 10m00s for vcvarsall.bat to end", "has waited 15m00s for vcvarsall.bat to end"],
            told);
    }

    /// <summary>The caller's token ends the wait, and the program it cancels with it.</summary>
    [Fact]
    public async Task TheCallersToken_EndsTheWait_AndTheProgram()
    {
        using var stop = new CancellationTokenSource();
        var cancelled = false;

        var waited = ProgramWait.ToItsEndAsync(
            async token =>
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return 0;
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    throw;
                }
            },
            "ctest to describe the tests",
            _ => { },
            new TimerClock(),
            stop.Token);

        await stop.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waited.WaitAsync(ClockDrive.Within, Token));
        Assert.True(cancelled);
    }

    /// <summary>
    /// A host's hold is waited for to its end, and told while it lasts: held to a minute of the code's own, a host on a
    /// link that crawls was said to have refused the hold.
    /// </summary>
    [Fact]
    public async Task AFourMinuteHold_IsWaitedAndTold()
    {
        var clock = new TimerClock();
        var output = Substitute.For<IHarnessOutput>();
        var hosts = Substitute.For<IHostCommandRunner>();
        var ends = new TaskCompletionSource<ProcessResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<HostCommand>();

        hosts.RunAsync(Arg.Any<HostConnection>(), Arg.Do<HostCommand>(commands.Add), Arg.Any<CancellationToken>()).Returns(_ => ends.Task);

        var ended = new HoldAwakeRegistry(hosts, output, clock: clock)
            .EndAsync(new HostConnection { Host = HostId.Wsl("wsl-a"), Distribution = "Example-Linux" }, ".dotnet/tools/dssharness", Token);

        var answer = await FourMinutes.WaitedAndToldAsync(clock, ended, () => ends.SetResult(HostResults.Ok(string.Empty)), Token);

        Assert.False(answer);
        Assert.Null(Assert.Single(commands).Timeout);
        output.Received().Info(ToolPackage.Command, Arg.Is<string>(line => line.StartsWith("has waited 5m00s for wsl wsl-a to answer the hold", StringComparison.Ordinal)));
    }
}

/// <summary>
/// Drives a wait through four minutes - past the bound any probe was once given - and a fifth, at which it is told, before
/// the program it waits on is let end.
/// </summary>
internal static class FourMinutes
{
    /// <summary>
    /// <paramref name="work"/>'s answer, once it has been waited on four minutes without ending, told at the fifth, and
    /// <paramref name="release"/> has let its program end.
    /// </summary>
    /// <typeparam name="T">What the work answers.</typeparam>
    /// <param name="clock">The clock the wait is measured on.</param>
    /// <param name="work">What waits on the program.</param>
    /// <param name="release">Lets the program end.</param>
    /// <param name="cancellationToken">The test's own token.</param>
    public static async Task<T> WaitedAndToldAsync<T>(TimerClock clock, Task<T> work, Action release, CancellationToken cancellationToken)
    {
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, cancellationToken);
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.False(work.IsCompleted, "the program was not waited for past four minutes");

        clock.Advance(TimeSpan.FromMinutes(1));

        // Told, and waiting again.
        await clock.WaitingAsync().WaitAsync(ClockDrive.Within, cancellationToken);
        Assert.False(work.IsCompleted, "the program was not waited for past five minutes");

        release();

        return await work.WaitAsync(ClockDrive.Within, cancellationToken);
    }
}
