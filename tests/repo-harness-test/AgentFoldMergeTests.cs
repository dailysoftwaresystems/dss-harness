using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A path both trees changed, met by a fold: what a three-way merge of the two gives is measured and said, a clean one is
/// taken only where the path is named to be merged, and a merge is written into both trees, recorded as what they share,
/// with nothing written over that was not weighed.
/// </summary>
public sealed class AgentFoldMergeTests
{
    private const string Agent = "ag";

    /// <summary>The file both trees change.</summary>
    private const string Both = "src/m.txt";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Twelve lines, each <c>line N</c> but those given: far enough apart that two edits of different lines merge.</summary>
    internal static string Text(params (int Line, string Text)[] changed)
        => string.Concat(Enumerable.Range(1, 12).Select(line => (changed.FirstOrDefault(change => change.Line == line).Text ?? $"line {line}") + "\n"));

    private static FoldAllowances Merging(params string[] paths) => new() { Merge = paths };

    private static SeedRecord Seed(OrchestrationKit kit) => kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, Agent)!;

    private static string Digest(string text) => FileContentHash.Of(Encoding.UTF8.GetBytes(text));

    /// <summary>An agent made at a commit that holds <see cref="Both"/>: a path it shares nothing for, weighed against its base.</summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> AtItsBaseAsync(TempDirectory temp)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file two will change", Token);

        return (kit, await kit.CreateAgentAsync(Agent));
    }

    /// <summary>An agent handed <see cref="Both"/> as the main tree held it uncommitted: a path it shares, weighed against its seed.</summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> HandedAsync(TempDirectory temp, string? text = null)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, text ?? Text());

        return (kit, await kit.CreateAgentAsync(Agent));
    }

    [Theory]
    [InlineData(false, "the main tree changed it after the agent's base")]
    [InlineData(true, "the main tree changed it after the agent was last handed it or folded, so writing the agent's copy would lose that change")]
    public async Task APathBothTreesChanged_StillRefusesTheFold_AndItsLineSaysTheMergeIsClean_AndNamesTheOptionForIt(bool handed, string reason)
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = handed ? await HandedAsync(temp) : await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (3, "AGENT 3")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        foreach (var apply in new[] { false, true })
        {
            var refused = await kit.FoldAsync(Agent, apply);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);

            var line = Assert.Single(refused.Details!, line => line.StartsWith($"  '{Both}': ", StringComparison.Ordinal));
            Assert.Contains(reason, line, StringComparison.Ordinal);
            Assert.EndsWith(
                $"so writing the agent's copy would lose that change; a three-way merge of the two is clean - 1 hunk(s) from the agent, 1 from the main tree - and --merge {Both} writes it",
                line,
                StringComparison.Ordinal);
            Assert.Contains(refused.Details!, detail => detail.Contains("--merge <path> writes it into both trees: it is never taken unasked", StringComparison.Ordinal));

            // The last of the refusal's lines, before the log an --apply names.
            var naming = Assert.Single(refused.Details!, detail => detail.StartsWith("to merge the ", StringComparison.Ordinal));
            Assert.Equal($"to merge the 1 path(s) that merge clean: '{ToolPackage.Command} fold-agent o1 {Agent} --merge {Both}'", naming);
            Assert.Equal(apply ? refused.Details!.Count - 2 : refused.Details!.Count - 1, refused.Details!.ToList().IndexOf(naming));

            // Measured, said, and not taken: nobody asked for it.
            Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
            Assert.Equal(Text((2, "AGENT 2"), (3, "AGENT 3")), OrchestrationKit.Read(worktree, Both));
        }
    }

    [Fact]
    public async Task NamedToBeMerged_ItIsWrittenIntoBothTrees_RecordedAsSharedAtTheMergedBytes_AndTheAgentsNextFoldLeavesItOut()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(worktree, "new.txt", "new\n");
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9"), (11, "MAIN 11")));
        var merged = Text((2, "AGENT 2"), (9, "MAIN 9"), (11, "MAIN 11"));

        var dry = await kit.FoldAsync(Agent, apply: false, Merging(Both));

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree, merges 1, removes 0 and applies 0 row(s); pass --apply to write them", dry.Message);
        Assert.Contains(
            "1 path(s) both trees changed, merged three-way, to write into both trees and record as shared - a clean textual merge proves nothing about the program, "
            + "so build and test the tree before you commit it:",
            dry.Details!);
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 2 from the main tree)", dry.Details!);
        Assert.Contains(
            "the agent's session must read these again before it writes them: a write made from what it held before would fold as its change, over what the main tree brought",
            dry.Details!);
        Assert.Equal(Text((9, "MAIN 9"), (11, "MAIN 11")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.StartsWith("folded agent 'ag' of 'o1': wrote 1 path(s) into the main tree, merged 1, removed 0 and applied 0 row(s); ", applied.Message);
        Assert.Contains(applied.Details!, line => line.StartsWith("1 path(s) both trees changed, merged three-way, written into both trees and recorded as shared - ", StringComparison.Ordinal));
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal("new\n", OrchestrationKit.Read(kit.Main, "new.txt"));

        // Recorded as what both trees share, with its bytes kept to merge from next time.
        Assert.Equal(Digest(merged), Seed(kit).Paths[Both]);
        Assert.Equal(merged, File.ReadAllText(kit.Layout.SharedFile(Agent, Digest(merged))));

        // The command line runs again: the path has nothing left to merge, and the option naming it is let stand.
        var again = await kit.FoldAsync(Agent, apply: false, Merging(Both));

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("dry run: folding agent 'ag' of 'o1' writes 0 path(s) into the main tree, removes 0 and applies 0 row(s); pass --apply to write them", again.Message);
        Assert.Contains("2 path(s) it shares with the main tree, handed to it or folded before: 0 its own, 2 inherited, 0 already in the main tree, 0 deleted, 0 settled", again.Details!);
    }

    /// <summary>
    /// What both trees held at a path the agent shares is what its last fold recorded, never its base: merged from the
    /// base, a line the first merge already brought in and the main tree then changed again would conflict with itself.
    /// </summary>
    [Fact]
    public async Task ASecondFoldOfTheSameFile_BothChangedAgain_MergesFromWhatTheFirstFoldRecorded_NotFromItsBase()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        var first = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(first.Succeeded, OrchestrationKit.Describe(first));

        // The main tree changes the line the agent's first fold brought; the agent changes another.
        OrchestrationKit.Write(kit.Main, Both, Text((2, "MAIN 2, AFTER THE AGENT'S"), (9, "MAIN 9")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (6, "AGENT 6"), (9, "MAIN 9")));

        var second = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(second.Succeeded, OrchestrationKit.Describe(second));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)", second.Details!);
        Assert.Contains("1 path(s) it shares with the main tree, handed to it or folded before: 0 its own, 0 inherited, 0 already in the main tree, 0 deleted, 0 settled, 1 merged", second.Details!);

        var merged = Text((2, "MAIN 2, AFTER THE AGENT'S"), (6, "AGENT 6"), (9, "MAIN 9"));
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(Digest(merged), Seed(kit).Paths[Both]);

        // The copy of what they shared before is pruned with the seed that no longer names it.
        Assert.Equal([Digest(merged)], Directory.EnumerateFiles(kit.Layout.SharedDirectory(Agent)).Select(Path.GetFileName));
    }

    [Fact]
    public async Task APathWhoseMergeConflicts_RefusesTheFold_ThoughItIsNamedToBeMerged_AndItsConflictsAreShown()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, Both, Text((2, "MAIN 2")));

        var refused = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);

        var details = refused.Details!.ToList();
        var line = Assert.Single(details, line => line.StartsWith($"  '{Both}': ", StringComparison.Ordinal));
        Assert.EndsWith($"; a three-way merge of the two conflicts in 1 place(s), shown below: reconcile it by hand, then --settled {Both}", line, StringComparison.Ordinal);

        var shown = details.IndexOf($"conflicts of '{Both}', 1 place(s) - as the main tree holds it, as both trees held it, as the agent holds it:");
        Assert.True(shown > details.IndexOf(line), "The conflicts were not shown under the refusals.");
        Assert.Equal(
            ["  <<<<<<< the main tree", "  MAIN 2", "  ||||||| what both held", "  line 2", "  =======", "  AGENT 2", "  >>>>>>> the agent"],
            details.Skip(shown + 1).Take(7));
        Assert.DoesNotContain(details, detail => detail.StartsWith("to merge the", StringComparison.Ordinal));

        // The whole fold is refused: neither the conflicting path nor the agent's other work is written.
        Assert.Equal(Text((2, "MAIN 2")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    [Fact]
    public async Task ADeletionAgainstAChange_IsNeverMerged_NamedOrNot()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(kit.Main, "src/other.txt", Text());
        await kit.Harness.CommitAllAsync(kit.Main, "another", Token);
        await kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new RebaseRequest(), apply: true, Token);
        File.Delete(Path.Combine(worktree, Both));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        OrchestrationKit.Write(worktree, "src/other.txt", Text((2, "AGENT 2")));
        File.Delete(Path.Combine(kit.Main, "src", "other.txt"));

        foreach (var allowances in new[] { new FoldAllowances(), Merging(Both, "src/other.txt") })
        {
            var refused = await kit.FoldAsync(Agent, apply: true, allowances);

            Assert.Equal(HarnessExit.Refused, refused.ExitCode);
            Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Both}': the main tree changed it after the agent's base", StringComparison.Ordinal)
                && line.EndsWith("so removing it would lose that change", StringComparison.Ordinal));
            Assert.Contains(refused.Details!, line => line.StartsWith("  'src/other.txt' is at the agent's base", StringComparison.Ordinal)
                && line.EndsWith("and missing from the main tree, removed by an uncommitted edit, so writing the agent's copy would lose that change", StringComparison.Ordinal));
            Assert.DoesNotContain(refused.Details!, line => line.Contains("three-way merge of the two", StringComparison.Ordinal) && line.StartsWith("  '", StringComparison.Ordinal));
            Assert.False(File.Exists(Path.Combine(worktree, Both)));
            Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        }
    }

    [Fact]
    public async Task AFileBothTreesMade_WhereNeitherHeldOneBefore_IsOfferedNoMerge_SayingWhy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, "made.txt", "the agent's\n");
        OrchestrationKit.Write(kit.Main, "made.txt", "the main tree's\n");

        var refused = await kit.FoldAsync(Agent, apply: true, Merging("made.txt"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'made.txt' is not at the agent's base", StringComparison.Ordinal)
            && line.EndsWith(
                "and the main tree made it since, by an uncommitted edit, so writing the agent's copy would lose that change; no three-way merge "
                    + "is offered: both trees made it, and neither held a file there before",
                StringComparison.Ordinal));
        Assert.Equal("the main tree's\n", OrchestrationKit.Read(kit.Main, "made.txt"));
    }

    [Fact]
    public async Task AFileGitWillNotMerge_OrWhoseAttributeAsksForAnotherMerge_IsOfferedNone_SayingWhy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        File.WriteAllBytes(Path.Combine(kit.Main, "image.bin"), [0x89, 0x50, 0x00, 0x01, 0x0A]);
        OrchestrationKit.Write(kit.Main, "joined.log", Text());
        OrchestrationKit.Write(kit.Main, ".gitattributes", "*.log merge=union\n");
        await kit.Harness.CommitAllAsync(kit.Main, "a binary file, and one merged another way", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        File.WriteAllBytes(Path.Combine(worktree, "image.bin"), [0x89, 0x50, 0x00, 0x02, 0x0A]);
        File.WriteAllBytes(Path.Combine(kit.Main, "image.bin"), [0x89, 0x50, 0x00, 0x03, 0x0A]);
        OrchestrationKit.Write(worktree, "joined.log", Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, "joined.log", Text((9, "MAIN 9")));

        var refused = await kit.FoldAsync(Agent, apply: true, Merging("image.bin", "joined.log"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  'image.bin': ", StringComparison.Ordinal)
            && line.Contains("; no three-way merge is offered: git would not merge it - ", StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith("  'joined.log': ", StringComparison.Ordinal)
            && line.EndsWith("; no three-way merge is offered: its merge attribute is 'union', and only git's own merge of text is offered here", StringComparison.Ordinal));
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, "joined.log"));
    }

    /// <summary>
    /// A link is no file to merge, whichever version is one: where the agent's base held a link at a path both trees then
    /// made a file of, the path is offered no merge, saying why, as a move of the agent's base says it.
    /// </summary>
    [Fact]
    public async Task APathTheAgentsBaseHeldAsALink_IsOfferedNoMerge_SayingWhy()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, "target.txt", "a.txt");
        var target = (await kit.Harness.RunGitAsync(kit.Main, ["hash-object", "-w", "target.txt"], Token)).StandardOutput.Trim();
        File.Delete(Path.Combine(kit.Main, "target.txt"));
        await kit.GitAsync(kit.Main, "update-index", "--add", "--cacheinfo", $"120000,{target},{Both}");
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a link");
        await kit.GitAsync(kit.Main, "checkout", "--", Both);
        var worktree = await kit.CreateAgentAsync(Agent);

        // Removed first: written through, a link would write the file it leads to.
        File.Delete(Path.Combine(kit.Main, Both));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        File.Delete(Path.Combine(worktree, Both));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));

        var refused = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Both}': ", StringComparison.Ordinal)
            && line.EndsWith("; no three-way merge is offered: one of its versions is a link, or is reached through one, and only files are merged", StringComparison.Ordinal));
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
    }

    /// <summary>A file above the bound a merge is offered for is never read into memory to be merged: none is offered, and its size is said.</summary>
    [Fact]
    public async Task AFileAboveTheBound_IsOfferedNoMerge_SayingHowLargeItIs()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")) + new string('x', (int)TwoChanges.MostBytes + (1024 * 1024)));

        var refused = await kit.FoldAsync(Agent, apply: false, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Both}': ", StringComparison.Ordinal)
            && line.EndsWith("; no three-way merge is offered: it is 17 MiB, and a merge is offered for a file up to 16 MiB", StringComparison.Ordinal));
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("to merge the", StringComparison.Ordinal));
    }

    /// <summary>
    /// A seed written by a build that kept no copies names bytes nothing holds beside it. They are looked for in the main
    /// tree's history, as a commit holds them or as it is checked out, and taken only where they hash to the digest
    /// recorded; a run that goes on to write keeps what it found, so it is looked for once.
    /// </summary>
    [Theory]
    [InlineData("src/h.txt", "\n")]
    [InlineData("src/h.dos", "\r\n")]
    public async Task ASharedPathWithNoKeptCopy_MergesWhereTheMainTreesHistoryHoldsTheRecordedBytes(string path, string ending)
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);

        string Ended(string text) => text.Replace("\n", ending, StringComparison.Ordinal);

        OrchestrationKit.Write(kit.Main, ".gitattributes", "*.dos text eol=crlf\n");
        await kit.Harness.CommitAllAsync(kit.Main, "how one kind of file is checked out", Token);
        OrchestrationKit.Write(kit.Main, path, Ended(Text((5, "AS HANDED"))));
        var worktree = await kit.CreateAgentAsync(Agent);
        var recorded = Seed(kit).Paths[path];

        // As a build that kept no copies left it: the seed, and nothing beside it. The main tree then commits what it handed.
        Directory.Delete(kit.Layout.SharedDirectory(Agent), recursive: true);
        await kit.Harness.CommitAllAsync(kit.Main, "what the agent was handed", Token);
        OrchestrationKit.Write(worktree, path, Ended(Text((2, "AGENT 2"), (5, "AS HANDED"))));
        OrchestrationKit.Write(kit.Main, path, Ended(Text((5, "AS HANDED"), (9, "MAIN 9"))));

        var dry = await kit.FoldAsync(Agent, apply: false);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(dry.Details!, line => line.EndsWith($"a three-way merge of the two is clean - 1 hunk(s) from the agent, 1 from the main tree - and --merge {path} writes it", StringComparison.Ordinal));
        Assert.False(File.Exists(kit.Layout.SharedFile(Agent, recorded)), "A dry run kept a copy.");

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(recorded, FileContentHash.Of(File.ReadAllBytes(kit.Layout.SharedFile(Agent, recorded))));

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(path));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(Ended(Text((2, "AGENT 2"), (5, "AS HANDED"), (9, "MAIN 9"))), OrchestrationKit.Read(kit.Main, path));
        Assert.Equal(Ended(Text((2, "AGENT 2"), (5, "AS HANDED"), (9, "MAIN 9"))), OrchestrationKit.Read(worktree, path));
    }

    [Fact]
    public async Task ASharedPathWithNoKeptCopy_IsOfferedNoMerge_WhereTheMainTreesHistoryDoesNotHoldTheRecordedBytes_AndSaysSo()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await HandedAsync(temp, Text((5, "HANDED, AND NEVER COMMITTED")));
        var recorded = Seed(kit).Paths[Both];
        Directory.Delete(kit.Layout.SharedDirectory(Agent), recursive: true);
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        await kit.Harness.CommitAllAsync(kit.Main, "another version than the one handed", Token);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (5, "HANDED, AND NEVER COMMITTED")));

        var refused = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.EndsWith(
            $"; no three-way merge is offered: the bytes both trees shared (SHA-256 {recorded[..8]}...) were recorded by a build that kept no copy of them, and none of the "
            + "1 version(s) of the path in the last 512 commit(s) of the main tree that changed it is those bytes",
            StringComparison.Ordinal));
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
    }

    /// <summary>
    /// A version of the main tree's history git cannot check out - a filter that fails on it - is not taken for what both
    /// trees held, and stops nothing else: the path is offered no merge, saying why, and the fold's other refusals are said.
    /// </summary>
    [Fact]
    public async Task AVersionOfTheHistoryGitCannotCheckOut_LeavesThePathWithNoMerge_AndTheFoldsOtherRefusalsAreSaid()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        const string Dos = "src/h.dos";

        static string Ended(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

        OrchestrationKit.Write(kit.Main, ".gitattributes", "*.dos text eol=crlf\n");
        await kit.Harness.CommitAllAsync(kit.Main, "how one kind of file is checked out", Token);
        OrchestrationKit.Write(kit.Main, Dos, Ended(Text((5, "AS HANDED"))));
        var worktree = await kit.CreateAgentAsync(Agent);
        var recorded = Seed(kit).Paths[Dos];
        Directory.Delete(kit.Layout.SharedDirectory(Agent), recursive: true);
        await kit.Harness.CommitAllAsync(kit.Main, "what the agent was handed", Token);
        OrchestrationKit.Write(worktree, Dos, Ended(Text((2, "AGENT 2"), (5, "AS HANDED"))));
        OrchestrationKit.Write(kit.Main, Dos, Ended(Text((5, "AS HANDED"), (9, "MAIN 9"))));
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.Write(kit.Main, "b.txt", "two\nmain edit\n");

        // The version the history holds, as git stores it: checked out, it is what the seed recorded.
        var stored = Encoding.UTF8.GetBytes(Text((5, "AS HANDED")));
        const string Failed = "Could not read 'src/h.dos' as git would check it out: error: external filter 'smudge' failed";
        var git = new InterceptingGitClient(kit.Harness.GitClient)
        {
            CheckOutFailure = (_, staged) => staged.AsSpan().SequenceEqual(stored) ? new HarnessException(HarnessExit.CommandFailed, Failed) : null,
        };
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, kit.Harness.AnchorRegistryService, git);

        var refused = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, Merging(Dos), apply: true, Token);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Dos}': ", StringComparison.Ordinal) && line.EndsWith(
            $"; no three-way merge is offered: the bytes both trees shared (SHA-256 {recorded[..8]}...) were recorded by a build that kept no copy of them, and none of the "
            + $"1 version(s) of the path in the last 512 commit(s) of the main tree that changed it is those bytes - 1 of them could not be checked out to compare: {Failed}",
            StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith("  'b.txt': the main tree changed it after the agent's base", StringComparison.Ordinal));
        Assert.Equal(Ended(Text((5, "AS HANDED"), (9, "MAIN 9"))), OrchestrationKit.Read(kit.Main, Dos));
    }

    [Fact]
    public async Task AKeptCopyThatNoLongerHashesToItsName_IsNotUsed_AndSaid()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await HandedAsync(temp);
        var copy = kit.Layout.SharedFile(Agent, Seed(kit).Paths[Both]);
        File.WriteAllText(copy, Text((9, "MAIN 9")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        var refused = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        // Taken for what both held, the changed copy would make the main tree's change no change, and the merge the agent's file.
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.EndsWith(
            $"; no three-way merge is offered: its kept copy '{copy}' no longer holds the bytes recorded for it (SHA-256 {Path.GetFileName(copy)[..8]}...)",
            StringComparison.Ordinal));
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
    }

    /// <summary>
    /// Both copies of a merged path are asked to be what was weighed before either is written: an agent still at work that
    /// saved the file meanwhile stops the fold, and the main tree's file is as it was.
    /// </summary>
    [Fact]
    public async Task AMergedFileChangedInTheAgentsWorktreeAfterItWasWeighed_StopsTheFold_WithTheMainTreesFileAsItWas()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        // The main tree's copy is read when it is weighed, when its size is asked, and when the write asks whether it is
        // still what was weighed: the agent saves its own just before that third.
        var saving = new MeanwhileFileSystem(
            kit.Harness.FileSystem,
            Path.Combine(kit.Main, Both),
            read: 3,
            () => File.WriteAllText(Path.Combine(worktree, Both), Text((2, "AGENT 2"), (4, "SAVED MEANWHILE"))));
        var agents = kit.Harness.Agents(saving, kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, Merging(Both), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(
            $"Folding agent 'ag' of 'o1' stopped part way, after writing 0 of 0 path(s), merging 0 of 1 and removing 0 of 0: '{Both}' holds other content in the agent's worktree "
            + "now than when it was measured, so it was not written over. What it wrote is in the main tree and recorded as shared, and its rows were not applied; run it again once "
            + "that is dealt with - a fold run again measures the rest afresh.",
            stopped.Message);
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2"), (4, "SAVED MEANWHILE")), OrchestrationKit.Read(worktree, Both));
        Assert.False(Seed(kit).Paths.ContainsKey(Both));
    }

    /// <summary>
    /// A file is its content and whether it can be run: a merged file either tree made runnable after it was weighed is
    /// not what was weighed, and stops the fold with neither copy written, as a change of its content does.
    /// </summary>
    [Theory]
    [InlineData(true, "the main tree")]
    [InlineData(false, "the agent's worktree")]
    public async Task AMergedFileMadeRunnableAfterItWasWeighed_StopsTheFold_WithNeitherCopyWritten(bool inMain, string tree)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        var changed = Path.Combine(inMain ? kit.Main : worktree, Both);
        var meanwhile = new MeanwhileFileSystem(
            kit.Harness.FileSystem,
            Path.Combine(kit.Main, Both),
            read: 3,
            () => kit.Harness.FilePermissions.SetExecutable(changed, executable: true));
        var agents = kit.Harness.Agents(meanwhile, kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, Merging(Both), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains($": '{Both}' can now be run in {tree}, which it could not when it was measured, so it was not written over.", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
        Assert.False(Seed(kit).Paths.ContainsKey(Both));
    }

    /// <summary>
    /// --merge reaches the fold from the command line, once for each path, and a path no tree could hold is refused there as
    /// a usage error. The line that names the command taking every clean merge keeps every option the fold was given.
    /// </summary>
    [Fact]
    public async Task TheCommandLine_TakesMergeOnceForEachPath_AndTheLineNamingEveryCleanMergeKeepsTheOtherOptions()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        const string Other = "src/n.txt";
        const string Settled = "notes/a b.txt";
        OrchestrationKit.Write(kit.Main, Both, Text());
        OrchestrationKit.Write(kit.Main, Other, Text());
        await kit.Harness.CommitAllAsync(kit.Main, "two files two will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);

        foreach (var path in new[] { Both, Other })
        {
            OrchestrationKit.Write(worktree, path, Text((2, "AGENT 2")));
            OrchestrationKit.Write(kit.Main, path, Text((9, "MAIN 9")));
        }

        OrchestrationKit.Write(worktree, Settled, "the agent's\n");
        OrchestrationKit.Write(kit.Main, Settled, "reconciled by hand\n");

        var usage = await CliRunner.RunAsync(["fold-agent", "o1", Agent, "--merge", "../outside.txt"], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.UsageError, usage.ExitCode);
        Assert.Contains("--merge", usage.StandardOutput + usage.StandardError, StringComparison.Ordinal);

        var dry = await CliRunner.RunAsync(["fold-agent", "o1", Agent, "--merge", Both, "--settled", Settled], Token, workingDirectory: kit.Main);

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(
            $"to merge the 1 path(s) that merge clean: '{ToolPackage.Command} fold-agent o1 {Agent} --merge {Both} --merge {Other} --settled \"{Settled}\"'",
            dry.StandardOutput,
            StringComparison.Ordinal);

        var applied = await CliRunner.RunAsync(["fold-agent", "o1", Agent, "--apply", "--merge", Both, "--merge", Other, "--settled", Settled], Token, workingDirectory: kit.Main);

        Assert.True(applied.Succeeded, applied.StandardOutput + applied.StandardError);

        foreach (var path in new[] { Both, Other })
        {
            Assert.Equal(Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(kit.Main, path));
            Assert.Equal(Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(worktree, path));
        }

        Assert.Equal("reconciled by hand\n", OrchestrationKit.Read(kit.Main, Settled));
        Assert.Equal("reconciled by hand\n", OrchestrationKit.Read(worktree, Settled));
    }

    /// <summary>
    /// The merge reaches the main tree first and is recorded only once both trees hold it. Stopped between the two, the
    /// same command run again merges what the main tree now holds with the agent's copy, and comes to the same bytes.
    /// </summary>
    [Fact]
    public async Task AFoldThatReachedTheMainTreeAndNotTheAgentsWorktree_RunAgain_LeavesBothHoldingTheSameMerge()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        var merged = Text((2, "AGENT 2"), (9, "MAIN 9"));
        var agents = kit.Harness.Agents(new UnwritableFile(kit.Harness.FileSystem, Path.Combine(worktree, Both)), kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, Merging(Both), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Contains("merging 0 of 1", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(worktree, Both));
        Assert.False(Seed(kit).Paths.ContainsKey(Both));

        var again = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Both));
        Assert.Equal(Digest(merged), Seed(kit).Paths[Both]);
    }

    [Fact]
    public async Task AnOptionNamingNoPathTheFoldWeighs_IsRefused_AndOneNamingAPathWithNothingToMerge_IsLetStand()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var refused = await kit.FoldAsync(Agent, apply: true, Merging("b.txt", "src/typo.txt"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(["1 problem(s), and nothing was written:", "  --merge 'src/typo.txt' names no path this fold weighs, so it merges nothing: check its spelling"], refused.Details!.Take(2));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var applied = await kit.FoldAsync(Agent, apply: true, Merging("b.txt"));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    [Fact]
    public async Task APathNamedBothToBeMergedAndAsSettled_IsAUsageError_BeforeAnythingIsRead()
    {
        using var temp = new TempDirectory();
        var (kit, _) = await AtItsBaseAsync(temp);

        foreach (var deleting in new[] { false, true })
        {
            var allowances = new FoldAllowances { Merge = [Both], Settled = [$"{Both}/"] };
            var usage = deleting ? await kit.DeleteAsync(Agent, apply: false, allowances) : await kit.FoldAsync(Agent, apply: false, allowances);

            Assert.Equal(HarnessExit.UsageError, usage.ExitCode);
            Assert.Equal($"'{Both}' is named with both --merge and --settled: a path is merged for you, or reconciled by hand, never both.", usage.Message);
        }

        var unnameable = await kit.FoldAsync(Agent, apply: false, Merging("../outside.txt"));

        Assert.Equal(HarnessExit.UsageError, unnameable.ExitCode);
        Assert.Contains("--merge", unnameable.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An anchor registry changes only through the rows an agent files: both trees having changed one as a file, it is
    /// refused as it always was, with no word of a merge, whatever is named. The arm registry is merged by arm and never by
    /// line: one whose versions cannot be read as a registry is refused, saying so, whatever is named.
    /// </summary>
    [Fact]
    public async Task ARegistryBothTreesChangedAsAFile_IsRefused_WhateverIsNamedToBeMerged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        const string anchors = AnchorSettings.DefaultPendingAnchorsPath;
        const string arms = "arms/registry.md";
        kit.Harness.WriteConfig(kit.Main, new HarnessConfig { Worktrees = OrchestrationKit.Settings, Mutations = new MutationSettings { Registry = arms } });
        OrchestrationKit.Write(kit.Main, arms, Text());
        await kit.Harness.CommitAllAsync(kit.Main, "an arm registry", Token);
        var worktree = await kit.CreateAgentAsync(Agent);

        foreach (var registry in new[] { anchors, arms })
        {
            File.AppendAllText(Path.Combine(worktree, registry), "an edit of the agent's own\n");
            File.WriteAllText(Path.Combine(kit.Main, registry), "an edit of the main tree's\n" + File.ReadAllText(Path.Combine(kit.Main, registry)));
        }

        var refused = await kit.FoldAsync(Agent, apply: true, Merging(anchors, arms));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains($"  --merge '{anchors}' names an anchor registry, which a fold changes only through the rows the agent files, never as a file", refused.Details!);
        Assert.Contains($"  --merge '{arms}' names the arm registry, which is merged by arm without it and never by line: drop it", refused.Details!);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{anchors}': the main tree changed it after the agent's base", StringComparison.Ordinal)
            && line.EndsWith("so writing the agent's copy would lose that change", StringComparison.Ordinal));
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{arms}': the main tree changed it after the agent's base", StringComparison.Ordinal)
            && line.Contains("so writing the agent's copy would lose that change; merged by arm and never by line, and it cannot be read as a registry - ", StringComparison.Ordinal)
            && line.EndsWith($": reconcile it by hand, then --settled {arms}", StringComparison.Ordinal));

        foreach (var registry in new[] { anchors, arms })
        {
            Assert.StartsWith("an edit of the main tree's\n", OrchestrationKit.Read(kit.Main, registry));
        }

        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("to merge the", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DeletingAnAgent_TakesTheOption_AndItsProofThatNothingIsLeftHoldsAfterAMerge()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        var refused = await kit.DeleteAsync(Agent, apply: false, new FoldAllowances());

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal($"to merge the 1 path(s) that merge clean: '{ToolPackage.Command} delete-agent o1 {Agent} --merge {Both}'", refused.Details![^1]);

        var dry = await kit.DeleteAsync(Agent, apply: false, Merging(Both));

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: deleting agent 'ag' of 'o1' folds 0 path(s), merges 1, removes 0 and applies 0 row(s), ", dry.Message);

        var deleted = await kit.DeleteAsync(Agent, apply: true, Merging(Both));

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal(AgentStates.Deleted, kit.Record(Agent).State);
        Assert.Equal(Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.False(Directory.Exists(worktree));
    }

    /// <summary>
    /// Deleting an agent keeps what it finds in the main tree's history for a seed with no kept copy, and the bytes of the
    /// merge it writes, as any fold does: a deletion that stops before its closing leaves the agent live, its seed naming
    /// the merge, which the main tree holds uncommitted and no history holds - and the next fold of the path both trees
    /// then change merges from the copy kept.
    /// </summary>
    [Fact]
    public async Task ADeletionThatStopsAfterItsFold_KeepsWhatItMerged_AndTheNextFoldMergesFromIt()
    {
        using var temp = new TempDirectory();
        using var claude = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.ClaudeConfigDirectory = claude.Path;
        var transcript = claude.WriteFile(Path.Combine("projects", "some-project", "sess1.jsonl"), "{}\n");
        OrchestrationKit.Write(kit.Main, Both, Text((5, "AS HANDED")));
        var worktree = await kit.CreateAgentAsync(Agent, session: "sess1");
        var recorded = Seed(kit).Paths[Both];
        File.Delete(kit.Layout.SharedFile(Agent, recorded));
        await kit.Harness.CommitAllAsync(kit.Main, "what the agent was handed", Token);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (5, "AS HANDED")));
        OrchestrationKit.Write(kit.Main, Both, Text((5, "AS HANDED"), (9, "MAIN 9")));
        var agents = kit.Harness.Agents(new UnreadableFile(kit.Harness.FileSystem, transcript), kit.Harness.AnchorRegistryService);

        var stopped = await agents.DeleteAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, Merging(Both), apply: true, discardUncommitted: false, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not deleted, because its transcripts could not be kept:", stopped.Message, StringComparison.Ordinal);
        Assert.Equal(Text((2, "AGENT 2"), (5, "AS HANDED"), (9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Equal(Digest(Text((2, "AGENT 2"), (5, "AS HANDED"), (9, "MAIN 9"))), Seed(kit).Paths[Both]);
        Assert.True(File.Exists(kit.Layout.SharedFile(Agent, Seed(kit).Paths[Both])), "A deletion kept no copy of the merge it wrote.");

        OrchestrationKit.Write(kit.Main, Both, Text((2, "AGENT 2"), (5, "AS HANDED"), (9, "MAIN 9"), (11, "MAIN 11")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (4, "AGENT 4"), (5, "AS HANDED"), (9, "MAIN 9")));

        var folded = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal(Text((2, "AGENT 2"), (4, "AGENT 4"), (5, "AS HANDED"), (9, "MAIN 9"), (11, "MAIN 11")), OrchestrationKit.Read(kit.Main, Both));
    }

    [Fact]
    public async Task AFoldThatMergesNothing_SaysWhatItSaidBefore()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await HandedAsync(temp);
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var dry = await kit.FoldAsync(Agent, apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Equal("dry run: folding agent 'ag' of 'o1' writes 1 path(s) into the main tree, removes 0 and applies 0 row(s); pass --apply to write them", dry.Message);
        Assert.Equal(
            [
                "1 path(s) it shares with the main tree, handed to it or folded before: 0 its own, 1 inherited, 0 already in the main tree, 0 deleted, 0 settled",
                "1 inherited path(s) left out; 1 path(s) are its own:",
                "  b.txt   (not handed to it, nor folded before)",
            ],
            dry.Details!);

        var applied = await kit.FoldAsync(Agent, apply: true);

        Assert.StartsWith("folded agent 'ag' of 'o1': wrote 1 path(s) into the main tree, removed 0 and applied 0 row(s); ", applied.Message);
    }

    /// <summary>
    /// Whether a file can be run is merged with its content, three ways: the merged bit is the one that differs from what
    /// both held, where either side's does.
    /// </summary>
    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, false, true, true)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, false, false)]
    public void TheMergedBit_IsTheOneThatDiffersFromWhatBothHeld_WhereEitherSidesDoes(bool both, bool ours, bool theirs, bool merged)
    {
        Assert.Equal(merged, TwoChanges.MergedBit(both, ours, theirs));
    }

    [Fact]
    public async Task AFileTheAgentMadeRunnable_AndTheMainTreeEdited_IsMergedAsTheMainTreesTextWithTheAgentsBit_InBothTrees()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        var refused = await kit.FoldAsync(Agent, apply: false);

        // Two copies that differ in the bit are two changes of one file, merged like any other: never refused for the bit.
        Assert.Contains(refused.Details!, line => line.EndsWith($"a three-way merge of the two is clean - 0 hunk(s) from the agent, 1 from the main tree - and --merge {Both} writes it", StringComparison.Ordinal));

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 0 hunk(s) from the agent, 1 from the main tree; it can be run, as the agent made it)", applied.Details!);

        // The file replaced in the main tree could not be run, and the one written can: the bit is the merge's.
        foreach (var tree in new[] { kit.Main, worktree })
        {
            Assert.Equal(Text((9, "MAIN 9")), OrchestrationKit.Read(tree, Both));
            Assert.True(permissions.IsExecutable(Path.Combine(tree, Both)), $"The merged file cannot be run in '{tree}'.");
        }

        Assert.Equal([Both], Seed(kit).Executable);
    }

    [Fact]
    public async Task AFileTheMainTreeMadeNoLongerRunnable_AndTheAgentEdited_TakesTheMainTreesBit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        OrchestrationKit.Write(kit.Main, Both, Text());
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: true);
        await kit.Harness.CommitAllAsync(kit.Main, "a file that can be run", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: false);

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        // What both held is the base's entry, which could be run: the main tree changed that, and the agent did not.
        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 0 from the main tree; it can no longer be run, as the main tree made it)", applied.Details!);

        foreach (var tree in new[] { kit.Main, worktree })
        {
            Assert.Equal(Text((2, "AGENT 2")), OrchestrationKit.Read(tree, Both));
            Assert.False(permissions.IsExecutable(Path.Combine(tree, Both)), $"The merged file can be run in '{tree}'.");
        }

        Assert.Null(Seed(kit).Executable);
    }

    [Fact]
    public async Task AFileBothMadeRunnable_EachEditingOtherLines_IsRunnableOnce_AndItsLineSaysNothingOfTheBit()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree) = await AtItsBaseAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: true);

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)", applied.Details!);
        Assert.True(permissions.IsExecutable(Path.Combine(kit.Main, Both)));
        Assert.True(permissions.IsExecutable(Path.Combine(worktree, Both)));
        Assert.Equal([Both], Seed(kit).Executable);
    }

    /// <summary>
    /// The bit both held is the seed's for a path the agent shares, whatever its base's entry says, and a second fold
    /// merges the bit from what the first recorded - the bit the merge was written with, not the one the seed held before.
    /// </summary>
    [Fact]
    public async Task TheBitBothHeld_IsTheSeedsForASharedPath_AndASecondFoldMergesItFromWhatTheFirstRecorded()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var permissions = kit.Harness.FilePermissions;
        OrchestrationKit.Write(kit.Main, Both, Text());
        await kit.Harness.CommitAllAsync(kit.Main, "a file that cannot be run", Token);

        // Handed as the main tree made it since: one that can be run, which is what the seed records both held.
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: true);
        var worktree = await kit.CreateAgentAsync(Agent);
        Assert.Equal([Both], Seed(kit).Executable);

        // The main tree takes the bit away again, and edits; the agent only edits. Read from the base's entry, which could
        // not be run, the agent's bit would be taken for its change.
        permissions.SetExecutable(Path.Combine(kit.Main, Both), executable: false);
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);

        var first = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(first.Succeeded, OrchestrationKit.Describe(first));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree; it can no longer be run, as the main tree made it)", first.Details!);
        Assert.False(permissions.IsExecutable(Path.Combine(kit.Main, Both)));
        Assert.False(permissions.IsExecutable(Path.Combine(worktree, Both)));
        Assert.Null(Seed(kit).Executable);

        // Both edit again, and the agent makes it one that can be run: its change, against what the first fold recorded.
        OrchestrationKit.Write(kit.Main, Both, Text((2, "AGENT 2"), (9, "MAIN 9"), (11, "MAIN 11")));
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2"), (4, "AGENT 4"), (9, "MAIN 9")));
        permissions.SetExecutable(Path.Combine(worktree, Both), executable: true);

        var second = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(second.Succeeded, OrchestrationKit.Describe(second));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree; it can be run, as the agent made it)", second.Details!);
        Assert.True(permissions.IsExecutable(Path.Combine(kit.Main, Both)));
        Assert.True(permissions.IsExecutable(Path.Combine(worktree, Both)));
        Assert.Equal([Both], Seed(kit).Executable);
    }

    [Fact]
    public async Task OnWindows_NoBitIsReadMergedOrSet_AndAMergeIsAsItIsWithoutOne()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Linux and macOS keep the bit; see those tests.");

        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        OrchestrationKit.Write(kit.Main, Both, Text());
        await kit.GitAsync(kit.Main, "add", "-A");

        // The commit says the file can be run; no file here says so, and nothing is made of it.
        await kit.GitAsync(kit.Main, "update-index", "--chmod=+x", Both);
        await kit.GitAsync(kit.Main, "commit", "--quiet", "-m", "a file git holds as one that can be run");
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(worktree, Both, Text((2, "AGENT 2")));
        OrchestrationKit.Write(kit.Main, Both, Text((9, "MAIN 9")));

        var applied = await kit.FoldAsync(Agent, apply: true, Merging(Both));

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Both}   (--merge: 1 hunk(s) from the agent, 1 from the main tree)", applied.Details!);
        Assert.Equal(Text((2, "AGENT 2"), (9, "MAIN 9")), OrchestrationKit.Read(kit.Main, Both));
        Assert.Null(Seed(kit).Executable);
    }

    /// <summary>The real file system, except that something is done just before one file is read for the given time: an agent still at work.</summary>
    internal sealed class MeanwhileFileSystem(IFileSystem inner, string watched, int read, Action meanwhile) : PassThroughFileSystem(inner)
    {
        private int _reads;

        public override Stream OpenRead(string path)
        {
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(watched), StringComparison.OrdinalIgnoreCase) && Interlocked.Increment(ref _reads) == read)
            {
                meanwhile();
            }

            return base.OpenRead(path);
        }
    }

    /// <summary>The real file system, except that one file cannot be read, as while another program holds it.</summary>
    private sealed class UnreadableFile(IFileSystem inner, string held) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => string.Equals(Path.GetFullPath(path), Path.GetFullPath(held), StringComparison.OrdinalIgnoreCase)
                ? throw new IOException($"The process cannot access the file '{path}' because it is being used by another process.")
                : base.OpenRead(path);
    }

    /// <summary>The real file system, except that nothing can be put in place at one path, as while another program holds the file open.</summary>
    internal sealed class UnwritableFile(IFileSystem inner, string file) : PassThroughFileSystem(inner)
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
}
