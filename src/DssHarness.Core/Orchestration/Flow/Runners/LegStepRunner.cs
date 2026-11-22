using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A step that runs legs - <c>build</c>, <c>test</c>, <c>run</c>, <c>check-mutations</c> - as a thin adapter over the
/// use case its command runs: the legs run in the tree the step works in (the worktree of the agent its <c>in</c>
/// names, else the main checkout); where each leg works, each copy made and each leg ended are told to the engine as the
/// leg service says them, so the step lets its tree go once no leg reads it; each leg's verdict is read on its own, and
/// <c>red</c> holds the red legs alone. Its legs are run as every step of the engine runs legs (<see cref="FlowStepLegs"/>,
/// a reason step's measure calls' owner too): under a process scope recorded in the step's directory, and the ledger kept
/// there, with - for a step in an agent's worktree - the log of each red leg's phase that did not pass, whole, where this
/// machine ran it: that worktree, and the run directory in it, go when the agent does.
/// </summary>
/// <remarks>
/// What its legs build, test and run is what the run's agents wrote: the work of each leg this machine runs is held inside
/// the operating system's isolation of the agents' work, the leg's own (<see cref="ExecutionGrounds.OfLeg"/>), and the step
/// answers the isolation under <c>isolation</c>. Where this machine has none, <c>agentPolicy.isolation: required</c> fails
/// the step 14 with nothing run, and <c>preferred</c> runs its legs outside, each leg's line saying so. A leg another
/// machine runs - an ssh host, a WSL distribution - runs there as that machine's tool runs any leg, outside this machine's
/// isolation: a WSL distribution, which reaches this machine's drives, is decided as the isolation's own absence
/// (<see cref="ExecutionIsolation.InDistribution"/>), and an ssh host is taken to be another machine, for one that is
/// this machine or shares its drives cannot be told by its name or its address.
/// </remarks>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the ledger and the red logs in the step's directory.</param>
/// <param name="children">Records in its directory the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
public abstract class LegStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IAgentIsolation isolation) : IFlowStepTypeRunner
{
    private readonly FlowStepLegs _legs = new(fileSystem, children, isolation);

    /// <summary>Reads the run's records - the agent a step works in, and the folds and commits its ledger attributes to an item.</summary>
    protected OrchestrationStore Store => store;

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

        // A type whose legs work on a copy of their own - a sweep of an item's own arms, on the bytes its fold landed
        // as - makes it here, and it goes when the legs are done with it, however they ended. One that cannot make
        // its copy - no commit of the item's in the ledger, a worktree git refused - is refused, nothing run.
        FlowStepTree? made;

        try
        {
            made = await TreeOfAsync(run, place, context, layout, tree, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            return FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message);
        }

        await using var copy = made;

        if (copy is not null)
        {
            tree = copy.Path;
        }

        // The legs run as every step of the engine runs them: the isolation their work is held inside decided before
        // anything of the step starts - where it is required and this machine has none, no leg runs - under the step's
        // process scope, and the ledger kept in the step's directory, with each red leg's failing log in an agent's worktree.
        var watch = new StepWatch(run.Events);
        var directory = layout.InstanceDirectory(run.Instance, run.WorksIn);
        var ran = await _legs.RunAsync(
            new FlowStepLegsRequest(context.Config, context.Layout.MainCheckoutRoot, tree, directory, directory, RedLogs: run.WorksIn is not null, $"'{run.Instance}'") { WritesBeside = WritesBeside },
            legIsolation => RunLegsAsync(run, context, tree, watch, legIsolation, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (ran.Result is not { } result)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ToolMissing, ran.Refusal!);
        }

        var output = Output(result, arms: Type == "check-mutations");
        output["isolation"] = ran.Isolation;
        var lines = result.Report.Lines;

