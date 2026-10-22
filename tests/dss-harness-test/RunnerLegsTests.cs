using DssHarness.Core.Application.Legs;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// run's use case, in process: every refusal it makes before any leg is placed or any host is measured - a runner
/// nobody declared, a manual step of a runner with no action, a runner or a run check that writes the tree where a leg
/// would work in a copy, and --local-copy with --in-place - each with nothing run and no run kept.
/// </summary>
public sealed class RunnerLegsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A runner named blank is a runner nobody declared: a usage error naming what predefinedRunners declares, as any
    /// other name nobody declared is - never a defect of the tool.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("nobody")]
    public async Task ARunnerNobodyDeclared_IsAUsageError_NamingTheRunnersDeclared(string runner)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, Config(harness));

        var refused = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = runner });

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal($"'{runner}' is not declared under predefinedRunners. Declared: format, probe.", refused.Message);
        NothingWasRun(temp);
    }

    /// <summary>A manual step of a runner that runs phases of its own names nothing it could run: a usage error.</summary>
    [Fact]
    public async Task AManualStepOfARunnerWithPhases_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, Config(harness));

        var refused = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "probe", ManualSteps = ["look"] });

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            $"Runner 'probe' runs phases of its own, and only an action's steps can be manual, so {StepSelection.Option} names nothing it could run.",
            refused.Message);
        NothingWasRun(temp);
    }

    /// <summary>
    /// A runner that writes the tree is refused for --local-copy, a usage error, and for a leg another machine would run,
    /// refused: the copy there is what the next sync makes the tree again.
    /// </summary>
    [Fact]
    public async Task ARunnerThatWritesTheTree_IsRefusedForLocalCopy_AndForALegAnotherMachineWouldRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, Config(harness));

        var askedForACopy = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "format", LegNames = ["native"], LocalCopy = true });
        var onAHost = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "format", LegNames = ["there"] });

        Assert.Equal(HarnessExit.UsageError, askedForACopy.ExitCode);
        Assert.Equal(
            "Runner 'format' writes the tree (writesTree), which --local-copy would have it write in a copy. Leave --local-copy out.",
            askedForACopy.Message);
        Assert.Equal(HarnessExit.Refused, onAHost.ExitCode);
        Assert.Equal(
            "Runner 'format' writes the tree (writesTree), and leg 'there' would work in a copy of it on ssh vps, which the next sync "
                + "makes the tree again. Nothing was run. Run it on a leg this machine runs itself.",
            onAHost.Message);
        NothingWasRun(temp);
    }

    /// <summary>
    /// A run check's runner that writes the tree is refused wherever the leg it checks would work in a copy: in a copy of
    /// its own, as its localCopy key says; on a host; and with --local-copy, a usage error.
    /// </summary>
    [Fact]
    public async Task ARunCheckWhoseRunnerWritesTheTree_IsRefusedWhereverItsLegWouldWorkInACopy()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, Config(harness, checkedByFormat: true));

        var inACopy = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "probe", LegNames = ["native"] });
        var onAHost = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "probe", LegNames = ["there"] });
        var askedForACopy = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "probe", LegNames = ["there"], LocalCopy = true });

        Assert.Equal(HarnessExit.Refused, inACopy.ExitCode);
        Assert.Equal(
            "Run check 'format' of runner 'probe' writes the tree (writesTree), and leg 'native' would work in a copy of it, as its own "
                + "localCopy key or the repository's default says. Nothing was run. Run it with --in-place.",
            inACopy.Message);
        Assert.Equal(HarnessExit.Refused, onAHost.ExitCode);
        Assert.Equal(
            "Run check 'format' of runner 'probe' writes the tree (writesTree), and leg 'there' would work in a copy of it on ssh vps, "
                + "which the next sync makes the tree again. Nothing was run. Run it on a leg this machine runs itself.",
            onAHost.Message);
        Assert.Equal(HarnessExit.UsageError, askedForACopy.ExitCode);
        Assert.Equal(
            "Run check 'format' of runner 'probe' writes the tree (writesTree), which --local-copy would have it write in a copy. "
                + "Leave --local-copy out.",
            askedForACopy.Message);
        NothingWasRun(temp);
    }

    /// <summary>
    /// --local-copy with --in-place is a usage error for a run too, made once the runner it names is read - whether that
    /// runner writes the tree is part of where its legs work - and so after its configuration, which an empty directory
    /// refuses first.
    /// </summary>
    [Fact]
    public async Task LocalCopyAndInPlaceTogether_AreAUsageError_OnceTheRunnerIsRead()
    {
        using var temp = new TempDirectory();
        using var empty = new TempDirectory();
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, Token, Config(harness));

        var refused = await RefusedAsync(harness, new RunnerLegsRequest { Directory = temp.Path, Runner = "probe", LegNames = ["native"], LocalCopy = true, InPlace = true });
        var unread = await RefusedAsync(harness, new RunnerLegsRequest { Directory = empty.Path, Runner = "probe", LocalCopy = true, InPlace = true });

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal("--local-copy and --in-place say opposite things about where this run's own legs work. Give one.", refused.Message);
        LegUseCases.IsTheConfigurationsRead(unread, empty);
        NothingWasRun(temp);
    }

    /// <summary>
    /// A repository with a leg this machine runs in a copy of its own, a leg pinned to a host, a runner that writes the
    /// tree and one that does not - which a run check of the writing one checks, where asked.
    /// </summary>
    private static HarnessConfig Config(HarnessFactory harness, bool checkedByFormat = false)
    {
        var platform = harness.Platform;

        return new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            SshItems = { "vps" },
            Hosts = new HostsConfig { Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/home/dev/repo" } } },
            Legs =
            {
                ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", LocalCopy = true },
                ["there"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Ssh = "vps" },
            },
            PredefinedRunners =
            {
                ["format"] = new RunnerConfig
                {
                    WritesTree = true,
                    Phases = [new RunnerPhase { Name = "tidy", Command = ["dotnet", "--version"] }],
                },
                ["probe"] = new RunnerConfig
                {
                    Phases = [new RunnerPhase { Name = "look", Command = ["dotnet", "--version"] }],
                    ExpectedExceptions = checkedByFormat
                        ?
                        [
                            new ExpectedException
                            {
                                ExceptionType = "Flake",
                                Messages = ["it flaked"],
                                Message = "excused: the device was busy",
                                EarnedOn = "2026-10-13",
                                EarnedAt = "local",
                                Mechanism = "the device is shared",
                                Anchor = "D-AREA-TOPIC-ONE",
                                RunChecks = [new RunCheck { PredefinedRunner = "format" }],
                            },
                        ]
                        : [],
                },
            },
        };
    }

    /// <summary>
    /// A run of a runner writes the tree where the runner says writesTree, or a runner its run checks name does - each
    /// found whatever case it is named in - and not otherwise, nor for a runner nobody declared, which is refused where it
    /// is run: the one rule a run step's claim and its place read.
    /// </summary>
    [Fact]
    public void ARunWritesTheTree_WhereItsRunnerOrARunnerItsRunChecksNameSaysSo()
    {
        var harness = new HarnessFactory();
        var checkedByFormat = Config(harness, checkedByFormat: true);
        var plain = Config(harness);

        Assert.Equal([("format", false)], plain.WritingRunners("format"));
        Assert.Equal([("FORMAT", false)], plain.WritingRunners("FORMAT"));
        Assert.Equal([("format", true)], checkedByFormat.WritingRunners("probe"));
        Assert.Empty(plain.WritingRunners("probe"));
        Assert.Empty(plain.WritingRunners("nobody"));
        Assert.True(new ConfiguredRunnerFacts(checkedByFormat).WritesTree("Probe"));
        Assert.False(new ConfiguredRunnerFacts(plain).WritesTree("probe"));
    }

    private static async Task<HarnessException> RefusedAsync(HarnessFactory harness, RunnerLegsRequest request)
    {
        await using var services = LegUseCases.Of(harness, RunnerRunService.CommandName);

        return await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IRunnerLegs>().RunAsync(request, RunId.New(), Token));
    }

    /// <summary>That no run began in <paramref name="temp"/>: no directory of records was made for one.</summary>
    private static void NothingWasRun(TempDirectory temp)
    {
        var runs = Path.Combine(temp.Path, HarnessLayout.DirectoryName, "runs");

        Assert.Empty(Directory.Exists(runs) ? Directory.EnumerateDirectories(runs) : []);
    }
}
