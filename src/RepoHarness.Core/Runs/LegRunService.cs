using System.Collections.Concurrent;
using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Runs;

/// <summary>What a leg-running command was asked to do.</summary>
/// <param name="Directory">The directory the command was invoked in.</param>
/// <param name="LegNames">The legs named with <c>--legs</c>, or null when it was left out.</param>
/// <param name="ForceLock">Whether to take a lock a run on another host holds.</param>
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
}

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
    bool Time);

/// <summary>
/// What every leg-running command shares: selecting legs, measuring the hosts, refusing what cannot
/// run, taking the locks, syncing each tree once, running the legs together, and reporting a ledger.
/// </summary>
/// <remarks>
/// One implementation for <c>build</c>, <c>test</c> and <c>run</c>, so the isolation rules cannot
/// hold for one command and not another. Each command supplies only what a leg actually does.
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
    KeepAwake keepAwake,
    DeveloperEnvironmentProvider developerEnvironments,
    IFileSystem fileSystem,
    IFilePermissions filePermissions,
    IHostPlatform platform,
    IHarnessOutput output)
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
    private readonly KeepAwake _keepAwake = keepAwake;
    private readonly DeveloperEnvironmentProvider _developerEnvironments = developerEnvironments;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IFilePermissions _filePermissions = filePermissions;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;

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
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(commandName);
        ArgumentNullException.ThrowIfNull(runId);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(work);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // Hosts are measured before anything runs, and DssHarness on each is brought to this
        // machine's build there, so a leg never starts on a host that turns out not to answer. A leg
        // goes where a sync puts its tree whatever this command starts, so a run on what is already
        // staged finds it there.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, request.Workload, request.Here, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, out var skipped);
        var factor = context.Config.Defaults.DurationWarningFactor;

        if (placed.Count == 0)
        {
            var nothing = LegRunPlan.NothingRuns(skipped);

            // Before the run has a directory: there is none to name.
            return Stopped(request, runId, nothing.ExitCode, nothing.Message, skipped, factor, nothing.Details ?? [], runDirectory: null);
        }

        var runDirectory = context.Layout.RunDirectory(runId.Value);
        var ledger = new LegLedger(_output, commandName, dispatched: request.Here is not null);

        IgnoreRunRecords(context.Layout);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        var claim = await _logOwnership.ClaimAsync(runDirectory, runId, request.ForceLock, cancellationToken).ConfigureAwait(false);

        if (!claim.Taken)
        {
            // Two runs writing one set of logs would each read the other's output as its own, which
            // is why this is its own verdict and its own exit code rather than a lock refusal - and
            // the verdict of every leg this run would have started.
            var held = $"another run owns '{runDirectory}': {claim.HeldBy}";

            return Stopped(
                request,
                runId,
                LegExit.LogHeld,
                held,
                [.. skipped, .. placed.Select(leg => leg.Entry(LegVerdict.LogHeld, held))],
                factor,
                [$"logs: {runDirectory}"],
                runDirectory);
        }

        // Trees another run holds, by tree: a verdict for the legs that need one, as a variant
        // another run holds is, and no end to the legs that do not.
        var lockedTrees = new ConcurrentDictionary<string, string>(LegPlan.TreeKeyComparer);

        // What each host's copy is marked, read once per copy, for a run on what is already staged there.
        var stagedMarks = new ConcurrentDictionary<string, Lazy<Task<CopyMark>>>(LegPlan.TreeKeyComparer);

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

            if (!request.UseStaged && placed.Any(leg => leg.Remote))
            {
                var sources = await ReadSourcesAsync(placed, commandName, cancellationToken).ConfigureAwait(false);

                syncTree = (treeKey, token) => SyncTreeAsync(context, placed, treeKey, runId, request.ForceLock, sources, lockedTrees, token);
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
                                context, placed, plan, runId, runDirectory, request, work, commandName, ledger, lockedTrees, stagedMarks, token),
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
                // already knew. The exit code is still the refusal's own.
                var reached = ledger.Build(factor);

                return Stopped(
                    request,
                    runId,
                    ex.ExitCode,
                    ex.Message,
                    ledger.Entries,
                    factor,
                    [.. reached.Render(), .. Logs(runDirectory, reached, placed)],
                    runDirectory);
            }

            return Report(commandName, context, ledger, execution, runId, runDirectory, placed, request.Json);
        }
        finally
        {
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
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => LegPlan.TreeKeyComparer.Equals(candidate.TreeKey, treeKey));

        if (!leg.Remote)
        {
            return;
        }

        // The tree this leg declares, as the run read it when it began - not whatever tree the command was typed in. A
        // leg naming a worktree measures that worktree; sending the main checkout instead would report the worktree's
        // name over the main checkout's sources. A tree that could not be read raises its failure here, in the sync of
        // each copy made from it, where that copy's legs report it.
        var source = await sources[leg.TreeRoot].ConfigureAwait(false);

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
            await _syncService
                .SyncAsync(source, _transportFactory.For(leg.Host), leg.HostTreeRoot, new SyncOptions(), cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Reads, once each, the trees the remote legs among <paramref name="placed"/> are synced from, all of them before
    /// any leg's work, so every host's copy of a tree is made from the one reading (see
    /// <see cref="ISyncService.ReadSourceAsync"/>).
    /// </summary>
    /// <returns>Each tree's reading by its root, or what stopped it.</returns>
    /// <remarks>
    /// Read side by side, and waited for whether each was read or not: a tree that could not be read - git would not
    /// answer in it, a file in it could not be opened - raises nothing here, but in the sync of each copy made from it,
    /// while the legs on other trees and on this machine still run.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, Task<SyncSource>>> ReadSourcesAsync(
        IReadOnlyList<PlacedLeg> placed,
        string commandName,
        CancellationToken cancellationToken)
    {
        var comparer = _platform.PathComparer();
        var trees = placed
            .Where(leg => leg.Remote)
            .Select(leg => leg.TreeRoot)
            .Distinct(comparer)
            .ToList();

        // Said, because a large tree takes a while to read, and nothing else is said until it is.
        _output.Info(commandName, $"reading {ReportText.Listed(trees)} for the copies on other machines");

        var sources = trees.ToDictionary(
            tree => tree,
            tree => Task.Run(() => _syncService.ReadSourceAsync(tree, cancellationToken), cancellationToken),
            comparer);

        await Task.WhenAll(sources.Values.Cast<Task>()).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return sources;
    }

    /// <summary>
    /// What a run ends with when something other than its legs ended it, with the legs that had a
    /// line by then.
    /// </summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="runId">The run, which its document names whatever ended it.</param>
    /// <param name="exitCode">What the process exits with.</param>
    /// <param name="message">The line it ends on.</param>
    /// <param name="entries">The legs' lines so far.</param>
    /// <param name="factor">The duration warning factor the ledger is built with.</param>
    /// <param name="details">What the table form says beneath the line.</param>
    /// <param name="runDirectory">
    /// Where the run keeps its records, once it has a directory; <see langword="null"/> before.
    /// </param>
    /// <remarks>
    /// Asked for data, the ledger is the whole of standard output whatever ended the run: the document,
    /// with the code and the line the process ends on. Written as text instead - a table, a list of
    /// reasons - it reached the machine that dispatched the leg as a host whose answer could not be
    /// read, where the host had said exactly what happened.
    /// </remarks>
    private CommandOutcome Stopped(
        LegRunRequest request,
        RunId runId,
        int exitCode,
        string message,
        IReadOnlyList<LegEntry> entries,
        double factor,
        IReadOnlyList<string> details,
        string? runDirectory)
        => request.Json
            ? new CommandOutcome(exitCode, message) { Data = [LedgerReport.From(entries, factor).ToJson(exitCode, message, runDirectory, _output.Shown, runId)] }
            : CommandOutcome.Failed(exitCode, message, details);

    /// <summary>
    /// Where a run's records are, as its text form says it: this run's directory, then the one each
    /// leg another host ran keeps there, since a host runs a leg under a run of its own.
    /// </summary>
    private static IEnumerable<string> Logs(string runDirectory, LedgerReport report, IReadOnlyList<PlacedLeg> placed)
    {
        yield return $"logs: {runDirectory}";

        foreach (var line in report.Lines.Where(line => line.RunDirectory is { Length: > 0 }))
        {
            var host = placed.FirstOrDefault(leg => leg.Name == line.Leg)?.Host.Host.ToString();

            yield return host is null
                ? $"logs of {line.Leg}: {line.RunDirectory}"
                : $"logs of {line.Leg} on {host}: {line.RunDirectory}";
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
        ConcurrentDictionary<string, Lazy<Task<CopyMark>>> stagedMarks,
        CancellationToken cancellationToken)
    {
        var leg = placed.First(candidate => candidate.Name == plan.Name);
        var started = Stopwatch.GetTimestamp();

        if (lockedTrees.TryGetValue(leg.TreeKey, out var treeHeld))
        {
            return Ended(leg, LegVerdict.RefusedLocked, treeHeld, started);
        }

        // A run on what each host already holds tests that copy as it is: one a sync or a takeover of began and did not
        // finish holds no tree a run began with, and its legs are inputs-moved, as a copy whose tree moved before it was
        // carried is - before a slot is taken, and nothing of them runs. Read by this machine, which would have synced it,
        // once per copy: a host is never told a run is on what is staged, since the staging is this machine's decision.
        if (request.UseStaged && leg.Remote && request.Here is null)
        {
            var mark = await stagedMarks
                .GetOrAdd(leg.TreeKey, _ => new Lazy<Task<CopyMark>>(() => _transportFactory.For(leg.Host).ReadMarkAsync(leg.HostTreeRoot, cancellationToken)))
                .Value
                .ConfigureAwait(false);

            if (SyncService.PartMade(mark) is { } partMade)
            {
                return Ended(
                    leg,
                    LegVerdict.InputsMoved,
                    $"{leg.Named}: '{leg.HostTreeRoot}' {partMade}: --use-staged has nothing current to run there. "
                    + (mark == CopyMark.AdoptionStopped
                        ? $"Finish taking it over with '{ToolPackage.Command} sync --adopt \"{leg.Named}\"', and run again."
                        : "Run without --use-staged, which syncs it first."),
                    started);
            }
        }

        // The tree shared and this variant exclusive: variants build side by side, but never while
        // their sources are being replaced.
        var attempt = await _runLock
            .TryAcquireAsync(
                context.Layout,
                leg.BuildLock(runId, ledger.CommandName) with { Force = request.ForceLock },
                cancellationToken)
            .ConfigureAwait(false);

        if (attempt.Handle is not { } handle)
        {
            // The one refusal that is a verdict rather than an end to the run: it is about this leg
            // and this moment, so the other legs still report, and one locked leg never hides them.
            // A lock file nobody can use is raised instead, as the refusal of the run it is.
            return Ended(leg, LegVerdict.RefusedLocked, attempt.HeldBy!, started);
        }

        // Every other refusal is left to propagate. A configuration a leg cannot satisfy — an
        // undeclared program in an action file, a project that names no toolchain — is the same
        // fact for every leg, and turning it into a per-leg verdict would report it as many times
        // as there are legs, under a verdict that named the wrong cause and an exit code that said
        // a lock was held.
        await using (handle)
        {
            // A heavy leg waits here, once its tree is synced and its lock taken, until its machine takes it: another run of
            // its variant is refused-locked meanwhile, as it would be while it ran. Its slot is given back when its work
            // ends, and before the lock is.
            using var admitted = await AdmitAsync(context, leg, request, runId, ledger, cancellationToken).ConfigureAwait(false);

            if (admitted is { Refusal: { } refusal })
            {
                return Ended(leg, LegVerdict.NotAdmitted, refusal, started) with { Admission = admitted.Fact };
            }

            var entry = await RunTakenLegAsync(context, leg, runId, runDirectory, request, work, commandName, ledger, started, cancellationToken).ConfigureAwait(false);

            return admitted is null ? entry : entry with { Admission = admitted.Fact };
        }
    }

    /// <summary>
    /// Asks the machine <paramref name="leg"/>'s work runs on to take it, where this process is on that machine,
    /// the leg is heavy and the machine declares admission; <see langword="null"/> where any of those is not so.
    /// </summary>
    /// <remarks>
    /// Asked by a process on the machine the work runs on, which outlives that work: this one, for a leg of this machine
    /// or of one of its WSL distributions, which run on it and are counted against its memory; the host's own, for a leg
    /// on an ssh host, which is a machine of its own. A distribution running a leg this machine dispatched to it asks
    /// nothing: this machine's process took it before dispatching it, and the distribution's own figures do not show this
    /// machine's memory.
    /// </remarks>
    private async Task<Admission?> AdmitAsync(
        HarnessContext context,
        PlacedLeg leg,
        LegRunRequest request,
        RunId runId,
        LegLedger ledger,
        CancellationToken cancellationToken)
    {
        // Heavy as it is on this leg's system: a heavy step limited by runOn, or one that builds, makes the legs of those
        // systems alone heavy.
        if (!request.Workload.On(leg.Leg.Os).Heavy
            || (request.Here is null && leg.Host.Host.Kind == HostKind.Ssh)
            || request.Here is { Kind: HostKind.Wsl })
        {
            return null;
        }

        var machine = leg.Named.Kind == HostKind.Wsl ? context.Config.Hosts.Local : leg.HostSettings;

        if (AdmissionSettings.RuleFor(machine.Admission, context.Config.Defaults.Admission) is not { } rule)
        {
            return null;
        }

        return await _admission
            .AdmitAsync(
                new AdmissionRequest(
                    rule,
                    runId.Value,
                    ledger.CommandName,
                    leg.Name,
                    leg.Host.Host.ToString(),
                    leg.HostTreeRoot,
                    leg.Variant.DirectoryName,
                    message => ledger.Transition(leg.Name, message),
                    leg.Need),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// <paramref name="leg"/>'s line, reaching <paramref name="verdict"/> for <paramref name="detail"/> before its own work
    /// ran, and how long it took since it was <paramref name="started"/>.
    /// </summary>
    private static LegEntry Ended(PlacedLeg leg, LegVerdict verdict, string detail, long started)
        => leg.Entry(verdict, detail) with { Duration = Stopwatch.GetElapsedTime(started) };

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
        if (leg.Remote && request.Here is null)
        {
            return await _remoteLegs
                .RunAsync(
                    commandName,
                    leg,
                    request.RemoteArguments ?? [],
                    cancellationToken)
                .ConfigureAwait(false);
        }

        // Held awake for as long as the leg's own work runs here, by the command this machine
        // declares - under the section the machine that dispatched the leg knows it by.
        await using var awake = _keepAwake.Hold(commandName, leg.Name, leg.HostSettings, leg.Host.ProgramDirectories, cancellationToken);

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

            if (MissingInDeveloperEnvironment(context.Config, leg, request.Workload, environmentName, setUp) is { } missing)
            {
                return Ended(leg, missing.Verdict, missing.Reason, started) with { DeveloperEnvironment = setUp.Fact };
            }

            leg = leg with { DeveloperEnvironment = setUp.Environment };
        }

        var entry = await work(new LegWork(leg, context, runId, runDirectory, request.Time), cancellationToken).ConfigureAwait(false);

        return setUp is null ? entry : entry with { DeveloperEnvironment = setUp.Fact };
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
    /// CMake component is installed.
    /// </remarks>
    private (string Reason, LegVerdict Verdict)? MissingInDeveloperEnvironment(
        HarnessConfig config,
        PlacedLeg leg,
        LegWorkload workload,
        string name,
        DeveloperEnvironmentSetup setUp)
    {
        var search = PathSearch.For(PhaseEnvironment.Layered(leg.HostSettings.Env, setUp.Environment), leg.Host.ProgramDirectories);

        return LegPlacement.MissingPrograms(
            LegPrograms.InDeveloperEnvironment(config, leg.Leg, workload, leg.HostSettings),
            program => search.Find(_platform, _filePermissions, program),
            $"the PATH developer environment '{name}' sets up");
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

    private CommandOutcome Report(
        string commandName,
        HarnessContext context,
        LegLedger ledger,
        LegExecution execution,
        RunId runId,
        string runDirectory,
        IReadOnlyList<PlacedLeg> placed,
        bool json)
    {
        var report = ledger.Build(context.Config.Defaults.DurationWarningFactor);

        // One code and one line, whichever form the ledger is shown in. Deciding them per branch
        // is how the JSON branch came to report a failing run with an empty line: a host is always
        // asked for JSON, so every failure on another machine read as `FAIL - ` and nothing else.
        var exitCode = report.ExitCodeGiven(execution.Cancelled, execution.Unfinished);
        var message = report.Summarize(execution.Cancelled, execution.Unfinished);

        if (json)
        {
            return new CommandOutcome(exitCode, message)
            {
                Data = [report.ToJson(execution.Cancelled, execution.Unfinished, runDirectory, _output.Shown, runId)],
                Quiet = true,
            };
        }

        var details = new List<string>(report.Render());
        details.AddRange(Logs(runDirectory, report, placed));

        if (execution.Unfinished.Count > 0)
        {
            details.Add($"left unfinished: {string.Join(", ", execution.Unfinished)}");
        }

        return new CommandOutcome(exitCode, message, details);
    }
}
