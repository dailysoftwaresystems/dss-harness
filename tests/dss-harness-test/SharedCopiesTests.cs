using System.Text.Json.Nodes;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// The bytes of every file an agent shares with the main tree, kept beside its seed under the digest the seed records:
/// written at every seed, refresh and fold, pruned with the seed, removed with the agent, and never a reason a command
/// fails.
/// </summary>
public sealed class SharedCopiesTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Whatever records a path as shared - the hand-over that makes an agent, a fold, a refresh - keeps the bytes recorded,
    /// in a file named by their digest: one file for each digest, however many paths hold it.
    /// </summary>
    [Fact]
    public async Task EveryPathRecordedAsShared_HasItsBytesKeptUnderItsDigest_OnceForEachDigest()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "the same\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "the same\n");
        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit\n");

        var worktree = await kit.CreateAgentAsync("ag");

        Assert.Equal(new[] { Digest("the same\n"), Digest("x\nmain edit\n") }.Order(StringComparer.Ordinal), await KeptAsync(kit, "ag"));
        Assert.Equal(3, kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Count);

        OrchestrationKit.Write(worktree, "new.txt", "the agent's\n");
        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains(Digest("the agent's\n"), await KeptAsync(kit, "ag"));

        OrchestrationKit.Write(kit.Main, "docs/x.md", "x\nmain edit again\n");
        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("docs"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains(Digest("x\nmain edit again\n"), await KeptAsync(kit, "ag"));
    }

    /// <summary>
    /// The kept copies are pruned to what the seed names once the seed is written: the bytes a path no longer shares go,
    /// and so does what a copy that stopped part way left behind.
    /// </summary>
    [Fact]
    public async Task ACopyTheSeedNoLongerNames_IsPrunedWhenTheSeedIsWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");
        var before = kit.Layout.SharedFile("ag", Digest("one\nmain edit\n"));
        var litter = Path.Combine(kit.Layout.SharedDirectory("ag"), Digest("left\n") + ".tmp-0a1b2c3d");
        File.WriteAllText(litter, "left\n");

        Assert.True(File.Exists(before));

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");
        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal([Digest("one\nmain edit again\n")], await KeptAsync(kit, "ag"));
        Assert.False(File.Exists(before));
        Assert.False(File.Exists(litter));
    }

    /// <summary>
    /// A copy that cannot be kept - a full disk - never fails the command that recorded the path: the path is recorded as
    /// shared all the same, and a line says its bytes were not kept, at a hand-over and at a fold alike.
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBeKept_IsSaid_AndThePathIsStillRecorded()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var shared = kit.Layout.SharedDirectory("ag");
        var agents = kit.Harness.Agents(new NoRoomFileSystem(kit.Harness.FileSystem, shared), kit.Harness.AnchorRegistryService);

        var created = await agents.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.Contains($"'a.txt' is recorded as shared, and its bytes were not kept in '{shared}': There is not enough space on the disk", created.Details!);
        Assert.Equal(["a.txt"], kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Keys);
        Assert.Empty(await KeptAsync(kit, "ag", proved: false));

        OrchestrationKit.Write(kit.Worktree("ag"), "b.txt", "two\nagent edit\n");
        var folded = await agents.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, Token);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains($"'b.txt' is recorded as shared, and its bytes were not kept in '{shared}': There is not enough space on the disk", folded.Details!);
        Assert.Equal(Digest("two\nagent edit\n"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A file above the bound is recorded as shared like any other and its bytes are not kept - no merge is offered
    /// above that size either - which is the rule, so nothing is said of it.
    /// </summary>
    [Fact]
    public async Task AFileAboveTheBound_IsRecordedAndNotKept()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var large = new byte[TwoChanges.MostBytes + 1];
        File.WriteAllBytes(Path.Combine(kit.Main, "large.bin"), large);
        File.WriteAllBytes(Path.Combine(kit.Main, "at-the-bound.bin"), new byte[TwoChanges.MostBytes]);

        var created = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.Equal(FileContentHash.Of(large), seed.Paths["large.bin"]);
        Assert.False(File.Exists(kit.Layout.SharedFile("ag", seed.Paths["large.bin"])));
        Assert.True(File.Exists(kit.Layout.SharedFile("ag", seed.Paths["at-the-bound.bin"])));
        Assert.DoesNotContain(created.Details!, line => line.Contains("were not kept", StringComparison.Ordinal));
    }

    /// <summary>
    /// A kept copy is found by the digest it is named by, and read again each time: one that no longer hashes to its name
    /// is said to hold other bytes, and one never kept is not found.
    /// </summary>
    [Fact]
    public async Task AKeptCopy_IsFoundByItsDigest_AndSaidNotToHoldItsBytesWhereItNoLongerHashesToItsName()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "o1");
        var copies = new SharedCopies(harness.FileSystem, harness.FilePermissions, layout, "ag");
        var source = temp.WriteFile("source.txt", "shared\n");
        var digest = Digest("shared\n");

        Assert.Null(await copies.FindAsync(digest, Token));
        Assert.Null(await copies.KeepAsync(digest, source, Token));

        var found = await copies.FindAsync(digest, Token);

        Assert.Equal(new SharedCopy(layout.SharedFile("ag", digest), Holds: true), found);
        Assert.Equal("shared\n", File.ReadAllText(found!.File));

        File.WriteAllText(found.File, "damaged\n");

        Assert.Equal(new SharedCopy(layout.SharedFile("ag", digest), Holds: false), await copies.FindAsync(digest, Token));
    }

    /// <summary>A copy is never kept that is not the bytes recorded: a file that holds other bytes than the digest names is not kept, and why is said.</summary>
    [Fact]
    public async Task AFileThatDoesNotHoldTheDigestRecorded_IsNotKept()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "o1");
        var copies = new SharedCopies(harness.FileSystem, harness.FilePermissions, layout, "ag");
        var source = temp.WriteFile("source.txt", "changed since\n");

        var why = await copies.KeepAsync(Digest("shared\n"), source, Token);

        Assert.Contains("holds other content now than when it was measured", why);
        Assert.False(Directory.Exists(copies.Directory) && Directory.EnumerateFiles(copies.Directory).Any());
    }

    /// <summary>
    /// A kept copy the seed no longer names that cannot be removed - a program holds it - is said and stops nothing, and
    /// the next write of the seed removes it.
    /// </summary>
    [Fact]
    public async Task ACopyThatCannotBePruned_IsSaid_AndGoesAtTheNextWriteOfTheSeed()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");
        var held = kit.Layout.SharedFile("ag", Digest("one\nmain edit\n"));
        var agents = kit.Harness.Agents(new HeldFileSystem(kit.Harness.FileSystem, held), kit.Harness.AnchorRegistryService);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");

        var refreshed = await agents.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains(refreshed.Details!, line => line.StartsWith($"a kept copy the seed no longer names, '{held}', could not be removed: ", StringComparison.Ordinal)
            && line.EndsWith("; the next write of the seed removes it", StringComparison.Ordinal));
        Assert.True(File.Exists(held));

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit once more\n");
        var again = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal([Digest("one\nmain edit once more\n")], await KeptAsync(kit, "ag"));
    }

    /// <summary>
    /// Kept copies the seed no longer names that none of can be removed are said in one line however many: each named,
    /// the first reason and how many other reasons there were.
    /// </summary>
    [Fact]
    public async Task CopiesThatCannotBePruned_AreSaidInOneLine()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nfirst\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nfirst\n");
        await kit.CreateAgentAsync("ag");
        var digests = new[] { Digest("one\nfirst\n"), Digest("two\nfirst\n") };
        var (first, second) = string.CompareOrdinal(digests[0], digests[1]) < 0 ? (digests[0], digests[1]) : (digests[1], digests[0]);
        var agents = kit.Harness.Agents(
            new HeldFileSystem(kit.Harness.FileSystem, [.. digests.Select(digest => kit.Layout.SharedFile("ag", digest))]),
            kit.Harness.AnchorRegistryService);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nsecond\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nsecond\n");

        var refreshed = await agents.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Contains(
            $"2 kept copy(ies) the seed no longer names, in '{kit.Layout.SharedDirectory("ag")}', could not be removed - {first}, {second}: "
                + $"The process cannot access the file '{kit.Layout.SharedFile("ag", first)}' because it is being used by another process; and 1 other reason(s); "
                + "the next write of the seed removes them",
            refreshed.Details!);
        Assert.True(digests.All(digest => File.Exists(kit.Layout.SharedFile("ag", digest))));
    }

    /// <summary>
    /// Deleting an agent removes the copies kept for it once its record says it is deleted, and its directory stays as
    /// its history. They are no evidence: a directory of them left behind is nothing delete-orchestrator asks
    /// --delete-evidence for.
    /// </summary>
    [Fact]
    public async Task ADeletedAgent_KeepsNoCopies_AndTheyAreNoEvidence()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");
        var shared = kit.Layout.SharedDirectory("ag");

        Assert.NotEmpty(await KeptAsync(kit, "ag"));

        var deleted = await kit.DeleteAsync("ag", apply: true);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.False(Directory.Exists(shared));
        Assert.True(File.Exists(kit.Layout.SeedFile("ag")));

        OrchestrationKit.Write(shared, Digest("left\n"), "left\n");
        var gone = await kit.Harness.OrchestratorService.DeleteAsync(kit.Main, "o1", deleteEvidence: false, Token);

        Assert.True(gone.Succeeded, OrchestrationKit.Describe(gone));
        Assert.False(Directory.Exists(kit.Layout.Directory));
    }

    /// <summary>
    /// Copies that cannot be removed when their agent is deleted never leave the deletion unfinished: the agent is
    /// deleted, and a line says where they still are and what removes them.
    /// </summary>
    [Fact]
    public async Task CopiesThatCannotBeRemovedWithTheirAgent_AreSaid_AndTheAgentIsDeleted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");
        var shared = kit.Layout.SharedDirectory("ag");
        var agents = kit.Harness.Agents(new HeldDirectoryFileSystem(kit.Harness.FileSystem, shared), kit.Harness.AnchorRegistryService);

        var deleted = await agents.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.Contains(deleted.Details!, line => line.StartsWith($"the bytes of the files it shared are still kept in '{shared}', which could not be removed: ", StringComparison.Ordinal)
            && line.EndsWith("; deleting its orchestrator removes them with its directory", StringComparison.Ordinal));
        Assert.True(Directory.Exists(shared));
    }

    /// <summary>
    /// A seed written by a build that kept no copies reads as it always did - a seed names digests and nothing else, and
    /// gained no member - and its agent folds with none: only what that fold records is kept.
    /// </summary>
    [Fact]
    public async Task ASeedWrittenByABuildThatKeptNoCopies_Reads_AndItsAgentFolds_WithNone()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        Directory.Delete(kit.Layout.SharedDirectory("ag"), recursive: true);

        var members = JsonNode.Parse(File.ReadAllText(kit.Layout.SeedFile("ag")))!.AsObject().Select(member => member.Key);

        Assert.Equal(["seededAt", "empty", "paths"], members);

        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var folded = await kit.FoldAsync("ag", apply: true);
        var seed = kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!;

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(["a.txt", "b.txt"], seed.Paths.Keys.Order(StringComparer.Ordinal));
        Assert.Equal([Digest("two\nagent edit\n")], await KeptAsync(kit, "ag", proved: false));
        Assert.DoesNotContain(folded.Details!, line => line.Contains("were not kept", StringComparison.Ordinal));
    }

    /// <summary>
    /// Every command that keeps the bytes an agent shares names where they are kept, beside its seed, on each way out once
    /// it wrote them: create-agent, seed-agent - handing something or nothing more - refresh-agent and fold-agent.
    /// </summary>
    [Fact]
    public async Task EachCommandThatKeepsCopies_NamesWhereTheyAreKept()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var created = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);

        NamesShared(kit, "ag", created);

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");

        NamesShared(kit, "ag", await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token));
        NamesShared(kit, "ag", await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: true, force: false, Token));

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit once more\n");

        NamesShared(kit, "ag", await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token));

        OrchestrationKit.Write(kit.Worktree("ag"), "b.txt", "two\nagent edit\n");

        NamesShared(kit, "ag", await kit.FoldAsync("ag", apply: true));
    }

    /// <summary>An agent handed nothing keeps no bytes, and no directory of them is named.</summary>
    [Fact]
    public async Task AnAgentHandedNothing_KeepsNoCopies_AndNamesNoDirectoryOfThem()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");

        var created = await kit.Harness.AgentService.CreateAsync(kit.Main, "o1", "ag", "model-b", true, null, Token);

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.False(Directory.Exists(kit.Layout.SharedDirectory("ag")));
        Assert.DoesNotContain(created.Details!, line => line.StartsWith("shared ", StringComparison.Ordinal));
        Assert.Contains($"seed {kit.Layout.SeedFile("ag")}", created.Details!);
    }

    /// <summary>
    /// A command that stopped part way names the bytes it kept as well as the seed that records them: a hand-over that
    /// stopped - making an agent, seeding it, refreshing it - and a fold that stopped, which kept the first bytes of an
    /// agent handed nothing before.
    /// </summary>
    [Fact]
    public async Task ACommandThatStoppedPartWay_NamesWhereTheBytesItKeptAre()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var stopping = kit.Harness.Agents(new UncopyableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "b.txt")), kit.Harness.AnchorRegistryService);

        var created = await stopping.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);

        Assert.Equal(HarnessExit.Incomplete, created.ExitCode);
        NamesShared(kit, "ag", created, succeeded: false);

        var seeded = await stopping.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        Assert.Equal(HarnessExit.Incomplete, seeded.ExitCode);
        NamesShared(kit, "ag", seeded, succeeded: false);

        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");
        var refreshed = await stopping.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt", "b.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, refreshed.ExitCode);
        NamesShared(kit, "ag", refreshed, succeeded: false);

        // An agent handed nothing has no copies until its fold keeps the first.
        await kit.CreateAgentAsync("bare", empty: true);
        OrchestrationKit.Write(kit.Worktree("bare"), "c.txt", "three\n");
        OrchestrationKit.Write(kit.Worktree("bare"), "d.txt", "four\n");
        var unwritable = kit.Harness.Agents(new UnplaceableFileSystem(kit.Harness.FileSystem, Path.Combine(kit.Main, "d.txt")), kit.Harness.AnchorRegistryService);

        Assert.False(Directory.Exists(kit.Layout.SharedDirectory("bare")));

        var folded = await unwritable.FoldAsync(kit.Main, "o1", "bare", new FoldAllowances(), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, folded.ExitCode);
        Assert.Equal([Digest("three\n")], await KeptAsync(kit, "bare"));
        NamesShared(kit, "bare", folded, succeeded: false);
    }

    /// <summary>
    /// The seed is written before the copies are pruned to it: a seed that cannot be written leaves the one on disk naming
    /// what it named, and every copy it names is still there. The command says it could not record what it did, and names
    /// where the copies it kept are - making an agent, seeding, refreshing and folding alike.
    /// </summary>
    [Fact]
    public async Task ASeedThatCannotBeWritten_LeavesEveryCopyTheSeedOnDiskNames_AndItsCommandNamesWhereTheyAre()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        await kit.CreateAgentAsync("ag");
        var before = kit.Layout.SharedFile("ag", Digest("one\nmain edit\n"));
        var unrecorded = kit.Harness.Agents(new UnwritableFileSystem(kit.Harness.FileSystem, kit.Layout.SeedFile("ag")), kit.Harness.AnchorRegistryService);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");

        var refreshed = await unrecorded.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, refreshed.ExitCode);
        Assert.StartsWith("Refreshing agent 'ag' of 'o1' handed over what it could and could not record it:", refreshed.Message, StringComparison.Ordinal);
        Assert.Equal(Digest("one\nmain edit\n"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["a.txt"]);
        Assert.True(File.Exists(before), "A copy the seed on disk names was pruned before the seed that no longer names it was written.");
        NamesShared(kit, "ag", refreshed, succeeded: false);

        var seeded = await unrecorded.SeedAsync(kit.Main, "o1", "ag", empty: false, force: true, Token);

        Assert.Equal(HarnessExit.Incomplete, seeded.ExitCode);
        Assert.True(File.Exists(before));
        NamesShared(kit, "ag", seeded, succeeded: false);

        OrchestrationKit.Write(kit.Worktree("ag"), "b.txt", "two\nagent edit\n");
        var folded = await unrecorded.FoldAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, folded.ExitCode);
        Assert.True(File.Exists(before));
        NamesShared(kit, "ag", folded, succeeded: false);

        var making = kit.Harness.Agents(new UnwritableFileSystem(kit.Harness.FileSystem, kit.Layout.SeedFile("other")), kit.Harness.AnchorRegistryService);
        var created = await making.CreateAsync(kit.Main, "o1", "other", "model-b", false, null, Token);

        Assert.Equal(HarnessExit.Incomplete, created.ExitCode);
        Assert.False(File.Exists(kit.Layout.SeedFile("other")));
        Assert.DoesNotContain(created.Details!, line => line.StartsWith("seed ", StringComparison.Ordinal));
        NamesShared(kit, "other", created, succeeded: false);
    }

    /// <summary>One file is kept for a digest however many paths hold it, and it stays while any of them still does.</summary>
    [Fact]
    public async Task ACopyTwoPathsShare_StaysWhenOneOfThemChanges()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "the same\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "the same\n");
        await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "changed\n");

        var refreshed = await kit.Harness.AgentService.RefreshAsync(kit.Main, "o1", "ag", RefreshRequest.Under("a.txt"), apply: true, Token);

        Assert.True(refreshed.Succeeded, OrchestrationKit.Describe(refreshed));
        Assert.Equal(new[] { Digest("the same\n"), Digest("changed\n") }.Order(StringComparer.Ordinal), await KeptAsync(kit, "ag"));
    }

    /// <summary>
    /// Seeding an agent again keeps the bytes it hands, and the bytes of what it no longer shares go; a fold that removes a
    /// path both trees shared prunes the bytes nothing names any more.
    /// </summary>
    [Fact]
    public async Task SeedingAgainKeepsWhatItHands_AndAFoldThatRemovesASharedPath_PrunesItsBytes()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit again\n");

        var seeded = await kit.Harness.AgentService.SeedAsync(kit.Main, "o1", "ag", empty: false, force: false, Token);

        Assert.True(seeded.Succeeded, OrchestrationKit.Describe(seeded));
        Assert.Equal(new[] { Digest("one\nmain edit again\n"), Digest("two\nmain edit\n") }.Order(StringComparer.Ordinal), await KeptAsync(kit, "ag"));

        File.Delete(Path.Combine(worktree, "b.txt"));
        var folded = await kit.FoldAsync("ag", apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.False(File.Exists(Path.Combine(kit.Main, "b.txt")));
        Assert.Equal([Digest("one\nmain edit again\n")], await KeptAsync(kit, "ag"));
    }

    /// <summary>
    /// Copies that cannot be kept are said in one line whatever their number - which paths, as many as a line names, how
    /// many more, and why - never a line for each: a full disk would otherwise print one for every file handed.
    /// </summary>
    [Fact]
    public async Task ManyCopiesThatCannotBeKept_AreSaidInOneLine()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        foreach (var name in new[] { "a", "b", "c", "d", "e" })
        {
            OrchestrationKit.Write(kit.Main, $"{name}.txt", $"{name}\nmain edit\n");
        }

        var shared = kit.Layout.SharedDirectory("ag");
        var agents = kit.Harness.Agents(new NoRoomFileSystem(kit.Harness.FileSystem, shared), kit.Harness.AnchorRegistryService);

        var created = await agents.CreateAsync(kit.Main, "o1", "ag", "model-b", false, null, Token);

        Assert.True(created.Succeeded, OrchestrationKit.Describe(created));
        Assert.Equal(
            $"5 path(s) are recorded as shared, and their bytes were not kept in '{shared}' - a.txt, b.txt, c.txt and 2 more: There is not enough space on the disk",
            Assert.Single(created.Details!, line => line.Contains("were not kept", StringComparison.Ordinal)));
        Assert.Equal(5, kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths.Count);
    }

    /// <summary>
    /// Deleting an agent keeps the copies of what its fold writes as any fold does: one that stops after its fold leaves
    /// the agent live, its seed naming bytes only those copies hold. A copy that could not be kept is said where the agent
    /// outlives the command, and not by one that deletes it, its copies going with it.
    /// </summary>
    [Fact]
    public async Task ADeletionsFold_KeepsCopiesAsAnyFoldDoes_AndSaysOfOneNotKeptOnlyWhereTheAgentOutlivesIt()
    {
        using var temp = new TempDirectory();
        using var claude = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.ClaudeConfigDirectory = claude.Path;
        var transcript = claude.WriteFile(Path.Combine("projects", "some-project", "sess1.jsonl"), "{}\n");
        OrchestrationKit.Write(kit.Main, "a.txt", "one\nmain edit\n");
        var worktree = await kit.CreateAgentAsync("ag", session: "sess1");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        var shared = kit.Layout.SharedDirectory("ag");
        var unreadable = kit.Harness.Agents(new UnreadableFileSystem(kit.Harness.FileSystem, transcript), kit.Harness.AnchorRegistryService);

        var stopped = await unreadable.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(Digest("two\nagent edit\n"), kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, "ag")!.Paths["b.txt"]);
        Assert.Equal(
            new[] { Digest("one\nmain edit\n"), Digest("two\nagent edit\n") }.Order(StringComparer.Ordinal),
            await KeptAsync(kit, "ag"));
        NamesShared(kit, "ag", stopped, succeeded: false);

        OrchestrationKit.Write(worktree, "c.txt", "three\nagent edit\n");
        var fullAndUnreadable = kit.Harness.Agents(
            new NoRoomFileSystem(new UnreadableFileSystem(kit.Harness.FileSystem, transcript), shared),
            kit.Harness.AnchorRegistryService);

        var unkept = await fullAndUnreadable.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, unkept.ExitCode);
        Assert.Equal(AgentStates.Live, kit.Record("ag").State);
        Assert.Contains(unkept.Details!, line => line.StartsWith($"'c.txt' is recorded as shared, and its bytes were not kept in '{shared}': ", StringComparison.Ordinal));

        OrchestrationKit.Write(worktree, "d.txt", "four\nagent edit\n");
        var full = kit.Harness.Agents(new NoRoomFileSystem(kit.Harness.FileSystem, shared), kit.Harness.AnchorRegistryService);

        var deleted = await full.DeleteAsync(kit.Main, "o1", "ag", new FoldAllowances(), apply: true, discardUncommitted: false, Token);

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record("ag").State);
        Assert.DoesNotContain(deleted.Details!, line => line.Contains("were not kept", StringComparison.Ordinal));
        Assert.DoesNotContain(deleted.Details!, line => line.StartsWith("shared ", StringComparison.Ordinal));
        Assert.False(Directory.Exists(shared), "The copies kept for a deleted agent were not removed with it.");
    }

    /// <summary>
    /// A copy is a file of the agent's own, whatever the file it was kept from was: never read-only, and never one that
    /// can be run. A digest already kept is kept: a copy that holds those bytes is not written again, so one made
    /// read-only meanwhile stops nothing.
    /// </summary>
    [Fact]
    public async Task AKeptCopy_IsNeverReadOnly_AndADigestAlreadyKept_IsNotWrittenAgain()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var layout = OrchestratorLayout.Of(new HarnessLayout(temp.Path, temp.Path), "o1");
        var copies = new SharedCopies(harness.FileSystem, harness.FilePermissions, layout, "ag");
        var digest = Digest("shared\n");
        var source = temp.WriteFile("source.txt", "shared\n");
        var again = temp.WriteFile("again.txt", "shared\n");
        var kept = layout.SharedFile("ag", digest);

        try
        {
            harness.FilePermissions.SetExecutable(source, executable: true);
            MakeReadOnly(source);
            MakeReadOnly(again);

            Assert.Null(await copies.KeepAsync(digest, source, Token));
            Assert.False(IsReadOnly(kept), "The kept copy is read-only, as the file it was kept from was.");

            // Windows keeps no such bit: what runs there is decided by a file's name.
            Assert.False(!OperatingSystem.IsWindows() && harness.FilePermissions.IsExecutable(kept), "The kept copy can be run, as the file it was kept from could.");

            MakeReadOnly(kept);
            var written = new DateTime(2001, 2, 3, 4, 5, 6, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(kept, written);

            Assert.Null(await copies.KeepAsync(digest, again, Token));
            Assert.Null(await copies.KeepAsync(digest, System.Text.Encoding.UTF8.GetBytes("shared\n"), Token));
            Assert.Equal(written, File.GetLastWriteTimeUtc(kept));
            Assert.Equal([digest], Directory.EnumerateFiles(copies.Directory).Select(file => Path.GetFileName(file)));
        }
        finally
        {
            foreach (var file in new[] { source, again, kept }.Where(File.Exists))
            {
                File.SetAttributes(file, FileAttributes.Normal);

                if (!OperatingSystem.IsWindows())
                {
                    File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                }
            }
        }
    }

    /// <summary>Makes <paramref name="file"/> read-only as this platform does: its attribute on Windows, its mode elsewhere.</summary>
    private static void MakeReadOnly(string file)
    {
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, File.GetAttributes(file) | FileAttributes.ReadOnly);
        }
        else
        {
            File.SetUnixFileMode(file, File.GetUnixFileMode(file) & ~(UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite));
        }
    }

    /// <summary>Whether <paramref name="file"/> is read-only as this platform says it.</summary>
    private static bool IsReadOnly(string file)
        => OperatingSystem.IsWindows()
            ? File.GetAttributes(file).HasFlag(FileAttributes.ReadOnly)
            : !File.GetUnixFileMode(file).HasFlag(UnixFileMode.UserWrite);

    /// <summary>
    /// Asserts that <paramref name="outcome"/> names where the bytes <paramref name="agent"/> shares are kept, beside its
    /// seed's line where the seed is there, and that they are there.
    /// </summary>
    private static void NamesShared(OrchestrationKit kit, string agent, CommandOutcome outcome, bool succeeded = true)
    {
        Assert.True(!succeeded || outcome.Succeeded, OrchestrationKit.Describe(outcome));
        Assert.True(Directory.Exists(kit.Layout.SharedDirectory(agent)), "No copies were kept.");
        Assert.Contains($"shared {kit.Layout.SharedDirectory(agent)}", outcome.Details!);
    }

    /// <summary>
    /// The files kept for <paramref name="agent"/>, by name, each read again and found to hash to its name; and, where
    /// <paramref name="proved"/>, exactly one for each digest its seed records.
    /// </summary>
    private static async Task<IReadOnlyList<string>> KeptAsync(OrchestrationKit kit, string agent, bool proved = true)
    {
        var directory = kit.Layout.SharedDirectory(agent);
        IReadOnlyList<string> kept = Directory.Exists(directory)
            ? [.. Directory.EnumerateFiles(directory).Select(file => Path.GetFileName(file)).Order(StringComparer.Ordinal)]
            : [];

        foreach (var digest in kept)
        {
            Assert.Equal(digest, (await FileContentHash.OfAsync(kit.Harness.FileSystem, kit.Layout.SharedFile(agent, digest), Token)).Content);
        }

        if (proved)
        {
            Assert.Equal(kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, agent)!.Paths.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), kept);
        }

        return kept;
    }

    /// <summary>The digest a seed records for a file holding <paramref name="text"/>.</summary>
    private static string Digest(string text) => FileContentHash.Of(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>The real file system, except that nothing can be copied or written into one directory, as onto a disk with no room left.</summary>
    private sealed class NoRoomFileSystem(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public override void CopyFile(string source, string destination, bool overwrite = false)
        {
            Refuse(destination);
            base.CopyFile(source, destination, overwrite);
        }

        public override Task WriteAllBytesAtomicAsync(string path, byte[] contents, CancellationToken cancellationToken = default)
        {
            Refuse(path);
            return base.WriteAllBytesAtomicAsync(path, contents, cancellationToken);
        }

        private void Refuse(string destination)
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(destination)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("There is not enough space on the disk.");
            }
        }
    }

    /// <summary>The real file system, except that one file cannot be copied, as while another program holds it.</summary>
    private sealed class UncopyableFileSystem(IFileSystem inner, string source) : PassThroughFileSystem(inner)
    {
        public override void CopyFile(string from, string destination, bool overwrite = false)
        {
            if (string.Equals(Path.GetFullPath(from), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{from}' because it is being used by another process.");
            }

            base.CopyFile(from, destination, overwrite);
        }
    }

    /// <summary>The real file system, except that nothing can be put in place at one path, as while another program holds the file open.</summary>
    private sealed class UnplaceableFileSystem(IFileSystem inner, string file) : PassThroughFileSystem(inner)
    {
        public override void ReplaceFile(string source, string destination)
        {
            if (string.Equals(Path.GetFullPath(destination), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("The process cannot access the file because it is being used by another process.");
            }

            base.ReplaceFile(source, destination);
        }
    }

    /// <summary>The real file system, except that one record cannot be written, as onto a disk with no room left.</summary>
    private sealed class UnwritableFileSystem(IFileSystem inner, string record) : PassThroughFileSystem(inner)
    {
        public override void WriteAllTextAtomic(string path, string contents)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(record), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("There is not enough space on the disk.");
            }

            base.WriteAllTextAtomic(path, contents);
        }
    }

    /// <summary>The real file system, except that one file cannot be read, as while another program holds it.</summary>
    private sealed class UnreadableFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.")
                : base.OpenRead(path);
    }

    /// <summary>The real file system, except that the files named cannot be deleted, as while other programs hold them open.</summary>
    private sealed class HeldFileSystem(IFileSystem inner, params string[] held) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (held.Any(file => string.Equals(Path.GetFullPath(path), Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            base.DeleteFile(path);
        }
    }

    /// <summary>The real file system, except that one directory cannot be deleted, as while another program stands in it.</summary>
    private sealed class HeldDirectoryFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override void DeleteDirectory(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.");
            }

            base.DeleteDirectory(path);
        }
    }
}
