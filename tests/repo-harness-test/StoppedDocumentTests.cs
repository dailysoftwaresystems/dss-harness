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

    /// <summary>The agent document's members, in its order.</summary>
    private static readonly string[] AgentDocument = ["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines"];

    /// <summary>The ledger's members as a command that begins a run answers with it stopped, in its order.</summary>
    private static readonly string[] Ledger = ["exitCode", "summary", "runId", "passed", "cancelled", "unfinished", "complete", "legs"];

    /// <summary>
    /// Every command that takes --json, with the members of the document it answers with where it stops before its body
    /// has an outcome, in their order - an empty list where its document is an array, which it answers with empty.
    /// </summary>
    private static readonly Dictionary<string, string[]> OwnDocuments = new(StringComparer.Ordinal)
    {
        ["list-worktree"] = ["exitCode", "summary", "worktrees", "elsewhere", "gone"],
        ["list-orchestrator"] = ["exitCode", "summary", "orchestrators"],
        ["refresh-agent"] = AgentDocument,
        ["rebase-agent"] = AgentDocument,
        ["fold-agent"] = AgentDocument,
        ["delete-agent"] = AgentDocument,
        ["read-anchor"] = [],
        ["read-anchors"] = [],
        ["check-anchor-balance"] =
        [
            "exitCode", "summary", "base", "baseCommit", "commit", "merging", "openAtBase", "openNow", "netNew", "passed", "closed", "lost",
            "opened", "missingAtBase", "malformedAtBase", "findings",
        ],
        ["check-anchor-citations"] = ["exitCode", "summary", "subject", "roots", "filesScanned", "citationsFound", "registryRows", "passed", "unresolved"],
        ["fix-line-endings"] = ["exitCode", "summary", "scope", "check", "considered", "binary", "undeclared", "excluded", "changed"],
        ["check-ci-legs"] = ["exitCode", "summary", "branch", "runs"],
        ["legs"] = ["exitCode", "summary", "legs", "hosts"],
        ["install-missing-tools"] = ["exitCode", "summary", "dryRun", "legs"],
        ["build"] = Ledger,
        ["test"] = Ledger,
        ["run"] = Ledger,
        ["check-mutations"] = Ledger,
        ["clean"] = ["exitCode", "summary", "passed", "cancelled", "unfinished", "complete", "legs"],
    };

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

    /// <summary>
    /// Every command the root help lists that takes --json, stopped by a --directory that is not there, answers with one
    /// document of its own shape, empty - the code the process exits with in it, and the line it ends on - or, where its
    /// document is an array, with that array, empty: never nothing, and never another command's document. A command that
    /// takes --json and registers no document of its own fails here, and so does one this table does not know.
    /// </summary>
    [Fact]
    public async Task EveryCommandThatTakesJson_AnswersWithItsOwnDocument_Empty_WhereItStops()
    {
        using var temp = new TempDirectory();
        var missing = temp.Combine("does-not-exist");
        var taking = new List<string>();

        foreach (var command in await CliRunner.CommandsAsync(Token))
        {
            var help = await CliRunner.RunAsync([command, "--help"], Token);

            if (!help.StandardOutput.Contains("--json", StringComparison.Ordinal))
            {
                continue;
            }

            taking.Add(command);

            // Each argument the command requires, given a value, so it is the missing directory that stops it.
            var usage = help.StandardOutput.Split('\n').SkipWhile(line => !line.StartsWith("Usage:", StringComparison.Ordinal)).Skip(1).First();
            var required = usage.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(token => token.StartsWith('<'));
            var result = await CliRunner.RunAsync([command, .. Enumerable.Repeat("x", required), "--json", "--directory", missing], Token);

            Assert.True(OwnDocuments.TryGetValue(command, out var members), $"'{command}' takes --json, and this test does not know its document.");
            Assert.Equal(HarnessExit.UsageError, result.ExitCode);

            var document = JsonNode.Parse(result.StandardOutput.TrimEnd());

            if (members.Length == 0)
            {
                Assert.Empty(Assert.IsType<JsonArray>(document));
                continue;
            }

            var answered = Assert.IsType<JsonObject>(document);

            Assert.Equal(members, answered.Select(member => member.Key));
            Assert.Equal(result.ExitCode, answered["exitCode"]!.GetValue<int>());
            Assert.Contains(answered["summary"]!.GetValue<string>(), result.StandardError, StringComparison.Ordinal);
        }

        Assert.Equal(OwnDocuments.Keys.Order(StringComparer.Ordinal), taking.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A usage error or a refusal a command's body returns before it weighs anything - a name no command takes, two
    /// options that cannot go together, an agent or an orchestrator that is not there - is answered with the command's
    /// own document, empty, as a stop its plumbing meets is: never nothing.
    /// </summary>
    [Fact]
    public async Task AUsageErrorOrARefusalTheBodyReturns_IsAnsweredWithTheCommandsOwnDocument_Empty()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");

        foreach (var (arguments, exitCode) in new (string[], int)[]
        {
            (["fold-agent", "BAD!", "ag"], HarnessExit.UsageError),
            (["fold-agent", "o1", "ag", "--merge", "x", "--settled", "x"], HarnessExit.UsageError),
            (["fold-agent", "o1", "nosuch"], HarnessExit.Refused),
            (["refresh-agent", "BAD!", "ag", "--all"], HarnessExit.UsageError),
            (["rebase-agent", "BAD!", "ag"], HarnessExit.UsageError),
            (["delete-agent", "o1", "BAD!"], HarnessExit.UsageError),
        })
        {
            var result = await CliRunner.RunAsync([.. arguments, "--json"], Token, workingDirectory: kit.Main);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(AgentDocument, Stopped(result).Select(member => member.Key));
        }

        foreach (var (arguments, exitCode) in new (string[], int)[]
        {
            (["list-orchestrator", "BAD!"], HarnessExit.UsageError),
            (["list-orchestrator", "nosuch"], HarnessExit.Refused),
        })
        {
            var result = await CliRunner.RunAsync([.. arguments, "--json"], Token, workingDirectory: kit.Main);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(OwnDocuments["list-orchestrator"], Stopped(result).Select(member => member.Key));
        }
    }

    /// <summary>
    /// The help says, once for every command and again wherever a listing's document is described, what a command asked
    /// for --json answers with where it stops - its own document holding nothing, exitCode and summary first, which a
    /// listing carries only then, or an array empty; and the architecture says which document each command answers with,
    /// and that a body's own usage error or refusal is answered with it too.
    /// </summary>
    [Fact]
    public async Task TheHelpAndTheArchitectureSayWhatAStoppedCommandAnswersWith()
    {
        const string Said = "Asked for --json, a command answers with one document on every exit: stopped before it has anything of its own to "
            + "say - a --directory that is not there, a repository not initialised, a usage error, or a refusal met before anything is "
            + "weighed - it answers with its own document holding nothing, exitCode and summary first, which a listing carries only "
            + "then, or, where its document is an array, with that array empty.";

        foreach (var topic in new[] { "worktrees", "mutations", "orchestrators" })
        {
            var help = await CliRunner.RunAsync(["help", topic], Token);

            Assert.Contains("--json prints the listing as one JSON document. " + Said, Prose.Words(help.StandardOutput), StringComparison.Ordinal);
        }

        Assert.Contains(Said, Prose.Words((await CliRunner.RunAsync(["help"], Token)).StandardOutput), StringComparison.Ordinal);

        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "Every other command asked for `--json` that stops before it has anything of its own to say answers in its own document's "
                + "shape, never the ledger's: the agent verbs with the agent document, empty; `list-orchestrator`, `list-worktree`, `legs`, "
                + "`install-missing-tools`, `check-ci-legs`, `check-anchor-citations`, `check-anchor-balance` and `fix-line-endings` with "
                + "their own document holding nothing, after `exitCode` and `summary` (`ReportJson.Stopped`); `check-mutations --list` with "
                + "its listing, no legs; and `read-anchor` and `read-anchors`, whose document is an array, with that array empty - the "
                + "process's exit code and the line it ends on say the rest. Each command registers which with `CommandRunner.Wrap` "
                + "(`JsonAnswer`), so the plumbing answers for it without choosing by its name, both where it stops the command itself and "
                + "where the body answers with no document - a usage error or a refusal returned before anything is weighed "
                + "(`CommandRunner.Report`): a command asked for `--json` answers with one document on every exit.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A command stopped before anything is weighed - a `--directory` that is not there, a repository not initialised, a usage "
                + "error, or a refusal met before then, an agent that is not there among them - answers with the same document, empty "
                + "(`OrchestrationReports.Stopped`): `applied` false, `request`, `heldNow` and `heldUnread` null, no `lines`, its "
                + "`exitCode` and `summary` the code it exits with and the line it ends on.",
            architecture,
            StringComparison.Ordinal);
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
