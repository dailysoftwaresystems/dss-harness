using DssHarness.Core.Anchors;
using DssHarness.Core.Application;
using DssHarness.Core.Application.Legs;
using DssHarness.Core.Build;
using DssHarness.Core.Ci;
using DssHarness.Core.Commands;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.LineEndings;
using DssHarness.Core.Mutations;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Projects;
using DssHarness.Core.Repository;
using DssHarness.Core.Runners;
using DssHarness.Core.Runs;
using DssHarness.Core.Secrets;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Testing;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;
using Microsoft.Extensions.DependencyInjection;

namespace DssHarness.Core.Composition;

/// <summary>
/// The tool's own registrations: every dependency assembled in one place, so that no service constructs its own
/// collaborators and every one of them stays substitutable.
/// </summary>
/// <remarks>
/// The front end that begins a session supplies where it writes and who it may ask; the registrations are the same
/// whatever front end it is, which is what makes a graphical one possible without a second composition root drifting
/// beside this one.
/// </remarks>
public static class ServiceRegistration
{
    /// <summary>
    /// The host of a front end that supplies the one thing the core cannot do for itself - ask for a superuser password
    /// at its own terminal - and nothing else: each operation it begins is given services of its own, built here from
    /// this tool's registrations, written through the console and stamped with that operation's id and its parent's.
    /// </summary>
    /// <param name="prompt">
    /// How a superuser password is asked, given the operation's output and options; <see langword="null"/> for nobody
    /// to ask (<see cref="NobodyToAsk"/>).
    /// </param>
    /// <returns>The host, one per process.</returns>
    /// <remarks>
    /// The one place a provider is built for an operation, so a front end - the command line among them - builds none of
    /// its own and needs no container to begin one.
    /// </remarks>
    public static DssHarnessHost Host(Func<IHarnessOutput, SessionOptions, ISuperuserPrompt>? prompt = null)
        => new((options, operation) => new ServiceCollection()
            .AddDssHarness(
                options,
                operation,
                prompt: prompt is null ? null : provider => prompt(provider.GetRequiredService<IHarnessOutput>(), options))
            .BuildServiceProvider());

