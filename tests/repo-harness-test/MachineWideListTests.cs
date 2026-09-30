using RepoHarness.Core.Execution;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A list every process on the machine decides by - the run lock, the heavy legs admitted onto a machine - read, decided
/// on and written back as one step, what is said of it said once it is let go, and an entry this process gave back never
/// counted by it again, whether or not the file could be changed just then.
/// </summary>
public sealed class MachineWideListTests
{
    /// <summary>
    /// What a change has to say is said once the file is let go: said while it was held, a console with text selected in
    /// its window would hold every other process's change of the file until it was refused.
    /// </summary>
    [Fact]
    public void WhatAChangeSays_IsSaidOnceTheFileIsLetGo()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("list.json");
        bool? freeWhenSaid = null;

        MachineWideFile.Update(path, TimeSpan.FromSeconds(5), afterwards =>
        {
            afterwards(() => freeWhenSaid = Task.Run(() => MachineWideFile.Update(path, TimeSpan.FromMilliseconds(100), () => true)).GetAwaiter().GetResult());
            Assert.Null(freeWhenSaid);
            return 0;
        });

        Assert.True(freeWhenSaid);
    }

    /// <summary>A change that failed says nothing of itself: what it would have said was never done.</summary>
    [Fact]
    public void AChangeThatFailed_SaysNothing()
    {
        using var temp = new TempDirectory();
        var said = false;

        Assert.Throws<InvalidOperationException>(() => MachineWideFile.Update<int>(temp.Combine("list.json"), TimeSpan.FromSeconds(5), afterwards =>
        {
            afterwards(() => said = true);
            throw new InvalidOperationException("the change failed");
        }));

        Assert.False(said);
    }

    /// <summary>
    /// An entry given back while the file could not be written - a full disk - leaves this process's reading at once,
    /// and the file with the next change this process writes: one kept instead would stand in front of this process's
    /// own later entries for as long as it runs.
    /// </summary>
    [Fact]
    public void AnEntryGivenBackUnwritten_LeavesThisProcessesReading_AndTheFileWithItsNextChange()
    {
        using var temp = new TempDirectory();
        var disk = new FullDiskFileSystem(new HarnessFactory().FileSystem);
        var list = new MachineWideList<string>(disk, temp.Combine("list.json"), "The test's list", "Until it can be, the test cannot go on.", "Remove it.");
        HarnessException? couldNot = null;

        list.Update((_, _) => ((IReadOnlyList<string>?)["first", "kept"], 0));
        disk.Full = true;
        list.GiveBack(entry => entry == "first", ex => couldNot = ex);
        disk.Full = false;

        Assert.Contains("could not be written: There is not enough space on the disk", couldNot?.Message, StringComparison.Ordinal);
        Assert.Equal(["first", "kept"], list.Read());
        Assert.Equal(["kept"], list.Update((standing, _) => ((IReadOnlyList<string>?)null, standing)));

        list.Update((standing, _) => ((IReadOnlyList<string>?)[.. standing, "later"], 0));

        Assert.Equal(["kept", "later"], list.Read());
    }

    /// <summary>
    /// A lock whose entry leaves out a member it needs - written by hand, or by something else - is refused, never read
    /// in as a holder of no machine that no run could take; one an older build wrote, before stamps, still reads.
    /// </summary>
    [Fact]
    public void AnEntryLeavingOutAMemberItNeeds_IsRefused_AndOneAnOlderBuildWroteStillReads()
    {
        using var temp = new TempDirectory();
        var factory = new HarnessFactory();
        var layout = new Core.Repository.HarnessLayout(temp.Path, temp.Path);
        var runLock = new RunLock(factory.FileSystem, factory.Output, factory.Identity);

        Directory.CreateDirectory(Path.GetDirectoryName(layout.LockFile)!);
        File.WriteAllText(
            layout.LockFile,
            """[{ "host": "local", "tree": "/repo", "scope": "TreeExclusive", "holder": { "processId": 7, "runId": "r", "takenUtc": "2026-09-30T16:29:42+00:00", "command": "test" } }]""");

        var refusal = Assert.Throws<HarnessException>(() => runLock.Read(layout));

        Assert.Contains("is not readable as JSON", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("machine", refusal.Message, StringComparison.Ordinal);

        File.WriteAllText(
            layout.LockFile,
            """[{ "host": "local", "tree": "/repo", "scope": "TreeExclusive", "holder": { "machine": "box", "processId": 7, "processStartedUtc": "2026-09-30T16:00:00+00:00", "runId": "r", "takenUtc": "2026-09-30T16:29:42+00:00", "command": "test" } }]""");

        var entry = Assert.Single(runLock.Read(layout));

        Assert.Null(entry.Holder.ProcessStamp);
        Assert.Null(entry.Variant);
    }

    /// <summary>A file holding null holds no list, and is refused as one that is not this build's JSON, never read as empty.</summary>
    [Fact]
    public void AFileHoldingNull_IsRefused_NeverReadAsEmpty()
    {
        using var temp = new TempDirectory();
        var path = temp.Combine("list.json");
        File.WriteAllText(path, "null");
        var list = new MachineWideList<string>(new HarnessFactory().FileSystem, path, "The test's list", "Until it can be, the test cannot go on.", "Remove it.");

        var refusal = Assert.Throws<HarnessException>(() => list.Read());

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal($"The test's list '{path}' is not readable as JSON: it holds null. Remove it.", refusal.Message);
    }
}
