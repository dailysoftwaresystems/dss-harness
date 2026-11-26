using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Agents.Isolation;
using DssHarness.Core.Application;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Hosts;
using DssHarness.Core.Json;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Processes;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;
using DssHarness.Core.Secrets;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>How a command ended, as its output logs say it.</summary>
/// <param name="Command">The command.</param>
/// <param name="CommandLine">The command line as it was typed, the tool's name first.</param>
/// <param name="Directory">The directory the command acted on; null where it named none that could be.</param>
/// <param name="Run">The repository and the run the command reached; null where it reached none.</param>
/// <param name="ExitCode">The code it exits with.</param>
/// <param name="Summary">The line it ends on: why it stopped.</param>
/// <param name="Output">What it wrote.</param>
public sealed record OutputLogsEnd(
    string Command,
    IReadOnlyList<string> CommandLine,
    string? Directory,
    (HarnessLayout Repository, OrchestratorLayout Run)? Run,
    int ExitCode,
    string Summary,
    CapturedOutput Output);

/// <summary>
/// A command's output logs: one zip holding everything needed to understand a run and to put right what it met - the
/// run's whole directory, its folders kept, the configuration the run was read with, and <c>manifest.json</c> at the zip's
/// root, the place to start reading: what was run, on which build and system, how it ended and why, the run's state and
/// its open questions, the command's own output, every entry with its size, and what was left out and why. Asked of
/// <c>run-orchestrator</c>, <c>resume-orchestrator</c> and <c>answer-orchestrator</c> by <see cref="Option"/>
/// (<see cref="Commands"/>), and only then: no key of the configuration, no default.
/// </summary>
/// <remarks>
/// <para>
/// Written at every end of the command - done, failed, paused, stopped, refused before a run was reached or after, a
/// failure the command caught - by the one lifecycle every command shares (<see cref="CommandSession"/>), whole each time
/// through a file beside the target that then replaces it (<see cref="IFileSystem.WriteAtomic"/>), so a crash never leaves
/// half a zip and a resumed run's zip holds the whole run. Where it is, is named in the command's text and in its
/// <c>--json</c> document (<see cref="IDocumentFacts"/>) as every record of a run is, on every end; a path the zip cannot
/// be written at is refused before anything runs (<see cref="CheckAsync"/>).
/// </para>
/// <para>
/// The run's directory is read as the tool reads a place an isolated process writes (<see cref="IFileSystem.Hold"/>): a
/// link or a junction is never followed, wherever it stands, so nothing outside the run is carried into the zip. Left out,
/// each named with why: every link, every key store and dotenv file at any depth and the shared copy of one an agent was
/// handed, the run's temporary directory, the caches of its codebase graph that are made again from the tree, and each
/// agent's worktree, which is outside the run. Nothing is capped, cut or dropped: a file that could not be read whole is
/// said so.
/// </para>
/// <para>
/// No secret value lands in the zip: every byte of it is held to the mask the run's records are held to (every secret the
/// configuration appoints that the key slot answers, <see cref="ReasonLaunch.Mask"/>), read byte for byte
/// (<see cref="ActionValues.ReadByteForByte"/>), the configuration, the command line and the command's output among it.
/// </para>
/// </remarks>
/// <param name="fileSystem">Reads the run and writes the zip.</param>
/// <param name="repositories">Finds the repository the command acted in, where it reached no run.</param>
/// <param name="contextLoader">Loads the configuration whose appointed secrets the zip is masked by.</param>
/// <param name="store">Reads the run's state, its questions and its agents' records, for the manifest.</param>
/// <param name="platform">Which system this is, and how its paths compare.</param>
/// <param name="process">The directory a relative path is read from.</param>
/// <param name="tool">Which build of the tool this is.</param>
/// <param name="keys">The slot the mask's secrets are read from.</param>
/// <param name="isolation">What the launch whose mask is reused is built with.</param>
/// <param name="reached">The run the command reached.</param>
/// <param name="capture">The command's lines, kept; null where nothing keeps them.</param>
/// <param name="clock">When the zip is written.</param>
public sealed class FlowOutputLogs(
    IFileSystem fileSystem,
    IRepositoryLocator repositories,
    IHarnessContextLoader contextLoader,
    OrchestrationStore store,
    IHostPlatform platform,
    IProcessContext process,
    IToolIdentityProvider tool,
    AgentKeyStore keys,
    IAgentIsolation isolation,
    FlowRunReached reached,
    OutputCapture? capture = null,
    TimeProvider? clock = null) : IDocumentFacts
{
    /// <summary>The option that asks for the zip.</summary>
    public const string Option = "--output-logs";

    /// <summary>What the zip is named by among a command's records: the member of its document's <c>records</c>, and its line's name.</summary>
    public const string Record = "outputLogs";

    /// <summary>The manifest, at the zip's root.</summary>
    public const string ManifestName = "manifest.json";

    /// <summary>The configuration the run was read with, at the zip's root.</summary>
    public const string ConfigName = "config.json";

    /// <summary>The folder of the zip the run's directory is kept in.</summary>
    public const string RunFolder = "run";

    /// <summary>The manifest's schema.</summary>
    public const int Schema = 1;

    /// <summary>Why a link or a junction is left out.</summary>
    public const string LinkWhy = "a link or a junction, never followed: it could lead outside the run";

    /// <summary>Why a key store or a dotenv file is left out.</summary>
    public const string KeyStoreWhy = "a key store or a dotenv file, whose values are secrets";

    /// <summary>Why the run's temporary directory is left out.</summary>
    public const string TempWhy = "the run's temporary directory: what its launches and their children left as temp";

    /// <summary>Why a cache of the run's codebase graph is left out.</summary>
    public const string GraphCacheWhy = "a cache of the run's codebase graph, made again from the tree";

    /// <summary>The commands that take <see cref="Option"/>: the three that drive a session of a run.</summary>
    public static IReadOnlyList<string> Commands { get; } = [FlowService.RunCommand, FlowService.ResumeCommand, FlowService.AnswerCommand];

    /// <summary>The names in the graph's directory that are its caches, made again from the tree: its database, its link result, the extracts and the agents' views.</summary>
    public static IReadOnlyList<string> GraphCaches { get; } =
    [
        OrchestratorLayout.GraphManifestFileName,
        OrchestratorLayout.GraphSymbolsFileName,
        OrchestratorLayout.GraphExtractsDirectoryName,
        OrchestratorLayout.GraphViewsDirectoryName,
    ];

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private string? _target;
    private IReadOnlyList<string>? _commandLine;
    private string? _directory;
    private bool _named;

    /// <summary>The zip's whole path, once <see cref="CheckAsync"/> held it to be one that can be written; null until then.</summary>
    public string? Target
    {
        get
        {
            lock (_gate)
            {
                return _target;
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>True from the check that held the path to the end that wrote the zip, or found it could not.</remarks>
    public bool HasAny
    {
        get
        {
            lock (_gate)
            {
                return _named;
            }
        }
    }

    /// <summary>The line the command names the zip with, among the lines that name its records.</summary>
    public string Line => $"{Record}: {Target}";

    /// <summary>Whether <paramref name="name"/>, a file's or a directory's, is a key store or a dotenv file: the tool's own slots' names - <c>.env</c>, <c>.secrets</c>, <c>.key</c> - and <c>.env.&lt;any&gt;</c> or <c>&lt;any&gt;.env</c>, in any case.</summary>
    /// <param name="name">One name, no separator in it.</param>
    public static bool IsKeyStore(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return string.Equals(name, HarnessLayout.ItemEnvFileName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HarnessLayout.RunnerSecretsDirectoryName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HarnessLayout.ItemKeyFileName, StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(HarnessLayout.ItemEnvFileName + ".", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(HarnessLayout.ItemEnvFileName, StringComparison.OrdinalIgnoreCase);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// The zip, by its whole path, in the document's <c>records</c> as <see cref="Record"/>, as every record a command of a
    /// run wrote is named there (<see cref="FlowReports.Document"/>).
    /// </remarks>
    public void WriteInto(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document[FlowReports.RecordsMember] is not JsonObject records)
        {
            records = [];
            document[FlowReports.RecordsMember] = records;
        }

        records[Record] = Target;
    }

    /// <summary>
    /// Holds <paramref name="path"/> to one the zip can be written at, before anything of the command
    /// runs: whole, read from the directory the process stands in where it is relative; not a directory; outside the
    /// repository's orchestrators, where every run keeps its own records - a run started without <c>--run</c> is named
    /// only as it starts; and in a directory that is there or can be made, which is made now.
    /// </summary>
    /// <param name="path">Where the zip is asked to be written, as the front end was given it.</param>
    /// <param name="commandLine">The command line as it was typed, the tool's name first, for the manifest.</param>
    /// <param name="directory">The directory the command acts on; null where it names none that could be.</param>
    /// <param name="cancellationToken">Stops the look at the repository.</param>
    /// <returns>The refusal, saying nothing was started; null where the path holds, from then on the zip's <see cref="Target"/>.</returns>
    public async Task<string?> CheckAsync(string path, IReadOnlyList<string> commandLine, string? directory, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(commandLine);

        string target;

        try
        {
            target = Path.GetFullPath(path, process.CurrentDirectory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return $"{Option} names '{path}', which is no path a file can be written at: {Why(ex)}; name a file. Nothing was started.";
        }

        try
        {
            if (fileSystem.KindOf(target) == PathKind.Directory)
            {
                return $"{Option} names '{target}', which is a directory; name the file the zip is written to. Nothing was started.";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Option} names '{target}', which could not be looked at: {Why(ex)}; name a file that can be written. Nothing was started.";
        }

        if (directory is not null && await LocateAsync(directory, cancellationToken).ConfigureAwait(false) is { } repository
            && PathContainment.IsInsideOrSame(Resolved(repository.OrchestratorsDirectory), Resolved(target), platform.PathComparison))
        {
            return $"{Option} names '{target}', inside '{repository.OrchestratorsDirectory}', where every run keeps its own records; "
                + "name a file outside it. Nothing was started.";
        }

        var parent = Path.GetDirectoryName(target)!;

        try
        {
            fileSystem.CreateDirectory(parent);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{Option} names '{target}', whose directory '{parent}' could not be made: {Why(ex)}; name a file in a directory that can be. Nothing was started.";
        }

        lock (_gate)
        {
            _target = target;
            _commandLine = commandLine;
            _directory = directory;
            _named = true;
        }

        return null;
    }

    /// <summary>
    /// Writes the zip at the end of the command, over the run it reached, with the lines it wrote - <paramref name="report"/>,
    /// the events it is about to end with, kept as they will be written first, so the zip holds the command's output as it
    /// ends.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <param name="exitCode">The code it exits with.</param>
    /// <param name="summary">The line it ends on.</param>
    /// <param name="json">Whether it answers with its <c>--json</c> document.</param>
    /// <param name="report">The events it ends with, not yet written.</param>
    /// <param name="cancellationToken">Stops the reading of the configuration and the key slot; the zip is written whole or not at all.</param>
    /// <exception cref="InvalidOperationException">No path was held (<see cref="CheckAsync"/>).</exception>
    /// <exception cref="IOException">The zip could not be written; what was there before is as it was.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write the zip; what was there before is as it was.</exception>
    public async Task WriteAsync(string command, int exitCode, string summary, IReadOnlyList<RunEvent> report, bool json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);

        string target;
        IReadOnlyList<string> commandLine;
        string? directory;

        lock (_gate)
        {
            target = _target ?? throw new InvalidOperationException($"no path was held for {Option}: {nameof(CheckAsync)} holds one first.");
            commandLine = _commandLine!;
            directory = _directory;
        }

        foreach (var @event in report)
        {
            capture?.Emit(@event);
        }

        await WriteAsync(
            target,
            new OutputLogsEnd(command, commandLine, directory, reached.Reached, exitCode, summary, capture?.Captured(json) ?? new CapturedOutput(json, null, [])),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes the zip <paramref name="end"/> says, at <paramref name="target"/>: the run's directory, the configuration and the
    /// manifest, through a file beside it that then replaces whatever is there. The one writer of the zip, for any front end.
    /// </summary>
    /// <param name="target">Where the zip is written, whole.</param>
    /// <param name="end">How the command ended.</param>
    /// <param name="cancellationToken">Stops the reading of the configuration and the key slot; the zip is written whole or not at all.</param>
    /// <exception cref="IOException">The zip could not be written; what was there before is as it was.</exception>
    /// <exception cref="UnauthorizedAccessException">This process may not write the zip; what was there before is as it was.</exception>
    public async Task WriteAsync(string target, OutputLogsEnd end, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(target);
        ArgumentNullException.ThrowIfNull(end);

        var repository = end.Run?.Repository
            ?? (end.Directory is not null ? await LocateAsync(end.Directory, cancellationToken).ConfigureAwait(false) : null);
        var (mask, masked) = await MaskAsync(repository, cancellationToken).ConfigureAwait(false);
        var bytes = mask?.ReadByteForByte();
        var written = _clock.GetLocalNow();

        fileSystem.WriteAtomic(target, stream =>
        {
            using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true, Encoding.UTF8);
            var entries = new List<(string Path, long Size, string? Incomplete)>();
            var leftOut = new List<(string Path, string Why)>();

            if (end.Run is { } run)
            {
                AddRun(zip, run.Repository, run.Run, bytes, written, entries, leftOut);
            }

            var config = repository is null ? null : Path.Combine(repository.MainHarnessDirectory, HarnessLayout.ConfigFileName);
            var configSaid = config is null ? "no repository was found" : AddConfig(zip, config, bytes, written, entries);

            var manifest = Manifest(end, run: end.Run, configSaid, masked, entries, leftOut);
            Mask(manifest, mask);

            var entry = zip.CreateEntry(ManifestName, CompressionLevel.Optimal);
            entry.LastWriteTime = written;
            using var into = entry.Open();
            into.Write(Encoding.UTF8.GetBytes(manifest.ToJsonString(ReportJson.Options) + "\n"));
        });
    }

    /// <summary>Its documents name the zip no more: it could not be written.</summary>
    internal void Unnamed()
    {
        lock (_gate)
        {
            _named = false;
        }
    }

    /// <summary>The repository <paramref name="directory"/> is in; null where it is in none, or git could not say.</summary>
    private async Task<HarnessLayout?> LocateAsync(string directory, CancellationToken cancellationToken)
    {
        try
        {
            return await repositories.LocateAsync(directory, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HarnessException or ProgramStartException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary><paramref name="path"/> with its links followed, as spelled where they cannot be read: compared, never acted on.</summary>
    private string Resolved(string path)
    {
        try
        {
            return fileSystem.ResolveLinks(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>
    /// The mask the zip is held to - the run's records' own (<see cref="ReasonLaunch.Mask"/>), over the configuration of
    /// <paramref name="repository"/>'s main checkout - with how many secrets it masks, or why there is none.
    /// </summary>
    private async Task<(ActionValues? Mask, JsonObject Said)> MaskAsync(HarnessLayout? repository, CancellationToken cancellationToken)
    {
        if (repository is null)
        {
            return (null, new JsonObject { ["secrets"] = 0, ["whyNone"] = "no repository was found, so no configuration appoints a secret" });
        }

        try
        {
            var context = await MainTree.LoadAsync(contextLoader, platform, repository.MainCheckoutRoot, cancellationToken).ConfigureAwait(false);

            if (Agents.AgentProviders.Resolve(context.Config).Providers is not { } providers)
            {
                return (null, new JsonObject { ["secrets"] = 0, ["whyNone"] = "the configuration's providers do not resolve, so none appoints a secret" });
            }

            var mask = new ReasonLaunch(fileSystem, platform, keys, updates: null, isolation).Mask(context.Layout, providers);

            return (mask, new JsonObject { ["secrets"] = mask?.SecretNames.Count ?? 0 });
        }
        catch (Exception ex) when (ex is HarnessException or ConfigException or ProgramStartException or IOException or UnauthorizedAccessException)
        {
            return (null, new JsonObject { ["secrets"] = 0, ["whyNone"] = $"the configuration could not be read: {Why(ex)}" });
        }
    }

    /// <summary>
    /// Adds the run's directory under <see cref="RunFolder"/>, its folders kept - a folder that holds nothing as one of its
    /// own - walked from the run's directory held, one directory held for each level, however deep, never through a link.
    /// </summary>
    private void AddRun(
        ZipArchive zip,
        HarnessLayout repository,
        OrchestratorLayout run,
        ActionValues? mask,
        DateTimeOffset written,
        List<(string Path, long Size, string? Incomplete)> entries,
        List<(string Path, string Why)> leftOut)
    {
        foreach (var agent in SafeAgents(run))
        {
            if (agent.Record?.WorktreePath(repository) is { } tree)
            {
                leftOut.Add((tree, $"the worktree of agent '{agent.Name}', outside the run: the tree it worked in, which git holds; its records and its steps' records say what it did there"));
            }
        }

        var handed = HandedKeyStores(run);
        HeldDirectory root;

        try
        {
            root = fileSystem.Hold(run.Directory, run.Directory, make: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftOut.Add((RunFolder, $"the run's directory '{run.Directory}' could not be read: {Why(ex)}"));
            return;
        }

        var frames = new Stack<Frame>();
        frames.Push(new Frame(root, string.Empty, Listed(root, RunFolder, leftOut)));

        while (frames.Count > 0)
        {
            var frame = frames.Peek();

            if (!frame.Entries.MoveNext())
            {
                frames.Pop();
                frame.Held.Dispose();

                if (frame.Relative.Length > 0 && frame.Added == 0)
                {
                    // A folder that holds nothing is kept as itself, so the zip's folders are the run's.
                    var folder = zip.CreateEntry($"{RunFolder}/{frame.Relative}/");
                    folder.LastWriteTime = written;
                    entries.Add(($"{RunFolder}/{frame.Relative}/", 0, null));
                }

                if (frames.Count > 0)
                {
                    frames.Peek().Added += frame.Added + (frame.Added == 0 && frame.Relative.Length > 0 ? 1 : 0);
                }

                continue;
            }

            var entry = frame.Entries.Current;
            var relative = frame.Relative.Length == 0 ? entry.Name : $"{frame.Relative}/{entry.Name}";
            var path = $"{RunFolder}/{relative}";

            if (LeftOutBecause(relative, entry, handed) is { } why)
            {
                leftOut.Add((path, why));
                continue;
            }

            switch (entry.Kind)
            {
                case PathKind.Directory:
                    HeldDirectory below;

                    try
                    {
                        below = frame.Held.Below(entry.Name, make: false);
                    }
                    catch (LinkOnTheWayException)
                    {
                        leftOut.Add((path, LinkWhy));
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        leftOut.Add((path, $"could not be read: {Why(ex)}"));
                        break;
                    }

                    frames.Push(new Frame(below, relative, Listed(below, path, leftOut)));
                    break;

                case PathKind.File:
                    if (AddFile(zip, path, () => frame.Held.OpenRead(entry.Name), mask, written, entries, leftOut))
                    {
                        frame.Added++;
                    }

                    break;

                case PathKind.Link:
                    leftOut.Add((path, LinkWhy));
                    break;

                default:
                    leftOut.Add((path, "was gone by the time it was read"));
                    break;
            }
        }
    }

    /// <summary>The entries of <paramref name="held"/>, in name order; none, said, where it cannot be listed.</summary>
    private static IEnumerator<HeldName> Listed(HeldDirectory held, string path, List<(string Path, string Why)> leftOut)
    {
        try
        {
            return held.Entries().OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList().GetEnumerator();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftOut.Add((path, $"could not be listed: {Why(ex)}"));
            return Enumerable.Empty<HeldName>().GetEnumerator();
        }
    }

    /// <summary>Why the entry at <paramref name="relative"/>, in the run's directory, is left out; null where it is kept.</summary>
    private static string? LeftOutBecause(string relative, HeldName entry, IReadOnlySet<string> handed)
    {
        if (IsKeyStore(entry.Name) || handed.Contains(relative))
        {
            return KeyStoreWhy;
        }

        if (string.Equals(relative, HarnessLayout.OrchestratorTempDirectoryName, StringComparison.Ordinal))
        {
            return TempWhy;
        }

        var graph = OrchestratorLayout.GraphDirectoryName + "/";

        return relative.StartsWith(graph, StringComparison.Ordinal) && GraphCaches.Contains(relative[graph.Length..], StringComparer.Ordinal)
            ? GraphCacheWhy
            : null;
    }

    /// <summary>
    /// The shared copy of each file an agent was handed whose path is a key store or a dotenv file, as its seed names it:
    /// <c>agents/&lt;agent&gt;/shared/&lt;sha256&gt;</c>, relative to the run's directory - a copy is named by its digest
    /// alone, and only the seed says what it is a copy of.
    /// </summary>
    private HashSet<string> HandedKeyStores(OrchestratorLayout run)
    {
        var handed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var agent in SafeAgents(run))
        {
            SeedRecord? seed;

            try
            {
                seed = store.ReadSeed(run, agent.Name);
            }
            catch (HarnessException)
            {
                continue;
            }

            foreach (var (path, digest) in seed?.Paths ?? [])
            {
                if (path.Split('/').Any(IsKeyStore))
                {
                    handed.Add($"{OrchestratorLayout.AgentsDirectoryName}/{agent.Name}/{OrchestratorLayout.SharedDirectoryName}/{digest}");
                }
            }
        }

        return handed;
    }

    /// <summary>The run's agents, each with its record where it reads; none where they cannot be listed.</summary>
    private IReadOnlyList<AgentEntry> SafeAgents(OrchestratorLayout run)
    {
        try
        {
            return store.Agents(run);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or HarnessException)
        {
            return [];
        }
    }

    /// <summary>Adds the configuration, read as the harness reads it, masked; what the manifest says of it.</summary>
    private string AddConfig(ZipArchive zip, string config, ActionValues? mask, DateTimeOffset written, List<(string Path, long Size, string? Incomplete)> entries)
    {
        var leftOut = new List<(string Path, string Why)>();

        if (!fileSystem.FileExists(config))
        {
            return $"'{config}' is not there";
        }

        return AddFile(zip, ConfigName, () => fileSystem.OpenRead(config), mask, written, entries, leftOut)
            ? config
            : $"'{config}' {leftOut[0].Why}";
    }

    /// <summary>
    /// Adds the file <paramref name="open"/> opens at <paramref name="path"/> in the zip, masked, with its size; said in
    /// <paramref name="leftOut"/> where it could not be opened, and as incomplete where it could not be read whole.
    /// </summary>
    /// <returns>Whether it was added.</returns>
    private static bool AddFile(
        ZipArchive zip,
        string path,
        Func<Stream> open,
        ActionValues? mask,
        DateTimeOffset written,
        List<(string Path, long Size, string? Incomplete)> entries,
        List<(string Path, string Why)> leftOut)
    {
        Stream read;

        try
        {
            read = open();
        }
        catch (LinkOnTheWayException)
        {
            leftOut.Add((path, LinkWhy));
            return false;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            leftOut.Add((path, "was gone by the time it was read"));
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            leftOut.Add((path, $"could not be read: {Why(ex)}"));
            return false;
        }

        using (read)
        {
            var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
            entry.LastWriteTime = written;
            using var into = entry.Open();
            var (size, incomplete) = MaskedCopy(read, into, mask);
            entries.Add((path, size, incomplete));
            return true;
        }
    }

    /// <summary>
    /// Copies <paramref name="from"/> to <paramref name="into"/> with every secret <paramref name="mask"/> masks masked, read
    /// byte for byte so every other byte is kept as it was: a little at a time, never parting a secret where one piece ends
    /// (<see cref="Execution.ILineMask.Cut"/>), however long the file.
    /// </summary>
    /// <returns>How many bytes were written; why the file could not be read whole, where it could not.</returns>
    private static (long Size, string? Incomplete) MaskedCopy(Stream from, Stream into, ActionValues? mask)
    {
        var buffer = new byte[81920];
        var pending = new StringBuilder();
        long size = 0;
        string? incomplete = null;

        while (true)
        {
            int read;

            try
            {
                read = from.Read(buffer);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                incomplete = $"could not be read whole: {Why(ex)}";
                break;
            }

            if (read == 0)
            {
                break;
            }

            if (mask is null)
            {
                into.Write(buffer, 0, read);
                size += read;
                continue;
            }

            pending.Append(Encoding.Latin1.GetString(buffer, 0, read));
            var text = pending.ToString();
            var cut = mask.Cut(text);

            if (cut > 0)
            {
                size += Written(into, mask.Redact(text[..cut]));
                pending.Clear().Append(text, cut, text.Length - cut);
            }
        }

        if (pending.Length > 0)
        {
            size += Written(into, mask!.Redact(pending.ToString()));
        }

        return (size, incomplete);
    }

    /// <summary>Writes <paramref name="text"/>, a byte-for-byte reading, back as the bytes it reads; how many.</summary>
    private static long Written(Stream into, string text)
    {
        var bytes = Encoding.Latin1.GetBytes(text);
        into.Write(bytes);
        return bytes.Length;
    }

    /// <summary>The manifest: the place to start reading.</summary>
    private JsonObject Manifest(
        OutputLogsEnd end,
        (HarnessLayout Repository, OrchestratorLayout Run)? run,
        string configSaid,
        JsonObject masked,
        List<(string Path, long Size, string? Incomplete)> entries,
        List<(string Path, string Why)> leftOut)
    {
        var identity = tool.Current;

        return new JsonObject
        {
            ["schema"] = Schema,
            ["writtenAt"] = FlowTimeConverter.Written(_clock.GetUtcNow()),
            ["harness"] = new JsonObject { ["version"] = identity.Version, ["assemblySha256"] = identity.AssemblySha256 },
            ["platform"] = new JsonObject { ["os"] = platform.PlatformKey, ["processor"] = platform.Processor },
            ["command"] = end.Command,
            ["commandLine"] = new JsonArray([.. end.CommandLine.Select(token => JsonValue.Create(token))]),
            ["directory"] = end.Directory,
            ["exitCode"] = end.ExitCode,
            ["summary"] = end.Summary,
            ["run"] = run is { } reached ? RunOf(reached.Run) : null,
            ["output"] = new JsonObject
            {
                ["json"] = end.Output.Json,
                ["document"] = end.Output.Document is { } document ? DocumentOf(document) : null,
                ["lines"] = new JsonArray([.. end.Output.Lines.Select(line => JsonValue.Create(line))]),
            },
            ["config"] = configSaid,
            ["mask"] = masked,
            ["entries"] = new JsonArray([.. entries.Select(EntryOf)]),
            ["leftOut"] = new JsonArray([.. leftOut.Select(left => (JsonNode)new JsonObject { ["path"] = left.Path, ["why"] = left.Why })]),
        };
    }

    /// <summary>One entry of the zip, as the manifest lists it: its path, its size, and why it is not whole where it is not.</summary>
    private static JsonNode EntryOf((string Path, long Size, string? Incomplete) entry)
    {
        var said = new JsonObject { ["path"] = entry.Path, ["size"] = entry.Size };

        if (entry.Incomplete is { } why)
        {
            said["incomplete"] = why;
        }

        return said;
    }

    /// <summary>What the manifest says of the run: its name, workflow, state, directory and open questions, as its records hold them.</summary>
    private JsonObject RunOf(OrchestratorLayout run)
    {
        var flow = SafeRead(() => store.ReadFlow(run));
        var questions = SafeRead(() => store.ReadFlowQuestions(run));

        return new JsonObject
        {
            ["name"] = run.Name,
            ["workflow"] = flow?.Definition ?? SafeRead(() => store.ReadDefinition(run))?.Name,
            ["state"] = flow?.Status,
            ["directory"] = run.Directory,
            ["questions"] = new JsonArray([.. (questions?.Open ?? []).Select(question => (JsonNode)new JsonObject
            {
                ["id"] = question.Id,
                ["kind"] = question.Kind,
                ["step"] = question.RaisedBy?.Step,
                ["question"] = question.ProblemShown?.Happened,
            })]),
        };
    }

    /// <summary>The command's document as it printed it: read as the JSON it is, or kept as its text where it is not.</summary>
    private static JsonNode? DocumentOf(string document)
    {
        try
        {
            return JsonDepth.ParseNode(document);
        }
        catch (JsonException)
        {
            return JsonValue.Create(document);
        }
    }

    /// <summary>Every string of <paramref name="manifest"/>, at any depth, masked by <paramref name="mask"/>: as text, never through the escapes it is written with.</summary>
    private static void Mask(JsonNode manifest, ActionValues? mask)
    {
        if (mask is null)
        {
            return;
        }

        var strings = JsonWalk.Descendants(manifest)
            .Select(found => found.Node)
            .OfType<JsonValue>()
            .Where(value => value.TryGetValue<string>(out _))
            .ToList();

        foreach (var value in strings)
        {
            var text = value.GetValue<string>();
            var redacted = mask.Redact(text);

            if (!string.Equals(text, redacted, StringComparison.Ordinal))
            {
                value.ReplaceWith(redacted);
            }
        }
    }

    /// <summary>A record read for the manifest that must not fail it: null where it is not there or does not read.</summary>
    private static T? SafeRead<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is HarnessException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>An exception's message, as a clause of a sentence.</summary>
    private static string Why(Exception exception) => exception.Message.TrimEnd('.');

    /// <summary>One directory of the walk, held: its path below the run's, its entries still to be taken, and how many entries the zip took from it.</summary>
    private sealed class Frame(HeldDirectory held, string relative, IEnumerator<HeldName> entries)
    {
        public HeldDirectory Held { get; } = held;

        public string Relative { get; } = relative;

        public IEnumerator<HeldName> Entries { get; } = entries;

        public int Added { get; set; }
    }
}
