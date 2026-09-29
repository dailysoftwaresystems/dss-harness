using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Output;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Worktrees;

namespace RepoHarness.Core.Orchestration;

/// <summary>What an agent's worktree holds under its evidence roots, each file with its digest.</summary>
/// <param name="Files">Every evidence file, relative to the worktree, with its digest.</param>
/// <param name="Linked">
/// The roots that are links, or are reached through one, and the links to files under them: what they lead to is not the
/// worktree's to lose, which removing it leaves where it is, so it is neither kept nor deleted.
/// </param>
public sealed record EvidenceFound(IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Linked);

/// <summary>What keeping an agent's evidence did.</summary>
/// <param name="Kept">Every file kept, relative to the worktree, with the digest its copy was proved against.</param>
/// <param name="Linked">The roots, and files under them, that are links: neither kept nor deleted.</param>
/// <param name="Problem">Why the evidence cannot be relied on as kept, and nothing of it is left kept; null where every file is.</param>
public sealed record EvidenceKept(IReadOnlyDictionary<string, string> Kept, IReadOnlyList<string> Linked, string? Problem);

/// <summary>What deleting the kept originals did.</summary>
/// <param name="Left">Each original left, with why: changed since it was kept, or one that could not be deleted.</param>
public sealed record EvidenceDeleted(IReadOnlyList<string> Left);

