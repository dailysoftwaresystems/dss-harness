using System.Text.Json.Nodes;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Results;

namespace RepoHarness.Tests;

/// <summary>
/// A command asked for <c>--json</c> that stops before its body has an outcome - a <c>--directory</c> that does not
/// exist, a repository not initialised, a usage error the command's plumbing catches - answers in its own document's
/// shape: that document's members, empty, with the code it exits with and the line it ends on. Never another command's
/// document: the leg ledger is the leg commands' alone.
/// </summary>
public sealed class StoppedDocumentTests
{
    private static readonly string[] LedgerOnly = ["passed", "cancelled", "unfinished", "complete"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The members of the one document <paramref name="result"/> answered with, after checking its code is the process's.</summary>
    private static JsonObject Stopped(ProcessResult result)
    {
        Assert.NotEqual(HarnessExit.Success, result.ExitCode);

        var document = JsonNode.Parse(result.StandardOutput.TrimEnd())!.AsObject();

        Assert.Equal(result.ExitCode, document["exitCode"]!.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(document["summary"]!.GetValue<string>()));
        Assert.Contains(document["summary"]!.GetValue<string>(), result.StandardError, StringComparison.Ordinal);

        return document;
    }

    /// <summary>
    /// An agent verb stopped before its body - its --directory not there, or a directory no repository holds - answers
    /// with the agent document: applied false, request null, nobody asked who holds anything (heldNow null, heldUnread
    /// null), no lines - and nothing of the leg ledger.
    /// </summary>
    [Fact]
    public async Task AnAgentVerbThatStops_AnswersWithTheAgentDocument_Empty()
    {
        using var temp = new TempDirectory();

        var missing = await CliRunner.RunAsync(["fold-agent", "o1", "ag", "--json", "--directory", temp.Combine("does-not-exist")], Token);
        var uninitialised = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--apply", "--json", "--directory", temp.Path], Token);

        Assert.Equal(HarnessExit.UsageError, missing.ExitCode);

        foreach (var result in new[] { missing, uninitialised })
        {
            var document = Stopped(result);

            Assert.Equal(["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines"], document.Select(member => member.Key));
            Assert.False(document["applied"]!.GetValue<bool>());
            Assert.Null(document["request"]);
            Assert.Null(document["heldNow"]);
            Assert.Null(document["heldUnread"]);
            Assert.Empty(document["lines"]!.AsArray());
        }
    }

    /// <summary>list-orchestrator stopped before its body answers with its listing, listing no orchestrator.</summary>
    [Fact]
    public async Task ListOrchestratorThatStops_AnswersWithItsListing_Empty()
    {
        using var temp = new TempDirectory();

        var document = Stopped(await CliRunner.RunAsync(["list-orchestrator", "--json", "--directory", temp.Combine("does-not-exist")], Token));

        Assert.Equal(["exitCode", "summary", "orchestrators"], document.Select(member => member.Key));
        Assert.Empty(document["orchestrators"]!.AsArray());
    }

    /// <summary>list-worktree stopped before its body answers with its listing, listing no worktree and no copy.</summary>
    [Fact]
    public async Task ListWorktreeThatStops_AnswersWithItsListing_Empty()
    {
        using var temp = new TempDirectory();

        var document = Stopped(await CliRunner.RunAsync(["list-worktree", "--json", "--directory", temp.Combine("does-not-exist")], Token));

        Assert.Equal(["exitCode", "summary", "worktrees", "elsewhere", "gone"], document.Select(member => member.Key));
        Assert.All(new[] { "worktrees", "elsewhere", "gone" }, member => Assert.Empty(document[member]!.AsArray()));
    }

    /// <summary>
    /// check-mutations --list stopped - its --directory not there, or a usage error it refuses before anything is read -
    /// answers with the listing document, listing no leg; the same command sweeping answers with its ledger.
    /// </summary>
    [Fact]
    public async Task CheckMutationsListThatStops_AnswersWithTheListingDocument_AndASweepWithItsLedger()
    {
        using var temp = new TempDirectory();

        var missing = await CliRunner.RunAsync(["check-mutations", "--list", "--json", "--directory", temp.Combine("does-not-exist")], Token);
        var refused = await CliRunner.RunAsync(["check-mutations", "--list", "--force-lock", "--json", "--directory", temp.Path], Token);
        var sweep = await CliRunner.RunAsync(["check-mutations", "--json", "--directory", temp.Combine("does-not-exist")], Token);

        Assert.Equal(HarnessExit.UsageError, refused.ExitCode);

        foreach (var result in new[] { missing, refused })
        {
            var document = Stopped(result);

            Assert.Equal(["exitCode", "summary", "legs"], document.Select(member => member.Key));
            Assert.Empty(document["legs"]!.AsArray());
        }

        var ledger = Stopped(sweep);

        Assert.All(LedgerOnly, member => Assert.True(ledger.ContainsKey(member), member));
        Assert.False(string.IsNullOrEmpty(ledger["runId"]!.GetValue<string>()));
    }

    /// <summary>A leg command stopped before its body answers with its ledger, as it always has: no legs, nothing passed.</summary>
    [Fact]
    public async Task ALegCommandThatStops_AnswersWithItsLedger()
    {
        using var temp = new TempDirectory();

        var document = Stopped(await CliRunner.RunAsync(["build", "--json", "--directory", temp.Combine("does-not-exist")], Token));

        Assert.All(LedgerOnly, member => Assert.True(document.ContainsKey(member), member));
        Assert.False(document["passed"]!.GetValue<bool>());
        Assert.False(document["complete"]!.GetValue<bool>());
        Assert.Empty(document["legs"]!.AsArray());
    }
}
