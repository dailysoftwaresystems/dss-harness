using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.Git;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Output;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;
using NSubstitute;

namespace DssHarness.Tests;

/// <summary>
/// The sync of a run's codebase graph over real git and real trees, with a toy language: what it reads, what it reuses
/// and why, every state a file is listed in, what it never lists, what it seeds from, and the records it leaves.
/// </summary>
public sealed class CodebaseGraphServiceTests
{
    /// <summary>
    /// A first sync reads every file it keeps, links them, and writes the manifest, the link result, an extract for each
    /// file read and one line of <c>sync.jsonl</c>, and tells of it.
    /// </summary>
    [Fact]
    public async Task AFirstSync_ReadsEveryFile_LinksThem_AndWritesItsRecords()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\ncall B\n");
        graph.Write("src/b.toy", "class B\n");
        graph.Write("notes.txt", "plain\n");
        graph.WriteBytes("logo.bin", [0x89, 0x50, 0x00, 0x47]);
        await graph.CommitAsync();

        var synced = await graph.SyncAsync();
        var manifest = graph.Manifest();
        var symbols = graph.Symbols();

        Assert.Equal((5, 4, 0, 0, 0, false), (synced.Files, synced.Extracted, synced.Reused, synced.Removed, synced.Hashed, synced.Dirty));
        Assert.Equal(await graph.HeadAsync(), synced.Commit);
        Assert.Equal(["listed:1", "markdown:1", "toy:2"], synced.Languages.Select(language => $"{language.Language}:{language.Files}"));
        Assert.Equal([("logo.bin", CodeFileStates.Binary)], synced.Skipped.Select(file => (file.Path, file.State)));
        Assert.Null(synced.Rebuilt);
        Assert.Null(synced.SeededFrom);

        Assert.Equal(CodeFileStates.Parsed, manifest.Files["src/a.toy"].State);
        Assert.Equal(CodeFileStates.Outlined, manifest.Files["README.md"].State);
        Assert.Equal(CodeFileStates.Listed, manifest.Files["notes.txt"].State);
        Assert.Equal(CodeFileStates.Binary, manifest.Files["logo.bin"].State);
        Assert.Equal("9.9.9", manifest.Tool);
        Assert.Equal(graph.Tree, manifest.Tree);
        Assert.Equal(manifest.Digest, symbols.Graph);
        Assert.Contains(symbols.Edges, edge => edge is { From: "toy:A", To: "toy:B", Kind: CodeEdgeKinds.Instantiates, Confidence: CodeConfidences.Unique });
        Assert.Equal(4, graph.Extracts().Count);

