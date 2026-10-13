using System.Text.Json.Nodes;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

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
        var large = new byte[SharedCopies.MostBytes + 1];
        File.WriteAllBytes(Path.Combine(kit.Main, "large.bin"), large);
        File.WriteAllBytes(Path.Combine(kit.Main, "at-the-bound.bin"), new byte[SharedCopies.MostBytes]);

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
        var copies = new SharedCopies(harness.FileSystem, layout, "ag");
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
        var copies = new SharedCopies(harness.FileSystem, layout, "ag");
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

    /// <summary>The real file system, except that nothing can be copied into one directory, as onto a disk with no room left.</summary>
    private sealed class NoRoomFileSystem(IFileSystem inner, string directory) : PassThroughFileSystem(inner)
    {
        public override void CopyFile(string source, string destination, bool overwrite = false)
        {
            if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(destination)), Path.GetFullPath(directory), StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("There is not enough space on the disk.");
            }

            base.CopyFile(source, destination, overwrite);
        }
    }

    /// <summary>The real file system, except that one file cannot be deleted, as while another program holds it open.</summary>
    private sealed class HeldFileSystem(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override void DeleteFile(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase))
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