    /// <summary>
    /// Registers every service of this tool for one operation, as the options of that operation ask for them.
    /// </summary>
    /// <param name="services">The collection to register them in.</param>
    /// <param name="options">What the operation is asked and how it runs.</param>
    /// <param name="operation">
    /// The operation's id, which every event its output publishes is stamped with, beside its parent
    /// (<see cref="SessionOptions.Parent"/>).
    /// </param>
    /// <param name="output">
    /// Where the operation writes; <see langword="null"/> for the console, which is what the command line supplies -
    /// a front end of its own supplies its own.
    /// </param>
    /// <param name="prompt">
    /// How a superuser password is asked of whoever is there; <see langword="null"/> for nobody to ask
    /// (<see cref="NobodyToAsk"/>), which refuses whatever a superuser must grant as a run with no credential always
    /// was. Reading a terminal without echoing what is typed is the one thing the core cannot do for itself, so the
    /// front end supplies it.
    /// </param>
    /// <returns>The collection, registered.</returns>
    public static IServiceCollection AddDssHarness(
        this IServiceCollection services,
        SessionOptions options,
        OperationId operation,
        Func<IServiceProvider, IHarnessOutput>? output = null,
        Func<IServiceProvider, ISuperuserPrompt>? prompt = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        var verbose = options.Verbose;
        var origin = options.Origin;

        services.AddSingleton(origin);

        // The only registrations that observe the operating system: which system this is, how it
        // expresses a file's permissions, and how it publishes its process table. Everything
        // registered after them depends on these abstractions, never on the platform.
        services.AddSingleton<IHostPlatform, HostPlatform>();
        services.AddSingleton(_ => FilePermissionsFactory.Create());
        services.AddSingleton(provider => ProcessTableFactory.Create(
            provider.GetRequiredService<IHostPlatform>(),
            provider.GetRequiredService<IProcessRunner>()));

        services.AddSingleton<IProcessIdentity, ProcessIdentity>();

        // A host answering another machine writes its home as ~ in what it tells that machine; a command
        // typed here writes its own paths as they are. The machine is asked through the overload that survives one
        // this build does not support: the output is what says so, in the failure line the machine that asked reads,
        // and an output that could not be built would take the reason with it.
        services.AddSingleton(provider => origin.ServesAnotherMachine
            ? HomeShorthand.Of(provider.GetRequiredService<IHostPlatform>, provider.GetRequiredService<IFileSystem>())
            : HomeShorthand.None);
        services.AddSingleton<IHarnessOutput>(output ?? (provider => new ConsoleHarnessOutput(verbose, provider.GetRequiredService<HomeShorthand>(), operation, options.Parent)));
        services.AddSingleton<IFileSystem, PhysicalFileSystem>();
        services.AddSingleton<IProcessContext, ProcessContext>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();

        // One answer per command to which variables git reads a repository from, shared by every child
        // that runs git: the git client's and the forge's command line's.
        services.AddSingleton<GitLocalVariables>();
        services.AddSingleton<IGitClient, GitClient>();
        services.AddSingleton<IRepositoryLocator, RepositoryLocator>();
        services.AddSingleton<IConfigStore, JsonConfigStore>();
        services.AddSingleton<IGitIgnoreManager, GitIgnoreManager>();
        services.AddSingleton<ManagedIgnoreCheck>();
        services.AddSingleton<IProjectDetector, ProjectDetector>();
        services.AddSingleton<IPathBudget, PathBudget>();
        services.AddSingleton<IPublishedToolVersions, NuGetPublishedToolVersions>();
        services.AddSingleton<SyncedCopyToolCheck>();

        // How a command this machine's host agent runs for another machine tells that machine something while it runs:
        // notes on its standard error, read by the machine that asked and relayed by nobody.
        services.AddSingleton<HostNotes>();
        services.AddSingleton<IHarnessContextLoader, HarnessContextLoader>();
        services.AddSingleton<IWorktreeService, WorktreeService>();
        services.AddSingleton<IAnchorRegistryLocator, AnchorRegistryLocator>();
        services.AddSingleton<IAnchorRegistryLock>(provider => new NamedMutexAnchorRegistryLock(
            provider.GetRequiredService<IHostPlatform>(),
            NamedMutexAnchorRegistryLock.DefaultTimeout));
        services.AddSingleton<IAnchorRegistryService, AnchorRegistryService>();
        services.AddSingleton<IAnchorBalanceService, AnchorBalanceService>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<OrchestrationStore>();
        services.AddSingleton<OrchestrationLog>();
        services.AddSingleton<ClaudeTranscripts>();
        services.AddSingleton<IOrchestratorService, OrchestratorService>();
        services.AddSingleton<IAgentService, AgentService>();
        services.AddSingleton<IWorkflowValidationService, WorkflowValidationService>();
        services.AddSingleton<IWorkflowExportService, WorkflowExportService>();

        services.AddSingleton<IToolIdentityProvider, EntryAssemblyToolIdentityProvider>();
        services.AddSingleton<IHostCommandRunner, HostCommandRunner>();
        services.AddSingleton<EmulatorProbe>();
        services.AddSingleton<IHostAgentLastResort>(provider => new HostAgentLastResort(provider.GetRequiredService<IProcessTable>(), provider.GetRequiredService<IHostPlatform>()));
        services.AddSingleton<IOwnProgram, OwnProgram>();
        services.AddSingleton(provider => new ToolUpdateService(
            provider.GetRequiredService<IProcessTable>(),
            provider.GetRequiredService<IProcessRunner>(),
            provider.GetRequiredService<IPublishedToolVersions>(),
            provider.GetRequiredService<IToolIdentityProvider>(),
            provider.GetRequiredService<IProcessIdentity>(),
            provider.GetRequiredService<IOwnProgram>(),
            provider.GetRequiredService<IHostPlatform>(),
            provider.GetRequiredService<IHarnessOutput>()));
        services.AddSingleton<HostAgentService>();

        // Reaching a host: its own directory under .harness-config says where it is, a name is
        // resolved with a retry before ssh is started, and where each program is on it is measured
        // per connection rather than assumed from a login shell's PATH.
        services.AddSingleton<IHostSecretsStore, HostSecretsStore>();
        services.AddSingleton<INameLookup, DnsNameLookup>();
        services.AddSingleton<IHostAddressResolver>(provider => new HostAddressResolver(
            provider.GetRequiredService<INameLookup>(),
            TimeProvider.System,
            HostAddressResolver.DefaultRetryDelay));
        services.AddSingleton(provider => new HoldAwakeStore(provider.GetRequiredService<IFileSystem>(), HoldAwakeStore.DefaultPath));
        services.AddSingleton<IDetachedProcessLauncher, DetachedProcessLauncher>();
        services.AddSingleton<HoldAwakeRegistry>();
        services.AddSingleton<SyncedCopyRefusals>();
        services.AddSingleton<IWslDiskImages, WslDiskImages>();
        services.AddSingleton<CommandEnd>();
        services.AddSingleton(provider => new HoldAwakeService(
            provider.GetRequiredService<HoldAwakeStore>(),
            provider.GetRequiredService<KeepAwake>(),
            TimeProvider.System,
            HoldAwakeService.PollInterval));
        services.AddSingleton(provider => new SshWakeWindow(
            provider.GetRequiredService<INameLookup>(),
            TimeProvider.System,
            SshWakeWindow.DefaultPollDelay));
        services.AddSingleton<LocalProgramResolver>();
        services.AddSingleton<ProgramFloors>();
        services.AddSingleton<IHostProgramResolver, HostProgramResolver>();
        services.AddSingleton<IHostConnector, HostConnector>();

        services.AddSingleton<IHostInspector, HostInspector>();

        // The front end's own way of asking for a password, where it has one: whether anybody is there to
        // ask is the front end's fact, measured by its implementation; this only carries the refusal to ask. A front
        // end that supplies none runs with nobody to ask, refused as a run with no credential always was.
        services.AddSingleton<ISuperuserPrompt>(prompt ?? (_ => new NobodyToAsk()));

        services.AddSingleton<IToolProvisionService, ToolProvisionService>();
        services.AddSingleton<IRepositoryTrees, RepositoryTrees>();
        services.AddSingleton<LegsService>();
        services.AddSingleton<HostExecService>();
        services.AddSingleton<IBuildLegs, BuildLegs>();
        services.AddSingleton<ITestLegs, TestLegs>();
        services.AddSingleton<IRunnerLegs, RunnerLegs>();
        services.AddSingleton<IMutationLegs, MutationLegs>();

        // Leg machinery. Shared by build, test, run and check-mutations, so the isolation rules cannot hold for one
        // command and not another.
        services.AddSingleton<PhaseRunner>();
        services.AddSingleton<LegExecutor>();
        services.AddSingleton<RunLock>();
        services.AddSingleton<LogOwnership>();
        services.AddSingleton<InputFingerprint>();
        services.AddSingleton<ProcessSampler>();
        services.AddSingleton<RemoteLegRunner>();
        services.AddSingleton<KeepAwake>();

        // Heavy legs are admitted onto a machine across every command this user runs there, so their slots are kept
        // among this user's own state rather than in any one tree.
        services.AddSingleton<IMemoryGauge, MemoryGauge>();
        services.AddSingleton(provider => new HeavyLegSlots(
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IHarnessOutput>(),
            provider.GetRequiredService<IProcessIdentity>(),
            () => HeavyLegSlots.PathFor(provider.GetRequiredService<IHostPlatform>(), provider.GetRequiredService<IHarnessOutput>())));
        services.AddSingleton(provider => new TreeHolders(
            provider.GetRequiredService<RunLock>(),
            provider.GetRequiredService<HeavyLegSlots>(),
            provider.GetRequiredService<IProcessIdentity>()));
        services.AddSingleton(provider => new LegAdmission(
            provider.GetRequiredService<HeavyLegSlots>(),
            provider.GetRequiredService<IMemoryGauge>()));
        services.AddSingleton<IWslPageCache, WslPageCache>();
        services.AddSingleton<LegRunService>();
        services.AddSingleton<CleanService>();

        services.AddSingleton<BuildDirectoryGuard>();
        services.AddSingleton<CMakeToolchainReader>();
        services.AddSingleton<CompilerVersionProbe>();
        services.AddSingleton<DeveloperEnvironmentProbe>();
        services.AddSingleton<DeveloperEnvironmentProvider>();
        services.AddSingleton<NinjaDependencyCheck>();
        services.AddSingleton<NinjaDeadOutputCheck>();
        services.AddSingleton<IBuildService, BuildService>();

        // Sync. The local transport is registered as the interface because it is also what a host
        // runs on its own side, where `sync-serve` resolves exactly this one.
        services.AddSingleton<IManifestBuilder, ManifestBuilder>();

        // What keeps a mutation worker's copy from being removed from under the sweep mutating it, wherever the workers
        // kept in a tree are removed with it: here, or on the host serving a removal.
        services.AddSingleton(provider => MutationWorkers.CopyClaims(
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IHarnessOutput>(),
            provider.GetRequiredService<IProcessIdentity>()));
        services.AddSingleton<LocalSyncTransport>();
        services.AddSingleton<ISyncTransport>(provider => provider.GetRequiredService<LocalSyncTransport>());
        services.AddSingleton<ISyncTransportFactory, SyncTransportFactory>();
        services.AddSingleton<ISyncService, SyncService>();
        services.AddSingleton<IHostCopyRemover, HostCopyRemover>();
        services.AddSingleton<IHostCopyLister, HostCopyLister>();

        // Predefined runners and their action files.
        services.AddSingleton<IActionFileParser, ActionFileParser>();
        services.AddSingleton<ActionToolPolicy>();
        services.AddSingleton<ActionValuesReader>();
        services.AddSingleton<ExpectedExceptionMatcher>();
        services.AddSingleton<RunCheckGate>();
        services.AddSingleton<RunSegments>();
        services.AddSingleton<IPredefinedActionRunner, PredefinedActionRunner>();
        services.AddSingleton<IRunnerRunService, RunnerRunService>();
        services.AddSingleton<ITestService, TestService>();

        // Mutation testing: a sweep of each leg's arms, in worker copies of its own, through the leg machinery above. A
        // self-test's fixture is kept among this user's own data, as the slots are, whichever tree the command is typed in.
        services.AddSingleton(provider => new MutationFixtureStore(provider.GetRequiredService<IFileSystem>(), MutationFixtureStore.DefaultDirectory));
        services.AddSingleton<MutationService>();

        // Guards that became commands.
        services.AddSingleton<IRootLitterService, RootLitterService>();
        services.AddSingleton(provider => new SetUpHolds(
            provider.GetRequiredService<IFileSystem>(),
            provider.GetRequiredService<IHarnessOutput>(),
            provider.GetRequiredService<IProcessIdentity>(),
            provider.GetRequiredService<IHostPlatform>(),
            () => SetUpHolds.PathFor(provider.GetRequiredService<IHostPlatform>(), provider.GetRequiredService<IHarnessOutput>())));
        services.AddSingleton<ISetUpService, SetUpService>();
        services.AddSingleton<LegSetUps>();
        services.AddSingleton<IAnchorCitationService, AnchorCitationService>();
        services.AddSingleton<ILineEndingService, LineEndingService>();
        services.AddSingleton<ICiJobSource, GhCiJobSource>();
        services.AddSingleton<ICiLegsService, CiLegsService>();

        services.AddSingleton<VerifyGitService>();
        services.AddSingleton<InitService>();

        return services;
    }
}
