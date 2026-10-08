using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Testing;

namespace RepoHarness.Core.Mutations;

/// <summary>What <c>check-mutations</c> was asked to do.</summary>
/// <param name="Directory">The directory the command was typed in.</param>
/// <param name="LegNames">What <c>--legs</c> was given, or <see langword="null"/> where it was left out.</param>
/// <param name="ArmNames">What <c>--arms</c> was given, or <see langword="null"/> where it was left out.</param>
/// <param name="ForceLock">Whether to take a lock, or a worker, a run on another host holds.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="UseStaged">Whether to sweep what is already staged on each host, without syncing again.</param>
/// <param name="Here">The host this machine is to the machine that dispatched the legs here, or <see langword="null"/>.</param>
/// <param name="RemoteArguments">
/// The options a host sweeping one of the legs is given, so it sweeps the same arms, or self-tests as this machine does.
/// </param>
/// <param name="SelfTest">
/// Whether to sweep the fixture this tool carries, through each leg's toolchain, holding each arm to the verdict it is
/// designed to reach - rather than the arms the repository's registry declares.
/// </param>
public sealed record MutationRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    IReadOnlyList<string>? ArmNames,
    bool ForceLock = false,
    bool Json = false,
    bool UseStaged = false,
    HostId? Here = null,
    IReadOnlyList<string>? RemoteArguments = null,
    bool SelfTest = false);

/// <summary>The arms a sweep drives, as the registry declares them and <c>--arms</c> and the S rows select them.</summary>
/// <param name="Registry">The registry, every arm in it.</param>
/// <param name="Selected">The arms <c>--arms</c> selected, in the registry's order.</param>
/// <param name="Scopes">Each scoped arm's legs.</param>
internal sealed record SweepArms(MutationRegistry Registry, IReadOnlyList<MutationArm> Selected, IReadOnlyDictionary<string, IReadOnlySet<string>> Scopes);

