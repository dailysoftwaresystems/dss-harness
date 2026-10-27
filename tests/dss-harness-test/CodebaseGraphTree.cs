using System.Collections.Concurrent;
using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>A tree, a run of it, and the sync of its graph over a registry the test chooses.</summary>
internal sealed class CodebaseGraphTree : IDisposable
{
    private readonly TempDirectory _temp;
    private readonly TempDirectory _worktrees = new();
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;
    private CodeGraphExtractors _registry;
    private CodebaseGraphShape _shape;
    private IGitClient _git;
    private IFileSystem? _files;
    private IHostPlatform _platform;
    private CodebaseGraphService _service;

    private CodebaseGraphTree(TempDirectory temp, CodebaseGraphShape shape)
    {
        _temp = temp;
        _shape = shape;
        Layout = new HarnessLayout(temp.Path, temp.Path);
        Run = OrchestratorLayout.Of(Layout, "run");
        _registry = new CodeGraphExtractors([Toy, new ListedExtractor(), new MarkdownExtractor(), new AmbiguousExtractor("ambone"), new AmbiguousExtractor("ambtwo")]);
        _git = Harness.GitClient;
        _platform = Harness.Platform;
        _service = Made();
        Output.When(output => output.Publish(Arg.Any<RunEvent>())).Do(call => Events.Enqueue(call.Arg<RunEvent>()));
    }

    public HarnessFactory Harness { get; } = new();

    public ToyExtractor Toy { get; } = new();

    public ManualClock Clock { get; } = new();

    public IHarnessOutput Output { get; } = Substitute.For<IHarnessOutput>();

    /// <summary>Every event the syncs told of, in order.</summary>
    public ConcurrentQueue<RunEvent> Events { get; } = new();

    public HarnessLayout Layout { get; }

    public OrchestratorLayout Run { get; private set; }

    public OrchestrationStore Store => Harness.OrchestrationStore;

    public string Tree => _temp.Path;

    /// <summary>The worktrees root the run says, whose state the graph never lists.</summary>
    public string WorktreesRoot { get; set; } = "worktrees";

    public static async Task<CodebaseGraphTree> CreateAsync(CodebaseGraphShape? shape = null)
    {
        var graph = new CodebaseGraphTree(new TempDirectory(), shape ?? CodebaseGraphShape.Default);
        await graph.Harness.InitializeGitRepositoryAsync(graph.Tree, TestContext.Current.CancellationToken);
        return graph;
    }

    public void Write(string path, string text) => WriteBytes(path, Encoding.UTF8.GetBytes(text));

