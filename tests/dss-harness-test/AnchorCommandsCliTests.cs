using System.Text.Json;
using RepoHarness.Core.Anchors;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>The anchor commands through the built CLI: arguments, output streams and exit codes.</summary>
public sealed class AnchorCommandsCliTests
{
    private const string One = "D-AREA-TOPIC-ONE";

    [Fact]
    public async Task AnAnchor_CanBeWritten_Read_Closed_AndListed()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);

        var written = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger", "breaks | when retried", "-C", temp.Path],
            cancellationToken);
        Assert.Equal(HarnessExit.Success, written.ExitCode);
        Assert.Contains($"added {One} to the pending registry", written.StandardOutput, StringComparison.Ordinal);

        // The status is emoji: this also proves output reaches a pipe as UTF-8 on every platform.
        var read = await CliRunner.RunAsync(["read-anchor", One, "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, read.ExitCode);
        Assert.Contains("status      : 🟠 OPEN   -> OPEN", read.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("breaks | when retried", read.StandardOutput, StringComparison.Ordinal);

        var closed = await CliRunner.RunAsync(["set-anchor", One, "--status", "closed", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, closed.ExitCode);
        Assert.Contains($"moved {One} to the done registry", closed.StandardOutput, StringComparison.Ordinal);

        var listed = await CliRunner.RunAsync(["read-anchors", "--done", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        // The status column is padded to 13 visible characters, then one space separates the id.
        Assert.Contains($"P1  ✅ CLOSED      {One}", listed.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("1 row(s) in the done registry.", listed.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAnchor_WithMissingIds_ExitsOne_AndItsJsonStillParses()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);
        await WriteAsync(temp, One);

        var result = await CliRunner.RunAsync(
            ["read-anchor", One, "D-AREA-TOPIC-MISSING", "--json", "-C", temp.Path],
            cancellationToken);

        Assert.Equal(AnchorExit.Findings, result.ExitCode);
        Assert.Contains("D-AREA-TOPIC-MISSING", result.StandardError, StringComparison.Ordinal);

        using var json = JsonDocument.Parse(result.StandardOutput);
        var anchor = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal(One, anchor.GetProperty("anchor").GetString());
        Assert.Equal("🟠 OPEN", anchor.GetProperty("status").GetString());
        Assert.False(anchor.GetProperty("closed").GetBoolean());
    }

    [Fact]
    public async Task ReadAnchors_Json_ListsEveryAnchorWithItsRegistry()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareAsync(temp);
        await WriteAsync(temp, One);

        var result = await CliRunner.RunAsync(["read-anchors", "--json", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        using var json = JsonDocument.Parse(result.StandardOutput);
        var anchor = Assert.Single(json.RootElement.EnumerateArray());
        Assert.Equal("pending", anchor.GetProperty("registry").GetString());
    }

    [Fact]
    public async Task WriteAnchor_WithADryRun_WritesNothing()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);
        var before = File.ReadAllText(temp.Combine(".plans", "_deferred-anchor-registry.md"));

        var result = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger", "t", "--anchor-dry-run", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("dry run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(temp.Combine(".plans", "_deferred-anchor-registry.md")));
    }

    [Theory]
    [InlineData("--trigger", "t")]
    [InlineData("--priority", "P1")]
    public async Task WriteAnchor_RequiresBothPriorityAndTrigger(string option, string value)
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["write-anchor", One, option, value, "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task WriteAnchor_RefusesDone_AsAStatus()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["write-anchor", One, "--priority", "P1", "--trigger", "t", "--status", "done", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("open, gated, disclosed, closed", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetAnchor_OfAnIdWithNoRow_IsRefused()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["set-anchor", One, "--priority", "P0", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
    }

    [Theory]
    [InlineData("read-anchors")]
    [InlineData("read-anchor", One)]
    [InlineData("set-anchor", One, "--priority", "P0")]
    public async Task PendingAndDone_CannotBeCombined(params string[] command)
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            [.. command, "--pending", "--done", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--pending and --done cannot be combined", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadAnchorsLint_ExitsOne_WhenARegistryHasAProblem()
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);
        File.AppendAllText(temp.Combine(".plans", "_deferred-anchor-registry.md"), $"| `{One}` | P1 | ✅ CLOSED | t | - | - |\n");

        var result = await CliRunner.RunAsync(["read-anchors", "--lint", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(AnchorExit.Findings, result.ExitCode);
        Assert.Contains($"closed anchor '{One}' is in the pending registry", result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--pending")]
    [InlineData("--done")]
    [InlineData("--band", "P1")]
    [InlineData("--open")]
    [InlineData("--closed")]
    public async Task ReadAnchorsLint_RefusesAListingFilter_RatherThanIgnoringIt(params string[] filter)
    {
        using var temp = new TempDirectory();
        await PrepareAsync(temp);

        var result = await CliRunner.RunAsync(
            ["read-anchors", "--lint", .. filter, "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains(filter[0], result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckAnchorBalance_HoldsOnACleanTree_AndFailsWhenOpenAnchorsRise()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp);
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);

        var clean = await CliRunner.RunAsync(["check-anchor-balance", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, clean.ExitCode);
        Assert.Contains("the balance holds", clean.StandardOutput, StringComparison.Ordinal);

        await WriteAsync(temp, One);

        var risen = await CliRunner.RunAsync(["check-anchor-balance", "--base", "HEAD", "--json", "-C", temp.Path], cancellationToken);
        Assert.Equal(AnchorExit.Findings, risen.ExitCode);

        using var json = JsonDocument.Parse(risen.StandardOutput);
        Assert.Equal(1, json.RootElement.GetProperty("netNew").GetInt32());
        Assert.False(json.RootElement.GetProperty("passed").GetBoolean());
        Assert.Contains("this change creates 1 more anchor(s) than its work closes", risen.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A row closed since the base through set-anchor, its Trigger opening with the bookkeeping pair where the Trigger
    /// carries the verdict, leaves the open count and is named as bookkeeping and not credited, in the text and in the
    /// JSON.
    /// </summary>
    [Fact]
    public async Task CheckAnchorBalance_NamesABookkeepingClosure_AndDoesNotCreditIt()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var harness = await PrepareAsync(temp, HarnessFactory.TriggerCarriesVerdict());
        await WriteAsync(temp, One);
        await harness.CommitAllAsync(temp.Path, "base", cancellationToken);

        var closing = await CliRunner.RunAsync(
            ["set-anchor", One, "--status", "closed", "--trigger", "✅🧾 **CLOSED** 2026-10-01, mark repaired: the work predates the base", "-C", temp.Path],
            cancellationToken);
        Assert.Equal(HarnessExit.Success, closing.ExitCode);

        var text = await CliRunner.RunAsync(["check-anchor-balance", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, text.ExitCode);
        Assert.Contains("open      1 at HEAD, 0 now", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("change    1 closed (1 bookkeeping), 0 opened (0 created, 0 disclosed); counted 0", text.StandardOutput, StringComparison.Ordinal);
        Assert.Contains($"  - {One}   [bookkeeping: not credited]", text.StandardOutput, StringComparison.Ordinal);

        var json = await CliRunner.RunAsync(["check-anchor-balance", "--json", "-C", temp.Path], cancellationToken);
        using var receipt = JsonDocument.Parse(json.StandardOutput);
        var closed = Assert.Single(receipt.RootElement.GetProperty("closed").EnumerateArray());
        Assert.Equal(One, closed.GetProperty("anchor").GetString());
        Assert.True(closed.GetProperty("bookkeeping").GetBoolean());
        Assert.Equal(0, receipt.RootElement.GetProperty("netNew").GetInt32());
    }

    [Fact]
    public async Task AnchorCommands_BeforeInit_ReportNotInitialised()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(["read-anchors", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.NotInitialized, result.ExitCode);
    }

    private static async Task<HarnessFactory> PrepareAsync(TempDirectory temp, HarnessConfig? config = null)
    {
        var harness = new HarnessFactory();
        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, config);
        return harness;
    }

    private static Task WriteAsync(TempDirectory temp, string id)
        => new HarnessFactory().AnchorRegistryService.WriteAsync(
            temp.Path,
            new AnchorWriteRequest(id, "P1", "trigger"),
            dryRun: false,
            TestContext.Current.CancellationToken);
}
