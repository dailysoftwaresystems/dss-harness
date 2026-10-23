using System.Collections.Concurrent;
using System.Globalization;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Hosts;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Core.Codebase;

/// <summary>Syncs a run's one codebase graph with its tree.</summary>
public interface ICodebaseGraphService
{
    /// <summary>
    /// Brings the run's graph of the main checkout up to date: lists the tree, reads only the files whose blob, claimant,
    /// claimant's version, or what <c>maxFileBytes</c> or <c>languages</c> decide of them changed since the graph was last
    /// synced - or since the newest earlier run's graph, where the run has none - those whose extract is gone, and those it
    /// could not read, links it again, and writes its records. Two syncs of one graph never run at once.
    /// </summary>
    /// <param name="request">What to sync, how the workflow shapes it, and why.</param>
    /// <param name="cancellationToken">Stops the sync; what it wrote is the last whole manifest and the extracts before it.</param>
    /// <exception cref="HarnessException">
    /// git did not answer (<see cref="HarnessExit.ToolMissing"/>); a record could not be written, by its cause.
    /// </exception>
    Task<CodebaseGraphSynced> SyncAsync(CodebaseGraphSync request, CancellationToken cancellationToken = default);
}

/// <summary>One sync asked of a run's graph.</summary>
/// <param name="Repository">The repository whose main checkout is the graph's tree, and whose other runs a first sync seeds from.</param>
/// <param name="Run">The run whose graph it is.</param>
/// <param name="Shape">What the workflow's <c>codebaseGraph</c> says.</param>
/// <param name="WorktreesRoot">The configured worktrees root, which the tool's state paths name.</param>
/// <param name="Rebuild">Whether every file is extracted again, no extract reused.</param>
/// <param name="Why">What asked for it: a step, by its instance.</param>
public sealed record CodebaseGraphSync(HarnessLayout Repository, OrchestratorLayout Run, CodebaseGraphShape Shape, string WorktreesRoot, bool Rebuild, string Why);

/// <summary>What one sync of a graph did and left.</summary>
/// <param name="Graph">The graph's directory.</param>
/// <param name="View">The view synced: <c>base</c>.</param>
/// <param name="Commit">The commit the tree's HEAD named; <see langword="null"/> where it named none yet.</param>
/// <param name="Dirty">Whether the tree held anything its HEAD does not.</param>
/// <param name="Files">How many files the graph keeps.</param>
/// <param name="Listed">How many files the tree listed that the graph keeps, every one looked at.</param>
/// <param name="Hashed">How many files git was asked the blob of.</param>
/// <param name="Extracted">How many files an implementation read.</param>
/// <param name="Reused">How many files kept an extract already made, or a state already found.</param>
/// <param name="Removed">How many paths left the graph.</param>
/// <param name="Nodes">How many nodes the graph holds.</param>
/// <param name="Edges">How many edges.</param>
/// <param name="Languages">For each implementation that read a file, how many it read and the nodes they hold.</param>
/// <param name="Skipped">Every file the graph lists and holds no declaration of, with its state and why.</param>
/// <param name="Seconds">How long the sync took.</param>
public sealed record CodebaseGraphSynced(
    string Graph,
    string View,
    string? Commit,
    bool Dirty,
    int Files,
    int Listed,
    int Hashed,
    int Extracted,
    int Reused,
    int Removed,
    int Nodes,
    int Edges,
    IReadOnlyList<CodebaseGraphLanguage> Languages,
    IReadOnlyList<CodebaseGraphSkipped> Skipped,
    double Seconds)
{
    /// <summary>Why the graph was built again whole, where it was.</summary>
    public string? Rebuilt { get; init; }

    /// <summary>The run whose graph the sync started from, where the run had none.</summary>
    public string? SeededFrom { get; init; }

    /// <summary>The files the graph could not read - undecodable, or gone between the listing and the read: what makes it incomplete.</summary>
    public IReadOnlyList<CodebaseGraphSkipped> Unreadable => [.. Skipped.Where(file => file.State == CodeFileStates.Unreadable)];
}

