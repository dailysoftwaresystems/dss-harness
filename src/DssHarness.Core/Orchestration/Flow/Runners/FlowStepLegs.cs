using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>What a step of the engine asks of the one way it runs legs (<see cref="FlowStepLegs"/>).</summary>
/// <param name="Config">The repository's configuration, as the main checkout reads it.</param>
/// <param name="MainCheckout">The main checkout's root.</param>
/// <param name="Tree">The tree the legs work in: the worktree of the agent the step works in, or the main checkout.</param>
/// <param name="StepDirectory">The step's own directory, where the group of what its legs start bound is recorded while anything is in it.</param>
/// <param name="Records">
/// Where the ledger is kept, with each red leg's failing log where <paramref name="RedLogs"/> says so: the step's own
/// directory for a leg step, a measure call's own folder in it (<see cref="Agents.AgentMeasureSupport.RecordsOf"/>) for
/// a measure.
/// </param>
/// <param name="RedLogs">
/// Whether the log of each red leg's phase that did not pass is kept beside the ledger, whole, where this machine ran the
/// leg: where the step works in an agent's worktree, which goes, with the run directory in it, when the agent does.
/// </param>
/// <param name="Refused">What an isolation's refusal names: <c>'step'</c>, or the measure call and its step.</param>
public sealed record FlowStepLegsRequest(HarnessConfig Config, string MainCheckout, string Tree, string StepDirectory, string Records, bool RedLogs, string Refused)
{
    /// <summary>
    /// What a leg's work writes inside the tool's state beside its tree and its records, by the tree the leg works in -
    /// the workers a sweep makes its arms in; nothing for every other step.
    /// </summary>
    public Func<string, IReadOnlyList<string>> WritesBeside { get; init; } = _ => [];
}

/// <summary>What a step's legs came to: their run, or the isolation's refusal and nothing run, and the isolation the work was decided under.</summary>
/// <param name="Result">What the legs reached; null where the isolation refused them and nothing ran.</param>
/// <param name="Refusal">The isolation's refusal, as a sentence that says nothing was run; null where the legs ran.</param>
/// <param name="Isolation">The isolation the legs' work was decided under, as a step's output says it.</param>
public sealed record FlowStepLegsRan(LegRunResult? Result, string? Refusal, JsonObject Isolation);

/// <summary>
/// The one way a step of the engine runs legs - a leg step's (<see cref="LegStepRunner"/>) and a measure call a reason
/// step's agent makes (<see cref="ReasonStepRunner"/>) alike: the operating system's isolation of the agents' work decided
/// for the tree before anything starts, and where <c>agentPolicy.isolation</c> is <c>required</c> and this machine has
/// none, nothing run; each leg this machine runs held inside the isolation of its own ground
/// (<see cref="ExecutionGrounds.OfLeg"/>); the legs run under a process scope whose records are kept in the step's own
/// directory (<see cref="ChildSessions.For"/>), so what they start bound is apart from the engine's terminal and a killed
/// engine leaves none of it running unrecorded - the next command over the run, a resume's first among them, ends it; and
/// the ledger, as <c>--json</c> gives it, kept where the step keeps the run's records, with each red leg's failing log
/// where the step works in an agent's worktree.
/// </summary>
/// <param name="fileSystem">Keeps the ledger and the red logs.</param>
/// <param name="children">Records in the step's directory the group of what its legs start bound, while anything is in it.</param>
/// <param name="isolation">Decides the isolation the work of the step's legs is held inside.</param>
public sealed class FlowStepLegs(IFileSystem fileSystem, ChildSessions children, IAgentIsolation isolation)
{
    /// <summary>
    /// Runs a step's legs by <paramref name="legs"/>, handed the isolation each leg this machine runs is held inside: the
    /// isolation decided first, the legs under the step's process scope, the ledger kept as <paramref name="request"/> says.
    /// </summary>
    /// <param name="request">The step's tree, its directories and how its isolation's refusal names it.</param>
    /// <param name="legs">Runs the legs through the step's use case, handed the isolation of each leg's work.</param>
    /// <param name="cancellationToken">Stops the decision of the isolation; the legs are stopped by what <paramref name="legs"/> was given.</param>
    public async Task<FlowStepLegsRan> RunAsync(FlowStepLegsRequest request, Func<ILegIsolation, Task<LegRunResult>> legs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(legs);

        using var held = await isolation.ForExecutionAsync(request.Config, ExecutionGrounds.OfLegs(request.Config, request.MainCheckout, request.Tree), request.MainCheckout, cancellationToken).ConfigureAwait(false);

        if (held.Refusal(request.Refused) is { } refused)
        {
            return new FlowStepLegsRan(null, $"{refused}. Nothing was run.", held.Record.ToOutput());
        }

        LegRunResult result;

        // What its legs start here is apart from the engine's terminal, so a first interruption typed there leaves it
        // running for the pause to wait out; and the group of each phase - a set-up's up line apart - a leg's keep-awake and
        // what carries a leg to a host is recorded in the step's directory while anything is in it, so what a killed engine
        // left is ended by the next command over the run, a resume's first among them.
        using (ProcessScope.Enter(new ProcessScope(() => children.For(request.StepDirectory))))
        {
            result = await legs(new StepLegIsolation(isolation, request.Config, request.MainCheckout, request.Tree, request.WritesBeside)).ConfigureAwait(false);
        }

        Keep(result, request.Records, request.RedLogs);

        return new FlowStepLegsRan(result, null, held.Record.ToOutput());
    }

