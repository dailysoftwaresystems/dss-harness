using System.Text.Json;
using DssHarness.Core.Application;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runs;
using DssHarness.Core.Sync;
using DssHarness.Core.Testing;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Mutations;

/// <summary>What <c>check-mutations</c> was asked to do.</summary>
/// <param name="Directory">The directory the command was typed in.</param>
/// <param name="LegNames">What <c>--legs</c> was given, or <see langword="null"/> where it was left out.</param>
/// <param name="ArmNames">What <c>--arms</c> was given, or <see langword="null"/> where it was left out.</param>
/// <param name="ForceLock">Whether to take a lock, or a worker, another run holds, on this host or another.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="UseStaged">Whether to sweep what is already staged on each host, without syncing again.</param>
/// <param name="Here">The host this machine is to the machine that dispatched the legs here, or <see langword="null"/>.</param>
/// <param name="SelfTest">
/// Whether to sweep the fixture this tool carries, through each leg's toolchain, holding each arm to the verdict it is
/// designed to reach - rather than the arms the repository's registry declares.
/// </param>
/// <param name="List">
/// Whether to sweep nothing, and say instead each selected arm's last verdict on each leg and whether anything it was
/// judged on that this machine reads has moved since.
/// </param>
/// <param name="Pending">
/// Whether to drive, on each leg, only the arms with no recorded <c>passed</c> that holds: each other is verified on the
/// leg's machine and left out.
/// </param>
/// <param name="Held">
/// What the record of verdicts holds of this leg's arms, where the machine that dispatched this sweep gave it as bytes -
/// the hidden <c>--held</c>, which no person types; <see langword="null"/> where none was given, and a sweep under
/// <c>--pending</c> then holds nothing.
/// </param>
public sealed record MutationRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    IReadOnlyList<string>? ArmNames,
    bool ForceLock = false,
    bool Json = false,
    bool UseStaged = false,
    HostId? Here = null,
    bool SelfTest = false,
    bool List = false,
    bool Pending = false,
    RecordedArms? Held = null);

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
    IGitClient gitClient,
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
    IHostPlatform platform,
    TimeProvider clock,
    Hosts.HostNotes notes,
    IHarnessOutput output) : IUseCase
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "check-mutations";

    /// <summary>
    /// The listing <c>--list --json</c> answers with where the command stops before it lists anything: the listing's own
    /// document, listing no leg, with the code it exits with and the line it ends on (<see cref="MutationListing.Stopped"/>).
    /// </summary>
    /// <param name="exitCode">The code the command exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="show">Tells a line of the harness's own as it is told to whoever reads the document.</param>
    public static string ListStopped(int exitCode, string summary, Func<string, string> show) => MutationListing.Stopped(exitCode, summary, show);

    /// <summary>
    /// What a sweep exits with where no selected leg can run and no failure turned one away: incomplete, which is what
    /// such a run is - nothing failed, and no leg reached a verdict. The 1 a build, a test and a run give there is, of a
    /// sweep, an arm <c>violated</c>: a caller sorting by exit code would be sent to fix an arm's declaration by a sweep
    /// that drove none.
    /// </summary>
    public const int NothingRuns = HarnessExit.Incomplete;

    /// <summary>
    /// What a sweep has each leg do: build, as a leg builds, in workers of its own, each arm asking its machine to take it
    /// as it starts rather than the leg taking a slot for the whole of a sweep of hours - and, on another machine, in a
    /// copy of its own there (<see cref="LegWorkload.CopyOnHosts"/>), so a sweep of hours never holds off a test of the
    /// host's copy, nor waits for one.
    /// </summary>
    public static LegWorkload Workload { get; } = LegWorkload.BuildOnly with { AdmitsEachUnit = true, CopyOnHosts = true };

    /// <summary>
    /// What a self-test has each leg do: build as a sweep builds, the fixture this tool carries in place of the leg's tree,
    /// so no host is asked about the room a build of the leg's tree needs.
    /// </summary>
    public static LegWorkload SelfTestWorkload { get; } = Workload with { BuildsTheLegsTree = false };

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly IGitClient _gitClient = gitClient;
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
    private readonly IHostPlatform _platform = platform;
    private readonly TimeProvider _clock = clock;
    private readonly Hosts.HostNotes _notes = notes;
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
        => (await ExecuteAsync(request, runId, cancellationToken).ConfigureAwait(false)).ToOutcome(request.Json, _output.Shown);

    /// <summary>
    /// Sweeps every selected leg and answers what the run reached, in the terms its caller answers in: what the mutation
    /// use case answers with - which a step of an orchestrator run will call - and the command's own body below it.
    /// </summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="runId">The run, begun and said by the command before anything could refuse it.</param>
    /// <param name="cancellationToken">Stops the sweep: every site is put back, here and on every host.</param>
    /// <exception cref="HarnessException">
    /// The registry, its texts, its scopes or <c>--arms</c> cannot be swept, a selected leg cannot be, or no selected arm
    /// runs on a selected leg: each refused before any host is touched.
    /// </exception>
    public async Task<LegRunResult> ExecuteAsync(MutationRequest request, RunId runId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(runId);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);
        var arms = request.SelfTest
            ? SelfTestArms(request.ArmNames)
            : await ReadAsync(context, request.ArmNames, cancellationToken).ConfigureAwait(false);
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
            new CtestDescription(_processRunner, _fileSystem, _platform),
            _pathBudget,
            _fileSystem,
            _output,
            _clock,
            CommandName);

        // A sweep typed on this machine records each verdict as its arm ends; one a host runs for it keeps no record
        // there - it tells this machine instead, as each arm ends - and nothing of a self-test is kept. Nor does a sweep
        // typed by hand in a copy the harness synced: the record is the main checkout's on the machine the copy was synced
        // from, and one kept in the copy would hold what that machine never sees, and decide what a later sweep typed
        // there holds - which it says, once. Where this one records, it says where, as a listing does.
        var sweepsHere = request.Here is null && !request.SelfTest;
        var records = sweepsHere && !context.IsSyncedCopy;

        if (sweepsHere && context.IsSyncedCopy)
        {
            _output.Warn(
                CommandName,
                "this tree is a copy the harness synced, so this sweep records nothing: the record of verdicts is kept in the main "
                + "checkout it was synced from" + (request.Pending ? ", and --pending holds nothing here, so every selected arm is swept" : string.Empty));
        }

        if (records && !request.Json)
        {
            _output.Info(CommandName, $"the record is '{new ArmVerdictRecord(_fileSystem, context.Layout).Directory}'");
        }

        var record = new ArmVerdictRecord(_fileSystem, context.Layout);

        return await _legRuns
            .ExecuteAsync(
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
                    RemoteArguments(request.ArmNames, request.SelfTest, request.Pending && request.Here is null))
                {
                    // A self-test takes the leg's own sweep lock, as a sweep of the leg does: its workers are kept in the
                    // leg's tree as a sweep's are, in a family of their own, and a clean of the leg takes both under it.
                    Workload = request.SelfTest ? SelfTestWorkload : Workload,
                    Lock = MutationWorkers.SweepLock,
                    NothingRunsExit = NothingRuns,

                    // A sweep makes no copy of its own: its workers are already copies of one reading. The legs it runs
                    // here work in their trees, wherever the configuration would have a command's legs work.
                    LocalTrees = LocalTrees.InPlace,
                    MutationRecord = records ? context.Layout.MutationRecordDirectory : null,

                    // Under --pending, each leg's host is given what the record holds of that leg's own arms, as bytes it
                    // reads as their base64 text: held and pending are decided there, by the leg's own reading and worker.
                    RemoteArgumentsOf = records && request.Pending
                        ? leg =>
                        {
                            var driven = ArmSelection.For(leg.Name, arms.Registry, arms.Selected, arms.Scopes).Driven;

                            return ["--held", HostArgument.Carrying(record.ArmsOf(leg.Name, driven.Select(arm => arm.Id)).Carried())];
                        }
                        : null,

                    // Each leg swept on a host tells this machine, as each arm ends there, what it judged: the same
                    // record-writing sink a leg swept here feeds, so a sweep stopped part way keeps what it had judged.
                    HostNotes = records ? NotesOf(context) : null,
                },
                (work, token) => request.SelfTest
                    ? SelfTestAsync(runner, work, arms, request.ForceLock, token)
                    : SweepAsync(runner, work, arms, request.ForceLock, records, request.Pending, request.Held, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the notes one dispatched leg's host writes while it sweeps - a table an entry names, an arm's verdict as the
    /// arm ends, what a sweep under <c>--pending</c> found holding - into that leg's own record-writing sink, which the
    /// notes of no other leg reach.
    /// </summary>
    /// <param name="context">The repository, whose main checkout keeps the record.</param>
    private Func<PlacedLeg, Action<string, string>?> NotesOf(HarnessContext context) => leg =>
    {
        var sink = new RecordSink(new ArmVerdictRecord(_fileSystem, context.Layout), leg.Name, _output, CommandName);
        var tables = new Dictionary<string, BuiltFromTable>(StringComparer.Ordinal);

        // A note whose base64 decodes to no JSON this build writes - a piece lost, a marker missed, a truncation that
        // lands aligned - is dropped as a damaged one is: the record then lacks that arm, which is pending, and nothing
        // a host said in passing fails this run.
        T? Read<T>(string text)
            where T : class
        {
            try
            {
                return JsonSerializer.Deserialize<T>(text, JsonStateFile.LineOptions);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return (kind, text) =>
        {
            switch (kind)
            {
                case NoteSink.TableKind:

                    if (Read<BuiltFromTable>(text) is { } table)
                    {
                        tables[table.Digest()] = table;
                    }

                    break;

                case NoteSink.ArmKind:

                    if (Read<NotedArm>(text) is { } arm)
                    {
                        sink.Judged(
                            arm.Arm,
                            arm.Verdict,
                            [.. arm.Verdict.JudgedOn?.Built.Values.Select(digest => tables.GetValueOrDefault(digest)).OfType<BuiltFromTable>() ?? []]);
                    }

                    break;

                case NoteSink.VerifiedKind:

                    if (Read<NotedVerified>(text) is { } verified)
                    {
                        sink.Verified(verified.Keys, verified.By);
                    }

                    break;

                default:
                    break;
            }
        };
    };

    /// <summary>
    /// Lists, without sweeping, each selected arm's last verdict on each selected leg its S row names, and whether anything
    /// it was judged on that this machine reads has moved since: the registry is read and verified as a sweep reads it,
    /// each leg's tree is read once, and each leg's record. It asks no host, takes no lock, begins no run and writes nothing,
    /// and it needs nothing only a sweep needs: report arguments, a leg built with Ninja, a selection that drives an arm. A
    /// leg that cannot be swept is said, never refused; so is a record that cannot be read.
    /// </summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <returns>The listing, as a table or as its document, carrying the facts both are made of (<see cref="ArmListing"/>).</returns>
    /// <exception cref="HarnessException">
    /// The registry, its texts or its scopes cannot be swept (<see cref="HarnessExit.ConfigInvalid"/>); <c>--legs</c> or
    /// <c>--arms</c> names something not declared (<see cref="HarnessExit.UsageError"/>); git could not say what it ignores
    /// in a tree (<see cref="HarnessExit.CommandFailed"/>); or a tree changed while it was read (<see cref="LegExit.InputsMoved"/>).
    /// </exception>
    public async Task<CommandOutcome> ListAsync(MutationRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);
        var config = context.Config;
        var arms = await ReadAsync(context, request.ArmNames, cancellationToken, sweeps: false).ConfigureAwait(false);
        var legs = LegSelection.Resolve(config, request.LegNames).Legs;
        var unsweepable = Unsweepable(config, legs);
        var record = new ArmVerdictRecord(_fileSystem, context.Layout);
        var trees = new Dictionary<string, TreeFiles>(StringComparer.FromComparison(_platform.PathComparison));
        var listed = new List<ListedLeg>();

        foreach (var leg in legs)
        {
            var tree = LegTrees.Here(context, leg.Leg, null);

            if (!trees.TryGetValue(tree, out var files))
            {
                using var reading = await _syncService.ReadSourceAsync(tree, cancellationToken).ConfigureAwait(false);

                trees[tree] = files = new TreeFiles(reading.Files, PathCase.In(_fileSystem, tree));
            }

            var named = LegPlacement.Named(config, leg.Leg);
            var project = VariantKey.ProjectFor(config, leg.Leg);

            // One read of a table to a digest, for the whole listing, however many arms and entries name it.
            var tables = new Dictionary<string, RecordedTable>(StringComparer.Ordinal);

            RecordedTable TableOf(string digest)
                => tables.TryGetValue(digest, out var read) ? read : tables[digest] = record.Table(leg.Name, digest);

            listed.Add(MutationListing.Of(new ListingInput(
                leg.Name,
                ArmSelection.For(leg.Name, arms.Registry, arms.Selected, arms.Scopes).Driven,
                unsweepable.GetValueOrDefault(leg.Name),
                record.Read(leg.Name),
                TableOf,
                files,
                named?.ToString(),

                // Asked of the host the leg names, or of the one an entry was judged on where it names none: which host a
                // sweep would place it on is not read here, so it is read as it was, and the environment its build is
                // given with it - what that host declares, as the sweep that judged it was given what it declared.
                host => SweepSettings.Of(config, leg.Leg, config.Hosts.SettingsFor(host)),
                config.Mutations,
                project is null ? null : JudgedCauses.GenerationOf(config, VariantKey.For(config, leg.Leg, leg.Leg.Os), project))));
        }

        var registry = config.Mutations.Registry!;

        // The facts the table and the document are made of, from the same locals, so the three cannot drift.
        var listing = new ArmListing(listed, registry, record.Directory);

        // Said under --json too: a warning is for whoever reads the document, and no row of it names the file.
        foreach (var leg in listed)
        {
            if (leg.RecordUnread is { } unread)
            {
                _output.Warn(CommandName, unread.Warning(record.FileOf(leg.Leg), leg.Leg, sweep: false));
            }
        }

        if (request.Json)
        {
            return CommandOutcome.Ok(MutationListing.Summary(listed)) with
            {
                Data = [MutationListing.Json(listing.Legs, listing.Registry, listing.RecordDirectory, _output.Shown)],
                Quiet = true,
                Facts = listing,
            };
        }

        _output.Info(
            CommandName,
            $"listing {ReportText.Counted(arms.Selected.Count, "arm")} of '{registry}' on {ReportText.Counted(legs.Count, "leg")}; nothing is swept "
            + "and no host is asked");
        _output.Info(CommandName, $"the record is '{record.Directory}'");

        foreach (var leg in listed)
        {
            if (leg.Unsweepable is { } why)
            {
                _output.Info(CommandName, $"'{leg.Leg}' cannot be swept, so every arm on it is pending: {why}");
            }
        }

        return CommandOutcome.Ok(MutationListing.Summary(listed), [MutationListing.Note]) with
        {
            Data = MutationListing.Table(listing.Legs, _output.Shown),
            Quiet = true,
            Facts = listing,
        };
    }

    /// <summary>
    /// The options a host sweeping one of a run's legs is given, so it sweeps what this machine was asked to: a self-test
    /// where this is one, each value <c>--arms</c> was given, as it was given - an empty one among them, which the host
    /// refuses as this machine does - and <c>--pending</c> where this machine was asked to sweep only what is pending.
    /// </summary>
    /// <param name="arms">What <c>--arms</c> was given, or <see langword="null"/> where it was left out.</param>
    /// <param name="selfTest">Whether the run is a self-test.</param>
    /// <param name="pending">Whether the run sweeps only the arms with no passed that holds.</param>
    /// <remarks>
    /// <c>--legs</c> and <c>--json</c> are supplied by the dispatch itself, and the lock and the staging are this
    /// machine's decisions about its own state. What the record holds of that leg's own arms - <c>--held</c> - is given to
    /// the leg alone, beside these, by <see cref="LegRunRequest.RemoteArgumentsOf"/>.
    /// </remarks>
    public static IReadOnlyList<string> RemoteArguments(IReadOnlyList<string>? arms, bool selfTest, bool pending = false)
    {
        var options = new List<string>();

        if (selfTest)
        {
            options.Add("--self-test");
        }

        if (pending)
        {
            options.Add("--pending");
        }

        foreach (var arm in arms ?? [])
        {
            options.AddRange(["--arms", arm]);
        }

        return options;
    }

    /// <summary>
    /// Sweeps <paramref name="work"/>'s leg, its workers' builds measured against every tree of the repository on this
    /// machine, as they were listed as the leg's work began here: a tree that never built the leg's variant here is
    /// measured by another's that did. Trees beside the main checkout's that could not be listed are said, with why, and
    /// with what the workers' builds are measured by instead. Each arm's verdict is recorded as it ends where
    /// <paramref name="records"/>: on the machine the command was typed on, which keeps the record - and where it does
    /// not, told to that machine as notes, if a host swept the leg for it.
    /// </summary>
    private async Task<LegEntry> SweepAsync(MutationLegRunner runner, LegWork work, SweepArms arms, bool force, bool records, bool pending, RecordedArms? held, CancellationToken cancellationToken)
    {
        var beside = work.Leg.Beside ?? throw new InvalidOperationException($"Leg '{work.Leg.Name}' began its work with no trees listed beside it.");

        if (beside.Unlisted is { } why)
        {
            var measured = beside.Trees.Any(tree => tree.Name == RepositoryTree.MainCheckout) ? "its own tree's and the main checkout's" : "its own tree's";

            _output.Warn(
                CommandName,
                $"leg '{work.Leg.Name}': the worktrees beside the main checkout could not be listed, so a worker's build is measured by "
                + $"{measured} alone: {why}");
        }

        var subject = Subject(work, arms, force, beside, records, pending, held);

        await RefuseUnignoredAsync(subject.Workers, cancellationToken).ConfigureAwait(false);

        return await runner.RunAsync(subject, work, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses a sweep whose workers git would not ignore in the tree they are kept in, before anything is made: asked on
    /// the machine that makes them, so a host answers for its own copy (<see cref="SlotIgnored"/>).
    /// </summary>
    /// <param name="workers">The family of workers the sweep keeps.</param>
    /// <param name="cancellationToken">Stops the asking.</param>
    /// <exception cref="HarnessException">git does not ignore them (<see cref="HarnessExit.Refused"/>), or could not be asked.</exception>
    private async Task RefuseUnignoredAsync(WorkerFamily workers, CancellationToken cancellationToken)
    {
        var probe = $"{HarnessLayout.MutationsDirectoryName}/{workers.Name}-1";

        if (await SlotIgnored.RefusalAsync(_gitClient, workers.TreeRoot, probe, $"the mutation workers a sweep makes in '{workers.TreeRoot}'", cancellationToken).ConfigureAwait(false) is { } unignored)
        {
            throw new HarnessException(HarnessExit.Refused, $"{unignored} Nothing was swept.");
        }
    }

    /// <summary>
    /// What <paramref name="work"/>'s leg sweeps: its tree; its project, as the leg builds it; the dependency sources its
    /// own build fetched, which each worker is given its own copy of and configured with; the arms it drives; the test
    /// settings its tests start by; and what a build of its variant is expected to come to - its <c>buildSpaceGiB</c>
    /// where it declares one, else what its own build directory last recorded, else the most any of
    /// <paramref name="beside"/> - the repository's other trees here - recorded of it (<see cref="LegRoom.ExpectedBuildBytes"/>).
    /// Where it <paramref name="records"/>, each arm's verdict is kept in the record of verdicts in the main checkout,
    /// whichever tree the leg is swept in, which the record names as a line names it; where it does not - a host swept
    /// the leg for another machine - each verdict is told to that machine as a note instead, and a leg swept by hand on
    /// the host records nothing. Where it sweeps only what is <paramref name="pending"/>, it is given what the record
    /// holds of the arms it drives - <paramref name="held"/> where a dispatch gave it that, and nothing held where it
    /// gave nothing, so every arm is swept.
    /// </summary>
    internal MutationSubject Subject(LegWork work, SweepArms arms, bool force, RepositoryTreesFound beside, bool records, bool pending = false, RecordedArms? held = null)
    {
        ArgumentNullException.ThrowIfNull(beside);

        var leg = work.Leg;
        var expected = LegRoom.ExpectedBuildBytes(
            leg.Leg,
            BuildRecord.BytesIn(_fileSystem, leg.BuildDirectory),
            beside.Trees.Select(tree => (tree.Name, BuildRecord.BytesIn(_fileSystem, leg.Variant.DirectoryUnder(tree.Root)))));
        var project = leg.BuildableProject();
        var tests = TestInvocationResolver.SettingsFor(work.Context.Config, leg.Leg, project);
        var record = new ArmVerdictRecord(_fileSystem, work.Context.Layout);
        var driven = ArmSelection.For(leg.Name, arms.Registry, arms.Selected, arms.Scopes);

        // Where the leg's own build is, as its configuration places it for this machine: in its tree, or in the copy of it
        // the leg works in where its key or the repository's default says one - a sweep reads what that build fetched from
        // the copy's build directory, and makes no copy of its own (its workers already are copies of one reading).
        var own = LegTrees.For(
            work.Context,
            leg.Leg,
            leg.Variant,
            HostId.Local,
            leg.Named.Kind == HostKind.Local ? null : leg.Named,
            _platform.PathComparison,
            LocalTrees.AsConfigured).Work;

        return new MutationSubject
        {
            TreeRoot = leg.HostTreeRoot,
            Workers = MutationWorkers.Of(leg.HostTreeRoot, leg.Variant),
            Project = project,
            Fetched = FetchedSources.Of(_buildDirectoryGuard.Read(leg.Variant.DirectoryOn(HostId.Local, own)), _fileSystem),
            Tests = tests,
            Arms = driven,
            Settings = work.Context.Config.Mutations,

            // What the leg is swept with, resolved once with it: the variant placed, and the system its host runs.
            SweepSettings = SweepSettings.Of(work.Context.Config, leg.Variant, project, tests, leg.Os, work.Context.Config.Mutations, leg.HostSettings.Env),
            Sink = records ? new RecordSink(record, leg.Name, _output, CommandName) : new NoteSink(_notes),
            Recorded = pending
                ? held ?? (records ? record.ArmsOf(leg.Name, driven.Driven.Select(arm => arm.Id)) : new RecordedArms())
                : null,
            SweptIn = RepositoryTree.Of(work.Context, leg.TreeRoot, _platform.PathComparison),
            ExpectedBuildBytes = expected?.Bytes,
            ExpectedBuildSource = expected?.Source ?? string.Empty,
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
    /// swept as the leg builds, in workers kept in the leg's own tree.
    /// </summary>
    /// <exception cref="HarnessException">
    /// This machine has nowhere of this user's own to keep the fixture, or it could not be written there
    /// (<see cref="HarnessExit.Refused"/>).
    /// </exception>
    private async Task<LegEntry> SelfTestAsync(MutationLegRunner runner, LegWork work, SweepArms arms, bool force, CancellationToken cancellationToken)
    {
        var directory = _fixtures.Directory;
        var subject = SelfTestSubject(work, arms, force, directory);

        await RefuseUnignoredAsync(subject.Workers, cancellationToken).ConfigureAwait(false);

        work.Progress($"writing the self-test's fixture to '{directory}'");
        _fixtures.Write();

        return await runner.RunAsync(subject, work, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a self-test of <paramref name="work"/>'s leg sweeps: the fixture kept in <paramref name="directory"/>, built as
    /// the leg's variant builds, its binary run in its worker with what the leg's host gives it, each worker's paths
    /// reckoned by the fixture's own build, each arm held to the verdict it is designed to reach, and what each arm is
    /// judged on read by every worker and compared. Nothing says what a build of it comes to: a few megabytes, which the
    /// copy's own size is planned with.
    /// </summary>
    /// <remarks>
    /// Its workers are copies of the fixture kept in the leg's own tree, in the self-test's family of that tree's
    /// workers: one fixture serves every repository on the machine, and workers kept beside it were shared by all of
    /// them - one repository's toolchain refusing the build directory another's had made - with nothing to remove them.
    /// In the leg's tree they are that repository's, counted by its room, and go with a clean of the leg, and with its
    /// worktree or its host's copy. Each is a repository of its own, as every copy a sync makes is, holding the fixture's
    /// files staged: so the files git tracks there, which a worker's build watches, are the fixture's, and never what the
    /// tree around it tracks.
    /// </remarks>
    internal static MutationSubject SelfTestSubject(LegWork work, SweepArms arms, bool force, string directory)
    {
        ArgumentNullException.ThrowIfNull(work);
        ArgumentNullException.ThrowIfNull(arms);

        var settings = MutationFixture.SettingsFor(work.Context.Config.Mutations);

        return new MutationSubject
        {
            TreeRoot = directory,
            Workers = MutationWorkers.Of(work.Leg.HostTreeRoot, work.Leg.Variant, selfTest: true),
            Project = MutationFixture.Project,
            Arms = ArmSelection.For(work.Leg.Name, arms.Registry, arms.Selected, arms.Scopes),
            Settings = settings,
            SweepSettings = SweepSettings.Of(
                work.Context.Config,
                work.Leg.Variant,
                MutationFixture.Project,
                tests: null,
                work.Leg.Os,
                settings,
                work.Leg.HostSettings.Env),
            ComparesKeys = true,
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
    /// No registry is configured, it or a text it cites cannot be read whole, it or a text it cites is one a sync
    /// withholds from every copy of the tree - by the configuration's rules or by git's - a scope names no leg, or a
    /// selected arm runs a binary with no report arguments configured where <paramref name="sweeps"/> - a listing runs
    /// none, and needs none (<see cref="HarnessExit.ConfigInvalid"/>); or
    /// <c>--arms</c> names an arm it does not declare (<see cref="HarnessExit.UsageError"/>); or git could not be asked
    /// what it ignores in the tree (<see cref="HarnessExit.CommandFailed"/>), which no sync of it could be made without.
    /// </exception>
    internal async Task<SweepArms> ReadAsync(HarnessContext context, IReadOnlyList<string>? armNames, CancellationToken cancellationToken, bool sweeps = true)
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
        var file = TreeRegistryFiles.In(root, registry);

        if (!_fileSystem.FileExists(file))
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, $"mutations.registry names '{registry}', which is not a file in '{root}'.");
        }

        string rows;

        try
        {
            rows = _fileSystem.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // There, and not read: the registry's refusal, as one that is not there is - never a defect of this tool.
            throw new HarnessException(HarnessExit.ConfigInvalid, $"mutations.registry names '{registry}', which could not be read: {ex.Message}");
        }

        // What a sync withholds is in no copy of the tree, and a worker is such a copy, as a host's is: by the rules the
        // configuration names, which the registry and the text directory are held to as it is read, and by git's - what
        // git ignores a sync leaves behind as well, and only the tree says which that is. So it is asked here, as the
        // tree's view is made.
        var files = await TreeRegistryFiles.OfAsync(_syncService, _fileSystem, context, cancellationToken).ConfigureAwait(false);

        if (files.IsWithheld(PathPatterns.Normalize(registry)))
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"mutations.registry names '{registry}', which a sync withholds from every copy of the tree - {MutationRegistryValidation.Withholders} - so no "
                + "host sweeping a leg would hold it: keep it where a sync carries it.");
        }

        // Every problem of the registry in one refusal, and before --arms is weighed against it: a scope naming no leg is
        // the registry's whatever else is wrong with it, and whichever arms the command names - one it does not declare
        // among them, which is a mistake of the command and is said once the registry itself can be swept.
        var validation = await MutationRegistryValidation.ValidateAsync(rows, files, settings, context.Config, cancellationToken).ConfigureAwait(false);

        if (!validation.Valid)
        {
            throw new HarnessException(HarnessExit.ConfigInvalid, string.Join(Environment.NewLine, validation.Refusal(registry)));
        }

        var selected = ArmSelection.Resolve(validation.Registry, armNames);

        if (sweeps && selected.Any(arm => arm.Kind == RedKind.TestRed) && settings.ReportArgs is not { Count: > 0 })
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{MutationReport.Setting} is not set, and the sweep runs {MutationRegistryParser.TestRed} arms, whose binaries it "
                + "judges by the JUnit report each run writes: set it to the arguments that make the binary write one to "
                + $"{{{MutationReport.Placeholder}}}, such as [\"--gtest_output=xml:{{{MutationReport.Placeholder}}}\"].");
        }

        // A runner runReads names that no TEST-RED row names covers nothing: an entry for a runner that was renamed, or
        // spelled otherwise, would silently keep every key of the runner it meant to name.
        var named = validation.Registry.Arms
            .Where(arm => arm.Kind == RedKind.TestRed)
            .Select(arm => arm.Runner)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (settings.RunReads?.Keys.FirstOrDefault(runner => !named.Contains(runner)) is { } unnamed)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                $"{MutationSettings.RunReadsSetting} names runner '{unnamed}', which no {MutationRegistryParser.TestRed} row of '{registry}' names: "
                + "an entry no arm runs covers nothing. Name the runner as its A row does, or leave it out.");
        }

        return new SweepArms(validation.Registry, selected, validation.Scopes);
    }

    /// <summary>
    /// Refuses every one of <paramref name="legs"/> whose build a sweep cannot read: one that cannot be built, or is built
    /// by anything but CMake with the Ninja generator, whose own records are what says which objects each arm rebuilt.
    /// A self-test builds the fixture, a CMake project whatever the leg's own is: so the leg's project is not asked to
    /// be one, though the leg must still be one that can be built - a project to take its toolchain from where it names
    /// none - and its toolchain's generator Ninja.
    /// </summary>
    /// <param name="config">The configuration declaring the legs.</param>
    /// <param name="legs">The selected legs.</param>
    /// <param name="selfTest">Whether the run is a self-test.</param>
    /// <exception cref="HarnessException">Any of them is such a leg: one refusal names each, with its fix.</exception>
    internal static void RequireSweepable(HarnessConfig config, IEnumerable<SelectedLeg> legs, bool selfTest = false)
    {
        var problems = Unsweepable(config, legs, selfTest);

        if (problems.Count > 0)
        {
            throw new HarnessException(
                HarnessExit.ConfigInvalid,
                string.Join(Environment.NewLine, [$"{CommandName} cannot sweep every leg it was asked to, so nothing was run:", .. problems.Values.Select(why => $"  - {why}")]));
        }
    }

    /// <summary>
    /// Why each of <paramref name="legs"/> a sweep cannot read the build of cannot be swept, by its name, in the order
    /// given - as <see cref="RequireSweepable"/> refuses them; none where every one can be.
    /// </summary>
    /// <param name="config">The configuration declaring the legs.</param>
    /// <param name="legs">The selected legs.</param>
    /// <param name="selfTest">Whether the run is a self-test.</param>
    internal static IReadOnlyDictionary<string, string> Unsweepable(HarnessConfig config, IEnumerable<SelectedLeg> legs, bool selfTest = false)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(legs);

        var problems = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var leg in legs)
        {
            var project = VariantKey.ProjectFor(config, leg.Leg);
            var variant = VariantKey.For(config, leg.Leg, leg.Leg.Os);

            if (!variant.CanBuild(leg.Name, project, leg.Leg.Os, out var why))
            {
                problems[leg.Name] = why;
                continue;
            }

            if (!selfTest && !string.Equals(project.Type, "cmake", StringComparison.OrdinalIgnoreCase))
            {
                problems[leg.Name] = $"Leg '{leg.Name}' builds project '{project.Name}', a {project.Type} project: only a CMake build's ninja records say which objects a mutation rebuilt.";
                continue;
            }

            // A self-test builds the fixture, so it is the fixture's build system that must keep ninja's records.
            var generator = Build.BuildAdapters.For(selfTest ? MutationFixture.Project.Type : project.Type).GeneratorOf(config, variant.Toolchain);

            if (!string.Equals(generator, "Ninja", StringComparison.Ordinal))
            {
                problems[leg.Name] =
                    $"Leg '{leg.Name}' builds with toolchain '{variant.Toolchain}', whose generator is "
                    + (generator is null ? "not declared, so CMake picks one" : $"'{generator}'")
                    + $": set toolchains.{variant.Toolchain}.generator to \"Ninja\". A sweep reads which objects each mutation rebuilt "
                    + "from ninja's own records of the build, which only the Ninja generator keeps for one configuration alone.";
            }
        }

        return problems;
    }
}
