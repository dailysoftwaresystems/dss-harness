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
using DssHarness.Core.Worktrees;

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
public abstract class LegStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IAgentIsolation isolation) : IFlowStepTypeRunner, IFlowHandoffRunner
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

        // A type with nothing to run - a sweep whose arms expression yielded none - ends here, done and saying so:
        // nothing is made and no leg is asked.
        if (NothingToRun(run) is { } nothing)
        {
            return nothing;
        }

        // A type whose legs work on a copy of their own - a sweep of an item's own arms, on the bytes its fold landed
        // as - makes it here, and it goes when the legs are done with it, however they ended: a stop or a pause among
        // them, whose cancellation never reaches the taking away. What could not be taken away is said in the step's
        // output. One that cannot make its copy - no commit of the item's in the ledger, a worktree git refused - is
        // refused, nothing run.
        FlowStepTree? copy;

        try
        {
            copy = await TreeOfAsync(run, place, context, layout, tree, cancellationToken).ConfigureAwait(false);
        }
        catch (HarnessException refused)
        {
            return FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message);
        }

        if (copy is null)
        {
            return await RunLegsInAsync(run, context, layout, tree, cancellationToken).ConfigureAwait(false);
        }

        FlowStepResult result;
        string? left;

        try
        {
            result = await RunLegsInAsync(run, context, layout, copy.Path, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            left = await copy.TakeAwayAsync().ConfigureAwait(false);
        }

        if (result.Output is JsonObject said)
        {
            said["copy"] = new JsonObject { ["path"] = copy.Path, ["left"] = left };
        }

        return result;
    }

    /// <summary>Runs the step's legs in <paramref name="tree"/>, as every step of the engine runs legs, and reads what they reached.</summary>
    private async Task<FlowStepResult> RunLegsInAsync(FlowStepRun run, HarnessContext context, OrchestratorLayout layout, string tree, CancellationToken cancellationToken)
    {
        // The legs run as every step of the engine runs them: the isolation their work is held inside decided before
        // anything of the step starts - where it is required and this machine has none, no leg runs - under the step's
        // process scope, and the ledger kept in the step's directory, with each red leg's failing log in an agent's worktree.
        var watch = new StepWatch(run.Events);
        var directory = layout.InstanceDirectory(run.Instance, run.WorksIn);
        var request = new FlowStepLegsRequest(context.Config, context.Layout.MainCheckoutRoot, tree, directory, directory, RedLogs: run.WorksIn is not null, $"'{run.Instance}'") { WritesBeside = WritesBeside };
        var ran = await _legs.RunAsync(
            request,
            legIsolation => RunLegsAsync(run, context, tree, watch, legIsolation, cancellationToken),
            cancellationToken).ConfigureAwait(false);

        if (ran.Result is not { } result)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.ToolMissing, ran.Refusal!);
        }

        var output = Output(result, arms: Type == "check-mutations", line => _legs.LogOf(line, request));
        output["isolation"] = ran.Isolation;
        var lines = result.Report.Lines;

        // What each leg did, for the attempt's measure: however the run ended, every line it kept is the attempt's.
        var account = new FlowStepAttempt(run.Attempt, Legs: [.. lines.Select(FlowStepLeg.Of)]);

        // A run something other than its legs ended - refused, cut off, a leg it placed left with no line - ends the step
        // with the run's own code: its ledger holds only what its legs reached, which says nothing of what ended it. A run
        // its legs ended - none could run, another run owns its records - is read by its legs' verdicts like any other.
        if (FlowStepLegs.EndedOutsideItsLegs(result, watch.PlacedLegs))
        {
            return FlowStepResult.Ended(passed: false, result.ExitCode, result.Message, output) with { Cause = result.Cause, Attempt = account };
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
            Attempt = account,
        };
    }

    /// <summary>No handoff turn of a machine step: its legs keep no conversation, and the engine writes the handoff itself.</summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the turn.</param>
    Task<string?> IFlowHandoffRunner.HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        => Task.FromResult<string?>(null);

    /// <summary>No account of a machine step's stored stream: it keeps none, and the handoff stands on its first line alone.</summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    string? IFlowHandoffRunner.Account(FlowStepRun run) => null;

    /// <summary>
    /// The account of the attempt <paramref name="run"/> names, where a crash cut the machine step off before the engine
    /// read its runner's answer: the ledger its legs' run kept in the step's directory, read back as the cut attempt's own
    /// (<see cref="CutAccountOf"/>) - each line a leg the attempt ran, with its verdict, its time and its digest's counts -
    /// closed where that ledger was written, the latest time the step's own records give.
    /// </summary>
    /// <param name="run">The step, the instance and the attempt the record says was current.</param>
    /// <param name="cancellationToken">Cancels the reading of the repository.</param>
    async Task<FlowCutAttempt?> IFlowHandoffRunner.AttemptOf(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        return CutAccountOf(fileSystem, OrchestratorLayout.Of(context.Layout, place.Run), run);
    }

    /// <summary>
    /// The cut attempt's account out of the ledger a step's directory keeps, <see cref="OrchestratorLayout.StepLedgerFileName"/>
    /// under <paramref name="layout"/>'s directory for the step: each line a leg of the attempt as its measure keeps it
    /// (<see cref="FlowStepLeg.Recorded"/>), and the attempt closed where the ledger was written - the latest time the
    /// step's own records give. Null where the directory holds no ledger, or one that does not read: nothing of the
    /// attempt is taken then, and its run closes where the engine would close it for a step that kept no record of its
    /// own - the run's last, as the resume reads it.
    /// </summary>
    /// <param name="fileSystem">Reads the step's ledger.</param>
    /// <param name="layout">The run's layout, which names the step's directory.</param>
    /// <param name="run">The step, the instance and the attempt the record says was current.</param>
    internal static FlowCutAttempt? CutAccountOf(IFileSystem fileSystem, OrchestratorLayout layout, FlowStepRun run)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(run);

        var ledger = Path.Combine(layout.InstanceDirectory(run.Instance, run.WorksIn), OrchestratorLayout.StepLedgerFileName);

        if (fileSystem.KindOf(ledger) != PathKind.File)
        {
            return null;
        }

        try
        {
            // The ledger as the step's legs' run wrote it, one write at their run's end; a file another run left there is
            // told from this attempt's run by when it was written, which the engine reads from the account it is given.
            var lines = Json.JsonDepth.ParseNode(fileSystem.ReadAllText(ledger)) as JsonObject ?? throw new JsonException("the step's ledger is no JSON object");

            if (lines["legs"] is not JsonArray entries)
            {
                return null;
            }

            return new FlowCutAttempt(
                new FlowStepAttempt(run.Attempt, Legs: [.. entries.OfType<JsonObject>().Select(FlowStepLeg.Recorded)]),
                [],
                new DateTimeOffset(fileSystem.LastWriteTimeUtc(ledger), TimeSpan.Zero));
        }
        catch (Exception failed) when (failed is JsonException or InvalidOperationException or IOException)
        {
            // A ledger that cannot be read is no account of the attempt: nothing of it is taken, not even its time, and
            // the resume is not stopped by a record the crash tore.
            return null;
        }
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
    /// The step's ending where it has nothing to run - null for every type but a sweep whose <c>arms</c> expression
    /// yielded none, which sweeps nothing: an empty list is no arm, never every arm.
    /// </summary>
    /// <param name="run">The step, its keys filled.</param>
    protected virtual FlowStepResult? NothingToRun(FlowStepRun run) => null;

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
    /// The priority a step's legs ask the machine's dispatch queue at (<see cref="FlowUse.PriorityOf"/>): the governing use's,
    /// else the run's; the least a run has for a step run outside any run's definition.
    /// </summary>
    /// <param name="run">The step's run.</param>
    protected static int PriorityOf(FlowStepRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Place is { } place ? FlowUse.PriorityOf(place.Definition, run.DeclarationPath) : DispatchOrder.LeastPriority;
    }

    /// <summary>
    /// What a leg step answers: whether every leg passed, the command's own code, each leg's verdict, the red legs alone -
    /// each with the phase that did not pass, the ledger's line for it, the digest of what it printed with the raw lines
    /// beside it the digest does not hold, and the log that phase wrote, whose lines the digest's ranges number; or of a
    /// sweep each arm that is not as its registry says - and where the run's records are.
    /// </summary>
    /// <param name="result">What the legs reached.</param>
    /// <param name="arms">Whether the step is a sweep, whose red entries each name their arm.</param>
    /// <param name="logOf">Where a red leg's failing log is on this machine; null where it is on none. None where not given.</param>
    internal static JsonObject Output(LegRunResult result, bool arms, Func<LedgerLine, string?>? logOf = null)
    {
        ArgumentNullException.ThrowIfNull(result);

        var lines = result.Report.Lines;
        var red = new JsonArray();

        foreach (var line in lines.Where(line => FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.Red))
        {
            var redArms = arms ? line.Arms.Where(arm => FlowLegVerdicts.ClassOf(arm.Verdict) == FlowLegClass.Red).ToList() : [];

            foreach (var arm in redArms)
            {
                red.Add(Red(line.Leg, arm.Verdict, phase: null, arm.Detail, [], arm.Arm, failedUnits: null, digest: null, log: null));
            }

            // The leg's own entry, where its verdict is its own - it failed outside every arm - and not one of its arms':
            // a sweep's leg is red by the worst of its own verdicts and its arms', and neither hides the other.
            if (!redArms.Any(arm => arm.Verdict == line.Verdict))
            {
                red.Add(Red(line.Leg, line.Verdict, line.TailPhase, line.Detail, line.LogTail, arm: null, line.FailedUnits, line.Digest, logOf?.Invoke(line)));
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

        JsonObject Red(string leg, LegVerdict verdict, string? phase, string detail, IReadOnlyList<string> tail, string? arm, FailedUnits? failedUnits, ResultDigest? digest, string? log)
        {
            // What the phase printed, every line once: the digest, and the raw lines it does not say.
            var handed = ResultDigest.Handed(digest, tail, PhaseParsers.AsRead);
            var entry = new JsonObject
            {
                ["leg"] = leg,
                ["verdict"] = Verdicts.Display(verdict),
                ["phase"] = phase,
                ["detail"] = detail,
                ["tail"] = new JsonArray([.. handed.Lines.Select(line => (JsonNode?)line)]),
                ["digest"] = handed.Digest,

                // The log the phase wrote, whose lines the digest's ranges number: one read away from the entry.
                ["log"] = log,
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

    /// <summary>
    /// Tells the engine what the leg service says of the step's legs, in the engine's words, and keeps the legs placed -
    /// and, from each stage of their admission, whether the machine holds every leg still to run back
    /// (<see cref="StepHold"/>), as the engine counts the step's machine wait by.
    /// </summary>
    internal sealed class StepWatch(IFlowRunEvents events) : ILegRunWatch
    {
        private readonly Lock _gate = new();
        private readonly StepHold _hold = new();
        private IReadOnlyList<string> _placed = [];
        private IReadOnlyList<string> _told = [];
        private IReadOnlyList<QueueAhead> _toldAhead = [];

        /// <summary>Every leg the run placed, by name; none before it placed them.</summary>
        public IReadOnlyList<string> PlacedLegs => Volatile.Read(ref _placed);

        /// <inheritdoc/>
        public void Placed(IReadOnlyList<LegPlace> legs)
        {
            Volatile.Write(ref _placed, [.. legs.Select(leg => leg.Leg)]);

            lock (_gate)
            {
                _hold.Placed(legs.Select(leg => leg.Leg));
            }

            foreach (var leg in legs)
            {
                events.LegPlaced(leg.Leg, leg.InACopy);
            }

            events.EveryLegPlaced();
        }

        /// <inheritdoc/>
        public void CopyMade(string leg) => events.LegCopyMade(leg);

        /// <inheritdoc/>
        public void Ended(string leg)
        {
            Tell(() => _hold.Ended(leg));
            events.LegEnded(leg);
        }

        /// <inheritdoc/>
        public void Admitting(string leg, string asking, AdmissionTold told) => Tell(() => _hold.Admitting(leg, asking, told));

        // Tells the engine what holds the step's legs back, where a change makes it other than it last said - the causes,
        // or who stands ahead in the machine's queue - under one lock, so two legs' stages said at once are told in the
        // order they changed it.
        private void Tell(Action change)
        {
            lock (_gate)
            {
                change();

                var (on, ahead) = _hold.Held();

                if (!on.SequenceEqual(_told) || !ahead.SequenceEqual(_toldAhead))
                {
                    _told = on;
                    _toldAhead = ahead;
                    events.Held(on, ahead);
                }
            }
        }
    }

    /// <summary>
    /// What holds a machine step's legs back, from each stage of their admission: a leg is held while something of it -
    /// itself, or a unit of its work - waits at its machine and nothing of it holds a slot there; the step is held while
    /// every leg it placed that has not ended is held, on what each of them waits on, and - where what they wait on is the
    /// machine's queue - behind who stands ahead there, joined over every unit that waits.
    /// </summary>
    internal sealed class StepHold
    {
        private readonly HashSet<string> _legs = new(StringComparer.Ordinal);
        private readonly HashSet<string> _ended = new(StringComparer.Ordinal);
        private readonly Dictionary<string, (string Leg, AdmissionTold Told)> _asking = new(StringComparer.Ordinal);

        /// <summary>The run placed <paramref name="legs"/>.</summary>
        public void Placed(IEnumerable<string> legs) => _legs.UnionWith(legs);

        /// <summary><paramref name="leg"/> ended: nothing of it waits or holds a slot any more.</summary>
        public void Ended(string leg)
        {
            _ = _ended.Add(leg);

            foreach (var asking in _asking.Where(entry => entry.Value.Leg == leg).Select(entry => entry.Key).ToList())
            {
                _ = _asking.Remove(asking);
            }
        }

        /// <summary><paramref name="asking"/>, of <paramref name="leg"/>, reached what <paramref name="told"/> says of its admission.</summary>
        public void Admitting(string leg, string asking, AdmissionTold told)
        {
            _ = _legs.Add(leg);

            if (told.Stage == AdmissionStage.Ended || _ended.Contains(leg))
            {
                _ = _asking.Remove(asking);
            }
            else
            {
                _asking[asking] = (leg, told);
            }
        }

        /// <summary>
        /// What holds the step's legs back now, read in one pass: what the machine holds every leg still to run back for,
        /// as <see cref="FlowWaits"/> names each, in the order of <see cref="AdmissionStage"/>, and who stands ahead in its
        /// queue, joined over every unit of those legs waiting there - each class once, its tenants in the order each first
        /// stands. Both are empty where a leg of the step runs, or a unit of one, or none is left to: nothing holds the step
        /// back then, whoever stands ahead of a unit of it still in line.
        /// </summary>
        public (IReadOnlyList<string> On, IReadOnlyList<QueueAhead> Ahead) Held()
        {
            var waiting = new HashSet<string>(StringComparer.Ordinal);
            var waits = new SortedSet<AdmissionStage>();
            var ahead = new List<QueueAhead>();

            // Every unit asking is of a leg still to run: a leg's units leave as it ends, and a unit as its admission ends.
            foreach (var (leg, told) in _asking.Values)
            {
                if (told.Stage == AdmissionStage.Taken)
                {
                    return ([], []);
                }

                _ = waiting.Add(leg);
                _ = waits.Add(told.Stage);

                if (told is { Stage: AdmissionStage.Queue, Ahead: { } standing })
                {
                    ahead.Add(standing);
                }
            }

            // A leg still to run with nothing of it waiting runs, or has yet to ask, and the step is not held.
            return _legs.Any(leg => !_ended.Contains(leg) && !waiting.Contains(leg))
                ? ([], [])
                : ([.. waits.Select(Words)], QueueAhead.Joined(ahead));
        }

        /// <summary>The words a record says a wait on <paramref name="stage"/> by.</summary>
        private static string Words(AdmissionStage stage) => stage switch
        {
            AdmissionStage.Queue => FlowWaits.MachineQueue,
            AdmissionStage.Memory => FlowWaits.MachineMemory,
            AdmissionStage.Room => FlowWaits.MachineRoom,
            _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Only a wait's stage is said as a wait."),
        };
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
            new BuildLegsRequest { Directory = tree, LegNames = LegsOf(run.Step), KeepGoing = run.Step.KeepGoing == true, Watch = watch, Isolation = isolation, Priority = PriorityOf(run) },
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
                Priority = PriorityOf(run),
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
                Priority = PriorityOf(run),
            },
            RunId.New(),
            cancellationToken);
    }
}

/// <summary>
/// The <c>check-mutations</c> step: the arms it names - every arm the registry declares where it names none, or those
/// its <c>arms</c> expression yields, the item's own from the record its builder answered, and none where it yields none
/// - swept on its legs through <see cref="IMutationLegs"/>, as <c>check-mutations</c> sweeps them; with <c>pending</c>,
/// on each leg only the arms with no recorded pass that still holds; and on a copy of the commit its <c>base</c> names
/// where it names one - 'item' is the newest commit the run's ledger gives the item the step is one of - made and taken
/// away by the one owner of such a copy (<see cref="IWorktreeService.CreateCopyAsync"/>) under the main tree's lock, so
/// other items' folds never move the bytes under it. Each red entry names its arm.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the record of the agent the step works in, and the run's ledger of its folds and commits.</param>
/// <param name="fileSystem">Keeps the step's records.</param>
/// <param name="children">Records the group of what the step's legs start bound, while anything is in it.</param>
/// <param name="mutations">Sweeps the arms on the legs.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
/// <param name="worktrees">Makes and takes away the copy of the commit the sweep runs on.</param>
public sealed class SweepStepRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IFileSystem fileSystem, ChildSessions children, IMutationLegs mutations, IAgentIsolation isolation, IWorktreeService worktrees)
    : LegStepRunner(contextLoader, platform, store, fileSystem, children, isolation)
{
    /// <inheritdoc/>
    public override string Type => "check-mutations";

    /// <summary>The word a sweep's <c>base</c> names the item's own newest commit by: the newest commit the run's ledger attributes to the item the step is one of.</summary>
    public const string ItemBase = "item";

    /// <summary>The name of the copy a sweep of <paramref name="instance"/> runs on, under the run's temporary directory: one per instance, every attempt's.</summary>
    /// <param name="instance">The sweep's instance.</param>
    public static string CopyNameOf(string instance) => $"sweep-{FlowInstanceId.DirectoryOf(instance)}";

    /// <inheritdoc/>
    /// <remarks>
    /// An <c>arms</c> expression that yielded none names no arm - the record arms nothing, or the repository keeps no
    /// registry - and the step ends done, saying so, nothing made and no leg asked: an empty list is never every arm,
    /// which only a step that names no <c>arms</c> sweeps.
    /// </remarks>
    protected override FlowStepResult? NothingToRun(FlowStepRun run)
        => FlowStepKeys.NamesOf(run.Step.Arms, run, "arms") is { Count: 0 }
            ? FlowStepResult.Ended(
                passed: true,
                HarnessExit.Success,
                output: new JsonObject
                {
                    ["passed"] = true,
                    ["exitCode"] = HarnessExit.Success,
                    ["verdicts"] = new JsonArray(),
                    ["red"] = new JsonArray(),
                    ["runDirectory"] = null,
                    ["swept"] = NothingSwept(run.Step.Arms!.Expression),
                })
            : null;

    /// <summary>What a sweep whose arms named none says in its output, its arms as the step wrote them.</summary>
    /// <param name="expression">The step's arms expression; null where it wrote an empty list out.</param>
    public static string NothingSwept(string? expression)
        => $"no arm: its arms {(expression is null ? "list is empty" : $"'{expression}' yielded none")}, so nothing was swept";

    /// <inheritdoc/>
    protected override async Task<FlowStepTree?> TreeOfAsync(FlowStepRun run, FlowRunPlace place, HarnessContext context, OrchestratorLayout layout, string tree, CancellationToken cancellationToken)
    {
        if (run.Step.Base is not { } written)
        {
            return null;
        }

        // A base the step writes and that names nothing as it starts - a template whose value is blank - is refused, never
        // read as no base at all: that would sweep the main tree as other items' folds move it, not the bytes it names.
        if (string.IsNullOrWhiteSpace(written))
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{run.Instance}' sweeps a copy of the commit its base names, and its base named none as it started: it was blank. Nothing was swept.");
        }

        var named = written.Trim();

        // The item's own newest commit, as the run's ledger attributes its commits to the item the step is one of: a
        // rearm's refold and its commit are the bytes a re-run sweeps, never the first checkpoint alone.
        var commit = string.Equals(named, ItemBase, StringComparison.Ordinal)
            ? NewestCommitOfItem(layout, FlowInstanceId.ItemOf(run.Instance))
            : named;

        if (commit is null)
        {
            throw new HarnessException(
                HarnessExit.Refused,
                $"'{run.Instance}' sweeps the arms of the item's own newest commit, and the run's ledger holds no commit of item '{FlowInstanceId.ItemOf(run.Instance)}' that was made. Nothing was swept.");
        }

        var main = context.Layout.MainCheckoutRoot;
        var path = Path.Combine(layout.TempDirectory, CopyNameOf(run.Instance));

        // The copy is made and taken away under the main tree's lock, for its making writes into the repository's git
        // directory as a fold's commit does; the sweep itself runs on the frozen bytes, holding nothing. The one owner
        // makes it, taking away first a copy an earlier attempt of this instance left - a stop, a pause or a crash.
        using (await MainTreeWriteLock.HoldAsync(main, cancellationToken).ConfigureAwait(false) ?? throw new OperationCanceledException(cancellationToken))
        {
            var made = await worktrees.CreateCopyAsync(main, path, commit, cancellationToken).ConfigureAwait(false);

            if (!made.Succeeded)
            {
                throw new HarnessException(
                    made.Outcome.ExitCode,
                    $"'{run.Instance}' could not make its copy of commit '{commit}' to sweep on: {made.Outcome.Message.TrimEnd('.')}. Nothing was swept.");
            }
        }

        return new FlowStepTree(
            path,
            async () =>
            {
                // Taken away whatever ended the legs - a stop or a pause among them - so nothing of the step's own token
                // reaches it: the lock is waited for, and the removal is never interrupted.
                using (await MainTreeWriteLock.HoldAsync(main, CancellationToken.None).ConfigureAwait(false))
                {
                    var removed = await worktrees.RemoveCopyAsync(main, path).ConfigureAwait(false);

                    return removed.Succeeded ? null : removed.Outcome.Message;
                }
            });
    }

    /// <summary>
    /// The newest commit the run's ledger attributes to a step of <paramref name="item"/> that a commit took - a staged
    /// set, or one git refused, names none - or null where it holds none of its.
    /// </summary>
    private string? NewestCommitOfItem(OrchestratorLayout layout, string item)
    {
        var ledger = Store.ReadFolds(layout);

        return ledger?.Commits
            .Where(commit => commit is { State: FlowCommitEntry.Committed, Sha: not null }
                && commit.Folds.Any(fold => ledger.Of(fold) is { } attributed && FlowInstanceId.ItemOf(attributed.Instance) == item))
            .LastOrDefault()?.Sha;
    }

    /// <inheritdoc/>
    protected override Task<LegRunResult> RunLegsAsync(FlowStepRun run, HarnessContext context, string tree, ILegRunWatch watch, ILegIsolation isolation, CancellationToken cancellationToken)
        => mutations.RunAsync(
            new MutationLegsRequest
            {
                Directory = tree,
                LegNames = LegsOf(run.Step),
                ArmNames = FlowStepKeys.NamesOf(run.Step.Arms, run, "arms"),
                Pending = run.Step.Pending == true,
                Watch = watch,
                Isolation = isolation,
                Priority = PriorityOf(run),
            },
            RunId.New(),
            cancellationToken);

    /// <inheritdoc/>
    /// <remarks>The workers its arms are swept in, kept in the slot for them of the tree the leg works in.</remarks>
    protected override IReadOnlyList<string> WritesBeside(string legTree) => [Path.Combine(legTree, HarnessLayout.MutationsDirectoryName)];
}

/// <summary>
/// A tree of its own that a step's legs work on, made where they start and taken away when they are done with it: the
/// copy of a commit an item's own sweep runs on, so other items' folds never move the bytes under it. What could not be
/// taken away is said in the step's output; an attempt that never took it away - a crash - leaves it for the next
/// attempt's making, which takes it away first, or for the run's deletion.
/// </summary>
/// <param name="Path">The copy's directory, a detached worktree of the commit it was made of.</param>
/// <param name="takeAway">Takes the copy away, called once the step is done with it, however its legs ended; answers what was left, or null.</param>
public sealed class FlowStepTree(string Path, Func<Task<string?>> takeAway)
{
    /// <summary>The copy's directory.</summary>
    public string Path { get; } = Path;

    /// <summary>Takes the copy away, never interrupted; why it could not be, or null where it was.</summary>
    public Task<string?> TakeAwayAsync() => takeAway();
}