        var line = Assert.Single(graph.SyncLines());
        Assert.Contains("\"why\":\"graph#1\"", line, StringComparison.Ordinal);
        Assert.Contains("\"extracted\":4", line, StringComparison.Ordinal);
        var told = Assert.Single(graph.Events.OfType<GraphSynced>());
        Assert.Equal((graph.Run.GraphDirectory, CodebaseGraphService.BaseView, "graph#1", 5, 4, 0, 0), (told.Graph, told.View, told.Why, told.Listed, told.Extracted, told.Reused, told.Removed));
    }

    /// <summary>A sync of a tree that has not changed reads no file, and keeps the link result it has.</summary>
    [Fact]
    public async Task ASyncOfAnUnchangedTree_ReadsNoFile()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        var reads = graph.Toy.Reads;
        var symbols = File.ReadAllText(graph.Run.GraphSymbolsFile);

        var synced = await graph.SyncAsync();

        Assert.Equal((0, 2, 0), (synced.Extracted, synced.Reused, synced.Hashed));
        Assert.Equal(reads, graph.Toy.Reads);
        Assert.Equal(symbols, File.ReadAllText(graph.Run.GraphSymbolsFile));
        Assert.Equal(2, graph.SyncLines().Count);
    }

    /// <summary>
    /// A changed file is hashed and read again, alone, and the tree is dirty; once it is committed, the blob git gives it
    /// is the one already read, and it is not read again.
    /// </summary>
    [Fact]
    public async Task AChangedFile_IsReadAgainAlone_AndNotOnceCommitted()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        graph.Write("src/b.toy", "class B\nclass C\n");
        var changed = await graph.SyncAsync();

        Assert.Equal((1, 1, true), (changed.Extracted, changed.Hashed, changed.Dirty));
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:C");

        await graph.CommitAsync();
        var committed = await graph.SyncAsync();

        Assert.Equal((0, 0, false), (committed.Extracted, committed.Hashed, committed.Dirty));
    }

    /// <summary>
    /// A file kept unread for its state - binary, too large, claimed by two - whose content changed is looked at again:
    /// its state is its blob's, never its path's.
    /// </summary>
    [Fact]
    public async Task ASettledFileWhoseContentChanged_IsLookedAtAgain()
    {
        using var graph = await Graph.CreateAsync(shape: CodebaseGraphShape.Default with { MaxFileBytes = 200 });
        graph.WriteBytes("logo.toy", [0x63, 0x00, 0x0A]);
        graph.Write("big.toy", "class Big\n" + new string('x', 300) + "\n");
        graph.Write("both.amb", "either\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        graph.Write("logo.toy", "class Logo\n");
        graph.Write("big.toy", "class Big\n");
        graph.Write("both.amb", "or\n");
        var synced = await graph.SyncAsync();
        var files = graph.Manifest().Files;

        Assert.Equal((3, 2), (synced.Hashed, synced.Extracted));
        Assert.Equal((CodeFileStates.Parsed, CodeFileStates.Parsed), (files["logo.toy"].State, files["big.toy"].State));
        Assert.Equal((CodeFileStates.Ambiguous, (await graph.RunGitAsync("hash-object", "both.amb")).Trim()), (files["both.amb"].State, files["both.amb"].Blob));
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:Logo");
    }

    /// <summary>A raised version reads again the files of its implementation and no other, and the old extracts are removed.</summary>
    [Fact]
    public async Task ARaisedVersion_ReadsItsFilesAgain_AndNoOther()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        graph.Toy.Version = 2;
        var synced = await graph.SyncAsync();

        Assert.Equal((2, 1), (synced.Extracted, synced.Reused));
        Assert.Equal(3, graph.Extracts().Count);
        Assert.DoesNotContain(graph.Extracts(), name => name.EndsWith(".toy-1.json", StringComparison.Ordinal));
        Assert.Contains(new CodeGraphExtractorVersion("toy", 2), graph.Manifest().Extractors);
    }

    /// <summary>A new implementation that claims files the fallback listed takes them, and reads them.</summary>
    [Fact]
    public async Task ANewClaimant_TakesItsFilesFromTheFallback()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        var before = await graph.Over(new ListedExtractor(), new MarkdownExtractor()).SyncAsync();

        Assert.Equal(CodeFileStates.Listed, graph.Manifest().Files["src/a.toy"].State);

        var after = await graph.Over(graph.Toy, new ListedExtractor(), new MarkdownExtractor()).SyncAsync();

        Assert.Equal((2, 1, 1), (before.Extracted, after.Extracted, after.Reused));
        Assert.Equal(CodeFileStates.Parsed, graph.Manifest().Files["src/a.toy"].State);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:A");
    }

    /// <summary>A manifest that does not read is never guessed at: the graph is built again whole, and says why.</summary>
    [Fact]
    public async Task AManifestThatDoesNotRead_IsBuiltAgainWhole()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        File.WriteAllText(graph.Run.GraphManifestFile, File.ReadAllText(graph.Run.GraphManifestFile).Replace("\"schema\": 1", "\"schema\": 99", StringComparison.Ordinal));

        var synced = await graph.SyncAsync();

        Assert.Equal(2, synced.Extracted);
        Assert.StartsWith("its manifest did not read, and a graph is never guessed at: ", synced.Rebuilt, StringComparison.Ordinal);
        Assert.Contains("schema is 99", synced.Rebuilt, StringComparison.Ordinal);
        Assert.Contains("\"rebuilt\":", graph.SyncLines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A link result another linker's revision made is linked again, though no file changed, and no file is read again:
    /// the manifest still reads, and only the link result names the linker.
    /// </summary>
    [Fact]
    public async Task ALinkResultOfAnotherLinker_IsLinkedAgain_AndNoFileIsReadAgain()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\ncall B\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        graph.Store.WriteGraphSymbols(graph.Run, graph.Symbols() with { Linker = CodeGraphLinker.Revision + 1, Edges = [] });
        var reads = graph.Toy.Reads;

        var synced = await graph.SyncAsync();

        Assert.Equal((0, 3, null), (synced.Extracted, synced.Reused, synced.Rebuilt));
        Assert.Equal(reads, graph.Toy.Reads);
        Assert.Equal(CodeGraphLinker.Revision, graph.Symbols().Linker);
        Assert.Contains(graph.Symbols().Edges, edge => edge is { From: "toy:A", To: "toy:B", Kind: CodeEdgeKinds.Instantiates });
        Assert.Equal(graph.Manifest().Digest, graph.Symbols().Graph);
    }

    /// <summary>A step that says rebuild reads every file again, whatever the graph holds.</summary>
    [Fact]
    public async Task ARebuild_ReadsEveryFileAgain()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/gone.toy", "class Gone\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        File.Delete(Path.Combine(graph.Tree, "src", "gone.toy"));

        var synced = await graph.SyncAsync(rebuild: true);

        // It keeps nothing it read, and counts what left the graph all the same.
        Assert.Equal((2, 0, 1), (synced.Extracted, synced.Reused, synced.Removed));
        Assert.Equal("the step said rebuild: every file is extracted again", synced.Rebuilt);

        // Asked again of a tree that did not change, it links the graph again too.
        File.SetLastWriteTimeUtc(graph.Run.GraphSymbolsFile, DateTime.UnixEpoch);
        await graph.SyncAsync(rebuild: true);

        Assert.NotEqual(DateTime.UnixEpoch, File.GetLastWriteTimeUtc(graph.Run.GraphSymbolsFile));
    }

    /// <summary>
    /// A run's first sync starts from the graph of the earlier run synced last - not the first or the last by name -
    /// copying the extracts it reuses, and reads only what changed since; nothing is counted removed from a graph it never
    /// had.
    /// </summary>
    [Fact]
    public async Task AFirstSync_SeedsFromTheRunSyncedLast_AndReadsOnlyWhatChanged()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/gone.toy", "class Gone\n");
        await graph.CommitAsync();

        foreach (var run in new[] { "alpha", "zulu", "bravo" })
        {
            await graph.ForRun(run).SyncAsync();
            graph.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        File.Delete(Path.Combine(graph.Tree, "src", "gone.toy"));
        graph.Write("src/new.toy", "class New\n");
        await graph.CommitAsync();
        var reads = graph.Toy.Reads;

        var synced = await graph.ForRun("mike").SyncAsync();

        Assert.Equal("bravo", synced.SeededFrom);
        Assert.Equal((1, 2, 0), (synced.Extracted, synced.Reused, synced.Removed));
        Assert.Equal(reads + 1, graph.Toy.Reads);
        Assert.Equal(3, graph.Extracts().Count);
        Assert.Contains("\"seededFrom\":\"bravo\"", graph.SyncLines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// What no implementation is ever given is never listed: the credential slots and the tool's own state, tracked or
    /// not; nor what the repository ignores. And the tool's own state is no change of the tree.
    /// </summary>
    [Fact]
    public async Task TheCredentialSlotsAndTheToolsOwnState_AreNeverListed_NorAnIgnoredFile()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write(".gitignore", "*.log\n");
        graph.Write(".harness-config/runner/.secrets/token", "secret\n");
        await graph.CommitAsync(force: ".harness-config/runner/.secrets/token");
        graph.Write(".harness-config/sshItems/host/id", "key\n");
        graph.Write(".mutations/worker/a.toy", "class Worker\n");
        graph.Write("debug.log", "noise\n");

        var synced = await graph.SyncAsync();

        Assert.Equal([".gitignore", "README.md"], graph.Manifest().Files.Keys);
        Assert.False(synced.Dirty);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:Worker");
    }

    /// <summary>
    /// A file untracked is graphed, a file deleted from the tree leaves it - counted removed - and a file git ignores is
    /// never graphed.
    /// </summary>
    [Fact]
    public async Task AnUntrackedFileIsGraphed_ADeletedOneLeaves_AndAnIgnoredOneIsNot()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write(".gitignore", "ignored.toy\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        File.Delete(Path.Combine(graph.Tree, "src", "a.toy"));
        graph.Write("src/new.toy", "class New\n");
        graph.Write("ignored.toy", "class Ignored\n");
        var synced = await graph.SyncAsync();

        Assert.Equal([".gitignore", "README.md", "src/new.toy"], graph.Manifest().Files.Keys);
        Assert.Equal((1, 1, 1, true), (synced.Removed, synced.Extracted, synced.Hashed, synced.Dirty));
    }

    /// <summary>
    /// Every state a file the core does not give an implementation is in: larger than the shape reads, a Git LFS pointer,
    /// undecodable, claimed by two that both say yes, a link git records as one, a submodule - each listed with why.
    /// </summary>
    [Fact]
    public async Task EveryStateTheCoreGivesAFile_IsListedWithWhy()
    {
        using var graph = await Graph.CreateAsync(shape: CodebaseGraphShape.Default with { MaxFileBytes = 200 });
        var head = await graph.HeadAsync();
        graph.Write("big.toy", "class Big\n" + new string('x', 300) + "\n");
        graph.Write("model.toy", "version https://git-lfs.github.com/spec/v1\noid sha256:" + new string('a', 64) + "\nsize 9\n");
        graph.WriteBytes("bad.toy", [0x63, 0xE9, 0x0A]);
        graph.Write("both.amb", "either\n");
        graph.Write("boom.toy", "boom\n");
        graph.Write("wrong.toy", "wrong\n");
        graph.Write("linked", "target.toy");
        await graph.RunGitAsync("config", "core.symlinks", "false");
        await graph.CommitAsync();
        var blob = (await graph.RunGitAsync("hash-object", "-w", "linked")).Trim();
        await graph.RunGitAsync("rm", "--cached", "--quiet", "linked");
        await graph.RunGitAsync("update-index", "--add", "--cacheinfo", $"120000,{blob},linked");
        await graph.RunGitAsync("update-index", "--add", "--cacheinfo", $"160000,{head},vendor/sub");
        Directory.CreateDirectory(Path.Combine(graph.Tree, "vendor", "sub"));
        await graph.RunGitAsync("commit", "--quiet", "-m", "link and submodule");

        var synced = await graph.SyncAsync();
        var states = synced.Skipped.ToDictionary(file => file.Path, file => (file.State, file.Why));

        // A link and a submodule are listed without a read, and a first sync keeps nothing it never had.
        Assert.Equal(0, synced.Reused);

        Assert.Equal(CodeFileStates.TooLarge, states["big.toy"].State);
        Assert.Equal("311 bytes, over maxFileBytes (200)", states["big.toy"].Why);
        Assert.Equal((CodeFileStates.Binary, "a Git LFS pointer, whose file git keeps elsewhere"), states["model.toy"]);
        Assert.Equal(CodeFileStates.Unreadable, states["bad.toy"].State);
        Assert.Equal((CodeFileStates.Ambiguous, "'ambone', 'ambtwo' each say by their content tests that it is theirs"), states["both.amb"]);
        Assert.Equal(CodeFileStates.Unreadable, states["boom.toy"].State);
        Assert.StartsWith("its implementation 'toy' failed on it: ", states["boom.toy"].Why, StringComparison.Ordinal);
        Assert.Equal((CodeFileStates.Link, "a link git records as one, which the graph does not follow"), states["linked"]);
        Assert.Equal((CodeFileStates.Submodule, "a submodule, which the graph does not enter"), states["vendor/sub"]);
        Assert.Equal(CodeFileStates.Unreadable, states["wrong.toy"].State);
        Assert.StartsWith("its implementation 'toy' answered what is no extract: its node 'toy:Wrong': its lines 1-9 are not lines of the file's 1", states["wrong.toy"].Why, StringComparison.Ordinal);
        Assert.Equal(["bad.toy", "boom.toy", "wrong.toy"], synced.Unreadable.Select(file => file.Path));

        // Settled states are kept, unread, while their blob is; an unreadable file is looked at again.
        var again = await graph.SyncAsync();
        Assert.Equal((0, 0, synced.Files - 3), (again.Extracted, again.Hashed, again.Reused));
    }

    /// <summary>
    /// Generated code is kept, marked and read like any other under <c>low</c>; under <c>exclude</c> it is listed
    /// <c>generated</c> and its declarations left out - and a change of the shape alone reads nothing again.
    /// </summary>
    [Fact]
    public async Task GeneratedCode_IsKeptUnderLow_AndLeftOutUnderExclude_WithoutAReadAgain()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("gen.toy", "// generated\nclass Gen\n");
        await graph.CommitAsync();

        var low = await graph.SyncAsync();
        var entry = graph.Manifest().Files["gen.toy"];

        Assert.Equal((CodeFileStates.Parsed, "a generator wrote it"), (entry.State, entry.Why));
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:Gen");
        Assert.DoesNotContain(low.Skipped, file => file.Path == "gen.toy");

        var exclude = await graph.Shaped(CodebaseGraphShape.Default with { ExcludeGenerated = true }).SyncAsync();

        Assert.Equal(0, exclude.Extracted);
        Assert.Equal(CodeFileStates.Generated, graph.Manifest().Files["gen.toy"].State);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:Gen");
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "file:gen.toy");
        Assert.Contains(exclude.Skipped, file => file is { Path: "gen.toy", State: CodeFileStates.Generated });
    }

    /// <summary>
    /// The shape's patterns decide which paths are graphed; its languages which implementations read - a file whose claimant
    /// is left out is the fallback's - and a change of them reads such files again.
    /// </summary>
    [Fact]
    public async Task TheShapesPatternsAndLanguages_DecideWhatIsGraphed_AndHowItIsRead()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        graph.Write("docs/x.md", "# X\n");
        await graph.CommitAsync();

        await graph.Shaped(CodebaseGraphShape.Of(["src/**", "docs/**"], ["src/b.toy"], ["markdown"], null, null, null)).SyncAsync();

        Assert.Equal(["docs/x.md", "src/a.toy"], graph.Manifest().Files.Keys);
        Assert.Equal(("listed", CodeFileStates.Listed), (graph.Manifest().Files["src/a.toy"].Language, graph.Manifest().Files["src/a.toy"].State));
        Assert.Equal(["markdown"], graph.Manifest().Languages);

        var all = await graph.Shaped(CodebaseGraphShape.Of(["src/**", "docs/**"], ["src/b.toy"], null, null, null, null)).SyncAsync();

        Assert.Equal(1, all.Extracted);
        Assert.Equal(("toy", CodeFileStates.Parsed), (graph.Manifest().Files["src/a.toy"].Language, graph.Manifest().Files["src/a.toy"].State));
        Assert.Null(graph.Manifest().Languages);
    }

    /// <summary>
    /// An extract the manifest does not name - what a crash or an earlier state left - is removed by the next sync, one of
    /// a tree that did not change as much as one that links again.
    /// </summary>
    [Fact]
    public async Task AnExtractTheManifestDoesNotName_IsRemoved_ByEverySync()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        var stray = Path.Combine(graph.Run.GraphExtractsDirectory, "ab", "stray.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "{}");

        var unchanged = await graph.SyncAsync();

        Assert.Equal(0, unchanged.Extracted);
        Assert.False(File.Exists(stray));

        File.WriteAllText(stray, "{}");
        graph.Write("src/b.toy", "class B\n");
        await graph.SyncAsync();

        Assert.False(File.Exists(stray));
        Assert.Equal(3, graph.Extracts().Count);
    }

    /// <summary>An extract that does not read is made again from its file while linking, never linked as it is.</summary>
    [Fact]
    public async Task AnExtractThatDoesNotRead_IsMadeAgainWhileLinking()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        var name = graph.Manifest().Files["src/a.toy"].Extract!;
        File.WriteAllText(graph.Run.GraphExtractFile(name), "not json");

        graph.Write("src/b.toy", "class B\nclass C\n");
        var synced = await graph.SyncAsync();

        // The file made again is counted read, and no longer kept: every file is counted once.
        Assert.Equal((2, 1, 3), (synced.Extracted, synced.Reused, synced.Files));
        Assert.Contains("\"extracted\":2,\"reused\":1", graph.SyncLines()[^1], StringComparison.Ordinal);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:A");
        Assert.NotNull(graph.Store.ReadGraphExtract(graph.Run.GraphExtractFile(name)));
    }

    /// <summary>
    /// The extracts keep names and not targets: a file added that declares a name binds the caller in a file that did not
    /// change, which is not read again.
    /// </summary>
    [Fact]
    public async Task ANewFilesName_BindsAnUnchangedCaller_WhichIsNotReadAgain()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\ncall Fresh\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        Assert.Equal(1, graph.Symbols().External["toy:A"]);

        graph.Write("src/fresh.toy", "class Fresh\n");
        var synced = await graph.SyncAsync();

        Assert.Equal(1, synced.Extracted);
        Assert.Contains(graph.Symbols().Edges, edge => edge is { From: "toy:A", To: "toy:Fresh", Kind: CodeEdgeKinds.Instantiates });
        Assert.Empty(graph.Symbols().External);
    }

    /// <summary>A tree git does not answer for is said as git not answering, exit 14 - never an empty graph.</summary>
    [Fact]
    public async Task ATreeGitDoesNotAnswerFor_IsRefusedFourteen()
    {
        using var graph = await Graph.CreateAsync();
        Directory.Move(Path.Combine(graph.Tree, ".git"), Path.Combine(graph.Tree, ".git-gone"));

        var refused = await Assert.ThrowsAsync<HarnessException>(() => graph.SyncAsync());

        Assert.Equal(HarnessExit.ToolMissing, refused.ExitCode);
        Assert.StartsWith($"git did not answer while the codebase graph listed '{graph.Tree}': ", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(graph.Run.GraphManifestFile));
    }

    /// <summary>
    /// Syncs of one graph asked at once wait for each other: no two list the tree at once - each listing held open long
    /// enough for another to begin - each ends whole, and the graph reads.
    /// </summary>
    [Fact]
    public async Task SyncsAskedAtOnce_WaitForEachOther()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        var real = graph.Harness.GitClient;
        var git = Substitute.For<IGitClient>();
        var gate = new object();
        var listing = 0;
        var most = 0;

        git.ListIndexedFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            lock (gate)
            {
                most = Math.Max(most, ++listing);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(150), call.ArgAt<CancellationToken>(1));
            return await real.ListIndexedFilesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
        });
        git.ListWorkingFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => real.ListWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        git.ResolveCommitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => real.ResolveCommitAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2)));
        git.HashWorkingFilesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var blobs = await real.HashWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<string>>(1), call.ArgAt<CancellationToken>(2));

            lock (gate)
            {
                listing--;
            }

            return blobs;
        });

        graph.WithGit(git);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => graph.SyncAsync()));

        Assert.Equal(1, most);
        Assert.Equal(4, git.ReceivedCalls().Count(call => call.GetMethodInfo().Name == nameof(IGitClient.ListIndexedFilesAsync)));
        Assert.Equal(4, graph.SyncLines().Count);
        Assert.Equal(2, graph.Extracts().Count);
        Assert.NotNull(graph.Manifest());
    }

    /// <summary>
    /// A file over maxFileBytes is kept unread while the limit stands; raised, it is read, and nothing else is; lowered
    /// again, it is listed too large once more, its declarations leave the graph and its extract goes.
    /// </summary>
    [Fact]
    public async Task ARaisedOrLoweredMaxFileBytes_ReadsAgainWhatItNowDecides_AndNoOther()
    {
        using var graph = await Graph.CreateAsync(shape: CodebaseGraphShape.Default with { MaxFileBytes = 200 });
        graph.Write("big.toy", "class Big\n" + new string('x', 300) + "\n");
        graph.Write("small.toy", "class Small\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        Assert.Equal(CodeFileStates.TooLarge, graph.Manifest().Files["big.toy"].State);

        var same = await graph.SyncAsync();

        Assert.Equal((0, 3), (same.Extracted, same.Reused));

        var raised = await graph.Shaped(CodebaseGraphShape.Default with { MaxFileBytes = 1000 }).SyncAsync();

        Assert.Equal((1, 2), (raised.Extracted, raised.Reused));
        Assert.Equal(CodeFileStates.Parsed, graph.Manifest().Files["big.toy"].State);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:Big");

        var lowered = await graph.Shaped(CodebaseGraphShape.Default with { MaxFileBytes = 200 }).SyncAsync();

        Assert.Equal((0, 2), (lowered.Extracted, lowered.Reused));
        Assert.Equal(CodeFileStates.TooLarge, graph.Manifest().Files["big.toy"].State);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:Big");
        Assert.Equal(2, graph.Extracts().Count);
    }

    /// <summary>
    /// A run's first sync seeds past an earlier run whose manifest does not read, from the newest that does, and reads
    /// again a file whose extract that run no longer holds.
    /// </summary>
    [Fact]
    public async Task AFirstSync_SeedsPastAManifestThatDoesNotRead_AndReadsWhatTheSeedLacks()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.ForRun("alpha").SyncAsync();
        var lacking = graph.Run.GraphExtractFile(graph.Manifest().Files["src/a.toy"].Extract!);
        graph.Clock.Advance(TimeSpan.FromMinutes(1));
        await graph.ForRun("bravo").SyncAsync();
        File.WriteAllText(graph.Run.GraphManifestFile, "not json");
        File.Delete(lacking);
        var reads = graph.Toy.Reads;

        var synced = await graph.ForRun("mike").SyncAsync();

        Assert.Equal("alpha", synced.SeededFrom);
        Assert.Equal((1, 2, 0), (synced.Extracted, synced.Reused, synced.Removed));
        Assert.Equal(reads + 1, graph.Toy.Reads);
        Assert.Equal(3, graph.Extracts().Count);
    }

    /// <summary>A repository with no commit yet is graphed from its index and files, naming no commit, and dirty.</summary>
    [Fact]
    public async Task ARepositoryWithNoCommit_IsGraphed_NamingNoCommit()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.RunGitAsync("add", "--all");
        await graph.RunGitAsync("update-ref", "-d", "HEAD");

        var synced = await graph.SyncAsync();

        Assert.Null(synced.Commit);
        Assert.Null(graph.Manifest().Head);
        Assert.True(synced.Dirty);
        Assert.Equal(["README.md", "src/a.toy"], graph.Manifest().Files.Keys);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:A");
    }

    /// <summary>
    /// Every credential slot and every place of the tool's own state the layout names - under the worktrees root the run
    /// says, read from the request - is never listed, tracked or not; a directory of that name elsewhere is graphed.
    /// </summary>
    [Fact]
    public async Task EveryCredentialSlotAndStatePath_IsNeverListed_UnderTheWorktreesRootTheRunSays()
    {
        using var graph = await Graph.CreateAsync();
        graph.WorktreesRoot = "build/trees";
        var never = HarnessLayout.SecretSlots.Concat(HarnessLayout.StatePaths(graph.WorktreesRoot)).ToList();

        // The lock and a synced copy's marker are files; every other place is a folder of them.
        string[] files = [$"{HarnessLayout.DirectoryName}/{HarnessLayout.LockFileName}", $"{HarnessLayout.DirectoryName}/{HarnessLayout.SyncedCopyMarkerName}"];

        foreach (var slot in never)
        {
            graph.Write(files.Contains(slot) ? slot : slot + "/inside.toy", "class Never\n");
        }

        graph.Write("worktrees/agent/a.toy", "class Kept\n");
        await graph.CommitAsync();

        foreach (var slot in never.Except(files))
        {
            graph.Write(slot + "/untracked.toy", "class Never\n");
        }

        await graph.SyncAsync();

        Assert.Contains("build/trees", never);
        Assert.All(files, file => Assert.Contains(file, never));
        Assert.Equal(["README.md", "worktrees/agent/a.toy"], graph.Manifest().Files.Keys);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:Never");
    }

    /// <summary>A repository inside the tree that git lists whole as untracked is not entered, as a submodule is not.</summary>
    [Fact]
    public async Task AnUntrackedRepositoryInsideTheTree_IsNotEntered()
    {
        using var graph = await Graph.CreateAsync();
        Directory.CreateDirectory(Path.Combine(graph.Tree, "inner"));
        await graph.Harness.InitializeGitRepositoryAsync(Path.Combine(graph.Tree, "inner"), TestContext.Current.CancellationToken);
        graph.Write("inner/x.toy", "class Inner\n");
        graph.Write("src/a.toy", "class A\n");

        var synced = await graph.SyncAsync();

        Assert.Equal(["README.md", "src/a.toy"], graph.Manifest().Files.Keys);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:Inner");
        Assert.Empty(synced.Skipped);
    }

    /// <summary>
    /// A path git keeps out of the checkout - marked skip-worktree, as a sparse checkout marks it - is graphed as its file
    /// stands, which git does not look at, and leaves the graph where its file is not there.
    /// </summary>
    [Fact]
    public async Task APathKeptOutOfTheCheckout_IsGraphedAsItsFileStands_AndNotWhereItIsAbsent()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        graph.Write("src/b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.RunGitAsync("update-index", "--skip-worktree", "src/b.toy");
        graph.Write("src/b.toy", "class B\nclass Changed\n");

        var present = await graph.SyncAsync();

        Assert.Equal(1, present.Hashed);
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:Changed");
        Assert.NotEqual((await graph.RunGitAsync("rev-parse", ":src/b.toy")).Trim(), graph.Manifest().Files["src/b.toy"].Blob);

        File.Delete(Path.Combine(graph.Tree, "src", "b.toy"));
        var absent = await graph.SyncAsync();

        Assert.Equal(["README.md", "src/a.toy"], graph.Manifest().Files.Keys);
        Assert.Equal(1, absent.Removed);
        Assert.DoesNotContain(graph.Symbols().Nodes, node => node.Id == "toy:B");
    }

    /// <summary>The shape's patterns compare names as the machine's file system does: without regard to case where it does, and with it where it does not.</summary>
    [Theory]
    [InlineData(StringComparison.OrdinalIgnoreCase)]
    [InlineData(StringComparison.Ordinal)]
    public async Task TheShapesPatterns_CompareNames_AsTheMachinesFileSystemDoes(StringComparison comparison)
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        var platform = Substitute.For<IHostPlatform>();
        platform.PathComparison.Returns(comparison);

        await graph.WithPlatform(platform).Shaped(CodebaseGraphShape.Of(["SRC/**"], null, null, null, null, null)).SyncAsync();

        string[] graphed = comparison == StringComparison.OrdinalIgnoreCase ? ["src/a.toy"] : [];
        Assert.Equal(graphed, graph.Manifest().Files.Keys);
    }

    /// <summary>
    /// A file kept unread for its state - binary, claimed by two - is listed too large once maxFileBytes is lowered under
    /// it, as a graph made afresh would list it.
    /// </summary>
    [Fact]
    public async Task AKeptFileOverALoweredLimit_IsListedTooLarge_WhateverItsState()
    {
        using var graph = await Graph.CreateAsync(shape: CodebaseGraphShape.Default with { MaxFileBytes = 1000 });
        graph.WriteBytes("bin.toy", [.. Enumerable.Repeat((byte)'a', 300), 0]);
        graph.Write("both.amb", new string('e', 300) + "\n");
        await graph.CommitAsync();
        await graph.SyncAsync();

        Assert.Equal((CodeFileStates.Binary, CodeFileStates.Ambiguous), (graph.Manifest().Files["bin.toy"].State, graph.Manifest().Files["both.amb"].State));

        await graph.Shaped(CodebaseGraphShape.Default with { MaxFileBytes = 200 }).SyncAsync();

        Assert.Equal((CodeFileStates.TooLarge, CodeFileStates.TooLarge), (graph.Manifest().Files["bin.toy"].State, graph.Manifest().Files["both.amb"].State));
    }

    /// <summary>A content test that fails on a file lists that file unreadable, naming its implementation and claim, and the sync goes on.</summary>
    [Fact]
    public async Task AContentTestThatFails_ListsItsFileUnreadable_AndTheSyncGoesOn()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.chk", "check\n");
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();

        var synced = await graph.Over(graph.Toy, new ListedExtractor(), new MarkdownExtractor(), new Checking()).SyncAsync();
        var failed = Assert.Single(synced.Unreadable);

        Assert.Equal(("a.chk", "its implementation 'checking' failed on it in the content test of its claim '.chk': no test of this"), (failed.Path, failed.Why));
        Assert.Contains(graph.Symbols().Nodes, node => node.Id == "toy:A");
    }

    /// <summary>A file whose name is not UTF-8, listed by the index and by status alike, is said once, unreadable, with why.</summary>
    [Fact]
    public async Task AFileWhoseNameIsNotUtf8_IsSaidOnce_AsUnreadable()
    {
        Assert.SkipUnless(OperatingSystem.IsLinux(), "a file named in no UTF-8 is made through Linux's file system alone.");

        using var graph = await Graph.CreateAsync();
        Shell(graph, "printf 'class A\\n' > \"$(printf 'caf\\351.toy')\"");
        await graph.CommitAsync();
        Shell(graph, "printf 'class B\\n' >> \"$(printf 'caf\\351.toy')\"");

        var synced = await graph.SyncAsync();
        var unreadable = Assert.Single(synced.Unreadable);

        Assert.Equal("its name is not UTF-8, and this tool opens a file only by a UTF-8 name", unreadable.Why);
        Assert.Equal(["README.md"], graph.Manifest().Files.Keys);

        static void Shell(Graph graph, string command)
        {
            using var shell = Process.Start(new ProcessStartInfo("sh", ["-c", command]) { WorkingDirectory = graph.Tree })!;
            shell.WaitForExit();
            Assert.Equal(0, shell.ExitCode);
        }
    }

    /// <summary>A tree, a run of it, and the sync of its graph over a registry the test chooses.</summary>
    private sealed class Graph : IDisposable
    {
        private readonly TempDirectory _temp;
        private readonly CancellationToken _token = TestContext.Current.CancellationToken;
        private CodeGraphExtractors _registry;
        private CodebaseGraphShape _shape;
        private IGitClient _git;
        private IHostPlatform _platform;
        private CodebaseGraphService _service;

        private Graph(TempDirectory temp, CodebaseGraphShape shape)
        {
            _temp = temp;
            _shape = shape;
            Layout = new HarnessLayout(temp.Path, temp.Path);
            Run = OrchestratorLayout.Of(Layout, "run");
            _registry = new CodeGraphExtractors([Toy, new ListedExtractor(), new MarkdownExtractor(), new Ambiguous("ambone"), new Ambiguous("ambtwo")]);
            _git = Harness.GitClient;
            _platform = Harness.Platform;
            _service = Service();
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

        public static async Task<Graph> CreateAsync(CodebaseGraphShape? shape = null)
        {
            var graph = new Graph(new TempDirectory(), shape ?? CodebaseGraphShape.Default);
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

        /// <summary>This graph synced over a registry of <paramref name="extractors"/> from now on.</summary>
        public Graph Over(params ICodeGraphExtractor[] extractors)
        {
            _registry = new CodeGraphExtractors(extractors);
            _service = Service();
            return this;
        }

        /// <summary>This graph synced through <paramref name="git"/> from now on.</summary>
        public Graph WithGit(IGitClient git)
        {
            _git = git;
            _service = Service();
            return this;
        }

        /// <summary>This graph synced on <paramref name="platform"/> from now on.</summary>
        public Graph WithPlatform(IHostPlatform platform)
        {
            _platform = platform;
            _service = Service();
            return this;
        }

        /// <summary>This graph synced in <paramref name="shape"/> from now on.</summary>
        public Graph Shaped(CodebaseGraphShape shape)
        {
            _shape = shape;
            return this;
        }

        /// <summary>This graph's syncs made for the run <paramref name="name"/> from now on: a run the store lists.</summary>
        public Graph ForRun(string name)
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
            return await _service.SyncAsync(new CodebaseGraphSync(Layout, Run, _shape, WorktreesRoot, rebuild, "graph#1"), _token);
        }

        public CodeGraphManifest Manifest() => Store.ReadGraphManifest(Run)!;

        public CodeGraphSymbolsFile Symbols() => Store.ReadGraphSymbols(Run)!;

        public IReadOnlyList<string> Extracts()
            => Directory.Exists(Run.GraphExtractsDirectory)
                ? [.. Directory.EnumerateFiles(Run.GraphExtractsDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName).Order(StringComparer.Ordinal)!]
                : [];

        public IReadOnlyList<string> SyncLines() => File.ReadAllLines(Run.GraphSyncFile);

        public void Dispose() => _temp.Dispose();

        private CodebaseGraphService Service()
            => new(_git, Harness.FileSystem, Store, _registry, _platform, new RunningToolDouble("9.9.9"), Output, Clock);
    }

    /// <summary>
    /// A toy language: each <c>class X</c> line declares the type <c>X</c>, each <c>call Y</c> line calls <c>Y</c> from the
    /// type above it, a first line <c>// generated</c> marks the file generated, a file that says <c>boom</c> fails it, and
    /// a line <c>wrong</c> declares a type whose lines are not the file's.
    /// </summary>
    private sealed class ToyExtractor : ICodeGraphExtractor
    {
        private int _reads;

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
    private sealed class Checking : ICodeGraphExtractor
    {
        public string Language => "checking";

        public int Version => 1;

        public CodeGraphClaims Claims { get; } = new([], [new CodeGraphClaim(".chk", _ => throw new InvalidOperationException("no test of this."))]);

        public bool Outlines => false;

        public CodeGraphExtractorHelp Help { get; } = new("chk files", []);

        public FileExtract Extract(CodeFileInput input) => FileExtract.Empty;
    }

    /// <summary>One of two implementations that claim <c>.amb</c> and both say yes to every file.</summary>
    private sealed class Ambiguous(string language) : ICodeGraphExtractor
    {
        public string Language => language;

        public int Version => 1;

        public CodeGraphClaims Claims { get; } = new([], [new CodeGraphClaim(".amb", _ => true)]);

        public bool Outlines => false;

        public CodeGraphExtractorHelp Help { get; } = new("amb files", []);

        public FileExtract Extract(CodeFileInput input) => FileExtract.Empty;
    }
}
