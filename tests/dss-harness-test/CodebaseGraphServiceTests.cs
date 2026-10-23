using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using DssHarness.Core.Codebase;
using DssHarness.Core.Codebase.Languages;
using DssHarness.Core.FileSystem;
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

        // What only the manifest that does not read named is named by no record once it is written again - an extract
        // of it, as the stray stands for, goes with the sweep, though this process synced the graph before.
        var stray = Path.Combine(graph.Run.GraphExtractsDirectory, "ab", "stray.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "{}");

        var synced = await graph.SyncAsync();

        Assert.False(File.Exists(stray));
        Assert.Equal(2, synced.Extracted);
        Assert.StartsWith("its manifest did not read, and a graph is never guessed at: ", synced.Rebuilt, StringComparison.Ordinal);
        Assert.Contains("schema is 99", synced.Rebuilt, StringComparison.Ordinal);
        Assert.Contains("\"rebuilt\":", graph.SyncLines()[^1], StringComparison.Ordinal);
    }

    /// <summary>
    /// A link result another linker's revision made is linked again by the next process, though no file changed, and no
    /// file is read again: the manifest still reads, and only the link result names the linker.
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

        var synced = await graph.Restarted().SyncAsync();

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
    /// An extract no record names - what a crash or an earlier state left - is removed by a process's first sync, of a tree
    /// that did not change too; then by a sync that wrote a record no longer naming one it named, and never looked for by
    /// a sync that did not.
    /// </summary>
    [Fact]
    public async Task AnExtractNoRecordNames_IsRemoved_ByAProcesssFirstSync_AndByOneThatUnnamedAnExtract()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("src/a.toy", "class A\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        var stray = Path.Combine(graph.Run.GraphExtractsDirectory, "ab", "stray.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stray)!);
        File.WriteAllText(stray, "{}");

        var unchanged = await graph.Restarted().SyncAsync();

        Assert.Equal(0, unchanged.Extracted);
        Assert.False(File.Exists(stray));

        File.WriteAllText(stray, "{}");
        graph.Write("src/b.toy", "class B\n");
        await graph.SyncAsync();

        Assert.True(File.Exists(stray));
        Assert.Equal(4, graph.Extracts().Count);

        graph.Write("src/a.toy", "class A\nclass A2\n");
        await graph.SyncAsync();

        Assert.False(File.Exists(stray));
        Assert.Equal(3, graph.Extracts().Count);
    }

    /// <summary>
    /// An extract that does not read is made again from its file while linking, never linked as it is, by a process that
    /// holds no link of the graph: one that does links from what it holds, and reads no extract back.
    /// </summary>
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
        var held = await graph.SyncAsync();

        Assert.Equal((1, 2, 3), (held.Extracted, held.Reused, held.Files));

        graph.Write("src/b.toy", "class B\nclass D\n");
        var synced = await graph.Restarted().SyncAsync();

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

    /// <summary>
    /// An agent's view is held against the base - synced first where the run has none - as the overlay of each file its
    /// worktree holds otherwise and each of the base's it does not hold: no file is read whose extract the base made, and a
    /// sync of a worktree that did not change reads none; the base keeps what the worktree deleted, and the view's link
    /// holds the worktree's files alone.
    /// </summary>
    [Fact]
    public async Task AnAgentsView_HoldsWhatItsWorktreeHoldsOtherwise_AndReadsNoFileTheBaseRead()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\ncall B\n");
        graph.Write("b.toy", "class B\n");
        graph.Write("c.toy", "class C\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        Graph.WriteIn(worktree, "b.toy", "class B\nclass Bee\n");
        Graph.WriteIn(worktree, "d.toy", "class D\ncall A\n");
        File.Delete(Path.Combine(worktree, "c.toy"));

        var synced = await graph.SyncViewAsync("w", worktree);
        var view = graph.View("w");
        var again = await graph.SyncViewAsync("w", worktree);
        var read = (await graph.CheckAsync("w", worktree, "d.toy")).Read;

        // The repository's README is the base's, and the view's alike.
        Assert.Equal(("w", 4, 2, 2), (synced.View, synced.Files, synced.Extracted, synced.Reused));

        // Linked again from the base's link: d's call alone is found - b's new name is named by no reference.
        Assert.Equal(1, synced.Resolved);
        Assert.Equal(5, graph.Toy.Reads);
        Assert.Equal((graph.Manifest().Digest, worktree, "w"), (view.Base, view.Tree, view.Agent));
        Assert.Equal(graph.Manifest().Digest, synced.Base);
        Assert.Equal(["b.toy", "d.toy"], view.Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["c.toy"], view.Deleted);
        Assert.Contains("c.toy", graph.Manifest().Files.Keys);
        Assert.Equal((0, 4), (again.Extracted, again.Reused));
        Assert.Equal(5, graph.Toy.Reads);
        Assert.Contains(read.Symbols.Edges, edge => edge is { From: "toy:D", To: "toy:A" });
        Assert.Contains(read.Symbols.Nodes, node => node.Id == "toy:Bee");
        Assert.DoesNotContain(read.Symbols.Nodes, node => node.Id == "toy:C");
        Assert.Equal(["base", "w", "w"], graph.SyncLines().Select(line => (string)System.Text.Json.Nodes.JsonNode.Parse(line)!["view"]!));
    }

    /// <summary>
    /// A view is held against a base that moved without a file read: each file of its worktree the new base holds alike
    /// leaves its overlay, each the base changed and the worktree did not joins it with its earlier extract, which the sweep
    /// keeps - and the next sync of the view, in this process or one begun again, reads no file whose extract exists.
    /// </summary>
    [Fact]
    public async Task AViewIsHeldAgainstABaseThatMoved_AndReadsNoFileWhoseExtractExists()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        Graph.WriteIn(worktree, "b.toy", "class B\nclass Bee\n");
        await graph.SyncViewAsync("w", worktree);
        var before = graph.Manifest();

        graph.Write("b.toy", "class B\nclass Bee\n");
        graph.Write("a.toy", "class A\ncall B\n");
        graph.Write("e.toy", "class E\n");
        var moved = await graph.SyncAsync();
        var view = graph.View("w");

        Assert.Equal(3, moved.Extracted);
        Assert.Equal(6, graph.Toy.Reads);
        Assert.NotEqual(before.Digest, view.Base);
        Assert.Equal(graph.Manifest().Digest, view.Base);
        Assert.Equal(["a.toy"], view.Files.Keys);
        Assert.Equal(before.Files["a.toy"], view.Files["a.toy"]);
        Assert.Equal(["e.toy"], view.Deleted);
        Assert.True(File.Exists(graph.Run.GraphExtractFile(view.Files["a.toy"].Extract!)));

        var synced = await graph.SyncViewAsync("w", worktree);
        var restarted = await graph.Restarted().SyncViewAsync("w", worktree);

        Assert.Equal((0, 3), (synced.Extracted, synced.Reused));
        Assert.Equal((0, 3), (restarted.Extracted, restarted.Reused));
        Assert.Equal(6, graph.Toy.Reads);
        Assert.Equal(view.Base, graph.View("w").Base);
        Assert.Equal(["a.toy"], graph.View("w").Files.Keys);
    }

    /// <summary>
    /// Syncs of one view wait for each other by its gate, and syncs of different views and of the base run side by side -
    /// each listing held open long enough for another to begin - each view on one snapshot of the base: a base that moved
    /// among them leaves every view held against the manifest it wrote, whatever their order.
    /// </summary>
    [Fact]
    public async Task SyncsOfOneView_WaitForEachOther_AndOfDifferentViews_RunSideBySide()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        await graph.CommitAsync();
        var one = await graph.WorktreeAsync("one");
        var two = await graph.WorktreeAsync("two");
        Graph.WriteIn(one, "b.toy", "class B\n");
        Graph.WriteIn(two, "c.toy", "class C\n");
        await graph.SyncAsync();
        graph.Write("a.toy", "class A\ncall B\n");

        var real = graph.Harness.GitClient;
        var git = Substitute.For<IGitClient>();
        var gate = new object();
        var listing = new Dictionary<string, int>(StringComparer.Ordinal);
        var mostOfOne = 0;
        var most = 0;

        git.ListIndexedFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var tree = call.ArgAt<string>(0);

            lock (gate)
            {
                listing[tree] = listing.GetValueOrDefault(tree) + 1;
                mostOfOne = Math.Max(mostOfOne, listing[tree]);
                most = Math.Max(most, listing.Values.Sum());
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), call.ArgAt<CancellationToken>(1));
            return await real.ListIndexedFilesAsync(tree, call.ArgAt<CancellationToken>(1));
        });
        git.ListWorkingFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => real.ListWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        git.HashWorkingFilesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(call => real.HashWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<string>>(1), call.ArgAt<CancellationToken>(2)));
        git.ResolveCommitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var head = await real.ResolveCommitAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2));

            lock (gate)
            {
                listing[call.ArgAt<string>(0)]--;
            }

            return head;
        });

        graph.WithGit(git);
        await Task.WhenAll(graph.SyncViewAsync("one", one), graph.SyncViewAsync("one", one), graph.SyncViewAsync("two", two), graph.SyncViewAsync("two", two), graph.SyncAsync());

        Assert.Equal(1, mostOfOne);
        Assert.True(most > 1, $"at most {most} listing(s) at once");
        Assert.All(new[] { "one", "two" }, agent => Assert.Equal(graph.Manifest().Digest, graph.View(agent).Base));
        Assert.Equal(["a.toy", "b.toy"], graph.View("one").Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["a.toy", "c.toy"], graph.View("two").Files.Keys.Order(StringComparer.Ordinal));
        Assert.All(new[] { "one", "two" }, agent => Assert.Empty(graph.View(agent).Deleted));
    }

    /// <summary>
    /// A base synced by paths asks git of those paths alone: each changed one read again, each gone one taken out, a path git
    /// holds nothing of counted and left - and a file it was not given kept as the graph had it, though it changed; a view
    /// held against the base after. A run with no graph is not brought up to date: nothing is made.
    /// </summary>
    [Fact]
    public async Task ABaseSyncedByPaths_TakesThosePathsAlone()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        graph.Write("c.toy", "class C\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");

        Assert.Null(await graph.SyncPathsAsync("a.toy"));
        Assert.False(File.Exists(graph.Run.GraphManifestFile));

        await graph.SyncViewAsync("w", worktree);
        var b = graph.Manifest().Files["b.toy"];
        graph.Write("a.toy", "class A\ncall B\n");
        graph.Write("b.toy", "class B\nclass Bee\n");
        graph.Write("e.toy", "class E\n");
        File.Delete(Path.Combine(graph.Tree, "c.toy"));

        var synced = (await graph.SyncPathsAsync("a.toy", "c.toy", "e.toy", "gone.toy"))!;

        Assert.Equal((4, 2, 1, 2), (synced.Listed, synced.Extracted, synced.Removed, synced.Hashed));
        Assert.Equal(5, graph.Toy.Reads);
        Assert.Equal(["README.md", "a.toy", "b.toy", "e.toy"], graph.Manifest().Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(b, graph.Manifest().Files["b.toy"]);
        Assert.True(graph.Manifest().Dirty);
        Assert.Contains(graph.Symbols().Edges, edge => edge is { From: "toy:A", To: "toy:B" });
        Assert.Equal(graph.Manifest().Digest, graph.View("w").Base);
        Assert.Equal(["a.toy", "c.toy"], graph.View("w").Files.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(["e.toy"], graph.View("w").Deleted);
    }

    /// <summary>
    /// The check of the files an answer names stats each against what the view recorded, hashes only one whose size or time
    /// moved, and reads again one whose blob moved before it answers: a file changed under a view between two answers is
    /// answered the second time with its new span and blob, never the old; one whose time alone moved is hashed once, and
    /// one gone leaves the view - and the base's own check holds every view against what it moved.
    /// </summary>
    [Fact]
    public async Task TheCheckOfTheFilesAnAnswerNames_AnswersEachAsItIs()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        Graph.WriteIn(worktree, "a.toy", "class A\nclass Aye\n");
        await graph.SyncViewAsync("w", worktree);

        var first = await graph.CheckAsync("w", worktree, "a.toy", "b.toy", "none.toy");
        Graph.WriteIn(worktree, "a.toy", "\n\nclass A\n");
        File.SetLastWriteTimeUtc(Path.Combine(worktree, "b.toy"), DateTime.UtcNow.AddMinutes(5));
        var second = await graph.CheckAsync("w", worktree, "a.toy", "b.toy");
        var third = await graph.CheckAsync("w", worktree, "a.toy", "b.toy");

        Assert.Equal((2, 1, 0, 0), (first.Checked, first.Hashed, first.Extracted, first.Removed));
        Assert.Equal(2, first.Read.Extract("a.toy")!.Nodes.Count(node => node.Kind == CodeNodeKinds.Type));
        Assert.Equal((2, 2, 1, 0), (second.Checked, second.Hashed, second.Extracted, second.Removed));
        Assert.Equal((2, 0, 0, 0), (third.Checked, third.Hashed, third.Extracted, third.Removed));

        var blob = (await graph.Harness.RunGitAsync(worktree, ["hash-object", "a.toy"], TestContext.Current.CancellationToken)).StandardOutput.Trim();

        Assert.All(new[] { second, third }, made => Assert.Equal((3, blob), (made.Read.Extract("a.toy")!.Nodes.Single(node => node.Name == "A").Start, made.Read.Files["a.toy"].Blob)));
        Assert.DoesNotContain(third.Read.Symbols.Nodes, node => node.Id == "toy:Aye");
        Assert.Equal(blob, graph.View("w").Files["a.toy"].Blob);
        Assert.Equal(4, graph.Toy.Reads);

        File.Delete(Path.Combine(worktree, "b.toy"));
        var gone = await graph.CheckAsync("w", worktree, "b.toy");

        Assert.Equal((1, 0, 0, 1), (gone.Checked, gone.Hashed, gone.Extracted, gone.Removed));
        Assert.Equal(["b.toy"], graph.View("w").Deleted);

        graph.Write("a.toy", "class A\ncall B\n");
        var own = await graph.CheckAsync(null, null, "a.toy");

        Assert.Equal((1, 1, 1, 0), (own.Checked, own.Hashed, own.Extracted, own.Removed));
        Assert.Contains(own.Read.Symbols.Edges, edge => edge is { From: "toy:A", To: "toy:B" });
        Assert.Equal(graph.Manifest().Digest, graph.View("w").Base);
    }

    /// <summary>
    /// A view a process begun again did not link is answered from its own link: the check of a file that did not move holds
    /// what the view holds, never what the base does, and reads no file again.
    /// </summary>
    [Fact]
    public async Task ACheckOfAViewTheProcessDidNotLink_AnswersFromTheViewsOwnLink()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        await graph.SyncAsync();
        Graph.WriteIn(worktree, "a.toy", "class A\nclass Aye\n");
        await graph.SyncViewAsync("w", worktree);
        var reads = graph.Toy.Reads;

        var check = await graph.Restarted().CheckAsync("w", worktree, "b.toy");

        Assert.Equal((1, 0, 0), (check.Checked, check.Extracted, check.Removed));
        Assert.Contains(check.Read.Symbols.Nodes, node => node.Id == "toy:Aye");
        Assert.Equal(reads, graph.Toy.Reads);
    }

    /// <summary>
    /// A view's file whose content goes back to the base's is taken from the base by the next check, its file not read
    /// again: the view holds it as the base does.
    /// </summary>
    [Fact]
    public async Task AViewsFileThatGoesBackToTheBases_IsTakenFromTheBase_AtItsCheck()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        await graph.SyncAsync();
        Graph.WriteIn(worktree, "a.toy", "class A\nclass Aye\n");
        await graph.SyncViewAsync("w", worktree);
        var reads = graph.Toy.Reads;
        Graph.WriteIn(worktree, "a.toy", "class A\n");

        var check = await graph.CheckAsync("w", worktree, "a.toy");

        Assert.Equal((1, 1, 0), (check.Checked, check.Hashed, check.Extracted));
        Assert.Equal(reads, graph.Toy.Reads);
        Assert.DoesNotContain("a.toy", graph.View("w").Files.Keys);
        Assert.DoesNotContain(check.Read.Symbols.Nodes, node => node.Id == "toy:Aye");
    }

    /// <summary>The sync log's lines appended at once by many syncs are each written whole, and none is lost.</summary>
    [Fact]
    public async Task SyncLinesAppendedAtOnce_AreEachWrittenWhole()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        var before = graph.SyncLines().Count;

        Parallel.For(0, 64, at => graph.Store.AppendGraphSync(graph.Run, new CodeGraphSyncLine(DateTimeOffset.UnixEpoch, $"v{at}", "test", 0, 0, 0, 0, 0, 0)));

        Assert.Equal(before + 64, graph.SyncLines().Count);
        Assert.All(graph.SyncLines(), line => Assert.NotNull(System.Text.Json.Nodes.JsonNode.Parse(line)));
    }

    /// <summary>
    /// A link or a submodule the graph lists without a read is no file a check looks at: neither is hashed nor taken out,
    /// though the submodule stands as a directory and the link's file holds other text than git's.
    /// </summary>
    [Fact]
    public async Task TheCheck_LooksAtNoLinkNorSubmodule()
    {
        using var graph = await Graph.CreateAsync();
        var head = await graph.HeadAsync();
        graph.Write("linked", "target.toy");
        await graph.RunGitAsync("config", "core.symlinks", "false");
        await graph.CommitAsync();
        var blob = (await graph.RunGitAsync("hash-object", "-w", "linked")).Trim();
        await graph.RunGitAsync("rm", "--cached", "--quiet", "linked");
        await graph.RunGitAsync("update-index", "--add", "--cacheinfo", $"120000,{blob},linked");
        await graph.RunGitAsync("update-index", "--add", "--cacheinfo", $"160000,{head},vendor/sub");
        Directory.CreateDirectory(Path.Combine(graph.Tree, "vendor", "sub"));
        await graph.RunGitAsync("commit", "--quiet", "-m", "link and submodule");
        await graph.SyncAsync();
        graph.Write("linked", "elsewhere.toy");

        var check = await graph.CheckAsync(null, null, "linked", "vendor/sub");

        Assert.Equal((0, 0, 0), (check.Checked, check.Hashed, check.Removed));
        Assert.Equal((CodeFileStates.Link, CodeFileStates.Submodule), (graph.Manifest().Files["linked"].State, graph.Manifest().Files["vendor/sub"].State));
    }

    /// <summary>
    /// A file gone between its check's look at it and its hash is never given to git to hash: it leaves the tree, as one
    /// gone before the look does.
    /// </summary>
    [Fact]
    public async Task AFileGoneBetweenItsLookAndItsHash_LeavesTheTree()
    {
        using var graph = await Graph.CreateAsync();
        var (files, watch) = Watched.Over(graph.Harness.FileSystem);
        graph.WithFiles(files);
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        await graph.SyncAsync();
        graph.Write("b.toy", "class B\nclass Bee\n");
        watch.After = (method, args) =>
        {
            if (method == nameof(IFileSystem.StandingOf) && args?[0] is string path && Path.GetFileName(path) == "b.toy")
            {
                File.Delete(path);
            }
        };

        var check = await graph.CheckAsync(null, null, "a.toy", "b.toy");

        Assert.Equal((2, 0, 0, 1), (check.Checked, check.Hashed, check.Extracted, check.Removed));
        Assert.DoesNotContain("b.toy", graph.Manifest().Files.Keys);
    }

    /// <summary>
    /// What a sync that did not end read is on disk, and no record names it: the sweep at its end removes it, though no
    /// record it wrote named less than the one before; and the process holds nothing of the graph after it, so the next
    /// sync takes the tree as its records say and links it whole.
    /// </summary>
    [Fact]
    public async Task WhatASyncThatDidNotEndRead_IsRemovedAtItsEnd_AndTheNextTakesTheRecords()
    {
        using var graph = await Graph.CreateAsync();
        var (files, watch) = Watched.Over(graph.Harness.FileSystem);
        graph.WithFiles(files);
        graph.Write("a.toy", "class A\n");
        graph.Write("b.toy", "class B\n");
        graph.Write("c.toy", "class C\ncall D\n");
        graph.Write("d.toy", "class D\n");
        await graph.CommitAsync();
        var first = await graph.SyncAsync();
        var held = graph.Extracts();
        var made = 0;
        watch.Before = (method, args) =>
        {
            if (method == nameof(IFileSystem.CreateDirectory) && args?[0] is string directory
                && directory.StartsWith(graph.Run.GraphExtractsDirectory, StringComparison.Ordinal)
                && Interlocked.Increment(ref made) == 2)
            {
                throw new IOException("the disk is full");
            }
        };
        graph.Write("a.toy", "class A\nclass Aye\n");
        graph.Write("b.toy", "class B\nclass Bee\n");

        await Assert.ThrowsAsync<IOException>(() => graph.SyncAsync());

        Assert.Equal(held, graph.Extracts());

        watch.Before = null;
        var again = await graph.SyncAsync();

        Assert.True(first.Resolved > 0);
        Assert.Equal(first.Resolved, again.Resolved);
    }

    /// <summary>
    /// A sync links again only what its change could have changed, in the process that linked the tree before: the first
    /// finds every reference; one of a file whose calls alone changed finds its own again; one that adds a name finds again
    /// the reference naming it - each as its sync line says; and a process begun again over a tree that did not change
    /// links nothing.
    /// </summary>
    [Fact]
    public async Task ASync_LinksAgainOnlyWhatItsChangeCouldHaveChanged()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\ncall B\ncall Fresh\n");
        graph.Write("b.toy", "class B\ncall A\n");
        graph.Write("c.toy", "class C\ncall A\ncall B\n");
        await graph.CommitAsync();

        var whole = await graph.SyncAsync();
        graph.Write("c.toy", "class C\ncall B\n");
        var own = await graph.SyncAsync();
        graph.Write("fresh.toy", "class Fresh\n");
        var named = await graph.SyncAsync();
        var restarted = await graph.Restarted().SyncAsync();

        Assert.Equal((5, 1, 1), (whole.Resolved, own.Resolved, named.Resolved));
        Assert.Null(restarted.Resolved);
        Assert.Contains("\"resolved\":5", graph.SyncLines()[0], StringComparison.Ordinal);
        Assert.Contains("\"resolved\":1", graph.SyncLines()[1], StringComparison.Ordinal);
        Assert.DoesNotContain("\"resolved\"", graph.SyncLines()[3], StringComparison.Ordinal);
        Assert.Contains(graph.Symbols().Edges, edge => edge is { From: "toy:A", To: "toy:Fresh" });
        Assert.DoesNotContain(graph.Symbols().Edges, edge => edge is { From: "toy:C", To: "toy:A" });
    }

    /// <summary>A view dropped leaves the graph, and what only it named goes with the sync that ends it; the base keeps its own.</summary>
    [Fact]
    public async Task ADroppedView_LeavesTheGraph_AndWhatOnlyItNamedGoes()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        Graph.WriteIn(worktree, "b.toy", "class B\nclass Bee\n");
        await graph.SyncViewAsync("w", worktree);
        var own = graph.View("w").Files["b.toy"].Extract!;

        await graph.DropAsync("w");

        Assert.Empty(graph.Store.GraphViews(graph.Run));
        Assert.False(File.Exists(graph.Run.GraphExtractFile(own)));
        Assert.True(File.Exists(graph.Run.GraphExtractFile(graph.Manifest().Files["b.toy"].Extract!)));

        // Made again, the view reads its file again: nothing of it was kept.
        Assert.Equal(1, (await graph.SyncViewAsync("w", worktree)).Extracted);
    }

    /// <summary>
    /// A file a view took from the base is no file the view read: its first check hashes it whatever its size and time say,
    /// though both are the base's file's - and a content that differs is read again.
    /// </summary>
    [Fact]
    public async Task AFileAViewTookFromTheBase_IsHashedAtItsFirstCheck_WhateverItsSizeAndTime()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("b.toy", "class B\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        await graph.SyncViewAsync("w", worktree);

        Graph.WriteIn(worktree, "b.toy", "class Q\n");
        File.SetLastWriteTimeUtc(Path.Combine(worktree, "b.toy"), File.GetLastWriteTimeUtc(Path.Combine(graph.Tree, "b.toy")));
        var check = await graph.CheckAsync("w", worktree, "b.toy");

        Assert.Equal((1, 1, 1), (check.Checked, check.Hashed, check.Extracted));
        Assert.Contains(check.Read.Symbols.Nodes, node => node.Id == "toy:Q");
    }

    /// <summary>
    /// A view's sync reads the base without the base's gate: one that read the base's manifest before a base sync moved it
    /// never puts that older base back as the one the process holds, so the next sync of the base starts from what the
    /// moved one left.
    /// </summary>
    [Fact]
    public async Task AViewThatReadTheBaseBeforeItMoved_NeverPutsTheOlderBaseBack()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        graph.Write("c.toy", "class C\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        await graph.SyncAsync();
        graph.Write("a.toy", "class A\nclass Aye\n");

        var (files, watch) = Watched.Over(graph.Harness.FileSystem);
        using var read = new ManualResetEventSlim();
        using var moved = new ManualResetEventSlim();
        var armed = 1;

        // The view opens the manifest and is held there until the base's sync has moved it and said so: it reads the older
        // manifest through the handle it holds.
        watch.After = (method, arguments) =>
        {
            if (method == nameof(IFileSystem.OpenRead) && arguments?[0] as string == graph.Run.GraphManifestFile && Interlocked.Exchange(ref armed, 0) == 1)
            {
                read.Set();
                Assert.True(moved.Wait(TimeSpan.FromSeconds(30)));
            }
        };
        watch.Before = (method, arguments) =>
        {
            if (method == nameof(IFileSystem.OpenForAppend) && read.IsSet)
            {
                moved.Set();
            }
        };

        graph.WithFiles(files);
        var view = Task.Run(() => graph.SyncViewAsync("w", worktree), TestContext.Current.CancellationToken);
        Assert.True(read.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        await graph.SyncAsync();
        await view;
        await graph.SyncPathsAsync("c.toy");

        var blob = (await graph.RunGitAsync("hash-object", "a.toy")).Trim();

        Assert.Equal(blob, graph.Manifest().Files["a.toy"].Blob);
        Assert.Equal(graph.Manifest().Digest, graph.View("w").Base);
    }

    /// <summary>
    /// A view whose first sync is under way when the base moves - no record of it written yet - is held against the moved
    /// base all the same: the base's sync waits for its gate, and holds it against the new manifest once it is written.
    /// </summary>
    [Fact]
    public async Task AViewWhoseFirstSyncIsUnderWay_IsHeldAgainstTheBaseThatMovedMeanwhile()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        Graph.WriteIn(worktree, "b.toy", "class B\n");
        await graph.SyncAsync();
        graph.Write("a.toy", "class A\ncall B\n");

        var real = graph.Harness.GitClient;
        var git = Substitute.For<IGitClient>();
        var listed = new TaskCompletionSource();

        // The view lists its worktree once the base has listed the main tree, and hashes it slowly: its record is written
        // after the base's sync has moved the base.
        git.ListIndexedFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.ArgAt<string>(0) == worktree)
            {
                await listed.Task.WaitAsync(TimeSpan.FromSeconds(30), call.ArgAt<CancellationToken>(1));
            }

            return await real.ListIndexedFilesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1));
        });
        git.ListWorkingFilesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => real.ListWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<CancellationToken>(1)));
        git.ResolveCommitAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var head = await real.ResolveCommitAsync(call.ArgAt<string>(0), call.ArgAt<string>(1), call.ArgAt<CancellationToken>(2));

            if (call.ArgAt<string>(0) == graph.Tree)
            {
                listed.TrySetResult();
            }

            return head;
        });
        git.HashWorkingFilesAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (call.ArgAt<string>(0) == worktree)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400), call.ArgAt<CancellationToken>(2));
            }

            return await real.HashWorkingFilesAsync(call.ArgAt<string>(0), call.ArgAt<IReadOnlyList<string>>(1), call.ArgAt<CancellationToken>(2));
        });

        graph.WithGit(git);
        await Task.WhenAll(graph.SyncViewAsync("w", worktree), graph.SyncAsync());

        Assert.Equal(graph.Manifest().Digest, graph.View("w").Base);
        Assert.Equal(["a.toy", "b.toy"], graph.View("w").Files.Keys.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Only the last sync to end with none under way removes what no record names: a sync that ends while a view's sync
    /// has written an extract its view does not name yet leaves it, and the view names it once written.
    /// </summary>
    [Fact]
    public async Task ASyncThatEndsWhileAnotherIsUnderWay_RemovesNothingItHasWritten()
    {
        using var graph = await Graph.CreateAsync();
        graph.Write("a.toy", "class A\n");
        await graph.CommitAsync();
        var worktree = await graph.WorktreeAsync("w");
        var other = await graph.WorktreeAsync("v");
        Graph.WriteIn(worktree, "x.toy", "class X\n");
        Graph.WriteIn(worktree, "y.toy", "class Y\nhold\n");
        await graph.SyncAsync();
        var held = graph.Extracts().Count;

        using var gate = new ManualResetEventSlim();
        graph.Toy.Gate = gate;
        var view = Task.Run(() => graph.SyncViewAsync("w", worktree), TestContext.Current.CancellationToken);
        Assert.True(graph.Toy.Holding.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        // x's extract is written before y is read, which waits: the first sync of another view ends meanwhile - one that
        // replaced a record it did not hold, after which a sweep is owed, and that waits for no other view's gate.
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (graph.Extracts().Count <= held && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await graph.SyncViewAsync("v", other);
        gate.Set();
        await view;

        Assert.True(File.Exists(graph.Run.GraphExtractFile(graph.View("w").Files["x.toy"].Extract!)));
        Assert.True(File.Exists(graph.Run.GraphExtractFile(graph.View("w").Files["y.toy"].Extract!)));
    }

    /// <summary>A file system that hands every call to another, and lets a test act before and after each.</summary>
    public class Watched : System.Reflection.DispatchProxy
    {
        private IFileSystem _inner = null!;

        /// <summary>Called before each call, with its method's name and its arguments.</summary>
        public Action<string, object?[]?>? Before { get; set; }

        /// <summary>Called after each call that returned, with its method's name and its arguments.</summary>
        public Action<string, object?[]?>? After { get; set; }

        public static (IFileSystem Files, Watched Watch) Over(IFileSystem inner)
        {
            var files = Create<IFileSystem, Watched>();
            var watch = (Watched)(object)files;
            watch._inner = inner;
            return (files, watch);
        }

        protected override object? Invoke(System.Reflection.MethodInfo? targetMethod, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(targetMethod);
            Before?.Invoke(targetMethod.Name, args);

            try
            {
                var result = targetMethod.Invoke(_inner, args);
                After?.Invoke(targetMethod.Name, args);
                return result;
            }
            catch (System.Reflection.TargetInvocationException ex) when (ex.InnerException is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
                throw;
            }
        }
    }

    /// <summary>A tree, a run of it, and the sync of its graph over a registry the test chooses.</summary>
    private sealed class Graph : IDisposable
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

        /// <summary>This graph synced by a service that holds nothing of it from now on, as a process begun again is.</summary>
        public Graph Restarted()
        {
            _service = Service();
            return this;
        }

        /// <summary>This graph synced by a service that holds nothing of it, over <paramref name="files"/>, from now on.</summary>
        public Graph WithFiles(IFileSystem files)
        {
            _files = files;
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

        private CodebaseGraphSync Request(string? agent = null, string? worktree = null, bool rebuild = false)
            => new(Layout, Run, _shape, WorktreesRoot, rebuild, "graph#1") { Agent = agent, Worktree = worktree };

        private CodebaseGraphService Service()
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
    private sealed class ToyExtractor : ICodeGraphExtractor
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
