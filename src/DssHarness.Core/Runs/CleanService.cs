using System.Collections.Concurrent;
using System.Diagnostics;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Legs;
using DssHarness.Core.Mutations;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Core.Runs;

/// <summary>What <c>clean</c> was asked to do.</summary>
/// <param name="Directory">The directory the command was typed in.</param>
/// <param name="LegNames">The legs named with <c>--legs</c>, or null when it was left out.</param>
/// <param name="DryRun">Whether to say what each build directory holds, and remove nothing.</param>
/// <param name="Json">Whether the ledger is wanted as data rather than as a table.</param>
/// <param name="Here">
/// The host this machine is to the machine that sent the legs here, as <see cref="LegRunRequest.Here"/>
/// says; <see langword="null"/> where this machine places them itself.
/// </param>
public sealed record CleanRequest(
    string Directory,
    IReadOnlyList<string>? LegNames,
    bool DryRun = false,
    bool Json = false,
    HostId? Here = null)
{
    /// <summary>
    /// Whether the clean removes the workers an earlier version of this tool kept beside the repository's trees
    /// (<see cref="CleanService.FormerWorkersOption"/>), on each machine a selected leg runs on, and nothing else.
    /// </summary>
    public bool FormerWorkers { get; init; }
}

/// <summary>
/// Removes each selected leg's build directory wherever the leg runs - this machine, a WSL distribution's
/// copy, an ssh host's copy - with the mutation workers its sweeps keep in its tree and the copies of it its legs
/// work in, or, asked for a dry run, says what each holds and the room left beside it.
/// </summary>
/// <remarks>
/// For a machine whose disk a build filled. Nothing else removes a build directory from a host's copy: a
/// build that starts from clean fills the disk again as it goes, and deleting the worktree takes its local
/// tree too. So it writes nothing on the machine it removes from before it has removed: no sync, no lock
/// entry, no run records - a leg another run holds is kept from it by the lock file being read, under the
/// mutex a run needs to write one, and never by an entry of its own. The directory is renamed aside while
/// no run can take the lock, then removed; one an earlier removal left aside is removed by the next. A host
/// whose DssHarness is behind this machine's is still brought to this build first, as it is by every
/// command that asks it anything, and that write needs room.
/// <para>
/// A leg's mutation workers are removed the same way, and first, so the room its line says is the room once they are
/// gone: its own variant's, its self-test's, and those of any variant no leg of its host and tree builds any more,
/// which nothing else would ever remove - another leg's are that leg's. Each variant's go under the lock a sweep of
/// it takes: a sweep running keeps every worker of its variant from it, and a worker a live sweep still claims is
/// kept; one whose sweep died holding it is released first, and said. A directory under a worker's name that no sync
/// made is said and left. Where a host holds no copy of the tree, it holds nothing of it to remove: the workers kept
/// there were in the copy.
/// </para>
/// <para>
/// The copies of the tree its legs work in go the same way - the leg's own variant's, and those of any variant no leg
/// builds any more - whatever the configuration says now: a copy made under a setting since changed is still the leg's.
/// Each goes under the lock a run takes on it: one a run is building in, or replacing, is kept and said, and the clean
/// of that leg is refused-locked.
/// </para>
/// <para>
/// A dry run ends as the clean would: it prints all it measured, and a leg a run holds - its build directory, a worker
/// a sweep holds, or a copy a run holds - is refused-locked in the dry run too, exit 13.
/// </para>
/// </remarks>
public sealed class CleanService(
    IHarnessContextLoader contextLoader,
    LegsService legsService,
    RunLock runLock,
    ISyncTransportFactory transports,
    RemoteLegRunner remoteLegs,
    LegExecutor legExecutor,
    ISyncService syncService,
    LocalSyncTransport localTransport,
    IProcessIdentity identity,
    IFileSystem fileSystem,
    IHostPlatform platform,
    IHarnessOutput output,
    IRepositoryTrees trees)
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "clean";

    /// <summary>The option that measures and removes nothing, passed on to a host as it was given here.</summary>
    public const string DryRunOption = "--dry-run";

    /// <summary>
    /// The option that removes the workers an earlier version of this tool kept beside the repository's trees, and
    /// nothing else, passed on to a host as it was given here.
    /// </summary>
    public const string FormerWorkersOption = "--former-workers";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegsService _legsService = legsService;
    private readonly RunLock _runLock = runLock;
    private readonly ISyncTransportFactory _transports = transports;
    private readonly RemoteLegRunner _remoteLegs = remoteLegs;
    private readonly LegExecutor _legExecutor = legExecutor;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
    private readonly LocalSyncTransport _localTransport = localTransport;
    private readonly IRepositoryTrees _trees = trees;
    private readonly WorkerCopies _workers = new(syncService, localTransport, fileSystem, output, identity, CommandName);

    /// <summary>Removes, or measures, every selected leg's build directory, and reports the ledger.</summary>
    /// <param name="request">What the command was asked to do.</param>
    /// <param name="cancellationToken">Stops the command, here and on every host.</param>
    public async Task<CommandOutcome> RunAsync(CleanRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var context = await _contextLoader.LoadAsync(request.Directory, cancellationToken).ConfigureAwait(false);

        // What a copy of the tree needs of a host: no program starts there, so a host without the build's tools
        // still has its build directories removed.
        var report = await _legsService
            .CheckAsync(request.Directory, request.LegNames, LegWorkload.Copy, request.Here, LocalTrees.InPlace, cancellationToken)
            .ConfigureAwait(false);

        var placed = LegRunPlan.From(context, report, _platform, out var skipped);
        var factor = context.Config.Defaults.DurationWarningFactor;

        if (placed.Count == 0)
        {
            var nothing = LegRunPlan.NothingRuns(skipped);
            return Stopped(request, nothing.ExitCode, nothing.Message, skipped, factor, nothing.Details ?? []);
        }

        var ledger = new LegLedger(_output, CommandName, dispatched: request.Here is not null);

        foreach (var entry in skipped)
        {
            ledger.Record(entry);
        }

        LegExecution execution;

        // The machines whose former workers a leg of this clean has taken, each by the first leg that runs there.
        var machines = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);

        // Through the executor every leg-running command uses, so each leg ends with a line of its own: a host
        // that stops answering skips its own legs and no other's, and an interrupted clean still says what it
        // had removed, naming the legs it left.
        try
        {
            execution = await _legExecutor
                .RunAsync(
                    new LegExecutionRequest
                    {
                        Legs = [.. placed.Select(leg => leg.ToPlan())],
                        RunLeg = async (plan, token) => await CleanAsync(
                                context,
                                placed.Single(leg => string.Equals(leg.Name, plan.Name, StringComparison.Ordinal)),
                                request,
                                machines,
                                token)
                            .ConfigureAwait(false),
                    },
                    ledger,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (HarnessException ex)
        {
            // The legs already dealt with are said first, then the refusal, as a run that builds says them: a
            // directory already removed is gone whatever stopped the rest.
            return Stopped(request, ex.ExitCode, ex.Message, ledger.Entries, factor, ledger.Build(factor).Render());
        }

        var built = ledger.Build(factor);
        var exitCode = built.ExitCodeGiven(execution.Cancelled, execution.Unfinished);
        var message = built.Summarize(execution.Cancelled, execution.Unfinished);

        return request.Json
            ? new CommandOutcome(exitCode, message) { Data = [built.ToJson(execution.Cancelled, execution.Unfinished, shown: _output.Shown)], Quiet = true }
            : new CommandOutcome(exitCode, message, built.Render());
    }

    /// <summary>Removes, or measures, one leg's build directory where the leg runs.</summary>
    private async Task<LegEntry> CleanAsync(
        HarnessContext context,
        PlacedLeg leg,
        CleanRequest request,
        ConcurrentDictionary<string, string> machines,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        if (request.FormerWorkers)
        {
            return await FormerWorkersAsync(context, leg, request, machines, cancellationToken).ConfigureAwait(false) with
            {
                Duration = Stopwatch.GetElapsedTime(started),
            };
        }

        // The lock a build of this leg takes, so a build holding it is seen, and a build about to take it is kept out.
        var building = leg.BuildLock(RunId.New(), CommandName);

        if (!leg.Dispatched)
        {
            // The workers first, so the room the build directory's removal measures is the room once all are gone, and
            // the leg's copies with them: the tree's own build directory last, for what it alone holds.
            var workers = await CleanWorkersAsync(context, leg, request.DryRun, cancellationToken).ConfigureAwait(false);
            var copies = await CleanCopiesAsync(context, leg, request.DryRun, cancellationToken).ConfigureAwait(false);
            var here = CleanHere(context.Layout, leg, building, request.DryRun);

            var parts = new[] { here.Detail, workers?.Verdict.Detail, copies?.Verdict.Detail }.Where(part => part is { Length: > 0 }).ToList();

            return (workers is not null || copies is not null
                ? here with
                {
                    Verdict = Verdicts.Worst([here.Verdict, workers?.Verdict.Verdict ?? LegVerdict.Passed, copies?.Verdict.Verdict ?? LegVerdict.Passed]),
                    Detail = string.Join("; ", parts),
                    Space = (here.Space ?? WorkersAlone(leg.BuildDirectory)) with
                    {
                        WorkerBytes = workers?.Bytes,
                        CopyBytes = copies?.Bytes,
                    },
                }
                : here) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // A run started on this machine that is building the leg there holds this machine's lock too, for as
        // long as it runs: asked here first - on a dry run as well, so it ends as the clean would - the host is never
        // sent a command a run of this machine's holds off.
        // So does a sweep of the leg there, in the copy of its own it is swept in, which that host's clean removes.
        if ((_runLock.HeldBy(context.Layout, building)
                ?? _runLock.HeldBy(context.Layout, LegCopies.Whole(LegCopies.PathOf(leg.HostTreeRoot, leg.Variant), CommandName, leg.Host.Host))) is { } held)
        {
            return leg.Entry(LegVerdict.RefusedLocked, held) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // Asked before the host is sent the command: DssHarness there runs a command in the tree's copy, and
        // refuses one for a copy that is not there as a host it could not run it on.
        var transport = _transports.For(leg.Host);

        // A host holding no copy of the tree holds nothing of it to remove: the workers a sweep kept there were in the copy.
        if (!await transport.RootExistsAsync(leg.HostTreeRoot, cancellationToken).ConfigureAwait(false))
        {
            return leg.Entry(LegVerdict.Passed, $"nothing to remove: {leg.Named} holds no copy of this tree at '{leg.HostTreeRoot}'")
                with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        return await _remoteLegs
            .RunAsync(CommandName, leg, request.DryRun ? [DryRunOption] : [], cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Removes, or measures, the workers an earlier version of this tool kept beside each tree of the repository on the
    /// machine <paramref name="leg"/> runs on, once a machine, by the first leg that runs there: here, beside the main
    /// checkout, every worktree git records, and each worktree gone since that workers were left beside; on a host, asked
    /// of it from its home, beside its copy of the main checkout and each worktree's copy beside it, there still or
    /// removed since. No version kept one beside a leg's copy of a tree, so none is looked for there; where the trees
    /// cannot all be listed, those beside the trees that are known go, and the leg fails, saying why.
    /// </summary>
    private async Task<LegEntry> FormerWorkersAsync(
        HarnessContext context,
        PlacedLeg leg,
        CleanRequest request,
        ConcurrentDictionary<string, string> machines,
        CancellationToken cancellationToken)
    {
        var first = machines.GetOrAdd(leg.Named.ToString(), leg.Name);

        if (!string.Equals(first, leg.Name, StringComparison.Ordinal))
        {
            return leg.Entry(LegVerdict.Passed, $"the workers an earlier version kept on {Said(leg)} are {(request.DryRun ? "measured" : "cleaned")} with leg '{first}'");
        }

        var removal = WorkersRemoval.None;
        var unlisted = new List<string>();
        string looked;

        try
        {
            if (leg.Dispatched)
            {
                // Asked of the host from its home, where its copies are kept together: every copy of the repository there
                // may be gone - an agent's, removed by a version that removed only the workers kept in it - and the
                // workers beside them still there.
                var main = HostCopies.RepositoryPathOf(context.Config, leg.Host.Host);

                looked = $"'{main}' or a copy beside it";
                removal = await _transports
                    .For(leg.Host)
                    .RemoveFormerWorkersBesideCopiesAsync(main, request.DryRun, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // The leg's own tree first: known where the others cannot be listed. Then each tree gone since that workers
                // were left beside. Each where its workers are, once: one tree reached by two spellings - a link along the
                // way, a separator spelt otherwise - would have its workers measured twice.
                var found = await _trees.HereAsync(context, request.Here, cancellationToken).ConfigureAwait(false);
                var known = found.Trees.Where(tree => !tree.LegCopy).Select(tree => tree.Root).Prepend(leg.TreeRoot).DistinctBy(WorkersOf, _platform.PathComparer()).ToList();
                IReadOnlyList<string> gone;

                if (found.Unlisted is { } why)
                {
                    unlisted.Add($"the worktrees beside the main checkout could not be listed, so none beside them was looked for: {why}");
                }

                try
                {
                    gone = [.. FormerTreesGone(context)];
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    gone = [];
                    unlisted.Add($"the worktrees deleted since could not be looked for, so none beside them was: {ex.Message.TrimEnd('.')}");
                }

                looked = ReportText.Listed([.. known.Select(tree => $"'{tree}'")]);

                foreach (var tree in known.Concat(gone).DistinctBy(WorkersOf, _platform.PathComparer()))
                {
                    removal = removal.And(await _localTransport.RemoveFormerWorkersAsync(tree, request.DryRun, cancellationToken).ConfigureAwait(false));

                    if (removal.Interrupted)
                    {
                        break;
                    }
                }
            }
        }
        catch (HarnessException ex) when (ex.ExitCode == HarnessExit.CommandFailed)
        {
            return leg.Entry(LegVerdict.Failed, $"{ex.Message.TrimEnd('.')}{Removed(removal, request.DryRun)}") with
            {
                Space = WorkersAlone(leg.BuildDirectory) with { WorkerBytes = removal.Bytes },
            };
        }

        var said = new List<string>
        {
            removal.Removed.Count == 0
                ? $"no worker an earlier version kept beside {looked} is there"
                : Removed(removal, request.DryRun).TrimStart(';', ' '),
        };

        said.AddRange(removal.Left.Select(worker => worker.Told()));

        if (removal.Interrupted)
        {
            said.Add(LocalSyncTransport.StoppedPartWay);
        }

        said.AddRange(unlisted);

        return leg.Entry(Verdicts.Worst([removal.Verdict, unlisted.Count == 0 ? LegVerdict.Passed : LegVerdict.Failed]), string.Join("; ", said)) with
        {
            Space = WorkersAlone(leg.BuildDirectory) with { WorkerBytes = removal.Bytes },
        };

        // What went, or would go, after what is said before it; nothing where nothing does.
        static string Removed(WorkersRemoval removal, bool dryRun) => removal.Removed.Count == 0
            ? string.Empty
            : $"; {(dryRun ? string.Empty : "removed ")}{ReportText.Counted(removal.Removed.Count, "worker")} an earlier version kept beside "
                + $"its trees, {DiskSpace.Size(removal.Bytes)}: {string.Join(", ", removal.Removed.Select(worker => $"'{worker.Path}'"))}";
    }

    /// <summary>
    /// Where the workers an earlier version kept beside <paramref name="tree"/> are, with the links to the directory they
    /// are in followed: the one place two spellings of the tree name alike. The tree itself is not followed, since what
    /// was kept beside a link is beside the link. Spelt as given where it cannot be followed.
    /// </summary>
    private string WorkersOf(string tree)
    {
        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(tree));

        try
        {
            return Path.GetDirectoryName(whole) is { Length: > 0 } parent ? Path.Combine(_fileSystem.ResolveLinks(parent), Path.GetFileName(whole)) : whole;
        }
        catch (IOException)
        {
            return whole;
        }
    }

    /// <summary>
    /// The worktrees of the repository on this machine that are gone, beside which an earlier version kept workers that
    /// are still there: in the worktrees root or a directory of it, which hold the repository's worktrees alone.
    /// </summary>
    /// <exception cref="IOException">The worktrees root, or a directory of it, could not be looked in.</exception>
    /// <exception cref="UnauthorizedAccessException">The same, for want of leave.</exception>
    private IEnumerable<string> FormerTreesGone(HarnessContext context)
    {
        var root = context.Layout.WorktreesDirectoryUnder(context.Config.Worktrees.Root);

        return _fileSystem.DirectoryExists(root)
            ? [.. new[] { root }.Concat(_fileSystem.EnumerateDirectories(root)).SelectMany(directory => _localTransport.FormerWorkerTrees(directory, _ => true))]
            : [];
    }

    /// <summary>The machine a leg runs on, as a line says it.</summary>
    private static string Said(PlacedLeg leg) => leg.Named.Kind == HostKind.Local ? "this machine" : leg.Named.ToString();

    /// <summary>
    /// The space of a leg whose build directory was not measured - a link, locked, or not removable - which says its
    /// mutation workers alone: nothing of the directory at <paramref name="directory"/>, and no room.
    /// </summary>
    private static BuildSpace WorkersAlone(string directory) => new(directory, 0, Removed: false, Disk: null);

    /// <summary>Removes, or measures, the build directory of a leg that runs on this machine.</summary>
    private LegEntry CleanHere(HarnessLayout layout, PlacedLeg leg, LockRequest building, bool dryRun)
    {
        var directory = leg.BuildDirectory;
        var aside = RemovalAside.Of(directory);

        try
        {
            // Where it points is somebody's decision - a variant built on another disk - and removing the link
            // alone would free nothing and have the next build fill this disk instead.
            if (_fileSystem.IsLink(directory))
            {
                return leg.Entry(LegVerdict.Failed, $"'{directory}' is a link, so nothing was removed: what it holds is wherever it points, and yours to remove");
            }

            if (dryRun)
            {
                var holds = _fileSystem.DirectorySize(directory);
                var left = _fileSystem.DirectorySize(aside);
                var (room, unmeasured) = DiskSpace.Measure(_fileSystem, directory);

                var said = _fileSystem.DirectoryExists(directory) ? $"{DiskSpace.Size(holds)} in '{directory}'" : $"nothing at '{directory}'";

                if (left > 0)
                {
                    said += $", and {DiskSpace.Size(left)} a removal that did not finish left at '{aside}'";
                }

                said += $"; {room?.Describe() ?? Unmeasured(unmeasured)}";

                var space = new BuildSpace(directory, holds + left, Removed: false, room);

                // Asked, and nothing moved: a dry run ends as the clean would, so a leg a run is building is
                // refused-locked here too, with what the directory holds still said and still in its space.
                return _runLock.HeldBy(layout, building) is { } buildingHeld
                    ? leg.Entry(LegVerdict.RefusedLocked, $"{buildingHeld} {said}") with { Space = space }
                    : leg.Entry(LegVerdict.Passed, said) with { Space = space };
            }

            // Removed first, and outside the lock: nothing builds in a directory an earlier removal moved aside.
            var removed = Remove(aside);

            var moved = false;
            var holder = _runLock.HeldBy(layout, building, () =>
            {
                if (_fileSystem.DirectoryExists(directory))
                {
                    _fileSystem.MoveDirectory(directory, aside);
                    moved = true;
                }
            });

            if (holder is not null)
            {
                return leg.Entry(
                    LegVerdict.RefusedLocked,
                    removed > 0 ? $"{holder} What an earlier removal had left at '{aside}' was removed: {DiskSpace.Size(removed)}." : holder);
            }

            if (moved)
            {
                removed += Remove(aside);
            }

            var (after, why) = DiskSpace.Measure(_fileSystem, directory);
            var done = moved || removed > 0 ? $"removed {DiskSpace.Size(removed)} from '{directory}'" : $"nothing to remove at '{directory}'";

            return leg.Entry(LegVerdict.Passed, $"{done}; {after?.Describe() ?? Unmeasured(why)}") with
            {
                Space = new BuildSpace(directory, removed, Removed: moved || removed > 0, after),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            var remains = _fileSystem.DirectoryExists(aside)
                ? $"; what is left of it is at '{aside}', which the next clean of this leg tries again to remove"
                : string.Empty;

            return leg.Entry(
                LegVerdict.Failed,
                $"'{directory}' could not be {(dryRun ? "measured" : "removed")}: {ex.Message.TrimEnd('.')}{remains}");
        }
    }

    /// <summary>What a clean did with the mutation workers a leg keeps in its tree.</summary>
    /// <param name="Verdict">What was done, as the leg's line says it.</param>
    /// <param name="Bytes">What was removed of them - 0 where every one there was kept - or, in a dry run, what is there.</param>
    private sealed record WorkersCleaned(ReachedVerdict Verdict, long Bytes);

    /// <summary>
    /// Removes, or measures, the mutation workers a leg on this machine keeps in its tree - its own variant's, its
    /// self-test's, and those of any variant no leg of its host and tree builds any more - each variant's under the lock
    /// a sweep of it takes: what was done, or <see langword="null"/> where there is none of them.
    /// </summary>
    private async Task<WorkersCleaned?> CleanWorkersAsync(HarnessContext context, PlacedLeg leg, bool dryRun, CancellationToken cancellationToken)
    {
        var own = leg.Variant.CopyKey;
        var others = OtherLegsKeys(context.Config, leg);

        // Its own, and those nothing else would ever remove: another leg's are left for that leg's clean.
        bool Cleaned(string family) => WorkerFamily.Named(leg.HostTreeRoot, family) is { } named && !others.Contains(named.Key);

        IReadOnlyList<WorkerCopy> workers;
        IReadOnlyList<string> asides;

        try
        {
            asides = _localTransport.AsidesInSlot(leg.HostTreeRoot, HarnessLayout.MutationsDirectoryName, name => MutationWorkers.Named(name) is { } worker && Cleaned(worker.Family));
            workers = await _workers.ListInTreeAsync(leg.HostTreeRoot, Cleaned, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkersCleaned(ReachedVerdict.Of(LegVerdict.Failed, $"its mutation workers could not be listed: {ex.Message.TrimEnd('.')}"), 0);
        }

        if (workers.Count == 0 && asides.Count == 0)
        {
            return null;
        }

        // A directory under a worker's name that no sync made is somebody's: said, and never removed.
        var made = workers.Where(worker => worker.Made).ToList();
        var left = workers
            .Where(worker => !worker.Made)
            .Select(worker => $"'{worker.Path}' is named as worker {worker.Number}, and was left: {WorktreeReports.Origin(worker.Found)}")
            .ToList();

        if (dryRun)
        {
            try
            {
                // Asked, and nothing moved: each worker a live sweep claims, and each variant a sweep of it holds the
                // lock of, is said as the clean would say it, and makes the leg refused-locked - with each size still
                // measured, so what a clean would leave is still told.
                var held = new List<string>();

                foreach (var swept in made.GroupBy(worker => worker.Family.Key, StringComparer.Ordinal))
                {
                    var family = WorkerFamily.Of(leg.HostTreeRoot, swept.Key);
                    var holder = _runLock.HeldBy(context.Layout, MutationWorkers.SweepLock(leg.Host.Host, family, RunId.New(), CommandName));

                    if (holder is not null)
                    {
                        held.Add(string.Equals(swept.Key, own, StringComparison.Ordinal)
                            ? $"its mutation workers were left, as a sweep of the leg holds them: {holder}"
                            : $"the mutation workers keyed '{swept.Key}', of a variant no leg here builds, were left, as a sweep holds them: {holder}");
                    }

                    foreach (var worker in swept.Where(worker => _workers.HeldBy(worker.Path) is not null))
                    {
                        held.Add($"worker {worker.Number}, '{worker.Path}', is claimed by a sweep still running: {_workers.HeldBy(worker.Path)}");
                    }
                }

                return new WorkersCleaned(
                    ReachedVerdict.Of(
                        Verdicts.Worst([LegVerdict.Passed, held.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed]),
                        string.Join("; ", held.Append(MeasuredWorkers(workers, asides)).Where(part => part.Length > 0))),
                    workers.Sum(worker => worker.Bytes) + asides.Sum(_fileSystem.DirectorySize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new WorkersCleaned(ReachedVerdict.Of(LegVerdict.Failed, $"its mutation workers could not be measured: {ex.Message.TrimEnd('.')}"), 0);
            }
        }

        var removed = 0L;
        var asidesGone = 0;
        var gone = new List<WorkerCopy>();
        var kept = new List<string>();
        string? failure = null;

        try
        {
            // Removed first, and outside the lock: nothing sweeps in a worker an earlier removal moved aside.
            foreach (var aside in asides)
            {
                removed += Remove(aside);
                asidesGone++;
            }

            foreach (var worker in made)
            {
                _workers.ReleaseAbandoned(worker.Path);
            }

            var moved = new List<WorkerCopy>();

            // Each variant's workers, its self-test's with them, under the lock a sweep of that variant takes.
            foreach (var swept in made.GroupBy(worker => worker.Family.Key, StringComparer.Ordinal))
            {
                var family = WorkerFamily.Of(leg.HostTreeRoot, swept.Key);
                var holder = _runLock.HeldBy(context.Layout, MutationWorkers.SweepLock(leg.Host.Host, family, RunId.New(), CommandName), () =>
                {
                    foreach (var worker in swept)
                    {
                        if (_workers.HeldBy(worker.Path) is { } live)
                        {
                            kept.Add($"worker {worker.Number}, '{worker.Path}', is claimed by a sweep still running: {live}");
                        }
                        else if (_fileSystem.DirectoryExists(worker.Path))
                        {
                            // Still there: another leg of this command, cleaning the same tree, may have taken one no leg builds.
                            _fileSystem.MoveDirectory(worker.Path, RemovalAside.Of(worker.Path));
                            moved.Add(worker);
                        }
                    }
                });

                if (holder is not null)
                {
                    kept.Add(string.Equals(swept.Key, own, StringComparison.Ordinal)
                        ? $"its mutation workers were left, as a sweep of the leg holds them: {holder}"
                        : $"the mutation workers keyed '{swept.Key}', of a variant no leg here builds, were left, as a sweep holds them: {holder}");
                }
            }

            foreach (var worker in moved)
            {
                removed += Remove(RemovalAside.Of(worker.Path));
                gone.Add(worker);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message.TrimEnd('.');
        }

        // Said whichever way the removal ended: a failure naming none of what went would leave nobody knowing what the
        // clean freed, nor which workers a sweep kept.
        var said = new List<string>();

        if (failure is not null)
        {
            said.Add($"its mutation workers could not all be removed: {failure}");
        }

        if (gone.Count > 0)
        {
            said.Add(
                $"removed {ReportText.Counted(gone.Count, "mutation worker")}, {DiskSpace.Size(removed)}"
                + (asidesGone > 0 ? " with what an earlier removal of them left aside" : string.Empty)
                + $": {string.Join(", ", gone.Select(worker => $"'{worker.Path}'"))}");
        }
        else if (asidesGone > 0)
        {
            said.Add($"removed what an earlier removal of its mutation workers left aside: {DiskSpace.Size(removed)}");
        }

        if (failure is not null)
        {
            said.Add("what is left of any moved aside is removed by the next clean of this leg");
        }

        said.AddRange(kept);
        said.AddRange(left);

        return new WorkersCleaned(
            ReachedVerdict.Of(
                Verdicts.Worst([failure is null ? LegVerdict.Passed : LegVerdict.Failed, kept.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed]),
                string.Join("; ", said)),
            removed);
    }

    /// <summary>What a clean did with the copies of the tree a leg's legs work in.</summary>
    /// <param name="Verdict">What was done, as the leg's line says it.</param>
    /// <param name="Bytes">
    /// What was removed of them - 0 where every one there was kept - or, in a dry run, what is there; <see langword="null"/>
    /// where the tree keeps none.
    /// </param>
    private sealed record CopiesCleaned(ReachedVerdict Verdict, long? Bytes);

    /// <summary>
    /// Removes, or measures, the copies of its tree the legs of <paramref name="leg"/>'s host and tree work in - its own
    /// variant's, and those of any variant no leg builds any more - whatever the configuration says now: a copy made under
    /// a setting since changed is still the leg's. Each goes under the lock a run takes on it, so one a run is building in,
    /// or replacing, is kept and said.
    /// </summary>
    private async Task<CopiesCleaned?> CleanCopiesAsync(HarnessContext context, PlacedLeg leg, bool dryRun, CancellationToken cancellationToken)
    {
        var others = OtherLegsKeys(context.Config, leg);

        // Its own, and those nothing else would ever remove: another leg's are that leg's to clean.
        bool Cleaned(string name) => LegCopies.IsKey(name) && !others.Contains(name);

        IReadOnlyList<HostCopyFound> copies;
        IReadOnlyList<string> asides;

        try
        {
            asides = _localTransport.AsidesInSlot(leg.HostTreeRoot, HarnessLayout.LegsDirectoryName, Cleaned);
            copies = await _localTransport.ListInSlotAsync(leg.HostTreeRoot, HarnessLayout.LegsDirectoryName, Cleaned, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CopiesCleaned(ReachedVerdict.Of(LegVerdict.Failed, $"the copies of its tree its legs work in could not be listed: {ex.Message.TrimEnd('.')}"), 0);
        }

        if (copies.Count == 0 && asides.Count == 0)
        {
            return null;
        }

        // A directory under a copy's name that no sync made is somebody's: said, and never removed.
        var made = copies.Where(copy => copy.Origin == CopyOrigin.Made).ToList();
        var left = copies
            .Where(copy => copy.Origin != CopyOrigin.Made)
            .Select(copy => $"'{copy.Path}' is named as a copy its legs work in, and was left: {WorktreeReports.Origin(copy)}")
            .ToList();

        if (dryRun)
        {
            try
            {
                var held = made
                    .Select(copy => (Copy: copy, Holder: LegCopies.HeldBy(_runLock, context.Layout, copy.Path, CommandName)))
                    .Where(held => held.Holder is not null)
                    .ToList();

                return new CopiesCleaned(
                    ReachedVerdict.Of(
                        Verdicts.Worst([LegVerdict.Passed, held.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed]),
                        string.Join("; ", held
                            .Select(held => $"its copy '{held.Copy.Path}' was left, as a run holds it: {held.Holder}")
                            .Append(MeasuredCopies(made, asides, copies.Where(copy => copy.Origin != CopyOrigin.Made).ToList()))
                            .Where(part => part.Length > 0))),
                    copies.Sum(copy => copy.Bytes) + asides.Sum(_fileSystem.DirectorySize));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CopiesCleaned(ReachedVerdict.Of(LegVerdict.Failed, $"the copies of its tree its legs work in could not be measured: {ex.Message.TrimEnd('.')}"), 0);
            }
        }

        var removed = 0L;
        var asidesGone = 0;
        var gone = new List<HostCopyFound>();
        var kept = new List<string>();
        string? failure = null;

        try
        {
            // Removed first, and outside the lock: nothing works in a copy an earlier removal moved aside.
            foreach (var aside in asides)
            {
                removed += Remove(aside);
                asidesGone++;
            }

            var moved = new List<HostCopyFound>();

            // Each copy under the lock a run takes on it: one a run holds is kept, saying who.
            foreach (var copy in made)
            {
                var holder = LegCopies.HeldBy(_runLock, context.Layout, copy.Path, CommandName, () =>
                {
                    if (_fileSystem.DirectoryExists(copy.Path))
                    {
                        // Still there: another leg of this command, cleaning the same tree, may have taken one no leg builds.
                        _fileSystem.MoveDirectory(copy.Path, RemovalAside.Of(copy.Path));
                        moved.Add(copy);
                    }
                });

                if (holder is not null)
                {
                    kept.Add($"its copy '{copy.Path}' was left, as a run holds it: {holder}");
                }
            }

            foreach (var copy in moved)
            {
                removed += Remove(RemovalAside.Of(copy.Path));
                gone.Add(copy);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = ex.Message.TrimEnd('.');
        }

        var said = new List<string>();

        if (failure is not null)
        {
            said.Add($"the copies of its tree its legs work in could not all be removed: {failure}");
        }

        if (gone.Count > 0)
        {
            said.Add(
                $"removed {ReportText.Counted(gone.Count, "leg copy")}, {DiskSpace.Size(removed)}"
                + (asidesGone > 0 ? " with what an earlier removal of them left aside" : string.Empty)
                + $": {string.Join(", ", gone.Select(copy => $"'{copy.Path}'"))}");
        }
        else if (asidesGone > 0)
        {
            said.Add($"removed what an earlier removal of the copies of its tree left aside: {DiskSpace.Size(removed)}");
        }

        if (failure is not null)
        {
            said.Add("what is left of any moved aside is removed by the next clean of this leg");
        }

        said.AddRange(kept);
        said.AddRange(left);

        return new CopiesCleaned(
            ReachedVerdict.Of(
                Verdicts.Worst([failure is null ? LegVerdict.Passed : LegVerdict.Failed, kept.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed]),
                string.Join("; ", said)),
            removed);
    }

    /// <summary>What a dry run says of the leg's copies: what each holds, and what an earlier removal left aside.</summary>
    private string MeasuredCopies(IReadOnlyList<HostCopyFound> made, IReadOnlyList<string> asides, IReadOnlyList<HostCopyFound> somebodys)
    {
        var said = made
            .Select(copy => $"{DiskSpace.Size(copy.Bytes)} in '{copy.Path}'")
            .Concat(somebodys.Select(copy => $"{DiskSpace.Size(copy.Bytes)} in '{copy.Path}', which a clean leaves: {WorktreeReports.Origin(copy)}"))
            .ToList();

        if (asides.Count > 0)
        {
            said.Add($"{DiskSpace.Size(asides.Sum(_fileSystem.DirectorySize))} a removal that did not finish left aside");
        }

        return made.Count + somebodys.Count > 0
            ? $"{ReportText.Counted(made.Count + somebodys.Count, "leg copy")}: {string.Join(", ", said)}"
            : $"no copy its legs work in, and {said.Single()}";
    }

    /// <summary>
    /// Removes <paramref name="directory"/>, and says what it held: counted only once it is gone, so one that could not
    /// be removed is never among what a clean says it freed - it is still there, for the next clean to count.
    /// </summary>
    private long Remove(string directory)
    {
        var held = _fileSystem.DirectorySize(directory);

        _fileSystem.DeleteDirectory(directory);

        return held;
    }


    /// <summary>
    /// The variants the other legs configured for <paramref name="leg"/>'s host and tree build, by the keys their
    /// workers are named by: whose mutation workers in that tree are theirs to clean, and never this leg's.
    /// </summary>
    private static IReadOnlySet<string> OtherLegsKeys(HarnessConfig config, PlacedLeg leg)
        => config.Legs.Values
            .Where(other => string.Equals(other.Wsl, leg.Leg.Wsl, StringComparison.Ordinal)
                && string.Equals(other.Ssh, leg.Leg.Ssh, StringComparison.Ordinal)
                && string.Equals(other.Worktree, leg.Leg.Worktree, StringComparison.Ordinal))
            .Select(other => VariantKey.For(config, other, other.Os).CopyKey)
            .Where(key => !string.Equals(key, leg.Variant.CopyKey, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>What a dry run says of the leg's workers: what each holds, and what an earlier removal left aside.</summary>
    private string MeasuredWorkers(IReadOnlyList<WorkerCopy> workers, IReadOnlyList<string> asides)
    {
        var said = workers
            .Select(worker => worker.Made
                ? $"{DiskSpace.Size(worker.Bytes)} in '{worker.Path}'"
                : $"{DiskSpace.Size(worker.Bytes)} in '{worker.Path}', which a clean leaves: {WorktreeReports.Origin(worker.Found)}")
            .ToList();

        if (asides.Count > 0)
        {
            said.Add($"{DiskSpace.Size(asides.Sum(_fileSystem.DirectorySize))} a removal that did not finish left aside");
        }

        return workers.Count > 0
            ? $"{ReportText.Counted(workers.Count, "mutation worker")}: {string.Join(", ", said)}"
            : $"no mutation worker, and {said.Single()}";
    }

    private static string Unmeasured(string? why) => $"the room on its filesystem could not be measured: {why}";

    /// <summary>
    /// What the command ends with when something other than its legs ended it, with the legs that had a line
    /// by then; asked for data, the ledger is still the whole of standard output.
    /// </summary>
    private CommandOutcome Stopped(
        CleanRequest request,
        int exitCode,
        string message,
        IReadOnlyList<LegEntry> entries,
        double factor,
        IReadOnlyList<string> details)
        => request.Json
            ? new CommandOutcome(exitCode, message) { Data = [LedgerReport.From(entries, factor).ToJson(exitCode, message, shown: _output.Shown)] }
            : CommandOutcome.Failed(exitCode, message, details);
}
