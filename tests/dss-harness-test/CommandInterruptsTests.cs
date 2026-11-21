using System.CommandLine;
using System.Runtime.InteropServices;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// How a command typed at a terminal takes its interrupts: the first cancels it and it ends once its work has stopped,
/// however long that takes - no clock of the command line's own cuts it off - and the second is the system's hard stop.
/// Each case runs in a process of its own, which interrupts itself: Ctrl+C on its own console on Windows, SIGINT to itself
/// on Linux and macOS.
/// </summary>
public sealed class CommandInterruptsTests
{
    /// <summary>The longest a case may take: well past the few seconds each takes.</summary>
    private static readonly TimeSpan Bound = TimeSpan.FromMinutes(2);

    /// <summary>The terminal's interruption, SIGINT, as Linux and macOS number it.</summary>
    private const int Interrupt = 2;

    /// <summary>The command each case runs.</summary>
    private const string CommandName = "slow";

    /// <summary>
    /// A command interrupted once goes on until its work has stopped - here three seconds after the interrupt, past the two
    /// the command line once gave before it ended the process - says the interrupt, and answers as it ends; interrupted
    /// twice, the second is the hard stop, and it ends where it stands, its work never finished.
    /// </summary>
    [Fact]
    public async Task ACommandFinishesAfterCtrlC_TheSecondIsTheHardStop()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var once = await ChildRun.MethodAsync(typeof(CommandInterruptsTests), nameof(InterruptedOnceAsync), cancellationToken).WaitAsync(Bound, cancellationToken);

        Assert.SkipWhen(
            once.Output.Contains("not interrupted", StringComparison.Ordinal),
            "this process was started with the console's interruption turned off, which its children inherit, so no child can interrupt itself.");
        Assert.Equal(new ChildRunAnswer(0, $"{HarnessExit.Cancelled} finished\n", $"{CommandName}: WARN - {CommandInterrupts.StoppingLine}\n"), once);

        var twice = await ChildRun.MethodAsync(typeof(CommandInterruptsTests), nameof(InterruptedTwiceAsync), cancellationToken).WaitAsync(Bound, cancellationToken);

        Assert.NotEqual(0, twice.Exit);
        Assert.Equal(string.Empty, twice.Output);
        Assert.Equal($"{CommandName}: WARN - {CommandInterrupts.StoppingLine}\n", twice.Error);
    }

    /// <summary>The child's side of the first case: interrupted once, its work goes on for three seconds past it.</summary>
    internal static Task<string> InterruptedOnceAsync()
        => InvokedAsync(async cancellationToken =>
        {
            if (!await InterruptedAsync(cancellationToken))
            {
                return "not interrupted";
            }

            await Task.Delay(TimeSpan.FromSeconds(3), CancellationToken.None);
            return "finished";
        });

    /// <summary>The child's side of the second case: interrupted, and interrupted again while its work goes on.</summary>
    internal static Task<string> InterruptedTwiceAsync()
        => InvokedAsync(async cancellationToken =>
        {
            if (!await InterruptedAsync(cancellationToken))
            {
                return "not interrupted";
            }

            InterruptThisProcess();
            await Task.Delay(Bound, CancellationToken.None);
            return "finished";
        });

    /// <summary>
    /// Runs <paramref name="work"/> as a command typed at the terminal, through the command line's one choice of how its
    /// interrupts are taken, and answers its exit code and what the work said.
    /// </summary>
    private static async Task<string> InvokedAsync(Func<CancellationToken, Task<string>> work)
    {
        string? said = null;
        var command = new Command(CommandName);

        command.SetAction(async (_, cancellationToken) =>
        {
            said = await work(cancellationToken);
            return HarnessExit.Cancelled;
        });

        var root = new RootCommand("a command that is slow to end");
        root.Subcommands.Add(command);

        var exit = await CommandInterrupts.InvokeAsync(root.Parse([CommandName]), [CommandName], atTheTerminal: true, Console.Error, CancellationToken.None);

        return $"{exit} {said}\n";
    }

    /// <summary>Interrupts this process and waits for the command's token to say so; false where it never does.</summary>
    private static async Task<bool> InterruptedAsync(CancellationToken cancellationToken)
    {
        InterruptThisProcess();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    /// <summary>
    /// Sends this process the terminal's interruption: Ctrl+C to every process of its console on Windows - its own, which
    /// the runner gives each child - and SIGINT to itself on Linux and macOS.
    /// </summary>
    private static void InterruptThisProcess()
    {
        var sent = OperatingSystem.IsWindows() ? GenerateConsoleCtrlEvent(0, 0) : kill(Environment.ProcessId, Interrupt) == 0;

        if (!sent)
        {
            throw new InvalidOperationException($"the interruption was not sent: {Marshal.GetLastPInvokeError()}");
        }
    }

    /// <summary>Signals a process on Linux and macOS.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int process, int signal);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GenerateConsoleCtrlEvent(uint signal, uint group);
}
