using DssHarness.Core.FileSystem;
using DssHarness.Core.Sync;

namespace DssHarness.Tests;

/// <summary>
/// What a reading of a tree says of itself - the line a run says, and the part each leg on a copy says - and how a file
/// that stands as it was read is told from one written since.
/// </summary>
public sealed class TreeReadingTests
{
    /// <summary>
    /// The line a run says once it has read a tree names the commit by its first twelve characters, and the uncommitted
    /// changes only where there are any; a tree with no commit yet says so, and no count.
    /// </summary>
    [Theory]
    [InlineData("1a2b3c4d5e6f7a8b9c0d1a2b3c4d5e6f7a8b9c0d", 0, "'/src/repo' was read at commit 1a2b3c4d5e6f")]
    [InlineData("1a2b3c4d5e6f7a8b9c0d1a2b3c4d5e6f7a8b9c0d", 1, "'/src/repo' was read at commit 1a2b3c4d5e6f with 1 uncommitted change(s)")]
    [InlineData(null, 3, "'/src/repo' was read with no commit yet")]
    public void TheReadingLine_NamesTheCommit_AndTheUncommittedChanges_OrThatThereIsNoCommitYet(string? commit, int changes, string said)
        => Assert.Equal(said, new TreeReading("/src/repo", commit, changes).Said);

    /// <summary>
    /// A leg on a copy says the tree it tested: read at the commit by its own run, or staged there by the run the copy
    /// records - and by no run where the sync that made it kept none.
    /// </summary>
    [Theory]
    [InlineData("20261012-150233-1a2b3c4d", false, "tree read at 1a2b3c4d5e6f with 2 uncommitted change(s)")]
    [InlineData("20261012-150233-1a2b3c4d", true, "tree staged at 1a2b3c4d5e6f with 2 uncommitted change(s) by run 20261012-150233-1a2b3c4d")]
    [InlineData(null, true, "tree staged at 1a2b3c4d5e6f with 2 uncommitted change(s)")]
    public void ALegsLine_SaysTheTreeItTested_ReadByItsRun_OrStagedByTheRunTheCopyRecords(string? run, bool staged, string said)
        => Assert.Equal(said, new CopyReading(run, "1a2b3c4d5e6f7a8b9c0d1a2b3c4d5e6f7a8b9c0d", 2).Said(staged));

    /// <summary>
    /// A file stands as it was read only where it is there, the same length, and written and created when it was; a
    /// standing whose times could not be read confirms nothing, as a file that is gone stands as nothing it was.
    /// </summary>
    [Fact]
    public void AFileStandsAsItWasRead_OnlyWhereItsLengthAndBothTimesAreTheSame()
    {
        var written = new DateTime(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);
        var read = new FileStanding(10, written, written);

        Assert.True(read.StandsAs(new FileStanding(10, written, written)));
        Assert.False(read.StandsAs(new FileStanding(11, written, written)));
        Assert.False(read.StandsAs(new FileStanding(10, written.AddTicks(1), written)));
        Assert.False(read.StandsAs(new FileStanding(10, written, written.AddTicks(1))));
        Assert.False(read.StandsAs(null));
        Assert.False(new FileStanding(10, null, written).StandsAs(new FileStanding(10, null, written)));
    }
}
