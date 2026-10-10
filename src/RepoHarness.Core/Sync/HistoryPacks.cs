using System.Text.Json;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

/// <summary>
/// The packs made of one reading's history for the copies synced from it: each made once, whatever number of copies it
/// is sent to, kept in the tree's own git directory, and removed with the reading.
/// </summary>
/// <remarks>
/// <para>
/// Made for each copy, a sweep's workers were each packed the same commit; and kept in the temporary directory, which a
/// system may hold in memory, a pack - the size of what it carries, every commit of the repository where all are asked
/// for - was counted by nothing. In the git directory it is on the volume that already holds the repository, and there
/// before the room a sweep's workers need is measured.
/// </para>
/// <para>
/// What a process that was killed left there is removed by the next reading that packs: each directory is recorded as
/// its process's own in a file beside it, written before the directory is made, and one whose process no longer runs is
/// nobody's (<see cref="IProcessIdentity"/>). One recorded by another machine cannot be asked, and stands.
/// </para>
/// </remarks>
internal sealed class HistoryPacks : IDisposable
{
    /// <summary>What a directory of packs is for, in its name.</summary>
    internal const string Purpose = "history";

    /// <summary>What the record of a directory's owner adds to the directory's path.</summary>
    internal const string OwnerSuffix = ".owner.json";

    private readonly IGitClient _git;
    private readonly IFileSystem _fileSystem;
    private readonly IProcessIdentity _identity;
    private readonly string _root;
    private readonly GitHistoryWanted _wanted;
    private readonly Action<string> _warn;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, GitHistoryPack> _made = new(StringComparer.Ordinal);
    private ScratchDirectory? _kept;
    private bool _disposed;

    /// <summary>Names the packs of a reading; nothing is packed until a copy needs one.</summary>
    /// <param name="git">Packs, and says where the tree's git directory is.</param>
    /// <param name="fileSystem">What the packs are removed through.</param>
    /// <param name="identity">This process, and whether another still runs.</param>
    /// <param name="root">The top of the tree read.</param>
    /// <param name="wanted">The commit the tree's HEAD named when it was read, and how much behind it a copy is given.</param>
    /// <param name="warn">Says, for the command syncing, what could not be removed, and why.</param>
    public HistoryPacks(IGitClient git, IFileSystem fileSystem, IProcessIdentity identity, string root, GitHistoryWanted wanted, Action<string> warn)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(wanted);
        ArgumentNullException.ThrowIfNull(warn);

        _git = git;
        _fileSystem = fileSystem;
        _identity = identity;
        _root = root;
        _wanted = wanted;
        _warn = warn;
    }

    /// <summary>The process a directory of packs is the own of.</summary>
    /// <param name="Machine">The machine it runs on.</param>
    /// <param name="ProcessId">Its process id.</param>
    /// <param name="ProcessStamp">What tells it from a later holder of that id, where this platform says.</param>
    internal sealed record Owner(string Machine, int ProcessId, string? ProcessStamp = null);

    /// <summary>
    /// The pack of what a copy lacks of the reading's history: made the first time it is asked for, and the same one
    /// every time after.
    /// </summary>
    /// <param name="leftOut">The commit of its own the copy holds, left out of the pack, or <see langword="null"/> where nothing is.</param>
    /// <param name="cancellationToken">Stops the packing; the copy asking next packs again.</param>
    /// <exception cref="HarnessException">git could not pack it, or nothing could be kept where the packs go.</exception>
    public async Task<GitHistoryPack> ForAsync(string? leftOut, CancellationToken cancellationToken)
    {
        // One at a time: two copies lacking the same thing are sent one pack, and the second waits for it.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var key = leftOut ?? string.Empty;

            if (!_made.TryGetValue(key, out var pack))
            {
                _kept ??= await KeepAsync(cancellationToken).ConfigureAwait(false);
                pack = await _git.PackHistoryAsync(_root, _wanted, leftOut, _kept.Path, cancellationToken).ConfigureAwait(false);
                _made[key] = pack;
            }

            return pack;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Removes every pack made, and the record that they were this process's.</summary>
    public void Dispose()
    {
        // Waited for: a pack still being written is written into what this removes.
        _gate.Wait();

        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _made.Clear();

            if (_kept is not { } kept)
            {
                return;
            }

            kept.Dispose();

            // The record goes with what it records, and stays where that could not be removed: the next reading that
            // packs, once this process has ended, clears both.
            if (!_fileSystem.DirectoryExists(kept.Path))
            {
                Discard(OwnerOf(kept.Path));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Where the owner of the directory at <paramref name="directory"/> is recorded: beside it, never in it.</summary>
    internal static string OwnerOf(string directory) => directory + OwnerSuffix;

    /// <summary>The directory the packs go in, recorded as this process's own, with what a dead process left beside it cleared.</summary>
    private async Task<ScratchDirectory> KeepAsync(CancellationToken cancellationToken)
    {
        var under = await _git.ResolveGitDirectoryAsync(_root, Path.Combine(_root, ".git"), cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{_root}' named a commit when it was read and is no git repository's tree now, so nothing of its history can be packed.");

        ClearAbandoned(under);

        var kept = new ScratchDirectory(_fileSystem, Purpose, _warn, under);
        var record = OwnerOf(kept.Path);

        try
        {
            // Before the directory is made: one found with no record beside it would be nobody's to clear.
            _fileSystem.WriteAllTextAtomic(
                record,
                JsonSerializer.Serialize(new Owner(_identity.CurrentMachine, _identity.CurrentId, _identity.Current), JsonStateFile.Options) + "\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"The history of '{_root}' could not be packed for its copies: '{record}', which records whose the packs are, "
                + $"could not be written: {ex.Message.TrimEnd('.')}.");
        }

        return kept;
    }

    /// <summary>Removes each directory of packs in <paramref name="under"/> whose process no longer runs, with its record.</summary>
    private void ClearAbandoned(string under)
    {
        List<string> records;

        try
        {
            records =
            [
                .. _fileSystem.EnumerateFiles(under, recursive: false).Where(file => IsRecord(Path.GetFileName(file))),
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"'{under}' could not be searched for packs an earlier sync left there: {ex.Message.TrimEnd('.')}.");
            return;
        }

        foreach (var record in records)
        {
            try
            {
                // One that reads as nothing is left: what it records cannot be told to be nobody's.
                if (JsonSerializer.Deserialize<Owner>(_fileSystem.ReadAllText(record), JsonStateFile.Options) is not { } owner
                    || _identity.Stands(owner.Machine, owner.ProcessId, owner.ProcessStamp))
                {
                    continue;
                }

                var left = record[..^OwnerSuffix.Length];

                if (_fileSystem.DirectoryExists(left))
                {
                    _fileSystem.DeleteDirectory(left);
                }

                _fileSystem.DeleteFile(record);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                _warn($"The packs '{record}' records, which an earlier sync left in '{under}', could not be cleared: {ex.Message.TrimEnd('.')}.");
            }
        }
    }

    private static bool IsRecord(string name)
        => name.StartsWith(ScratchDirectory.Prefix + Purpose + "-", StringComparison.Ordinal) && name.EndsWith(OwnerSuffix, StringComparison.Ordinal);

    private void Discard(string record)
    {
        try
        {
            _fileSystem.DeleteFile(record);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _warn($"'{record}' could not be removed: {ex.Message.TrimEnd('.')}.");
        }
    }
}
