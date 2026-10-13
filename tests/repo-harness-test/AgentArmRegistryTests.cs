using System.Collections.Concurrent;
using System.Text;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// The arm registry both trees changed, met by a fold or a move of an agent's base: merged by arm with no option given and
/// never by line, judged as a registry of the tree the command leaves, and refused - saying why - where two sides changed an
/// arm differently, a version cannot be read as a registry, or the merged text does not read as one.
/// </summary>
public sealed class AgentArmRegistryTests
{
    private const string Agent = "ag";

    private const string Registry = "mutations/arms.txt";

    private const string Texts = "mutations/texts";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The configuration naming the registry and its texts' directory.</summary>
    private static HarnessConfig Config => new()
    {
        Worktrees = OrchestrationKit.Settings,
        Mutations = new MutationSettings { Registry = Registry, TextDirectory = Texts },
    };

    /// <summary>An arm's two rows: what it mutates, and the case it reddens.</summary>
    private static string Arm(string id, string? @case = null)
        => $"A | {id} | src/{id}.cpp | {Texts}/{id}.before | {Texts}/{id}.after | TEST-RED | app | app_tests | 1 | {Texts}/{id}.diag | the {id} is pinned\n"
            + $"C | {id} | {@case ?? $"Suite.{id}"} | red\n";

    /// <summary>A registry of <paramref name="arms"/>, under a heading.</summary>
    private static string Text(params string[] arms) => "# arms\n\n" + string.Concat(arms);

    private static string Crlf(string text) => text.Replace("\n", "\r\n", StringComparison.Ordinal);

    private static SeedRecord Seed(OrchestrationKit kit) => kit.Harness.OrchestrationStore.ReadSeed(kit.Layout, Agent)!;

    private static string Digest(string text) => FileContentHash.Of(Encoding.UTF8.GetBytes(text));

    /// <summary>Writes the three texts arm <paramref name="id"/> cites into <paramref name="root"/>.</summary>
    private static void WriteTexts(string root, string id)
    {
        OrchestrationKit.Write(root, $"{Texts}/{id}.before", $"x <= {id}");
        OrchestrationKit.Write(root, $"{Texts}/{id}.after", $"x < {id}");
        OrchestrationKit.Write(root, $"{Texts}/{id}.diag", $"{id} exceeded");
    }

    /// <summary>
    /// A repository whose configuration names the registry, holding <paramref name="arms"/> and their texts, committed,
    /// with agent <see cref="Agent"/> made from it; or holding no registry at all, where none are given.
    /// </summary>
    private static async Task<(OrchestrationKit Kit, string Worktree)> RegistryAsync(TempDirectory temp, params string[] arms)
    {
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.WriteConfig(kit.Main, Config);

        if (arms.Length > 0)
        {
            OrchestrationKit.Write(kit.Main, Registry, Text([.. arms.Select(id => Arm(id))]));

            foreach (var id in arms)
            {
                WriteTexts(kit.Main, id);
            }
        }

        await kit.Harness.CommitAllAsync(kit.Main, "an arm registry", Token);

        return (kit, await kit.CreateAgentAsync(Agent));
    }

    /// <summary>The main tree commits arm <paramref name="id"/> after the arms <paramref name="before"/>, with its texts.</summary>
    private static async Task MainAddsAsync(OrchestrationKit kit, string id, params string[] before)
    {
        OrchestrationKit.Write(kit.Main, Registry, Text([.. before.Select(arm => Arm(arm)), Arm(id)]));
        WriteTexts(kit.Main, id);
        await kit.Harness.CommitAllAsync(kit.Main, $"the main tree's arm {id}", Token);
    }

