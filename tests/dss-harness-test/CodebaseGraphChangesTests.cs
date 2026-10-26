using DssHarness.Core.Codebase;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Repository;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>Change-first's ground (value 371): the line diff, the declarations a change touched, and what a tree holds otherwise than its base.</summary>
public sealed class CodebaseGraphChangesTests
{
    private readonly CancellationToken _token = TestContext.Current.CancellationToken;

    [Fact]
    public void TheLineDiff_TellsEachRunOfNewLines_AndEachPlaceLinesWereTakenOut()
    {
        Assert.Empty(CodeLineDiff.Hunks(["a", "b"], ["a", "b"]));
        Assert.Equal([new CodeLineHunk(2, 1, 0, 2)], CodeLineDiff.Hunks(["a", "c"], ["a", "b", "c"]));
        Assert.Equal([new CodeLineHunk(2, 0, 1, 2)], CodeLineDiff.Hunks(["a", "b", "c"], ["a", "c"]));
        Assert.Equal([new CodeLineHunk(2, 1, 1, 2)], CodeLineDiff.Hunks(["a", "b", "c"], ["a", "x", "c"]));
        Assert.Equal([new CodeLineHunk(1, 2, 0, 1)], CodeLineDiff.Hunks([], ["a", "b"]));
        Assert.Equal([new CodeLineHunk(1, 0, 2, 1)], CodeLineDiff.Hunks(["a", "b"], []));
        Assert.Equal(
            [new CodeLineHunk(2, 1, 1, 2), new CodeLineHunk(4, 0, 1, 4), new CodeLineHunk(6, 2, 0, 7)],
            CodeLineDiff.Hunks(["a", "b", "c", "d", "e", "f"], ["a", "B", "c", "e", "f", "g", "h"]));
        Assert.Equal((3, 4), (new CodeLineHunk(2, 2, 3, 2).End, new CodeLineHunk(2, 2, 3, 2).TakenEnd));
    }

    [Fact]
    public void TheLineDiff_PastItsMostEdits_TakesWhatLiesBetweenTheCommonEndsAsChangedWhole()
    {
        List<string> before = ["head", .. Enumerable.Range(0, CodeLineDiff.MostEdits).Select(at => $"old{at}"), "tail"];
        List<string> after = ["head", .. Enumerable.Range(0, CodeLineDiff.MostEdits).Select(at => $"new{at}"), "tail"];

        Assert.Equal([new CodeLineHunk(2, CodeLineDiff.MostEdits, CodeLineDiff.MostEdits, 2)], CodeLineDiff.Hunks(before, after));
    }

    [Fact]
    public void ADeclaration_IsChangedByItsOwnLines_AWholeTypeByNoneOfItsMembers()
    {
        var before = Extract(("T", CodeNodeKinds.Type, 1, 9, null, "t0"), ("T.A", CodeNodeKinds.Member, 3, 5, "T", "a0"), ("T.B", CodeNodeKinds.Member, 7, 8, "T", "b0"));
        var after = Extract(("T", CodeNodeKinds.Type, 1, 9, null, "t1"), ("T.A", CodeNodeKinds.Member, 3, 5, "T", "a1"), ("T.B", CodeNodeKinds.Member, 7, 8, "T", "b0"));
        string[] old = ["class T", "{", "  A", "  {", "  }", "", "  B", "  }", "}"];
        string[] now = ["class T", "{", "  A", "  { x", "  }", "", "  B", "  }", "}"];

        var changed = Assert.Single(CodebaseGraphChanges.Compared("t.cs", before, old, after, now));

        Assert.Equal(("T.A", CodeChanges.Changed, 3, 5), (changed.Id, changed.Change, changed.Start, changed.End));
        Assert.Equal([new CodeLineHunk(4, 1, 1, 4)], changed.Lines);
    }

