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

    /// <summary>
    /// The declarations of the run's graph each red leg's digest is joined with before the step's records are kept
    /// (<see cref="ResultDigest.Declared"/>), asked by the files of the tree its places name and answering null where
    /// there are none to give (<see cref="FlowGraphSyncs.DeclarationsAsync"/>): a leg step's whose legs run in the tree the
    /// step works in; none for every other - a sweep on a copy of a commit, which no graph of the run is of, and a measure
    /// call - whose digests' places stay with no declaration.
    /// </summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<Func<string, int, EnclosingDeclaration?>?>>? Declarations { get; init; }
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

        result = await DeclaredAsync(result, request.Declarations, cancellationToken).ConfigureAwait(false);
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
    /// <paramref name="result"/> with each red leg's digest joined to the declarations <paramref name="declarations"/>
    /// gives for the files of the tree its places name (<see cref="ResultDigest.Declared"/>), so the ledger the step keeps
    /// and the entries it answers hold the same; as it was where nothing is asked - no declarations given, a run cut off,
    /// no red leg with a place in the tree, an ask that answers none - and where a stop comes while the graph is asked, for
    /// what the legs reached is kept whatever stops the step after they ended.
    /// </summary>
    private static async Task<LegRunResult> DeclaredAsync(
        LegRunResult result,
        Func<IReadOnlyList<string>, CancellationToken, Task<Func<string, int, EnclosingDeclaration?>?>>? declarations,
        CancellationToken cancellationToken)
    {
        if (declarations is null || result.Cancelled)
        {
            return result;
        }

        var files = result.Report.Lines.Where(Joined).SelectMany(line => line.Digest!.FilesInTree()).Distinct(StringComparer.Ordinal).ToList();

        if (files.Count == 0)
        {
            return result;
        }

        Func<string, int, EnclosingDeclaration?>? declarationAt;

        try
        {
            declarationAt = await declarations(files, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return result;
        }

        return declarationAt is null
            ? result
            : result with { Report = result.Report.WithDigests(line => Joined(line) ? line.Digest!.Declared(declarationAt) : line.Digest) };

        // A red leg's digest is what a model is handed, and what the step's ledger keeps of it.
        static bool Joined(LedgerLine line) => line.Digest is not null && FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.Red;
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

        foreach (var line in result.Report.Lines.Where(line => FlowLegVerdicts.ClassOf(line.Verdict) == FlowLegClass.Red))
        {
            if (FailingLogOf(line) is not { } log || !fileSystem.FileExists(log))
            {
                continue;
            }

            fileSystem.CreateDirectory(logs);
            fileSystem.CopyFile(log, PhaseLogs.OfLeg(logs, line.Leg, line.TailPhase!), overwrite: true);
        }
    }

    /// <summary>
    /// The log of the phase a red leg did not pass, where this machine ran the leg: the one its run's records hold,
    /// found as every log is named for its phase - a runner's step <c>build (2/2)</c> is kept as <c>build (2-2)</c>; null
    /// for a leg a host ran, whose log stays on that host, and for a leg with no failing phase or no log of it.
    /// </summary>
    /// <param name="line">The red leg's line.</param>
    public static string? FailingLogOf(LedgerLine line)
    {
        ArgumentNullException.ThrowIfNull(line);

        return line.HostRunId is null && line.TailPhase is { } phase
            ? line.Logs.FirstOrDefault(log => log.Phase == PhaseLogs.NameFor(phase))?.File
            : null;
    }

    /// <summary>
    /// Where a red leg's failing log is for a reader of the step, as <paramref name="request"/> kept the step's records:
    /// the copy kept beside the step's ledger where the step keeps one - each attempt's own, which outlives the agent's
    /// worktree - else the log its run's records hold; null where neither is on this machine (<see cref="FailingLogOf"/>).
    /// </summary>
    /// <param name="line">The red leg's line.</param>
    /// <param name="request">Where the step keeps its records, and whether it keeps the red legs' logs beside them.</param>
    public string? LogOf(LedgerLine line, FlowStepLegsRequest request)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(request);

        if (FailingLogOf(line) is not { } log)
        {
            return null;
        }

        var kept = PhaseLogs.OfLeg(Path.Combine(request.Records, OrchestratorLayout.StepLogsDirectoryName), line.Leg, line.TailPhase!);

        return request.RedLogs && fileSystem.FileExists(kept) ? kept
            : fileSystem.FileExists(log) ? log
            : null;
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
