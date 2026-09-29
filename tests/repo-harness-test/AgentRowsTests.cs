using RepoHarness.Core.Anchors;
using RepoHarness.Core.FileSystem;
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
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        await kit.Harness.AnchorRegistryService.SetAsync(kit.Main, new AnchorSetRequest(Id) { Status = "gated" }, dryRun: false, Token);

        var again = await kit.FoldAsync("ag", apply: true);
        var deleted = await kit.DeleteAsync("ag", apply: true, discard: false);

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
        Assert.True((await kit.FoldAsync("ag", apply: true)).Succeeded);
        await kit.Harness.AnchorRegistryService.SetAsync(kit.Main, new AnchorSetRequest(Id) { Status = "gated" }, dryRun: false, Token);
        kit.FileRow("ag", Id, new Dictionary<string, string>(Row) { ["closing"] = "a better fix" });
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

        var refused = await kit.FoldAsync("ag", apply: true);

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

        var stopped = await agents.FoldAsync(kit.Main, "o1", "ag", [], apply: true, Token);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is folded into the main tree, and applying its rows failed", stopped.Message);
        Assert.Equal("two\nagent edit\n", OrchestrationKit.Read(kit.Main, "b.txt"));

        var again = await kit.FoldAsync("ag", apply: true);

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

        var stopped = await kit.FoldAsync("ag", apply: true);

        Assert.Equal(HarnessExit.Incomplete, stopped.ExitCode);
        Assert.StartsWith("Agent 'ag' of 'o1' is folded into the main tree, and applying its rows failed", stopped.Message);
        Assert.Equal("{", OrchestrationKit.Read(kit.Main, Path.Combine(".harness-config", "config.json")));
    }

    private static async Task<string> StatusAsync(OrchestrationKit kit)
        => Assert.Single(Assert.Single((await kit.Harness.AnchorRegistryService.ReadAsync(kit.Main, [Id], AnchorScope.All, Token)).Results).Matches).Row.Status;

    /// <summary>The anchor service, except that a batch that would write fails, as a registry held by another program would.</summary>
    private sealed class FailingWrites(IAnchorRegistryService inner) : IAnchorRegistryService
    {
        public Task<AnchorChange> WriteAsync(string startDirectory, AnchorWriteRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.WriteAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorChange> SetAsync(string startDirectory, AnchorSetRequest request, bool dryRun, CancellationToken cancellationToken = default)
            => inner.SetAsync(startDirectory, request, dryRun, cancellationToken);

        public Task<AnchorBatch> ApplyAsync(string startDirectory, IReadOnlyList<AnchorRowDeclaration> rows, bool dryRun, CancellationToken cancellationToken = default)
            => dryRun ? inner.ApplyAsync(startDirectory, rows, dryRun, cancellationToken) : throw new IOException("The registry is being used by another process.");

        public Task<AnchorLookup> ReadAsync(string startDirectory, IReadOnlyList<string> ids, AnchorScope scope, CancellationToken cancellationToken = default)
            => inner.ReadAsync(startDirectory, ids, scope, cancellationToken);

        public Task<IReadOnlyList<AnchorEntry>> ListAsync(string startDirectory, AnchorListFilter filter, CancellationToken cancellationToken = default)
            => inner.ListAsync(startDirectory, filter, cancellationToken);

        public Task<IReadOnlyList<AnchorFinding>> LintAsync(string startDirectory, CancellationToken cancellationToken = default)
            => inner.LintAsync(startDirectory, cancellationToken);
    }
}
