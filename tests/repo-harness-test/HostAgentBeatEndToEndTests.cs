using System.Diagnostics;
using System.Globalization;
using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A leg on a host and the machine that dispatched it, end to end: this build's own agent, started as a host starts it,
/// running a step that sleeps, with its input held open as ssh or wsl.exe holds it.
/// </summary>
public sealed class HostAgentBeatEndToEndTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";

    /// <summary>How long the step sleeps: far longer than either test takes, so only its being stopped ends it early.</summary>
    private const int NapSeconds = 90;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A host whose asker stops writing its beat, with the input still open - what a dispatcher killed where nothing
    /// closes its end leaves behind - stops the leg it was running within the beats it lets pass, says why, and ends:
    /// the step is no longer running, and the host is free.
    /// </summary>
    [Fact]
    public async Task AHostWhoseAskerFallsSilent_StopsTheLegItWasRunning_ThoughItsInputStaysOpen()
    {
        using var temp = new TempDirectory();
        await NappingRepositoryAsync(temp);

        var result = await new HarnessFactory().ProcessRunner.RunAsync(
            Agent(temp, beatSeconds: 1) with { StandardInputBeat = null },
            Token);

        Assert.False(result.TimedOut, "The host went on running its leg with nobody writing a beat.");
        Assert.InRange(result.Duration, TimeSpan.FromSeconds(HostAgentProtocol.BeatsMissed), TimeSpan.FromSeconds(NapSeconds - 30));
        Assert.Contains(
            $"host-agent: FAIL - the machine that asked has gone - it wrote nothing on the input it holds open here for {HostAgentProtocol.BeatsMissed} s, "
            + "where it writes a beat while it is there - so what it started here is stopped",
            result.StandardError,
            StringComparison.Ordinal);
        Assert.NotEqual(HarnessExit.Success, result.ExitCode);
        Assert.True(HostAgentProtocol.TryReadCompletionLine(result.StandardError.TrimEnd().Split('\n')[^1].TrimEnd('\r'), Nonce, out var finished));
        Assert.Equal(result.ExitCode, finished);
    }

    /// <summary>
    /// The same host, whose asker goes on writing its beat, runs its leg for longer than the silence that would have
    /// stopped it, and is stopped only when the input ends - as a dispatcher stopped here ends it.
    /// </summary>
    [Fact]
    public async Task AHostWhoseAskerKeepsBeating_GoesOnRunningItsLeg_UntilTheInputEnds()
    {
        using var temp = new TempDirectory();
        await NappingRepositoryAsync(temp);

        // Half as long again as eight beats unheard, then this end stops, which ends the input.
        var patience = TimeSpan.FromSeconds(HostAgentProtocol.BeatsMissed * 1.5);

        var result = await new HarnessFactory().ProcessRunner.RunAsync(Agent(temp, beatSeconds: 1) with { Timeout = patience }, Token);

        Assert.True(result.TimedOut, $"The host stopped its leg, though its asker was beating: {result.StandardError}");
        Assert.DoesNotContain("has gone", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same host, its session hung up while its leg runs - its input still open, and no beat asked of its asker, so
    /// that nothing else says that machine has gone: on Linux and macOS it stops the leg as a cancellation does and says
    /// how the request finished, where a process that took the hang-up as its own end left the step running under
    /// nobody.
    /// </summary>
    [Fact]
    public async Task AHostWhoseSessionIsHungUp_StopsTheLegItWasRunning_AndSaysHowItFinished()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows ends a process whose console closes, whatever it answers.");

        using var temp = new TempDirectory();
        await NappingRepositoryAsync(temp);

        var asked = Asked(temp, beatSeconds: 0);
        var start = new ProcessStartInfo(TestHost.DotnetExecutable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (var argument in new[] { "exec", CliRunner.CliAssemblyPath, HostAgentProtocol.CommandName })
        {
            start.ArgumentList.Add(argument);
        }

        using var agent = Process.Start(start) ?? throw new InvalidOperationException("The agent did not start.");

        try
        {
            var serving = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var said = new StringBuilder();

            var reading = Task.Run(
                async () =>
                {
                    while (await agent.StandardError.ReadLineAsync(Token) is { } line)
                    {
                        lock (said)
                        {
                            said.AppendLine(line);
                        }

                        if (HostAgentProtocol.IsStartedLine(line, Nonce))
                        {
                            serving.TrySetResult();
                        }
                    }
                },
                Token);
            var written = agent.StandardOutput.ReadToEndAsync(Token);

            HostAgentProtocol.Input(asked).WriteTo(agent.StandardInput.BaseStream);
            await agent.StandardInput.BaseStream.FlushAsync(Token);
            await serving.Task.WaitAsync(TimeSpan.FromMinutes(2), Token);

            // Long enough for the leg to have started its step.
            await Task.Delay(TimeSpan.FromSeconds(5), Token);

            using (var hangUp = Process.Start("kill", ["-HUP", agent.Id.ToString(CultureInfo.InvariantCulture)]))
            {
                await hangUp.WaitForExitAsync(Token);
            }

            await agent.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(NapSeconds - 30), Token);
            await reading.WaitAsync(TimeSpan.FromSeconds(30), Token);
            await written.WaitAsync(TimeSpan.FromSeconds(30), Token);

            string all;

            lock (said)
            {
                all = said.ToString();
            }

            Assert.NotEqual(HarnessExit.Success, agent.ExitCode);
            Assert.True(HostAgentProtocol.TryReadCompletionLine(all.TrimEnd().Split('\n')[^1].TrimEnd('\r'), Nonce, out var finished), all);
            Assert.Equal(agent.ExitCode, finished);
        }
        finally
        {
            if (!agent.HasExited)
            {
                agent.Kill(entireProcessTree: true);
            }
        }
    }

    /// <summary>The request to run the napping step in <paramref name="temp"/>, its asker writing a beat every <paramref name="beatSeconds"/> seconds; none where zero.</summary>
    private static HostAgentRequest Asked(TempDirectory temp, int beatSeconds)
        => new()
        {
            Kind = HostAgentRequestKind.Run,
            Directory = temp.Path,
            Arguments = ["run", "nap", "--legs", "native"],
            Nonce = Nonce,
            BeatSeconds = beatSeconds,
        };

    /// <summary>This build's agent, asked to run the napping step in <paramref name="temp"/>, its input held open and its beat written.</summary>
    private static ProcessRequest Agent(TempDirectory temp, int beatSeconds)
    {
        var request = Asked(temp, beatSeconds);

        return new ProcessRequest
        {
            FileName = TestHost.DotnetExecutable,
            Arguments = ["exec", CliRunner.CliAssemblyPath, HostAgentProtocol.CommandName],
            StandardInput = HostAgentProtocol.Input(request),
            HoldStandardInputOpen = true,
            StandardInputBeat = HostAgentProtocol.BeatOf(request),
            Environment = new Dictionary<string, string?>(StringComparer.Ordinal),
            Timeout = TimeSpan.FromSeconds(NapSeconds + 60),
        };
    }

    /// <summary>A repository whose runner 'nap' runs one step that sleeps for <see cref="NapSeconds"/> seconds.</summary>
    private static async Task NappingRepositoryAsync(TempDirectory temp)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var (tool, nap) = OperatingSystem.IsWindows()
            ? ("ping", $"ping -n {NapSeconds + 1} 127.0.0.1")
            : ("sleep", $"sleep {NapSeconds}");

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = tool } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" } },
            PredefinedRunners = { ["nap"] = new RunnerConfig { Action = "nap/nap.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "nap", "nap.yml"),
            $"""
            name: nap
            steps:
              - name: nap
                run: {nap}
            """);
    }
}
