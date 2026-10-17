using DssHarness.Core.Application;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The tool's own registrations build, and build whole: every service's collaborators are registered, which is what
/// lets the hidden commands serve - and a front end of its own begin the same operations - without a second root.
/// </summary>
public sealed class CompositionRootTests
{
    /// <summary>
    /// The root builds with every dependency present, and resolves the services the two hidden commands serve: today
    /// only process tests catch a missing registration, which is a slow way to learn of one.
    /// </summary>
    [Fact]
    public void TheRootBuildsWhole_AndServesTheHiddenCommands()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<HostAgentService>());
        Assert.NotNull(provider.GetRequiredService<HoldAwakeService>());
        Assert.NotNull(provider.GetRequiredService<IHarnessContextLoader>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowValidationService>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowExportService>());
    }

    /// <summary>
    /// The front end's own output and prompt are what the operation runs with, in the place of the console's: a
    /// graphical front end begins the same operations without the command line's terminal.
    /// </summary>
    [Fact]
    public void AFrontEndsOwnOutputAndPrompt_AreWhatTheOperationRunsWith()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(
                new SessionOptions { Command = "test" },
                output: _ => new EventHarnessOutput(new TextRunEvents(TextWriter.Null, TextWriter.Null, verbose: false)),
                prompt: _ => Substitute.For<ISuperuserPrompt>())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsNotType<ConsoleHarnessOutput>(provider.GetRequiredService<IHarnessOutput>());
        Assert.NotNull(provider.GetRequiredService<ISuperuserPrompt>());
    }
}