/// <summary>One implementation's part of a graph.</summary>
/// <param name="Language">Its name.</param>
/// <param name="Files">How many files it read.</param>
/// <param name="Nodes">How many nodes they hold, their files' own among them.</param>
public sealed record CodebaseGraphLanguage(string Language, int Files, int Nodes);

/// <summary>A file a graph lists and holds no declaration of.</summary>
/// <param name="Path">Its path, relative to the tree.</param>
/// <param name="State">Its state.</param>
/// <param name="Why">Why.</param>
public sealed record CodebaseGraphSkipped(string Path, string State, string? Why);

/// <summary>A sync of a run's codebase graph ended: what it listed, read, reused and removed.</summary>
/// <param name="Graph">The graph's directory.</param>
/// <param name="View">The view synced.</param>
/// <param name="Why">What asked for it.</param>
/// <param name="Listed">How many files the tree listed that the graph keeps.</param>
/// <param name="Extracted">How many files an implementation read.</param>
/// <param name="Reused">How many kept what was found before.</param>
/// <param name="Removed">How many paths left the graph.</param>
/// <param name="Seconds">How long it took.</param>
public sealed record GraphSynced(string Graph, string View, string Why, int Listed, int Extracted, int Reused, int Removed, double Seconds) : RunEvent;

/// <inheritdoc cref="ICodebaseGraphService"/>
/// <param name="gitClient">Lists the tree.</param>
/// <param name="fileSystem">Reads its files, and the graph's records.</param>
/// <param name="store">Reads and writes the graph's records, strictly.</param>
/// <param name="extractors">Every implementation of the contract.</param>
/// <param name="platform">How this machine compares paths.</param>
/// <param name="identity">The tool's version, which the manifest names.</param>
/// <param name="output">Hears each sync.</param>
/// <param name="time">The clock.</param>
public sealed class CodebaseGraphService(
    IGitClient gitClient,
    IFileSystem fileSystem,
    OrchestrationStore store,
    CodeGraphExtractors extractors,
    IHostPlatform platform,
    IToolIdentityProvider identity,
    IHarnessOutput output,
    TimeProvider time) : ICodebaseGraphService
{
    /// <summary>The view a sync of the main checkout is.</summary>
    public const string BaseView = "base";

    /// <summary>
    /// The share of the machine's processors a sync reads files on: one of every this many, so a build beside it is not
    /// starved - and at least one.
    /// </summary>
    public const int ReadShare = 2;

    /// <summary>One lock for each graph, by its directory: two syncs of one graph wait for each other.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public async Task<CodebaseGraphSynced> SyncAsync(CodebaseGraphSync request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var gate = _locks.GetOrAdd(request.Run.GraphDirectory, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            return await new Sync(this, request, cancellationToken).RunAsync().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>One sync, holding what it read so no fact is read twice.</summary>
    private sealed class Sync(CodebaseGraphService service, CodebaseGraphSync request, CancellationToken cancellationToken)
    {
        private readonly long _started = service._time.GetTimestamp();
        private readonly string _tree = request.Repository.MainCheckoutRoot;
        private readonly OrchestratorLayout _run = request.Run;
        private readonly CodebaseGraphShape _shape = request.Shape;
        private readonly List<CodeGraphExtractorVersion> _versions = [.. service._extractors.All.Select(extractor => new CodeGraphExtractorVersion(extractor.Language, extractor.Version))];
        /// <summary>Each file the sync could not name or found gone before it was hashed, once, by the name it is said by.</summary>
        private readonly SortedDictionary<string, CodebaseGraphSkipped> _unnamed = new(StringComparer.Ordinal);

        /// <summary>The paths whose entry was kept, unread: one made again while linking is then counted extracted alone.</summary>
        private readonly HashSet<string> _reusedPaths = new(StringComparer.Ordinal);

        /// <summary>The credential slots and the tool's own state under the run's worktrees root: what the graph never lists.</summary>
        private readonly IReadOnlyList<string> _never = [.. HarnessLayout.SecretSlots, .. HarnessLayout.StatePaths(request.WorktreesRoot)];
        private int _extracted;
        private int _reused;
        private int _hashed;

        public async Task<CodebaseGraphSynced> RunAsync()
        {
            var (previous, from, rebuilt, seededFrom) = Previous();
            var (listed, head, dirty) = await ListAsync().ConfigureAwait(false);
            var sameReading = previous is not null
                && previous.Extractors.SequenceEqual(_versions)
                && (previous.Languages ?? []).SequenceEqual(_shape.Languages ?? [], StringComparer.Ordinal)
                && (previous.Languages is null) == (_shape.Languages is null);
            var entries = new SortedDictionary<string, CodeGraphFileEntry>(StringComparer.Ordinal);
            var toRead = new List<(string Path, string Blob)>();

            foreach (var (path, file) in listed)
            {
                var before = request.Rebuild ? null : previous?.Files.GetValueOrDefault(path);

                if (Settled(path, file, before, sameReading, from) is { } kept)
                {
                    entries[path] = kept;

                    // A link or a submodule is listed without a read; it is kept only where the graph had it, of that blob.
                    if (before is not null && before.Blob == kept.Blob)
                    {
                        _reusedPaths.Add(path);
                        _reused++;
                    }
                }
                else
                {
                    toRead.Add((path, file.Blob!));
                }
            }

            foreach (var (path, entry) in await ReadAsync(toRead).ConfigureAwait(false))
            {
                entries[path] = entry;
            }

            var files = new Dictionary<string, CodeGraphFileEntry>(entries, StringComparer.Ordinal);
            var digest = CodeGraphManifest.DigestOf(_versions, files);
            var symbols = previous is not null && from is null && !request.Rebuild && previous.Digest == digest ? Symbols(digest) : null;
            var unchanged = symbols is not null;

            if (symbols is null)
            {
                symbols = await LinkAsync(entries).ConfigureAwait(false);

                // An extract that did not read was made again while linking: the manifest names what was made.
                files = new Dictionary<string, CodeGraphFileEntry>(entries, StringComparer.Ordinal);
                digest = CodeGraphManifest.DigestOf(_versions, files);
                symbols = symbols with { Graph = digest };
            }

            var manifest = new CodeGraphManifest(
                CodeGraphManifest.CurrentSchema,
                service._identity.Current.Version,
                _versions,
                _tree,
                head,
                dirty,
                service._time.GetUtcNow(),
                files,
                digest)
            {
                Languages = _shape.Languages,
            };

            service._store.WriteGraphManifest(_run, manifest);

            // A link result made from the manifest as it stands is kept as it is; one made again is written after it. What the
            // manifest names no more - or a sync that stopped part way wrote and never named - goes at every sync.
            if (!unchanged)
            {
                service._store.WriteGraphSymbols(_run, symbols);
            }

            RemoveUnnamed(manifest);

            // A first sync seeded from another run's graph removed nothing from this run's: it had none.
            var removed = from is null ? previous?.Files.Keys.Count(path => !files.ContainsKey(path)) ?? 0 : 0;
            var seconds = Math.Round(service._time.GetElapsedTime(_started).TotalSeconds, 3);

            service._store.AppendGraphSync(_run, new CodeGraphSyncLine(service._time.GetUtcNow(), BaseView, request.Why, files.Count, _hashed, _extracted, _reused, removed, seconds)
            {
                Rebuilt = rebuilt,
                SeededFrom = seededFrom,
            });
            service._output.Publish(new GraphSynced(_run.GraphDirectory, BaseView, request.Why, files.Count, _extracted, _reused, removed, seconds));

            return new CodebaseGraphSynced(
                _run.GraphDirectory,
                BaseView,
                head,
                dirty,
                files.Count,
                files.Count,
                _hashed,
                _extracted,
                _reused,
                removed,
                symbols.Nodes.Count,
                symbols.Edges.Count,
                Languages(files, symbols),
                [.. _unnamed.Values.Concat(files.Where(file => !Declares(file.Value)).Select(file => new CodebaseGraphSkipped(file.Key, file.Value.State, file.Value.Why)))],
                seconds)
            {
                Rebuilt = rebuilt,
                SeededFrom = seededFrom,
            };
        }

        /// <summary>
        /// What the sync starts from: the run's own manifest; where it has none, the newest earlier run's that reads, whose
        /// extracts are copied as they are reused; and why the graph is built again whole, where it is.
        /// </summary>
        private (CodeGraphManifest? Manifest, OrchestratorLayout? From, string? Rebuilt, string? SeededFrom) Previous()
        {
            CodeGraphManifest? own;

            try
            {
                own = service._store.ReadGraphManifest(_run);
            }
            catch (HarnessException ex) when (ex.ExitCode == HarnessExit.Refused)
            {
                return (null, null, request.Rebuild ? Rebuilding : $"its manifest did not read, and a graph is never guessed at: {ex.Message}", null);
            }

            // Asked to rebuild, the graph keeps nothing it read, and counts what left it all the same.
            if (request.Rebuild || own is not null)
            {
                return (own, null, request.Rebuild ? Rebuilding : null, null);
            }

            var seed = service._store.Orchestrators(request.Repository)
                .Where(other => !string.Equals(other.Name, _run.Name, StringComparison.Ordinal))
                .Select(other => (Run: other, Manifest: ReadOrNull(other)))
                .Where(other => other.Manifest is not null)
                .OrderByDescending(other => other.Manifest!.SyncedAt)
                .ThenBy(other => other.Run.Name, StringComparer.Ordinal)
                .FirstOrDefault();

            return seed.Manifest is { } seeded ? (seeded, seed.Run, null, seed.Run.Name) : (null, null, null, null);
        }

        /// <summary>Why a graph a step said to rebuild was built again whole.</summary>
        private const string Rebuilding = "the step said rebuild: every file is extracted again";

        /// <summary>Another run's manifest, where it has one that reads with this build's schema; null where it has none or one that does not.</summary>
        private CodeGraphManifest? ReadOrNull(OrchestratorLayout other)
        {
            try
            {
                return service._store.ReadGraphManifest(other);
            }
            catch (HarnessException)
            {
                return null;
            }
        }

        /// <summary>
        /// Every file of the tree the graph keeps, with its blob and what git records it as: the index's paths and objects,
        /// less what is gone, the changed and untracked ones with the blobs git gives their working files - less what no
        /// implementation is ever given, and what the workflow's patterns leave out. A name that is not UTF-8 names no file
        /// this tool opens, and is said as unreadable. A path git keeps out of the checkout - a sparse checkout's, or one
        /// marked skip-worktree - is in the graph where its file is there, read as it stands, and nowhere else; a repository
        /// inside the tree that git lists whole as untracked is not entered, as a submodule is not.
        /// </summary>
        private async Task<(SortedDictionary<string, Listed> Files, string? Head, bool Dirty)> ListAsync()
        {
            IReadOnlyList<GitListedFile> indexed;
            IReadOnlyList<GitWorkingFile> working;
            string? head;

            try
            {
                indexed = await service._gitClient.ListIndexedFilesAsync(_tree, cancellationToken).ConfigureAwait(false);
                working = await service._gitClient.ListWorkingFilesAsync(_tree, cancellationToken).ConfigureAwait(false);
                head = await service._gitClient.ResolveCommitAsync(_tree, "HEAD", cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode is not HarnessExit.ToolMissing)
            {
                throw NotAnswered(ex);
            }

            var files = new SortedDictionary<string, Listed>(StringComparer.Ordinal);
            var toHash = new List<string>();

            foreach (var file in indexed)
            {
                if (!Kept(file.Name))
                {
                    continue;
                }

                // git does not look at a skip-worktree path's file, so its index object says nothing of what is there.
                if (file.SkipWorktree && !file.IsSubmodule)
                {
                    if (service._fileSystem.FileExists(Absolute(file.Name.Text)))
                    {
                        files[file.Name.Text] = new Listed(null, Link: false, Submodule: false);
                        toHash.Add(file.Name.Text);
                    }

                    continue;
                }

                files[file.Name.Text] = new Listed(file.ObjectId, file.IsLink, file.IsSubmodule);
            }

            var present = working.Where(file => !file.Gone).Select(file => file.Name.Text).ToHashSet(StringComparer.Ordinal);

            foreach (var gone in working.Where(file => file.Gone && !present.Contains(file.Name.Text)))
            {
                files.Remove(gone.Name.Text);
            }

            // An untracked repository inside the tree is listed whole, by its directory's name.
            foreach (var file in working.Where(file => !file.Gone && file.Differs && !file.Name.Text.EndsWith('/') && Kept(file.Name)))
            {
                var path = file.Name.Text;
                var known = files.GetValueOrDefault(path);

                if (known is { Submodule: true } or { Blob: null, Link: false })
                {
                    continue;
                }

                if (file.Mode == "120000" || (file.Untracked && IsLink(path)))
                {
                    files[path] = new Listed(known?.Blob, Link: true, Submodule: false);
                    continue;
                }

                files[path] = new Listed(null, Link: false, Submodule: false);
                toHash.Add(path);
            }

            // A file gone since git listed it is not given to git to hash: it is listed unreadable, as one gone before its read is.
            var there = toHash.Where(path => service._fileSystem.FileExists(Absolute(path))).ToList();

            foreach (var gone in toHash.Except(there, StringComparer.Ordinal))
            {
                files.Remove(gone);
                _unnamed.TryAdd(gone, new CodebaseGraphSkipped(gone, CodeFileStates.Unreadable, "it was gone when it was read"));
            }

            IReadOnlyList<string> blobs;

            try
            {
                blobs = await service._gitClient.HashWorkingFilesAsync(_tree, there, cancellationToken).ConfigureAwait(false);
            }
            catch (HarnessException ex) when (ex.ExitCode is not HarnessExit.ToolMissing)
            {
                throw NotAnswered(ex);
            }

            _hashed = there.Count;

            for (var at = 0; at < there.Count; at++)
            {
                files[there[at]] = files[there[at]] with { Blob = blobs[at] };
            }

            // The tool's own state and the credential slots are no change of the tree, though git lists them where they are not ignored.
            return (files, head, working.Any(file => !Never(file.Name.Text)));
        }

        /// <summary>
        /// Whether the graph keeps the file git names <paramref name="name"/>: one this tool can open, never one of the
        /// credential slots or the tool's own state - what the graph never extracts it never serves - and one the workflow's
        /// patterns graph.
        /// </summary>
        private bool Kept(GitName name)
        {
            if (!name.IsUtf8)
            {
                // Listed by the index and by status alike where it changed: said once.
                if (!Never(name.Text) && Graphs(name.Text))
                {
                    _unnamed.TryAdd(name.Quoted, new CodebaseGraphSkipped(name.Quoted, CodeFileStates.Unreadable, "its name is not UTF-8, and this tool opens a file only by a UTF-8 name"));
                }

                return false;
            }

            return !Never(name.Text) && Graphs(name.Text);
        }

        /// <summary>Whether the workflow's patterns graph <paramref name="path"/>, as this machine's file system compares names.</summary>
        private bool Graphs(string path) => _shape.Graphs(path, service._platform.PathComparison);

        /// <summary>Whether <paramref name="path"/> is in a credential slot or the tool's own state.</summary>
        private bool Never(string path)
            => PathPatterns.Matches(_never, path, service._platform.PathComparison);

        private bool IsLink(string path)
        {
            try
            {
                return service._fileSystem.IsLink(Absolute(path));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// The entry of a file whose blob has not changed, kept as it was - its extract copied from the run it was seeded from
        /// - where nothing else that decides it changed: the implementation that claims it and its version, the largest
        /// file read; null where the file is to be read again.
        /// </summary>
        private CodeGraphFileEntry? Settled(string path, Listed file, CodeGraphFileEntry? before, bool sameReading, OrchestratorLayout? from)
        {
            if (file.Submodule)
            {
                return new CodeGraphFileEntry(file.Blob, CodeFileStates.Submodule, 0) { Why = "a submodule, which the graph does not enter" };
            }

            if (file.Link)
            {
                return new CodeGraphFileEntry(file.Blob, CodeFileStates.Link, 0) { Why = "a link git records as one, which the graph does not follow" };
            }

            if (before is null || before.Blob != file.Blob)
            {
                return null;
            }

            // A limit lowered under a file read, or found binary or ambiguous, lists it too large: a fresh graph would.
            if (before.State != CodeFileStates.TooLarge && before.Bytes > _shape.MaxFileBytes)
            {
                return null;
            }

            switch (before.State)
            {
                case CodeFileStates.Binary:
                    return before;

                case CodeFileStates.TooLarge:
                    return before.Bytes > _shape.MaxFileBytes ? before : null;

                case CodeFileStates.Ambiguous:
                    return sameReading ? before : null;

                case CodeFileStates.Parsed or CodeFileStates.Outlined or CodeFileStates.Listed or CodeFileStates.Generated:
                    // The same blob read by the same implementations under the same languages goes to the one it went to;
                    // otherwise its claimant is found again, where that needs no read of it.
                    var claimant = sameReading ? service._extractors.Find(before.Language!) : Claimant(path);

                    if (claimant is null || claimant.Language != before.Language)
                    {
                        return null;
                    }

                    var name = CodeGraphIds.ExtractName(file.Blob!, path, claimant.Language, claimant.Version);

                    if (name != before.Extract || !Present(name, from))
                    {
                        return null;
                    }

                    // A file read before keeps its generated mark in its why; whether it is left out is the shape's now.
                    return before with { State = StateOf(claimant, generated: before.Why is not null) };

                default:
                    return null;
            }
        }

        /// <summary>
        /// The implementation that reads <paramref name="path"/> where that is known without reading it - the fallback, or
        /// the one claimant of its exact name or extension that asks no content test - and used; null where its content
        /// must be read to know.
        /// </summary>
        private ICodeGraphExtractor? Claimant(string path)
        {
            var claimants = service._extractors.ClaimantsOf(path);

            var claimant = claimants.Count switch
            {
                0 => service._extractors.Fallback,
                1 when claimants[0].Claim.ContentTest is null => claimants[0].Extractor,
                _ => null,
            };

            return claimant is null ? null : _shape.Uses(claimant.Language) ? claimant : service._extractors.Fallback;
        }

        /// <summary>Whether the extract <paramref name="name"/> is in this run's graph - copied there from the run it was seeded from, where it is there.</summary>
        private bool Present(string name, OrchestratorLayout? from)
        {
            var own = _run.GraphExtractFile(name);

            if (service._fileSystem.FileExists(own))
            {
                return true;
            }

            if (from is null || !service._fileSystem.FileExists(from.GraphExtractFile(name)))
            {
                return false;
            }

            service._fileSystem.CreateDirectory(Path.GetDirectoryName(own)!);
            service._fileSystem.CopyFile(from.GraphExtractFile(name), own, overwrite: true);
            return true;
        }

        /// <summary>Reads each of <paramref name="files"/> and gives it to its implementation, in parallel on one of every <see cref="ReadShare"/> of the machine's processors.</summary>
        private async Task<IReadOnlyList<(string Path, CodeGraphFileEntry Entry)>> ReadAsync(IReadOnlyList<(string Path, string Blob)> files)
        {
            var read = new ConcurrentBag<(string, CodeGraphFileEntry)>();
            var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / ReadShare), CancellationToken = cancellationToken };

            await Parallel.ForEachAsync(files, options, async (file, token) =>
            {
                read.Add((file.Path, await ReadOneAsync(file.Path, file.Blob, token).ConfigureAwait(false)));
            }).ConfigureAwait(false);

            return [.. read];
        }

        /// <summary>
        /// One file read, by the core's rules first - its size, then its bytes, binary or undecodable, gone since it was
        /// listed - and then by the implementation that claims it, whose extract is completed with every node's digest and
        /// written once.
        /// </summary>
        private async Task<CodeGraphFileEntry> ReadOneAsync(string path, string blob, CancellationToken token)
        {
            var absolute = Absolute(path);
            FileStanding? standing;
            byte[] bytes;

            try
            {
                standing = service._fileSystem.StandingOf(absolute);

                if (standing is null)
                {
                    return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = "it was gone when it was read" };
                }

                if (standing.Length > _shape.MaxFileBytes)
                {
                    return new CodeGraphFileEntry(blob, CodeFileStates.TooLarge, standing.Length) { Why = string.Create(CultureInfo.InvariantCulture, $"{standing.Length:N0} bytes, over maxFileBytes ({_shape.MaxFileBytes:N0})"), Mtime = standing.Written };
                }

                bytes = await service._fileSystem.ReadAllBytesAsync(absolute, token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = "it was gone when it was read" };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, 0) { Why = $"it could not be read: {ex.Message.TrimEnd('.')}" };
            }

            var decoded = CodeFileText.Decode(bytes);

            if (decoded.Text is not { } text)
            {
                return new CodeGraphFileEntry(blob, decoded.State!, bytes.Length) { Why = decoded.Why, Mtime = standing.Written };
            }

            var route = service._extractors.Route(path, CodeFileText.Probe(text));

            if (route.Failed is { } failed)
            {
                return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = failed, Mtime = standing.Written };
            }

            if (route.Extractor is not { } claimant)
            {
                return new CodeGraphFileEntry(blob, CodeFileStates.Ambiguous, bytes.Length) { Why = route.Ambiguous, Mtime = standing.Written };
            }

            var extractor = _shape.Uses(claimant.Language) ? claimant : service._extractors.Fallback;
            var input = new CodeFileInput(path, text);
            var lines = input.Lines;
            FileExtract extract;

            try
            {
                extract = extractor.Extract(input).WithDigests(lines);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The contract's kit holds every implementation to answering for any text; one that throws is a defect of
                // it, said for the one file and never the end of the sync.
                return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = $"its implementation '{extractor.Language}' failed on it: {ex.Message.TrimEnd('.')}", Mtime = standing.Written };
            }

            var name = CodeGraphIds.ExtractName(blob, path, extractor.Language, extractor.Version);
            var stored = new CodeGraphStoredExtract(CodeGraphManifest.CurrentSchema, extractor.Language, extractor.Version, blob, path, lines.Count, extract);

            if (stored.Problem() is { } wrong)
            {
                return new CodeGraphFileEntry(blob, CodeFileStates.Unreadable, bytes.Length) { Why = $"its implementation '{extractor.Language}' answered what is no extract: {wrong}", Mtime = standing.Written };
            }

            var file = _run.GraphExtractFile(name);
            service._fileSystem.CreateDirectory(Path.GetDirectoryName(file)!);
            service._store.WriteGraphExtract(file, stored);
            Interlocked.Increment(ref _extracted);

            return new CodeGraphFileEntry(blob, StateOf(extractor, extract.Generated is not null), bytes.Length)
            {
                Language = extractor.Language,
                Why = extract.Generated,
                Lines = lines.Count,
                Mtime = standing.Written,
                Extract = name,
            };
        }

        /// <summary>The state a file read by <paramref name="extractor"/> is in.</summary>
        private string StateOf(ICodeGraphExtractor extractor, bool generated)
            => generated && _shape.ExcludeGenerated ? CodeFileStates.Generated
                : extractor.Claims.Fallback ? CodeFileStates.Listed
                : extractor.Outlines ? CodeFileStates.Outlined
                : CodeFileStates.Parsed;

        /// <summary>
        /// Links every file the manifest keeps: each extract read back - one that does not read is made again from its file
        /// - and given to the linker, a generated file's left out under <c>exclude</c>.
        /// </summary>
        private async Task<CodeGraphSymbolsFile> LinkAsync(SortedDictionary<string, CodeGraphFileEntry> entries)
        {
            var files = new List<CodeGraphFile>();

            foreach (var (path, entry) in entries.ToList())
            {
                if (entry.Extract is not { } name)
                {
                    files.Add(new CodeGraphFile(path, null, null));
                    continue;
                }

                var stored = ExtractOrNull(name, path);

                if (stored is null)
                {
                    var made = await ReadOneAsync(path, entry.Blob!, cancellationToken).ConfigureAwait(false);
                    entries[path] = made;

                    // Read after all: counted as read, and no longer as kept.
                    if (_reusedPaths.Remove(path))
                    {
                        _reused--;
                    }

                    stored = made.Extract is { } again ? ExtractOrNull(again, path) : null;
                }

                files.Add(new CodeGraphFile(path, entries[path].Language, entries[path].State == CodeFileStates.Generated ? null : stored?.Extract));
            }

            var linked = CodeGraphLinker.Link(files);
            var handles = CodeGraphIds.Handles(linked.Symbols.Select(symbol => symbol.Id));

            return new CodeGraphSymbolsFile(
                CodeGraphManifest.CurrentSchema,
                string.Empty,
                CodeGraphLinker.Revision,
                [.. linked.Symbols.Select(symbol => new CodeGraphSymbolEntry(symbol.Id, handles[symbol.Id], symbol.Kind, symbol.Paths))],
                linked.Edges,
                linked.External,
                linked.Unresolved);
        }

        /// <summary>The extract <paramref name="name"/> of <paramref name="path"/>, where it reads as one made for it; null where it does not.</summary>
        private CodeGraphStoredExtract? ExtractOrNull(string name, string path)
        {
            try
            {
                return service._store.ReadGraphExtract(_run.GraphExtractFile(name)) is { } stored && stored.Path == path ? stored : null;
            }
            catch (HarnessException)
            {
                return null;
            }
        }

        /// <summary>
        /// The run's link result, where it reads and was made from the manifest of <paramref name="digest"/> by this build's
        /// linker; null where it is to be made again.
        /// </summary>
        private CodeGraphSymbolsFile? Symbols(string digest)
        {
            try
            {
                return service._store.ReadGraphSymbols(_run) is { } symbols && symbols.Graph == digest && symbols.Linker == CodeGraphLinker.Revision ? symbols : null;
            }
            catch (HarnessException)
            {
                return null;
            }
        }

        /// <summary>Removes every extract <paramref name="manifest"/> does not name: what a crash or an earlier state left.</summary>
        private void RemoveUnnamed(CodeGraphManifest manifest)
        {
            var named = manifest.Files.Values.Where(entry => entry.Extract is not null)
                .Select(entry => _run.GraphExtractFile(entry.Extract!))
                .ToHashSet(StringComparer.FromComparison(service._platform.PathComparison));

            if (!service._fileSystem.DirectoryExists(_run.GraphExtractsDirectory))
            {
                return;
            }

            foreach (var file in service._fileSystem.EnumerateFiles(_run.GraphExtractsDirectory, recursive: true).Where(file => !named.Contains(file)).ToList())
            {
                service._fileSystem.DeleteFile(file);
            }
        }

        /// <summary>For each implementation that read a file, how many it read and the nodes they hold.</summary>
        private static IReadOnlyList<CodebaseGraphLanguage> Languages(IReadOnlyDictionary<string, CodeGraphFileEntry> files, CodeGraphSymbolsFile symbols)
        {
            var byPath = files.Where(file => file.Value.Language is not null).ToDictionary(file => file.Key, file => file.Value.Language!, StringComparer.Ordinal);

            return [.. byPath.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(language => new CodebaseGraphLanguage(
                language,
                byPath.Count(file => file.Value == language),
                symbols.Nodes.Count(node => node.Id.StartsWith(language + ":", StringComparison.Ordinal)
                    || (node.Kind == CodeNodeKinds.File && byPath.GetValueOrDefault(node.Paths[0]) == language))))];
        }

        /// <summary>Whether the graph holds declarations of a file in <paramref name="entry"/>'s state.</summary>
        private static bool Declares(CodeGraphFileEntry entry) => entry.State is CodeFileStates.Parsed or CodeFileStates.Outlined or CodeFileStates.Listed;

        private string Absolute(string path) => Path.Combine(_tree, path.Replace('/', Path.DirectorySeparatorChar));

        private HarnessException NotAnswered(HarnessException ex)
            => new(HarnessExit.ToolMissing, $"git did not answer while the codebase graph listed '{_tree}': {ex.Message}");
    }

    private readonly IGitClient _gitClient = gitClient;
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly OrchestrationStore _store = store;
    private readonly CodeGraphExtractors _extractors = extractors;
    private readonly IHostPlatform _platform = platform;
    private readonly IToolIdentityProvider _identity = identity;
    private readonly IHarnessOutput _output = output;
    private readonly TimeProvider _time = time;

    /// <summary>One file of the tree as it was listed.</summary>
    /// <param name="Blob">The blob git gives its content, or the submodule's commit; null for a link it does not hold.</param>
    /// <param name="Link">Whether git records it as a link.</param>
    /// <param name="Submodule">Whether it is a submodule.</param>
    private sealed record Listed(string? Blob, bool Link, bool Submodule);
}
