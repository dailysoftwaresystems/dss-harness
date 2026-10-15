using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Platform;

namespace DssHarness.Tests;

/// <summary>
/// Where a run keeps one leg's records is said in one place: its directory, what lies below it, each phase's log, and
/// the logs that are there to be named.
/// </summary>
public sealed class LegRecordsTests
{
    private const string Leg = "win-msvc-release";

    /// <summary>A leg's records are a directory of its name in the run's own.</summary>
    [Fact]
    public void ALegsRecords_AreADirectoryOfItsNameInTheRunsOwn()
    {
        using var temp = new TempDirectory();

        Assert.Equal(Path.Combine(temp.Path, Leg), new LegRecords(temp.Path, Leg).Directory);
    }

    /// <summary>
    /// What a leg keeps below its records - a sweep's arms, its controls - is a path part by part beneath its directory,
    /// and is called by the leg's name and each part after it, whatever this machine separates a path with.
    /// </summary>
    [Fact]
    public void WhatALegKeepsBelowItsRecords_IsAPathBeneathItsDirectory_AndANameAfterItsOwn()
    {
        using var temp = new TempDirectory();
        var records = new LegRecords(temp.Path, Leg);

        Assert.Equal(Path.Combine(temp.Path, Leg, "arms", "charge-bound"), records.Under("arms", "charge-bound"));
        Assert.Equal(records.Directory, records.Under());
        Assert.Equal($"{Leg}/arms/charge-bound/control", records.Named("arms", "charge-bound", "control"));
        Assert.Equal(Leg, records.Named());
    }

    /// <summary>A phase's log is the file of the phase's name in the leg's directory, and in any directory it is asked in.</summary>
    [Fact]
    public void APhasesLog_IsTheFileOfItsNameInTheDirectory()
    {
        using var temp = new TempDirectory();
        var records = new LegRecords(temp.Path, Leg);

        Assert.Equal(Path.Combine(temp.Path, Leg, "configure.log"), records.PhaseLog("configure"));
        Assert.Equal(Path.Combine(records.Under("arms", "charge-bound"), "test.log"), PhaseLogs.In(records.Under("arms", "charge-bound"), "test"));
    }

    /// <summary>
    /// A leg's logs are the phase logs directly in its directory, each with its phase, in the order of their names:
    /// never another file kept there, and never a log of something kept below - an arm's, a control's.
    /// </summary>
    [Fact]
    public void TheLogsOfALeg_AreThePhaseLogsDirectlyInItsDirectory_InTheOrderOfTheirNames()
    {
        using var temp = new TempDirectory();
        var records = new LegRecords(temp.Path, Leg);

        foreach (var phase in (string[])["test", "build", "configure"])
        {
            temp.WriteFile(Path.GetRelativePath(temp.Path, records.PhaseLog(phase)), "said");
        }

        temp.WriteFile(Path.Combine(Leg, "compiler-version-C.c"), "not a log");
        temp.WriteFile(Path.Combine(Leg, "log"), "not a log either");
        temp.WriteFile(Path.GetRelativePath(temp.Path, PhaseLogs.In(records.Under("arms", "charge-bound"), "test")), "an arm's");
        temp.WriteFile(Path.GetRelativePath(temp.Path, PhaseLogs.In(temp.Path, "stray")), "the run's, no leg's");

        Assert.Equal(
            [
                new PhaseLogFile("build", records.PhaseLog("build")),
                new PhaseLogFile("configure", records.PhaseLog("configure")),
                new PhaseLogFile("test", records.PhaseLog("test")),
            ],
            records.Logs(FileSystem()));
    }

    /// <summary>A leg that kept nothing - refused, not admitted, skipped before its work - has no directory, and no logs.</summary>
    [Fact]
    public void ALegThatKeptNothing_HasNoLogs()
    {
        using var temp = new TempDirectory();

        Assert.Empty(new LegRecords(temp.Path, Leg).Logs(FileSystem()));
    }

    /// <summary>A directory of records that holds no log has none, though it is there.</summary>
    [Fact]
    public void ALegWhoseDirectoryHoldsNoLog_HasNoLogs()
    {
        using var temp = new TempDirectory();

        temp.WriteFile(Path.Combine(Leg, "compiler-version-C.c"), "not a log");

        Assert.Empty(new LegRecords(temp.Path, Leg).Logs(FileSystem()));
    }

    private static PhysicalFileSystem FileSystem() => new(FilePermissionsFactory.Create());
}
