using System.Text.Json.Nodes;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A step that runs legs - <c>build</c>, <c>test</c>, <c>run</c>, <c>check-mutations</c> - as a thin adapter over the
/// use case its command runs: the legs run in the tree the step works in (the worktree of the agent its <c>in</c>
/// names, else the main checkout); where each leg works, each copy made and each leg ended are told to the engine as the
/// leg service says them, so the step lets its tree go once no leg reads it; each leg's verdict is read on its own, and
/// <c>red</c> holds the red legs alone. The ledger is kept in the step's directory, and for a step in an agent's worktree
/// the log of each red leg's phase that did not pass, whole, where this machine ran it: that worktree, and the run
/// directory in it, go when the agent does.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the ledger and the red logs in the step's directory.</param>
public abstract class LegStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public abstract string Type { get; }

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var tree = place.Repository;

        if (run.Step.In is { } named)
        {
            // The tree is the worktree of the agent the step's create-agent made, which the step waited for: one that
            // is gone, or never had one, has no tree for its legs, and the step is not run.
            if (run.WorksIn is not { } agent)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' works in the agent '{named}' makes, and the run has made none for it");
            }

            if (store.ReadAgent(layout, agent) is not { State: AgentStates.Live } record || record.WorktreePath(context.Layout) is not { } worktree)
            {
                return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"'{run.Instance}' works in agent '{agent}' of '{place.Run}', which has no worktree for its legs to run in. Nothing was run.");
            }

            tree = worktree;
        }

        var watch = new StepWatch(run.Events);
        var result = await RunLegsAsync(run, context, tree, watch, cancellationToken).ConfigureAwait(false);

        Keep(result, layout.InstanceDirectory(run.Instance, run.WorksIn), redLogs: run.WorksIn is not null);

        var output = Output(result, arms: Type == "check-mutations");
        var lines = result.Report.Lines;

        // A run something other than its legs ended - refused, cut off, a leg it placed left with no line - ends the step
        // with the run's own code: its ledger holds only what its legs reached, which says nothing of what ended it. A run
        // its legs ended - none could run, another run owns its records - is read by its legs' verdicts like any other.
        if (result.Cancelled
            || lines.Count == 0
            || result.Stopped && (HarnessExit.RefusesTheRun(result.ExitCode) || watch.PlacedLegs.Any(leg => !lines.Any(line => line.Leg == leg))))
        {
            return FlowStepResult.Ended(passed: false, result.ExitCode, result.Message, output) with { Cause = result.Cause };
        }

        // The coordinator decides the step by each leg's verdict; what is said beside them is what those verdicts say,
        // never the command's own code, which ranks them otherwise.
        IReadOnlyList<FlowLeg> legs = [.. lines.Select(line => new FlowLeg(line.Leg, line.Verdict))];
        var deciding = legs.Where(leg => FlowLegVerdicts.ClassOf(leg.Verdict) != FlowLegClass.None).ToList();

        return new FlowStepResult
        {
            Asked = null,
            Passed = output["passed"]!.GetValue<bool>(),
            Exit = deciding.Count == 0 ? HarnessExit.Success : FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(deciding)!.Value),
            Output = output,
            Legs = legs,
            Cause = result.Cause,
        };
    }

    /// <summary>Runs the step's legs in <paramref name="tree"/> through its type's use case, telling <paramref name="watch"/> of each.</summary>
    /// <param name="run">The step, its keys filled.</param>
    /// <param name="context">The repository and its configuration, as the main checkout reads them.</param>
    /// <param name="tree">The tree the legs work in.</param>
    /// <param name="watch">Told of each leg as the leg service says it.</param>
    /// <param name="cancellationToken">Stops the legs.</param>
    protected abstract Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, CancellationToken cancellationToken);

    /// <summary>The legs a step names, or <see langword="null"/> for every leg its use case runs where it names none.</summary>
    /// <param name="step">The step.</param>
    protected static IReadOnlyList<string>? LegsOf(WorkflowStep step) => step.Legs.Count == 0 ? null : step.Legs;

    /// <summary>
    /// What a leg step answers: whether every leg passed, the command's own code, each leg's verdict, the red legs alone -
    /// each with the phase that did not pass, the ledger's line for it and the last lines it printed, or of a sweep each
    /// arm that is not as its registry says - and where the run's records are.
    /// </summary>
    /// <param name="result">What the legs reached.</param>
    /// <param name="arms">Whether the step is a sweep, whose red entries each name their arm.</param>
    internal static JsonObject Output(LegRunResult result, bool arms)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = result.Report.Lines;
        var red = new JsonArray();

        foreach (var line in lines.Where(line => FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.Red))
        {
            var redArms = arms ? line.Arms.Where(arm => FlowLegVerdicts.ClassOf(arm.Verdict) == FlowLegClass.Red).ToList() : [];

            foreach (var arm in redArms)
            {
                red.Add(Red(line.Leg, arm.Verdict, phase: null, arm.Detail, [], arm.Arm));
            }

            // The leg's own entry, where its verdict is its own - it failed outside every arm - and not one of its arms':
            // a sweep's leg is red by the worst of its own verdicts and its arms', and neither hides the other.
            if (!redArms.Any(arm => arm.Verdict == line.Verdict))
            {
                red.Add(Red(line.Leg, line.Verdict, line.TailPhase, line.Detail, line.LogTail, arm: null));
            }
        }

        return new JsonObject
        {
            ["passed"] = !result.Stopped && !result.Cancelled && lines.Count > 0 && lines.All(line => FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.None),
            ["exitCode"] = result.ExitCode,
            ["verdicts"] = new JsonArray([.. lines.Select(line => (JsonNode)new JsonObject { ["leg"] = line.Leg, ["verdict"] = Verdicts.Display(line.Verdict) })]),
            ["red"] = red,
            ["runDirectory"] = result.RunDirectory,
        };

        JsonObject Red(string leg, LegVerdict verdict, string? phase, string detail, IReadOnlyList<string> tail, string? arm)
        {
            var entry = new JsonObject
            {
                ["leg"] = leg,
                ["verdict"] = Verdicts.Display(verdict),
                ["phase"] = phase,
                ["detail"] = detail,
                ["tail"] = new JsonArray([.. tail.Select(line => (JsonNode?)line)]),
            };

            // A sweep's entry names its arm, or none where the leg failed outside every arm; no other step's has the key.
            if (arms)
            {
                entry["arm"] = arm;
            }

            return entry;
        }
    }

    /// <summary>
    /// Keeps what the step's legs reached in its own directory: the ledger, as <c>--json</c> gives it; and, where
    /// <paramref name="redLogs"/>, for each red leg this machine ran, the log of its phase that did not pass, whole.
    /// </summary>
    private void Keep(LegRunResult result, string stepDirectory, bool redLogs)
    {
        fileSystem.CreateDirectory(stepDirectory);
        fileSystem.WriteAllTextAtomic(Path.Combine(stepDirectory, OrchestratorLayout.StepLedgerFileName), result.ToDocument(path => path) + "\n");

        if (!redLogs)
        {
            return;
        }

        // The logs of this attempt's red legs alone: an attempt before it kept its own, of legs that may be green now.
        var logs = Path.Combine(stepDirectory, OrchestratorLayout.StepLogsDirectoryName);

        fileSystem.DeleteDirectory(logs);

        foreach (var line in result.Report.Lines.Where(line => line.HostRunId is null && FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.Red))
        {
            if (line.TailPhase is not { } phase)
            {
                continue;
            }

            // Found as every log is named for its phase - a runner's step 'build (2/2)' is kept as 'build (2-2)'.
            var named = PhaseLogs.NameFor(phase);

            if (line.Logs.FirstOrDefault(log => log.Phase == named) is not { } log || !fileSystem.FileExists(log.File))
            {
                continue;
            }

            fileSystem.CreateDirectory(logs);
            fileSystem.CopyFile(log.File, Path.Combine(logs, $"{line.Leg}.{named}{PhaseLogs.Extension}"), overwrite: true);
        }
    }

    /// <summary>Tells the engine what the leg service says of the step's legs, in the engine's words, and keeps the legs placed.</summary>
    private sealed class StepWatch(IFlowRunEvents events) : ILegRunWatch
    {
        private IReadOnlyList<string> _placed = [];

        /// <summary>Every leg the run placed, by name; none before it placed them.</summary>
        public IReadOnlyList<string> PlacedLegs => Volatile.Read(ref _placed);

        /// <inheritdoc/>
        public void Placed(IReadOnlyList<LegPlace> legs)
        {
            Volatile.Write(ref _placed, [.. legs.Select(leg => leg.Leg)]);

            foreach (var leg in legs)
            {
                events.LegPlaced(leg.Leg, leg.InACopy);
            }

            events.EveryLegPlaced();
        }

        /// <inheritdoc/>
        public void CopyMade(string leg) => events.LegCopyMade(leg);

        /// <inheritdoc/>
        public void Ended(string leg) => events.LegEnded(leg);
    }
}

