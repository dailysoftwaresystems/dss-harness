using System.Diagnostics;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Application.Legs;

/// <summary>
/// Runs a predefined runner across the legs it declares: the use case the <c>run</c> command runs, and a step of an
/// orchestrator run will call - the runner's steps read and chosen, its action's inputs given their values and held to
/// what each declares, its workload decided, and the placement of a runner that would write the tree refused, all before
/// any leg is placed or any host is measured; and everything else its steps would refuse that does not wait on the leg's
/// build, before each leg waits for its slot.
/// </summary>
public interface IRunnerLegs : IUseCase
{
    /// <summary>Runs the runner across the legs it declares and answers what the run reached.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="run">The run, begun before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What the run reached, in the terms its caller answers in.</returns>
    Task<LegRunResult> RunAsync(RunnerLegsRequest request, RunId run, CancellationToken cancellationToken = default);

    /// <summary>
    /// Refuses what a run of the same request would refuse before its legs wait for their slots - what it refuses before
    /// any leg is placed, and what each leg this machine runs would refuse as its runner starts that does not wait on the
    /// leg's build, by this machine's values - and runs nothing: what a WSL distribution is asked, with
    /// <see cref="RunnerLegs.CheckBeforeAdmissionOption"/>, for a leg the machine it runs on admits.
    /// </summary>
    /// <param name="request">What was asked.</param>
    /// <param name="cancellationToken">Stops the reads and the survey.</param>
    /// <returns>A run that kept nothing and ran no leg, saying the check found nothing to refuse.</returns>
    /// <exception cref="HarnessException">The run would be refused, as it would be refused.</exception>
    Task<LegRunResult> CheckBeforeAdmissionAsync(RunnerLegsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>What a run of a runner was asked: the runner, its inputs, and the manual steps of its action to run.</summary>
public sealed record RunnerLegsRequest : LegWorkWithSetUpsRequest
{
    /// <summary>The predefined runner to run, as <c>predefinedRunners</c> names it.</summary>
    public required string Runner { get; init; }

    /// <summary>
    /// A value for each of the action's inputs this run gives: <c>--input name=value</c>, parsed. Over the runner's
    /// <c>.env</c> values and the input's default; never a secret.
    /// </summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = new Dictionary<string, string>();

    /// <summary>
    /// Only these manual steps of the runner's action, each after the steps it needs; empty for every step that is not
    /// manual, or the steps the runner names.
    /// </summary>
    public IReadOnlyList<string> ManualSteps { get; init; } = [];
}

/// <summary>Whether a runner is declared, and which: the one question the command and its run checks ask alike.</summary>
public static class RunnerSelection
{
    /// <summary>
    /// The runner <paramref name="runnerName"/> names, as the configuration declares it.
    /// </summary>
    /// <param name="config">The configuration.</param>
    /// <param name="runnerName">The name as it was given, blank included: a runner nobody declared, refused as one.</param>
    /// <exception cref="HarnessException">No runner of that name is declared (<see cref="HarnessExit.UsageError"/>).</exception>
    public static RunnerConfig Resolve(HarnessConfig config, string runnerName)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(runnerName);

        var declared = DeclaredName.In(config.PredefinedRunners.Keys, runnerName);

        return declared is null
            ? throw new HarnessException(
                HarnessExit.UsageError,
                DeclaredName.NotDeclared(runnerName, "predefinedRunners", config.PredefinedRunners.Keys))
            : config.PredefinedRunners[declared];
    }
}

/// <summary>
/// Runs a predefined runner across the legs it declares, with the same isolation, locking, stall bounds, witnesses and
/// reporting build and test get.
/// </summary>
public sealed class RunnerLegs(
    IHarnessContextLoader contextLoader,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IActionFileParser actionFiles,
    ActionValuesReader values,
    IRunnerRunService runners,
    IBuildService builds,
    CMakeToolchainReader toolchains,
    LegRunService runs) : IRunnerLegs
{
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly IActionFileParser _actionFiles = actionFiles;
    private readonly ActionValuesReader _values = values;
    private readonly IRunnerRunService _runners = runners;
    private readonly IBuildService _builds = builds;
    private readonly CMakeToolchainReader _toolchains = toolchains;
    private readonly LegRunService _runs = runs;

    /// <summary>
    /// The hidden option of <c>run</c> that has it check what a run would refuse of its legs before they wait for their
    /// slots and run nothing (<see cref="CheckBeforeAdmissionAsync"/>): given, with <c>--here</c>, to the run of a leg a WSL
    /// distribution runs that the machine it runs on admits, before that machine asks for the leg's slot. Typed by nobody.
    /// </summary>
    public const string CheckBeforeAdmissionOption = "--check-before-admission";

    /// <inheritdoc/>
    public async Task<LegRunResult> RunAsync(RunnerLegsRequest request, RunId run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);

        var (_, legRun, workload) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var runnerName = request.Runner;

        return await _runs.ExecuteAsync(
            RunnerRunService.CommandName,
            run,
            legRun,
            (work, token) => RunLegAsync(_runners, _builds, _toolchains, runnerName, request.Inputs, request.ManualSteps, workload, work, token),
            cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<LegRunResult> CheckBeforeAdmissionAsync(RunnerLegsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var (harness, legRun, _) = await PlanAsync(request, cancellationToken).ConfigureAwait(false);
        var legs = await _runs.CheckBeforeAdmissionAsync(legRun, cancellationToken).ConfigureAwait(false);
        var checkedLegs = legs.Count == 0 ? "no leg this machine runs" : $"leg(s) {string.Join(", ", legs)}";

        return new LegRunResult(
            RunnerRunService.CommandName,
            Run: null,
            RunDirectory: null,
            LedgerReport.From([], harness.Config.Defaults.DurationWarningFactor),
            HarnessExit.Success,
            $"runner '{request.Runner}': nothing a run of it would refuse before its legs wait for their slots was found, "
            + $"checking {checkedLegs}; nothing was run",
            Cancelled: false,
            Unfinished: [],
            Details: [],
            Stopped: true);
    }

    /// <summary>
    /// The run of the legs <paramref name="request"/> asks for, with everything refused that can be before any leg is
    /// placed, and the check each leg is held to before it waits for its slot; and what the legs' work is: the one plan a
    /// run and a check before admission share, so the check refuses exactly what the run would.
    /// </summary>
    private async Task<(HarnessContext Harness, LegRunRequest Run, LegWorkload Workload)> PlanAsync(RunnerLegsRequest request, CancellationToken cancellationToken)
    {
        var (harness, runner, file, legs, selected) = await PrepareAsync(request, cancellationToken).ConfigureAwait(false);
        var runnerName = request.Runner;

        // The runners its expected exceptions' run checks name, and theirs to any depth, which run within its legs, each with the steps a run of
        // it that names none runs: one among them that needs the build builds the legs first, since a check runs on the
        // leg as this runner left it, and a heavy one makes them heavy, as this runner would. Read only to know those:
        // one whose steps cannot be read counts heavy, said here, builds nothing, and is refused by the run check that
        // runs it, as it always was, rather than refusing a run whose checks may never run.
        var checks = new List<(string Name, RunnerConfig Runner, ActionFile? Action, bool Unread)>();

        foreach (var check in RunnerConfig.RunCheckRunners(harness.Config.PredefinedRunners, runnerName))
        {
            var checkRunner = RunnerSelection.Resolve(harness.Config, check);

            try
            {
                checks.Add((check, checkRunner, (await StepsAsync(check, checkRunner, [], harness, cancellationToken).ConfigureAwait(false))?.File, false));
            }
            catch (HarnessException ex)
            {
                _output.Warn(
                    RunnerRunService.CommandName,
                    $"run check '{check}' of runner '{runnerName}' could not be read to know how heavy it is or whether it "
                    + $"needs the build, so the legs of '{runnerName}' count as heavy, and are not built for it: {ex.Message}");
                checks.Add((check, checkRunner, null, true));
            }
        }

        // Built only where the runner, or a runner its run checks name, requires the build or runs a step or
        // phase naming what the build makes - {product}, {buildDir} or one of the leg's compilers - and never
        // tested: a host needs cmake for a runner that measures a build product, and not for one that only runs
        // a script. Decided once, here, for every leg: whether a leg builds is this workload read for its system.
        var workload = LegWorkload.ForRunner(runner, file?.File, checks) with { SetUp = request.SetUp };

        // A leg it would build that cannot be built, a {product} this runner's steps or phases name that no one
        // declared file fills in, and a compiler they name on a leg no build identifies one for, refused here as
        // a leg that would run nothing is: before a host is measured, naming every such leg and what builds it.
        workload.RequireBuildable(harness.Config, runnerName, legs);

        // Where this run's own legs work, as the command's two options say - and as the runner says too: one that
        // writes the tree works in place whatever the leg's own key or the repository's default says, for what it
        // wrote in a copy the next sync would make the tree again. Both refusals are made here, as a leg that
        // would run nothing is refused: before a host is measured, naming the leg it refuses. A run check's runner
        // runs within the leg's own work, so it writes wherever the leg works and is held to the key's contract
        // the same way.
        var where = LegTrees.Asked(request.LocalCopy, request.InPlace);
        var here = request.Here is not null;
        var writing = harness.Config.WritingRunners(runnerName);

        if (writing.Any(writer => !writer.IsCheck))
        {
            if (where == LocalTrees.Copy)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"Runner '{runnerName}' writes the tree (writesTree), which --local-copy would have it write "
                    + "in a copy. Leave --local-copy out.");
            }

            foreach (var leg in legs.Where(leg => !RunsHere(_platform, harness.Config, leg.Leg, here)))
            {
                if (WhereItWouldRun(harness.Config, leg.Leg) is not { } on)
                {
                    continue;
                }

                throw new HarnessException(
                    HarnessExit.Refused,
                    $"Runner '{runnerName}' writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of "
                    + $"it on {on}, which the next sync makes the tree again. Nothing was run. "
                    + "Run it on a leg this machine runs itself.");
            }

            where = LocalTrees.InPlace;
        }

        foreach (var (check, _) in writing.Where(writer => writer.IsCheck))
        {
            var who = $"Run check '{check}' of runner '{runnerName}'";

            if (where == LocalTrees.Copy)
            {
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"{who} writes the tree (writesTree), which --local-copy would have it write in a copy. "
                    + "Leave --local-copy out.");
            }

            foreach (var leg in legs.Where(leg => !RunsHere(_platform, harness.Config, leg.Leg, here)))
            {
                if (WhereItWouldRun(harness.Config, leg.Leg) is not { } on)
                {
                    continue;
                }

                throw new HarnessException(
                    HarnessExit.Refused,
                    $"{who} writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of it on "
                    + $"{on}, which the next sync makes the tree again. Nothing was run. "
                    + "Run it on a leg this machine runs itself.");
            }

            // The key alone does not decide where a copy would be made: only a leg this machine runs itself works
            // in one, and only where the placement's own rule says so - LegTrees.InLocalCopy, the rule LegTrees.For
            // places a leg by, asked whole so the two cannot differ. A leg typed in a synced copy, or sent here by
            // another machine, works in the tree itself whatever its key says, so the check is not refused there;
            // and a leg no host can run works nowhere, so the copy this refusal exists to keep the check's writes
            // out of is never made - the run says so its own way, not over its localCopy key.
            foreach (var leg in legs.Where(leg => RunsHere(_platform, harness.Config, leg.Leg, here)
                    && LegTrees.InLocalCopy(harness, leg.Leg, request.Here, where)))
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"{who} writes the tree (writesTree), and leg '{leg.Name}' would work in a copy of it, as its own "
                    + $"localCopy key or the repository's default says. Nothing was run. Run it with --in-place.");
            }
        }

        // The run checks whose steps were read, which a leg's check before its slot vets too - one that could not be read
        // was said above, and is refused by the check that runs it.
        IReadOnlyList<string> vetted = [.. checks.Where(check => !check.Unread).Select(check => check.Name)];

        var legRun = new LegRunRequest(
            request.Directory,
            selected,
            request.ForceLock,
            request.Json,
            request.UseStaged,
            request.Time,
            request.Here,
            RunnerRunService.RemoteArguments(runnerName, request.Time, request.Inputs, request.ManualSteps))
        {
            Workload = workload,
            LocalTrees = where,
            Watch = request.Watch,
            Isolation = request.Isolation,
            KeepGoing = request.KeepGoing,

            // What each leg's runner would refuse as it starts that does not wait on the leg's build - its inputs, the
            // names its lines hold, the programs they start - asked before the leg waits for its slot: here, by this
            // machine's values, for a leg it runs; by the distribution, by its own, for a leg a WSL distribution runs.
            AdmissionCheck = new LegAdmissionCheck(
                (work, token) => VetLegAsync(_runners, _toolchains, _output, runnerName, request.Inputs, request.ManualSteps, vetted, work, token),
                [CheckBeforeAdmissionOption]),
        };

        return (harness, legRun, workload);
    }

    /// <summary>
    /// What a run of the runner <paramref name="request"/> names is made of, with everything refused that can be before any
    /// leg is placed or any host is measured - its action's inputs given their values and held to what each declares among
    /// it: the first part of the plan a run and a check before admission share (<see cref="PlanAsync"/>).
    /// </summary>
    private async Task<(HarnessContext Harness, RunnerConfig Runner, SelectedSteps? File, IReadOnlyList<SelectedLeg> Legs, IReadOnlyList<string>? Selected)> PrepareAsync(
        RunnerLegsRequest request,
        CancellationToken cancellationToken)
    {
        var runnerName = request.Runner;
        var harness = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);
        var runner = RunnerSelection.Resolve(harness.Config, runnerName);

        // The steps a run of a runner runs, where it runs an action: before a leg is placed or a host is
        // measured, so that a mistyped action costs nothing and says so in the same terms 'legs' would have.
        var file = await StepsAsync(runnerName, runner, request.ManualSteps, harness, cancellationToken).ConfigureAwait(false);

        if (file is null && request.ManualSteps.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"Runner '{runnerName}' runs phases of its own, and only an action's steps can be manual, "
                + $"so {StepSelection.Option} names nothing it could run.");
        }

        // And every value given goes to an input a step this run runs reads, asked here for the
        // same reason: once, before any leg's run has begun.
        CommandLineInputs.RequireRead(runnerName, file, request.Inputs);

        // The runner's own legs when --legs was left out. Resolved here rather than left to the
        // default of every declared leg, because running a benchmark on hosts nobody meant to
        // measure is not what "no --legs" asks for. Every declared leg only where the runner
        // leaves its legs out: a list given empty was refused when the file was read, and one a caller
        // builds empty reads as left out.
        var selected = request.LegNames ?? (runner.Legs is { Count: > 0 } declared ? declared : null);

        // A leg whose operating system no step runs on would run nothing and pass, and one on which
        // none of the steps this run named runs would run only what they need and pass. Refused here,
        // like a mistyped action, before a host is measured and naming every such leg at once,
        // rather than once per leg after each one's run has begun.
        var legs = LegSelection.Resolve(harness.Config, selected).Legs;
        var reached = legs.Select(leg => (leg.Name, leg.Leg.Os)).ToList();

        file?.File.RequireAStepOn(runnerName, reached);
        file?.RequireANamedStepOn(runnerName, reached);

        // Every input the steps this run runs read given its value and held to what it declares, here too: before any
        // leg is placed, synced, locked or admitted. Left to each leg's runner, a value the action refuses, or a required
        // input with none, was refused only once its leg had waited for a heavy-leg slot of its machine - a consumer's run
        // 49 minutes, to be told it gave 43 sources where its action took 32.
        if (file is not null)
        {
            await RequireInputsAsync(file, request.Inputs, legs, harness, request.Here is not null, cancellationToken).ConfigureAwait(false);
        }

        return (harness, runner, file, legs, selected);
    }

    /// <summary>
    /// The steps a run of <paramref name="declared"/> runs, where it runs an action: read here, not only once a leg is
    /// running it, so one unrecognised key or a mistyped manual step is refused once - before any leg's run has begun
    /// and its run directory exists.
    /// </summary>
    private async Task<SelectedSteps?> StepsAsync(
        string name,
        RunnerConfig declared,
        IReadOnlyList<string> manual,
        HarnessContext harness,
        CancellationToken cancellationToken)
    {
        if (declared.Action is not { Length: > 0 } action)
        {
            return null;
        }

        ActionPath.RequireResolvable(
            [new KeyValuePair<string, string>(name, action)],
            harness.Layout.RunnerActionsDirectory,
            _fileSystem,
            _platform.PathComparison);

        return StepSelection.For(declared, manual).Apply(name, await _actionFiles
            .LoadAsync(harness.Layout.RunnerActionsDirectory, action, cancellationToken)
            .ConfigureAwait(false));
    }

    /// <summary>
    /// Refuses a run in which an input a step of <paramref name="selected"/> reads is required and has no value, or has
    /// one its declaration refuses, on any of <paramref name="legs"/> - by the one rule each leg's runner gives them their
    /// values by (<see cref="ActionInputs"/>), over the steps that leg's system runs, every refused input named at once -
    /// and then one in which a step a leg this machine runs names an input with no value.
    /// </summary>
    /// <param name="selected">The steps the run runs.</param>
    /// <param name="given">What <c>run --input</c> gave.</param>
    /// <param name="legs">The legs the run reaches.</param>
    /// <param name="harness">The repository, whose runner values this machine reads.</param>
    /// <param name="here">Whether this process runs a leg the machine that reached it dispatched.</param>
    /// <param name="cancellationToken">Stops the read.</param>
    /// <exception cref="HarnessException">
    /// An input is missing or refused (<see cref="HarnessExit.UsageError"/>), or a step names one with no value
    /// (<see cref="HarnessExit.ConfigInvalid"/>).
    /// </exception>
    /// <remarks>
    /// A leg this machine runs reads this machine's <c>.env</c>, so its inputs are resolved from it whole. A leg another
    /// machine runs reads that machine's, which is its own and never synced: only what the command line gave is held to
    /// its declaration for it here, since that value reaches every machine alike, and an input given nothing is that
    /// machine's to resolve - an ssh host as its own run of the leg begins, before it admits the leg; a WSL distribution,
    /// for which this machine admits the leg, when this machine asks it to, before the leg waits for its slot
    /// (<see cref="CheckBeforeAdmissionOption"/>). The values are read only where a leg here needs them, so a run of another
    /// machine's legs alone is never refused over this machine's.
    /// </remarks>
    private async Task RequireInputsAsync(
        SelectedSteps selected,
        IReadOnlyDictionary<string, string> given,
        IReadOnlyList<SelectedLeg> legs,
        HarnessContext harness,
        bool here,
        CancellationToken cancellationToken)
    {
        var reached = legs.Select(leg => (leg.Leg.Os, Here: RunsHere(_platform, harness.Config, leg.Leg, here))).Distinct().ToList();

        var values = reached.Any(leg => leg.Here)
            ? await _values.ReadAsync(harness.Layout.RunnerEnvDirectory, harness.Layout.RunnerSecretsDirectory, cancellationToken).ConfigureAwait(false)
            : null;

        var missing = new List<string>();
        var refused = new List<string>();
        var resolvedHere = new List<(ActionFile Steps, ResolvedInputs Resolved)>();

        foreach (var (os, mine) in reached)
        {
            var steps = selected.File with { Steps = selected.File.StepsOn(os) };
            var resolved = ActionInputs.Resolve(steps, given, mine ? values!.Supplied : null);

            missing.AddRange(resolved.Missing.Where(name => !missing.Contains(name, StringComparer.Ordinal)));
            refused.AddRange(resolved.Refused.Where(said => !refused.Contains(said, StringComparer.Ordinal)));

            if (mine)
            {
                resolvedHere.Add((steps, resolved));
            }
        }

        ActionInputs.Require(
            selected.File.Path,
            harness.Layout.RunnerEnvDirectory,
            missing,
            refused,
            values is null ? (Func<string, string>)(said => said) : values.Redact);

        // A step a leg here runs naming an input with no value, refused here as its runner would refuse it, in the same
        // order: once every input is known to have a value it takes, where it needs one.
        foreach (var (steps, resolved) in resolvedHere)
        {
            ActionInputs.RefuseUnvalued(
                steps,
                step => ActionInputs.SuppliedTo(values!, resolved, step).Keys,
                values!,
                harness.Layout.RunnerEnvDirectory,
                namedOnCommandLine: true);
        }
    }

    /// <summary>
    /// Whether this machine would run <paramref name="leg"/> itself, as the placement's own rule answers it: a leg
    /// pinned to a host is that host's alone, and of the rest this machine takes one whose system and processor are its
    /// own. An emulator's check is the placement's to ask of the machine after this refusal, so an emulated leg of this
    /// machine's system is not refused here.
    /// </summary>
    private static bool RunsHere(IHostPlatform platform, HarnessConfig config, LegConfig leg, bool here)
    {
        if (!LegPlacement.Candidates(config, leg, here).Contains(HostId.Local))
        {
            return false;
        }

        return leg.Emulator is not null
            ? string.Equals(leg.Os, platform.PlatformKey, StringComparison.OrdinalIgnoreCase)
            : LegPlacement.PlatformObstacle(
                leg,
                new HostReport { Host = HostId.Local, Os = platform.PlatformKey, Processor = platform.Processor })
                is null;
    }

    /// <summary>
    /// The first host that would run <paramref name="leg"/> where this machine cannot, as a line names it, or
    /// <see langword="null"/> where the configuration names none: a leg nothing can run is not refused over where it
    /// would work, for the run says so its own way.
    /// </summary>
    private static string? WhereItWouldRun(HarnessConfig config, LegConfig leg)
        => LegPlacement.Candidates(config, leg).FirstOrDefault(host => host != HostId.Local)?.ToString();

    /// <summary>
    /// Refuses what the runner <paramref name="runnerName"/> would refuse of <paramref name="work"/>'s leg as it starts that
    /// does not wait on the leg's build, as its runner would refuse it (<see cref="IRunnerRunService.VetAsync"/>), and says
    /// what a run check among <paramref name="checks"/> would refuse were a failure to start it.
    /// </summary>
    /// <remarks>
    /// A run check's runner is vetted alike, by its own values, and what refuses it is said rather than refusing the run:
    /// it runs only where a failure matches the expected exception it confirms, so a run whose checks may never run is not
    /// refused over them - and one that does start it is refused by it then, as it always was, leaving the failure
    /// unexcused. Said before the leg waits, so a check that cannot run is known before it would have been needed.
    /// </remarks>
    private static async Task VetLegAsync(
        IRunnerRunService runners,
        CMakeToolchainReader toolchains,
        IHarnessOutput output,
        string runnerName,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyList<string> manualSteps,
        IReadOnlyList<string> checks,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var config = work.Context.Config;

        await runners
            .VetAsync(
                config,
                RequestFor(work, runnerName, RunnerSelection.Resolve(config, runnerName), built: false, toolchains) with
                {
                    Inputs = inputs,
                    ManualSteps = manualSteps,
                    NamedOnCommandLine = true,
                },
                cancellationToken)
            .ConfigureAwait(false);

        foreach (var check in checks)
        {
            try
            {
                await runners
                    .VetAsync(config, RequestFor(work, check, RunnerSelection.Resolve(config, check), built: false, toolchains), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex)
            {
                output.Warn(
                    RunnerRunService.CommandName,
                    $"{work.Leg.Name}: run check '{check}' of runner '{runnerName}' would be refused were a failure it "
                    + $"confirms to start it, leaving that failure unexcused: {ex.Message}");
            }
        }
    }

    private static async Task<LegEntry> RunLegAsync(
        IRunnerRunService runners,
        IBuildService builds,
        CMakeToolchainReader toolchains,
        string runnerName,
        IReadOnlyDictionary<string, string> inputs,
        IReadOnlyList<string> manualSteps,
        LegWorkload workload,
        LegWork work,
        CancellationToken cancellationToken)
    {
        var leg = work.Leg;
        var config = work.Context.Config;
        var runner = RunnerSelection.Resolve(config, runnerName);
        var started = Stopwatch.GetTimestamp();

        // Built before the runner starts where the workload says so for this leg's system: the runner,
        // or a runner its run checks name, requires the build or runs a step or phase naming what the
        // build makes - {product}, {buildDir} or one of the leg's compilers - on this leg's system,
        // whether or not a check ever runs. Unbuilt, such a step would read whatever was left there last time: a file that is not
        // there, or one an older commit built. Only a leg that builds names the compilers: one that
        // does not may never touch the build.
        IReadOnlyList<CompilerFact> compilers = [];

        // What the build says beyond its verdict, which the leg's line carries as the build's own does.
        IReadOnlyList<string> built = [];

        var building = workload.On(leg.Leg.Os).Build;

        if (building)
        {
            var build = await builds
                .BuildAsync(config, work.BuildRequest(), cancellationToken)
                .ConfigureAwait(false);

            compilers = build.Compilers;
            built = build.Notes;

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return build.Line(leg.Name, leg.Emulated, Stopwatch.GetElapsedTime(started));
            }
        }

        var result = await runners
            .RunAsync(
                config,
                RequestFor(work, runnerName, runner, building, toolchains) with
                {
                    Time = work.Time,

                    // Only the runner the command line named: the values were checked against its
                    // action's inputs, and a runner a check starts reads its own. So were the manual
                    // steps, and a runner a check starts runs its own steps.
                    Inputs = inputs,
                    ManualSteps = manualSteps,
                    NamedOnCommandLine = true,

                    // A check's run is given the same way to run its own checks, to any depth, each knowing the runners
                    // it is confirming for: a chain that led back to one would confirm itself, and is refused.
                    InvokeRunner = (name, token) => ConfirmAsync(runners, toolchains, config, work, name, building, [runnerName], token),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result.Entry with
        {
            Duration = Stopwatch.GetElapsedTime(started),
            TimingNotes = [.. built, .. result.Entry.TimingNotes],
            Compilers = compilers,
        };
    }

    /// <summary>
    /// Runs the runner a check names, for the gate that confirms an expected exception.
    /// </summary>
    /// <remarks>
    /// The runner this reaches may carry checks of its own, and is given the same way to run them, to any depth; each
    /// run knows the runners it is confirming for (<paramref name="along"/>), and one that a chain of checks leads back to
    /// is refused here as well as by the configuration, so even a configuration that slipped past validation cannot make a
    /// check confirm itself or its runs never end. A runner reached this way never builds the leg: the runner carrying
    /// the check built it first where the check needs the build, and rebuilding it mid-run would replace the binaries the
    /// failure being explained came from.
    /// </remarks>
    /// <param name="runners">What runs a runner.</param>
    /// <param name="toolchains">What reads a leg's CMake toolchain, for the run's request.</param>
    /// <param name="config">The configuration the runner is declared in.</param>
    /// <param name="work">The leg the check is confirming a failure on.</param>
    /// <param name="runnerName">The runner the check names.</param>
    /// <param name="built">Whether this run built the leg.</param>
    /// <param name="along">The runners whose checks led here, the leg's own runner first.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    internal static async Task<RunOutcome> ConfirmAsync(
        IRunnerRunService runners,
        CMakeToolchainReader toolchains,
        HarnessConfig config,
        LegWork work,
        string runnerName,
        bool built,
        IReadOnlyList<string> along,
        CancellationToken cancellationToken)
    {
        if (along.FirstOrDefault(earlier => string.Equals(earlier, runnerName, StringComparison.OrdinalIgnoreCase)) is { } repeated)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"a run check leads back to predefined runner '{repeated}' through {string.Join(" -> ", along.Select(name => $"'{name}'"))}, "
                + "so it would confirm itself; nothing it gates is excused");
        }

        IReadOnlyList<string> further = [.. along, runnerName];
        var result = await runners
            .RunAsync(
                config,
                RequestFor(work, runnerName, RunnerSelection.Resolve(config, runnerName), built, toolchains) with
                {
                    InvokeRunner = (name, token) => ConfirmAsync(runners, toolchains, config, work, name, built, further, token),
                },
                cancellationToken)
            .ConfigureAwait(false);

        return result.Outcome;
    }

    /// <summary>
    /// A run of <paramref name="runner"/> on the leg <paramref name="work"/> carries, with what the
    /// leg's host declares for it and whether this run built the leg. Made once, for the runner a leg
    /// runs and for the one a check names, so what the host declares reaches both or neither, and so
    /// does whether the leg was built: a step reading the build is refused on a leg that was not.
    /// </summary>
    private static RunnerRunRequest RequestFor(LegWork work, string runnerName, RunnerConfig runner, bool built, CMakeToolchainReader toolchains)
    {
        var leg = work.Leg;

        // Derived from the very directory this request carries, so a run line naming {product} and
        // one naming {buildDir} cannot come from two different tree roots. They do differ: a leg
        // placed on another machine re-invokes this there, where its own tree is the one that
        // resolves.
        var buildDirectory = leg.Variant.DirectoryUnder(leg.HostTreeRoot);
        var (product, productProblem) = leg.ProductFor(buildDirectory);

        return new RunnerRunRequest
        {
            ProgramDirectories = leg.Host.ProgramDirectories,
            HostEnvironment = leg.Environment,
            RunnerName = runnerName,
            Runner = runner,
            Leg = leg.Name,
            Layout = work.Context.Layout,
            RunId = work.RunId.Value,
            RunDirectory = work.RunDirectory,

            // A new segment each attempt, so resuming a run cannot record its work into the
            // attempt it is resuming.
            SegmentId = Guid.NewGuid().ToString("N")[..8],
            TreeRoot = leg.HostTreeRoot,
            WorkingDirectory = leg.HostTreeRoot,
            BuildDirectory = buildDirectory,
            Beside = leg.Beside,
            Built = built,
            Identity = leg.IdentityFor(work.RunId.Value),
            Product = product,
            ProductProblem = productProblem,

            // From that same directory, and read only where a run line names a compiler: what the build this run made
            // there identified, for the runner the command line named and for each a run check starts alike.
            Compilers = toolchains.NamedFor(leg.Project, buildDirectory),
            ResolvedLegs = [leg.Name],
            Emulated = leg.Emulated,
        };
    }
}
