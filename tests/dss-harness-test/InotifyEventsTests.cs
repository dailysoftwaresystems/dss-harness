using System.Text;
using RepoHarness.Core.Execution;

namespace RepoHarness.Tests;

/// <summary>
/// Reading what an inotify instance gives into events, on every system: every shape a record takes is read here, since
/// only Linux can make one.
/// </summary>
public sealed class InotifyEventsTests
{
    /// <summary>A read of several records gives each, in order, with its watch, its mask and its name.</summary>
    [Fact]
    public void AReadOfSeveralRecords_GivesEach_InOrder()
    {
        var records = InotifyEvents.Parse(InotifyRecord.Of(
            new InotifyEvent(1, InotifyBits.Create, "made.txt"),
            new InotifyEvent(2, InotifyBits.Modify, "written.cpp"),
            new InotifyEvent(1, InotifyBits.DeleteSelf, string.Empty)));

        Assert.False(records.CutShort);
        Assert.Equal(
            [
                new InotifyEvent(1, InotifyBits.Create, "made.txt"),
                new InotifyEvent(2, InotifyBits.Modify, "written.cpp"),
                new InotifyEvent(1, InotifyBits.DeleteSelf, string.Empty),
            ],
            records.Events);
    }

    /// <summary>A name padded with NULs to its record's length is the name alone, whatever the padding.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(16)]
    public void ANamePaddedWithNuls_IsTheNameAlone(int pad)
    {
        var records = InotifyEvents.Parse(InotifyRecord.One(7, InotifyBits.MovedTo, Encoding.UTF8.GetBytes("été.json"), pad));

        Assert.Equal([new InotifyEvent(7, InotifyBits.MovedTo, "été.json")], records.Events);
    }

    /// <summary>The instance's own record that its queue overflowed - no watch, no name - is read as one.</summary>
    [Fact]
    public void AnOverflowRecord_IsReadAsOne()
    {
        var records = InotifyEvents.Parse(InotifyRecord.Of(new InotifyEvent(-1, InotifyBits.QueueOverflow, string.Empty)));

        var overflow = Assert.Single(records.Events);
        Assert.Equal(-1, overflow.Descriptor);
        Assert.True(overflow.Says(InotifyBits.QueueOverflow));
        Assert.False(records.CutShort);
    }

    /// <summary>
    /// A record that runs past what was read - its numbers cut short, or its name longer than what is left - is said to
    /// have been cut short, and the whole records before it are still given.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(InotifyEvents.HeaderBytes - 1)]
    [InlineData(InotifyEvents.HeaderBytes + 3)]
    public void ARecordCutShort_IsSaidSo_AndTheRecordsBeforeItAreGiven(int kept)
    {
        var whole = InotifyRecord.Of(new InotifyEvent(1, InotifyBits.Create, "made.txt"));
        var cut = InotifyRecord.Of(new InotifyEvent(2, InotifyBits.Delete, "gone.txt"))[..kept];

        var records = InotifyEvents.Parse([.. whole, .. cut]);

        Assert.True(records.CutShort);
        Assert.Equal([new InotifyEvent(1, InotifyBits.Create, "made.txt")], records.Events);
    }

    /// <summary>A name that is no UTF-8 is said to be unreadable, and never read as another name.</summary>
    [Fact]
    public void ANameThatIsNoUtf8_IsSaidToBeUnreadable()
    {
        var records = InotifyEvents.Parse(InotifyRecord.One(3, InotifyBits.Create, [0x61, 0xff, 0x62], pad: 16));

        var heard = Assert.Single(records.Events);
        Assert.True(heard.NameUnreadable);
        Assert.Equal((3, InotifyBits.Create, string.Empty), (heard.Descriptor, heard.Mask, heard.Name));
        Assert.False(records.CutShort);
    }

    /// <summary>A read that gave nothing holds no event, and was not cut short.</summary>
    [Fact]
    public void AReadOfNothing_HoldsNoEvent()
    {
        var records = InotifyEvents.Parse([]);

        Assert.Empty(records.Events);
        Assert.False(records.CutShort);
    }

    /// <summary>
    /// What every watch asks for: what .NET's own watcher asks for the tool's filter, and the watched directory's own
    /// removal and move - the one mask, so a directory added twice changes nothing.
    /// </summary>
    [Fact]
    public void EveryWatch_AsksForEntriesMadeRemovedMovedAndWritten_AndTheDirectoryItselfGoingOrMoving()
    {
        Assert.Equal(
            0x0000_0100u | 0x0000_0200u | 0x0000_0002u | 0x0000_0004u | 0x0000_0040u | 0x0000_0080u | 0x0100_0000u | 0x0400_0000u
                | 0x0000_0400u | 0x0000_0800u,
            InotifyBits.Watched);
        Assert.Equal(0x0008_0000, InotifyBits.CloseOnExec);
    }
}
