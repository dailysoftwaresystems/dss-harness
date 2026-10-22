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
/// The runner a run's steps are given: each step to the runner of its type, and a step of a type no runner serves yet
/// refused loudly as <see cref="NoStepRunner"/> refuses it - a run that says what it cannot do, never one that passes a
/// step over.
/// </summary>
public sealed class FlowStepRunners : IFlowStepRunner
{
    /// <summary>The step types this build serves, in the order help says them: the tool registers a runner for each, and none other.</summary>
    public static IReadOnlyList<string> Served { get; } =
        ["orient", "validate-plan", "create-agent", "build", "test", "run", "check-mutations", "anchor-balance", "anchor-citations", "ci-read", "ci-wait", "fold", "delete-agent", "refresh", "rebase"];

    private readonly IReadOnlyDictionary<string, IFlowStepTypeRunner> _byType;
    private readonly NoStepRunner _none = new();

    /// <summary>Serves each step type by the runner registered for it.</summary>
    /// <param name="runners">The runners, one for each type they serve.</param>
    /// <exception cref="ArgumentException">Two runners serve one type.</exception>
    public FlowStepRunners(IEnumerable<IFlowStepTypeRunner> runners)
    {
        ArgumentNullException.ThrowIfNull(runners);

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
    public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        return run.Step.Type is { } type && _byType.TryGetValue(type, out var runner)
            ? runner.RunAsync(run, cancellationToken)
            : _none.RunAsync(run, cancellationToken);
    }

    /// <inheritdoc/>
    public Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken)
        => _none.HandoffAsync(run, cancellationToken);

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
