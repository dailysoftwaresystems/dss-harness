using DssHarness.Core.FileSystem;

namespace DssHarness.Core.Web.Index;

/// <summary>
/// The indexes of the sites the harness searches, for the process: each searched from what it holds, and filled in the
/// background when it has none, or one older than its source allows, or one a fill left unfinished - one fill of an index
/// at a time on the machine, and none while its site asked to be left alone, or soon after a fill stopped short.
/// </summary>
public sealed class WebIndexes : IDisposable
{
    private readonly IFileSystem _fileSystem;
    private readonly SitemapFill _fill;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WebIndex> _indexes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Task> _fills = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    /// <summary>The indexes kept on <paramref name="fileSystem"/>, filled by <paramref name="fill"/>.</summary>
    /// <param name="fileSystem">Where they are kept.</param>
    /// <param name="fill">What fills one.</param>
    /// <param name="clock">The clock: whether an index is due a fill.</param>
    public WebIndexes(IFileSystem fileSystem, SitemapFill fill, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(fill);
        ArgumentNullException.ThrowIfNull(clock);

        _fileSystem = fileSystem;
        _fill = fill;
        _clock = clock;
    }

    /// <summary>
    /// Searches the index of <paramref name="settings"/>' site in <paramref name="directory"/> for <paramref name="query"/> -
    /// its pages whose paths <paramref name="settings"/> read, so a source's patterns narrowed answer at once - beginning
    /// its fill in the background where one is due, each address the fill asks decided, recorded and paced by
    /// <paramref name="runGate"/>; the answer says how much of the site it held, and whether it is being filled.
    /// </summary>
    /// <param name="directory">The index's directory.</param>
    /// <param name="settings">What it reads.</param>
    /// <param name="query">The query.</param>
    /// <param name="limit">The most hits.</param>
    /// <param name="runGate">
    /// The run's gate, never one step's: a fill begun now asks under it, in the turns it gives, until it ends - long after
    /// this search - and a later search's gate is not asked.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="runGate"/> gives no turn: a fill is never unpaced.</exception>
    public WebIndexAnswer Search(string directory, SitemapSettings settings, string query, int limit, WebFetchGate runGate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(settings);
        SitemapFill.Paced(runGate);

        WebIndex index;

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_indexes.TryGetValue(directory, out var kept) || kept.Site != settings.Site)
            {
                kept = new WebIndex(_fileSystem, directory, settings.Site);
                _indexes[directory] = kept;
            }

            index = kept;
        }

        index.Refresh();

        var filling = Running(directory) || (WebIndexState.Read(_fileSystem, directory, settings.Site).Due(_clock.GetUtcNow(), settings) && Begin(directory, index, settings, runGate));

        // Read again for the answer: a fill that ended since the first read left a state of its own.
        var state = WebIndexState.Read(_fileSystem, directory, settings.Site);

        // A count of other settings is no count of these: none is said until a listing is read under them.
        var known = string.Equals(state.KnownUnder, settings.Fingerprint, StringComparison.Ordinal) ? state.Known : null;

        return new WebIndexAnswer(settings.Site, index.Search(query, limit, settings.ReadsPage), index.HeldOf(settings.ReadsPage), known, filling, state.Problem, state.PausedUntil)
        {
            PausedFor = state.PausedFor,
        };
    }

    /// <summary>The fill of the index in <paramref name="directory"/> this process runs, until it ends; one ended where none runs.</summary>
    /// <param name="directory">The index's directory.</param>
    public Task FilledAsync(string directory)
    {
        lock (_gate)
        {
            return _fills.TryGetValue(directory, out var fill) ? fill : Task.CompletedTask;
        }
    }

    /// <summary>Stops every fill this process runs, and waits for each to end at its next wait or request; what each read is kept. Once.</summary>
    public void Dispose()
    {
        Task[] running;

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            running = [.. _fills.Values];
        }

        _stopping.Cancel();

        // Waited for, not observed: a fill catches what ends it, and one whose last word could not be written has no one
        // to tell here either.
        Task.WhenAll(running).ContinueWith(static _ => { }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).Wait();
        _stopping.Dispose();
    }

    private bool Running(string directory)
    {
        lock (_gate)
        {
            return _fills.TryGetValue(directory, out var fill) && !fill.IsCompleted;
        }
    }

    /// <summary>Begins the fill of <paramref name="index"/> in the background; true where it is being filled now - by this fill, or by another holding it.</summary>
    private bool Begin(string directory, WebIndex index, SitemapSettings settings, WebFetchGate gate)
    {
        lock (_gate)
        {
            // Stopped since the search began: no fill begins after the last was waited for.
            if (_disposed)
            {
                return false;
            }

            if (_fills.TryGetValue(directory, out var running) && !running.IsCompleted)
            {
                return true;
            }

            if (_fill.Claim(directory) is not { } claim)
            {
                // Another run fills it.
                return true;
            }

            // Due as read before it was claimed, and perhaps no longer: a fill that ended between them, here or in another
            // process, left a state - a hold-off, a pause - that is read again now that none other can write it.
            if (!WebIndexState.Read(_fileSystem, directory, settings.Site).Due(_clock.GetUtcNow(), settings))
            {
                claim.Dispose();
                return false;
            }

            _fills[directory] = Task.Run(() => FillAsync(claim, index, settings, gate));
            return true;
        }
    }

    private async Task FillAsync(IDisposable claim, WebIndex index, SitemapSettings settings, WebFetchGate gate)
    {
        try
        {
            await _fill.RunAsync(claim, index, settings, gate, _stopping.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // Stopped with the process: what it read is kept, and the next run's search begins it again.
        }
        catch (Exception ex)
        {
            // Nothing awaits a fill in the background: what ended it is said where every search of the index reads it.
            var state = WebIndexState.Read(_fileSystem, index.Directory, settings.Site);

            state.Stopped($"its fill failed: {ex.Message}", _clock.GetUtcNow() + WebIndexState.AfterProblem, settings.All).Write(_fileSystem, index.Directory);
        }
    }
}