        // A run something other than its legs ended - refused, cut off, a leg it placed left with no line - ends the step
        // with the run's own code: its ledger holds only what its legs reached, which says nothing of what ended it. A run
        // its legs ended - none could run, another run owns its records - is read by its legs' verdicts like any other.
        if (FlowStepLegs.EndedOutsideItsLegs(result, watch.PlacedLegs))
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
    /// <param name="isolation">Holds the work of each leg this machine runs inside the operating system's isolation.</param>
    /// <param name="cancellationToken">Stops the legs.</param>
    protected abstract Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken);

    /// <summary>
    /// The tree of its own a type's legs work on instead of <paramref name="tree"/> - null for every type but a sweep
    /// that names a commit in <c>base</c>, whose arms are driven on a copy of that commit, so other items' folds never
    /// move the bytes under it. The copy goes when the step is done with it, however its legs ended.
    /// </summary>
    /// <param name="run">The step, its keys filled.</param>
    /// <param name="place">The run and its definition.</param>
    /// <param name="context">The repository and its configuration, as the main checkout reads them.</param>
    /// <param name="layout">The run's layout.</param>
    /// <param name="tree">The tree the step works in as the engine sees it.</param>
    /// <param name="cancellationToken">Stops the making of the copy.</param>
    protected virtual Task<FlowStepTree?> TreeOfAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, OrchestratorLayout layout, string tree, CancellationToken cancellationToken)
        => Task.FromResult<FlowStepTree?>(null);

    /// <summary>
    /// What a leg's work writes inside the tool's state beside its tree and its records, by the tree the leg works in -
    /// the workers a sweep makes its arms in; nothing for every other step.
    /// </summary>
    /// <param name="legTree">The tree the leg works in.</param>
    protected virtual IReadOnlyList<string> WritesBeside(string legTree) => [];

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
                red.Add(Red(line.Leg, arm.Verdict, phase: null, arm.Detail, [], arm.Arm, failedUnits: null));
            }

            // The leg's own entry, where its verdict is its own - it failed outside every arm - and not one of its arms':
            // a sweep's leg is red by the worst of its own verdicts and its arms', and neither hides the other.
            if (!redArms.Any(arm => arm.Verdict == line.Verdict))
            {
                red.Add(Red(line.Leg, line.Verdict, line.TailPhase, line.Detail, line.LogTail, arm: null, line.FailedUnits));
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

        JsonObject Red(string leg, LegVerdict verdict, string? phase, string detail, IReadOnlyList<string> tail, string? arm, FailedUnits? failedUnits)
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
            else
            {
                // The units the leg's build failed, as the ledger's --json says them, where its own tool failed it: the tail
                // holds a build's last lines alone, and a build that went on past its first failure ends far from them.
                entry["failedUnits"] = failedUnits is null ? null : JsonSerializer.SerializeToNode(failedUnits.Document(path => path), ReportJson.Options);
            }

            return entry;
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
/// <param name="children">Records the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="builds">Builds the legs.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
public sealed class BuildStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IBuildLegs builds, IAgentIsolation isolation)
    : LegStepRunner(contextLoader, platform, store, fileSystem, children, isolation)
{
    /// <inheritdoc/>
    public override string Type => "build";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken)
        => builds.RunAsync(
            new BuildLegsRequest { Directory = tree, LegNames = LegsOf(run.Step), KeepGoing = run.Step.KeepGoing == true, Watch = watch, Isolation = isolation },
            RunId.New(),
            cancellationToken);
}

/// <summary>
/// The <c>test</c> step: its legs built, unless it says <c>skipBuild</c>, and tested through <see cref="ITestLegs"/>, as
/// the <c>test</c> command does - with the filter's units the engine read where the step started (an expression's list in
/// the place of the expression; none where its filterWhen read false), and the exclusions and labels its keys filled to,
/// each left out where it filled to nothing.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="children">Records the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="tests">Tests the legs.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
public sealed class TestStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, ITestLegs tests, IAgentIsolation isolation)
    : LegStepRunner(contextLoader, platform, store, fileSystem, children, isolation)
{
    /// <inheritdoc/>
    public override string Type => "test";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken)
        => tests.RunAsync(
            new TestLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                Filters = run.Step.Filter?.Written ?? [],
                Excludes = run.Step.Exclude,
                Labels = run.Step.Labels,
                SkipBuild = run.Step.SkipBuild == true,
                KeepGoing = run.Step.KeepGoing == true,
                Watch = watch,
                Isolation = isolation,
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
/// <param name="children">Records the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="runners">Runs the runner on the legs.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
public sealed class RunStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IRunnerLegs runners, IAgentIsolation isolation)
    : LegStepRunner(contextLoader, platform, store, fileSystem, children, isolation)
{
    /// <inheritdoc/>
    public override string Type => "run";

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken)
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
                KeepGoing = run.Step.KeepGoing == true,
                Watch = watch,
                Isolation = isolation,
            },
            RunId.New(),
            cancellationToken);
    }
}

