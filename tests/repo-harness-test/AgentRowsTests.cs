using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Orchestration;
using RepoHarness.Core.Platform;

namespace RepoHarness.Tests;

/// <summary>The rows an agent files: a directory for each, a file for each cell, read strictly.</summary>
public sealed class AgentRowsTests
{
    private static readonly IFileSystem FileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());

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
}
