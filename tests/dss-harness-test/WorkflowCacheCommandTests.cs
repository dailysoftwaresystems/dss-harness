using System.Text.Json.Nodes;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

/// <summary>
/// <c>workflow-cache</c> through the real binary: a pruning names the days it prunes by - left out, or outside what it
/// takes, the command is refused as a usage and removes nothing - and every answer names the cache's directory, in
/// text and in <c>--json</c>, a refused one included.
/// </summary>
public sealed class WorkflowCacheCommandTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>A pruning with no days named is refused as a usage, never read as a listing that exits 0.</summary>
    [Fact]
    public async Task Prune_WithoutItsDays_IsAUsageError()
    {
        using var temp = new TempDirectory();
        var (environment, _) = OwnUserData(temp);

        var result = await CliRunner.RunAsync(["workflow-cache", "prune"], Token, temp.Path, environment: environment);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("Option '--unused-days' is required.", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("holds", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// Days below none, or past what a pruning takes, are refused as a usage naming the cache's directory, in text and
    /// in --json, and nothing of the cache is removed - a day count below none would move the cutoff into the future
    /// and prune every entry.
    /// </summary>
    [Theory]
    [InlineData("-1")]
    [InlineData("36501")]
    public async Task Prune_WithDaysOutsideWhatItTakes_IsAUsageError_NamingTheCache_AndRemovesNothing(string days)
    {
        using var temp = new TempDirectory();
        var (environment, cache) = OwnUserData(temp);
        var blob = Path.Combine(cache, "blobs", "aa", new string('a', 38));
        Directory.CreateDirectory(Path.GetDirectoryName(blob)!);
        await File.WriteAllTextAsync(blob, "kept\n", Token);

        var text = await CliRunner.RunAsync(["workflow-cache", "prune", "--unused-days", days], Token, temp.Path, environment: environment);
        var json = await CliRunner.RunAsync(["workflow-cache", "prune", "--unused-days", days, "--json"], Token, temp.Path, environment: environment);

        var refusal = $"--unused-days is {days}; it is a number of days from 0 to 36,500. Nothing was removed from the workflow cache '{cache}'.";

        Assert.Equal((HarnessExit.UsageError, HarnessExit.UsageError), (text.ExitCode, json.ExitCode));
        Assert.Contains(refusal, text.StandardOutput + text.StandardError, StringComparison.Ordinal);

        var document = JsonNode.Parse(json.StandardOutput)!.AsObject();

        Assert.Equal((HarnessExit.UsageError, refusal, cache), (document["exitCode"]!.GetValue<int>(), document["summary"]!.GetValue<string>(), document["cache"]!.GetValue<string>()));
        Assert.True(File.Exists(blob));
    }

    /// <summary>The listing names the cache's directory in its line and in its document, beside what it holds.</summary>
    [Fact]
    public async Task List_NamesTheCachesDirectory_InTextAndInJson()
    {
        using var temp = new TempDirectory();
        var (environment, cache) = OwnUserData(temp);

        var text = await CliRunner.RunAsync(["workflow-cache", "list"], Token, temp.Path, environment: environment);
        var json = await CliRunner.RunAsync(["workflow-cache", "list", "--json"], Token, temp.Path, environment: environment);

        Assert.Equal((HarnessExit.Success, HarnessExit.Success), (text.ExitCode, json.ExitCode));
        Assert.Contains($"the workflow cache '{cache}' holds no entry; a remote workflow a use names fills it", text.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(
            new JsonObject
            {
                ["cache"] = cache,
                ["entries"] = new JsonArray(),
                ["unread"] = new JsonArray(),
                ["prunedEntries"] = null,
                ["prunedBlobs"] = null,
                ["notRemoved"] = null,
            }.ToJsonString(),
            JsonNode.Parse(json.StandardOutput)!.ToJsonString());
    }

    /// <summary>
    /// An environment giving the CLI a user's data of its own, below <paramref name="temp"/>, and the workflow cache's
    /// directory it then names: a test never reaches the cache of the machine's own user.
    /// </summary>
    private static (Dictionary<string, string?> Environment, string Cache) OwnUserData(TempDirectory temp)
    {
        var home = temp.Combine("home");
        var local = Path.Combine(home, "AppData", "Local");
        var share = Path.Combine(home, ".local", "share");
        Directory.CreateDirectory(home);

        var data = OperatingSystem.IsMacOS() ? Path.Combine(home, "Library", "Application Support")
            : OperatingSystem.IsWindows() ? local
            : share;

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["XDG_DATA_HOME"] = share,
            ["LOCALAPPDATA"] = local,
        };

        return (environment, Path.Combine(data, "dssharness", WorkflowCache.DirectoryName));
    }
}
