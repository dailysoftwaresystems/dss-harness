using System.Collections.Concurrent;
using System.Diagnostics;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.SetUps;
using DssHarness.Core.Sync;
using DssHarness.Core.Tools;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Runs;

/// <summary>What a leg-running command was asked to do.</summary>
/// <param name="Directory">The directory the command was invoked in.</param>
/// <param name="LegNames">The legs named with <c>--legs</c>, or null when it was left out.</param>
/// <param name="ForceLock">Whether to take a lock another run holds, on this host or another.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="UseStaged">Whether to act on what is already staged on a host, without syncing again.</param>
/// <param name="Time">Whether to report the profile timing.</param>
/// <param name="Here">
/// The host this machine is to the machine that dispatched the legs here, which runs every selected
/// leg on this machine, under that host's settings; <see langword="null"/> where this machine places
/// them itself. Without it the host would be free to dispatch the leg onward, putting the verdict
/// one further hop from the reader, and would read its settings as 'local' - which, in the
/// configuration the two machines share, is the one that dispatched it.
/// </param>
/// <param name="RemoteArguments">
/// The command's own options, passed on to a host running a leg for this run so that it runs the
/// same command. Never <c>--legs</c> or <c>--json</c>, which the dispatch supplies itself.
/// </param>
public sealed record LegRunRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    bool ForceLock = false,
    bool Json = false,
    bool UseStaged = false,
    bool Time = false,
    HostId? Here = null,
    IReadOnlyList<string>? RemoteArguments = null)
{
    /// <summary>
    /// What the command has each leg do, which decides the programs a host must have to be given
    /// one. Said by every command, because what one needs is not what another does.
    /// </summary>
    public required LegWorkload Workload { get; init; }

    /// <summary>
    /// The lock each leg's work takes, by the leg, the run and the command; <see langword="null"/> for the one a build of
    /// it takes - its tree shared and its variant its own (<see cref="PlacedLeg.BuildLock(RunId, string)"/>). A command whose work never
    /// touches the leg's tree or its build directory - a sweep of mutation arms, which works in copies of its own - takes
    /// a key of its own instead, so it never holds a build of the leg off for hours, nor waits for one.
    /// </summary>
    public Func<PlacedLeg, RunId, string, LockRequest>? Lock { get; init; }

    /// <summary>
    /// What the command exits with where no selected leg can run and no failure turned one away:
    /// <see cref="LegsExit.Unavailable"/>, as <c>legs</c> answers. A command whose legs reach a verdict that already
    /// decides that code says another, so that within the command no code has two meanings: a sweep's arms reach
    /// <c>violated</c>, which is 1.
    /// </summary>
    public int NothingRunsExit { get; init; } = LegsExit.Unavailable;

    /// <summary>
    /// Where the legs this machine runs itself work, for this run: as the configuration places each, or in place or in a
    /// copy for them all. Never sent to a host: a host's legs work in its copies whatever it says.
    /// </summary>
    public LocalTrees LocalTrees { get; init; } = LocalTrees.AsConfigured;

    /// <summary>
    /// The directory this run's verdicts of its mutation arms are recorded in, where it records them; <see langword="null"/>
    /// where it records none. Said once in its ledger, so a reader of the document knows where what it holds was kept.
    /// </summary>
    public string? MutationRecord { get; init; }

    /// <summary>
    /// What one dispatched leg is given of its own, asked once for a leg it dispatches and appended after
    /// <see cref="RemoteArguments"/>: the one way a dispatched leg is given an argument no other leg has - a sweep under
    /// <c>--pending</c> gives its host what the record holds of that leg's own arms. <see langword="null"/> where the
    /// command gives a dispatched leg nothing.
    /// </summary>
    public Func<PlacedLeg, IReadOnlyList<HostArgument>>? RemoteArgumentsOf { get; init; }

    /// <summary>
    /// What reads the notes one dispatched leg's host writes while it runs - each whole note, its kind and its text - or
    /// <see langword="null"/> where the command reads none. Asked once for a leg it dispatches, and only where the
    /// command's own run keeps what the notes carry: a sweep's notes feed its record of verdicts.
    /// </summary>
    public Func<PlacedLeg, Action<string, string>?>? HostNotes { get; init; }
}

/// <summary>One unit of a leg's work asking its machine to take it: an arm of a sweep.</summary>
/// <param name="Unit">The unit, as the machine's record of its heavy legs names it after its leg: <c>leg/unit</c>.</param>
/// <param name="Room">What the unit needs of the machine's room, where something says; <see langword="null"/> where nothing does.</param>
/// <param name="Settle">Whether it settles before it starts where another leg holds a slot (<see cref="AdmissionRequest.Settle"/>).</param>
public sealed record UnitAdmission(string Unit, RoomNeed? Room = null, bool Settle = true);

/// <summary>What one leg is asked to do once its tree is ready.</summary>
/// <param name="Leg">The placed leg.</param>
/// <param name="Context">The repository and its configuration.</param>
/// <param name="RunId">This run's id, which every log is scoped to.</param>
/// <param name="RunDirectory">Where this run's logs go.</param>
/// <param name="Time">Whether to report the profile timing.</param>
public sealed record LegWork(
    PlacedLeg Leg,
    HarnessContext Context,
    RunId RunId,
    string RunDirectory,
    bool Time)
{
    /// <summary>
    /// Asks the machine the leg's work runs on to take one unit of it, by the admission a heavy leg is taken by - where
    /// this process is on that machine and the machine declares admission - holding its slot, and any room it claimed,
    /// until the answer is disposed; <see langword="null"/> where nothing is asked. Asked by a command whose workload
    /// admits each unit (<see cref="LegWorkload.AdmitsEachUnit"/>), which holds no slot for the leg.
    /// </summary>
    public Func<UnitAdmission, CancellationToken, Task<Admission?>> AdmitUnit { get; init; } = (_, _) => Task.FromResult<Admission?>(null);

    /// <summary>Says what the leg is doing now, under its own name.</summary>
    public Action<string> Progress { get; init; } = _ => { };

    /// <summary>
    /// Which developer environment the leg's toolchain names was set up for its work, on the machine running it;
    /// <see langword="null"/> where it names none.
    /// </summary>
    public DeveloperEnvironmentFact? DeveloperEnvironment { get; init; }
}

