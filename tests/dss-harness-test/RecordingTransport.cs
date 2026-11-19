using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// Everything the transport it wraps does, counting what it was asked and holding what it was
/// given. A real transport does the work - a <see cref="LocalSyncTransport"/> against a directory
/// here, or one reaching this build's own agent - so what a test asserts about a host's copy is what
/// a copy actually became rather than what a stand-in was told to say.
/// </summary>
/// <param name="inner">The transport that does the work.</param>
/// <param name="losesAFileWhenVerifying">
/// Whether to drop a file from the verification's manifest, which is what a copy that did not land
/// intact looks like from here.
/// </param>
/// <param name="reports">
/// The host to answer as, since the local transport is always <c>local</c> and the one thing
/// <c>--adopt</c> decides is which host a directory belongs to.
/// </param>
internal sealed class RecordingTransport(
    ISyncTransport inner,
    bool losesAFileWhenVerifying = false,
    HostId? reports = null) : ISyncTransport
{
    private int _manifests;
    private int _writes;

    /// <summary>Whether a batch is being carried, so a file of it is not counted as a write of its own.</summary>
    private bool _batching;

    /// <summary>How many times both halves were asked together.</summary>
    public int Inspections { get; private set; }

    /// <summary>How many times existence was asked on its own.</summary>
    public int RootExistsAsked { get; private set; }

    /// <summary>How many times the mark was asked on its own.</summary>
    public int MarksAsked { get; private set; }

    /// <summary>Every path written, in order, whether or not the write was then undone.</summary>
    public List<string> Written { get; } = [];

    /// <summary>Each reading a finished copy was marked as holding, in order.</summary>
    public List<CopyReading> Readings { get; } = [];

    /// <summary>The tree each finished copy was marked as made from, in order, beside <see cref="Readings"/>.</summary>
    public List<string?> Trees { get; } = [];

    /// <summary>Every path deleted, in order.</summary>
    public List<string> Deleted { get; } = [];

    /// <summary>
    /// What to answer about the copy instead of looking, or <see langword="null"/> to look. A host
    /// whose copy is missing or is a directory the harness never made is a state a test cannot
    /// reach by writing files, because the answer is about a marker that is not there.
    /// </summary>
    public SyncInspectAnswer? Answers { get; init; }

    /// <summary>
    /// Which write to fail, counting from one, or zero to fail none. What a link that dropped part
    /// way through a transfer looks like from here.
    /// </summary>
    public int FailsWrite { get; init; }

    /// <summary>Whether a deletion should fail, for the cleanup that could not finish either.</summary>
    public bool RefusesToDelete { get; init; }

    public HostId Host => reports ?? inner.Host;

    public int PieceBytes => inner.PieceBytes;

    public Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
    {
        RootExistsAsked++;
        return Answers is { } answer ? Task.FromResult(answer.Exists) : inner.RootExistsAsync(root, cancellationToken);
    }

    public Task<SyncInspectAnswer> InspectAsync(string root, GitHistoryWanted? wanted = null, CancellationToken cancellationToken = default)
    {
        Inspections++;
        return Answers is { } answer ? Task.FromResult(answer) : inner.InspectAsync(root, wanted, cancellationToken);
    }

    public Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
    {
        MarksAsked++;
        return Answers is { } answer ? Task.FromResult(answer.Mark) : inner.ReadMarkAsync(root, cancellationToken);
    }

    /// <summary>Every mark the copy was given, in order.</summary>
    public List<CopyMark> Marked { get; } = [];

    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
    {
        Marked.Add(mark);
        return inner.CreateRootAsync(root, mark, cancellationToken);
    }

    public Task MarkFinishedAsync(string root, CopyReading reading, string? tree, CancellationToken cancellationToken = default)
    {
        Marked.Add(CopyMark.Complete);
        Readings.Add(reading);
        Trees.Add(tree);
        return inner.MarkFinishedAsync(root, reading, tree, cancellationToken);
    }

    public Task InitialiseRepositoryAsync(string root, string? objectFormat = null, CancellationToken cancellationToken = default)
        => inner.InitialiseRepositoryAsync(root, objectFormat, cancellationToken);

    /// <summary>Every piece of a pack the copy was sent: the pack's name, where the piece starts, and how many bytes it is.</summary>
    public List<(string Pack, long Offset, int Bytes)> HistorySent { get; } = [];

    /// <summary>
    /// Which piece of a pack to fail, counting from one over every pack sent, or zero to fail none: what a link that
    /// dropped part way through a pack looks like from here.
    /// </summary>
    public int FailsPiece { get; set; }

    public Task SendHistoryAsync(string root, string pack, long offset, byte[] piece, CancellationToken cancellationToken = default)
    {
        HistorySent.Add((pack, offset, piece.Length));

        return HistorySent.Count == FailsPiece
            ? throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped sending the piece at {offset}")
            : inner.SendHistoryAsync(root, pack, offset, piece, cancellationToken);
    }

    /// <summary>Every history the copy was asked to take, in order.</summary>
    public List<GitHistoryTaken> HistoryTaken { get; } = [];

    public Task<GitHeadMoved> TakeHistoryAsync(string root, GitHistoryTaken taken, CancellationToken cancellationToken = default)
    {
        HistoryTaken.Add(taken);
        return inner.TakeHistoryAsync(root, taken, cancellationToken);
    }

    /// <summary>Every set of paths the copy's index was made to hold, in the order asked.</summary>
    public List<IReadOnlyList<string>> Indexed { get; } = [];

    public Task IndexAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        Indexed.Add(paths);
        return inner.IndexAsync(root, paths, cancellationToken);
    }

    public async Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        var manifest = await inner.ReadManifestAsync(root, withheld, cancellationToken).ConfigureAwait(false);

        // The first read builds the plan; the second is the verification.
        if (!losesAFileWhenVerifying || ++_manifests < 2 || manifest.Entries.Count == 0)
        {
            return manifest;
        }

        var short_ = manifest.Entries
            .Where(entry => entry.Key != manifest.Paths.First())
            .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);

        return new SyncManifest(root, short_) { Links = manifest.Links };
    }

    public async Task WriteFileAsync(string root, string relativePath, byte[] contents, CancellationToken cancellationToken = default)
    {
        if (!_batching)
        {
            SingleWrites++;
        }

        if (++_writes == FailsWrite)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped writing '{relativePath}'");
        }

        await inner.WriteFileAsync(root, relativePath, contents, cancellationToken).ConfigureAwait(false);
        Written.Add(relativePath);
    }

    /// <summary>
    /// How many batched writes were asked for. Over a connection one of these is one session, so it is what
    /// a sync's cost in sessions is counted by.
    /// </summary>
    public int Batches { get; private set; }

    /// <summary>How many files were written one at a time, outside any batch: what batching exists to avoid.</summary>
    public int SingleWrites { get; private set; }

    public async Task WriteFilesAsync(string root, IReadOnlyList<SyncFileContent> files, CancellationToken cancellationToken = default)
    {
        Batches++;
        _batching = true;

        // Through the same write as a file of its own, so a batch counts each file it carries and
        // FailsWrite still names the file to fail rather than the batch.
        try
        {
            foreach (var file in files)
            {
                await WriteFileAsync(root, file.Path, file.Contents, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _batching = false;
        }
    }

    public async Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        if (RefusesToDelete)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"'{relativePath}' could not be removed");
        }

        await inner.DeleteFileAsync(root, relativePath, cancellationToken).ConfigureAwait(false);
        Deleted.Add(relativePath);
    }

    public Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
        => inner.RemoveEmptyDirectoriesAsync(root, directories, cancellationToken);

    /// <summary>Every piece of a file sent to be kept aside until the whole of it arrives: the file, where the piece starts, and how many bytes it is.</summary>
    public List<(string Path, long Offset, int Bytes)> PiecesSent { get; } = [];

    /// <summary>Every file put in place from its pieces, in order.</summary>
    public List<string> Placed { get; } = [];

    /// <summary>Every file whose pieces were removed, its carrying having stopped, in order.</summary>
    public List<string> Dropped { get; } = [];

    /// <summary>
    /// Told each piece once it is sent, with its file and where it starts: what a tree that moves while a file of it crosses
    /// a piece at a time looks like from here.
    /// </summary>
    public Action<string, long>? AfterPiece { get; init; }

    public async Task WritePieceAsync(string root, string relativePath, long offset, ReadOnlyMemory<byte> piece, CancellationToken cancellationToken = default)
    {
        PiecesSent.Add((relativePath, offset, piece.Length));
        await inner.WritePieceAsync(root, relativePath, offset, piece, cancellationToken).ConfigureAwait(false);
        AfterPiece?.Invoke(relativePath, offset);
    }

    /// <summary>
    /// A file put in place from its pieces is written as a whole file is: counted among the writes, and failed where
    /// <see cref="FailsWrite"/> names it.
    /// </summary>
    public async Task PlacePiecesAsync(string root, string relativePath, FileContent whole, CancellationToken cancellationToken = default)
    {
        if (++_writes == FailsWrite)
        {
            throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped putting '{relativePath}' in place");
        }

        await inner.PlacePiecesAsync(root, relativePath, whole, cancellationToken).ConfigureAwait(false);
        Placed.Add(relativePath);
        Written.Add(relativePath);
    }

    public Task DropPiecesAsync(string root, string relativePath, CancellationToken cancellationToken = default)
    {
        Dropped.Add(relativePath);
        return inner.DropPiecesAsync(root, relativePath, cancellationToken);
    }

    /// <summary>
    /// Which read to fail, counting from one, or zero to fail none: what a link that dropped part way through a pull
    /// looks like from here. Each piece read is a read of its own.
    /// </summary>
    public int FailsRead { get; init; }

    private int _reads;

    public Task<SyncFilePiece> ReadFileAsync(string root, string relativePath, long offset, int count, CancellationToken cancellationToken = default)
        => ++_reads == FailsRead
            ? throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped reading '{relativePath}'")
            : inner.ReadFileAsync(root, relativePath, offset, count, cancellationToken);

    /// <summary>The hidden places each pull's read was given, in order: one for each piece read.</summary>
    public List<IReadOnlyList<IsolatedPath>> PulledHidden { get; } = [];

    public Task<SyncFilePiece> PullFileAsync(string root, string relativePath, long offset, int count, IReadOnlyList<IsolatedPath> hidden, CancellationToken cancellationToken = default)
    {
        PulledHidden.Add(hidden);
        return ++_reads == FailsRead
            ? throw new HarnessException(HarnessExit.HostUnavailable, $"the link dropped reading '{relativePath}'")
            : inner.PullFileAsync(root, relativePath, offset, count, hidden, cancellationToken);
    }

    /// <summary>Every directory the copy was asked to list, as it was asked.</summary>
    public List<string> Listed { get; } = [];

    public Task<SyncDirectoryListing> ListFilesAsync(string root, string relativeDirectory, IReadOnlyList<IsolatedPath>? hidden, CancellationToken cancellationToken = default)
    {
        Listed.Add(relativeDirectory);
        return inner.ListFilesAsync(root, relativeDirectory, hidden, cancellationToken);
    }

    public Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default)
        => inner.RemoveCopyAsync(root, cancellationToken);

    public Task<IReadOnlyList<HostCopyFound>> ListCopiesAsync(string repositoryPath, CancellationToken cancellationToken = default)
        => inner.ListCopiesAsync(repositoryPath, cancellationToken);

    public Task<WorkersRemoval> RemoveWorkersAsync(string root, bool measureOnly = false, CancellationToken cancellationToken = default)
        => inner.RemoveWorkersAsync(root, measureOnly, cancellationToken);

    public Task<WorkersRemoval> RemoveFormerWorkersBesideCopiesAsync(string repositoryPath, bool measureOnly = false, CancellationToken cancellationToken = default)
        => inner.RemoveFormerWorkersBesideCopiesAsync(repositoryPath, measureOnly, cancellationToken);
}