/// <summary>The <c>build</c> step: its legs built through <see cref="IBuildLegs"/>, as the <c>build</c> command builds them.</summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="builds">Builds the legs.</param>
public sealed class BuildStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, IBuildLegs builds)
    : LegStepRunner(contextLoader, platform, store, fileSystem)
{
    /// <inheritdoc/>
    public override string Type => "build";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, CancellationToken cancellationToken)
        => builds.RunAsync(new BuildLegsRequest { Directory = tree, LegNames = LegsOf(run.Step), Watch = watch }, RunId.New(), cancellationToken);
}

/// <summary>
/// The <c>test</c> step: its legs built, unless it says <c>skipBuild</c>, and tested through <see cref="ITestLegs"/>, as
/// the <c>test</c> command does - with the filter, exclusions and labels its keys filled to, each left out where it
/// filled to nothing.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="tests">Tests the legs.</param>
public sealed class TestStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ITestLegs tests)
    : LegStepRunner(contextLoader, platform, store, fileSystem)
{
    /// <inheritdoc/>
    public override string Type => "test";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, CancellationToken cancellationToken)
        => tests.RunAsync(
            new TestLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                Filter = run.Step.Filter,
                Excludes = run.Step.Exclude,
                Labels = run.Step.Labels,
                SkipBuild = run.Step.SkipBuild == true,
                Watch = watch,
            },
            RunId.New(),
            cancellationToken);
}

