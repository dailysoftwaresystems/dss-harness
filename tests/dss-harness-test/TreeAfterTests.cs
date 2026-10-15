using NSubstitute;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Repository;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>The tree a command leaves, as an arm registry it merges into that tree is judged by it.</summary>
public sealed class TreeAfterTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// Where the tree's file system finds a name in any case, a path the command removes and writes again in another case
    /// is the one file it writes: listed as the write spells it, and holding what the write holds.
    /// </summary>
    [Fact]
    public async Task APathRemovedAndWrittenAgainInAnotherCase_IsTheFileWritten_SpelledAsTheWriteSpellsIt()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        temp.WriteFile("mutations/texts/old.diag", "old");
        var config = new HarnessConfig { Mutations = new MutationSettings { Registry = "mutations/arms.txt", TextDirectory = "mutations/texts" } };
        var context = new HarnessContext(new HarnessLayout(temp.Path, temp.Path), config);
        var sync = Substitute.For<ISyncService>();
        sync.ExclusionsAsync(default!, Token).ReturnsForAnyArgs(Task.FromResult(new SyncExclusions(config.Sync, config.Worktrees.Root, [])));

        var after = await TreeAfter.OfAsync(
            sync,
            new CaseOf(harness.FileSystem, temp.Path, folds: true),
            context,
            temp.Path,
            [("mutations/texts/old.diag", null), ("mutations/texts/Old.diag", _ => Task.FromResult("new"u8.ToArray()))],
            Token);

        Assert.Same(StringComparer.OrdinalIgnoreCase, after.Names);
        Assert.Equal(["Old.diag"], after.List("mutations/texts")!.Files);
        Assert.True(after.Exists("mutations/texts/Old.diag"));
        Assert.Equal("new"u8.ToArray(), await after.ReadAsync("mutations/texts/old.diag", Token));
    }
}