    /// <summary>
    /// Whether something other than the legs ended <paramref name="result"/>: it was cut off, it reached no leg's line, or
    /// it was stopped by a refusal of the run or with a leg it placed left with no line. Its ledger then holds only what
    /// its legs reached, which says nothing of what ended it; a run its legs ended - none could run, another run owns its
    /// records - is read by its legs' verdicts like any other.
    /// </summary>
    /// <param name="result">What the legs reached.</param>
    /// <param name="placed">Every leg the run placed, by name.</param>
    public static bool EndedOutsideItsLegs(LegRunResult result, IReadOnlyList<string> placed)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(placed);

        var lines = result.Report.Lines;

        return result.Cancelled
            || lines.Count == 0
            || result.Stopped && (HarnessExit.RefusesTheRun(result.ExitCode) || placed.Any(leg => !lines.Any(line => line.Leg == leg)));
    }

    /// <summary>
    /// Keeps what the legs reached in <paramref name="records"/>: the ledger, as <c>--json</c> gives it; and, where
    /// <paramref name="redLogs"/>, for each red leg this machine ran, the log of its phase that did not pass, whole.
    /// </summary>
    private void Keep(LegRunResult result, string records, bool redLogs)
    {
        fileSystem.CreateDirectory(records);
        fileSystem.WriteAllTextAtomic(Path.Combine(records, OrchestratorLayout.StepLedgerFileName), result.ToDocument(path => path) + "\n");

        if (!redLogs)
        {
            return;
        }

        // The logs of this run's red legs alone: a run before it kept its own, of legs that may be green now.
        var logs = Path.Combine(records, OrchestratorLayout.StepLogsDirectoryName);

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

    /// <summary>The isolation of the work of one leg of the step, from the leg's own ground (<see cref="ExecutionGrounds.OfLeg"/>).</summary>
    private sealed class StepLegIsolation(IAgentIsolation isolation, HarnessConfig config, string mainCheckout, string tree, Func<string, IReadOnlyList<string>> besides) : ILegIsolation
    {
        /// <inheritdoc/>
        public Task<ExecutionIsolation> ForLegAsync(string legTree, IReadOnlyList<string> writes, CancellationToken cancellationToken)
            => isolation.ForExecutionAsync(config, ExecutionGrounds.OfLeg(config, mainCheckout, tree, legTree, [.. besides(legTree), .. writes]), mainCheckout, cancellationToken);

        /// <inheritdoc/>
        public ExecutionIsolation ForDistribution(string distribution) => ExecutionIsolation.InDistribution(AgentIsolationMode.Of(config), distribution);
    }
}