/// <summary>
/// The <c>run</c> step: its runner run on its legs through <see cref="IRunnerLegs"/>, as the <c>run</c> command runs it,
/// with the inputs its keys filled to. A run that writes the tree (<see cref="Configuration.HarnessConfig.WritingRunners"/> names any) works in
/// the tree itself, as the step's claim of write on it says; the command's refusals of where it would write are the
/// step's too.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="runners">Runs the runner on the legs.</param>
public sealed class RunStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, IRunnerLegs runners)
    : LegStepRunner(contextLoader, platform, store, fileSystem)
{
    /// <inheritdoc/>
    public override string Type => "run";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, CancellationToken cancellationToken)
    {
        var runner = run.Step.Runner
            ?? throw new HarnessException(HarnessExit.ConfigInvalid, $"'{run.Instance}' is a run step that names no runner");

        return runners.RunAsync(
            new RunnerLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                Runner = runner,
                Inputs = run.Step.Inputs,
                InPlace = context.Config.WritingRunners(runner).Count > 0,
                Watch = watch,
            },
            RunId.New(),
            cancellationToken);
    }
}

/// <summary>
/// The <c>check-mutations</c> step: the arms it names - every arm the registry declares where it names none - swept on
/// its legs through <see cref="IMutationLegs"/>, as <c>check-mutations</c> sweeps them; with <c>pending</c>, on each leg
/// only the arms with no recorded pass that still holds. Each red entry names its arm.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="mutations">Sweeps the arms on the legs.</param>
public sealed class SweepStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, IMutationLegs mutations)
    : LegStepRunner(contextLoader, platform, store, fileSystem)
{
    /// <inheritdoc/>
    public override string Type => "check-mutations";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, CancellationToken cancellationToken)
        => mutations.RunAsync(
            new MutationLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                ArmNames = run.Step.Arms.Count == 0 ? null : run.Step.Arms,
                Pending = run.Step.Pending == true,
                Watch = watch,
            },
            RunId.New(),
            cancellationToken);
}