    private static Task<CommandOutcome> RebaseAsync(OrchestrationKit kit, bool apply, RebaseRequest? request = null)
        => kit.Harness.AgentService.RebaseAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, request ?? new RebaseRequest(), apply, Token);

    /// <summary>
    /// A registry both trees added arms to - at one place, which git's merge of text would take for a conflict - is merged
    /// by arm with no option given: said in the dry run and the report, written into both trees, the main tree's addition
    /// first, and recorded as shared at the merged bytes. The texts the agent's arm cites are written by the same fold,
    /// and the merged registry is judged by the main tree as the fold leaves it, so it reads as a registry.
    /// </summary>
    [Fact]
    public async Task ARegistryBothTreesAddedArmsTo_IsMergedByArm_Unasked_IntoBothTrees_AndRecordedAsShared()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");
        var merged = Text(Arm("alpha"), Arm("beta"), Arm("gamma"));
        const string Listed = $"  {Registry}   (merged by arm: 1 from the agent (1 added), 1 from the main tree (1 added); 3 arm(s) in all; it reads as a registry)";

        var dry = await kit.FoldAsync(Agent, apply: false);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Contains(Listed, dry.Details!);
        Assert.Equal(Text(Arm("alpha"), Arm("beta")), OrchestrationKit.Read(kit.Main, Registry));

        var applied = await kit.FoldAsync(Agent, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains(Listed, applied.Details!);
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Registry));
        Assert.Equal("x <= gamma", OrchestrationKit.Read(kit.Main, $"{Texts}/gamma.before"));
        Assert.Equal(Digest(merged), Seed(kit).Paths[Registry]);
    }

    /// <summary>An arm both trees changed differently refuses the fold, nothing written, the arm shown with each side's version.</summary>
    [Fact]
    public async Task AnArmBothTreesChangedDifferently_RefusesTheFold_ShowingTheArmAsEachHoldsIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha", "beta");
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha"), Arm("beta", "Suite.betaMain")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's case", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("beta", "Suite.betaAgent")));

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': the main tree changed it after the agent's base", StringComparison.Ordinal)
            && line.EndsWith($"; merged by arm, it conflicts in 1 place(s), shown below: reconcile it by hand, then --settled {Registry}", StringComparison.Ordinal));
        Assert.Contains($"conflicts of '{Registry}' merged by arm, 1 place(s):", refused.Details!);
        Assert.Contains("  arm 'beta': changed by both, each differently", refused.Details!);
        Assert.Contains("    what both held, line 6:  C | beta | Suite.beta | red", refused.Details!);
        Assert.Contains("    the main tree, line 6:   C | beta | Suite.betaMain | red", refused.Details!);
        Assert.Contains("    the agent, line 6:       C | beta | Suite.betaAgent | red", refused.Details!);
        Assert.DoesNotContain(refused.Details!, line => line.StartsWith("to merge the", StringComparison.Ordinal));
        Assert.Equal(Text(Arm("alpha"), Arm("beta", "Suite.betaMain")), OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(Text(Arm("alpha"), Arm("beta", "Suite.betaAgent")), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// A merge by arm that is clean, whose text cites texts no tree will hold once the fold is written, does not read as a
    /// registry: the fold is refused, nothing written, every problem listed as check-mutations words it.
    /// </summary>
    [Fact]
    public async Task AMergedRegistryCitingATextNoTreeWillHold_RefusesTheFold_EveryProblemListed()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("delta")));

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': ", StringComparison.Ordinal)
            && line.EndsWith("; merged by arm, it does not read as a registry - 3 problem(s), listed below", StringComparison.Ordinal));
        Assert.Equal(
            [
                $"The arms registry '{Registry}', as the fold would write it, cannot be swept: 3 problem(s), each to fix:",
                $"  - line 7: text '{Texts}/delta.before' is not a file in the tree",
                $"  - line 7: text '{Texts}/delta.after' is not a file in the tree",
                $"  - line 7: text '{Texts}/delta.diag' is not a file in the tree",
            ],
            refused.Details!.SkipWhile(line => !line.StartsWith("The arms registry", StringComparison.Ordinal)).Take(4));
        Assert.Equal(Text(Arm("alpha"), Arm("beta")), OrchestrationKit.Read(kit.Main, Registry));
    }

    /// <summary>A registry both trees made, neither holding one at the agent's base, is merged by arm from nothing.</summary>
    [Fact]
    public async Task ARegistryBothTreesMade_IsMergedByArm_FromNothing()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp);
        await MainAddsAsync(kit, "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var applied = await kit.FoldAsync(Agent, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains($"  {Registry}   (merged by arm: 1 from the agent (1 added), 1 from the main tree (1 added); 2 arm(s) in all; it reads as a registry)", applied.Details!);
        Assert.Equal(Text(Arm("alpha"), Arm("gamma")), OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(Text(Arm("alpha"), Arm("gamma")), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// A registry the two shared whose shared bytes cannot be found - no copy kept, and no version of the main tree's
    /// history holding them - is offered no merge: it is never merged as though there had been nothing before, which would
    /// bring back the arm the main tree removed as the agent's addition.
    /// </summary>
    [Fact]
    public async Task ASharedRegistryWhoseSharedBytesCannotBeFound_IsOfferedNoMerge_AndAnArmTheMainTreeRemovedStaysRemoved()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.WriteConfig(kit.Main, Config);
        WriteTexts(kit.Main, "alpha");
        WriteTexts(kit.Main, "beta");
        await kit.Harness.CommitAllAsync(kit.Main, "the configuration and the texts", Token);

        // Handed and never committed; and, as a build that kept no copies left it, the seed with nothing beside it.
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha"), Arm("beta")));
        var worktree = await kit.CreateAgentAsync(Agent);
        var recorded = Seed(kit).Paths[Registry];
        Directory.Delete(kit.Layout.SharedDirectory(Agent), recursive: true);
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha")));
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree removes beta", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("beta"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': the main tree changed it after it was handed to the agent", StringComparison.Ordinal)
            && line.EndsWith(
                $"; no three-way merge is offered: the bytes both trees shared (SHA-256 {recorded[..8]}...) were recorded by a build that kept no copy of them, and none of the "
                + "1 version(s) of the path in the last 512 commit(s) of the main tree that changed it is those bytes",
                StringComparison.Ordinal));
        Assert.Equal(Text(Arm("alpha")), OrchestrationKit.Read(kit.Main, Registry));
    }

    /// <summary>
    /// A version that cannot be read as a registry - a row naming no arm - refuses the fold with the problem, and the path
    /// is never merged by line: named with --merge, the option itself is refused.
    /// </summary>
    [Fact]
    public async Task AVersionThatCannotBeReadAsARegistry_RefusesTheFold_AndIsNeverMergedByLine()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), "C\n"));

        var refused = await kit.FoldAsync(Agent, apply: true, new FoldAllowances { Merge = [Registry] });

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': ", StringComparison.Ordinal)
            && line.EndsWith(
                "; merged by arm and never by line, and it cannot be read as a registry - line 5 of the text of the agent: a row names no arm: "
                + $"reconcile it by hand, then --settled {Registry}",
                StringComparison.Ordinal));
        Assert.Contains($"  --merge '{Registry}' names the arm registry, which is merged by arm without it and never by line: drop it", refused.Details!);
        Assert.Equal(Text(Arm("alpha"), Arm("beta")), OrchestrationKit.Read(kit.Main, Registry));
    }

    /// <summary>
    /// The merged registry is written with the bytes the merge gave, ending its lines as the main tree's file does - though
    /// git stores the file with other endings, and the agent's worktree checks it out with them.
    /// </summary>
    [Fact]
    public async Task TheMergedRegistry_EndsItsLinesAsTheMainTreesFileDoes()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        OrchestrationKit.Write(kit.Main, ".gitattributes", $"{Registry} text eol=lf\n");
        await kit.Harness.CommitAllAsync(kit.Main, "how the registry is stored", Token);
        OrchestrationKit.Write(kit.Main, Registry, Crlf(Text(Arm("alpha"), Arm("beta"))));
        WriteTexts(kit.Main, "beta");
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's arm, written with its own endings", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var applied = await kit.FoldAsync(Agent, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Equal(Crlf(Text(Arm("alpha"), Arm("beta"), Arm("gamma"))), OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(Crlf(Text(Arm("alpha"), Arm("beta"), Arm("gamma"))), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// A fold of two hundred paths whose merged registry cites three of them reads only what the registry's validation asks
    /// for to judge it: the two texts it reads, of all the fold writes.
    /// </summary>
    [Fact]
    public async Task AFoldOfTwoHundredPaths_ReadsOnlyTheTextsTheMergedRegistryCites_ToJudgeIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        for (var file = 0; file < 197; file++)
        {
            OrchestrationKit.Write(worktree, $"src/f{file:D3}.txt", $"file {file}\n");
        }

        var reads = new ReadsUnder(kit.Harness.FileSystem, worktree);
        var agents = kit.Harness.Agents(reads, kit.Harness.AnchorRegistryService);

        var dry = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: false, Token);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.StartsWith("dry run: folding agent 'ag' of 'o1' writes 200 path(s) into the main tree, merges 1,", dry.Message);
        Assert.Equal([$"{Texts}/gamma.before", $"{Texts}/gamma.diag"], reads.Read.Where(path => path != Registry).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A merge by arm that reached the main tree and not the agent's worktree, run again, merges the main tree's registry -
    /// the merge - with the agent's, which gives the merge again: both trees then hold it, and it is recorded as shared.
    /// </summary>
    [Fact]
    public async Task AFoldThatReachedTheMainTreeAndNotTheAgentsWorktree_RunAgain_LeavesBothHoldingTheSameRegistry()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");
        var merged = Text(Arm("alpha"), Arm("beta"), Arm("gamma"));
        var agents = kit.Harness.Agents(new AgentFoldMergeTests.UnwritableFile(kit.Harness.FileSystem, Path.Combine(worktree, Registry)), kit.Harness.AnchorRegistryService);

        var stopped = await agents.FoldAsync(kit.Main, OrchestrationKit.Orchestrator, Agent, new FoldAllowances(), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(Text(Arm("alpha"), Arm("gamma")), OrchestrationKit.Read(worktree, Registry));

        var again = await kit.FoldAsync(Agent, apply: true);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal(merged, OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(merged, OrchestrationKit.Read(worktree, Registry));
        Assert.Equal(Digest(merged), Seed(kit).Paths[Registry]);
    }

    /// <summary>
    /// A move of the agent's base merges the registry both changed by arm, unasked, into its worktree as its own change on
    /// the new base, judged by the worktree as the move leaves it - the texts the new base brings in it - and records
    /// nothing; named with --merge, the option is refused.
    /// </summary>
    [Fact]
    public async Task ARebase_MergesTheRegistryByArm_JudgedByTheWorktreeWithTheNewBasesFilesInIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var named = await RebaseAsync(kit, apply: false, new RebaseRequest { Merge = [Registry] });

        Assert.Equal(HarnessExit.Refused, named.ExitCode);
        Assert.Contains($"  --merge '{Registry}' names the arm registry, which is merged by arm without it and never by line: drop it", named.Details!);

        var moved = await RebaseAsync(kit, apply: true);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Contains($"  {Registry}   (merged by arm: 1 from the agent (1 added), 1 from the main tree (1 added); 3 arm(s) in all; it reads as a registry)", moved.Details!);
        Assert.Equal(Text(Arm("alpha"), Arm("beta"), Arm("gamma")), OrchestrationKit.Read(worktree, Registry));
        Assert.Equal("x <= beta", OrchestrationKit.Read(worktree, $"{Texts}/beta.before"));
        Assert.False(Seed(kit).Paths.ContainsKey(Registry));
    }

    /// <summary>A registry the agent made where the new base brings one is merged by arm from nothing, on a move as in a fold.</summary>
    [Fact]
    public async Task ARebase_MergesARegistryBothMade_FromNothing()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp);
        await MainAddsAsync(kit, "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var moved = await RebaseAsync(kit, apply: true);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Equal(Text(Arm("alpha"), Arm("gamma")), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// The new base's version is merged as a checkout into the agent's worktree writes it, so the merged registry ends its
    /// lines as that checkout would, not as git stores the file.
    /// </summary>
    [Fact]
    public async Task ARebase_MergesTheNewBasesVersionAsTheWorktreeChecksItOut()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.WriteConfig(kit.Main, Config);
        OrchestrationKit.Write(kit.Main, ".gitattributes", $"{Registry} text eol=crlf\n");
        OrchestrationKit.Write(kit.Main, Registry, Crlf(Text(Arm("alpha"))));
        WriteTexts(kit.Main, "alpha");
        await kit.Harness.CommitAllAsync(kit.Main, "an arm registry, checked out with its own endings", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, Registry, Crlf(Text(Arm("alpha"), Arm("beta"))));
        WriteTexts(kit.Main, "beta");
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's arm", Token);
        OrchestrationKit.Write(worktree, Registry, Crlf(Text(Arm("alpha"), Arm("gamma"))));
        WriteTexts(worktree, "gamma");

        var moved = await RebaseAsync(kit, apply: true);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Equal(Crlf(Text(Arm("alpha"), Arm("beta"), Arm("gamma"))), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>A version that cannot be read as a registry refuses the move unless --settled names the path, which keeps the agent's copy.</summary>
    [Fact]
    public async Task ARebase_RefusesARegistryThatCannotBeRead_UnlessSettled()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), "C\n"));

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': the agent changed it, and the main tree committed a change to it", StringComparison.Ordinal)
            && line.EndsWith(
                "; merged by arm and never by line, and it cannot be read as a registry - line 5 of the text of the agent: a row names no arm: "
                + $"reconcile it by hand, then --settled {Registry}",
                StringComparison.Ordinal));

        var settled = await RebaseAsync(kit, apply: true, new RebaseRequest { Settled = [Registry] });

        Assert.True(settled.Succeeded, OrchestrationKit.Describe(settled));
        Assert.Equal(Text(Arm("alpha"), "C\n"), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// Whether the registry can be run is merged with its content, by the rule a merge of text keeps: the agent made it
    /// runnable, so the merge by arm can be run in both trees, and its line says so.
    /// </summary>
    [Fact]
    public async Task ARegistryMergedByArm_TakesTheBitOfTheSideThatChangedIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");
        kit.Harness.FilePermissions.SetExecutable(Path.Combine(worktree, Registry), executable: true);

        var applied = await kit.FoldAsync(Agent, apply: true);

        Assert.True(applied.Succeeded, OrchestrationKit.Describe(applied));
        Assert.Contains(
            $"  {Registry}   (merged by arm: 1 from the agent (1 added), 1 from the main tree (1 added); 3 arm(s) in all; it can be run, as the agent made it; it reads as a registry)",
            applied.Details!);
        Assert.True(kit.Harness.FilePermissions.IsExecutable(Path.Combine(kit.Main, Registry)), "The merged registry cannot be run in the main tree.");
        Assert.True(kit.Harness.FilePermissions.IsExecutable(Path.Combine(worktree, Registry)), "The merged registry cannot be run in the agent's worktree.");
    }

    /// <summary>
    /// A registry both trees made, one copy runnable and one not, is offered no merge, saying why: with nothing either held
    /// before, neither bit is a change, and nothing says which is meant.
    /// </summary>
    [Fact]
    public async Task ARegistryBothTreesMade_OneRunnableAndOneNot_IsOfferedNoMerge_SayingWhy()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Windows keeps no execute bit with a file.");

        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp);
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha")));
        WriteTexts(kit.Main, "alpha");
        kit.Harness.FilePermissions.SetExecutable(Path.Combine(kit.Main, Registry), executable: true);
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's registry, runnable", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}' is not at the agent's base ", StringComparison.Ordinal)
            && line.EndsWith(
                "; no three-way merge is offered: both trees made it, one so that it can be run and one not, and nothing either held before says "
                + "which is meant",
                StringComparison.Ordinal));
        Assert.Equal(Text(Arm("alpha")), OrchestrationKit.Read(kit.Main, Registry));
        Assert.Equal(Text(Arm("gamma")), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// The merged registry is judged by the main tree as the fold leaves it, without each file the fold removes: a text it
    /// cites that the agent deleted is no file of that tree, and the fold is refused, nothing written.
    /// </summary>
    [Fact]
    public async Task AFoldThatRemovesATextTheMergedRegistryCites_IsRefused_TheTextListed()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");
        File.Delete(Path.Combine(worktree, Texts, "alpha.diag"));

        var refused = await kit.FoldAsync(Agent, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': ", StringComparison.Ordinal)
            && line.EndsWith("; merged by arm, it does not read as a registry - 1 problem(s), listed below", StringComparison.Ordinal));
        Assert.Equal(
            [
                $"The arms registry '{Registry}', as the fold would write it, cannot be swept: 1 problem(s), each to fix:",
                $"  - line 3: text '{Texts}/alpha.diag' is not a file in the tree",
            ],
            refused.Details!.SkipWhile(line => !line.StartsWith("The arms registry", StringComparison.Ordinal)).Take(2));
        Assert.Equal("alpha exceeded", OrchestrationKit.Read(kit.Main, $"{Texts}/alpha.diag"));
    }

    /// <summary>
    /// The merged registry is judged by every merge the fold writes, not its own alone: a text both trees changed, merged
    /// with --merge to one that holds nothing, is read as the merge leaves it, and the fold is refused.
    /// </summary>
    [Fact]
    public async Task AMergedRegistry_IsJudgedByEveryOtherMergeTheFoldWrites()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        kit.Harness.WriteConfig(kit.Main, Config);
        var diagnostic = $"{Texts}/alpha.diag";
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha")));
        WriteTexts(kit.Main, "alpha");
        OrchestrationKit.Write(kit.Main, diagnostic, "a\n\nb\n");
        await kit.Harness.CommitAllAsync(kit.Main, "an arm whose diagnostic both trees will change", Token);
        var worktree = await kit.CreateAgentAsync(Agent);
        OrchestrationKit.Write(kit.Main, diagnostic, "\nb\n");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");
        OrchestrationKit.Write(worktree, diagnostic, "a\n\n");

        var refused = await kit.FoldAsync(Agent, apply: true, new FoldAllowances { Merge = [diagnostic] });

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains($"  - line 3: the text in '{diagnostic}' holds nothing, which occurs everywhere and which every run says", refused.Details!);
        Assert.Equal("\nb\n", OrchestrationKit.Read(kit.Main, diagnostic));
    }

    /// <summary>
    /// The merged registry is judged by the worktree as the move leaves it, without each file the new base removes - said
    /// as the rebase would write it, and the move refused, nothing written.
    /// </summary>
    [Fact]
    public async Task ARebaseThatRemovesATextTheMergedRegistryCites_IsRefused_AsTheRebaseWouldWriteIt()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha", "beta");
        File.Delete(Path.Combine(kit.Main, Texts, "beta.diag"));
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha"), Arm("beta"), Arm("delta")));
        WriteTexts(kit.Main, "delta");
        await kit.Harness.CommitAllAsync(kit.Main, "the main tree's arm, and beta's diagnostic gone", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("beta"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            [
                $"The arms registry '{Registry}', as the rebase would write it, cannot be swept: 1 problem(s), each to fix:",
                $"  - line 5: text '{Texts}/beta.diag' is not a file in the tree",
            ],
            refused.Details!.SkipWhile(line => !line.StartsWith("The arms registry", StringComparison.Ordinal)).Take(2));
        Assert.Equal("beta exceeded", OrchestrationKit.Read(worktree, $"{Texts}/beta.diag"));
    }

    /// <summary>
    /// A text the new base renamed in case alone, which the merged registry cites by its new name, is the file the move
    /// writes: the registry is judged by the worktree with the old name removed and the new one written, in that order,
    /// as the move makes them, and the move goes on.
    /// </summary>
    [Fact]
    public async Task ARebaseThatRenamesACitedTextInCase_JudgesTheRegistryByTheRenamedFile()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await kit.GitAsync(kit.Main, "mv", $"{Texts}/alpha.diag", $"{Texts}/Alpha.diag");
        var renamed = Arm("alpha").Replace($"{Texts}/alpha.diag", $"{Texts}/Alpha.diag", StringComparison.Ordinal);
        OrchestrationKit.Write(kit.Main, Registry, Text(renamed, Arm("beta")));
        WriteTexts(kit.Main, "beta");
        await kit.Harness.CommitAllAsync(kit.Main, "alpha's diagnostic renamed in case, and the main tree's arm", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var moved = await RebaseAsync(kit, apply: true);

        Assert.True(moved.Succeeded, OrchestrationKit.Describe(moved));
        Assert.Equal(Text(renamed, Arm("beta"), Arm("gamma")), OrchestrationKit.Read(worktree, Registry));
        Assert.Contains("Alpha.diag", Directory.EnumerateFiles(Path.Combine(worktree, Texts)).Select(Path.GetFileName));
    }

    /// <summary>A move whose merged registry cites texts no tree holds is refused, every problem listed, nothing written.</summary>
    [Fact]
    public async Task ARebaseWhoseMergedRegistryCitesAMissingText_IsRefused_EveryProblemListed()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        await MainAddsAsync(kit, "beta", "alpha");
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("delta")));

        var refused = await RebaseAsync(kit, apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': ", StringComparison.Ordinal)
            && line.EndsWith("; merged by arm, it does not read as a registry - 3 problem(s), listed below", StringComparison.Ordinal));
        Assert.Equal(
            [
                $"The arms registry '{Registry}', as the rebase would write it, cannot be swept: 3 problem(s), each to fix:",
                $"  - line 7: text '{Texts}/delta.before' is not a file in the tree",
                $"  - line 7: text '{Texts}/delta.after' is not a file in the tree",
                $"  - line 7: text '{Texts}/delta.diag' is not a file in the tree",
            ],
            refused.Details!.SkipWhile(line => !line.StartsWith("The arms registry", StringComparison.Ordinal)).Take(4));
        Assert.Equal(Text(Arm("alpha"), Arm("delta")), OrchestrationKit.Read(worktree, Registry));
    }

    /// <summary>
    /// The bound a merge of text is offered within holds for the registry too, asked again of a version once it is read:
    /// a new base's registry above it is offered no merge, its size said.
    /// </summary>
    [Fact]
    public async Task ARebase_OffersNoMergeOfARegistryAboveTheBound_ItsSizeAskedOnceItIsRead()
    {
        using var temp = new TempDirectory();
        var (kit, worktree) = await RegistryAsync(temp, "alpha");
        OrchestrationKit.Write(kit.Main, Registry, Text(Arm("alpha"), Arm("beta")) + "#" + new string('x', (int)TwoChanges.MostBytes + (1024 * 1024)) + "\n");
        WriteTexts(kit.Main, "beta");
        await kit.Harness.CommitAllAsync(kit.Main, "a registry above the bound", Token);
        OrchestrationKit.Write(worktree, Registry, Text(Arm("alpha"), Arm("gamma")));
        WriteTexts(worktree, "gamma");

        var refused = await RebaseAsync(kit, apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith($"  '{Registry}': ", StringComparison.Ordinal)
            && line.EndsWith("; no three-way merge is offered: it is 17 MiB, and a merge is offered for a file up to 16 MiB", StringComparison.Ordinal));
    }

    /// <summary>The real file system, noting each file under <paramref name="root"/> read whole, by its path relative to it.</summary>
    private sealed class ReadsUnder(IFileSystem inner, string root) : PassThroughFileSystem(inner)
    {
        private readonly ConcurrentQueue<string> _read = new();

        public IEnumerable<string> Read => _read;

        public override Task<byte[]> ReadAllBytesAsync(string path, CancellationToken cancellationToken = default)
        {
            var relative = Path.GetRelativePath(root, path);

            if (!relative.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(relative))
            {
                _read.Enqueue(relative.Replace(Path.DirectorySeparatorChar, '/'));
            }

            return base.ReadAllBytesAsync(path, cancellationToken);
        }
    }
}
