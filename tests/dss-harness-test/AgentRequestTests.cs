using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;
using DssHarness.Core.Anchors;
using DssHarness.Core.LineEndings;
using DssHarness.Core.Mutations;
using DssHarness.Core.Commands;
using DssHarness.Core.Hosts;
using DssHarness.Core.Runs;
using DssHarness.Core.Worktrees;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Tests;

/// <summary>
/// The verbs of an orchestrator's agents and of its orchestrators answer as use cases: one request in, one typed answer
/// out, the facts of the work read off the record rather than the lines - the boundary a front end or a step of a run
/// calls, beside the command line's own positional shape.
/// </summary>
public sealed class AgentRequestTests
{
    /// <summary>
    /// Creating and seeding an agent answer their facts: where the worktree is, what it was made from and handed, and
    /// what a seeding handed - and a refusal answers with none, the outcome alone, rather than half of one.
    /// </summary>
    [Fact]
    public async Task CreatingAndSeedingAnAgent_AnswerTheirFacts()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var head = await kit.Harness.GitClient.ResolveCommitAsync(kit.Main, "HEAD", TestContext.Current.CancellationToken);

        var created = await kit.Harness.AgentService.CreateAsync(new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "ag", "model-b", Empty: true, Session: null), TestContext.Current.CancellationToken);

        Assert.True(created.Outcome.Succeeded, OrchestrationKit.Describe(created.Outcome));
        var facts = created.Facts;
        Assert.NotNull(facts);
        Assert.Equal(
            (OrchestrationKit.Orchestrator, "ag", head, true, (string?)null),
            (facts.Orchestrator, facts.Name, facts.Base, facts.Empty, facts.Session));
        // Compared as the platform compares paths: a drive letter's case names the same worktree.
        PathAssert.Same(kit.Worktree("ag"), facts.Worktree);

        var seeded = await kit.Harness.AgentService.SeedAsync(new AgentSeedRequest(kit.Main, OrchestrationKit.Orchestrator, "ag", Empty: false, Force: false), TestContext.Current.CancellationToken);

        Assert.True(seeded.Outcome.Succeeded, OrchestrationKit.Describe(seeded.Outcome));
        Assert.NotNull(seeded.Facts);
        Assert.Equal("ag", seeded.Facts!.Name);
        Assert.False(seeded.Facts.Empty);

        var unshaped = await kit.Harness.AgentService.CreateAsync(new AgentCreateRequest(kit.Main, OrchestrationKit.Orchestrator, "bad name", "model-b", Empty: false, Session: null), TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, unshaped.Outcome.ExitCode);
        Assert.Null(unshaped.Facts);
    }

    /// <summary>
    /// A dry run of the fold answers its facts whatever form it is asked in: the standing, the consents it lacks and who
    /// holds what its --apply takes, read off one record under text and JSON alike.
    /// </summary>
    [Fact]
    public async Task AFoldAnsweredByRequest_CarriesItsFacts_AsTextAndAsOneDocument()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a file", TestContext.Current.CancellationToken);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "the agent's own\n");

        var asText = await kit.Harness.AgentService.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, "ag", new FoldAllowances(), Apply: false), TestContext.Current.CancellationToken);
        var asDocument = await kit.Harness.AgentService.FoldAsync(new FoldAgentRequest(kit.Main, OrchestrationKit.Orchestrator, "ag", new FoldAllowances(), Apply: false, Json: true), TestContext.Current.CancellationToken);

        Assert.True(asText.Outcome.Succeeded, OrchestrationKit.Describe(asText.Outcome));
        Assert.Equal(RequestStanding.Sound, asText.Facts?.Standing);
        Assert.Equal([], asText.Facts?.Needs);

        // Nobody was asked who holds anything, and nobody does: the holders are read, and hold nothing.
        var holders = asText.Facts?.Holders;
        Assert.NotNull(holders);
        Assert.Empty(holders.Holders);
        Assert.Null(holders.Unread);
        Assert.Null(holders.StandingUnread);
        Assert.Empty(holders.KeptCopies);

        // The document is the whole of standard output; the same facts ride it as ride the text.
        Assert.NotEmpty(asDocument.Outcome.Data);
        Assert.Equal(asText.Facts?.Standing, asDocument.Facts?.Standing);
        Assert.Equal(asText.Facts?.Needs, asDocument.Facts?.Needs);
    }

    /// <summary>
    /// The services that already were use cases are: the composition answers each as one, so a front end collects what a
    /// repository can do without knowing its commands.
    /// </summary>
    [Fact]
    public void TheServicesThatAlreadyWereUseCases_Are()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, Core.Output.OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        foreach (var service in new IUseCase[]
                 {
                     provider.GetRequiredService<IAgentService>(),
                     provider.GetRequiredService<IOrchestratorService>(),
                     provider.GetRequiredService<IWorktreeService>(),
                     provider.GetRequiredService<IAnchorRegistryService>(),
                     provider.GetRequiredService<ILineEndingService>(),
                     provider.GetRequiredService<MutationService>(),
                     provider.GetRequiredService<CleanService>(),
                     provider.GetRequiredService<InitService>(),
                     provider.GetRequiredService<ToolUpdateService>(),
                 })
        {
            Assert.NotNull(service);
        }
    }
}