    [Fact]
    public void LinesTakenOutAtADeclarationsFirstLine_AreItsChange_ByItsLinesAtTheBase()
    {
        var before = Extract(("T", CodeNodeKinds.Type, 1, 7, null, "t0"), ("T.M", CodeNodeKinds.Member, 3, 6, "T", "m0"));
        var after = Extract(("T", CodeNodeKinds.Type, 1, 6, null, "t1"), ("T.M", CodeNodeKinds.Member, 3, 5, "T", "m1"));

        var changed = Assert.Single(CodebaseGraphChanges.Compared("t.cs", before, ["class T", "{", "  [Fact]", "  void M()", "  {", "  }", "}"], after, ["class T", "{", "  void M()", "  {", "  }", "}"]));

        Assert.Equal(("T.M", CodeChanges.Changed), (changed.Id, changed.Change));
        Assert.Equal([new CodeLineHunk(3, 0, 1, 3)], changed.Lines);
    }

    [Fact]
    public void AnInnerDeclarationsLines_AreNotItsContainers_WhereANewRunSpansBoth()
    {
        var before = Extract(("T", CodeNodeKinds.Type, 1, 4, null, "t0"), ("T.A", CodeNodeKinds.Member, 3, 3, "T", "a0"));
        var after = Extract(("T", CodeNodeKinds.Type, 1, 6, null, "t1"), ("T.A", CodeNodeKinds.Member, 3, 3, "T", "a0"), ("T.B", CodeNodeKinds.Member, 5, 5, "T", "b0"));

        var changed = CodebaseGraphChanges.Compared("t.cs", before, ["class T", "{", "  A", "}"], after, ["class T", "{", "  A", "  // b", "  B", "}"]);

        Assert.Equal([new CodeLineHunk(4, 1, 0, 4)], changed.Single(node => node.Id == "T").Lines);
        Assert.Equal([new CodeLineHunk(5, 1, 0, 0)], changed.Single(node => node.Id == "T.B").Lines);
    }

    [Fact]
    public void ATypesOwnLines_ChangeIt_AndAMemberTakenOutOrAdded_IsItsOwnChange_NotItsTypes()
    {
        var before = Extract(("T", CodeNodeKinds.Type, 1, 8, null, "t0"), ("T.A", CodeNodeKinds.Member, 3, 3, "T", "a0"), ("T.Gone", CodeNodeKinds.Member, 4, 5, "T", "g0"), ("T.Gone.Inner", CodeNodeKinds.Member, 5, 5, "T.Gone", "i0"), ("T.B", CodeNodeKinds.Member, 6, 7, "T", "b0"));
        var after = Extract(("T", CodeNodeKinds.Type, 1, 7, null, "t1"), ("T.A", CodeNodeKinds.Member, 3, 3, "T", "a0"), ("T.B", CodeNodeKinds.Member, 4, 5, "T", "b0"), ("T.New", CodeNodeKinds.Member, 6, 6, "T", "n0"));
        string[] old = ["class T : Base", "{", "  A", "  Gone", "  inner", "  B", "  b", "}"];
        string[] now = ["class T : Other", "{", "  A", "  B", "  b", "  New", "}"];

        var changed = CodebaseGraphChanges.Compared("t.cs", before, old, after, now);

        Assert.Equal(
            [("T", CodeChanges.Changed, null), ("T.Gone", CodeChanges.Removed, "T"), ("T.Gone.Inner", CodeChanges.Removed, "T.Gone"), ("T.New", CodeChanges.Added, "T")],
            changed.Select(node => (node.Id, node.Change, node.Parent)).OrderBy(node => node.Id, StringComparer.Ordinal));
        // The member taken out is its own change, not its type's: the type changed by its first line alone.
        Assert.Equal([new CodeLineHunk(1, 1, 1, 1)], changed.Single(node => node.Id == "T").Lines);
        Assert.Equal((4, 5), (changed.Single(node => node.Id == "T.Gone").Start, changed.Single(node => node.Id == "T.Gone").End));
        Assert.Equal([new CodeLineHunk(6, 1, 0, 0)], changed.Single(node => node.Id == "T.New").Lines);
    }

