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
/// orchestrator run will call - the runner's steps read and chosen, its workload decided, and the placement of a runner
/// that would write the tree refused, all before any leg is placed or any host is measured.
/// </summary>
public interface IRunnerLegs : IUseCase
{
    /// <summary>Runs the runner across the legs it declares and answers what the run reached.</summary>
    /// <param name="request">What was asked.</param>
    /// <param name="run">The run, begun before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>What the run reached, in the terms its caller answers in.</returns>
    Task<LegRunResult> RunAsync(RunnerLegsRequest request, RunId run, CancellationToken cancellationToken = default);
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
    private readonly IRunnerRunService _runners = runners;
    private readonly IBuildService _builds = builds;
    private readonly CMakeToolchainReader _toolchains = toolchains;
    private readonly LegRunService _runs = runs;

    /// <inheritdoc/>
    public async Task<LegRunResult> RunAsync(RunnerLegsRequest request, RunId run, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(run);

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

        // The runners its expected exceptions' run checks name, which run within its legs, each with the steps a run of
        // it that names none runs: one among them that needs the build builds the legs first, since a check runs on the
        // leg as this runner left it, and a heavy one makes them heavy, as this runner would. Read only to know those:
        // one whose steps cannot be read counts heavy, said here, builds nothing, and is refused by the run check that
        // runs it, as it always was, rather than refusing a run whose checks may never run.
        var checks = new List<(string Name, RunnerConfig Runner, ActionFile? Action, bool Unread)>();

        foreach (var check in runner.RunCheckRunners())
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

        return await _runs.ExecuteAsync(
            RunnerRunService.CommandName,
            run,
            new LegRunRequest(
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
            },
            (work, token) => RunLegAsync(_runners, _builds, _toolchains, runnerName, request.Inputs, request.ManualSteps, workload, work, token),
            cancellationToken).ConfigureAwait(false);
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
                .BuildAsync(config, leg.BuildRequestFor(config, work.RunDirectory), cancellationToken)
                .ConfigureAwait(false);

            compilers = build.Compilers;
            built = build.Notes;

            if (build.Verdict.Verdict != LegVerdict.Passed)
            {
                return new LegEntry
                {
                    Leg = leg.Name,
                    Verdict = build.Verdict.Verdict,
                    Detail = build.Verdict.Detail,
                    Duration = Stopwatch.GetElapsedTime(started),
                    Emulated = leg.Emulated,
                    TimingNotes = built,
                    Compilers = compilers,
                    LogTail = build.Tail,
                    TailPhase = build.TailPhase,
                };
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

                    // One level deep by construction: the runner a check names carries no checks of
                    // its own, and this delegate reaches the service only for that one.
                    InvokeRunner = (name, token) => ConfirmAsync(runners, toolchains, config, work, name, building, token),
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
    /// One level deep, and enforced here as well as by the configuration: the runner this reaches is
    /// given no way to invoke another, so even a configuration that slipped past validation cannot
    /// make a check confirm itself. A runner reached this way never builds the leg: the runner
    /// carrying the check built it first where the check needs the build, and rebuilding it mid-run
    /// would replace the binaries the failure being explained came from.
    /// </remarks>
    private static async Task<RunOutcome> ConfirmAsync(
        IRunnerRunService runners,
        CMakeToolchainReader toolchains,
        HarnessConfig config,
        LegWork work,
        string runnerName,
        bool built,
        CancellationToken cancellationToken)
    {
        var result = await runners
            .RunAsync(config, RequestFor(work, runnerName, RunnerSelection.Resolve(config, runnerName), built, toolchains), cancellationToken)
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
