using System.Globalization;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Output;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Core.Sync;

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
public sealed class RemoteSyncTransport(
    HostId host,
    HostSession session,
    IHostCommandRunner hostCommands,
    IHarnessOutput output,
    IReadOnlyList<string>? keepAwake = null,
    IReadOnlyDictionary<string, string>? keepAwakeEnvironment = null,
    IReadOnlyList<string>? keepAwakeDirectories = null) : ISyncTransport
{
    private readonly HostSession _session = session;
    private readonly IHostCommandRunner _hostCommands = hostCommands;
    private readonly IHarnessOutput _output = output;

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
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).Exists;

    /// <inheritdoc/>
    public async Task<CopyMark> ReadMarkAsync(string root, CancellationToken cancellationToken = default)
        => (await InspectAsync(root, cancellationToken).ConfigureAwait(false)).Mark;

    /// <inheritdoc/>
    public Task CreateRootAsync(string root, CopyMark mark = CopyMark.Complete, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.Create, root, [mark.ToString()], cancellationToken);

    /// <inheritdoc/>
    public Task InitialiseRepositoryAsync(string root, CancellationToken cancellationToken = default)
        => AskAsync<object>(SyncServe.InitRepository, root, [], cancellationToken);

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
    /// Asked from the home directory, as a copy's removal is: the tree the workers copy, and the directory it was kept
    /// in, may be gone, and a host with none there answers that it removed none.
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
                $"{Host} did not answer which mutation workers beside '{root}' it removed.");

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
    /// Refused here if it is too large to carry, before anything is sent, so the reader is told which file rather than
    /// left with a host that could not read the request. Its content is encoded into the request as the request is
    /// written, and never held here as text.
    /// </remarks>
    public Task WriteFileAsync(
        string root,
        string relativePath,
        byte[] contents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(contents);

        SyncServe.RefuseAFileTooLargeToCarry(contents.LongLength, relativePath, Host.ToString());

        return AskAsync<object>(SyncServe.Write, root, [relativePath, HostArgument.Carrying(contents)], cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// One request, and so one session: the cost of reaching this host is paid once for the batch rather
    /// than once per file. Each file is refused here if it alone is too large to carry, before anything is
    /// sent. Each crosses as its path then its content, encoded into the request as the request is written:
    /// a batch built as text first - its files' base64, inside its JSON, inside the request's - left a
    /// consumer's first sync of 85 MiB holding 3.2 GiB here.
    /// </remarks>
    public Task WriteFilesAsync(
        string root,
        IReadOnlyList<SyncFileContent> files,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return Task.CompletedTask;
        }

        foreach (var file in files)
        {
            SyncServe.RefuseAFileTooLargeToCarry(file.Contents.LongLength, file.Path, Host.ToString());
        }

        return AskAsync<object>(
            SyncServe.WriteMany,
            root,
            [.. files.SelectMany(file => new[] { HostArgument.Of(file.Path), HostArgument.Carrying(file.Contents) })],
            cancellationToken);
    }

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
    /// Decoded a line at a time as it arrives, into the bytes the answer said the file holds, so it is never held here as
    /// text.
    /// </remarks>
    public async Task<byte[]> ReadFileAsync(
        string root,
        string relativePath,
        CancellationToken cancellationToken = default)
    {
        byte[]? contents = null;
        var filled = 0;

        var answer = await AskAsync<SyncFileAnswer>(
                SyncServe.Read,
                root,
                [relativePath],
                cancellationToken,
                following: (told, line) =>
                {
                    contents ??= new byte[Told(told, relativePath)];
                    filled += SyncServe.ReadContentLine(line, contents.AsSpan(filled), relativePath);
                })
            .ConfigureAwait(false);

        if (answer is null)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} did not answer with the content of '{relativePath}'.");
        }

        contents ??= new byte[Told(answer, relativePath)];

        // Fewer bytes than it said is a file cut short on the way, and said so rather than as a file that changed.
        if (filled != contents.Length)
        {
            throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' did not arrive whole from {Host}: {filled.ToString(CultureInfo.InvariantCulture)} of its "
                + $"{contents.Length.ToString(CultureInfo.InvariantCulture)} bytes did.");
        }

        var arrived = FileContentHash.Of(contents);

        // Checked against the hash the far side took of what it read, not against what arrived here.
        // A file that lost bytes on the way is the failure this catches, and it can only be caught by
        // a number that was computed before the journey.
        return string.Equals(arrived, answer.ContentHash, StringComparison.Ordinal)
            ? contents
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"'{relativePath}' changed between {Host} and here: it was read as {answer.ContentHash} "
                + $"and arrived as {arrived}.");
    }

    /// <inheritdoc/>
    public async Task<SyncDirectoryListing> ListFilesAsync(string root, string relativeDirectory, CancellationToken cancellationToken = default)
        => await AskAsync<SyncDirectoryListing>(SyncServe.List, root, [relativeDirectory], cancellationToken).ConfigureAwait(false)
            ?? throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} did not answer with what '{relativeDirectory}' holds.");

    /// <summary>How many bytes <paramref name="answer"/> says its file holds, refused where no file it can carry holds that many.</summary>
    /// <exception cref="HarnessException">The length is below nothing or past <see cref="SyncServe.LargestFile"/>.</exception>
    private int Told(SyncFileAnswer answer, string relativePath)
        => answer.Length is >= 0 and <= SyncServe.LargestFile
            ? (int)answer.Length
            : throw new HarnessException(
                HarnessExit.CommandFailed,
                $"{Host} said '{relativePath}' holds {answer.Length.ToString(CultureInfo.InvariantCulture)} bytes, which no file it can send does.");

    /// <inheritdoc/>
    public async Task<SyncInspectAnswer> InspectAsync(string root, CancellationToken cancellationToken = default)
        => await AskAsync<SyncInspectAnswer>(SyncServe.Inspect, root, [], cancellationToken).ConfigureAwait(false)
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
            .ConfigureAwait(false);

        if (lines.Finished is not { } exitCode)
        {
            throw new HarnessException(
                HarnessExit.HostUnavailable,
                $"{Host}: {HostProbes.NeverFinished($"'{operation}'", result, _session.Connection)}");
        }

        if (exitCode != HarnessExit.Success)
        {
            // From the agent's own output on, and under the name the configuration declares, as every other
            // reason built here is: the whole capture holds whatever the host's login shell printed first,
            // and ssh names the address it dialled, one the host's name resolved to.
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
    /// inside it; its parent is where the operation is served from instead.
    /// </summary>
    private static string ParentOf(string root)
    {
        var trimmed = root.Replace('\\', '/').TrimEnd('/');
        var slash = trimmed.LastIndexOf('/');

        return slash <= 0 ? trimmed : trimmed[..slash];
    }
}
