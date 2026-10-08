using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Sync;

namespace RepoHarness.Core.Worktrees;

/// <summary>What removing a worktree's copies from its hosts did, host by host.</summary>
/// <param name="Lines">One line per copy asked about: removed, not there, left in place, or not yet dealt with and why.</param>
/// <param name="Unfinished">
/// For each copy not yet dealt with, and so still recorded, the exit code its host, its lock or the record left it
/// with: <see cref="HarnessExit.Refused"/> where a run holds it, or where it or the record was refused;
/// <see cref="HarnessExit.HostUnavailable"/> where its host could not be asked; the code the host answered with
/// where removing it failed there; and, where a mutation worker kept beside it keeps it, the code of what became of
/// them (<see cref="WorkersRemoval.Verdict"/>).
/// </param>
/// <param name="Interrupted">
/// Whether an interruption stopped the asking before every recorded copy had been asked about. Those not asked
/// about stay recorded, and are not in <paramref name="Unfinished"/>.
/// </param>
/// <param name="LeftFor">
/// Worktrees elsewhere, kept under the same name and still there, whose copies were left for them.
/// </param>
public sealed record HostCopyRemoval(
    IReadOnlyList<string> Lines,
    IReadOnlyList<int> Unfinished,
    bool Interrupted,
    IReadOnlyList<string> LeftFor)
{
    /// <summary>Nothing recorded, so nothing asked.</summary>
    public static HostCopyRemoval None { get; } = new([], [], Interrupted: false, []);

    /// <summary>
    /// What the removal ends with: <see cref="HarnessExit.Cancelled"/> when interrupted,
    /// <see cref="HarnessExit.Success"/> when every copy was dealt with, and otherwise the highest code a copy was
    /// left with, so that one answer stands for all of them; each copy's own line says what kept it.
    /// </summary>
    public int ExitCode => Interrupted ? HarnessExit.Cancelled : Unfinished.Count == 0 ? HarnessExit.Success : Unfinished.Max();
}

/// <summary>Removes a worktree's copies from the hosts that hold one, as its deletion's last part.</summary>
public interface IHostCopyRemover
{
    /// <summary>
    /// Asks each host holding a copy of the worktree at <paramref name="tree"/> to remove it - and of any worktree
    /// kept under the same name that is gone - never one of a worktree of that name that is still there.
    /// </summary>
    /// <param name="context">The repository, from the command that deletes the worktree.</param>
    /// <param name="worktree">The name the worktree's copies are kept under.</param>
    /// <param name="tree">The worktree's own directory on this machine.</param>
    /// <param name="treeConfig">
    /// The configuration the worktree ran on, read before it was deleted, or <see langword="null"/> where it was gone
    /// already or could not be read. A host is reached through it first: the worktree's branch may declare a host the
    /// configuration the command runs in does not.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops the asking, which then answers with what it had done: a copy not yet asked about stays recorded.
    /// </param>
    /// <exception cref="HarnessException">The record of which hosts hold a copy cannot be read: refused.</exception>
    Task<HostCopyRemoval> RemoveAsync(
        HarnessContext context,
        string worktree,
        string tree,
        HarnessConfig? treeConfig,
        CancellationToken cancellationToken = default);
}

