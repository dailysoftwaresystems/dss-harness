using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The runner of one type of step: a thin adapter over the service that owns the step's rule, asked one instance at a
/// time by <see cref="FlowStepRunners"/>.
/// </summary>
public interface IFlowStepTypeRunner
{
    /// <summary>The step type it runs, as a workflow spells it.</summary>
    string Type { get; }

    /// <summary>Runs one instance of a step of its type.</summary>
    /// <param name="run">The step, the instance, the run it belongs to and what the engine read for it.</param>
    /// <param name="cancellationToken">Stops the work, as <see cref="IFlowStepRunner.RunAsync"/> says.</param>
    Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken);
}

/// <summary>
/// The runner of a step type whose steps judge a reason step's answer - a <c>validate-plan</c> step a plan's work items, a
/// <c>class-closure</c> step a builder's closing record (<see cref="WorkflowStepTypes.StepType.JudgesAnswer"/>) - and so
/// can say, at the answer itself, what such a step will find wrong with it: the agent is handed each problem in its own
/// conversation, as a schema's is, and puts it right before anything after the answer is paid for
/// (<see cref="FlowStepRun.AnswerChecks"/>). The step itself still runs at its own moment, records what it finds and gates
/// as it always did.
/// </summary>
public interface IFlowAnswerCheckRunner
{
    /// <summary>
    /// What a step of this type, run as <paramref name="check"/> is - its read holding what it reads of the answer - finds
    /// wrong that the answer's agent can put right: the problems its own run would answer, by the one judgement that run
    /// makes; none where it finds none, or where only its own moment can say.
    /// </summary>
    /// <param name="check">The check's run, as the engine would hand it the run as it stands, with what it reads of the answer.</param>
    /// <param name="cancellationToken">Stops the check.</param>
    Task<IReadOnlyList<string>> AnswerProblemsAsync(FlowStepRun check, CancellationToken cancellationToken);
}

/// <summary>
/// The runner a run's steps are given: each step to the runner of its type, and a step of a type no runner serves yet
/// refused loudly as <see cref="NoStepRunner"/> refuses it - a run that says what it cannot do, never one that passes a
/// step over. The view of the run's codebase graph of a step that reads it (<see cref="ReadsViewAtStart"/>) is synced
/// where it starts, before its runner is asked; and a reason step whose answer the flow's checks judge is handed, before
/// its runner is asked, what those checks find wrong with an answer, each asked of the runner of its own type
/// (<see cref="IFlowAnswerCheckRunner"/>).
/// </summary>
public sealed class FlowStepRunners : IFlowStepRunner, IFlowStreamAccount
{
    /// <summary>The step types this build serves, in the order help says them: the tool registers a runner for each, and none other.</summary>
    public static IReadOnlyList<string> Served { get; } =
        ["orient", "graphify-codebase", "validate-plan", "create-agent", "reason", "build", "test", "run", "check-mutations", "anchor-balance", "anchor-citations", "class-closure", "route-findings", "ci-read", "ci-wait", "fold", "delete-agent", "refresh", "rebase", "commit", "push", "pull-request"];

    /// <summary>
    /// The step types whose view of the run's codebase graph is synced where they start, before their runner is asked: a
    /// reason step, served the graph as it works, and a class-closure check, which reads what the agent's worktree still
    /// declares from its view after the work its builder did since that view was last synced.
    /// </summary>
    public static IReadOnlyList<string> ReadsViewAtStart { get; } = ["reason", "class-closure"];

    private readonly IReadOnlyDictionary<string, IFlowStepTypeRunner> _byType;
    private readonly FlowGraphSyncs _graph;
    private readonly NoStepRunner _none = new();

    /// <summary>Serves each step type by the runner registered for it.</summary>
    /// <param name="runners">The runners, one for each type they serve.</param>
    /// <param name="graph">The engine's own syncs of a run's codebase graph: the view of a step that reads it where it starts.</param>
    /// <exception cref="ArgumentException">Two runners serve one type.</exception>
    public FlowStepRunners(IEnumerable<IFlowStepTypeRunner> runners, FlowGraphSyncs graph)
    {
        ArgumentNullException.ThrowIfNull(runners);
        ArgumentNullException.ThrowIfNull(graph);

        _graph = graph;

        var byType = new Dictionary<string, IFlowStepTypeRunner>(StringComparer.Ordinal);

        foreach (var runner in runners)
        {
            if (!byType.TryAdd(runner.Type, runner))
            {
                throw new ArgumentException($"two runners serve steps of type '{runner.Type}'", nameof(runners));
            }
        }

        _byType = byType;
    }

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        // A person may have changed the main tree by hand, and an agent its own since its last step: a step that reads the
        // graph's view is served it as it stands where it starts.
        if (run.Step.Type is { } reading && ReadsViewAtStart.Contains(reading, StringComparer.Ordinal))
        {
            await _graph.AtStartAsync(run, cancellationToken).ConfigureAwait(false);
        }

