using System.Diagnostics;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Mutations;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Runs;

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
    HostId? Here = null);

/// <summary>
/// Removes each selected leg's build directory wherever the leg runs - this machine, a WSL distribution's
/// copy, an ssh host's copy - with the mutation workers its sweeps keep beside its tree, or, asked for a dry
/// run, says what each holds and the room left beside it.
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
/// made is said and left. Where a host holds no copy of the tree, nothing there can run a clean, and the host is
/// asked to remove the workers left beside where the copy was.
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
    IHarnessOutput output)
{
    /// <summary>The command, as it is typed and as it reports.</summary>
    public const string CommandName = "clean";

    /// <summary>The option that measures and removes nothing, passed on to a host as it was given here.</summary>
    public const string DryRunOption = "--dry-run";

    private readonly IHarnessContextLoader _contextLoader = contextLoader;
    private readonly LegsService _legsService = legsService;
    private readonly RunLock _runLock = runLock;
    private readonly ISyncTransportFactory _transports = transports;
    private readonly RemoteLegRunner _remoteLegs = remoteLegs;
    private readonly LegExecutor _legExecutor = legExecutor;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;
    private readonly IHarnessOutput _output = output;
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
            .CheckAsync(request.Directory, request.LegNames, LegWorkload.Copy, request.Here, cancellationToken)
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
    private async Task<LegEntry> CleanAsync(HarnessContext context, PlacedLeg leg, CleanRequest request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();

        // The lock a build of this leg takes, so a build holding it is seen, and a build about to take it is kept out.
        var building = leg.BuildLock(RunId.New(), CommandName);

        if (!leg.Remote)
        {
            // The workers first, so the room the build directory's removal measures is the room once both are gone.
            var workers = await CleanWorkersAsync(context, leg, request.DryRun, cancellationToken).ConfigureAwait(false);
            var here = CleanHere(context.Layout, leg, building, request.DryRun);

            return (workers is { } swept
                ? here with
                {
                    Verdict = Verdicts.Worst([here.Verdict, swept.Verdict.Verdict]),
                    Detail = $"{here.Detail}; {swept.Verdict.Detail}",
                    Space = here.Space is { } space ? space with { WorkerBytes = swept.Bytes } : null,
                }
                : here) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // A run started on this machine that is building the leg there holds this machine's lock too, for as
        // long as it runs: asked here first, the host is never sent a command a run of this machine's holds off.
        if (!request.DryRun && _runLock.HeldBy(context.Layout, building) is { } held)
        {
            return leg.Entry(LegVerdict.RefusedLocked, held) with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        // Asked before the host is sent the command: DssHarness there runs a command in the tree's copy, and
        // refuses one for a copy that is not there as a host it could not run it on.
        var transport = _transports.For(leg.Host);

        if (!await transport.RootExistsAsync(leg.HostTreeRoot, cancellationToken).ConfigureAwait(false))
        {
            return await CleanBesideNoCopyAsync(transport, leg, request.DryRun, cancellationToken).ConfigureAwait(false)
                with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        return await _remoteLegs
            .RunAsync(CommandName, leg, request.DryRun ? [DryRunOption] : [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// What a leg on a host that holds no copy of its tree has removed, or measured: the mutation workers a sweep left
    /// beside where the copy was, which the host is asked for, since nothing there can run a clean.
    /// </summary>
    private static async Task<LegEntry> CleanBesideNoCopyAsync(ISyncTransport transport, PlacedLeg leg, bool dryRun, CancellationToken cancellationToken)
    {
        var workers = await transport.RemoveWorkersAsync(leg.HostTreeRoot, dryRun, cancellationToken).ConfigureAwait(false);
        var said = new List<string> { $"nothing to remove: {leg.Named} holds no copy of this tree at '{leg.HostTreeRoot}'" };

        if (workers.Removed.Count > 0)
        {
            said.Add(dryRun
                ? $"{workers.Removed.Count} mutation worker(s) left beside where it was: {string.Join(", ", workers.Removed.Select(worker => $"{DiskSpace.Size(worker.Bytes)} in '{worker.Path}'"))}"
                : $"removed {workers.Removed.Count} mutation worker(s) left beside where it was, {DiskSpace.Size(workers.Bytes)}: {string.Join(", ", workers.Removed.Select(worker => $"'{worker.Path}'"))}");
        }

        said.AddRange(workers.Left.Select(worker => $"'{worker.Path}' was left: {worker.Why.TrimEnd('.')}"));

        var entry = leg.Entry(workers.InUse.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed, string.Join("; ", said));

        // Only where a worker was there: a leg with nothing on its host measured nothing.
        return workers.Removed.Count == 0
            ? entry
            : entry with { Space = new BuildSpace(leg.Variant.DirectoryOn(leg.Host.Host, leg.HostTreeRoot), 0, Removed: false, Disk: null) { WorkerBytes = workers.Bytes } };
    }

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

                return leg.Entry(LegVerdict.Passed, $"{said}; {room?.Describe() ?? Unmeasured(unmeasured)}") with
                {
                    Space = new BuildSpace(directory, holds + left, Removed: false, room),
                };
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

    /// <summary>What a clean did with the mutation workers a leg keeps beside its tree.</summary>
    /// <param name="Verdict">What was done, as the leg's line says it.</param>
    /// <param name="Bytes">What was removed of them - 0 where every one there was kept - or, in a dry run, what is there.</param>
    private sealed record WorkersCleaned(ReachedVerdict Verdict, long Bytes);

    /// <summary>
    /// Removes, or measures, the mutation workers a leg on this machine keeps beside its tree - its own variant's, its
    /// self-test's, and those of any variant no leg of its host and tree builds any more - each variant's under the lock
    /// a sweep of it takes: what was done, or <see langword="null"/> where there is none of them.
    /// </summary>
    private async Task<WorkersCleaned?> CleanWorkersAsync(HarnessContext context, PlacedLeg leg, bool dryRun, CancellationToken cancellationToken)
    {
        var own = MutationWorkers.KeyOf(leg.Variant);
        var others = OtherLegsKeys(context.Config, leg);

        // Its own, and those nothing else would ever remove: another leg's are left for that leg's clean.
        bool Cleaned(string family) => WorkerFamily.Named(leg.HostTreeRoot, family) is { } named && !others.Contains(named.Key);

        IReadOnlyList<WorkerCopy> workers;
        IReadOnlyList<string> asides;

        try
        {
            asides = WorkersAside(leg, Cleaned);
            workers = await _workers.ListBesideAsync(leg.HostTreeRoot, Cleaned, cancellationToken).ConfigureAwait(false);
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

        var removed = 0L;

        try
        {
            if (dryRun)
            {
                return new WorkersCleaned(
                    ReachedVerdict.Of(LegVerdict.Passed, MeasuredWorkers(workers, asides)),
                    workers.Sum(worker => worker.Bytes) + asides.Sum(_fileSystem.DirectorySize));
            }

            // Removed first, and outside the lock: nothing sweeps in a worker an earlier removal moved aside.
            foreach (var aside in asides)
            {
                removed += Remove(aside);
            }

            foreach (var worker in made)
            {
                _workers.ReleaseAbandoned(worker.Path);
            }

            var moved = new List<WorkerCopy>();
            var kept = new List<string>();

            // Each variant's workers, its self-test's with them, under the lock a sweep of that variant takes.
            foreach (var swept in made.GroupBy(worker => WorkerFamily.Named(leg.HostTreeRoot, worker.Family)!.Key, StringComparer.Ordinal))
            {
                var family = new WorkerFamily(leg.HostTreeRoot, swept.Key);
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
            }

            var said = new List<string>();

            if (moved.Count > 0)
            {
                said.Add(
                    $"removed {moved.Count} mutation worker(s), {DiskSpace.Size(removed)}"
                    + (asides.Count > 0 ? " with what an earlier removal of them left aside" : string.Empty)
                    + $": {string.Join(", ", moved.Select(worker => $"'{worker.Path}'"))}");
            }
            else if (asides.Count > 0)
            {
                said.Add($"removed what an earlier removal of its mutation workers left aside: {DiskSpace.Size(removed)}");
            }

            said.AddRange(kept);
            said.AddRange(left);

            return new WorkersCleaned(ReachedVerdict.Of(kept.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed, string.Join("; ", said)), removed);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new WorkersCleaned(
                ReachedVerdict.Of(
                    LegVerdict.Failed,
                    $"its mutation workers could not be {(dryRun ? "measured" : "removed")}: {ex.Message.TrimEnd('.')}"
                    + (dryRun ? string.Empty : "; what is left of any moved aside is removed by the next clean of this leg")),
                removed);
        }
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
    /// workers are named by: whose mutation workers beside that tree are theirs to clean, and never this leg's.
    /// </summary>
    private static IReadOnlySet<string> OtherLegsKeys(HarnessConfig config, PlacedLeg leg)
        => config.Legs.Values
            .Where(other => string.Equals(other.Wsl, leg.Leg.Wsl, StringComparison.Ordinal)
                && string.Equals(other.Ssh, leg.Leg.Ssh, StringComparison.Ordinal)
                && string.Equals(other.Worktree, leg.Leg.Worktree, StringComparison.Ordinal))
            .Select(other => MutationWorkers.KeyOf(VariantKey.For(config, other, other.Os)))
            .Where(key => !string.Equals(key, MutationWorkers.KeyOf(leg.Variant), StringComparison.Ordinal))
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
            ? $"{workers.Count} mutation worker(s): {string.Join(", ", said)}"
            : $"no mutation worker, and {said.Single()}";
    }

    /// <summary>
    /// What an earlier removal of the workers beside the leg's tree left aside, of each family <paramref name="cleaned"/>
    /// takes: each named as the aside (<see cref="RemovalAside"/>) of a worker of one.
    /// </summary>
    private IReadOnlyList<string> WorkersAside(PlacedLeg leg, Func<string, bool> cleaned)
    {
        var tree = Path.TrimEndingDirectorySeparator(Path.GetFullPath(leg.HostTreeRoot));
        var parent = Path.GetDirectoryName(tree);

        if (parent is null || !_fileSystem.DirectoryExists(parent))
        {
            return [];
        }

        var prefix = Path.GetFileName(tree) + HostCopies.MutationSuffix;

        // A worker's name alone between the two, as a worker's name spells it: another leg's, whose name starts alike and
        // goes on, is never this leg's to remove.
        return
        [
            .. _fileSystem.EnumerateDirectories(parent)
                .Where(aside => RemovalAside.Was(Path.GetFileName(Path.TrimEndingDirectorySeparator(aside))) is { } was
                    && was.StartsWith(prefix, StringComparison.Ordinal)
                    && MutationWorkers.Named(was[prefix.Length..]) is { } worker
                    && cleaned(worker.Family))
                .Order(StringComparer.Ordinal),
        ];
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