/// <summary>
/// Sweeps the mutation arms a repository's registry declares, on every selected leg: reads and verifies the registry and
/// its texts before any host is touched, refuses a leg its records cannot be read for, and runs each leg's sweep - in
/// copies of its tree of their own, under a lock of their own - through the machinery every leg-running command shares.
/// </summary>
/// <remarks>
/// <para>
/// A registry that cannot be read whole, an arm <c>--arms</c> names that it does not declare, a scope naming no leg, a
/// leg built without the Ninja generator, and a selection none of whose arms runs on any of its legs are refused before
/// anything starts, each naming its fix: refused from inside a leg, the same refusal would end the run once its hosts
/// were measured and its slots taken. A host sweeping one of the legs reads its own copy's registry the same way, and
/// sweeps it with its own workers and its own admission.
/// </para>
/// <para>
/// A self-test sweeps the fixture this tool carries instead (<see cref="MutationFixture"/>), on each selected leg as the
/// leg builds - its toolchain, configuration and sanitizer - written where the machine running the leg keeps this tool's
/// own data, and each arm held to the verdict it is designed to reach.
/// </para>
/// </remarks>
public sealed class MutationService(
    IHarnessContextLoader contextLoader,
    LegRunService legRuns,
    ISyncService syncService,
    LocalSyncTransport localTransport,
    IBuildService buildService,
    IProcessRunner processRunner,
    BuildDirectoryGuard buildDirectoryGuard,
    PhaseRunner phaseRunner,
    IPathBudget pathBudget,
    IFileSystem fileSystem,
    MutationFixtureStore fixtures,
    IProcessIdentity identity,
    TimeProvider clock,
    IHarnessOutput output)
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "check-mutations";

    /// <summary>
    /// What a sweep has each leg do: build, as a leg builds, in workers of its own, each arm asking its machine to take it
    /// as it starts rather than the leg taking a slot for the whole of a sweep of hours.
    /// </summary>
    public static LegWorkload Workload { get; } = LegWorkload.BuildOnly with { AdmitsEachUnit = true };

    /// <summary>
    /// What a self-test has each leg do: build as a sweep builds, the fixture this tool carries in place of the leg's tree,
    /// so no host is asked about the room a build of the leg's tree needs.
    /// </summary>
    public static LegWorkload SelfTestWorkload { get; } = Workload with { BuildsTheLegsTree = false };

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegRunService _legRuns = legRuns;
    private readonly ISyncService _syncService = syncService;
    private readonly LocalSyncTransport _localTransport = localTransport;
    private readonly IBuildService _buildService = buildService;
    private readonly IProcessRunner _processRunner = processRunner;
    private readonly BuildDirectoryGuard _buildDirectoryGuard = buildDirectoryGuard;
    private readonly PhaseRunner _phaseRunner = phaseRunner;
    private readonly IPathBudget _pathBudget = pathBudget;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly MutationFixtureStore _fixtures = fixtures;
    private readonly IProcessIdentity _identity = identity;
    private readonly TimeProvider _clock = clock;
    private readonly IHarnessOutput _output = output;

    /// <summary>Sweeps every selected leg, and reports the ledger, each arm beneath its leg.</summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="runId">The run, begun and said by the command before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the sweep: every site is put back, here and on every host.</param>
    /// <exception cref="HarnessException">
    /// The registry, its texts, its scopes or <c>--arms</c> cannot be swept, a selected leg cannot be, or no selected arm
    /// runs on a selected leg: each refused before any host is touched.
    /// </exception>
    public async Task<CommandOutcome> RunAsync(MutationRequest request, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runId);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);
        var arms = request.SelfTest ? SelfTestArms(request.ArmNames) : Read(context, request.ArmNames);
        var legs = LegSelection.Resolve(context.Config, request.LegNames).Legs;

        RequireSweepable(context.Config, legs, request.SelfTest);

        // The whole selection is this machine's to judge only where the command was typed on it: a host sweeping one leg
        // of a run is given that leg alone and the run's arms, and an arm among them that runs on another of the run's
        // legs is driven there, as the machine that selected them has already seen.
        if (request.Here is null)
        {
            IReadOnlyList<string> selected = [.. legs.Select(leg => leg.Name)];

            ArmSelection.RequireDriven(selected, arms.Selected, arms.Scopes);

            foreach (var arm in ArmSelection.DrivenNowhere(selected, arms.Selected, arms.Scopes))
            {
                _output.Warn(CommandName, $"arm '{arm.Id}' runs on none of the selected legs: its S row, line {arm.Scope!.Line}, names {string.Join(", ", arm.Scope.Legs)}");
            }
        }

        var runner = new MutationLegRunner(
            request.SelfTest ? new FixtureMutationSource() : new TreeMutationSource(_syncService),
            new WorkerCopies(_syncService, _localTransport, _fileSystem, _output, _identity, CommandName),
            new WorkerSite(_fileSystem, _clock),
            new ArmBuilder(_buildService, _processRunner, _buildDirectoryGuard, _fileSystem),
            new ArmTestRunner(_phaseRunner, _fileSystem, _clock),
            _pathBudget,
            _fileSystem,
            _output,
            CommandName);

        return await _legRuns
            .RunAsync(
                CommandName,
                runId,
                new LegRunRequest(
                    request.Directory,
                    request.LegNames,
                    request.ForceLock,
                    request.Json,
                    request.UseStaged,
                    Time: false,
                    request.Here,
                    request.RemoteArguments)
                {
                    // A self-test takes the leg's own sweep lock, as a sweep of the leg does: its workers are kept beside
                    // the leg's tree as a sweep's are, in a family of their own, and a clean of the leg takes both under it.
                    Workload = request.SelfTest ? SelfTestWorkload : Workload,
                    Lock = MutationWorkers.SweepLock,
                },
                (work, token) => request.SelfTest
                    ? SelfTestAsync(runner, work, arms, request.ForceLock, token)
                    : runner.RunAsync(Subject(work, arms, request.ForceLock), work, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The options a host sweeping one of a run's legs is given, so it sweeps what this machine was asked to: a self-test
    /// where this is one, and each value <c>--arms</c> was given, as it was given - an empty one among them, which the
    /// host refuses as this machine does - and nothing where it was left out, which is every arm there too.
    /// </summary>
    /// <param name="arms">What <c>--arms</c> was given, or <see langword="null"/> where it was left out.</param>
    /// <param name="selfTest">Whether the run is a self-test.</param>
    /// <remarks>
    /// <c>--legs</c> and <c>--json</c> are supplied by the dispatch itself, and the lock and the staging are this
    /// machine's decisions about its own state.
    /// </remarks>
    public static IReadOnlyList<string> RemoteArguments(IReadOnlyList<string>? arms, bool selfTest)
    {
        var options = new List<string>();

        if (selfTest)
        {
            options.Add("--self-test");
        }

        foreach (var arm in arms ?? [])
        {
            options.AddRange(["--arms", arm]);
        }

        return options;
    }

    /// <summary>
    /// What <paramref name="work"/>'s leg sweeps: its tree; its project, configured with the dependency sources its own
    /// build fetched; the arms it drives; the test settings its tests start by; and what a build of its variant is
    /// expected to come to, as its own build directory or the main checkout's copy of it last recorded.
    /// </summary>
    internal MutationSubject Subject(LegWork work, SweepArms arms, bool force)
    {
        var leg = work.Leg;
        var (bytes, source) = LegRoom.ExpectedBuildBytes(
            leg.Leg,
            BuildRecord.BytesIn(_fileSystem, leg.BuildDirectory),
            BuildRecord.BytesIn(_fileSystem, leg.Variant.DirectoryUnder(work.Context.Layout.MainCheckoutRoot)));
        var fetched = FetchedSources.CacheVarsFor(_buildDirectoryGuard.Read(leg.BuildDirectory), _fileSystem);
        var project = leg.BuildableProject();

        return new MutationSubject
        {
            TreeRoot = leg.TreeRoot,
            Workers = MutationWorkers.Of(leg.TreeRoot, leg.Variant),
            Project = project.WithCacheVarsBeneath(fetched),
            Tests = TestInvocationResolver.SettingsFor(work.Context.Config, leg.Leg, project),
            Arms = ArmSelection.For(leg.Name, arms.Registry, arms.Selected, arms.Scopes),
            Settings = work.Context.Config.Mutations,
            ExpectedBuildBytes = bytes,
            ExpectedBuildSource = source,
            Force = force,
        };
    }

    /// <summary>
    /// The arms a self-test drives: those of the fixture's own registry <paramref name="armNames"/> selects, every arm
    /// where it was left out.
    /// </summary>
    /// <exception cref="HarnessException"><c>--arms</c> names an arm the fixture does not declare (<see cref="HarnessExit.UsageError"/>).</exception>
    internal static SweepArms SelfTestArms(IReadOnlyList<string>? armNames)
    {
        var registry = MutationFixture.Registry();

        // Scoped to no leg: the fixture declares no S row, so every arm runs on every selected leg.
        return new SweepArms(registry, ArmSelection.Resolve(registry, armNames), new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal));
    }

    /// <summary>
    /// Self-tests <paramref name="work"/>'s leg: the fixture written where this machine keeps this tool's own data, and
    /// swept as the leg builds, in workers kept beside the leg's own tree.
    /// </summary>
    /// <exception cref="HarnessException">
    /// This machine has nowhere of this user's own to keep the fixture, or it could not be written there
    /// (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    private async Task<LegEntry> SelfTestAsync(MutationLegRunner runner, LegWork work, SweepArms arms, bool force, CancellationToken cancellationToken)
    {
        var directory = _fixtures.Directory;

        work.Progress($"writing the self-test's fixture to '{directory}'");
        _fixtures.Write();

        return await runner.RunAsync(SelfTestSubject(work, arms, force, directory), work, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a self-test of <paramref name="work"/>'s leg sweeps: the fixture kept in <paramref name="directory"/>, built as
    /// the leg's variant builds, its binary run in its worker with what the leg's host gives it, each worker's paths
    /// reckoned by the fixture's own build, and each arm held to the verdict it is designed to reach. Nothing says what a
    /// build of it comes to: a few megabytes, which the copy's own size is planned with.
    /// </summary>
    /// <remarks>
    /// Its workers are copies of the fixture kept beside the leg's own tree, in the self-test's family of that tree's
    /// workers: one fixture serves every repository on the machine, and workers kept beside it were shared by all of
    /// them - one repository's toolchain refusing the build directory another's had made - with nothing to remove them.
    /// Beside the leg's tree they are that repository's, counted by its room, and go with a clean of the leg, and with
    /// its worktree or its host's copy.
    /// </remarks>
    internal static MutationSubject SelfTestSubject(LegWork work, SweepArms arms, bool force, string directory)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(arms);

        return new MutationSubject
        {
            TreeRoot = directory,
            Workers = MutationWorkers.Of(work.Leg.TreeRoot, work.Leg.Variant, selfTest: true),
            Project = MutationFixture.Project,
            Arms = ArmSelection.For(work.Leg.Name, arms.Registry, arms.Selected, arms.Scopes),
            Settings = MutationFixture.SettingsFor(work.Context.Config.Mutations),
            PathReserve = MutationFixture.LongestBuildPath,
            Hold = MutationFixture.Hold,
            Force = force,
        };
    }

    /// <summary>
    /// Reads and verifies the registry <paramref name="context"/>'s configuration names, with every text it cites, and
    /// selects the arms <paramref name="armNames"/> names.
    /// </summary>
    /// <exception cref="HarnessException">
    /// No registry is configured, it or a text it cites cannot be read whole, a scope names no leg, or a selected arm runs
    /// a binary with no report arguments configured (<see cref="HarnessExit.ConfigInvalid"/>); or <c>--arms</c> names an
    /// arm it does not declare (<see cref="HarnessExit.UsageError"/>).
    /// </exception>
    internal SweepArms Read(HarnessContext context, IReadOnlyList<string>? armNames)
    {
        ArgumentNullException.ThrowIfNull(context);

        var settings = context.Config.Mutations;

        if (settings.Registry is not { } registry)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{CommandName} has no arms registry to sweep: set mutations.registry to the registry's path, relative to the repository root.");
        }

        var root = context.Layout.RepositoryRoot;
        var file = InTree(root, registry);

        if (!_fileSystem.FileExists(file))
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"mutations.registry names '{registry}', which is not a file in '{root}'.");
        }

        var reading = MutationRegistryParser.Parse(
            MutationRegistryParser.Lines(_fileSystem.ReadAllText(file)),
            Listing(root, settings.TextDirectory),
            PathCase.In(_fileSystem, root));
        var problems = new List<string>(reading.Problems);

        if (reading.Valid)
        {
            problems.AddRange(TextProblems(reading.Registry, root));
        }

        Refuse(registry, problems);

        var selected = ArmSelection.Resolve(reading.Registry, armNames);
        var scopes = ArmSelection.Scopes(context.Config, reading.Registry, problems);

        Refuse(registry, problems);

        if (selected.Any(arm => arm.Kind == RedKind.TestRed) && settings.ReportArgs is not { Count: > 0 })
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{MutationReport.Setting} is not set, and the sweep runs {MutationRegistryParser.TestRed} arms, whose binaries it "
                + "judges by the JUnit report each run writes: set it to the arguments that make the binary write one to "
                + $"{{{MutationReport.Placeholder}}}, such as [\"--gtest_output=xml:{{{MutationReport.Placeholder}}}\"].");
        }

        return new SweepArms(reading.Registry, selected, scopes);
    }

    /// <summary>
    /// Refuses every one of <paramref name="legs"/> whose build a sweep cannot read: one that cannot be built, or is built
    /// by anything but CMake with the Ninja generator, whose own records are what says which objects each arm rebuilt.
    /// A self-test builds the fixture, a CMake project whatever the leg's own is, so only the leg's toolchain is asked.
    /// </summary>
    /// <param name="config">The configuration declaring the legs.</param>
    /// <param name="legs">The selected legs.</param>
    /// <param name="selfTest">Whether the run is a self-test.</param>
    /// <exception cref="HarnessException">Any of them is such a leg: one refusal names each, with its fix.</exception>
    internal static void RequireSweepable(HarnessConfig config, IEnumerable<SelectedLeg> legs, bool selfTest = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(legs);

        var problems = new List<string>();

        foreach (var leg in legs)
        {
            var project = VariantKey.ProjectFor(config, leg.Leg);
            var variant = VariantKey.For(config, leg.Leg, leg.Leg.Os);

            if (!variant.CanBuild(leg.Name, project, leg.Leg.Os, out var why))
            {
                problems.Add($"  - {why}");
                continue;
            }

            if (!selfTest && !string.Equals(project.Type, "cmake", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"  - Leg '{leg.Name}' builds project '{project.Name}', a {project.Type} project: only a CMake build's ninja records say which objects a mutation rebuilt.");
                continue;
            }

            var generator = config.Toolchains.TryGetValue(variant.Toolchain, out var toolchain) ? toolchain.Generator : null;

            if (!string.Equals(generator, "Ninja", StringComparison.Ordinal))
            {
                problems.Add(
                    $"  - Leg '{leg.Name}' builds with toolchain '{variant.Toolchain}', whose generator is "
                    + (generator is null ? "not declared, so CMake picks one" : $"'{generator}'")
                    + $": set toolchains.{variant.Toolchain}.generator to \"Ninja\". A sweep reads which objects each mutation rebuilt "
                    + "from ninja's own records of the build, which only the Ninja generator keeps for one configuration alone.");
            }
        }

        if (problems.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                string.Join(Environment.NewLine, [$"{CommandName} cannot sweep every leg it was asked to, so nothing was run:", .. problems]));
        }
    }

    /// <summary>
    /// What is wrong with the texts <paramref name="registry"/>'s rows cite, read as the tree holds them: each that is not a
    /// file, and each before-text, control before-text or diagnostic holding nothing - which occurs everywhere, and which
    /// every run would say.
    /// </summary>
    private IEnumerable<string> TextProblems(MutationRegistry registry, string root)
    {
        foreach (var arm in registry.Arms)
        {
            foreach (var site in arm.Sites)
            {
                if (TextProblem(root, site.Before, site.Line, empty: true) is { } before)
                {
                    yield return before;
                }

                if (TextProblem(root, site.After, site.Line, empty: false) is { } after)
                {
                    yield return after;
                }
            }

            if (arm.Control is { } control)
            {
                if (TextProblem(root, control.Before, control.Line, empty: true) is { } before)
                {
                    yield return before;
                }

                if (TextProblem(root, control.After, control.Line, empty: false) is { } after)
                {
                    yield return after;
                }
            }

            if (arm.Kind == RedKind.TestRed && TextProblem(root, arm.Diagnostic, arm.Line, empty: true) is { } diagnostic)
            {
                yield return diagnostic;
            }
        }
    }

    /// <summary>What is wrong with the text <paramref name="cited"/> on line <paramref name="line"/>, or <see langword="null"/>.</summary>
    private string? TextProblem(string root, string cited, int line, bool empty)
    {
        var file = InTree(root, cited);

        if (!_fileSystem.FileExists(file))
        {
            return $"line {line}: text '{cited}' is not a file in the tree";
        }

        return empty && SiteEdit.Text(_fileSystem.ReadAllBytes(file)).Length == 0
            ? $"line {line}: the text in '{cited}' holds nothing, which occurs everywhere and which every run says"
            : null;
    }

    /// <summary>The files directly in the text directory, as the cover check reads them, or <see langword="null"/> where none is configured.</summary>
    private TextDirectoryListing? Listing(string root, string? textDirectory)
    {
        if (textDirectory is null)
        {
            return null;
        }

        var directory = InTree(root, textDirectory);

        return new TextDirectoryListing(
            textDirectory.TrimEnd('/'),
            _fileSystem.DirectoryExists(directory)
                ? [.. _fileSystem.EnumerateFiles(directory, recursive: false).Select(Path.GetFileName).OfType<string>()]
                : null);
    }

    /// <summary>Refuses the registry, naming every problem with it, where it has any.</summary>
    private static void Refuse(string registry, List<string> problems)
    {
        if (problems.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                string.Join(Environment.NewLine, [$"The arms registry '{registry}' cannot be swept: {problems.Count} problem(s), each to fix:", .. problems.Select(problem => "  - " + problem)]));
        }
    }

    /// <summary>Where <paramref name="relative"/>, as configuration and rows spell it, is in <paramref name="root"/>.</summary>
    private static string InTree(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