    public void WriteBytes(string path, byte[] bytes)
    {
        var file = Path.Combine(Tree, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllBytes(file, bytes);
    }

    public async Task CommitAsync(string? force = null)
    {
        if (force is not null)
        {
            await RunGitAsync("add", "--force", force);
        }

        await Harness.CommitAllAsync(Tree, "change", _token);
    }

    public async Task<string> RunGitAsync(params string[] arguments) => (await Harness.RunGitAsync(Tree, arguments, _token)).StandardOutput;

    public async Task<string> HeadAsync() => (await RunGitAsync("rev-parse", "HEAD")).Trim();

    /// <summary>A worktree of the tree's HEAD for <paramref name="agent"/>, outside the tree, as an agent's is.</summary>
    public async Task<string> WorktreeAsync(string agent)
    {
        var path = Path.Combine(_worktrees.Path, agent);
        await RunGitAsync("worktree", "add", "--detach", path);
        return path;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="path"/> under <paramref name="tree"/>.</summary>
    public static void WriteIn(string tree, string path, string text)
    {
        var file = Path.Combine(tree, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    /// <summary>This graph synced over a registry of <paramref name="extractors"/> from now on.</summary>
    public CodebaseGraphTree Over(params ICodeGraphExtractor[] extractors)
    {
        _registry = new CodeGraphExtractors(extractors);
        _service = Made();
        return this;
    }

    /// <summary>This graph synced through <paramref name="git"/> from now on.</summary>
    public CodebaseGraphTree WithGit(IGitClient git)
    {
        _git = git;
        _service = Made();
        return this;
    }

    /// <summary>This graph synced by a service that holds nothing of it from now on, as a process begun again is.</summary>
    public CodebaseGraphTree Restarted()
    {
        _service = Made();
        return this;
    }

    /// <summary>This graph synced by a service that holds nothing of it, over <paramref name="files"/>, from now on.</summary>
    public CodebaseGraphTree WithFiles(IFileSystem files)
    {
        _files = files;
        _service = Made();
        return this;
    }

    /// <summary>This graph synced on <paramref name="platform"/> from now on.</summary>
    public CodebaseGraphTree WithPlatform(IHostPlatform platform)
    {
        _platform = platform;
        _service = Made();
        return this;
    }

    /// <summary>This graph synced in <paramref name="shape"/> from now on.</summary>
    public CodebaseGraphTree Shaped(CodebaseGraphShape shape)
    {
        _shape = shape;
        return this;
    }

    /// <summary>This graph's syncs made for the run <paramref name="name"/> from now on: a run the store lists.</summary>
    public CodebaseGraphTree ForRun(string name)
    {
        Run = OrchestratorLayout.Of(Layout, name);
        Directory.CreateDirectory(Run.Directory);
        File.WriteAllText(Run.RecordFile, "{}");
        return this;
    }

    public async Task<CodebaseGraphSynced> SyncAsync(bool rebuild = false)
    {
        // Every sync of the test is told apart by the clock, as syncs a minute apart are.
        Clock.Advance(TimeSpan.FromSeconds(1));
        return await _service.SyncAsync(Request(rebuild: rebuild), _token);
    }

    /// <summary>A whole sync of <paramref name="agent"/>'s view of <paramref name="worktree"/>.</summary>
    public async Task<CodebaseGraphSynced> SyncViewAsync(string agent, string worktree)
    {
        Clock.Advance(TimeSpan.FromSeconds(1));
        return await _service.SyncAsync(Request(agent, worktree), _token);
    }

    /// <summary>A sync of the base by <paramref name="paths"/> alone.</summary>
    public Task<CodebaseGraphSynced?> SyncPathsAsync(params string[] paths) => _service.SyncPathsAsync(Request(), paths, _token);

    /// <summary>The check of <paramref name="paths"/> in <paramref name="agent"/>'s view - the base's where it names none.</summary>
    public Task<CodebaseGraphChecked> CheckAsync(string? agent, string? worktree, params string[] paths) => _service.CheckAsync(Request(agent, worktree), paths, _token);

    public Task DropAsync(string agent) => _service.DropViewAsync(Run, agent, _token);

    /// <summary>The run's graph read only - the base, or <paramref name="agent"/>'s view - as the query verb reads it.</summary>
    public Task<CodebaseGraphRead?> ReadOnlyAsync(string? agent = null) => _service.ReadOnlyAsync(Run, agent, _token);

    /// <summary>The service the syncs run through.</summary>
    public ICodebaseGraphService Service => _service;

    public CodeGraphView View(string agent) => Store.ReadGraphView(Run, agent)!;

    public CodeGraphManifest Manifest() => Store.ReadGraphManifest(Run)!;

    public CodeGraphSymbolsFile Symbols() => Store.ReadGraphSymbols(Run)!;

    public IReadOnlyList<string> Extracts()
        => Directory.Exists(Run.GraphExtractsDirectory)
            ? [.. Directory.EnumerateFiles(Run.GraphExtractsDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
            : [];

    public IReadOnlyList<string> SyncLines() => File.ReadAllLines(Run.GraphSyncFile);

    public void Dispose()
    {
        _temp.Dispose();
        _worktrees.Dispose();
    }

    /// <summary>The sync of <paramref name="agent"/>'s view of <paramref name="worktree"/> - the base's where it names none - as the tests ask the service.</summary>
    public CodebaseGraphSync Request(string? agent = null, string? worktree = null, bool rebuild = false)
        => new(Layout, Run, _shape, WorktreesRoot, rebuild, "graph#1") { Worktree = agent is null ? null : new CodebaseGraphWorktree(agent, worktree!) };

    private CodebaseGraphService Made()
        => _files is { } files
            ? new(_git, files, new OrchestrationStore(files, Output), _registry, _platform, new RunningToolDouble("9.9.9"), Output, Clock)
            : new(_git, Harness.FileSystem, Store, _registry, _platform, new RunningToolDouble("9.9.9"), Output, Clock);
}

/// <summary>
/// A toy language: each <c>class X</c> line declares the type <c>X</c>, each <c>call Y</c> line calls <c>Y</c> from the
/// type above it, a first line <c>// generated</c> marks the file generated, a file that says <c>boom</c> fails it, and
/// a line <c>wrong</c> declares a type whose lines are not the file's, and one that says <c>hold</c> waits for
/// <see cref="Gate"/> where it is set.
/// </summary>
internal sealed class ToyExtractor : ICodeGraphExtractor
{
    private int _reads;

    /// <summary>What a file that says <c>hold</c> waits for; none where it is not set.</summary>
    public ManualResetEventSlim? Gate { get; set; }

    /// <summary>Set once a file that says <c>hold</c> waits.</summary>
    public ManualResetEventSlim Holding { get; } = new();

    /// <summary>How many files it has read.</summary>
    public int Reads => _reads;

    public string Language => "toy";

    public int Version { get; set; } = 1;

    public CodeGraphClaims Claims { get; } = CodeGraphClaims.OfExtensions(".toy");

    public bool Outlines => false;

    public CodeGraphExtractorHelp Help { get; } = new("toy files", []);

    public FileExtract Extract(CodeFileInput input)
    {
        Interlocked.Increment(ref _reads);

        if (input.Text.Contains("boom", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("boom");
        }

        if (input.Text.Contains("hold", StringComparison.Ordinal) && Gate is { } gate)
        {
            Holding.Set();
            gate.Wait(TimeSpan.FromSeconds(30));
        }

        var lines = input.Lines;
        var nodes = new List<ExtractNode>();
        var references = new List<ExtractReference>();

        for (var at = 0; at < lines.Count; at++)
        {
            if (lines[at].StartsWith("class ", StringComparison.Ordinal))
            {
                var name = lines[at]["class ".Length..];
                nodes.Add(new ExtractNode($"toy:{name}", CodeNodeKinds.Type, name, name, at + 1, at + 1, lines[at]));
            }
            else if (lines[at] == "wrong")
            {
                nodes.Add(new ExtractNode("toy:Wrong", CodeNodeKinds.Type, "Wrong", "Wrong", at + 1, at + 9, "wrong"));
            }
            else if (lines[at].StartsWith("call ", StringComparison.Ordinal) && nodes.Count > 0)
            {
                references.Add(new ExtractReference(nodes[^1].Id, CodeEdgeKinds.Calls, lines[at]["call ".Length..], at + 1));
            }
        }

        return new FileExtract(nodes)
        {
            References = references,
            Generated = lines[0].StartsWith("// generated", StringComparison.Ordinal) ? "a generator wrote it" : null,
        };
    }
}

/// <summary>An implementation whose content test of <c>.chk</c> fails on every file.</summary>
internal sealed class CheckingExtractor : ICodeGraphExtractor
{
    public string Language => "checking";

    public int Version => 1;

    public CodeGraphClaims Claims { get; } = new([], [new CodeGraphClaim(".chk", _ => throw new InvalidOperationException("no test of this."))]);

    public bool Outlines => false;

    public CodeGraphExtractorHelp Help { get; } = new("chk files", []);

    public FileExtract Extract(CodeFileInput input) => FileExtract.Empty;
}

/// <summary>One of two implementations that claim <c>.amb</c> and both say yes to every file.</summary>
internal sealed class AmbiguousExtractor(string language) : ICodeGraphExtractor
{
    public string Language => language;

    public int Version => 1;

    public CodeGraphClaims Claims { get; } = new([], [new CodeGraphClaim(".amb", _ => true)]);

    public bool Outlines => false;

    public CodeGraphExtractorHelp Help { get; } = new("amb files", []);

    public FileExtract Extract(CodeFileInput input) => FileExtract.Empty;
}