/// <inheritdoc cref="IHostCopyRemover"/>
/// <remarks>
/// A copy is removed only where the harness made it: one it took over was somebody's directory first, and one
/// that holds no mark of the harness's is nothing the harness knows it may remove. Each is removed under the lock a
/// leg this machine runs there holds while it builds, so a copy one of its runs is using is never removed from
/// under it, and forgotten before that lock is let go. A copy not dealt with - its host cannot be asked, a run
/// holds it, or removing it failed - stays recorded, so deleting the worktree again asks once more.
/// <para>
/// The mutation workers a host keeps beside the copy go first, since nothing would remove them once it is gone: each a
/// copy of it a sweep there made. One still in use - a sweep still running there holds it - says the copy is, which
/// then stays, and is still recorded.
/// </para>
/// </remarks>
/// <param name="inspector">Reaches a host, and makes sure the harness there can be asked.</param>
/// <param name="transports">Asks the harness on a host.</param>
/// <param name="runLock">The lock a leg holds on the copy it works in.</param>
/// <param name="fileSystem">Reads and changes the record.</param>
/// <param name="platform">How this machine compares paths.</param>
public sealed class HostCopyRemover(
    IHostInspector inspector,
    ISyncTransportFactory transports,
    RunLock runLock,
    IFileSystem fileSystem,
    IHostPlatform platform) : IHostCopyRemover
{
    private static readonly IReadOnlyDictionary<string, EmulatorConfig> NoEmulators =
        new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase);

    private static readonly IReadOnlyDictionary<string, DeveloperEnvironmentConfig> NoEnvironments =
        new Dictionary<string, DeveloperEnvironmentConfig>(StringComparer.OrdinalIgnoreCase);

    private readonly IHostInspector _inspector = inspector;
    private readonly ISyncTransportFactory _transports = transports;
    private readonly RunLock _runLock = runLock;
    private readonly HostCopyRecord _record = new(fileSystem, platform.PathComparison);

    public async Task<HostCopyRemoval> RemoveAsync(
        HarnessContext context,
        string worktree,
        string tree,
        HarnessConfig? treeConfig,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(worktree);
        ArgumentException.ThrowIfNullOrWhiteSpace(tree);

        var layout = context.Layout;
        var runId = RunId.New();
        var lines = new List<string>();
        var unfinished = new List<int>();
        var leftFor = new List<string>();

        // Whether the removal of a copy's workers was stopped part way: by this command's interruption, it is that
        // interruption's, though the copy it stopped in was the last one recorded.
        var cut = false;

        try
        {
            foreach (var entry in _record.Of(layout, worktree))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Another worktree's, kept under the same name somewhere else, and still there: not this one's to
                // remove. One that is gone is, as the last thing that knows its copies are there.
                if (!_record.SameTree(entry.Tree, tree) && _record.Exists(entry.Tree))
                {
                    leftFor.Add(entry.Tree);
                    continue;
                }

                try
                {
                    var (said, code) = await DealWithAsync(entry).ConfigureAwait(false);
                    lines.Add(said);

                    if (code is { } kept)
                    {
                        unfinished.Add(kept);
                    }
                }
                catch (HarnessException ex)
                {
                    // The record, or the lock, could not be read or written for this copy. Said as that copy not
                    // dealt with, rather than thrown, so the copies already removed are still said to be.
                    lines.Add($"{entry.Host}: its copy at '{entry.Path}' is not yet dealt with: {ex.Message.TrimEnd('.')}");
                    unfinished.Add(ex.ExitCode);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Answered rather than thrown, so what was done before the interruption is still said: a copy already
            // removed is no longer recorded, and nothing else would ever say it went.
            return new HostCopyRemoval(lines, unfinished, Interrupted: true, [.. leftFor.Distinct()]);
        }

        return new HostCopyRemoval(lines, unfinished, Interrupted: cut && cancellationToken.IsCancellationRequested, [.. leftFor.Distinct()]);

        // What was done about one entry, and the code it is left with where it is not yet dealt with.
        async Task<(string Said, int? Unfinished)> DealWithAsync(HostCopyEntry entry)
        {
            if (!HostId.TryParse(entry.Host, out var host) || host.Kind == HostKind.Local)
            {
                _record.Forget(layout, entry);
                return ($"{entry.Host}: no host this build keeps a copy on, so nothing was asked to remove '{entry.Path}', and it is no longer recorded", null);
            }

            // A record is read, not trusted: it never has a directory removed that is not where a worktree's copy
            // is kept. The main checkout's copy, above all, is the host's repositoryPath itself, and a record
            // naming it - written by hand, or wrongly - would otherwise have deleting a worktree take it.
            if (!IsWorktreeCopy(entry))
            {
                _record.Forget(layout, entry);
                return ($"{host}: '{entry.Path}' is not where a worktree's copy is kept, so nothing was asked to remove it, and it is no longer recorded", null);
            }

            // Reached through the first configuration that declares the host: the worktree's own, read before it
            // went - its branch may declare a host the configuration this command runs in does not - and then that
            // one. A host neither declares cannot be reached, now or later: asked about, its copy would stay
            // recorded for good, and every deletion of this name fail over it.
            if (new[] { treeConfig, context.Config }.FirstOrDefault(config => config is not null && HostCopies.DeclaredFor(config, host) is not null)
                is not { } declaring)
            {
                _record.Forget(layout, entry);
                return ($"{host}: no configuration here declares it any more, so nothing was asked to remove its copy at '{entry.Path}', which stays there for you to remove, and is no longer recorded", null);
            }

            var through = ReferenceEquals(declaring, context.Config) ? context : new HarnessContext(layout, declaring);

            var attempt = await _runLock
                .TryAcquireAsync(
                    layout,
                    new LockRequest
                    {
                        Host = host.ToString(),
                        Tree = entry.Path,
                        Scope = LockScope.TreeExclusive,
                        RunId = runId,
                        Command = WorktreeService.DeleteCommand,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            if (attempt.Handle is not { } handle)
            {
                return Stays(host, entry, attempt.HeldBy, HarnessExit.Refused);
            }

            await using (handle)
            {
                // Read again under the lock. A worktree of the same name elsewhere can have claimed this copy since
                // the record was read - as it may, once the tree it was recorded for is gone - and synced into it:
                // what it holds now is not this deletion's to remove.
                switch (_record.Holding(layout, entry))
                {
                    case null:
                        return ($"{host}: '{entry.Path}' is no longer recorded, so nothing was asked to remove it", null);

                    case { } now when !_record.SameTree(now.Tree, entry.Tree):
                        return ($"{host}: left '{entry.Path}' in place: the worktree at '{now.Tree}' has claimed it since", null);
                }

                CopyRemoval removal;
                var workers = WorkersRemoval.None;

                try
                {
                    var report = await _inspector
                        .InspectAsync(through, host, NoEmulators, NoEnvironments, [], cancellationToken: cancellationToken)
                        .ConfigureAwait(false);

                    if (!report.Available)
                    {
                        return Stays(host, entry, report.Reason, HarnessExit.HostUnavailable);
                    }

                    var transport = _transports.For(report);

                    // Asked about before any goes, as beside the worktree itself: a copy kept for a worker - one a sweep
                    // still running holds, or one that cannot be told - has lost nothing, a worker no sweep holds included.
                    var asked = await transport.RemoveWorkersAsync(entry.Path, measureOnly: true, cancellationToken).ConfigureAwait(false);

                    // Nothing has gone yet, so an interruption here is the asking's own: this copy is one of the rest.
                    cancellationToken.ThrowIfCancellationRequested();

                    if (Keeps(asked) is { } kept)
                    {
                        return Stays(host, entry, kept.Why, kept.Code);
                    }

                    workers = await transport.RemoveWorkersAsync(entry.Path, cancellationToken: cancellationToken).ConfigureAwait(false);

                    // A sweep that took one between the asking and the removal keeps the copy as well, and so does one
                    // that could not be removed, or a removal stopped part way: the others are gone by then, which is said.
                    if (Keeps(workers) is { } late)
                    {
                        cut = workers.Interrupted;
                        return Stays(host, entry, late.Why.TrimEnd('.') + Went(workers), late.Code);
                    }

                    removal = await transport.RemoveCopyAsync(entry.Path, cancellationToken).ConfigureAwait(false);
                }
                catch (HarnessException ex)
                {
                    return Stays(host, entry, ex.Message.TrimEnd('.') + Went(workers), ex.ExitCode);
                }

                var said = $"{host}: {Said(removal, entry)}{Beside(removal, workers)}";

                // Forgotten before the lock is let go, so that no run of a worktree of this name can take the lock
                // between the copy going and its entry going, make the copy again, and find it recorded already.
                try
                {
                    _record.Forget(layout, entry);
                }
                catch (HarnessException ex)
                {
                    return ($"{said}, and it could not be forgotten here, so it is still recorded: {ex.Message.TrimEnd('.')}", ex.ExitCode);
                }

                return (said, null);
            }
        }

        string Said(CopyRemoval removal, HostCopyEntry entry)
        {
            var whose = _record.SameTree(entry.Tree, tree) ? string.Empty : $", of the worktree that was at '{entry.Tree}'";

            return removal switch
            {
                CopyRemoval.Removed => $"removed its copy at '{entry.Path}'{whose}",
                CopyRemoval.Absent => $"found nothing at '{entry.Path}' to remove",
                CopyRemoval.Adopted => $"left '{entry.Path}' in place: the harness took that directory over, so it is yours to remove",
                _ => $"left '{entry.Path}' in place: nothing there says the harness made it",
            };
        }

        // What was done about the mutation workers kept beside a copy, after what was done about the copy.
        static string Beside(CopyRemoval removal, WorkersRemoval workers)
        {
            var gone = (workers.Removed.Count, removal) switch
            {
                (0, _) => string.Empty,
                (var count, CopyRemoval.Removed) => $", with the {count} mutation worker(s) kept beside it, {DiskSpace.Size(workers.Bytes)}",
                var (count, _) => $"; the {count} mutation worker(s) kept beside it were removed, {DiskSpace.Size(workers.Bytes)}",
            };

            return workers.Left.Count == 0
                ? gone
                : $"{gone}; left beside it: {string.Join("; ", workers.Left.Select(worker => $"'{worker.Path}', {worker.Why.TrimEnd('.')}"))}";
        }

        static (string, int?) Stays(HostId host, HostCopyEntry entry, string? why, int code)
            => ($"{host}: its copy at '{entry.Path}' stays, and is still recorded: {why?.TrimEnd('.') ?? "no reason was given"}", code);

        // What of the mutation workers kept beside a copy keeps the copy, and the code that leaves the command with -
        // their removal's verdict's (WorkersRemoval.Verdict): one a sweep still running holds, which ends by waiting; one
        // that could not be removed, or told; or a removal of them that was stopped. Null where nothing of them does.
        static (string Why, int Code)? Keeps(WorkersRemoval workers)
            => workers.Verdict == LegVerdict.Passed
                ? null
                : (workers.Kept switch
                    {
                        { As: WorkerLeftAs.Held } busy => $"a mutation worker kept beside it is in use - '{busy.Path}': {busy.Why}",
                        { } stuck => $"a mutation worker kept beside it could not be removed - {stuck.Told()}",
                        _ => "the removal of the mutation workers kept beside it was stopped before each had been dealt with",
                    },
                    Verdicts.ExitCodeFor(workers.Verdict));

        // Which workers went before whatever keeps the copy was met: said on the copy's own line, as nothing else would.
        static string Went(WorkersRemoval workers)
            => workers.Removed.Count == 0
                ? string.Empty
                : $"; {workers.Removed.Count} mutation worker(s) kept beside it were removed before that, {DiskSpace.Size(workers.Bytes)}";
    }

    /// <summary>Whether <paramref name="entry"/> names a path a worktree's copy is kept at: its own name, after the suffix.</summary>
    private static bool IsWorktreeCopy(HostCopyEntry entry)
        => entry.Path.TrimEnd('/', '\\').EndsWith(HostCopies.WorktreeSuffix + entry.Worktree, StringComparison.Ordinal);
}