        // The checks that judge the step's answer, each asked by its own type's runner at the answer: the agent is handed
        // what they find in its own conversation, and the check after the answer finds none of it.
        if (run.AnswerChecks is [_, ..] checks && CorrectionsOf(checks, cancellationToken) is { } corrections)
        {
            run = run with { AnswerCorrections = corrections };
        }

        return await (run.Step.Type is { } type && _byType.TryGetValue(type, out var runner)
            ? runner.RunAsync(run, cancellationToken)
            : _none.RunAsync(run, cancellationToken)).ConfigureAwait(false);
    }

    /// <summary>
    /// What <paramref name="checks"/> find wrong with an answer's output: each check run, as at its own start, by the runner
    /// of its type (<see cref="IFlowAnswerCheckRunner"/>), its read holding, under each key it reads of the answer, what the
    /// output holds there (<see cref="FlowAnswerCheck.ReadOf"/>), and its view of the codebase graph synced first where its
    /// type reads one where it starts - the tree as the answer leaves it - every problem any of them finds, in their order,
    /// each said once. Null where no runner of their types judges answers.
    /// </summary>
    /// <param name="checks">The checks that judge the answer.</param>
    /// <param name="cancellationToken">Stops the checks: the reason step's own.</param>
    private Func<System.Text.Json.Nodes.JsonNode, Task<IReadOnlyList<string>>>? CorrectionsOf(IReadOnlyList<FlowAnswerCheck> checks, CancellationToken cancellationToken)
    {
        List<(FlowAnswerCheck Check, IFlowAnswerCheckRunner Runner)> judging =
        [
            .. checks
                .Select(check => (Check: check, Runner: check.Run.Step.Type is { } type && _byType.TryGetValue(type, out var runner) ? runner as IFlowAnswerCheckRunner : null))
                .Where(pair => pair.Runner is not null)
                .Select(pair => (pair.Check, pair.Runner!)),
        ];

        if (judging.Count == 0)
        {
            return null;
        }

        return async output =>
        {
            var problems = new List<string>();

            foreach (var (check, runner) in judging)
            {
                var run = check.Run with { Read = check.ReadOf(output) };

                if (ReadsViewAtStart.Contains(run.Step.Type, StringComparer.Ordinal))
                {
                    await _graph.AtStartAsync(run, cancellationToken).ConfigureAwait(false);
                }

                problems.AddRange(await runner.AnswerProblemsAsync(run, cancellationToken).ConfigureAwait(false));
            }

            return [.. problems.Distinct(StringComparer.Ordinal)];
        };
    }

    /// <inheritdoc/>
    public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        => run.Step.Type is { } type && _byType.TryGetValue(type, out var runner) && runner is IFlowHandoffRunner handoff
            ? handoff.HandoffAsync(run, cancellationToken)
            : _none.HandoffAsync(run, cancellationToken);

    /// <inheritdoc/>
    public string? Account(FlowStepRun run)
        => run.Step.Type is { } type && _byType.TryGetValue(type, out var runner) && runner is IFlowHandoffRunner handoff
            ? handoff.Account(run)
            : _none.Account(run);

    /// <inheritdoc/>
    public async Task<FlowCutAttempt?> AttemptOf(FlowStepRun run, CancellationToken cancellationToken)
        => run.Step.Type is { } type && _byType.TryGetValue(type, out var runner) && runner is IFlowHandoffRunner handoff
            ? await handoff.AttemptOf(run, cancellationToken).ConfigureAwait(false)
            : null;

    /// <summary>
    /// The run <paramref name="run"/>'s step belongs to, and the repository's context loaded from its main checkout, as
    /// every command over the orchestrator loads it.
    /// </summary>
    /// <param name="run">The step's run.</param>
    /// <param name="contextLoader">Finds the repository and its configuration.</param>
    /// <param name="platform">This machine.</param>
    /// <param name="cancellationToken">Cancels the load.</param>
    /// <exception cref="HarnessException">
    /// The run is held in memory alone, with no repository to work in (<see cref="HarnessExit.InternalError"/>); or the
    /// repository could not be loaded.
    /// </exception>
    public static async Task<(FlowRunPlace Place, HarnessContext Context)> PlaceOf(
        FlowStepRun run,
        IHarnessContextLoader contextLoader,
        IHostPlatform platform,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var place = run.Place
            ?? throw new HarnessException(HarnessExit.InternalError, $"'{run.Instance}' is a {run.Step.Type} step of a run held in memory alone, which has no repository for it to work in");

        return (place, await MainTree.LoadAsync(contextLoader, platform, place.Repository, cancellationToken).ConfigureAwait(false));
    }
}
