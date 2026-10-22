using System.Text.Json.Nodes;
using DssHarness.Core.Application;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>create-agent</c> step, and the making of every agent a run makes: the agent the engine named for the step,
/// made through <see cref="IAgentService.CreateAsync(AgentCreateRequest, CancellationToken)"/> for the model written beside its name, with a worktree - or, for
/// a read-only reason step, with none (<see cref="AgentCreateRequest.NoWorktree"/>). A step run again is not simply
/// made again: the agent's record and seed are read first - none, and it is made; whole, it is done; made part way
/// with the commit its worktree was made from recorded, its seeding is finished; made part way with no such commit, it
/// is deleted abandoned, and closed - its removal begun - its removal is finished, and the step fails either way, for
/// the name it was given is then spent, as it is where the agent was deleted (part 04, "Resume").
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="store">Reads the agent's record and seed.</param>
/// <param name="agents">Makes, seeds and deletes agents.</param>
public sealed class CreateAgentRunner(IHarnessContextLoader contextLoader, IHostPlatform platform, OrchestrationStore store, IAgentService agents) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "create-agent";

    /// <inheritdoc/>
    public Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
        => MakeAsync(run, noWorktree: false, cancellationToken);

    /// <summary>
    /// Makes the agent <paramref name="run"/>'s step was named (<see cref="FlowStepRun.Made"/>), or finishes making it:
    /// with a worktree, handed nothing where the step says <c>empty</c>, or with none where <paramref name="noWorktree"/>.
    /// Its output names the agent.
    /// </summary>
    /// <param name="run">The step, with the agent the engine named for it.</param>
    /// <param name="noWorktree">Whether the agent reads the main tree and has no worktree, as a read-only reason step's does.</param>
    /// <param name="cancellationToken">Cancels what has not begun writing; the service sees a making through once it has.</param>
    public async Task<FlowStepResult> MakeAsync(FlowStepRun run, bool noWorktree, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);

        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);

        if (run.Made is not { } made)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.InternalError, $"'{run.Instance}' makes an agent, and the engine named none for it");
        }

        if (made.Model is not { } model)
        {
            return FlowStepResult.Ended(
                passed: false,
                HarnessExit.ConfigInvalid,
                $"'{run.Instance}' makes agent '{made.Name}', and no model resolves for it: it is made for the model of the first reason step that works in it, or the default tier's first; name a tier under defaults.agentTier, or a model on that step");
        }

        var layout = OrchestratorLayout.Of(context.Layout, place.Run);
        var empty = run.Step.Empty == true;
        var record = store.ReadAgent(layout, made.Name);
        var unfinished = record is { State: AgentStates.Live, Reads: false }
            && AgentService.MakingUnfinished(record, store.ReadSeed(layout, made.Name)) is not null;

        if (unfinished && record!.Base is not null)
        {
            // Its worktree is there and its base recorded: its seeding is what did not finish, and is finished now -
            // over whatever of it the cut-off making wrote.
            var seeded = await agents.SeedAsync(new AgentSeedRequest(place.Repository, place.Run, made.Name, empty, Force: true), cancellationToken).ConfigureAwait(false);

            return seeded.Outcome.Succeeded
                ? Made(made.Name)
                : FlowStepResult.Ended(passed: false, seeded.Outcome.ExitCode, $"agent '{made.Name}' of '{place.Run}' was made part way, and finishing its seeding did not succeed: {seeded.Outcome.Message}");
        }

        if (record is { State: AgentStates.Deleted })
        {
            return Spent($"agent '{made.Name}' of '{place.Run}' was deleted, and an agent's name is never given twice; the step's next attempt makes a new agent");
        }

        if (unfinished || record is { State: AgentStates.Closed })
        {
            // Made part way with nothing of it measurable, it cannot be finished and is dropped; closed, its removal began
            // and is what is finished. Either way it is never the step's agent, and its name is spent with it.
            var why = unfinished
                ? "was made part way and recorded no commit its worktree was made from"
                : "was closed, its worktree's removal begun";
            var deleted = await agents.DeleteAsync(
                new DeleteAgentRequest(place.Repository, place.Run, made.Name, new FoldAllowances(), Apply: true, DiscardUncommitted: true),
                cancellationToken).ConfigureAwait(false);

            return deleted.Outcome.Succeeded
                ? Spent($"agent '{made.Name}' of '{place.Run}' {why}, so it was deleted{(unfinished ? ", its work abandoned" : string.Empty)}; its name is spent, and the step's next attempt makes a new agent")
                : FlowStepResult.Ended(passed: false, deleted.Outcome.ExitCode, $"agent '{made.Name}' of '{place.Run}' {why}, and deleting it did not finish: {deleted.Outcome.Message}");
        }

        // None, and it is made; one there, whole, is answered as already made where it is what the step asked for, and
        // refused, saying what it is, where it is not. A whole one is never handed anything again.
        var created = await agents.CreateAsync(
            new AgentCreateRequest(place.Repository, place.Run, made.Name, model, Empty: record is null && empty, Session: null, NoWorktree: noWorktree),
            cancellationToken).ConfigureAwait(false);

        return created.Outcome.Succeeded
            ? Made(made.Name)
            : FlowStepResult.Ended(passed: false, created.Outcome.ExitCode, created.Outcome.Message);
    }

    /// <summary>The step's ending where the agent it was named can never be its agent: it fails, and the name is spent.</summary>
    private static FlowStepResult Spent(string why)
        => new()
        {
            Asked = null,
            Passed = false,
            Exit = HarnessExit.Refused,
            Error = why,
            NameSpent = true,
        };

    /// <summary>The step's ending where its agent is made: its output names it.</summary>
    private static FlowStepResult Made(string agent)
        => FlowStepResult.Ended(passed: true, HarnessExit.Success, output: new JsonObject { ["agent"] = agent });
}