/// <summary>
/// The <c>check-mutations</c> step: the arms it names - every arm the registry declares where it names none, or those
/// its <c>arms</c> expression yields, the item's own from the record its builder answered - swept on its legs through
/// <see cref="IMutationLegs"/>, as <c>check-mutations</c> sweeps them; with <c>pending</c>, on each leg only the arms
/// with no recorded pass that still holds; and on a copy of the commit its <c>base</c> names where it names one - 'item'
/// is the newest commit the run's ledger gives the item the step is one of - made and taken away under the main tree's
/// lock, so other items' folds never move the bytes under it. Each red entry names its arm.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in, and the run's ledger of its folds and commits.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="children">Records the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="mutations">Sweeps the arms on the legs.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
/// <param name="gitClient">Makes and takes away the copy of the commit the sweep runs on.</param>
public sealed class SweepStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IMutationLegs mutations, IAgentIsolation isolation, IGitClient gitClient)
    : LegStepRunner(contextLoader, platform, store, fileSystem, children, isolation)
{
    /// <inheritdoc/>
    public override string Type => "check-mutations";

    /// <summary>The word a sweep's <c>base</c> names the item's own newest commit by: the newest commit the run's ledger attributes to the item the step is one of.</summary>
    public const string ItemBase = "item";

    /// <inheritdoc/>
    protected override async Task<FlowStepTree?> TreeOfAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, OrchestratorLayout layout, string tree, CancellationToken cancellationToken)
    {
        if (run.Step.Base is not { Length: > 0 } named)
        {
            return null;
        }

        // The item's own newest commit, as the run's ledger attributes its commits to the item the step is one of: a
        // rearm's refold and its commit are the bytes a re-run sweeps, never the first checkpoint alone.
        var commit = string.Equals(named, ItemBase, StringComparison.Ordinal)
            ? NewestCommitOfItem(layout, FlowInstanceId.ItemOf(run.Instance))
            : named;

        if (commit is null)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{run.Instance}' sweeps the arms of the item's own newest commit, and the run's ledger holds no commit of item '{FlowInstanceId.ItemOf(run.Instance)}'. Nothing was swept.");
        }

        var main = context.Layout.MainCheckoutRoot;
        var path = Path.Combine(layout.TempDirectory, $"sweep-{FlowInstanceId.DirectoryOf(run.Instance)}");

        // The copy is made and taken away under the main tree's lock, for its making writes into the repository's git
        // directory as a fold's commit does; the sweep itself runs on the frozen bytes, holding nothing.
        using (await MainTreeWriteLock.HoldAsync(main, cancellationToken).ConfigureAwait(false) ?? throw new OperationCanceledException(cancellationToken))
        {
            var made = await gitClient.RunAsync(main, ["worktree", "add", "--detach", "--force", path, commit], cancellationToken: cancellationToken).ConfigureAwait(false);

            if (!made.Succeeded)
            {
                throw new HarnessException(
                    HarnessExit.CommandFailed,
                    $"'{run.Instance}' could not make its copy of commit '{commit}' to sweep on: {made.FailureMessage}. Nothing was swept.");
            }
        }

        return new FlowStepTree(
            path,
            async () =>
            {
                using (await MainTreeWriteLock.HoldAsync(main, cancellationToken).ConfigureAwait(false))
                {
                    // Best effort: a copy left behind is inside the run's own directory, and goes with the run.
                    _ = await gitClient.RunAsync(main, ["worktree", "remove", "--force", path], cancellationToken: cancellationToken).ConfigureAwait(false);
                }
            });
    }

    /// <summary>The newest commit the run's ledger attributes to a step of <paramref name="item"/>, or null where it holds none of its.</summary>
    private string? NewestCommitOfItem(OrchestratorLayout layout, string item)
    {
        var ledger = Store.ReadFolds(layout);

        return ledger?.Commits
            .Where(commit => commit.Folds.Any(fold => ledger.Of(fold) is { } attributed && FlowInstanceId.ItemOf(attributed.Instance) == item))
            .LastOrDefault()?.Sha;
    }

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken)
        => mutations.RunAsync(
            new MutationLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                ArmNames = FlowStepKeys.NamesOf(run.Step.Arms, run, "arms") is { } arms && arms.Count > 0 ? arms : null,
                Pending = run.Step.Pending == true,
                Watch = watch,
                Isolation = isolation,
            },
            RunId.New(),
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The workers its arms are swept in, kept in the slot for them of the tree the leg works in.</remarks>
    protected override IReadOnlyList<string> WritesBeside(string legTree) => [Path.Combine(legTree, HarnessLayout.MutationsDirectoryName)];
}

/// <summary>
/// A tree of its own that a step's legs work on, made where they start and gone when they are done with it: the copy of
/// a commit an item's own sweep runs on, so other items' folds never move the bytes under it. Its going is best effort -
/// a copy left behind is inside the run's own directory, and goes with the run.
/// </summary>
/// <param name="Path">The copy's directory, a detached worktree of the commit it was made of.</param>
/// <param name="remove">Takes the copy away, called once the step is done with it, however its legs ended.</param>
public sealed class FlowStepTree(string Path, Func<Task> remove) : IAsyncDisposable
{
    /// <summary>The copy's directory.</summary>
    public string Path { get; } = Path;

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await remove().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HarnessException)
        {
            // Best effort: the copy is inside the run's directory, and goes with the run.
        }
    }
}