    [Fact]
    public void ADeclarationWhoseLinesMovedAlone_IsNoChange_NorIsAFileOrANamespace()
    {
        var before = Extract(("N", CodeNodeKinds.Namespace, 1, 3, null, "n0"), ("N.T", CodeNodeKinds.Type, 2, 3, "N", "t0"));
        var after = Extract(("N", CodeNodeKinds.Namespace, 1, 4, null, "n1"), ("N.T", CodeNodeKinds.Type, 3, 4, "N", "t0"));

        Assert.Empty(CodebaseGraphChanges.Compared("n.cs", before, ["namespace N", "class T", "{}"], after, ["namespace N", "using X;", "class T", "{}"]));
    }

    [Fact]
    public async Task TheService_ComparesTheTreeWithItsBase_ByGitsDiffAndItsUntrackedFiles_ReadingTheBaseInMemory()
    {
        using var tree = await CodebaseContextPackTests.TreeAsync(
            ("src/Kept.cs", "public static class Kept\n{\n    public static int One()\n    {\n        return 1;\n    }\n\n    public static int Two() => 2;\n}\n"),
            ("src/Gone.cs", "public static class Gone\n{\n    public static void Go()\n    {\n    }\n}\n"),
            ("src/Generated.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static int One() => 1;\n}\n"),
            ("notes.txt", "a note\n"));
        var based = await tree.HeadAsync();
        tree.Write("src/Kept.cs", "public static class Kept\n{\n    public static int One()\n    {\n        return 10;\n    }\n\n    public static int Two() => 2;\n}\n");
        File.Delete(Path.Combine(tree.Tree, "src", "Gone.cs"));
        tree.Write("src/New.cs", "public static class Fresh\n{\n    public static void Begin()\n    {\n    }\n}\n");
        tree.Write("notes.txt", "a longer note\n");
        tree.Write($"{HarnessLayout.SecretSlots[0]}/key", "TOKEN=1\n");
        await tree.SyncAsync();
        var read = (await tree.CheckAsync(null, null)).Read;
        var extracts = tree.Extracts();

        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        Assert.Equal(changes.Files, await tree.Service.DifferingAsync(tree.Request(), based, _token));

        // What a declaration taken out held is taken out with it.
        Assert.Equal(based, changes.Base);
        Assert.Equal(["notes.txt", "src/Gone.cs", "src/Kept.cs", "src/New.cs"], changes.Files);
        Assert.Equal(
            [("src/Gone.cs", "Gone", CodeChanges.Removed), ("src/Gone.cs", "Gone.Go", CodeChanges.Removed), ("src/Kept.cs", "Kept.One", CodeChanges.Changed), ("src/New.cs", "Fresh", CodeChanges.Added), ("src/New.cs", "Fresh.Begin", CodeChanges.Added)],
            changes.Nodes.Select(node => (node.Path, node.Qualified, node.Change)).OrderBy(node => node.Path, StringComparer.Ordinal).ThenBy(node => node.Qualified, StringComparer.Ordinal));
        Assert.Equal([new CodeLineHunk(5, 1, 1, 5)], changes.Nodes.Single(node => node.Qualified == "Kept.One").Lines);
        Assert.Empty(changes.Uncompared);
        Assert.Equal(extracts, tree.Extracts());
    }

    [Fact]
    public async Task TheService_TellsNoDeclarationOfAFileKeptOutAsGenerated_OrOfOneNoImplementationReadOnASide()
    {
        using var tree = await CodebaseGraphTree.CreateAsync(new CodebaseGraphShape([], [], null, 120, ExcludeGenerated: true, CodebaseGraphShape.DefaultMapTokens));
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);
        tree.Write("src/Made.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static int One() => 1;\n}\n");
        tree.Write("src/Grows.cs", "public static class Grows\n{\n}\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        var based = await tree.HeadAsync();
        tree.Write("src/Made.g.cs", "// <auto-generated/>\npublic static class Made\n{\n    public static int One() => 2;\n}\n");
        tree.Write("src/Grows.cs", "public static class Grows\n{\n" + string.Concat(Enumerable.Repeat("    // a comment that makes the file too large\n", 4)) + "}\n");
        var read = (await tree.CheckAsync(null, null, "src/Made.g.cs", "src/Grows.cs")).Read;

        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        Assert.Equal(["src/Grows.cs", "src/Made.g.cs"], changes.Files);
        Assert.Empty(changes.Nodes);

        // Neither is said unchanged: each is kept with why it could not be compared.
        Assert.Equal(
            [new CodeUncomparedFile("src/Grows.cs", "218 bytes, over maxFileBytes (120)"), new CodeUncomparedFile("src/Made.g.cs", "kept out as generated code")],
            changes.Uncompared);
    }

