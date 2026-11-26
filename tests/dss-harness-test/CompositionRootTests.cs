using DssHarness.Core.Application;
using DssHarness.Core.Commands;
using DssHarness.Core.Composition;
using DssHarness.Core.Execution;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Secrets;
using DssHarness.Core.Sync;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The tool's own registrations build, and build whole: every service's collaborators are registered, which is what
/// lets the hidden commands serve - and a front end of its own begin the same operations - without a second root.
/// </summary>
public sealed class CompositionRootTests
{
    /// <summary>
    /// The root hands the operation's output to the process table it builds: what a stop of the program the table is read
    /// through could not reach or end is said under the tool, naming the reading, once (<see cref="UnreachedSaying"/>).
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRoot_HandsTheOperationsOutput_ToTheProcessTable(bool stopped)
    {
        var harness = new HarnessFactory();

        var (said, _) = await UnreachedSaid.ByAsync(
            harness,
            stopped,
            async (stop, token) =>
            {
                await using var provider = Root(harness, UnreachingRunner.For(stop), HostDoubles.Platform(PlatformId.Windows));

                await provider.GetRequiredService<IProcessTable>().ReadAsync(token);
            });

        Assert.Equal([UnreachedSaid.Warned(ToolPackage.Command, "PowerShell reading the process table")], said);
    }

    /// <summary>
    /// The root hands the operation's output to the isolation platform it builds, and its own seams of the machine: what a
    /// stop of the isolation probe could not reach or end is said under the tool, naming the probe's program, once
    /// (<see cref="UnreachedSaying"/>); a trial whose end could not is the probe's report, said nowhere else.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheRoot_HandsTheOperationsOutput_ToTheIsolationPlatform(bool stopped)
    {
        using var tree = new TempDirectory();
        var harness = new HarnessFactory();
        var mac = new MacSandboxHost(path => path == SandboxExec.ProgramPath, () => false, () => "15.3", path => path);
        IsolationAvailability? proved = null;

        var (said, _) = await UnreachedSaid.ByAsync(
            harness,
            stopped,
            async (stop, token) =>
            {
                await using var provider = Root(harness, UnreachingRunner.For(stop), new SaidPlatform(PlatformId.MacOs), mac);

                proved = await provider.GetRequiredService<IIsolationPlatform>().ProbeAsync(tree.Path, token);
            });

        Assert.Equal(stopped ? [UnreachedSaid.Warned(ToolPackage.Command, "the isolation probe's '/bin/sh'")] : [], said);
        Assert.Equal(
            stopped ? null : $"macOS 15.3's sandbox ('{SandboxExec.ProgramPath}') did not prove itself: its probe ended, and {UnreachingRunner.Unreached}",
            proved?.Unavailable);
    }

    /// <summary>
    /// The root builds with every dependency present, and resolves the services the two hidden commands serve: today
    /// only process tests catch a missing registration, which is a slow way to learn of one.
    /// </summary>
    [Fact]
    public void TheRootBuildsWhole_AndServesTheHiddenCommands()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.NotNull(provider.GetRequiredService<HostAgentService>());
        Assert.NotNull(provider.GetRequiredService<HoldAwakeService>());
        Assert.NotNull(provider.GetRequiredService<IHarnessContextLoader>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowValidationService>());
        Assert.NotNull(provider.GetRequiredService<IWorkflowExportService>());
    }

    /// <summary>
    /// The process runner and the process table resolve from the root, each once and within a bound: the machine's table
    /// is built over the runner, so a runner the root built with a table of the root's would be a runner waiting on
    /// itself, and every command that runs a program would hang before its first line rather than fail.
    /// </summary>
    [Fact]
    public async Task TheProcessRunnerAndTable_ResolveFromTheRoot_WithoutWaitingOnEachOther()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var resolved = Task.Run(
            () => (provider.GetRequiredService<IProcessRunner>(), provider.GetRequiredService<IProcessTable>()),
            TestContext.Current.CancellationToken);

        var (runner, table) = await resolved.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.IsType<ProcessRunner>(runner);
        Assert.NotNull(table);
    }

    /// <summary>
    /// Every use case of the core resolves from the root - each interface that is one, and each class that is one under
    /// no interface of its own - read from the assembly, not listed by hand: a front end reaches the core through these,
    /// and one the root cannot build is a command no front end can run.
    /// </summary>
    [Fact]
    public void EveryUseCaseOfTheCore_ResolvesFromTheRoot()
    {
        using var provider = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        var useCases = typeof(IUseCase).Assembly.GetExportedTypes()
            .Where(type => type != typeof(IUseCase) && typeof(IUseCase).IsAssignableFrom(type))
            .Where(type => type.IsInterface
                || (!type.IsAbstract && !type.GetInterfaces().Any(contract => contract != typeof(IUseCase) && typeof(IUseCase).IsAssignableFrom(contract))))
            .ToList();

        Assert.NotEmpty(useCases);
        Assert.All(useCases, type => Assert.NotNull(provider.GetRequiredService(type)));
    }

    /// <summary>
    /// The services the commands that run no legs already called are use cases a front end may resolve - init, sync,
    /// host-exec and update-tool - with no wrapper of their own between it and them.
    /// </summary>
    [Fact]
    public void TheServicesInitSyncHostExecAndUpdateToolCall_AreUseCases()
    {
        Assert.All(
            [typeof(InitService), typeof(ISyncService), typeof(HostExecService), typeof(ToolUpdateService)],
            type => Assert.True(typeof(IUseCase).IsAssignableFrom(type), $"{type.Name} is no use case"));
    }

    /// <summary>
    /// The agent runner is the engine's own service, never a use case: its request carries what the engine built for a
    /// step out of the machine's seams - the codebase graph's tool among it - which no front end builds, so a front end
    /// reaches an agent only through the use cases that drive one, an orchestrator's run and a side-task.
    /// </summary>
    [Fact]
    public void TheAgentRunner_IsTheEnginesOwnService_NeverAUseCase()
    {
        Assert.False(typeof(IUseCase).IsAssignableFrom(typeof(Core.Agents.IAgentRunner)));
        Assert.True(typeof(IUseCase).IsAssignableFrom(typeof(IFlowService)));
        Assert.True(typeof(IUseCase).IsAssignableFrom(typeof(Core.Agents.SideTasks.ISideTaskService)));
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
                OperationId.New(),
                output: _ => new EventHarnessOutput(new TextRunEvents(TextWriter.Null, TextWriter.Null, verbose: false)),
                prompt: _ => Substitute.For<ISuperuserPrompt>())
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });

        Assert.IsNotType<ConsoleHarnessOutput>(provider.GetRequiredService<IHarnessOutput>());
        Assert.NotNull(provider.GetRequiredService<ISuperuserPrompt>());
    }

    /// <summary>
    /// The root as a command builds it, its output <paramref name="harness"/>'s, its programs run by
    /// <paramref name="runner"/> - which starts nothing - on <paramref name="platform"/>, and macOS's sandbox standing as
    /// <paramref name="mac"/> says where one is given.
    /// </summary>
    private static ServiceProvider Root(HarnessFactory harness, IProcessRunner runner, IHostPlatform platform, MacSandboxHost? mac = null)
    {
        var services = new ServiceCollection()
            .AddDssHarness(new SessionOptions { Command = "test" }, OperationId.New(), _ => harness.Output)
            .Replace(ServiceDescriptor.Singleton(runner))
            .Replace(ServiceDescriptor.Singleton(platform));

        if (mac is not null)
        {
            services.Replace(ServiceDescriptor.Singleton(mac));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
