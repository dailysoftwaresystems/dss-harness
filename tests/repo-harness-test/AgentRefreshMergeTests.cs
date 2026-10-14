using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A path the agent changed that the main tree moved too, met by a refresh: what a three-way merge of the two gives is
/// said beside it, and a clean one named with --merge is written into the agent's worktree as its change, the path
/// recorded as handed the main tree's copy, which the merge descends from - so its fold writes the merge.
/// </summary>
public sealed class AgentRefreshMergeTests
{
    private const string Agent = "ag";

    /// <summary>The file both trees change.</summary>
    private const string Both = "src/m.txt";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static string Text(params (int Line, string Text)[] changed) => AgentFoldMergeTests.Text(changed);

    private static string Digest(string text) => FileContentHash.Of(Encoding.UTF8.GetBytes(text));

    private static SeedRecord Seed(OrchestrationKit kit) => kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, Agent)!;

    /// <summary>
    /// An agent made at a commit that holds <see cref="Both"/>, which it then changes at line 2, and the main tree changes
    /// at <paramref name="line"/> and commits.
    /// </summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> BothChangedAsync(TempDirectory temp, int line = 9)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file two will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((line, $"MAIN {line}")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's change", Token);

        return (kit, worktree);
    }

    private static Task<CommandOutcome> RefreshAsync(OrchestrationKit kit, bool apply, params string[] merge)
        => kit.Harness.AgentService.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RefreshRequest { Paths = ["src"], Merge = merge }, apply, Token);

    /// <summary>
    /// A path both changed still refuses the refresh, nothing copied: its line says what a merge of the two gives, and the
    /// refusal names the arguments that take a clean one beside those that leave it.
    /// </summary>
    [Fact]
    public async Task APathBothChanged_RefusesTheRefresh_SayingWhatAMergeGives_AndNamingTheArgumentsThatTakeIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp);

        var refused = await RefreshAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"Agent '{Agent}' of 'o1' changed 1 of the path(s) to refresh - {Both} - and refreshing would undo those changes. Nothing was copied.", refused.Message);
        Assert.Equal(
            [
                $"  {Both}   (a three-way merge of the two is clean - 1 hunk(s) from the agent, 1 from the main tree)",
                "To hand it every other path and leave these as it changed them, run again with --except <path> for each:",
                $"  --except {Both}",
                "1 of them merges clean with the main tree's copy. To write the merge into its worktree as its change, name it:",
                $"  --merge {Both}",
                "--except says the path stays the agent's change, for its fold to weigh against what the main tree holds; --merge writes a clean three-way "
                    + "merge into its worktree as its change, and a clean textual merge proves nothing about the program. None is a --force.",
            ],
            refused.Details!.Take(6));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>
    /// Named with --merge, the merge is written into the agent's worktree - said in the dry run, written only with --apply -
    /// and the path recorded as handed the main tree's copy: the agent's copy differs from it, so its fold writes the merge,
    /// and the main tree then holds both changes. (Refused at 0.6.13.)
    /// </summary>
    [Fact]
    public async Task NamedWithMerge_TheMergeIsWrittenIntoTheWorktree_RecordedAtTheMainTreesBytes_AndItsFoldWritesIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp);
        var merged = Text((2, "AGENT 2"), (9, "MAIN 9"));
        string[] listed =
        [
            $"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)",
            "the agent's session must read these again before it writes them: a write made from what it held before would fold as its change, over what the "
                + "main tree brought",
        ];

        var dry = await RefreshAsync(kit, apply: false, Both);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal($"dry run: 1 path(s) would be merged into the worktree of agent '{Agent}' of 'o1' as its own change, recorded as handed the main tree's copy; pass --apply to write them", dry.Message);
        Assert.Equal(
            [
                "1 path(s) both trees changed, merged three-way, to write into its worktree as its own change, for its fold to write, and to record as handed "
                    + "the main tree's copy - a clean textual merge proves nothing about the program:",
                .. listed,
            ],
            dry.Details!.Take(3));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));

        var applied = await RefreshAsync(kit, apply: true, Both);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal($"1 path(s) merged into the worktree of agent '{Agent}' of 'o1' as its own change, recorded as handed the main tree's copy", applied.Message);
        Assert.Contains(listed[0], applied.Details!);
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Digest(Text((9, "MAIN 9"))), Seed(kit).Paths[Both]);

        // The line that ran once runs again: the path has nothing left to merge, and is let stand.
        var again = await RefreshAsync(kit, apply: true, Both);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));

        var folded = await kit.FoldAsync(Agent, apply: true);

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Digest(merged), Seed(kit).Paths[Both]);
    }

    /// <summary>
    /// What both trees held at a path the agent shares is what its seed records, never its base: a refresh merging a path an
    /// earlier fold merged starts from the merged bytes, so the main tree changing again the line that fold brought merges
    /// clean with the agent's change of another line - merged from the base, the two would conflict on it.
    /// </summary>
    [Fact]
    public async Task ARefreshMergeOfASharedPath_StartsFromWhatItsSeedRecords_NotFromItsBase()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp);
        var folded = await kit.FoldAsync(Agent, apply: true, new FoldAllowances { Merge = [Both] });

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));

        // The main tree changes the line the agent's fold brought; the agent changes another.
        OrchestrationKit.Write(kit.Main, Both, Text((2, "MAIN 2, AFTER THE AGENT'S"), (9, "MAIN 9")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (6, "AGENT 6"), (9, "MAIN 9")));

        var applied = await RefreshAsync(kit, apply: true, Both);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)", applied.Details!);
        Assert.Equal(Text((2, "MAIN 2, AFTER THE AGENT'S"), (6, "AGENT 6"), (9, "MAIN 9")), OrchestrationKit.Read(worktree, Both));
        Assert.Equal(Digest(Text((2, "MAIN 2, AFTER THE AGENT'S"), (9, "MAIN 9"))), Seed(kit).Paths[Both]);
    }

    /// <summary>
    /// A merge that conflicts is said beside the path, its conflicts shown, and is never taken, named or not; nothing names
    /// --merge for it.
    /// </summary>
    [Fact]
    public async Task APathWhoseMergeConflicts_IsShown_AndNeverTaken()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp, line: 2);

        foreach (var merge in new[] { Array.Empty<string>(), [Both] })
        {
            var refused = await RefreshAsync(kit, apply: true, merge);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Equal($"  {Both}   (a three-way merge of the two conflicts in 1 place(s), shown below)", refused.Details![0]);
            Assert.StartsWith($"conflicts of '{Both}', 1 place(s) - as the main tree holds it, as both trees held it, as the agent holds it:", refused.Details[1], StringComparison.Ordinal);
            Assert.DoesNotContain(refused.Details, line => line.StartsWith("  --merge", StringComparison.Ordinal));
            Assert.Contains("--except says the path stays the agent's change, for its fold to weigh against what the main tree holds; it is not a --force.", refused.Details);
        }

        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>A file the agent's session writes between the weighing and the merge is never written over: the refresh stops, exit 21, naming it.</summary>
    [Fact]
    public async Task AFileTheAgentChangedAfterItWasWeighed_StopsTheMerge_AndIsNotWrittenOver()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp);
        var copy = Path.Combine(worktree, Both);

        // Its fifth reading is the one that asks, before the merge is written, whether it is what was weighed.
        var agents = kit.Harness.Agents(
            new AgentFoldMergeTests.MeanwhileFileSystem(kit.Harness.FileSystem, copy, 5, () => File.WriteAllText(copy, "the session's edit\n")),
            kit.Harness.AnchorRegistryService);

        var stopped = await agents.RefreshAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RefreshRequest { Paths = ["src"], Merge = [Both] }, apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            $"Refreshing agent '{Agent}' of 'o1' stopped after 0 of 0 path(s) and merging 0 of 1: '{Both}' holds other content in the agent's worktree now than "
            + $"when it was measured, so it was not written over. Its seed records the 0 handed, the 0 merged; run 'dssharness refresh-agent o1 {Agent} src "
            + $"--merge {Both} --apply' again once that is dealt with.",
            stopped.Message);
        Assert.Equal("the session's edit\n", OrchestrationKit.Read(worktree, Both));
        Assert.DoesNotContain(Both, Seed(kit).Paths.Keys);
    }

    /// <summary>
    /// A --merge naming no path under the paths refreshed is refused as a misspelling, nothing copied; one naming the arm
    /// registry or an anchor registry is refused; and a path named by two options that say different things of it is a
    /// usage error, before anything is read.
    /// </summary>
    [Fact]
    public async Task AMergeNamingNothingTheRefreshWeighs_OrARegistry_OrAPathNamedTwice_IsRefused()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await BothChangedAsync(temp);

        var stray = await RefreshAsync(kit, apply: true, "docs/x.md");

        Assert.Equal(HarnessExit.Refused, stray.ExitCode);
        Assert.Equal("--merge 'docs/x.md' names no path under src the refresh weighs, so it merges nothing: check its spelling. Nothing was copied.", stray.Message);

        var anchors = await RefreshAsync(kit, apply: true, AnchorSettings.DefaultPendingAnchorsPath);

        Assert.Equal(HarnessExit.Refused, anchors.ExitCode);
        Assert.Equal(
            $"--merge '{AnchorSettings.DefaultPendingAnchorsPath}' names an anchor registry, which a fold changes only through the rows the agent files, never as a "
            + "file. Nothing was copied.",
            anchors.Message);

        var twice = await kit.Harness.AgentService.RefreshAsync(
            kit.Main, OrchestrationKit.Orchestrator, Agent, new RefreshRequest { Paths = ["src"], Except = [Both], Merge = [Both] }, apply: true, Token);

        Assert.Equal(HarnessExit.UsageError, twice.ExitCode);
        Assert.Equal($"'{Both}' is named with both --except and --merge: a path is left as the agent changed it, or merged for you, never both.", twice.Message);
        Assert.Equal(
            $"'{Both}' is named with both --stale and --merge: a path is handed the main tree's copy, or merged for you, never both.",
            new RefreshRequest { Stale = [Both], Merge = [Both] }.Problem());
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
    }
}