    [Fact]
    public async Task TheService_TellsNoDeclarationOfAFileGeneratedOnOneSideAlone()
    {
        // Turns became generated code, Untold stopped being: neither side's declarations are known of the other.
        using var tree = await CodebaseGraphTree.CreateAsync(new CodebaseGraphShape([], [], null, CodebaseGraphShape.DefaultMaxFileBytes, ExcludeGenerated: true, CodebaseGraphShape.DefaultMapTokens));
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);
        tree.Write("src/Turns.cs", "public static class Turns\n{\n    public static int One() => 1;\n}\n");
        tree.Write("src/Untold.cs", "// <auto-generated/>\npublic static class Untold\n{\n    public static int One() => 1;\n}\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        var based = await tree.HeadAsync();
        tree.Write("src/Turns.cs", "// <auto-generated/>\npublic static class Turns\n{\n    public static int One() => 2;\n}\n");
        tree.Write("src/Untold.cs", "public static class Untold\n{\n    public static int One() => 2;\n}\n");
        var read = (await tree.CheckAsync(null, null, "src/Turns.cs", "src/Untold.cs")).Read;
        Assert.Equal(CodeFileStates.Generated, read.Files["src/Turns.cs"].State);
        Assert.NotEqual(CodeFileStates.Generated, read.Files["src/Untold.cs"].State);

        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        Assert.Equal(["src/Turns.cs", "src/Untold.cs"], changes.Files);
        Assert.Empty(changes.Nodes);
        Assert.Equal(
            [new CodeUncomparedFile("src/Turns.cs", "kept out as generated code"), new CodeUncomparedFile("src/Untold.cs", "kept out as generated code at the base")],
            changes.Uncompared);
    }

    [Fact]
    public async Task TheService_ListsOnlyWhatTheGraphLists_AndTellsNothingOfAFileTooLargeAtTheBase()
    {
        using var tree = await CodebaseGraphTree.CreateAsync(new CodebaseGraphShape([], [GlobPattern.Parse("gen/**")], null, 200, ExcludeGenerated: false, CodebaseGraphShape.DefaultMapTokens));
        tree.Over([.. CodeGraphExtractors.OfThisBuild.All]);
        tree.Write("gen/Made.cs", "public static class Made\n{\n}\n");
        tree.Write("src/Shrinks.cs", "public static class Shrinks\n{\n" + string.Concat(Enumerable.Repeat("    // a comment that makes the file too large\n", 5)) + "    public static int One() => 1;\n}\n");
        await tree.CommitAsync();
        await tree.SyncAsync();
        var based = await tree.HeadAsync();
        tree.Write("gen/Made.cs", "public static class Made\n{\n    public static int One() => 1;\n}\n");
        tree.Write("src/Shrinks.cs", "public static class Shrinks\n{\n    public static int One() => 2;\n}\n");
        await tree.SyncAsync();
        var read = (await tree.CheckAsync(null, null)).Read;
        var tooLarge = (await tree.RunGitAsync("rev-parse", $"{based}:src/Shrinks.cs")).Trim();
        var asked = new List<string>();
        tree.WithGit(new InterceptingGitClient(tree.Harness.GitClient) { BeforeReadBlobs = objects => asked.AddRange(objects) });

        var changes = await tree.Service.ChangesAsync(tree.Request(), read, based, _token);

        Assert.Equal(["src/Shrinks.cs"], changes.Files);
        Assert.Empty(changes.Nodes);
        Assert.Equal([new CodeUncomparedFile("src/Shrinks.cs", "301 bytes, over maxFileBytes (200) at the base")], changes.Uncompared);
        Assert.DoesNotContain(tooLarge, asked);
    }