/// <summary>
/// What every leg-running command shares: selecting legs, measuring the hosts, refusing what cannot
/// run, taking the locks, syncing each tree once, running the legs together, and reporting a ledger.
/// </summary>
/// <remarks>
/// One implementation for <c>build</c>, <c>test</c>, <c>run</c> and <c>check-mutations</c>, so the isolation
/// rules cannot hold for one command and not another. Each command supplies only what a leg actually does.
/// </remarks>
public sealed class LegRunService(
    IHarnessContextLoader contextLoader,
    LegsService legsService,
    LegExecutor legExecutor,
    RunLock runLock,
    LogOwnership logOwnership,
    ISyncService syncService,
    ISyncTransportFactory transportFactory,
    RemoteLegRunner remoteLegs,
    LegAdmission admission,
    IWslPageCache pageCache,
    IRepositoryTrees trees,
    KeepAwake keepAwake,
    DeveloperEnvironmentProvider developerEnvironments,
    ProgramFloors programFloors,
    Git.IGitClient gitClient,
    Platform.IPathBudget pathBudget,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    CommandOrigin? origin = null,
    LegSetUps? setUps = null)
{
    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegsService _legsService = legsService;
    private readonly LegExecutor _legExecutor = legExecutor;
    private readonly RunLock _runLock = runLock;
    private readonly LogOwnership _logOwnership = logOwnership;
    private readonly ISyncService _syncService = syncService;
    private readonly ISyncTransportFactory _transportFactory = transportFactory;
    private readonly RemoteLegRunner _remoteLegs = remoteLegs;
    private readonly LegAdmission _admission = admission;
    private readonly IWslPageCache _pageCache = pageCache;
    private readonly IRepositoryTrees _trees = trees;
    private readonly KeepAwake _keepAwake = keepAwake;
    private readonly DeveloperEnvironmentProvider _developerEnvironments = developerEnvironments;
    private readonly ProgramFloors _programFloors = programFloors;
    private readonly Git.IGitClient _gitClient = gitClient;
    private readonly Platform.IPathBudget _pathBudget = pathBudget;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

    /// <summary>Who the command runs for: on a host, the run of the machine that asked, which its legs' slots are recorded under.</summary>
    private readonly CommandOrigin _origin = origin ?? CommandOrigin.Typed;

    /// <summary>What brings a leg's set-ups up and down, where its run is asked to set up.</summary>
    private readonly LegSetUps? _setUps = setUps;

    /// <summary>
    /// Runs <paramref name="work"/> on every selected leg and reports the ledger.
    /// </summary>
    /// <param name="commandName">The command reporting, which prefixes every line it writes.</param>
    /// <param name="runId">
    /// The run, begun and said by the command that asks for it before anything could refuse it, which every record and
    /// the ledger name.
    /// </param>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="work">What one leg does once its tree is ready.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<CommandOutcome> RunAsync(
        string commandName,
        RunId runId,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        CancellationToken cancellationToken = default)
        => (await ExecuteAsync(commandName, runId, request, work, cancellationToken).ConfigureAwait(false))
            .ToOutcome(request.Json, _output.Shown);

    /// <summary>
    /// Runs <paramref name="work"/> on every selected leg and answers what it reached, in the terms its caller answers
    /// in: the use case the engine's build, test and run steps call, and the commands' own bodies below it.
    /// </summary>
    /// <param name="commandName">The command reporting, which prefixes every line it writes.</param>
    /// <param name="runId">
    /// The run, begun and said by the command that asks for it before anything could refuse it, which every record and
    /// the ledger name.
    /// </param>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="work">What one leg does once its tree is ready.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    public async Task<LegRunResult> ExecuteAsync(
        string commandName,
        RunId runId,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(work);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // Asked of the legs the command line selects, before any host is measured: a run asked to set up that nothing it
        // runs names a set-up for, or with a line a leg's system cannot start, would otherwise be found out leg by leg,
        // each after its tree was synced and its earlier set-ups brought up.
        if (request.Workload.SetUp)
        {
            SetUpsOrRefuse().Require(context.Config, context.Layout.RepositoryRoot, LegSelection.Resolve(context.Config, request.LegNames).Legs, request.Workload);
        }

        // Hosts are measured before anything runs, and DssHarness on each is brought to this
        // machine's build there, so a leg never starts on a host that turns out not to answer. A leg
        // goes where a sync puts its tree whatever this command starts, so a run on what is already
        // staged finds it there.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, request.Workload, request.Here, request.LocalTrees, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, _pathBudget, out var skipped);
        var factor = context.Config.Defaults.DurationWarningFactor;

        if (placed.Count == 0)
        {
            var nothing = LegRunPlan.NothingRuns(skipped, request.NothingRunsExit);

            // Before the run has a directory: there is none to name.
            return Stopped(commandName, request, runId, nothing.ExitCode, nothing.Message, LedgerReport.From(skipped, factor), nothing.Details ?? [], runDirectory: null);
        }

        // Before any copy is made: a tree whose .legs git does not ignore would have every copy a run made there committed
        // by git add -A, so the run is refused naming init, with nothing made - as a sweep is for the workers it makes
        // (SlotIgnored) - not even the run's directory of records. Not asked on what is already staged, which makes nothing.
        if (!request.UseStaged)
        {
            foreach (var tree in placed.Where(leg => leg.LocalCopy).GroupBy(leg => leg.TreeRoot, _platform.PathComparer()))
            {
                if (await SlotIgnored.RefusalAsync(
                        _gitClient,
                        tree.Key,
                        $"{HarnessLayout.LegsDirectoryName}/{tree.First().Variant.CopyKey}",
                        $"the copies of '{tree.Key}' its legs work in",
                        cancellationToken)
                    .ConfigureAwait(false) is { } unignored)
                {
                    throw new HarnessException(HarnessExit.Refused, $"{unignored} Nothing was run.");
                }
            }
        }

        var runDirectory = context.Layout.RunDirectory(runId.Value);
        var ledger = new LegLedger(_output, commandName, dispatched: request.Here is not null);

        IgnoreRunRecords(context.Layout);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        // Every refusal of the whole run is asked above: once taken, the claim is followed at once by the try below that
        // gives it up, so no run that ended here leaves it held for the next run to call abandoned.
        var claim = await _logOwnership.ClaimAsync(runDirectory, runId, request.ForceLock, cancellationToken).ConfigureAwait(false);

        if (!claim.Taken)
        {
            // Two runs writing one set of logs would each read the other's output as its own, which
            // is why this is its own verdict and its own exit code rather than a lock refusal - and
            // the verdict of every leg this run would have started.
            var held = $"another run owns '{runDirectory}': {claim.HeldBy}";

            return Stopped(
                commandName,
                request,
                runId,
                LegExit.LogHeld,
                held,
                LedgerReport.From([.. skipped, .. placed.Select(leg => leg.Entry(LegVerdict.LogHeld, held))], factor),
                [$"logs: {runDirectory}"],
                runDirectory,
                cause: OutcomeCause.LockHeld);
        }

        // Trees another run holds, by tree: a verdict for the legs that need one, as a variant
        // another run holds is, and no end to the legs that do not.
        var lockedTrees = new ConcurrentDictionary<string, string>(LegPlan.TreeKeyComparer);

        // The hosts' copies of the tree the legs' own copies are kept in, each synced once a run where it is not there yet.
        var holders = new ConcurrentDictionary<string, Lazy<Task<string?>>>(LegPlan.TreeKeyComparer);

        // Each tree as it was read for its copies, where any leg works in one.
        IReadOnlyDictionary<string, Task<SyncSource>>? sources = null;

        try
        {
            // A run on this machine that ended holding its own directory - most likely killed, or stopped with its
            // machine - may have written no verdict, and nothing would ever claim that directory again: said here, once,
            // and let go. Inside this try, so that whatever happens in it, this run's own directory is still given up.
            _logOwnership.ReleaseAbandoned(runDirectory);

            // Left out entirely where nothing is remote, or where the run acts on what each host already holds
            // (--use-staged), rather than supplied and made to do nothing: the executor reports a sync transition per
            // tree, and a run that never leaves this machine should not announce a transfer it did not make. Nothing is
            // read for carrying then either.
            Func<string, CancellationToken, Task>? syncTree = null;

            if (!request.UseStaged && placed.Any(leg => leg.Synced))
            {
                var read = sources = await ReadSourcesAsync(placed, commandName, cancellationToken).ConfigureAwait(false);

                syncTree = (treeKey, token) => SyncTreeAsync(context, placed, treeKey, runId, request.ForceLock, read, lockedTrees, holders, token);
            }

            LegExecution execution;

            try
            {
                execution = await _legExecutor
                    .RunAsync(
                        new LegExecutionRequest
                        {
                            Legs = [.. placed.Select(leg => leg.ToPlan())],
                            MaxParallelLegs = context.Config.Defaults.MaxParallelLegs,
                            MaxParallelLegsTotal = context.Config.Defaults.MaxParallelLegsTotal,
                            SyncTree = syncTree,
                            RunLeg = (plan, token) => RunLegAsync(
                                context, placed, plan, runId, runDirectory, request, work, commandName, ledger, lockedTrees, token),
                        },
                        ledger,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (HarnessException ex)
            {
                // The ledger is reported first, then the refusal. A leg that finished before another
                // leg's configuration turned out to be unsatisfiable still reached a verdict, and
                // throwing it away would make the reader run everything again to learn what they
                // already knew. The exit code is still the refusal's own. What a leg with no line left running is said too.
                var reached = ledger.Build(factor);

                return Stopped(
                    commandName,
                    request,
                    runId,
                    ex.ExitCode,
                    ex.Message,
                    reached,
                    [.. reached.Render(), .. Logs(runDirectory, reached, placed)],
                    runDirectory);
            }

            return Report(commandName, context, ledger, execution, runId, runDirectory, placed, request.MutationRecord);
        }
        finally
        {
            // What was packed of each tree's history for its copies goes with the reading of it.
            foreach (var read in sources?.Values.Where(read => read.IsCompletedSuccessfully) ?? [])
            {
                read.Result.Dispose();
            }

            await _logOwnership.ReleaseAsync(runDirectory, runId, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Makes the tree's runs directory ignore itself, before a run writes into it.
    /// </summary>
    /// <exception cref="HarnessException">The file could not be written: refused, naming it.</exception>
    /// <remarks>
    /// Whatever the tree's own .gitignore says; see <see cref="HarnessLayout.RunsIgnoreFile"/>. Written
    /// through the rule every file a run decides by is written through, so a runs directory nobody can
    /// write is refused here, naming it, as the claim below would refuse it.
    /// </remarks>
    private void IgnoreRunRecords(HarnessLayout layout)
    {
        var ignore = layout.RunsIgnoreFile;

        if (_fileSystem.FileExists(ignore))
        {
            return;
        }

        MachineWideFile.Written(
            $"The runs directory's own ignore file '{ignore}'",
            "Until it can be, a run's records would show in git status and be committed with the tree.",
            () =>
            {
                _fileSystem.CreateDirectory(layout.RunsDirectory);
                _fileSystem.WriteAllTextAtomic(ignore, HarnessLayout.SelfIgnoreRule);
            });
    }

    /// <summary>
    /// Syncs one host's tree, once, however many legs share it.
    /// </summary>
    /// <remarks>
    /// Legs of one tree on one host share its copy there. If each synced it they would race over the
    /// same files, and the leg that lost would build sources another leg was halfway through
    /// replacing. The copy is taken exclusively while it is rewritten, and shared afterwards while
    /// the variants build.
    /// </remarks>
    private async Task SyncTreeAsync(
        HarnessContext context,
        IReadOnlyList<PlacedLeg> placed,
        string treeKey,
        RunId runId,
        bool force,
        IReadOnlyDictionary<string, Task<SyncSource>> sources,
        ConcurrentDictionary<string, string> lockedTrees,
        ConcurrentDictionary<string, Lazy<Task<string?>>> holders,
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => LegPlan.TreeKeyComparer.Equals(candidate.TreeKey, treeKey));

        if (!leg.Synced)
        {
            return;
        }

        // The tree this leg declares, as the run read it when it began - not whatever tree the command was typed in. A
        // leg naming a worktree measures that worktree; sending the main checkout instead would report the worktree's
        // name over the main checkout's sources. A tree that could not be read raises its failure here, in the sync of
        // each copy made from it, where that copy's legs report it.
        var source = await sources[leg.TreeRoot].ConfigureAwait(false);

        // A copy of the leg's own on a host is kept in the host's copy of the tree, which every command working in that
        // copy - a clean, a test, a deletion - expects to hold the tree: synced first where it is not there yet, once a
        // run however many legs keep their copies in it.
        if (leg.KeptIn is { } keptIn
            && await holders
                .GetOrAdd(
                    CompositeKey.Of(leg.Host.Host.ToString(), keptIn),
                    _ => new(() => SyncHolderAsync(context, leg, keptIn, source, runId, force, cancellationToken)))
                .Value
                .ConfigureAwait(false) is { } holderHeld)
        {
            lockedTrees[treeKey] = holderHeld;
            return;
        }

        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                new LockRequest
                {
                    Host = leg.Host.Host.ToString(),

                    // The tree on the host, which is what the legs sharing this sync also lock.
                    // Locked by the source instead, the exclusive hold taken while the copy is
                    // replaced and the shared holds taken while its variants build would sit in
                    // two different key spaces and never exclude each other.
                    Tree = leg.HostTreeRoot,
                    Scope = LockScope.TreeExclusive,
                    RunId = runId,
                    Command = SyncService.CommandName,
                    Force = force,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            // About this tree and this moment, as a variant another run holds is: each leg that needs
            // the tree records it as refused-locked, and the legs on other trees still report. Raised
            // from here it ended the whole run, as though it were a configuration every leg shares.
            // A lock file nobody can use is not this, and is raised as the refusal of the run it is.
            lockedTrees[treeKey] = attempt.HeldBy!;
            return;
        }

        await using (handle)
        {
            // A transport that will not start is reported by the runner that starts it, as that host
            // being unavailable. A file that moved since the run began raises as inputs-moved, the
            // verdict of this copy's legs alone, and nothing of them runs.
            // Marked with this run as the copy is finished, so each leg that takes it afterwards finds whether it still
            // holds this run's reading. A leg's own copy on this machine is never taken over: a directory this tool did
            // not make at its path is refused naming what a person can do, not --adopt.
            await _syncService
                .SyncAsync(
                    source,
                    _transportFactory.For(leg.Host),
                    leg.HostTreeRoot,
                    new SyncOptions { Run = runId, Adoptable = !leg.LocalCopy && leg.KeptIn is null, KeptIn = leg.KeptIn },
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Syncs the tree to <paramref name="keptIn"/>, the host's copy of it <paramref name="leg"/>'s own copy is kept in,
    /// where it is not there yet as a copy this tool finished - under the lock every sync of that copy takes, so no run's
    /// sync of it runs beside this one - and answers who holds that copy where another run does, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// One that is there, finished, is left as it is, and no lock taken: it may be in use, by a run whose tree a sync would
    /// move under it - the reason the leg works in a copy of its own. One a sync began and did not finish is synced as any
    /// command would; and one a takeover began, or a directory this tool did not make, is refused as any sync of a host's
    /// copy refuses it, naming --adopt, rather than having a copy made inside it.
    /// </remarks>
    private async Task<string?> SyncHolderAsync(
        HarnessContext context,
        PlacedLeg leg,
        string keptIn,
        SyncSource source,
        RunId runId,
        bool force,
        CancellationToken cancellationToken)
    {
        var transport = _transportFactory.For(leg.Host);

        var found = await transport.InspectAsync(keptIn, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (found is { Exists: true, Mark: CopyMark.Complete })
        {
            return null;
        }

        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                new LockRequest
                {
                    Host = leg.Host.Host.ToString(),
                    Tree = keptIn,
                    Scope = LockScope.TreeExclusive,
                    RunId = runId,
                    Command = SyncService.CommandName,
                    Force = force,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            return attempt.HeldBy!;
        }

        await using (handle)
        {
            // Said where the sync goes on to write: a directory a takeover began, or this tool did not make, it refuses,
            // and that refusal says what it is.
            if (!found.Exists || found.Mark == CopyMark.Unfinished)
            {
                _output.Info(
                    SyncService.CommandName,
                    $"{leg.Host.Host}: '{keptIn}' {(found.Exists ? "is a copy whose last sync did not finish" : "is not there yet")}, so the tree is "
                    + "synced to it first, to keep the legs' own copies in");
            }

            // The host's copy, which --adopt takes over as any sync of it does: a directory this tool did not make there is
            // refused naming that, never the remedy of a leg's own copy on this machine.
            await _syncService
                .SyncAsync(source, transport, keptIn, new SyncOptions { Run = runId }, cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>
    /// Reads, once each, the trees the legs among <paramref name="placed"/> that work in a synced copy are synced from, all of them before
    /// any leg's work, so every copy of a tree is made from the one reading (see
    /// <see cref="ISyncService.ReadSourceAsync"/>), and says which commit each was read at.
    /// </summary>
    /// <returns>Each tree's reading by its root, or what stopped it.</returns>
    /// <remarks>
    /// Read side by side, and waited for whether each was read or not: a tree that could not be read - git would not
    /// answer in it, a file in it could not be opened, a file in it moved while it was read - raises nothing here, but in
    /// the sync of each copy made from it, while the legs on other trees and on this machine still run.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Task<SyncSource>>> ReadSourcesAsync(
        IReadOnlyList<PlacedLeg> placed,
        string commandName,
        CancellationToken cancellationToken)
    {
        var comparer = _platform.PathComparer();
        var trees = placed
            .Where(leg => leg.Synced)
            .Select(leg => leg.TreeRoot)
            .Distinct(comparer)
            .ToList();

        // Said, because a large tree takes a while to read, and nothing else is said until it is.
        _output.Info(commandName, $"reading {ReportText.Listed([.. trees.Select(tree => $"'{tree}'")])} for {(trees.Count == 1 ? "its" : "their")} copies");

        var sources = trees.ToDictionary(
            tree => tree,
            tree => Task.Run(() => _syncService.ReadSourceAsync(tree, cancellationToken), cancellationToken),
            comparer);

        await Task.WhenAll(sources.Values.Cast<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        // Which tree each copy will hold, as it was read: what each leg on one of them names again with its verdict.
        foreach (var tree in trees.Where(tree => sources[tree].IsCompletedSuccessfully))
        {
            _output.Info(commandName, sources[tree].Result.Read.Said);
        }

        return sources;
    }

    /// <summary>
    /// What a run ends with when something other than its legs ended it, with the legs that had a
    /// line by then.
    /// </summary>
    /// <param name="commandName">The command the run reports under.</param>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="runId">The run, which its document names whatever ended it.</param>
    /// <param name="exitCode">What the process exits with.</param>
    /// <param name="message">The line it ends on.</param>
    /// <param name="reached">
    /// The ledger so far: the legs' lines, and what a leg with none left running, which the table form says after them.
    /// </param>
    /// <param name="details">What the table form says beneath the line.</param>
    /// <param name="runDirectory">
    /// Where the run keeps its records, once it has a directory; <see langword="null"/> before.
    /// </param>
    /// <param name="cause">Why the run ended as it did, where its exit code and its lines cannot say.</param>
    /// <remarks>
    /// Asked for data, the ledger is the whole of standard output whatever ended the run: the document,
    /// with the code and the line the process ends on. Written as text instead - a table, a list of
    /// reasons - it reached the machine that dispatched the leg as a host whose answer could not be
    /// read, where the host had said exactly what happened.
    /// </remarks>
    private static LegRunResult Stopped(
        string commandName,
        LegRunRequest request,
        RunId runId,
        int exitCode,
        string message,
        LedgerReport reached,
        IReadOnlyList<string> details,
        string? runDirectory,
        OutcomeCause cause = OutcomeCause.None)
        => new(
            commandName,
            runId,
            runDirectory,
            reached,
            reached.Lines,
            exitCode,
            message,
            Cancelled: exitCode == HarnessExit.Cancelled,
            Unfinished: [],
            [.. details, .. reached.LeftRunningSaid],
            Stopped: true,
            request.MutationRecord)
        {
            Cause = cause,
        };

    /// <summary>
    /// Where a run's records are, as its text form says it: this run's directory, then each leg's own directory of
    /// records and the logs in it - a host's leg's as the host spelled it - since a host runs a leg under a run of its own.
    /// </summary>
    private static IEnumerable<string> Logs(string runDirectory, LedgerReport report, IReadOnlyList<PlacedLeg> placed)
    {
        yield return $"logs: {runDirectory}";

        foreach (var line in report.Lines.Where(line => line.Records is { Length: > 0 }))
        {
            var host = placed.FirstOrDefault(leg => leg.Name == line.Leg)?.Host.Host;

            yield return LegRecords.Said(line.Leg, host, line.Records!, [.. (line.Logs ?? []).Select(log => Path.GetFileName(log.File))]);
        }
    }

    private async Task<LegEntry?> RunLegAsync(
        HarnessContext context,
        IReadOnlyList<PlacedLeg> placed,
        LegPlan plan,
        RunId runId,
        string runDirectory,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        string commandName,
        LegLedger ledger,
        ConcurrentDictionary<string, string> lockedTrees,
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => candidate.Name == plan.Name);
        var started = Stopwatch.GetTimestamp();

        // Where the leg works, on every line it ends with: its tree, or a copy of it - a host's, which this machine makes.
        var tree = leg.Synced && request.Here is null
            ? new WorkedTree(leg.HostTreeRoot) { CopyOf = leg.TreeRoot }
            : new WorkedTree(leg.HostTreeRoot);

        if (lockedTrees.TryGetValue(leg.TreeKey, out var treeHeld))
        {
            return Ended(leg, LegVerdict.RefusedLocked, treeHeld, started) with { Tree = tree };
        }

        // The tree shared and this variant exclusive: variants build side by side, but never while
        // their sources are being replaced. A command keyed apart takes its own.
        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                (request.Lock?.Invoke(leg, runId, ledger.CommandName) ?? leg.BuildLock(runId, ledger.CommandName)) with { Force = request.ForceLock },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            // The one refusal that is a verdict rather than an end to the run: it is about this leg
            // and this moment, so the other legs still report, and one locked leg never hides them.
            // A lock file nobody can use is raised instead, as the refusal of the run it is.
            return Ended(leg, LegVerdict.RefusedLocked, attempt.HeldBy!, started) with { Tree = tree };
        }

        // Every other refusal is left to propagate. A configuration a leg cannot satisfy — an
        // undeclared program in an action file, a project that names no toolchain — is the same
        // fact for every leg, and turning it into a per-leg verdict would report it as many times
        // as there are legs, under a verdict that named the wrong cause and an exit code that said
        // a lock was held.
        await using (handle)
        {
            // The copy, once the leg holds it: what it holds now is what the leg would test. Read by this machine, which
            // made it - a host is never told whose reading its copy should hold.
            if (tree.CopyOf is not null)
            {
                var (held, moved) = await CopyHeldAsync(leg, runId, request.UseStaged, cancellationToken).ConfigureAwait(false);

                tree = tree with { Reading = held, Staged = request.UseStaged };

                if (moved is not null)
                {
                    return Ended(leg, LegVerdict.InputsMoved, moved, started) with { Tree = tree };
                }
            }

            // A heavy leg waits here, once its tree is synced and its lock taken, until its machine takes it: another run of
            // its variant is refused-locked meanwhile, as it would be while it ran. Its slot is given back when its work
            // ends, and before the lock is.
            using var admitted = await AdmitAsync(context, leg, request, runId, ledger, cancellationToken).ConfigureAwait(false);

            if (admitted is { Refusal: { } refusal })
            {
                return Ended(leg, LegVerdict.NotAdmitted, refusal, started) with { Admission = admitted.Fact, Tree = tree };
            }

            var entry = await RunTakenLegAsync(context, leg, runId, runDirectory, request, work, commandName, ledger, started, cancellationToken).ConfigureAwait(false);

            return (admitted is null ? entry : entry with { Admission = admitted.Fact }) with { Tree = tree };
        }
    }

    /// <summary>
    /// What <paramref name="leg"/>'s copy holds now that the leg holds it, and why the leg is to test nothing there; no
    /// reason where it holds the tree the leg is to test.
    /// </summary>
    /// <remarks>
    /// The copy's sync releases it before its legs take it, and another command's sync of the same copy can run in
    /// between: the later leg would then test that command's tree under this run's name. So each leg asks the copy, under
    /// its own lock, whose reading it holds - this run's, or nothing it runs. A run on what is staged synced nothing, and
    /// tests what the copy holds: one a sync or a takeover of began and did not finish holds no tree a run began with,
    /// and one that is not there, or that no sync of this tool marked, holds nothing current either. Either way nothing of
    /// the leg runs.
    /// </remarks>
    private async Task<(CopyReading? Held, string? Moved)> CopyHeldAsync(PlacedLeg leg, RunId runId, bool staged, CancellationToken cancellationToken)
    {
        var found = await _transportFactory.For(leg.Host).InspectAsync(leg.HostTreeRoot, cancellationToken: cancellationToken).ConfigureAwait(false);
        var copy = $"{leg.Named}: '{leg.HostTreeRoot}'";

        if (staged)
        {
            var mark = found.Exists ? found.Mark : CopyMark.None;

            return (found.Reading, SyncService.PartMade(mark) is { } partMade
                ? $"{copy} {partMade}: --use-staged has nothing current to run there. "
                    + (mark == CopyMark.AdoptionStopped
                        ? $"Finish taking it over with '{ToolPackage.Command} sync --adopt \"{leg.Named}\"', and run again."
                        : "Run without --use-staged, which syncs it first.")
                : mark == CopyMark.None
                    ? $"{copy} holds no copy this tool made: --use-staged has nothing current to run there. Run without --use-staged, which syncs it first."
                    : null);
        }

        if (found is { Exists: true, Mark: CopyMark.Complete, Reading: { } held } && held.IsBy(runId))
        {
            return (held, null);
        }

        // The tree with its host, as every line names one - RunLock.TreeNamed, the one owner of that shape.
        var named = RunLock.TreeNamed(leg.Named.ToString(), leg.HostTreeRoot, variant: null);

        return (null, found is { Exists: true, Mark: CopyMark.Complete }
            ? $"{named} was synced again{(found.Reading?.ReadBy is { } by ? $" by {RunLine.Message(by)}" : string.Empty)} after "
                + "this run made it the tree it read, so it no longer holds that tree."
            : $"{named} no longer holds the tree this run read into it: "
                + (found.Exists && SyncService.PartMade(found.Mark) is { } stopped ? $"it {stopped}." : "it is not there, or no sync of this tool finished it."));
    }

    /// <summary>
    /// Asks the machine <paramref name="leg"/>'s work runs on to take it, where this process is on that machine, the
    /// machine declares admission, and the leg is heavy - or runs in a WSL distribution under a workload admitting each
    /// unit of its work, which this machine takes whole, its units asking nothing there; <see langword="null"/> where
    /// any of those is not so.
    /// </summary>
    /// <remarks>
    /// Asked by a process on the machine the work runs on, which outlives that work: this one, for a leg of this machine
    /// or of one of its WSL distributions, which run on it and are counted against its memory; the host's own, for a leg
    /// on an ssh host, which is a machine of its own. A distribution running a leg this machine dispatched to it asks
    /// nothing: this machine's process took it before dispatching it, and the distribution's own figures do not show this
    /// machine's memory.
    /// </remarks>
    private Task<Admission?> AdmitAsync(
        HarnessContext context,
        PlacedLeg leg,
        LegRunRequest request,
        RunId runId,
        LegLedger ledger,
        CancellationToken cancellationToken)
    {
        // Heavy as it is on this leg's system: a heavy step limited by runOn, or one that builds, makes the legs of those
        // systems alone heavy. A workload admitting each unit of its work holds no slot for the leg - but for a leg in a
        // WSL distribution, which this machine takes whole, its units asking nothing there.
        var workload = request.Workload.On(leg.Leg.Os);

        return workload.Heavy || (workload.AdmitsEachUnit && leg.Named.Kind == HostKind.Wsl)
            ? AskAsync(context, leg, request, runId, ledger, leg.Name, leg.Need, settle: true, cancellationToken)
            : Task.FromResult<Admission?>(null);
    }

    /// <summary>
    /// Asks the machine <paramref name="leg"/>'s work runs on to take <paramref name="asking"/> - the leg, or a unit of its
    /// work - where this process is on that machine and the machine declares admission; <see langword="null"/> where either
    /// is not so.
    /// </summary>
    /// <remarks>See <see cref="AdmitAsync"/> for which process asks, on which machine.</remarks>
    private async Task<Admission?> AskAsync(
        HarnessContext context,
        PlacedLeg leg,
        LegRunRequest request,
        RunId runId,
        LegLedger ledger,
        string asking,
        RoomNeed? room,
        bool settle,
        CancellationToken cancellationToken)
    {
        if ((request.Here is null && leg.Host.Host.Kind == HostKind.Ssh) || request.Here is { Kind: HostKind.Wsl })
        {
            return null;
        }

        if (AdmissionRuleOf(context, leg) is not { } rule)
        {
            return null;
        }

        // On an ssh host, under the run of the machine that dispatched the leg: its command's other legs there ask under
        // it too, and a wait for slots only they hold does not count.
        return await _admission
            .AdmitAsync(
                new AdmissionRequest(
                    rule,
                    _origin.Dispatched?.RunId?.Value ?? runId.Value,
                    ledger.CommandName,
                    asking,
                    leg.Host.Host.ToString(),
                    leg.HostTreeRoot,
                    leg.Variant.DirectoryName,
                    message => ledger.Transition(leg.Name, message),
                    room)
                {
                    Settle = settle,

                    // What this machine counts as in use and could have back: the page cache of WSL's virtual machine, which
                    // holds what every distribution's builds read and wrote until it idles for minutes.
                    DropPageCache = token => _pageCache.DropAsync(context, token),
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The least room <paramref name="leg"/>'s builds here leave free on each filesystem they fill - its machine's
    /// <c>admission.minFreeGiB</c>, where that machine declares admission and the leg's work is heavy or admits each of
    /// its units - or <see langword="null"/>, and nothing stops its builds for room.
    /// </summary>
    /// <remarks>
    /// The machine is the one the leg's work runs on, as its admission reads it: for a WSL distribution's leg, the Windows
    /// machine it runs on, whose drive its disk grows on. That drive is held to the floor as well, through its mount in
    /// the distribution, where the machine that sent the leg named it - the distribution's own room is its virtual disk's,
    /// which a full drive does not shrink - and where it was not, or is mounted nowhere here, the build is told why.
    /// </remarks>
    private RoomFloor? FloorOf(HarnessContext context, PlacedLeg leg, LegRunRequest request)
    {
        var workload = request.Workload.On(leg.Leg.Os);

        if (!(workload.Heavy || workload.AdmitsEachUnit) || AdmissionRuleOf(context, leg) is not { MinFreeBytes: > 0 } rule)
        {
            return null;
        }

        if (request.Here is not { Kind: HostKind.Wsl })
        {
            return RoomFloor.Of(leg.Name, rule.MinFreeBytes);
        }

        if (_origin.Dispatched?.DiskImageDrive is not { } drive)
        {
            return RoomFloor.Unwatching(
                leg.Name,
                rule.MinFreeBytes,
                "the machine that sent this leg named no drive where WSL keeps this distribution's disk, so only the distribution's "
                + "own room is held to admission.minFreeGiB");
        }

        var (mount, why) = WindowsDriveMounts.Of(_fileSystem, drive);

        return mount is not null
            ? RoomFloor.Beside(leg.Name, rule.MinFreeBytes, mount, ", where WSL keeps its disk")
            : RoomFloor.Unwatching(
                leg.Name,
                rule.MinFreeBytes,
                $"the drive where WSL keeps this distribution's disk, {drive}, could not be found here, so only the distribution's own "
                + $"room is held to admission.minFreeGiB: {why}");
    }

    /// <summary>
    /// The admission rule of the machine <paramref name="leg"/>'s work runs on, or <see langword="null"/> where it declares
    /// none: for a WSL distribution's leg, the Windows machine it runs on, whose memory its virtual machine takes and whose
    /// drive its disk grows on. Its slots, its floor and its page cache all answer to that one rule.
    /// </summary>
    private static AdmissionRule? AdmissionRuleOf(HarnessContext context, PlacedLeg leg)
        => AdmissionSettings.RuleFor(
            (leg.Named.Kind == HostKind.Wsl ? context.Config.Hosts.Local : leg.HostSettings).Admission,
            context.Config.Defaults.Admission);

    /// <summary>The trees of <paramref name="trees"/> but the one <paramref name="leg"/> works in: the repository's other trees here.</summary>
    private RepositoryTreesFound Beside(RepositoryTreesFound trees, PlacedLeg leg)
        => trees with { Trees = [.. trees.Trees.Where(tree => !PathContainment.AreSame(tree.Root, leg.HostTreeRoot, _platform.PathComparison))] };

    /// <summary>
    /// <paramref name="leg"/>'s line, reaching <paramref name="verdict"/> for <paramref name="detail"/> before its own work
    /// ran, and how long it took since it was <paramref name="started"/>.
    /// </summary>
    private static LegEntry Ended(PlacedLeg leg, LegVerdict verdict, string detail, long started)
        => leg.Entry(verdict, detail) with { Duration = Stopwatch.GetElapsedTime(started) };

    /// <summary>What brings a leg's set-ups up and down: given to every service a run asked to set up is made with.</summary>
    private LegSetUps SetUpsOrRefuse()
        => _setUps ?? throw new InvalidOperationException("A run asked to set up was given nothing to set up with.");

    /// <summary>
    /// <see cref="LegSetUps.Option"/> for a dispatched leg whose work names a set-up, where the run is asked to set up:
    /// the host brings them up, around the work it runs. Never for one whose work names none - its host would refuse an
    /// option that changes nothing for it.
    /// </summary>
    private static string[] SetUpOption(HarnessContext context, PlacedLeg leg, LegRunRequest request)
        => request.Workload.SetUpsOf(context.Config, leg.Leg).Count > 0 ? [LegSetUps.Option] : [];

    /// <summary>What <paramref name="leg"/> does once its lock is taken and, where it is heavy, its machine has taken it.</summary>
    private async Task<LegEntry> RunTakenLegAsync(
        HarnessContext context,
        PlacedLeg leg,
        RunId runId,
        string runDirectory,
        LegRunRequest request,
        Func<LegWork, CancellationToken, Task<LegEntry>> work,
        string commandName,
        LegLedger ledger,
        long started,
        CancellationToken cancellationToken)
    {
        // A leg placed on another machine runs on that machine. Doing the work here instead
        // would produce a verdict about the machine that typed the command, under the name of
        // the leg that was supposed to check a different one — which is the whole failure a
        // harness exists to prevent, wearing a green colour.
        if (leg.Dispatched && request.Here is null)
        {
            // What one dispatched leg is given of its own, after the command's own options, and what reads the notes its
            // host writes while it runs: asked of it alone, here, once - no other leg is asked for.
            var own = request.RemoteArgumentsOf?.Invoke(leg) ?? [];
            var onNote = request.HostNotes?.Invoke(leg);
            var ended = await _remoteLegs
                .RunAsync(
                    commandName,
                    leg,
                    [.. request.RemoteArguments ?? [], .. SetUpOption(context, leg, request), .. own],
                    runId,
                    onNote,
                    facts => ledger.SetUpsEnded(leg.Name, facts),
                    cancellationToken)
                .ConfigureAwait(false);

            // What a WSL leg read and wrote stays in its virtual machine's page cache, which this machine counts as in use
            // until the virtual machine idles for minutes: dropped as the leg ends - before its slot is given back - where
            // this machine admits its heavy legs by the memory. Not waited for: what comes back is the next wait's to read.
            // Nothing to drop is said too: the leg had just run in a distribution a host reaches, so WSL listing none of them
            // running leaves its cache where it was.
            if (leg.Named.Kind == HostKind.Wsl && AdmissionRuleOf(context, leg) is not null)
            {
                try
                {
                    var dropped = await _pageCache.DropAsync(context, cancellationToken).ConfigureAwait(false);

                    ledger.Transition(
                        leg.Name,
                        "as the leg ended, " + (dropped?.Said
                            ?? "WSL's page cache was not dropped: WSL listed no distribution a WSL host reaches as running, though this leg had just run in one"));
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The leg's verdict is in hand, and a stop asked for while the cache drops takes nothing of it: the run
                    // ends as a stopped run does, with this leg reported and recorded.
                    ledger.Transition(leg.Name, "as the leg ended, WSL's page cache was not dropped: the run was stopped first");
                }
            }

            return ended;
        }

        // Held awake for as long as the leg's own work runs here, by the command this machine
        // declares - under the section the machine that dispatched the leg knows it by.
        await using var awake = _keepAwake.Hold(commandName, leg.Name, leg.HostSettings, leg.Host.ProgramDirectories.All, cancellationToken);

        var environmentName = LegPrograms.DeveloperEnvironmentOf(context.Config, leg.Leg, request.Workload);
        DeveloperEnvironmentSetup? setUp = null;

        if (environmentName is not null)
        {
            setUp = await SetUpDeveloperEnvironmentAsync(environmentName, leg, ledger, cancellationToken).ConfigureAwait(false);

            if (setUp.HasFailed)
            {
                // The survey found the instance and the leg was placed here for it, so an environment
                // that will not set up now is the leg failing, as a program that will not start once a
                // leg began is: read as a skip, a gate that accepts an incomplete run passed it.
                return Ended(leg, LegVerdict.Failed, setUp.Failure, started);
            }

            if (await MissingInDeveloperEnvironmentAsync(context.Config, leg, request.Workload, environmentName, setUp, cancellationToken).ConfigureAwait(false) is { } missing)
            {
                return Ended(leg, missing.Verdict, missing.Reason, started) with { DeveloperEnvironment = setUp.Fact };
            }

            leg = leg with { DeveloperEnvironment = setUp.Environment };
        }

        // The repository's other trees here, whose build directory a process found beside the leg works in is told by,
        // listed as its work begins: a worktree made since the run began - an agent's, by its orchestrator - is among them.
        var working = leg with
        {
            Floor = FloorOf(context, leg, request),
            Beside = Beside(await _trees.HereAsync(context, request.Here, cancellationToken).ConfigureAwait(false), leg),
        };

        // A leg that works in a copy of its tree runs by the configuration and the actions of the reading: a context made
        // for the copy, whose tree is the copy and whose machine state - values, secrets, the lock - stays where this
        // machine keeps it. Records stay in the run's own directory, as they do for a host's leg.
        HarnessContext runsIn = context;
        var runsAs = working;

        if (working.LocalCopy)
        {
            runsIn = _contextLoader.ForCopy(context, working.HostTreeRoot);
        }

        Task<LegEntry> Work(CancellationToken token) => work(
            new LegWork(runsAs, runsIn, runId, runDirectory, request.Time)
            {
                AdmitUnit = (unit, admitted) => AskAsync(context, runsAs, request, runId, ledger, $"{runsAs.Name}/{unit.Unit}", unit.Room, unit.Settle, admitted),
                Progress = message => ledger.Transition(runsAs.Name, message),
                DeveloperEnvironment = setUp?.Fact,
            },
            token);

        // A leg's set-ups come up in the tree its work runs in, on this machine - the copy, the host's copy, the tree -
        // before that work starts, and go down after it, whatever it ended with: as the configuration of the reading its
        // work runs by declares them.
        var setUps = request.Workload.SetUpsOf(runsIn.Config, working.Leg);
        var entry = setUps.Count == 0
            ? await Work(cancellationToken).ConfigureAwait(false)
            : await SetUpsOrRefuse()
                .AroundAsync(
                    runsIn.Config,
                    working,
                    setUps,
                    runId,
                    runDirectory,
                    await _contextLoader.RepositoryOfAsync(runsIn, cancellationToken).ConfigureAwait(false),
                    message => ledger.Transition(working.Name, message),
                    (verdict, detail) => Ended(working, verdict, detail, started),
                    facts => ledger.SetUpsEnded(working.Name, facts),
                    Work,
                    cancellationToken)
                .ConfigureAwait(false);

        entry = setUp is null ? entry : entry with { DeveloperEnvironment = setUp.Fact };

        // The remedy a leg in place that ended inputs-moved is owed: where the command could have run it on a copy, one
        // name says how. Never for a leg that worked in one, nor under a command that takes no such option, whose reader
        // the remedy would send nowhere - nor where the placement's own rule puts the leg in no copy of its own whatever
        // the command says: a leg another machine sent here, or one typed in a copy a sync made, works in a copy already.
        if (entry.Verdict == LegVerdict.InputsMoved
            && !working.LocalCopy
            && !working.Dispatched
            && request.LocalTrees != LocalTrees.InPlace
            && LegTrees.InLocalCopy(context, working.Leg, request.Here, LocalTrees.Copy))
        {
            entry = entry with
            {
                Detail = $"{entry.Detail}; the leg worked in '{working.TreeRoot}' itself - legs.{working.Name}.localCopy, "
                    + "defaults.localCopy for every leg, or --local-copy for one run, runs it on a copy a later write does not reach",
            };
        }

        // Where the leg's own records are, named on its line and in the ledger from what is there: this machine's run
        // kept them, whatever tree the work happened in. A dispatched leg's are the host's to name, as its whole line is.
        if (!working.Dispatched)
        {
            var records = new LegRecords(runDirectory, working.Name);
            var logs = records.Logs(_fileSystem);

            entry = _fileSystem.DirectoryExists(records.Directory)
                ? entry with { Records = records.Directory, Logs = logs }
                : entry with { Records = null, Logs = null };
        }

        return entry;
    }

    /// <summary>
    /// The programs <paramref name="leg"/> starts that the PATH its developer environment set up does
    /// not hold, said as one reason with what the run records for it, or <see langword="null"/> where
    /// it holds them all.
    /// </summary>
    /// <remarks>
    /// The survey could not require them: that PATH exists only once the environment is set up, here,
    /// now. So they are looked for then, before anything of the leg starts, and one that is missing is
    /// a tool missing, named, as the survey names one it can see - never a program failing to start
    /// halfway through a build. Visual Studio carries cl and link, and CMake and Ninja only where its
    /// CMake component is installed. A declared tool is held to its minimum version as the survey holds
    /// one, in the copy that PATH names: the one the leg starts, a CMake Visual Studio carries older than
    /// the configuration needs among them.
    /// </remarks>
    private async Task<(string Reason, LegVerdict Verdict)?> MissingInDeveloperEnvironmentAsync(
        HarnessConfig config,
        PlacedLeg leg,
        LegWorkload workload,
        string name,
        DeveloperEnvironmentSetup setUp,
        CancellationToken cancellationToken)
    {
        var programs = LegPrograms.InDeveloperEnvironment(config, leg.Leg, workload, leg.HostSettings);
        var found = await _programFloors
            .HoldOnAsync(
                PathSearch.For(PhaseEnvironment.Layered(leg.HostSettings.Env, setUp.Environment), leg.Host.ProgramDirectories),
                programs,
                ToolFloor.Of(config.Tools),
                cancellationToken)
            .ConfigureAwait(false);

        return LegPlacement.MissingPrograms(
            programs,
            found.GetValueOrDefault,
            $"the PATH developer environment '{name}' sets up",
            LegPlacement.FloorRemedy(config, leg.Name, leg.Leg, leg.Host.Os));
    }

    /// <summary>
    /// Sets up, on this machine, the developer environment <paramref name="name"/> that
    /// <paramref name="leg"/> starts its work in.
    /// </summary>
    /// <remarks>
    /// Here, on the machine that runs the leg, and never on the one that placed it: what Visual
    /// Studio sets up is that machine's own paths. The survey asked this machine about the same
    /// environment, through <see cref="LegPrograms.DeveloperEnvironmentOf"/>, and the leg was placed
    /// here because it found an instance, so that instance is the one set up.
    /// </remarks>
    private async Task<DeveloperEnvironmentSetup> SetUpDeveloperEnvironmentAsync(
        string name,
        PlacedLeg leg,
        LegLedger ledger,
        CancellationToken cancellationToken)
    {
        ledger.Transition(leg.Name, $"setting up developer environment '{name}'");

        return await _developerEnvironments
            .SetUpAsync(name, leg.Host.DeveloperEnvironments[name], leg.Variant.Processor, leg.HostSettings.Env, cancellationToken)
            .ConfigureAwait(false);
    }

    private LegRunResult Report(
        string commandName,
        HarnessContext context,
        LegLedger ledger,
        LegExecution execution,
        RunId runId,
        string runDirectory,
        IReadOnlyList<PlacedLeg> placed,
        string? mutationRecord = null)
    {
        var report = ledger.Build(context.Config.Defaults.DurationWarningFactor);

        // One code and one line, whichever form the ledger is shown in. Deciding them per branch
        // is how the JSON branch came to report a failing run with an empty line: a host is always
        // asked for JSON, so every failure on another machine read as `FAIL - ` and nothing else.
        var details = new List<string>(report.Render());
        details.AddRange(Logs(runDirectory, report, placed));

        if (execution.Unfinished.Count > 0)
        {
            details.Add($"left unfinished: {string.Join(", ", execution.Unfinished)}");
        }

        return new LegRunResult(
            commandName,
            runId,
            runDirectory,
            report,
            report.Lines,
            report.ExitCodeGiven(execution.Cancelled, execution.Unfinished),
            report.Summarize(execution.Cancelled, execution.Unfinished),
            execution.Cancelled,
            execution.Unfinished,
            details,
            Stopped: false,
            mutationRecord);
    }
}
