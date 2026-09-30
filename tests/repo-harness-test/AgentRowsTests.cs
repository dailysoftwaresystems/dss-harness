using RepoHarness.Core.Anchors;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The rows an agent files: a directory for each, a file for each cell, read strictly.</summary>
public sealed class AgentRowsTests
{
    private const string Id = "D-TEST-AGENT-ROW";

    private static readonly IFileSystem FileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

    private static readonly Dictionary<string, string> Row = new()
    {
        ["status"] = "open",
        ["priority"] = "P2",
        ["trigger"] = "something the agent found",
        ["closing"] = "the fix",
        ["cross-refs"] = "b.txt",
    };

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>Each row directory becomes a row, its cells read from their files and its priority optional.</summary>
    [Fact]
    public void Rows_AreReadFromADirectoryForEach_ACellToAFile()
    {
        using var temp = new TempDirectory();
        OrchestrationKit.WriteRow(temp.Path, "D-AREA-TOPIC-ONE", ("status", "open\n"), ("trigger", "what is wrong\n"), ("closing", "the fix\n"), ("cross-refs", "src/a.c\n"), ("priority", "P1\n"));
        OrchestrationKit.WriteRow(temp.Path, "D-AREA-TOPIC-TWO", ("status", "closed"), ("trigger", "t"), ("closing", ""), ("cross-refs", ""));

        var (rows, problems) = AgentRows.Read(FileSystem, temp.Path);

        Assert.Empty(problems);
        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("D-AREA-TOPIC-ONE", row.Id);
                Assert.Equal("open", row.Status);
                Assert.Equal("P1", row.Priority);
                Assert.Equal("what is wrong\n", row.Trigger);
            },
            row =>
            {
                Assert.Equal("D-AREA-TOPIC-TWO", row.Id);
                Assert.Null(row.Priority);
            });
    }

    /// <summary>No rows directory is no rows.</summary>
    [Fact]
    public void NoRowsDirectory_IsNoRows()
    {
        using var temp = new TempDirectory();

        var (rows, problems) = AgentRows.Read(FileSystem, temp.Combine("rows"));

        Assert.Empty(rows);
        Assert.Empty(problems);
    }

    /// <summary>
    /// A draft beside the cells, a file loose in the rows directory, and a row missing a cell are each refused by name,
    /// and the row they are in is not read: a row skipped would die with the worktree unapplied.
    /// </summary>
    [Fact]
    public void Drafts_LooseFiles_AndMissingCells_AreRefusedByName()
    {
        using var temp = new TempDirectory();
        OrchestrationKit.WriteRow(temp.Path, "D-AREA-TOPIC-ONE", ("status", "open"), ("trigger", "t"), ("closing", "c"), ("cross-refs", "x"), ("notes", "a draft"));
        OrchestrationKit.WriteRow(temp.Path, "D-AREA-TOPIC-TWO", ("status", "open"), ("trigger", "t"));
        temp.WriteFile("loose.txt", "stray");

        var (rows, problems) = AgentRows.Read(FileSystem, temp.Path);

        Assert.Empty(rows);
        Assert.Collection(
            problems,
            problem => Assert.StartsWith($"'{temp.Combine("loose.txt")}' is a file directly in the rows directory", problem),
            problem => Assert.StartsWith("'D-AREA-TOPIC-ONE' holds 'notes.txt', which is not one of its cells", problem),
            problem => Assert.Equal("'D-AREA-TOPIC-TWO' is missing closing.txt, cross-refs.txt: a row declares every one of its cells but its priority", problem));
    }

    /// <summary>A cell's file is read as the anchor commands read a cell file: one opening with a byte order mark is refused.</summary>
    [Fact]
    public void ACellWithAByteOrderMark_IsRefused_AsTheAnchorCommandsRefuseOne()
    {
        using var temp = new TempDirectory();
        OrchestrationKit.WriteRow(temp.Path, "D-AREA-TOPIC-ONE", ("status", "open"), ("trigger", "t"), ("closing", "c"), ("cross-refs", "x"));
        File.WriteAllBytes(temp.Combine("D-AREA-TOPIC-ONE", "trigger.txt"), [0xEF, 0xBB, 0xBF, (byte)'t']);

        var (rows, problems) = AgentRows.Read(FileSystem, temp.Path);

        Assert.Empty(rows);
        Assert.Contains("opens with a byte-order mark", Assert.Single(problems));
    }

    /// <summary>
    /// A row an earlier fold applied, which the agent still declares as it did then, is never applied again: a change the
    /// registries took since - the row set to another status by hand - stands through every later fold and the deletion.
    /// </summary>
    [Fact]
    public async Task ARowAnEarlierFoldApplied_IsNeverAppliedAgain_OverAChangeTheRegistriesTookSince()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        kit.FileRow("ag", Id, Row);
        Assert.True((await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id))).Succeeded);
        await kit.Harness.AnchorRegistryService.SetAsync(kit.Main, new AnchorSetRequest(Id) { Status = "gated" }, dryRun: false, Token);

        var again = await kit.FoldAsync("ag", apply: true);

        // Named new again - the command line of the first fold, reused - it is a row the agent filed, made by that fold.
        var deleted = await kit.DeleteAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Contains($"  {Id}: applied by an earlier fold and not declared anew since, so the registries keep what they hold now", again.Details!);
        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Contains("gated", await StatusAsync(kit), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A row the agent declares anew over one an earlier fold applied is refused where the registries changed that row
    /// since, as a file the main tree changed is, and nothing is written.
    /// </summary>
    [Fact]
    public async Task ARowDeclaredAnew_OverOneTheRegistriesChangedSince_IsRefused()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        kit.FileRow("ag", Id, Row);
        Assert.True((await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id))).Succeeded);
        await kit.Harness.AnchorRegistryService.SetAsync(kit.Main, new AnchorSetRequest(Id) { Status = "gated" }, dryRun: false, Token);
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "the fix, made better" });
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.Contains($"{Id}: the registries changed it after an earlier fold of this agent applied it", StringComparison.Ordinal));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Contains("gated", await StatusAsync(kit), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A row that does not read refuses the fold whole: nothing of the agent's work is written while its rows cannot go in.</summary>
    [Fact]
    public async Task AMalformedRow_RefusesTheFold_WithNothingWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        OrchestrationKit.WriteRow(kit.Layout.RowsDirectory("ag"), Id, ("status", "open"), ("trigger", "t"));

        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("problem(s) with its rows", refused.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>A new row the anchor service refuses - here, one with no priority - refuses the fold whole, with nothing written.</summary>
    [Fact]
    public async Task ANewRowTheRegistriesRefuse_RefusesTheFold_WithNothingWritten()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", Id, Row.Where(cell => cell.Key != "priority").ToDictionary(cell => cell.Key, cell => cell.Value));

        var refused = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.Contains("a new anchor needs a priority", StringComparison.Ordinal));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// Rows that fail to write once the fold is in leave the command incomplete, saying so; run again, the fold finds its
    /// own writes shared and the rows go in.
    /// </summary>
    [Fact]
    public async Task RowsThatFailToWrite_AfterTheFold_AreIncomplete_AndARunAgainAppliesThem()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", Id, Row);
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, new FailingWrites(kit.Harness.AnchorRegistryService));

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", OrchestrationKit.Making(Id), apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is folded into the main tree, and applying its rows failed", stopped.Message);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var again = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Contains(Id, OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md")));
    }

    /// <summary>
    /// A fold that brings a configuration this build refuses into the main tree leaves the command incomplete, naming
    /// what is in, rather than failing as though nothing had been written: the rows that follow cannot load it.
    /// </summary>
    [Fact]
    public async Task AConfigurationTheFoldBrings_ThatNoLongerLoads_LeavesTheFoldIncomplete()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Path.Combine(".harness-config", "config.json"), "{");
        kit.FileRow("ag", Id, Row);

        var stopped = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is folded into the main tree, and applying its rows failed", stopped.Message);
        Assert.Equal("{", OrchestrationKit.Read(kit.Main, Path.Combine(".harness-config", "config.json")));
    }

    /// <summary>
    /// A consumer's run: rows holding an id broken at a line after a hyphen, a path broken after its '/', a citation of no
    /// row, and a typo of an existing row's id that declares a priority and closes it refuse the fold whole, each named, with
    /// nothing written - not the agent's file, and no row.
    /// </summary>
    [Fact]
    public async Task RowsTheDoorWouldStoreBroken_OrThatNobodyNamedNew_RefuseTheFoldWhole()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(kit.Main, new AnchorWriteRequest("D-PROBE-EXISTING-ROW-ONE", "P2", "an open row"), dryRun: false, Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", "D-PROBE-CUT-ID", new Dictionary<string, string>(Row) { ["trigger"] = "cites D-PROBE-EXISTING-\nROW-ONE across a line break after a hyphen" });
        kit.FileRow("ag", "D-PROBE-CUT-PATH", new Dictionary<string, string>(Row) { ["closing"] = "see tests/hir/\ntest_x.cpp for the case" });
        kit.FileRow("ag", "D-PROBE-UNRESOLVED-CITE", new Dictionary<string, string>(Row) { ["cross-refs"] = "[[D-PROBE-NO-SUCH-ROW-ANYWHERE]]" });
        kit.FileRow("ag", "D-PROBE-EXISTING-ROW-ONR", new Dictionary<string, string>(Row) { ["status"] = "closed" });
        var pending = OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md"));
        var done = OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry-done.md"));

        var refused = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making("D-PROBE-CUT-ID", "D-PROBE-CUT-PATH", "D-PROBE-UNRESOLVED-CITE"));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains("4 problem(s) with its rows", refused.Message);
        Assert.Collection(
            refused.Details!.Where(line => line.StartsWith("  refused: ", StringComparison.Ordinal)),
            line => Assert.StartsWith("  refused: D-PROBE-CUT-ID: The Trigger holds an anchor id cut where a line ends ('D-PROBE-EXISTING-')", line),
            line => Assert.StartsWith(@"  refused: D-PROBE-CUT-PATH: The Closing work holds a path broken across a line after a '/' ('tests/hir/\ntest_x.cpp')", line),
            line => Assert.EndsWith("pass --new D-PROBE-EXISTING-ROW-ONR; rows that begin the same way: D-PROBE-EXISTING-ROW-ONE.", line),
            line => Assert.StartsWith("  refused: D-PROBE-UNRESOLVED-CITE: 'D-PROBE-UNRESOLVED-CITE' cites D-PROBE-NO-SUCH-ROW-ANYWHERE in its Cross-refs", line));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal(pending, OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry.md")));
        Assert.Equal(done, OrchestrationKit.Read(kit.Main, Path.Combine(".plans", "_deferred-anchor-registry-done.md")));
    }

    /// <summary>
    /// A row whose stored text the agent's does not keep is shown in the dry run with its word diff and the command that
    /// writes it; --apply refuses it with nothing written, and writes it once --accept-lost names the cell.
    /// </summary>
    [Fact]
    public async Task ARowLosingStoredText_IsShownInTheDryRun_AndWrittenOnlyOnceAccepted()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main, new AnchorWriteRequest(Id, "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" }, dryRun: false, Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "another plan" });

        var dry = await kit.FoldAsync("ag", apply: false);
        var refused = await kit.FoldAsync("ag", apply: true);

        Assert.True(dry.Succeeded, OrchestrationKit.Describe(dry));
        Assert.Contains("--accept-lost", dry.Message);
        Assert.Contains($"  {Id}: changes closing (its stored text not kept)", dry.Details!);
        Assert.Contains($"    its closing does not keep its stored text word for word, written only with --accept-lost {Id}:closing: [-the-] {{+another+}} plan [-we agreed-]", dry.Details!);
        Assert.Equal($"to write them: '{ToolPackage.Command} fold-agent o1 ag --apply --accept-lost {Id}:closing'", dry.Details![^1]);
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
        Assert.Equal("the plan we agreed", (await RowAsync(kit)).ClosingWork);

        var accepted = await kit.FoldAsync("ag", apply: true, new FoldAllowances { AcceptLost = [$"{Id}:closing"] });

        Assert.True(accepted.Succeeded, OrchestrationKit.Describe(accepted));
        Assert.Equal("another plan", (await RowAsync(kit)).ClosingWork);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// --new naming no row the agent filed, and --accept-lost naming a cell that loses nothing, refuse the fold; delete-agent
    /// holds its rows to the same checks, and refuses either beside --discard-uncommitted, which folds nothing.
    /// </summary>
    [Fact]
    public async Task NamesThatLetNothingThrough_AreRefused_AndDeleteAgentHoldsItsRowsToTheSameChecks()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        kit.FileRow("ag", Id, Row);

        var unnamed = await kit.DeleteAsync("ag", apply: false);
        var stale = await kit.FoldAsync("ag", apply: false, new FoldAllowances { New = [Id, "D-TEST-AGENT-NINE"], AcceptLost = [$"{Id}:closing", "D-TEST-AGENT-NINE:closing"] });
        var misspelt = await kit.FoldAsync("ag", apply: false, new FoldAllowances { AcceptLost = [$"{Id}:status"] });
        var discarding = await kit.DeleteAsync("ag", apply: true, OrchestrationKit.Making(Id), discard: true);

        Assert.Equal(HarnessExit.Refused, unnamed.ExitCode);
        Assert.Contains(unnamed.Details!, line => line.Contains($"'{Id}' has no row in either registry, and it was not named new", StringComparison.Ordinal));
        Assert.Equal(HarnessExit.Refused, stale.ExitCode);
        Assert.Contains($"  refused: --new D-TEST-AGENT-NINE names no row filed in '{kit.Layout.RowsDirectory("ag")}': drop it, or correct the id.", stale.Details!);
        Assert.Contains($"  refused: --accept-lost {Id}:closing names a cell that keeps its stored text here: drop it, or correct it.", stale.Details!);
        Assert.Contains($"  refused: --accept-lost D-TEST-AGENT-NINE:closing names no row filed in '{kit.Layout.RowsDirectory("ag")}': drop it, or correct the id.", stale.Details!);
        Assert.Equal(HarnessExit.UsageError, misspelt.ExitCode);
        Assert.Equal($"--accept-lost '{Id}:status' is not <ID>:<cell>, where the id is an anchor's and the cell is trigger, closing, cross-refs.", misspelt.Message);
        Assert.Equal(HarnessExit.UsageError, discarding.ExitCode);
        Assert.Equal("--new lets a fold through what it otherwise refuses, and --discard-uncommitted folds nothing: give one or the other.", discarding.Message);

        var deleted = await kit.DeleteAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.True(deleted.Succeeded, OrchestrationKit.Describe(deleted));
        Assert.Equal("the fix", (await RowAsync(kit)).ClosingWork);
    }

    /// <summary>
    /// A row an earlier fold applied is compared with the registries as it was applied, and held to no rule a write is held
    /// to now: the agent correcting a path its earlier row stored cut folds like any change - its loss of the stored text
    /// accepted, as any is - rather than being refused for the cut the earlier row held.
    /// </summary>
    [Fact]
    public async Task ARowAppliedBeforeARuleItBreaks_IsComparedAsApplied_SoItsCorrectionFolds()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main, new AnchorWriteRequest(Id, "P2", "something the agent found") { ClosingWork = "placeholder", CrossRefs = "b.txt" }, dryRun: false, Token);
        var registry = Path.Combine(kit.Main, ".plans", "_deferred-anchor-registry.md");
        File.WriteAllText(registry, File.ReadAllText(registry).Replace("| placeholder |", "| see src/ main_x.c |", StringComparison.Ordinal));
        await kit.CreateAgentAsync("ag");
        kit.Harness.OrchestrationStore.WriteAppliedRows(
            kit.Layout, "ag", new AppliedRowsRecord { Rows = [new(Id, "open", "something the agent found", "see src/\nmain_x.c", "b.txt") { Priority = "P2" }] });
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "see src/main_x.c", ["cross-refs"] = "b.txt" });

        var folded = await kit.FoldAsync("ag", apply: true, new FoldAllowances { AcceptLost = [$"{Id}:closing"] });

        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Equal("see src/main_x.c", (await RowAsync(kit)).ClosingWork);
    }

    /// <summary>
    /// A row an earlier fold applied that the registries changed since is refused as changed, saying how it differs - never
    /// as a row whose state cannot be told because what the earlier fold applied breaks a rule a write is held to now.
    /// </summary>
    [Fact]
    public async Task ARowAppliedBeforeARuleItBreaks_ChangedSince_IsRefusedAsChanged()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main, new AnchorWriteRequest(Id, "P2", "something the agent found") { ClosingWork = "placeholder", CrossRefs = "b.txt" }, dryRun: false, Token);
        var registry = Path.Combine(kit.Main, ".plans", "_deferred-anchor-registry.md");
        File.WriteAllText(registry, File.ReadAllText(registry).Replace("| placeholder |", "| see src/main_x.c, mended by hand |", StringComparison.Ordinal));
        await kit.CreateAgentAsync("ag");
        kit.Harness.OrchestrationStore.WriteAppliedRows(
            kit.Layout, "ag", new AppliedRowsRecord { Rows = [new(Id, "open", "something the agent found", "see src/\nmain_x.c", "b.txt") { Priority = "P2" }] });
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["trigger"] = "something more the agent found", ["cross-refs"] = "b.txt" });

        var refused = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(
            refused.Details!,
            line => line.StartsWith($"  refused: {Id}: the registries changed it after an earlier fold of this agent applied it - its Closing work differs - so", StringComparison.Ordinal));
    }

    /// <summary>
    /// A row an earlier fold made that the registries have since lost is not made again by a --new the fold's command line
    /// still holds: it is refused as changed since, saying it has no row, with nothing written.
    /// </summary>
    [Fact]
    public async Task ARowAnEarlierFoldMade_ThatTheRegistriesLost_IsNotMadeAgain_EvenNamedNew()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");
        kit.FileRow("ag", Id, Row);
        Assert.True((await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id))).Succeeded);
        var registry = Path.Combine(kit.Main, ".plans", "_deferred-anchor-registry.md");
        File.WriteAllLines(registry, File.ReadAllLines(registry).Where(line => !line.Contains($"`{Id}`", StringComparison.Ordinal)));
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "the fix and a test" });

        var refused = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains($"  refused: {Id}: the registries changed it after an earlier fold of this agent applied it - it has no row in either registry - so "
            + "applying what it declares now would lose that change. Set the row by hand as it should be; a row already as declared is recorded, not written again", refused.Details!);
        Assert.DoesNotContain(Id, File.ReadAllText(registry));
    }

    /// <summary>
    /// A registry the agent changed as a file refuses the fold, naming it, with nothing written: its rows go in through its
    /// rows directory only, which were weighed against the registry the fold would have written over.
    /// </summary>
    [Fact]
    public async Task ARegistryTheAgentChangedAsAFile_RefusesTheFold()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        File.AppendAllText(Path.Combine(worktree, ".plans", "_deferred-anchor-registry.md"), "an edit of the agent's own\n");

        var refused = await kit.FoldAsync("ag", apply: false);

        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Contains(refused.Details!, line => line.StartsWith("  '.plans/_deferred-anchor-registry.md' is an anchor registry, which a fold changes only through the rows", StringComparison.Ordinal));
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    /// <summary>
    /// A path a row cites into a directory the fold itself makes at the top of the tree is judged the same before the fold's
    /// files are written as after: refused by the dry run and by --apply alike, with nothing written, never after the files.
    /// </summary>
    [Fact]
    public async Task APathARowCitesIntoADirectoryTheFoldMakes_IsJudgedTheSameBeforeAndAfter()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Path.Combine("tools", "gen_x.py"), "print('x')\n");
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["trigger"] = "see tools/ gen_x.py for the case" });

        var dry = await kit.FoldAsync("ag", apply: false, OrchestrationKit.Making(Id));
        var applied = await kit.FoldAsync("ag", apply: true, OrchestrationKit.Making(Id));

        Assert.Equal(HarnessExit.Refused, dry.ExitCode);
        Assert.Contains(dry.Details!, line => line.Contains("holds a path with a space after a '/' ('tools/ gen_x.py'", StringComparison.Ordinal));
        Assert.Equal(HarnessExit.Refused, applied.ExitCode);
        Assert.False(File.Exists(Path.Combine(kit.Main, "tools", "gen_x.py")));
    }

    /// <summary>
    /// The command a dry run ends with holds what the dry run was given - a settled path holding a space, quoted, so it is
    /// still one argument - and an --accept-lost for each cell that needs one; the fold it applies lists the loss accepted.
    /// </summary>
    [Fact]
    public async Task TheCommandADryRunEndsWith_HoldsWhatItWasGiven_QuotingAPathWithASpace()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main, new AnchorWriteRequest(Id, "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" }, dryRun: false, Token);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, Path.Combine("notes", "a b.txt"), "reconciled by hand\n");
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "another plan" });
        var given = new FoldAllowances { Settled = ["notes/a b.txt"] };

        var dry = await kit.FoldAsync("ag", apply: false, given);
        var folded = await kit.FoldAsync("ag", apply: true, given with { AcceptLost = [$"{Id}:closing"] });

        Assert.Equal($"to write them: '{ToolPackage.Command} fold-agent o1 ag --apply --settled \"notes/a b.txt\" --accept-lost {Id}:closing'", dry.Details![^1]);
        Assert.True(folded.Succeeded, OrchestrationKit.Describe(folded));
        Assert.Contains(folded.Details!, line => line.StartsWith($"    its closing does not keep its stored text word for word, accepted with --accept-lost {Id}:closing: ", StringComparison.Ordinal));
    }

    /// <summary>
    /// A fold after a review runs with the command line of the fold before it: a --new naming a row that fold made is let
    /// stand as the agent's change of that row goes in, and an --accept-lost naming a row the agent still declares as that
    /// fold applied it is let stand too.
    /// </summary>
    [Fact]
    public async Task AFoldAfterAReview_RunsWithTheCommandLineOfTheFoldBefore()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        const string Other = "D-TEST-AGENT-OTHER";
        await kit.Harness.AnchorRegistryService.WriteAsync(
            kit.Main, new AnchorWriteRequest(Other, "P2", "something the agent found") { ClosingWork = "the plan we agreed", CrossRefs = "b.txt" }, dryRun: false, Token);
        await kit.CreateAgentAsync("ag");
        kit.FileRow("ag", Id, Row);
        kit.FileRow("ag", Other, new Dictionary<string, string>(Row) { ["closing"] = "another plan" });
        var line = new FoldAllowances { New = [Id], AcceptLost = [$"{Other}:closing"] };
        Assert.True((await kit.FoldAsync("ag", apply: true, line)).Succeeded);
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "the fix and a test that proves it" });

        var again = await kit.FoldAsync("ag", apply: true, line);

        Assert.True(again.Succeeded, OrchestrationKit.Describe(again));
        Assert.Equal("the fix and a test that proves it", (await RowAsync(kit)).ClosingWork);
    }

    /// <summary>
    /// Rows that cannot be read, or registries they cannot be weighed against, fail the fold naming what cannot be read,
    /// with nothing written - never as a defect of the tool.
    /// </summary>
    [Fact]
    public async Task RowsThatCannotBeWeighed_FailTheFold_NamingWhatCannotBeRead()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");
        OrchestrationKit.Write(worktree, "b.txt", "two\nagent edit\n");
        kit.FileRow("ag", Id, Row);
        var agents = kit.Harness.Agents(kit.Harness.FileSystem, new UnreadableRegistries(kit.Harness.AnchorRegistryService));

        var failed = await agents.FoldAsync(kit.Main, "o1", "ag", OrchestrationKit.Making(Id), apply: true, Token);

        Assert.Equal(HarnessExit.CommandFailed, failed.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' was not folded: its rows, or the registries they go in, cannot be read - ", failed.Message);
        Assert.Equal("two\n", OrchestrationKit.Read(kit.Main, "b.txt"));
    }

    private static async Task<AnchorRow> RowAsync(OrchestrationKit kit)
        => Assert.Single(Assert.Single((await kit.Harness.AnchorRegistryService.ReadAsync(kit.Main, [Id], AnchorScope.All, Token)).Results).Matches).Row;

    private static async Task<string> StatusAsync(OrchestrationKit kit)
        => Assert.Single(Assert.Single((await kit.Harness.AnchorRegistryService.ReadAsync(kit.Main, [Id], AnchorScope.All, Token)).Results).Matches).Row.Status;

    /// <summary>The anchor service, except that every batch fails to read the registries, as one held by another program would.</summary>
    private sealed class UnreadableRegistries(IAnchorRegistryService inner) : IAnchorRegistryService
    {
        public Task<AnchorChange> WriteAsync(string startDirectory, AnchorWriteRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.WriteAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorChange> SetAsync(string startDirectory, AnchorSetRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.SetAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorBatch> ApplyAsync(string startDirectory, AnchorBatchRequest request, AnchorBatchMode mode, CancellationToken cancellationToken = default)
            => throw new IOException("The registry is being used by another process.");

        public Task<IReadOnlyList<AnchorDifference>> DifferencesAsync(string startDirectory, IReadOnlyList<AnchorRowDeclaration> rows, CancellationToken cancellationToken = default)
            => inner.DifferencesAsync(startDirectory, rows, cancellationToken);

        public Task<AnchorLookup> ReadAsync(string startDirectory, IReadOnlyList<string> ids, AnchorScope scope, CancellationToken cancellationToken = default)
            => inner.ReadAsync(startDirectory, ids, scope, cancellationToken);

        public Task<IReadOnlyList<AnchorEntry>> ListAsync(string startDirectory, AnchorListFilter filter, CancellationToken cancellationToken = default)
            => inner.ListAsync(startDirectory, filter, cancellationToken);

        public Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default)
            => inner.LintAsync(startDirectory, cancellationToken);
    }

    /// <summary>The anchor service, except that a batch that would write fails, as a registry held by another program would.</summary>
    private sealed class FailingWrites(IAnchorRegistryService inner) : IAnchorRegistryService
    {
        public Task<AnchorChange> WriteAsync(string startDirectory, AnchorWriteRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.WriteAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorChange> SetAsync(string startDirectory, AnchorSetRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.SetAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorBatch> ApplyAsync(string startDirectory, AnchorBatchRequest request, AnchorBatchMode mode, CancellationToken cancellationToken = default)
            => mode != AnchorBatchMode.Apply ? inner.ApplyAsync(startDirectory, request, mode, cancellationToken) : throw new IOException("The registry is being used by another process.");

        public Task<IReadOnlyList<AnchorDifference>> DifferencesAsync(string startDirectory, IReadOnlyList<AnchorRowDeclaration> rows, CancellationToken cancellationToken = default)
            => inner.DifferencesAsync(startDirectory, rows, cancellationToken);

        public Task<AnchorLookup> ReadAsync(string startDirectory, IReadOnlyList<string> ids, AnchorScope scope, CancellationToken cancellationToken = default)
            => inner.ReadAsync(startDirectory, ids, scope, cancellationToken);

        public Task<IReadOnlyList<AnchorEntry>> ListAsync(string startDirectory, AnchorListFilter filter, CancellationToken cancellationToken = default)
            => inner.ListAsync(startDirectory, filter, cancellationToken);

        public Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default)
            => inner.LintAsync(startDirectory, cancellationToken);
    }
}
