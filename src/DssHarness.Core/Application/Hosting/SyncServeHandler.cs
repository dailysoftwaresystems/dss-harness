using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Output;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;

namespace DssHarness.Core.Application.Hosting;

/// <summary>
/// One served sync operation's answer, as the machine asking reads it on the wire: the line that says what
/// happened - and, where the operation carries bytes back, the lines that follow it, a file's content a line at a
/// time - or no line at all, where the operation answers nothing and must still say nothing rather than a status
/// line the machine did not ask for.
/// </summary>
public sealed record SyncServeAnswer(string? Line, IReadOnlyList<string> Following)
{
    /// <summary>The answer of an operation that answers nothing.</summary>
    public static SyncServeAnswer None { get; } = new(null, []);
}

/// <summary>
/// Serves one sync operation on this host's copy of the repository: the body of the hidden
/// <c>sync-serve</c> command, a use case now, so a front end resolves it as any other and the command only wires
/// its arguments.
/// </summary>
/// <remarks>
/// Nobody types it: it is the far side of a sync, and every operation it performs is one the machine asking
/// already decided on. It serves them through the same <see cref="LocalSyncTransport"/> this machine uses for a
/// copy of its own, so there is one implementation of what a sync does to a tree and no second one to drift from
/// it.
/// </remarks>
public sealed class SyncServeHandler(ISyncTransport transport, IAgentIsolation isolation) : IUseCase
{
    private readonly ISyncTransport _transport = transport;
    private readonly IAgentIsolation _isolation = isolation;