/// <summary>
/// Keeps what an agent's worktree holds under the evidence roots - measurements, logs, anything ignored because it is not
/// source - in the agent's own directory, every file read back there, before anything of the worktree is removed: a check
/// after an irreversible step is a post-mortem.
/// </summary>
/// <remarks>
/// Files are found as <see cref="WorktreeEvidence"/> finds them, the one reading delete-worktree's evidence check uses, so
/// what is kept is what the removal would have refused over. The originals are deleted by this class, and only those still
/// holding what was kept; the worktree's removal is then asked for without waiving its evidence check, so a file written
/// after the copy was read back stops it rather than going with it.
/// </remarks>
/// <param name="fileSystem">Reads the worktree, and writes the kept copies.</param>
/// <param name="platform">How paths compare.</param>
internal sealed class AgentEvidence(IFileSystem fileSystem, IHostPlatform platform)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IHostPlatform _platform = platform;

    /// <summary>Every evidence file under <paramref name="roots"/> in <paramref name="worktree"/>, with its digest.</summary>
    /// <param name="worktree">The worktree, or what is left of it.</param>
    /// <param name="roots">The evidence roots, relative to it.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    /// <exception cref="IOException">A root, or a file under one, could not be read: an unread root is not an empty one.</exception>
    /// <exception cref="UnauthorizedAccessException">One could not be read.</exception>
    public async Task<EvidenceFound> FindAsync(string worktree, IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var found = Walk(worktree, roots);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var relative in found.All)
        {
            files[relative] = (await FileContentHash.OfAsync(_fileSystem, Path.Combine(worktree, relative), cancellationToken).ConfigureAwait(false)).Content;
        }

        return new EvidenceFound(new Dictionary<string, string>(files, StringComparer.Ordinal), found.Linked);
    }

    /// <summary>
    /// Copies every evidence file under <paramref name="roots"/> to <paramref name="destination"/>, each under its path in
    /// the worktree, each copy proved as it is made - read back and compared with what was read of its file before it is
    /// put in place: refused where the destination is inside the worktree, where a copy does not prove, and where a file
    /// measured before is gone or changed since. Refused, what it copied is taken back, so a directory of it is never left
    /// holding part of the evidence.
    /// </summary>
    /// <param name="worktree">The worktree, or what is left of it.</param>
    /// <param name="roots">The evidence roots, relative to it: those named before a fold and after it.</param>
    /// <param name="measured">What the roots held when the command measured them, before anything was written; null where nothing was measured before.</param>
    /// <param name="destination">A directory that does not exist yet, outside the worktree.</param>
    /// <param name="cancellationToken">Stops it before a copy is put in place.</param>
    public async Task<EvidenceKept> KeepAsync(
        string worktree,
        IReadOnlyList<string> roots,
        IReadOnlyDictionary<string, string>? measured,
        string destination,
        CancellationToken cancellationToken)
    {
        var comparison = _platform.PathComparison;
        string? problem;
        var kept = new SortedDictionary<string, string>(StringComparer.Ordinal);
        IReadOnlyList<string> linked = [];

        try
        {
            // A copy into the tree about to be removed is a copy into nothing, reported as kept.
            if (PathContainment.IsInsideOrSame(_fileSystem.ResolveLinks(worktree), _fileSystem.ResolveLinks(destination), comparison))
            {
                return new EvidenceKept(new Dictionary<string, string>(), [], $"'{destination}' is inside '{worktree}', which is about to be removed, so nothing can be kept there");
            }

            var found = Walk(worktree, roots);
            linked = found.Linked;

            foreach (var relative in found.All)
            {
                kept[relative] = (await VerifiedFileCopy.CopyAsync(_fileSystem, Path.Combine(worktree, relative), Path.Combine(destination, relative), cancellationToken: cancellationToken).ConfigureAwait(false)).Content;
            }

            var moved = (measured ?? new Dictionary<string, string>())
                .Where(pair => !kept.TryGetValue(pair.Key, out var now) || now != pair.Value)
                .Select(pair => pair.Key)
                .Order(StringComparer.Ordinal)
                .ToList();

            problem = moved.Count > 0 ? $"{moved.Count} file(s) are gone or changed in the worktree since it was measured: {ReportText.Listed(moved)}" : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            problem = ex is OperationCanceledException ? "keeping it was interrupted" : $"the evidence could not be read, copied or proved: {ex.Message.TrimEnd('.')}";
        }

        if (problem is null)
        {
            return new EvidenceKept(new Dictionary<string, string>(kept, StringComparer.Ordinal), linked, null);
        }

        return new EvidenceKept(new Dictionary<string, string>(), linked, TakeBack(destination) is { } left ? $"{problem}; and {left}" : problem);
    }

    /// <summary>What the roots hold, as <see cref="WorktreeEvidence"/> finds it; a root that cannot be read raises, never read as empty.</summary>
    private EvidenceFiles Walk(string worktree, IReadOnlyList<string> roots)
    {
        var found = WorktreeEvidence.Find(_fileSystem, _platform.PathComparison, worktree, roots);

        return found.Unreadable.Count > 0
            ? throw new IOException($"what {string.Join(", ", found.Unreadable.Select(root => $"'{root.Root}' ({root.Why})"))} hold{(found.Unreadable.Count == 1 ? "s" : string.Empty)} cannot be read")
            : found;
    }

    /// <summary>
    /// Deletes from the worktree each file of <paramref name="kept"/> that still holds what was kept, and says which it left
    /// and why: a file changed since is not the one kept, and one that cannot be deleted - held open, or refused - stays; the
    /// worktree's removal then stops on either, having lost nothing.
    /// </summary>
    /// <param name="worktree">The worktree.</param>
    /// <param name="kept">What was kept, relative to it, with each file's digest.</param>
    /// <param name="cancellationToken">Stops the reading.</param>
    public async Task<EvidenceDeleted> DeleteKeptAsync(string worktree, IReadOnlyDictionary<string, string> kept, CancellationToken cancellationToken)
    {
        var left = new List<string>();

        foreach (var (relative, digest) in kept)
        {
            var file = Path.Combine(worktree, relative);

            try
            {
                if (_fileSystem.KindOf(file) != PathKind.File)
                {
                    continue;
                }

                if ((await FileContentHash.OfAsync(_fileSystem, file, cancellationToken).ConfigureAwait(false)).Content == digest)
                {
                    _fileSystem.DeleteFile(file);
                }
                else
                {
                    left.Add($"{relative} (changed since it was kept)");
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                left.Add($"{relative} ({ex.Message.TrimEnd('.')})");
            }
        }

        return new EvidenceDeleted(left);
    }

    /// <summary>Removes a keeping that could not be completed; says why where it cannot be.</summary>
    private string? TakeBack(string destination)
    {
        try
        {
            _fileSystem.DeleteDirectory(destination);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"what was copied to '{destination}' could not be taken back: {ex.Message.TrimEnd('.')}";
        }
    }
}
