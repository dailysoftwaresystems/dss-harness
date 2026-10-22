using System.Text.Json.Nodes;
using DssHarness.Core.Build;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Testing;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The <c>orient</c> step: what the run's repository is and has, read once at the start of the flow, so later steps
/// decide by its output - the branch and the one its pull request goes to, the commit the run started on, a remote,
/// legs and how each tests, anchors, CI, mutation arms - with what is dirty, which agents are open and who holds the
/// main tree now. Its checkout is read by the probe the run's start reads (<see cref="FlowCheckout"/>), and each fact by
/// the owner of its rule.
/// </summary>
/// <param name="contextLoader">Finds the repository and its configuration.</param>
/// <param name="platform">This machine.</param>
/// <param name="gitClient">Reads the checkout.</param>
/// <param name="fileSystem">Reads the mutation registry's file.</param>
/// <param name="store">Reads the orchestrators and their agents.</param>
/// <param name="worktrees">Lists git's worktrees, which name agents beside their records.</param>
/// <param name="runLock">Spells the request for the main tree, as a hold asks it.</param>
/// <param name="treeHolders">Names who holds the main tree now.</param>
public sealed class OrientRunner(
    IHarnessContextLoader contextLoader,
    IHostPlatform platform,
    IGitClient gitClient,
    IFileSystem fileSystem,
    OrchestrationStore store,
    IWorktreeService worktrees,
    RunLock runLock,
    TreeHolders treeHolders) : IFlowStepTypeRunner
{
    /// <inheritdoc/>
    public string Type => "orient";

    /// <inheritdoc/>
    public async Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken)
    {
        var (place, context) = await FlowStepRunners.PlaceOf(run, contextLoader, platform, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var root = context.Layout.MainCheckoutRoot;
        var checkout = await FlowCheckout.ReadAsync(gitClient, root, place.Definition, cancellationToken).ConfigureAwait(false);

        // What the output must say is there, or the step says why it is not: a run commits on a branch, and starts from
        // a commit.
        if (checkout.Branch is not { } branch)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, "the main checkout's HEAD is detached, and a run commits its work on a branch: check out the run's branch again");
        }

        if ((place.StartCommit ?? checkout.Head) is not { } startCommit)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.Refused, $"the main checkout's branch '{branch}' names no commit yet, and a run starts from one: commit first");
        }

        var baseBranch = checkout.BaseBranch(run.Read is { } read && read.TryGetValue("base", out var filled) && !filled.IsMissing ? filled.AsTemplateText() : null);
        var legs = LegSelection.Resolve(config, null).Legs;
        var held = treeHolders.Read(context.Layout, runLock.LocalTreesWhole(Type, RunId.New(), root), [], []);

        if (held.Unread is { } unread)
        {
            return FlowStepResult.Ended(passed: false, HarnessExit.CommandFailed, $"who holds the main tree could not be read: {unread}");
        }

        var listed = await worktrees.ListAsync(root, cancellationToken).ConfigureAwait(false);

        var output = new JsonObject
        {
            ["branch"] = branch,
            ["baseBranch"] = baseBranch,
            ["startCommit"] = startCommit,
            ["hasRemote"] = checkout.HasRemote,
            ["hasLegs"] = legs.Count > 0,
            ["legCount"] = legs.Count,
            ["anchors"] = Anchors.AnchorCitationService.RootsOf(config.Anchors).Count > 0,

            // CI is read on the branch the pull request goes to, so with none there is nothing it could be read on.
            ["ciConfigured"] = config.Ci.Unset().Count == 0 && baseBranch is not null,
            ["hasMutations"] = MutationService.Declares(context, fileSystem),
            ["tests"] = new JsonArray([.. legs.Select(leg => TestsOf(config, leg)).OfType<JsonNode>()]),
            ["dirtyPaths"] = new JsonArray([.. checkout.Dirty.Select(path => (JsonNode)path)]),
            ["openAgents"] = new JsonArray([.. store.Orchestrators(context.Layout)
                .SelectMany(orchestrator => OrchestrationRules.OpenAgents(orchestrator.Name, store.Agents(orchestrator), listed.Select(worktree => worktree.Name))
                    .Select(agent => (JsonNode)new JsonObject { ["run"] = orchestrator.Name, ["agent"] = agent }))]),
            ["inFlight"] = new JsonArray([.. held.Holders.Select(holder => (JsonNode)holder.Said)]),
        };

        return FlowStepResult.Ended(passed: true, HarnessExit.Success, output: output);
    }

    /// <summary>
    /// How <paramref name="leg"/> tests, where it does - its test invocation as <see cref="TestInvocationResolver"/>
    /// merges it for the leg's operating system: the program, the option a filter is given through, the exclusions its
    /// arguments apply and those a leg on a host leaves out - so a planner writes a filter in what the leg's runner takes;
    /// null for a leg that runs no tests.
    /// </summary>
    private static JsonObject? TestsOf(Configuration.HarnessConfig config, SelectedLeg leg)
    {
        if (TestInvocationResolver.SettingsFor(config, leg.Leg, VariantKey.ProjectFor(config, leg.Leg)) is not { } settings
            || TestInvocationResolver.InvocationFor(settings, leg.Leg.Os) is not { Runner.Length: > 0 } invocation)
        {
            return null;
        }

        return new JsonObject
        {
            ["name"] = leg.Name,
            ["program"] = invocation.Runner,
            ["filterArg"] = invocation.FilterArg,
            ["exclude"] = new JsonArray([.. TestInvocationResolver.ExclusionsOf(invocation).Select(name => (JsonNode)name)]),
            ["remoteExcludes"] = new JsonArray([.. (invocation.RemoteExcludes ?? []).Select(name => (JsonNode)name)]),
        };
    }
}