    [Fact]
    public async Task TheGitClient_TellsABlobsSize_WithoutReadingIt_AndNoneForWhatIsNoBlob()
    {
        using var tree = await CodebaseContextPackTests.TreeAsync(("a.txt", "hello\n"));
        var blob = (await tree.RunGitAsync("rev-parse", "HEAD:a.txt")).Trim();
        var commit = await tree.HeadAsync();

        var sizes = await tree.Harness.GitClient.ReadBlobSizesAsync(tree.Tree, [blob, new string('0', 40), commit], _token);

        Assert.Equal([6L, null, null], sizes);
        Assert.Empty(await tree.Harness.GitClient.ReadBlobSizesAsync(tree.Tree, [], _token));
    }

    [Fact]
    public async Task TheService_ComparesAnAgentsWorktree_WithItsBase()
    {
        using var tree = await CodebaseContextPackTests.TreeAsync(("src/A.cs", "public static class A\n{\n    public static int One() => 1;\n}\n"));
        var based = await tree.HeadAsync();
        var worktree = await tree.WorktreeAsync("impl");
        CodebaseGraphTree.WriteIn(worktree, "src/A.cs", "public static class A\n{\n    public static int One() => 2;\n}\n");
        await tree.SyncViewAsync("impl", worktree);
        var read = (await tree.CheckAsync("impl", worktree, "src/A.cs")).Read;

        var changes = await tree.Service.ChangesAsync(tree.Request("impl", worktree), read, based, _token);

        var node = Assert.Single(changes.Nodes);
        Assert.Equal(("A.One", CodeChanges.Changed), (node.Qualified, node.Change));
    }

    [Fact]
    public async Task TheService_RefusesABaseTheRepositoryDoesNotHold()
    {
        using var tree = await CodebaseContextPackTests.TreeAsync(("src/A.cs", "public static class A\n{\n}\n"));
        var read = (await tree.CheckAsync(null, null)).Read;

        var refused = await Assert.ThrowsAsync<HarnessException>(() => tree.Service.ChangesAsync(tree.Request(), read, new string('0', 40), _token));

        Assert.Equal(HarnessExit.CommandFailed, refused.ExitCode);
    }

    [Fact]
    public void TheBaseChangeFirstComparesWith_IsAnAgentsRecorded_OrTheRunsStartCommit()
    {
        var flow = WorkflowFileParser.Parse("workflows/flow/flow.yml", "name: flow\nsteps:\n  - name: orient\n    type: orient\n");
        var run = FlowRunContext.Of("run", flow, startCommit: "start");
        var agent = new AgentRecord { Kind = AgentRecord.KindName, Name = "a", Orchestrator = "run", Model = "m", CreatedAt = DateTimeOffset.UnixEpoch, State = AgentStates.Live, Base = "agentbase" };

        Assert.Equal("agentbase", FlowGraphSyncs.ChangesBase(agent, run));
        Assert.Equal("start", FlowGraphSyncs.ChangesBase(agent with { Access = AgentRecord.AccessRead }, run));
        Assert.Equal("start", FlowGraphSyncs.ChangesBase(null, run));
        Assert.Null(FlowGraphSyncs.ChangesBase(agent with { Base = null }, run));
        Assert.Null(FlowGraphSyncs.ChangesBase(null, FlowRunContext.Of("run", flow)));
    }

    private static FileExtract Extract(params (string Id, string Kind, int Start, int End, string? Parent, string Digest)[] nodes)
        => new([.. nodes.Select(node => new ExtractNode(node.Id, node.Kind, node.Id, node.Id, node.Start, node.End, node.Id) { Parent = node.Parent, Digest = node.Digest })]);
}
