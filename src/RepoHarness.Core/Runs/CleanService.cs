using System.Diagnostics;
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
/// A leg's mutation workers are removed the same way, under the lock a sweep of the leg takes: a sweep running
/// keeps every worker of the leg from it, and a worker a live sweep still claims is kept; one whose sweep died
/// holding it is released first, and said. A directory under a worker's name that no sync made is said and left.
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

    /// <summary>
    /// What a build directory is renamed to while it is removed, beside it and after a dot: hidden, and never
    /// the name of another variant's, whose names start with their processor.
    /// </summary>
    private const string AsideSuffix = ".removing";

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
            var here = CleanHere(context.Layout, leg, building, request.DryRun);
            var workers = await CleanWorkersAsync(context.Layout, leg, request.DryRun, cancellationToken).ConfigureAwait(false);

            return (workers is { } swept
                ? here with { Verdict = Verdicts.Worst([here.Verdict, swept.Verdict]), Detail = $"{here.Detail}; {swept.Detail}" }
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
        if (!await _transports.For(leg.Host).RootExistsAsync(leg.HostTreeRoot, cancellationToken).ConfigureAwait(false))
        {
            return leg.Entry(LegVerdict.Passed, $"nothing to remove: {leg.Named} holds no copy of this tree at '{leg.HostTreeRoot}'")
                with { Duration = Stopwatch.GetElapsedTime(started) };
        }

        return await _remoteLegs
            .RunAsync(CommandName, leg, request.DryRun ? [DryRunOption] : [], cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Removes, or measures, the build directory of a leg that runs on this machine.</summary>
    private LegEntry CleanHere(HarnessLayout layout, PlacedLeg leg, LockRequest building, bool dryRun)
    {
        var directory = leg.BuildDirectory;
        var aside = Path.Combine(Path.GetDirectoryName(directory)!, "." + Path.GetFileName(directory) + AsideSuffix);

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
            var removed = _fileSystem.DirectorySize(aside);
            _fileSystem.DeleteDirectory(aside);

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
                removed += _fileSystem.DirectorySize(aside);
                _fileSystem.DeleteDirectory(aside);
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

    /// <summary>
    /// Removes, or measures, the mutation workers a leg on this machine keeps beside its tree, under the lock a sweep of the
    /// leg takes: what was done, or <see langword="null"/> where the leg keeps none.
    /// </summary>
    private async Task<ReachedVerdict?> CleanWorkersAsync(HarnessLayout layout, PlacedLeg leg, bool dryRun, CancellationToken cancellationToken)
    {
        IReadOnlyList<WorkerCopy> workers;
        IReadOnlyList<string> asides;

        try
        {
            asides = WorkersAside(leg);
            workers = await _workers.ListAsync(leg.HostTreeRoot, leg.Variant, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReachedVerdict.Of(LegVerdict.Failed, $"its mutation workers could not be listed: {ex.Message.TrimEnd('.')}");
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

        try
        {
            if (dryRun)
            {
                return ReachedVerdict.Of(LegVerdict.Passed, MeasuredWorkers(workers, asides));
            }

            // Removed first, and outside the lock: nothing sweeps in a worker an earlier removal moved aside.
            var removed = 0L;

            foreach (var aside in asides)
            {
                removed += _fileSystem.DirectorySize(aside);
                _fileSystem.DeleteDirectory(aside);
            }

            foreach (var worker in made)
            {
                _workers.ReleaseAbandoned(worker.Path);
            }

            var moved = new List<WorkerCopy>();
            var claimed = new List<string>();
            var holder = _runLock.HeldBy(layout, MutationWorkers.SweepLock(leg, RunId.New(), CommandName), () =>
            {
                foreach (var worker in made)
                {
                    if (_workers.HeldBy(worker.Path) is { } live)
                    {
                        claimed.Add($"worker {worker.Number}, '{worker.Path}', is claimed by a sweep still running: {live}");
                        continue;
                    }

                    _fileSystem.MoveDirectory(worker.Path, Aside(worker.Path));
                    moved.Add(worker);
                }
            });

            if (holder is not null)
            {
                return ReachedVerdict.Of(
                    LegVerdict.RefusedLocked,
                    $"its mutation workers were left, as a sweep of the leg holds them: {holder}"
                    + (removed > 0 ? $" What an earlier removal of them had left aside was removed: {DiskSpace.Size(removed)}." : string.Empty));
            }

            foreach (var worker in moved)
            {
                removed += _fileSystem.DirectorySize(Aside(worker.Path));
                _fileSystem.DeleteDirectory(Aside(worker.Path));
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

            said.AddRange(claimed);
            said.AddRange(left);

            return ReachedVerdict.Of(claimed.Count > 0 ? LegVerdict.RefusedLocked : LegVerdict.Passed, string.Join("; ", said));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ReachedVerdict.Of(
                LegVerdict.Failed,
                $"its mutation workers could not be {(dryRun ? "measured" : "removed")}: {ex.Message.TrimEnd('.')}"
                + (dryRun ? string.Empty : "; what is left of any moved aside is removed by the next clean of this leg"));
        }
    }

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
    /// What an earlier removal of the leg's workers left aside: each worker of the leg's variant, by its name with a dot
    /// before it and <see cref="AsideSuffix"/> after, as <see cref="Aside"/> renamed it.
    /// </summary>
    private IReadOnlyList<string> WorkersAside(PlacedLeg leg)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(MutationWorkers.RootOf(leg.HostTreeRoot, leg.Variant)));
        var parent = Path.GetDirectoryName(root);

        if (parent is null || !_fileSystem.DirectoryExists(parent))
        {
            return [];
        }

        var prefix = "." + Path.GetFileName(root) + "-";

        // A worker's number alone between the two, as a worker's name spells it: another variant's, whose name starts
        // alike and goes on, is never this leg's to remove.
        return
        [
            .. _fileSystem.EnumerateDirectories(parent).Where(aside =>
                Path.GetFileName(Path.TrimEndingDirectorySeparator(aside)) is var name
                && name.Length > prefix.Length + AsideSuffix.Length
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(AsideSuffix, StringComparison.Ordinal)
                && MutationWorkers.NumberOf(leg.Variant.DirectoryName + "-" + name[prefix.Length..^AsideSuffix.Length], leg.Variant) is not null),
        ];
    }

    /// <summary>Where <paramref name="directory"/> is renamed to while it is removed: beside it, hidden, and named as no copy or build is.</summary>
    private static string Aside(string directory)
    {
        var whole = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));

        return Path.Combine(Path.GetDirectoryName(whole)!, "." + Path.GetFileName(whole) + AsideSuffix);
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
