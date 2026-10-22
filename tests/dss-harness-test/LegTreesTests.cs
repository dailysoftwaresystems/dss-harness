using DssHarness.Core.Configuration;
using DssHarness.Core.Legs;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// Where this run's own legs work, as its command line's two options say it - refused where they say opposite things,
/// about the command line alone, before any configuration is read.
/// </summary>
public sealed class LegTreesTests
{
    /// <summary>
    /// One option says a copy, the other says the tree itself, and none says the configuration's own default - each
    /// where it was given alone.
    /// </summary>
    [Theory]
    [InlineData(false, false, LocalTrees.AsConfigured)]
    [InlineData(true, false, LocalTrees.Copy)]
    [InlineData(false, true, LocalTrees.InPlace)]
    public void WhereThisRunsOwnLegsWork_IsAsItsTwoOptionsSayIt(bool localCopy, bool inPlace, LocalTrees where)
        => Assert.Equal(where, LegTrees.Asked(localCopy, inPlace));

    /// <summary>
    /// A leg builds in this machine's own tree where it names no host - no WSL distribution, no ssh host - and its os is
    /// this machine's, however its case is written; one that names a host builds there, whatever its os says.
    /// </summary>
    [Theory]
    [InlineData("linux", null, null, true)]
    [InlineData("Linux", null, null, true)]
    [InlineData("windows", null, null, false)]
    [InlineData("linux", "ubuntu", null, false)]
    [InlineData("linux", null, "builder", false)]
    public void ALegBuildsHere_WhereItNamesNoHost_AndItsOsIsThisMachines(string os, string? wsl, string? ssh, bool here)
        => Assert.Equal(here, LegTrees.BuildsHere(new LegConfig { Os = os, Processor = "x64", Config = "debug", Wsl = wsl, Ssh = ssh }, "linux"));

    /// <summary>
    /// Both given is a mistake on the command line alone, refused as one before any configuration is read: naming the
    /// two options, so the reader is not sent looking through a repository for what was typed wrong.
    /// </summary>
    [Fact]
    public void BothGiven_IsRefusedAsAUsageError_NamingTheTwoOptions()
    {
        var refused = Assert.Throws<HarnessException>(() => LegTrees.Asked(localCopy: true, inPlace: true));

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);
        Assert.Equal(
            "--local-copy and --in-place say opposite things about where this run's own legs work. Give one.",
            refused.Message);
    }
}
