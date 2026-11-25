using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Sync;

/// <summary>
/// A sync into a WSL distribution's or an ssh host's copy of the repository, served by the DssHarness
/// already installed there.
/// </summary>
/// <remarks>
/// The far side runs the same <see cref="LocalSyncTransport"/> this machine uses for a local copy, so
/// there is one implementation of what a sync does to a tree and no second one to drift from it.
/// Requests travel inside the host agent's own request on standard input, never on a command line: a
/// command line is bounded, and an ssh server hands it to a shell whose identity is not known in
/// advance, so only a small set of characters may appear there at all.
/// </remarks>
/// <param name="host">The host, as reports name it.</param>
/// <param name="session">How the host is reached, and where its DssHarness is.</param>
/// <param name="hostCommands">Runs the host's agent.</param>
/// <param name="output">Where what the host says on its standard error, and a beat it lost, are told.</param>
/// <param name="keepAwake">What keeps the host awake while it serves each request, as the configuration declares it.</param>
/// <param name="keepAwakeEnvironment">What the host declares under <c>env</c>.</param>
/// <param name="keepAwakeDirectories">Where the survey found the host's programs.</param>
/// <param name="pieceBytes">
/// The most bytes of one file a request carries (<see cref="ISyncTransport.PieceBytes"/>); none for
/// <see cref="SyncServe.LargestBatch"/>. A test gives one of its own, to carry a file in pieces of the size it needs.
/// </param>
public sealed class RemoteSyncTransport(
    HostId host,
    HostSession session,
    IHostCommandRunner hostCommands,
    IHarnessOutput output,
    IReadOnlyList<string>? keepAwake = null,
    IReadOnlyDictionary<string, string>? keepAwakeEnvironment = null,
    IReadOnlyList<string>? keepAwakeDirectories = null,
    int? pieceBytes = null) : ISyncTransport
{
    private readonly HostSession _session = session;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

    /// <inheritdoc/>
    public int PieceBytes { get; } = pieceBytes is { } given
        ? given > 0 ? given : throw new ArgumentOutOfRangeException(nameof(pieceBytes), given, "A piece of a file holds at least one byte.")
        : (int)SyncServe.LargestBatch;

    /// <summary>
    /// What keeps the host awake while it serves each of this sync's requests, as the configuration
    /// declares it for that host. A sync to a fresh copy is the longest work a host does with no leg of
    /// its own running there, and a leg's own hold was all that ever kept one awake.
    /// </summary>
    private readonly IReadOnlyList<string> _keepAwake = keepAwake ?? [];

    /// <summary>What the host declares under <c>env</c>, which its keepAwake command starts under.</summary>
    private readonly IReadOnlyDictionary<string, string> _keepAwakeEnvironment =
        keepAwakeEnvironment ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the survey found the host's programs, which its keepAwake command is looked for in.</summary>
    private readonly IReadOnlyList<string> _keepAwakeDirectories = keepAwakeDirectories ?? [];

    /// <inheritdoc/>
    public HostId Host { get; } = host;

    /// <inheritdoc/>
    public async Task<bool> RootExistsAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false)).Exists;

    /// <inheritdoc/>
    public async Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken: cancellationToken).ConfigureAwait(false)).Mark;

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.Create, root, [mark.ToString()], cancellationToken);

    /// <inheritdoc/>
    public Task MarkFinishedAsync(string root, CopyReading reading, string? tree, CancellationToken cancellationToken = default)
        => AskAsync<object>(
            SyncServe.Create,
            root,
            [CopyMark.Complete.ToString(), .. SyncServe.Recording(reading, tree).Select(HostArgument.Of)],
            cancellationToken);

    /// <inheritdoc/>
    public Task InitialiseRepositoryAsync(string root, string? objectFormat = null, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.InitRepository, root, objectFormat is null ? [] : [objectFormat], cancellationToken);

    /// <inheritdoc/>
    public Task SendHistoryAsync(string root, string pack, long offset, byte[] piece, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(piece);

        return AskAsync<object>(
            SyncServe.HistoryPiece,
            root,
            [pack, offset.ToString(CultureInfo.InvariantCulture), HostArgument.Carrying(piece)],
            cancellationToken);
    }

    /// <inheritdoc/>
    public async Task<GitHeadMoved> TakeHistoryAsync(string root, GitHistoryTaken taken, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(taken);

        return await AskAsync<GitHeadMoved>(
                    SyncServe.TakeHistory,
                    root,
                    [taken.Pack ?? SyncServe.Nothing, taken.LeftOut ?? SyncServe.Nothing, .. SyncServe.Asking(taken.Wanted).Select(HostArgument.Of)],
                    cancellationToken)
                .ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer whether the HEAD of '{root}' was moved to {taken.Wanted.Commit}.");
    }

    /// <inheritdoc/>
    public Task IndexAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return AskAsync<object>(SyncServe.Index, root, [.. paths.Select(HostArgument.Of)], cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Asked from the home directory, not from the directory the copy is kept in: a removal is asked where that
    /// directory may be gone - a repositoryPath changed since, a distribution reinstalled - and a copy whose
    /// directory is gone is not there, which is the answer. Asked from it, the agent would refuse to start, and the
    /// copy would stay recorded for good.
    /// </remarks>
    public async Task<CopyRemoval> RemoveCopyAsync(string root, CancellationToken cancellationToken = default)
        => (await AskAsync<SyncRemoveAnswer>(SyncServe.RemoveCopy, root, [], cancellationToken, HomeDirectory).ConfigureAwait(false))?.Removal
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer whether it removed '{root}'.");

    /// <inheritdoc/>
    /// <remarks>
    /// Asked from the home directory, as a removal is: the directory the copies are kept in may be gone, and a
    /// host with none there answers that it keeps none.
    /// </remarks>
    public async Task<IReadOnlyList<HostCopyFound>> ListCopiesAsync(string repositoryPath, CancellationToken cancellationToken = default)
        => (await AskAsync<SyncCopiesAnswer>(SyncServe.ListCopies, repositoryPath, [], cancellationToken, HomeDirectory).ConfigureAwait(false))?.Copies
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer which copies it keeps beside '{repositoryPath}'.");

    /// <inheritdoc/>
    /// <remarks>
    /// Asked from the home directory, as a copy's removal is: the tree the workers are kept in may be gone, and a host
    /// with none there answers that it removed none.
    /// </remarks>
    public async Task<WorkersRemoval> RemoveWorkersAsync(string root, bool measureOnly = false, CancellationToken cancellationToken = default)
        => (await AskAsync<SyncWorkersAnswer>(
                    SyncServe.RemoveWorkers,
                    root,
                    measureOnly ? [SyncServe.MeasureOnly] : [],
                    cancellationToken,
                    HomeDirectory)
                .ConfigureAwait(false))?.Workers
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer which mutation workers in '{root}' it removed.");

    /// <inheritdoc/>
    /// <remarks>
    /// Asked from the home directory, as a copy's removal is: every copy of the repository there may be gone, and the
    /// workers an earlier version kept beside them still there.
    /// </remarks>
    public async Task<WorkersRemoval> RemoveFormerWorkersBesideCopiesAsync(string repositoryPath, bool measureOnly = false, CancellationToken cancellationToken = default)
        => (await AskAsync<SyncWorkersAnswer>(
                    SyncServe.RemoveFormerWorkers,
                    repositoryPath,
                    measureOnly ? [SyncServe.MeasureOnly] : [],
                    cancellationToken,
                    HomeDirectory)
                .ConfigureAwait(false))?.Workers
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer which workers an earlier version kept beside or in its copies of '{repositoryPath}' it removed.");

    /// <inheritdoc/>
    public async Task<SyncManifest> ReadManifestAsync(
        string root,
        IReadOnlyList<string> withheld,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(withheld);

        var answer = await AskAsync<SyncManifestAnswer>(
                SyncServe.Manifest,
                root,
                [.. withheld.Select(HostArgument.Of)],
                cancellationToken)
            .ConfigureAwait(false);

        // An answer that never arrived is refused rather than read as an empty copy. Empty is the
        // most dangerous thing this could return: it disables the deletion bound, which measures
        // against what the copy holds, and it makes a refusal report that taking the directory over
        // would remove nothing — advice somebody acts on, after which the manifest read succeeds and
        // everything the host held goes.
        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer with what '{root}' holds, so what a sync would delete there "
                + "is unknown and nothing was changed.");
        }

        return new SyncManifest(
            root,
            answer.Entries.ToDictionary(entry => entry.Path, entry => entry, StringComparer.Ordinal))
        {
            Links = answer.Links,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Its content is encoded into the request as the request is written, and never held here as text. One larger than a
    /// piece is sent a piece to a request (<see cref="SyncPieces.SendAsync"/>): a request is read on the host as one line,
    /// which one string holds, and a file inside one could be no larger than about 767 MiB.
    /// </remarks>
    public Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents.LongLength > PieceBytes
            ? WriteInPiecesAsync(root, relativePath, contents, cancellationToken)
            : AskAsync<object>(SyncServe.Write, root, [relativePath, HostArgument.Carrying(contents)], cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One request, and so one session: the cost of reaching this host is paid once for the batch rather
    /// than once per file. Each crosses as its path then its content, encoded into the request as the request is written:
    /// a batch built as text first - its files' base64, inside its JSON, inside the request's - left a
    /// consumer's first sync of 85 MiB holding 3.2 GiB here. A file larger than a piece is sent on its own, a piece to a
    /// request, after the files before it and before those after it.
    /// </remarks>
    public async Task WriteFilesAsync(
        string root,
        IReadOnlyList<SyncFileContent> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        var batch = new List<SyncFileContent>();

        foreach (var file in files)
        {
            if (file.Contents.LongLength <= PieceBytes)
            {
                batch.Add(file);
                continue;
            }

            await SendBatchAsync().ConfigureAwait(false);
            await WriteInPiecesAsync(root, file.Path, file.Contents, cancellationToken).ConfigureAwait(false);
        }

        await SendBatchAsync().ConfigureAwait(false);

        async Task SendBatchAsync()
        {
            if (batch.Count == 0)
            {
                return;
            }

            await AskAsync<object>(
                    SyncServe.WriteMany,
                    root,
                    [.. batch.SelectMany(file => new[] { HostArgument.Of(file.Path), HostArgument.Carrying(file.Contents) })],
                    cancellationToken)
                .ConfigureAwait(false);

            batch.Clear();
        }
    }

    /// <summary>A file whose bytes are in hand and larger than a piece, sent a piece to a request and put in place once all have arrived.</summary>
    private Task WriteInPiecesAsync(string root, string relativePath, byte[] contents, CancellationToken cancellationToken)
    {
        SyncFilePiece Piece(long offset) => new(
            contents.LongLength,
            offset == 0 ? FileContentHash.Of(contents) : null,
            contents.AsSpan((int)offset, (int)Math.Min(PieceBytes, contents.LongLength - offset)).ToArray());

        return SyncPieces.SendAsync(
            Piece(0),
            (offset, _) => Task.FromResult(Piece(offset)),
            this,
            root,
            relativePath,
            // Bytes in hand do not change while they are sent: a whole that differs from its pieces is a defect here.
            changed: () => new InvalidOperationException($"The bytes of '{relativePath}' in hand changed while they were sent to {Host}."),
            warn: message => _output.Warn(SyncService.CommandName, message),
            cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The piece's content is encoded into the request as the request is written, with the hash taken here of its bytes,
    /// which the host checks them against as they arrive (<see cref="SyncServe.PieceIn"/>), keeping nothing of a piece
    /// that arrived otherwise.
    /// </remarks>
    public Task WritePieceAsync(string root, string relativePath, long offset, ReadOnlyMemory<byte> piece, CancellationToken cancellationToken = default)
        => AskAsync<object>(
            SyncServe.WritePiece,
            root,
            [relativePath, offset.ToString(CultureInfo.InvariantCulture), FileContentHash.Of(piece.Span), HostArgument.Carrying(piece)],
            cancellationToken);

    /// <inheritdoc/>
    public Task PlacePiecesAsync(string root, string relativePath, FileContent whole, CancellationToken cancellationToken = default)
        => AskAsync<object>(
            SyncServe.PlacePieces,
            root,
            [relativePath, whole.Length.ToString(CultureInfo.InvariantCulture), whole.Content],
            cancellationToken);

    /// <inheritdoc/>
    public Task DropPiecesAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.DropPieces, root, [relativePath], cancellationToken);

    /// <inheritdoc/>
    public Task DeleteFileAsync(string root, string relativePath, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.Delete, root, [relativePath], cancellationToken);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<EmptiedDirectory>> RemoveEmptyDirectoriesAsync(
        string root,
        IReadOnlyList<string> directories,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directories);

        if (directories.Count == 0)
        {
            return [];
        }

        var answer = await AskAsync<SyncPruneAnswer>(
                SyncServe.Prune,
                root,
                [.. directories.Select(HostArgument.Of)],
                cancellationToken)
            .ConfigureAwait(false);

        // An answer that never arrived is refused rather than read as "nothing was removed": this
        // reports what a sync did to a host, and a report that quietly under-states it is the one
        // thing running the command again cannot put right.
        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer which directories of '{root}' it removed.");
        }

        return answer.Directories;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The host reads its own copy (<see cref="SyncServe.Pull"/>), the links along the path followed and none into a place
    /// its own isolation hides - the places <paramref name="hidden"/> names are this machine's, not the host's - and the
    /// file comes as <see cref="ReadFileAsync"/>'s does.
    /// </remarks>
    public Task<SyncFilePiece> PullFileAsync(string root, string relativePath, long offset, int count, IReadOnlyList<IsolatedPath> hidden, CancellationToken cancellationToken = default)
        => ReadAsync(SyncServe.Pull, root, relativePath, offset, count, cancellationToken);

    /// <inheritdoc/>
    /// <remarks>
    /// Decoded a line at a time as it arrives, into the bytes the answer said the piece holds, so it is never held here as
    /// text, and checked against the hash the host took of it before it is answered.
    /// </remarks>
    public Task<SyncFilePiece> ReadFileAsync(
        string root,
        string relativePath,
        long offset,
        int count,
        CancellationToken cancellationToken = default)
        => ReadAsync(SyncServe.Read, root, relativePath, offset, count, cancellationToken);

    /// <summary>One piece of a file read out of the host's copy by <paramref name="operation"/>, checked against the hash the host took of it.</summary>
    private async Task<SyncFilePiece> ReadAsync(string operation, string root, string relativePath, long offset, int count, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        byte[]? bytes = null;
        var filled = 0;

        var answer = await AskAsync<SyncFileAnswer>(
                operation,
                root,
                [relativePath, offset.ToString(CultureInfo.InvariantCulture), count.ToString(CultureInfo.InvariantCulture)],
                cancellationToken,
                following: (told, line) =>
                {
                    bytes ??= new byte[Told(told, relativePath, offset, count)];
                    filled += SyncServe.ReadContentLine(line, bytes.AsSpan(filled), relativePath);
                })
            .ConfigureAwait(false);

        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} did not answer with the content of '{relativePath}'.");
        }

        bytes ??= new byte[Told(answer, relativePath, offset, count)];

        // Fewer bytes than it said is a piece cut short on the way, and said so rather than as a file that changed.
        if (filled != bytes.Length)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' did not arrive whole from {Host}: {filled.ToString(CultureInfo.InvariantCulture)} of the "
                + $"{bytes.Length.ToString(CultureInfo.InvariantCulture)} bytes of its piece at byte {offset.ToString(CultureInfo.InvariantCulture)} did.");
        }

        // Checked against the hash the far side took of what it read, not against what arrived here. A piece that lost bytes
        // on the way is the failure this catches, and it can only be caught by a number that was computed before the journey.
        SyncServe.RefuseAPieceChanged(relativePath, offset, bytes, answer.PieceHash, $"{Host} and here");

        // A piece that starts the file says what all of it holds, which the rest of it is held to; one that does not, never.
        if ((offset == 0) != (answer.ContentHash is not null))
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} answered with a piece of '{relativePath}' at byte {offset.ToString(CultureInfo.InvariantCulture)} in a shape "
                + "this build cannot read: the hash of the whole file comes with the piece that starts it, and only with that one.");
        }

        return new SyncFilePiece(answer.Length, answer.ContentHash, bytes);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The host lists its own copy (<see cref="SyncServe.List"/>), the links along the path followed and none into a place
    /// its own isolation hides: the places <paramref name="hidden"/> names are this machine's, not the host's.
    /// </remarks>
    public async Task<SyncDirectoryListing> ListFilesAsync(string root, string relativeDirectory, IReadOnlyList<IsolatedPath>? hidden, CancellationToken cancellationToken = default)
        => await AskAsync<SyncDirectoryListing>(SyncServe.List, root, [relativeDirectory], cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} did not answer with what '{relativeDirectory}' holds.");

    /// <summary>
    /// How many bytes the piece <paramref name="answer"/> answers holds: as many as its file holds from
    /// <paramref name="offset"/> on, up to the <paramref name="count"/> asked for; refused where the file is said to hold
    /// fewer than none.
    /// </summary>
    /// <exception cref="HarnessException">The length is below nothing.</exception>
    private int Told(SyncFileAnswer answer, string relativePath, long offset, int count)
        => answer.Length >= 0
            ? (int)Math.Clamp(answer.Length - offset, 0, count)
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} said '{relativePath}' holds {answer.Length.ToString(CultureInfo.InvariantCulture)} bytes, which no file does.");

    /// <inheritdoc/>
    public async Task<SyncInspectAnswer> InspectAsync(string root, GitHistoryWanted? wanted = null, CancellationToken cancellationToken = default)
        => await AskAsync<SyncInspectAnswer>(SyncServe.Inspect, root, [.. SyncServe.Asking(wanted).Select(HostArgument.Of)], cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host} did not answer whether '{root}' exists.");

    /// <summary>
    /// Runs one sync operation on the host and reads its answer.
    /// </summary>
    /// <remarks>
    /// The exit code is read from the agent's completion line, never from the transport's own: ssh
    /// exits 255, and wsl.exe with codes of its own, when a connection fails, and neither is the
    /// command's result. A line that never arrives means the operation may not have run, or run only
    /// in part, which is exactly what must not be reported as a success.
    /// </remarks>
    /// <param name="operation">The operation.</param>
    /// <param name="root">The copy the operation acts on.</param>
    /// <param name="operands">What the operation takes after the copy's root, each value an argument of its own.</param>
    /// <param name="cancellationToken">Stops the operation here and there.</param>
    /// <param name="startIn">
    /// Where the agent starts, when not in the directory the copy is kept in, which a first sync needs to be there.
    /// </param>
    /// <param name="following">Given each line the agent writes after its answer, with that answer: a file's content.</param>
    private async Task<T?> AskAsync<T>(
        string operation,
        string root,
        IReadOnlyList<HostArgument> operands,
        CancellationToken cancellationToken,
        string? startIn = null,
        Action<T, string>? following = null)
        where T : class
    {
        var nonce = HostAgentProtocol.NewNonce();

        var request = new HostAgentRequest
        {
            Kind = HostAgentRequestKind.Run,

            // The operation names the tree it acts on in its own arguments, and the agent starts
            // in a directory that may not exist yet on a first sync.
            Directory = startIn ?? ParentOf(root),
            Arguments = [SyncServe.CommandName, operation, SyncServe.OperandsFollow, root, .. operands],
            KeepAwake = [.. _keepAwake],
            KeepAwakeEnvironment = new(_keepAwakeEnvironment, StringComparer.Ordinal),
            KeepAwakeDirectories = [.. _keepAwakeDirectories],
            Nonce = nonce,
        };

        T? answer = null;
        var lines = new HostAgentLines(nonce);

        var result = await _hostCommands.RunAsync(
                _session.Connection,
                new HostCommand
                {
                    Program = _session.ToolPath,
                    Arguments = [HostAgentProtocol.CommandName],
                    StandardInput = HostAgentProtocol.Input(request),
                    HoldStandardInputOpen = true,
                    StandardInputBeat = HostAgentProtocol.BeatOf(request),

                    // The answer arrives on standard output as one line, read whole, and a file's content
                    // after it a line at a time, each taken as it comes and kept nowhere else; standard error
                    // is shown line by line as it comes. Only the end of either is kept, for the message that
                    // says how the operation ended.
                    OutputKept = StreamKept.TailOfWholeLines,
                    ErrorKept = StreamKept.Tail,
                    OnOutputLine = line =>
                    {
                        if (!lines.Output(line))
                        {
                            return;
                        }

                        if (answer is null)
                        {
                            answer = SyncServe.ReadAnswer<T>(line);
                            return;
                        }

                        following?.Invoke(answer, line);
                    },
                    OnErrorLine = line =>
                    {
                        if (!lines.Error(line))
                        {
                            return;
                        }

                        // ssh writes here too, and names the address it dialled - one the host's name resolved
                        // to - rather than the one the configuration declares.
                        _output.RawError(HostProbes.AsConfigured(line, _session.Connection));
                    },
                },
                cancellationToken)
            .SayingUnreached($"{Host}", line => _output.Warn(SyncService.CommandName, line))
            .ConfigureAwait(false);

        if (HostProbes.BeatLost(result) is { } unheard)
        {
            _output.Warn(SyncService.CommandName, $"{Host}: {unheard}");
        }

        if (lines.Finished is not { } exitCode)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host}: {HostProbes.NeverFinished($"'{operation}'", result, _session.Connection)}");
        }

        if (exitCode != HarnessExit.Success)
        {
            // From the agent's own output on, and under the name the configuration declares, as every other
            // reason built here is: the whole capture holds whatever reached it ahead of the agent (see
            // HostAgentProtocol.StartedLine), and ssh names the address it dialled, one the host's name resolved to.
            throw new HarnessException(
                exitCode,
                $"{Host}: '{operation}' exited {exitCode}"
                + HostProbes.Detail(HostProbes.AsConfigured(HostAgentProtocol.SinceServing(result.StandardError, nonce), _session.Connection)));
        }

        return answer;
    }

    /// <summary>The home directory, as the agent is asked to start in it.</summary>
    private const string HomeDirectory = "~";

    /// <summary>
    /// The directory the agent starts in. A first sync creates the copy, so the agent cannot start
    /// inside it; its parent is where the operation is served from instead - or, for a copy kept in a
    /// slot of a tree (<see cref="HarnessLayout.InSlot"/>), the tree: a sync withholds the slot, so the
    /// first copy kept there makes it, and the tree, synced first, is there.
    /// </summary>
    private static string ParentOf(string root)
    {
        var parent = Up(root.Replace('\\', '/').TrimEnd('/'));
        var slot = parent[(parent.LastIndexOf('/') + 1)..];

        return slot is HarnessLayout.LegsDirectoryName or HarnessLayout.MutationsDirectoryName or HarnessLayout.FormerMutationsDirectoryName ? Up(parent) : parent;

        static string Up(string path) => path.LastIndexOf('/') is var slash and > 0 ? path[..slash] : path;
    }
}
