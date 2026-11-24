using DssHarness.Core.Application;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// run's use case, in process: every refusal it makes before any leg is placed or any host is measured - a runner
/// nobody declared, a manual step of a runner with no action, a runner or a run check that writes the tree where a leg
/// would work in a copy, --local-copy with --in-place, and an input with no value or one it refuses - each with nothing
/// run and no run kept; and the run checks a leg's run gives its checks' runners, to any depth.
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
    /// A value its input refuses - 43 sources where the action takes at most 32, the consumer's case - is refused before
    /// any leg is placed: no host measured, no run kept, and the heavy-leg slot this machine's only one is held by
    /// another run never waited for, as a usage error naming the input, where its value came from and why.
    /// </summary>
    [Fact]
    public async Task AValueItsInputRefuses_IsRefusedBeforeAnyLegIsPlaced_NeverWaitingForTheSlotAnotherRunHolds()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        var refused = await RefusedAsync(harness, Sources(temp, 43), inspector, record);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            $"'{await ActionPathAsync(temp, harness)}' declares what its inputs take, and this run's are not that: input 'sources', "
                + "given with --input, holds 43 item(s) parted by ' ', and it takes at most 32. Nothing was run.",
            refused.Message);
        NeverPlaced(temp, harness, inspector, record);
    }

    /// <summary>
    /// A required input with no value anywhere is refused before any leg is placed too, naming where a value could have
    /// come from - with this machine's .env, which the leg this machine runs reads - rather than once the leg had waited
    /// for its slot and its runner started.
    /// </summary>
    [Fact]
    public async Task ARequiredInputWithNoValue_IsRefusedBeforeAnyLegIsPlaced()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        var refused = await RefusedAsync(harness, Sources(temp, 2) with { Runner = "compile-rooted" }, inspector, record);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            $"'{await ActionPathAsync(temp, harness, "rooted")}' requires input(s) root, and none was given with --input, found in "
                + $"{(await harness.ContextLoader.LoadAsync(temp.Path, Token)).Layout.RunnerEnvDirectory} or declared as a default, "
                + "so its steps would run with nothing where a value belongs. Nothing was run.",
            refused.Message);
        NeverPlaced(temp, harness, inspector, record);
    }

    /// <summary>
    /// A value its input takes goes on: the leg is placed - its host measured - and waits for the slot another run holds
    /// until its machine's bound, and is not admitted. The scene the refusals above are made in is one whose run reaches
    /// admission, so what refused them came before it.
    /// </summary>
    [Fact]
    public async Task AValueItsInputTakes_GoesOnToWaitForItsSlot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        await using var services = Services(harness, inspector, record);
        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 32), RunId.New(), Token);

        Assert.Equal(LegExit.NotAdmitted, result.ExitCode);
        Assert.Equal(LegVerdict.NotAdmitted, Assert.Single(result.Report.Lines).Verdict);
        Assert.Equal([HostId.Local], inspector.Inspected);
        Assert.Equal(["other"], AdmissionKit.Read(record).Select(entry => entry.Leg));
    }

    /// <summary>
    /// The registrations a test builds keep this machine's settings file and its dispatch queue in the test process's own
    /// state, below the suite's temporary root, never in the machine user's - whose settings file may raise the machine's
    /// heavy legs past the one slot this scene's repository declares, and would have its leg admitted and its step run:
    /// the file they read declares nothing, so the repository's rule is the one in force and the leg waits and is not
    /// admitted, on any machine, whatever its user keeps.
    /// </summary>
    [Fact]
    public async Task TheRegistrationsATestBuilds_KeepTheMachinesSettingsAndQueueInTheTestProcesssOwnState()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        await using var services = Services(harness, inspector, record);
        var settings = services.GetRequiredService<MachineSettingsFile>();
        var queue = services.GetRequiredService<MachineDispatchQueue>();
        var machine = harness.Platform.MachineId.Id;

        Assert.Equal(
            (Path.Combine(TestHost.UserStateDirectory, MachineSettingsFile.FileNameFor(machine)), Path.Combine(TestHost.UserStateDirectory, MachineDispatchQueue.FileNameFor(machine))),
            (settings.Location, queue.Location));
        TestHost.IsKeptInTheProcesssOwnState(settings.Location!);
        TestHost.IsKeptInTheProcesssOwnState(queue.Location);
        Assert.Null(settings.Read().Admission);

        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 32), RunId.New(), Token);

        Assert.Equal(LegExit.NotAdmitted, result.ExitCode);
        Assert.Equal(LegVerdict.NotAdmitted, Assert.Single(result.Report.Lines).Verdict);
    }

    /// <summary>
    /// A leg another machine runs reads that machine's .env, never this one's: before it is placed, only what --input gave
    /// is held to its input - refused here as for a leg of this machine - and this machine's runner values are not read at
    /// all, so a run of another machine's legs is never refused over them, a .env here that defines a name twice included.
    /// </summary>
    [Fact]
    public async Task ALegAnotherMachineRuns_IsHeldOnlyToWhatTheCommandLineGave_AndThisMachinesValuesAreNotRead()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "a.env"), "root=here\nroot=again\n");

        var refused = await RefusedAsync(harness, Sources(temp, 33) with { LegNames = ["there"], Runner = "compile-rooted" }, inspector, record);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            $"'{await ActionPathAsync(temp, harness, "rooted")}' declares what its inputs take, and this run's are not that: input "
                + "'sources', given with --input, holds 33 item(s) parted by ' ', and it takes at most 32. Nothing was run.",
            refused.Message);
        NeverPlaced(temp, harness, inspector, record);
    }

    /// <summary>
    /// A step naming an input that is neither required nor given a default, which this run gave no value, is refused
    /// before any leg is placed too - after every input was found to have a value it takes where it needs one - as the
    /// leg's runner would refuse it: the file lets the input go without a value, a configuration error naming the line,
    /// the input and how it can have one, rather than once the leg had waited for its slot.
    /// </summary>
    [Fact]
    public async Task AStepNamingAnInputWithNoValue_IsRefusedBeforeAnyLegIsPlaced()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        var refused = await RefusedAsync(harness, Sources(temp, 2) with { Runner = "compile-labelled" }, inspector, record);

        Assert.Equal(HarnessExit.ConfigInvalid, refused.ExitCode);
        Assert.Equal(
            "'compile' run line names '{label}', an input of the action, which has no value: give it one with --input "
                + $"label=<value> or in {(await harness.ContextLoader.LoadAsync(temp.Path, Token)).Layout.RunnerEnvDirectory}, "
                + "or declare a default for it.",
            refused.Message);
        NeverPlaced(temp, harness, inspector, record);
    }

    /// <summary>
    /// A program nothing declares, which the leg's runner refuses before its first step, is refused before the leg waits
    /// for its slot: the leg placed and its tree locked, and then refused as its runner would refuse it, in the same words
    /// and with its code, the slot another run holds never waited for and nothing run.
    /// </summary>
    [Fact]
    public async Task AProgramNothingDeclares_IsRefusedBeforeTheLegWaitsForItsSlot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        await using var services = Services(harness, inspector, record);
        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 2) with { Runner = "compile-undeclared" }, RunId.New(), Token);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Equal(
            $"'{await ActionPathAsync(temp, harness, "undeclared")}' names 1 program(s) that may not run:{Environment.NewLine}"
                + "  - line 9: 'make' is not declared under 'tools' and is not a path inside the repository (declared: 'dotnet').",
            result.Message);
        WaitedForNothing(harness, inspector, record, result);
    }

    /// <summary>
    /// A run line naming what nothing fills in, which the leg's runner refuses before its first step, is refused before
    /// the leg waits for its slot too, as its runner would refuse it.
    /// </summary>
    [Fact]
    public async Task ANameNothingFillsIn_IsRefusedBeforeTheLegWaitsForItsSlot()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        await using var services = Services(harness, inspector, record);
        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 2) with { Runner = "compile-unfilled" }, RunId.New(), Token);

        Assert.Equal(HarnessExit.ConfigInvalid, result.ExitCode);
        Assert.StartsWith("'compile' run line names '{nothing}', which nothing here can fill in.", result.Message, StringComparison.Ordinal);
        WaitedForNothing(harness, inspector, record, result);
    }

    /// <summary>
    /// A line that would put a secret in its arguments, which the leg's runner refuses as its steps would start, is
    /// refused before the leg waits for its slot as well - by the same check and in the same words, the secret never said,
    /// the slot another run holds never waited for and nothing run.
    /// </summary>
    [Fact]
    public async Task ASecretALineWouldCarry_IsRefusedBeforeTheLegWaitsForItsSlot()
    {
        const string Secret = "not-a-real-secret-4f2a";
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".secrets", "ci.env"), $"HARNESS_TOKEN={Secret}\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "a.env"), $"DB_URL=https://reader:{Secret}@db\n");

        await using var services = Services(harness, inspector, record);
        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 2) with { Runner = "compile-leaking" }, RunId.New(), Token);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Equal(
            "Step 'compile' of runner 'compile-leaking' puts a secret in its argument list, where the log header, the "
                + "process table and any error quoting the command would all carry it. Hand it over through the environment instead.",
            result.Message);
        Assert.DoesNotContain(Secret, harness.StandardOutput.ToString() + harness.StandardError.ToString(), StringComparison.Ordinal);
        WaitedForNothing(harness, inspector, record, result);
    }

    /// <summary>
    /// A run check whose runner its own steps would refuse is said before the leg waits for its slot, and the run goes
    /// on: the check runs only where a failure matches the expected exception it confirms, so a run whose check may never
    /// run is not refused over it - one that starts it is refused by it then, leaving the failure unexcused.
    /// </summary>
    [Fact]
    public async Task ARunCheckItsOwnStepsWouldRefuse_IsSaidBeforeTheLegWaits_AndTheRunGoesOn()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);

        await using var services = Services(harness, inspector, record);
        var result = await services.GetRequiredService<IRunnerLegs>().RunAsync(Sources(temp, 2) with { Runner = "compile-checked" }, RunId.New(), Token);

        Assert.Equal(LegExit.NotAdmitted, result.ExitCode);
        Assert.Equal(LegVerdict.NotAdmitted, Assert.Single(result.Report.Lines).Verdict);
        Assert.Contains(
            "run: WARN - native: run check 'check-undeclared' of runner 'compile-checked' would be refused were a failure it "
                + $"confirms to start it, leaving that failure unexcused: '{await ActionPathAsync(temp, harness, "undeclared")}' "
                + "names 1 program(s) that may not run:",
            harness.StandardError.ToString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// What a WSL distribution is asked before the machine it runs on takes its leg's slot: the run's own line with
    /// --here and the check's option, which holds the leg - one that machine runs now - to that machine's own .env, by
    /// the same rule and in the same terms the run would, and to everything else its runner would refuse before its first
    /// step that does not wait on its build, and runs nothing: what its inputs refuse refused before its host is measured,
    /// the rest once it is placed, no slot asked for and no run kept.
    /// </summary>
    [Fact]
    public async Task ACheckBeforeAdmission_HoldsTheLegToTheValuesOfTheMachineItRunsOn_AndRunsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var (inspector, record) = await CompilingAsync(temp, harness);
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", ".env", "a.env"),
            $"sources={string.Join(" ", Enumerable.Range(1, 33).Select(index => $"s{index}.c"))}\n");

        var request = new RunnerLegsRequest
        {
            Directory = temp.Path,
            Runner = "compile-rooted",
            LegNames = ["there"],
            Here = HostId.Ssh("vps"),
            Inputs = new Dictionary<string, string> { ["root"] = "/srv" },
        };

        await using var services = Services(harness, inspector, record);
        var legs = services.GetRequiredService<IRunnerLegs>();

        var refused = await Assert.ThrowsAsync<HarnessException>(() => legs.CheckBeforeAdmissionAsync(request, Token));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            $"'{await ActionPathAsync(temp, harness, "rooted")}' declares what its inputs take, and this run's are not that: input "
                + "'sources', from the runner's .env, holds 33 item(s) parted by ' ', and it takes at most 32. Nothing was run.",
            refused.Message);
        NeverPlaced(temp, harness, inspector, record);

        temp.WriteFile(Path.Combine(".harness-config", "runner", ".env", "a.env"), "sources=a.c b.c\n");

        var passed = await legs.CheckBeforeAdmissionAsync(request, Token);

        Assert.Equal(
            (HarnessExit.Success, true, (RunId?)null, (string?)null),
            (passed.ExitCode, passed.Stopped, passed.Run, passed.RunDirectory));
        Assert.Equal(
            "runner 'compile-rooted': nothing a run of it would refuse before its legs wait for their slots was found, "
                + "checking leg(s) there; nothing was run",
            passed.Message);
        Assert.Empty(passed.Report.Lines);
        Assert.Equal([HostId.Local], inspector.Inspected);
        Assert.Equal([AdmissionKit.Holder(harness, "other")], AdmissionKit.Read(record));
        NothingWasRun(temp);

        var undeclared = await Assert.ThrowsAsync<HarnessException>(() => legs.CheckBeforeAdmissionAsync(
            request with { Runner = "compile-undeclared", Inputs = new Dictionary<string, string>() },
            Token));

        Assert.Equal(HarnessExit.Refused, undeclared.ExitCode);
        Assert.StartsWith($"'{await ActionPathAsync(temp, harness, "undeclared")}' names 1 program(s) that may not run:", undeclared.Message, StringComparison.Ordinal);
        Assert.Equal([AdmissionKit.Holder(harness, "other")], AdmissionKit.Read(record));
        NothingWasRun(temp);
    }

    /// <summary>
    /// A repository whose runners run an action of a heavy step bounding its sources at 32 - one of them requiring a root
    /// besides, another naming an input nothing need give a value - on a leg of this machine and one of an ssh host, this
    /// machine declaring one heavy-leg slot, which another run holds; with the survey that measures this machine as it is,
    /// and the slots' record the test's own.
    /// </summary>
    private static async Task<(RecordingInspector Inspector, string Record)> CompilingAsync(TempDirectory temp, HarnessFactory harness)
    {
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, Token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            SshItems = { "vps" },
            Hosts = new HostsConfig { Ssh = { ["vps"] = new SshHostConfig { RepositoryPath = "/home/dev/repo" } } },
            Defaults = new HarnessDefaults { Admission = new AdmissionSettings { HeavyLegs = 1, MaxWaitMinutes = 1 } },
            Legs =
            {
                ["native"] = HostDoubles.Leg(platform.PlatformKey, platform.Processor),
                ["there"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Ssh = "vps" },
            },
            PredefinedRunners =
            {
                ["compile"] = new RunnerConfig { Action = "compile/compile.yml", Legs = ["native"] },
                ["compile-rooted"] = new RunnerConfig { Action = "rooted/rooted.yml", Legs = ["native"] },
                ["compile-labelled"] = new RunnerConfig { Action = "labelled/labelled.yml", Legs = ["native"] },
                ["compile-undeclared"] = new RunnerConfig { Action = "undeclared/undeclared.yml", Legs = ["native"] },
                ["compile-unfilled"] = new RunnerConfig { Action = "unfilled/unfilled.yml", Legs = ["native"] },
                ["compile-leaking"] = new RunnerConfig { Action = "leaking/leaking.yml", Legs = ["native"] },
                ["compile-checked"] = new RunnerConfig
                {
                    Action = "compile/compile.yml",
                    Legs = ["native"],
                    ExpectedExceptions =
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
                            RunChecks = [new RunCheck { PredefinedRunner = "check-undeclared" }],
                        },
                    ],
                },
                ["check-undeclared"] = new RunnerConfig { Action = "undeclared/undeclared.yml", Legs = ["native"] },
            },
        });

        const string Steps = """
            steps:
              - name: compile
                heavy: true
                run: |
                  dotnet --version
            """;

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "compile", "compile.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n" + Steps);
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "rooted", "rooted.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n  root:\n    required: true\n" + Steps);
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "labelled", "labelled.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n  label:\n    description: what the build is called\n"
                + Steps.Replace("dotnet --version", "dotnet --version {label}", StringComparison.Ordinal));
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "undeclared", "undeclared.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n" + Steps.Replace("dotnet --version", "make --version", StringComparison.Ordinal));
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "unfilled", "unfilled.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n" + Steps.Replace("dotnet --version", "dotnet --version {nothing}", StringComparison.Ordinal));

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "leaking", "leaking.yml"),
            "inputs:\n  sources:\n    separator: ' '\n    maxItems: 32\n" + Steps.Replace("dotnet --version", "dotnet --version {DB_URL}", StringComparison.Ordinal));

        var record = temp.Combine("state", "admission.json");
        AdmissionKit.Write(record, AdmissionKit.Holder(harness, "other"));

        return (new RecordingInspector(host => new HostReport { Host = host, Os = platform.PlatformKey, Processor = platform.Processor }), record);
    }

    /// <summary>A run of 'compile' in place on its own legs, giving <paramref name="count"/> sources.</summary>
    private static RunnerLegsRequest Sources(TempDirectory temp, int count) => new()
    {
        Directory = temp.Path,
        Runner = "compile",
        InPlace = true,
        Inputs = new Dictionary<string, string>
        {
            ["sources"] = string.Join(" ", Enumerable.Range(1, count).Select(index => $"s{index}.c")),
        },
    };

    /// <summary>Where the action file of <paramref name="action"/> is read from, every link followed, as a refusal names it.</summary>
    private static async Task<string> ActionPathAsync(TempDirectory temp, HarnessFactory harness, string action = "compile")
        => harness.FileSystem.ResolveLinks(Path.Combine(
            (await harness.ContextLoader.LoadAsync(temp.Path, Token)).Layout.RunnerActionsDirectory,
            action,
            $"{action}.yml"));

    /// <summary>
    /// That a run was refused before any of its legs was placed: no host measured, the slots' record as another run left
    /// it, and no run kept.
    /// </summary>
    private static void NeverPlaced(TempDirectory temp, HarnessFactory harness, RecordingInspector inspector, string record)
    {
        Assert.Empty(inspector.Inspected);
        Assert.Equal([AdmissionKit.Holder(harness, "other")], AdmissionKit.Read(record));
        NothingWasRun(temp);
    }

    /// <summary>
    /// That a run was refused once its leg was placed and before the leg waited for its slot: this machine measured, the
    /// slots' record as another run left it, and no leg's line, its work never started.
    /// </summary>
    private static void WaitedForNothing(HarnessFactory harness, RecordingInspector inspector, string record, LegRunResult result)
    {
        Assert.Equal([HostId.Local], inspector.Inspected);
        Assert.Equal([AdmissionKit.Holder(harness, "other")], AdmissionKit.Read(record));
        Assert.Empty(result.Report.Lines);
    }

    /// <summary>
    /// run's use case as the tool's own registrations build it in a test (<see cref="TestRegistrations"/>), save that this
    /// machine is measured by <paramref name="inspector"/> and its heavy-leg slots are kept at <paramref name="record"/>
    /// under a clock every wait moves: a run that goes on reaches only what the test holds, and a leg admitted by mistake
    /// runs its step through the built CLI and fails the test's assertion rather than hanging it.
    /// </summary>
    private static ServiceProvider Services(HarnessFactory harness, RecordingInspector inspector, string record)
    {
        var services = TestRegistrations.Of(new SessionOptions { Command = RunnerRunService.CommandName, Prompting = false }, OperationId.New(), output: _ => harness.Output);

        services.AddSingleton<IHostInspector>(inspector);
        services.AddSingleton(AdmissionKit.Admission(harness, record, new ScriptedGauge(10), new ManualClock()));

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static async Task<HarnessException> RefusedAsync(HarnessFactory harness, RunnerLegsRequest request, RecordingInspector inspector, string record)
    {
        await using var services = Services(harness, inspector, record);

        return await Assert.ThrowsAsync<HarnessException>(() => services.GetRequiredService<IRunnerLegs>().RunAsync(request, RunId.New(), Token));
    }

    /// <summary>
    /// A run check's runner that carries checks of its own runs them at the leg, to any depth - three deep here, each
    /// check's runner run inside the one before it, on the leg, as built as the leg's own run left it - and a chain that
    /// leads back to a runner it is confirming for is refused, naming the chain, before that runner runs again.
    /// </summary>
    [Fact]
    public async Task ARunChecksOwnChecks_RunAtTheLegToAnyDepth_AndAChainBackToOneItConfirmsFor_IsRefused()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var config = Config(harness);

        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            config.PredefinedRunners[name] = new RunnerConfig { Phases = [new RunnerPhase { Name = "look", Command = ["dotnet", "--version"] }] };
        }

        var leg = config.Legs["native"];
        var variant = VariantKey.For(config, leg, harness.Platform.PlatformKey);
        var host = new HostReport { Host = HostId.Local, Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor };
        var work = new LegWork(
            new PlacedLeg("native", leg, host, null, variant, temp.Path, temp.Path, variant.DirectoryUnder(temp.Path), new LocalHostConfig(), Emulated: false),
            new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config),
            RunId.New(),
            temp.Combine("runs", "r1"),
            Time: false);
        var runs = new ChainedRuns(new Dictionary<string, string>(StringComparer.Ordinal) { ["a"] = "b", ["b"] = "c", ["d"] = "e", ["e"] = "d" });
        var toolchains = new CMakeToolchainReader(new PhysicalFileSystem(FilePermissionsFactory.Create()));

        var confirmed = await RunnerLegs.ConfirmAsync(runs, toolchains, config, work, "a", built: true, ["probe"], Token);

        Assert.True(confirmed.Success);
        Assert.Equal(["a", "b", "c"], runs.Ran.Select(request => request.RunnerName));
        Assert.All(runs.Ran, request => Assert.Equal(("native", true, temp.Path), (request.Leg, request.Built, request.TreeRoot)));

        runs.Ran.Clear();
        var refused = await Assert.ThrowsAsync<HarnessException>(() => RunnerLegs.ConfirmAsync(runs, toolchains, config, work, "d", built: true, ["probe"], Token));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "a run check leads back to predefined runner 'd' through 'probe' -> 'd' -> 'e', so it would confirm itself; nothing it gates is excused",
            refused.Message);
        Assert.Equal(["d", "e"], runs.Ran.Select(request => request.RunnerName));
    }

    /// <summary>
    /// Runs a runner as a run of it whose check names <paramref name="checks"/>' runner for it would: the check run, through
    /// the request's own way to run it, before the run answers - each request kept, in the order the runs began.
    /// </summary>
    private sealed class ChainedRuns(IReadOnlyDictionary<string, string> checks) : IRunnerRunService
    {
        public List<RunnerRunRequest> Ran { get; } = [];

        public async Task<RunnerLegResult> RunAsync(HarnessConfig config, RunnerRunRequest request, CancellationToken cancellationToken = default)
        {
            Ran.Add(request);

            var outcome = checks.TryGetValue(request.RunnerName, out var check)
                ? await request.InvokeRunner!(check, cancellationToken)
                : RunOutcome.Ok($"{request.RunnerName} ran");

            return new RunnerLegResult(
                ReachedVerdict.Of(LegVerdict.Passed),
                outcome,
                new LegEntry { Leg = request.Leg, Verdict = LegVerdict.Passed },
                [],
                new Dictionary<string, RunOutcome>(StringComparer.Ordinal),
                ExpectedException: null,
                Gate: null,
                PerformedActions: []);
        }

        public Task VetAsync(HarnessConfig config, RunnerRunRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("a check's run is never vetted before admission");
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
