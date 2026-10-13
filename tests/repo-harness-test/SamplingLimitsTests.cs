using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.Output;

namespace RepoHarness.Tests;

/// <summary>
/// What sampling cannot see is said in one frame, "sampling cannot see", and four phrases written for it: each line
/// reads whole, and the architecture names the four in the same words.
/// </summary>
public sealed class SamplingLimitsTests
{
    /// <summary>Each limit, framed, is the whole line a report says it in.</summary>
    [Theory]
    [InlineData(
        SamplingLimits.RelativeTool,
        "sampling cannot see a tool started from inside the build directory by a relative path, since another process's working directory cannot be read")]
    [InlineData(
        SamplingLimits.HiddenCommandLine,
        "sampling cannot see the command line of a process that does not expose it, so such a process is counted and never matched against the build directory")]
    [InlineData(
        SamplingLimits.NoCommandLine,
        "sampling cannot see any command line on this machine, so nothing was matched against the build directory")]
    [InlineData(
        SamplingLimits.NoStartTimes,
        "sampling cannot see when some processes on this machine started, so a process id reused during the leg is reported as one process")]
    public void EachLimit_Framed_IsTheWholeLine(string limit, string line)
        => Assert.Equal(line, SamplingLimits.Said(limit));

    /// <summary>The two limits that hold on every machine are the two every report states.</summary>
    [Fact]
    public void TheTwoThatAlwaysHold_AreTheRelativeToolAndTheHiddenCommandLine()
        => Assert.Equal([SamplingLimits.RelativeTool, SamplingLimits.HiddenCommandLine], SamplingLimits.Always);

    /// <summary>
    /// Under --verbose a report's limits are said one line each, under the verb and the leg, each in its frame - the
    /// lines a person reads.
    /// </summary>
    [Fact]
    public void UnderVerbose_EachLimitOfAReport_IsOneLine_UnderTheVerbAndTheLeg()
    {
        using var written = new StringWriter();
        var output = new ConsoleHarnessOutput(written, written, verbose: true);
        var report = new ContentionReport(
            [],
            [],
            [],
            [],
            [.. SamplingLimits.Always, SamplingLimits.NoCommandLine, SamplingLimits.NoStartTimes]);

        ContentionWarnings.Write(output, "test", "linux-x86_64-debug", report, new ContentionConfig());

        Assert.Equal(
            [
                "test: linux-x86_64-debug: sampling cannot see a tool started from inside the build directory by a relative path, since another process's working directory cannot be read",
                "test: linux-x86_64-debug: sampling cannot see the command line of a process that does not expose it, so such a process is counted and never matched against the build directory",
                "test: linux-x86_64-debug: sampling cannot see any command line on this machine, so nothing was matched against the build directory",
                "test: linux-x86_64-debug: sampling cannot see when some processes on this machine started, so a process id reused during the leg is reported as one process",
            ],
            written.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>The architecture names the four limits, and the frame they are said in, in the words this type owns, read from the code.</summary>
    [Fact]
    public void TheArchitecture_NamesTheFourLimits_InTheirOwnWords()
    {
        var architecture = Words(File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "architecture.md")));

        foreach (var limit in new[]
        {
            SamplingLimits.RelativeTool,
            SamplingLimits.HiddenCommandLine,
            SamplingLimits.NoCommandLine,
            SamplingLimits.NoStartTimes,
        })
        {
            Assert.Contains(limit, architecture, StringComparison.Ordinal);
        }

        Assert.Contains($"`{SamplingLimits.Said(string.Empty).TrimEnd()}`", architecture, StringComparison.Ordinal);
    }

    /// <summary>The repository this test was built from: the first directory above it holding the solution.</summary>
    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "RepoHarness.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No directory above '{AppContext.BaseDirectory}' holds RepoHarness.slnx.");
    }

    private static string Words(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