    /// <summary>Serves <paramref name="operation"/> on the copy its first argument names.</summary>
    /// <param name="operation">The operation to perform on this host's copy.</param>
    /// <param name="arguments">
    /// The operation's arguments, after the copy's root: the copy's root, then whatever the operation takes, each
    /// value an argument of its own.
    /// </param>
    /// <param name="cancellationToken">Stops the operation.</param>
    /// <returns>What the machine asking reads: an answer line and the lines that follow it, or none at all.</returns>
    public async Task<SyncServeAnswer> ServeAsync(
        string operation,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        ArgumentNullException.ThrowIfNull(arguments);

        if (arguments.Count == 0)
        {
            throw new HarnessException(
                HarnessExit.UsageError,
                $"sync operation '{operation}' was given no copy to act on. The two ends are different builds.");
        }

        var root = arguments[0];

        switch (operation)
        {
            case SyncServe.Inspect:
                return Answer(await _transport
                    .InspectAsync(root, SyncServe.WantedIn(arguments, 1), cancellationToken)
                    .ConfigureAwait(false));

            case SyncServe.Create:
                await (SyncServe.ReadingIn(arguments) is { } reading
                        ? _transport.MarkFinishedAsync(root, reading, cancellationToken)
                        : _transport.CreateRootAsync(root, SyncServe.MarkIn(arguments), cancellationToken))
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.InitRepository:
                await _transport
                    .InitialiseRepositoryAsync(root, SyncServe.ObjectFormatIn(arguments), cancellationToken)
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.HistoryPiece:
                await _transport
                    .SendHistoryAsync(
                        root,
                        Required(arguments, 1, operation),
                        SyncServe.OffsetIn(Required(arguments, 2, operation)),
                        new SyncFileWrite(Required(arguments, 1, operation), Required(arguments, 3, operation)).Bytes(),
                        cancellationToken)
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.TakeHistory:
                return Answer(await _transport
                    .TakeHistoryAsync(
                        root,
                        new GitHistoryTaken(
                            SyncServe.WantedIn(arguments, 3)
                                ?? throw new HarnessException(
                                    HarnessExit.UsageError,
                                    $"sync operation '{operation}' was given no commit to take. The two ends are different builds."),
                            SyncServe.Given(arguments, 1, operation),
                            SyncServe.Given(arguments, 2, operation)),
                        cancellationToken)
                    .ConfigureAwait(false));

            case SyncServe.Index:
                await _transport
                    .IndexAsync(root, SyncServe.CarriedPaths(arguments), cancellationToken)
                    .ConfigureAwait(false);
                return SyncServeAnswer.None;

            case SyncServe.RemoveCopy:
                return Answer(new SyncRemoveAnswer(
                    await _transport.RemoveCopyAsync(root, cancellationToken).ConfigureAwait(false)));

            case SyncServe.ListCopies:
                return Answer(new SyncCopiesAnswer(
                    await _transport.ListCopiesAsync(root, cancellationToken).ConfigureAwait(false)));

            case SyncServe.RemoveWorkers:
                return Answer(new SyncWorkersAnswer(
                    await _transport.RemoveWorkersAsync(root, SyncServe.MeasuresOnly(operation, arguments), cancellationToken).ConfigureAwait(false)));

            case SyncServe.RemoveFormerWorkers:
                return Answer(new SyncWorkersAnswer(
                    await _transport
                        .RemoveFormerWorkersBesideCopiesAsync(root, SyncServe.MeasuresOnly(operation, arguments), cancellationToken)
                        .ConfigureAwait(false)));

            case SyncServe.Manifest:
                var manifest = await _transport
                    .ReadManifestAsync(root, SyncServe.Named(arguments), cancellationToken)
                    .ConfigureAwait(false);

                return Answer(new SyncManifestAnswer([.. manifest.Paths.Select(path => manifest.Entries[path])])
                {
                    Links = manifest.Links,
                });

            case SyncServe.Write:
                var file = new SyncFileWrite(Required(arguments, 1, operation), Required(arguments, 2, operation));

                await _transport
                    .WriteFileAsync(root, file.Path, file.Bytes(), cancellationToken)
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.WriteMany:
                await _transport
                    .WriteFilesAsync(
                        root,
                        [.. SyncServe.CarriedFiles(arguments).Select(carried => new SyncFileContent(carried.Path, carried.Bytes()))],
                        cancellationToken)
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.Delete:
                await _transport
                    .DeleteFileAsync(root, Required(arguments, 1, operation), cancellationToken)
                    .ConfigureAwait(false);

                return SyncServeAnswer.None;

            case SyncServe.Prune:
                return Answer(new SyncPruneAnswer(
                    await _transport
                        .RemoveEmptyDirectoriesAsync(root, SyncServe.Named(arguments), cancellationToken)
                        .ConfigureAwait(false)));

            case SyncServe.List:
                return Answer(await _transport
                    .ListFilesAsync(root, Required(arguments, 1, operation), HiddenHere(root), cancellationToken)
                    .ConfigureAwait(false));

            case SyncServe.Read:
            case SyncServe.Pull:
                var contents = await (operation == SyncServe.Pull
                        ? _transport.PullFileAsync(root, Required(arguments, 1, operation), HiddenHere(root), cancellationToken)
                        : _transport.ReadFileAsync(root, Required(arguments, 1, operation), cancellationToken))
                    .ConfigureAwait(false);

                // Refused here rather than where it is decoded, because this is the side
                // that knows the size and is about to spend the memory.
                SyncServe.RefuseAFileTooLargeToCarry(
                    contents.LongLength,
                    Required(arguments, 1, operation),
                    "this host");

                // Hashed here, where the bytes were read. A hash the asking machine took of what
                // arrived would agree with those bytes whatever happened on the way.
                return Answer(
                    new SyncFileAnswer(contents.LongLength, FileContentHash.Of(contents)),
                    SyncServe.ContentLines(contents));

            default:
                // Named rather than passed over: an operation this build does not know means the
                // two ends are different builds, which the version check should already have
                // refused, so reporting it as "nothing happened" would hide a real defect.
                throw new HarnessException(
                    HarnessExit.UsageError,
                    $"'{operation}' is not a sync operation this build serves.");
        }
    }

    /// <summary>
    /// An answer the machine syncing reads, written to standard output on its own line, and the lines that follow it - a
    /// file's content, a piece to a line - where it has any.
    /// </summary>
    private static SyncServeAnswer Answer<T>(T answer, IEnumerable<string>? following = null)
        => new(SyncServe.Answer(answer), [.. following ?? []]);

    /// <summary>
    /// The places the isolation hides on this host, which no pull out of the copy at <paramref name="root"/> reaches: its
    /// user's Claude Code directories and the file Claude Code keeps beside one, and any credential slot the copy holds
    /// (<see cref="IAgentIsolation.Hidden"/>). No provider of the machine asking names a directory here, so none is read
    /// from its configuration.
    /// </summary>
    private IReadOnlyList<IsolatedPath> HiddenHere(string root)
        => _isolation.Hidden(new HarnessConfig(), root);

    private static string Required(IReadOnlyList<string> arguments, int index, string operation)
        => index < arguments.Count
            ? arguments[index]
            : throw new HarnessException(
                HarnessExit.UsageError,
                $"sync operation '{operation}' needs {ReportText.Counted(index + 1, "argument")}; it was given {arguments.Count}.");
}
