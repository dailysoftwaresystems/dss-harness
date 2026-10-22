using System.Text.Json.Nodes;
using DssHarness.Core.Hosts;
using DssHarness.Core.Processes;
using DssHarness.Core.Results;

namespace DssHarness.Tests;

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
    private static readonly string[] AgentDocument = ["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines", "needs", "refusals", "folded", "planned"];

    /// <summary>The ledger's members as a command that begins a run answers with it stopped, in its order.</summary>
    private static readonly string[] Ledger = ["exitCode", "summary", "runId", "passed", "cancelled", "unfinished", "complete", "legs"];

    /// <summary>The flow document's members, in its order: the session a run verb would have run, empty.</summary>
    private static readonly string[] FlowDocument = ["exitCode", "summary", "run", "status", "questions", "answered", "passedOver", "handoffs", "leftForAPerson", "spent", "records"];

    /// <summary>
    /// Every command that takes --json, with the members of the document it answers with where it stops before its body
    /// has an outcome, in their order - an empty list where its document is an array, which it answers with empty.
    /// </summary>
    private static readonly Dictionary<string, string[]> OwnDocuments = new(StringComparer.Ordinal)
    {
        ["list-worktree"] = ["exitCode", "summary", "worktrees", "elsewhere", "gone"],
        ["list-orchestrator"] = ["exitCode", "summary", "orchestrators"],
        ["list-agent-providers"] = ["exitCode", "summary", "slot", "files", "providers", "balanceKeyUsage", "tiers", "clients", "unappointed"],
        ["run-orchestrator"] = FlowDocument,
        ["resume-orchestrator"] = FlowDocument,
        ["answer-orchestrator"] = FlowDocument,
        ["pause-orchestrator"] = FlowDocument,
        ["stop-orchestrator"] = FlowDocument,
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
        ["validate-workflow"] = ["exitCode", "summary", "workflows", "passed"],
        ["export-workflow"] = ["exitCode", "summary", "workflow", "shipped", "destination", "files"],
        ["legs"] = ["exitCode", "summary", "legs", "hosts"],
        ["install-missing-tools"] = ["exitCode", "summary", "dryRun", "legs"],
        ["build"] = Ledger,
        ["test"] = Ledger,
        ["run"] = Ledger,
        ["check-mutations"] = Ledger,
        ["stop-run"] = ["exitCode", "summary", "run", "runDirectory", "dispatcher", "stopped", "records"],
        ["clean"] = ["exitCode", "summary", "passed", "cancelled", "unfinished", "complete", "legs"],
        ["up"] = ["exitCode", "summary", "direction"],
        ["down"] = ["exitCode", "summary", "direction"],
    };

    /// <summary>
    /// The commands whose stopped document says what their command line asked, each with the option that asks it and the
    /// member that says it - left out of one walk and given in the other, each holding the member to whether it was given,
    /// so a member that does not read the command line is seen.
    /// </summary>
    private static readonly Dictionary<string, (string Option, string Member)> Asked = new(StringComparer.Ordinal)
    {
        ["fix-line-endings"] = ("--check", "check"),
        ["install-missing-tools"] = ("--dry-run", "dryRun"),
        ["export-workflow"] = ("--shipped", "shipped"),
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
    /// null), no lines, nothing needed, no reason typed and nothing folded - and nothing of the leg ledger.
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

            Assert.Equal(["exitCode", "summary", "applied", "request", "heldNow", "heldUnread", "lines", "needs", "refusals", "folded", "planned"], document.Select(member => member.Key));
            Assert.False(document["applied"]!.GetValue<bool>());
            Assert.Null(document["request"]);
            Assert.Null(document["heldNow"]);
            Assert.Null(document["heldUnread"]);
            Assert.Empty(document["lines"]!.AsArray());
            Assert.Empty(document["needs"]!.AsArray());
            Assert.Empty(document["refusals"]!.AsArray());
            Assert.Null(document["folded"]);
            Assert.Null(document["planned"]);
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
    /// document of its own shape, empty - the code the process exits with in it, and the line it ends on, and what its
    /// command line asked where the document says it, here nothing, since no option that asks it is given - or, where its
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
            var required = CliRunner.RequiredArguments(help.StandardOutput);
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

            if (Asked.TryGetValue(command, out var flag))
            {
                Assert.False(answered[flag.Member]!.GetValue<bool>(), $"'{command}' without {flag.Option} answered {flag.Member} true.");
            }
        }

        Assert.Equal(OwnDocuments.Keys.Order(StringComparer.Ordinal), taking.Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// Every command the root help lists, given an option it does not take, is refused by the parser with the code a usage
    /// error has; asked for --json, a command that takes it answers first with its own document, empty - the first error
    /// its summary, the first line it ends on, what its command line asked where the document says it, here given and so
    /// said, and no run, since a command line the parser refuses never began one - and a command that takes no --json with
    /// nothing on standard
    /// output. Each argument is given a value, so the parser hands the option to none of them; an argument that takes any
    /// number of values takes an option nobody declared as one more, so a command whose last argument does is refused by
    /// an option given no value instead.
    /// </summary>
    [Fact]
    public async Task EveryCommandWhoseCommandLineIsRefused_AnswersWithItsOwnDocument_Empty_UnderJson()
    {
        using var temp = new TempDirectory();

        foreach (var command in await CliRunner.CommandsAsync(Token))
        {
            var help = await CliRunner.RunAsync([command, "--help"], Token);
            var arguments = CliRunner.Arguments(help.StandardOutput);
            var refusedBy = arguments.Count > 0 && arguments[^1].TrimEnd(']').EndsWith("...", StringComparison.Ordinal) ? "--directory" : "--no-such-option";
            string[] asked = Asked.TryGetValue(command, out var flag) ? [flag.Option] : [];
            var result = await CliRunner.RunAsync(
                [command, .. Enumerable.Repeat("x", arguments.Count), .. asked, "--json", refusedBy],
                Token,
                workingDirectory: temp.Path);

            Assert.Equal(HarnessExit.UsageError, result.ExitCode);
            Assert.Contains($"'{refusedBy}'", result.StandardError, StringComparison.Ordinal);
            Assert.EndsWith($"Run '{ToolPackage.Command} --help' for usage.", result.StandardError.TrimEnd(), StringComparison.Ordinal);

            if (!OwnDocuments.TryGetValue(command, out var members))
            {
                Assert.Equal(string.Empty, result.StandardOutput.Trim());
                continue;
            }

            var document = JsonNode.Parse(result.StandardOutput.TrimEnd());

            if (members.Length == 0)
            {
                Assert.Empty(Assert.IsType<JsonArray>(document));
                continue;
            }

            var answered = Assert.IsType<JsonObject>(document);

            Assert.Equal(members.Where(member => member != "runId"), answered.Select(member => member.Key));
            Assert.Equal(HarnessExit.UsageError, answered["exitCode"]!.GetValue<int>());
            Assert.Equal(result.StandardError.Split('\n')[0].TrimEnd('\r'), answered["summary"]!.GetValue<string>());

            if (asked.Length > 0)
            {
                Assert.True(answered[flag.Member]!.GetValue<bool>(), $"'{command} {flag.Option}' answered {flag.Member} false.");
            }
        }
    }

    /// <summary>
    /// A command line the parser refuses asks for the document only where the parser itself read --json as the command's
    /// own, set: wherever it sits among the command's arguments and options, and not as another option's value, after
    /// --, before the command's name, given false, or given twice with a value each - a flag whose own value the parser
    /// refuses reads as not given, so reading it never ends the process unanswered. Its summary is the first of the
    /// errors; check-mutations --list answers with its listing there too, and with the sweep's ledger, no run named,
    /// where its --list is so refused; and a flag a stopped document reports is reported false where so refused. Every
    /// one exits with the code a usage error has, its errors on standard error.
    /// </summary>
    [Fact]
    public async Task ACommandLineTheParserRefuses_AsksForTheDocument_OnlyWhereTheParserReadsItsJsonSet()
    {
        using var temp = new TempDirectory();
        string[] sweep = [.. Ledger.Where(member => member != "runId")];

        foreach (var (arguments, members, flag) in new (string[], string[], string?)[]
        {
            (["legs", "--no-such-option", "--json", "--nor-this-one"], OwnDocuments["legs"], null),
            (["fold-agent", "o1", "--json", "ag", "--no-such-option"], AgentDocument, null),
            (["check-mutations", "--list", "--no-such-option", "--json"], ["exitCode", "summary", "legs"], null),
            (["install-missing-tools", "--json", "--dry-run", "false", "--dry-run", "true", "--bogus"], OwnDocuments["install-missing-tools"], "dryRun"),
            (["fix-line-endings", "--json", "--check", "true", "--check", "false"], OwnDocuments["fix-line-endings"], "check"),
            (["check-mutations", "--json", "--list", "true", "--list", "false"], sweep, null),
        })
        {
            var result = await CliRunner.RunAsync(arguments, Token, workingDirectory: temp.Path);
            var document = Assert.IsType<JsonObject>(JsonNode.Parse(result.StandardOutput.TrimEnd()));

            Assert.Equal(HarnessExit.UsageError, result.ExitCode);
            Assert.Equal(members, document.Select(member => member.Key));
            Assert.Equal(HarnessExit.UsageError, document["exitCode"]!.GetValue<int>());
            Assert.Equal(result.StandardError.Split('\n')[0].TrimEnd('\r'), document["summary"]!.GetValue<string>());
            Assert.Contains(
                arguments.Contains("--no-such-option") ? "'--no-such-option'" : "expects a single argument but 2 were provided.",
                result.StandardError,
                StringComparison.Ordinal);
            Assert.EndsWith($"Run '{ToolPackage.Command} --help' for usage.", result.StandardError.TrimEnd(), StringComparison.Ordinal);

            if (flag is not null)
            {
                Assert.False(document[flag]!.GetValue<bool>(), $"'{string.Join(' ', arguments)}' answered {flag} true.");
            }
        }

        foreach (var arguments in new string[][]
        {
            ["legs", "--directory", "--json", "--no-such-option"],
            ["legs", "--directory=--json", "--no-such-option"],
            ["legs", "--no-such-option", "--", "--json"],
            ["legs", "--json", "false", "--no-such-option"],
            ["--json", "legs", "--no-such-option"],
            ["legs", "--json", "true", "--json", "false"],
            ["fold-agent", "o1", "ag", "--json", "true", "--json", "false"],
        })
        {
            var result = await CliRunner.RunAsync(arguments, Token, workingDirectory: temp.Path);

            Assert.Equal(HarnessExit.UsageError, result.ExitCode);
            Assert.Equal(string.Empty, result.StandardOutput.Trim());
            Assert.NotEqual(string.Empty, result.StandardError.Trim());
            Assert.True(
                !arguments.Contains("true") || result.StandardError.StartsWith("Option '--json' expects a single argument but 2 were provided.", StringComparison.Ordinal),
                result.StandardError);
            Assert.EndsWith($"Run '{ToolPackage.Command} --help' for usage.", result.StandardError.TrimEnd(), StringComparison.Ordinal);
        }
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

        // The run verbs answer with the flow document: the session they would have run, empty.
        foreach (var (arguments, exitCode) in new (string[], int)[]
        {
            (["resume-orchestrator", "BAD!"], HarnessExit.UsageError),
            (["answer-orchestrator", "BAD!", "more"], HarnessExit.UsageError),
            (["pause-orchestrator", "nosuch"], HarnessExit.Refused),
            (["stop-orchestrator", "nosuch"], HarnessExit.Refused),
        })
        {
            var result = await CliRunner.RunAsync([.. arguments, "--json"], Token, workingDirectory: kit.Main);

            Assert.Equal(exitCode, result.ExitCode);
            Assert.Equal(FlowDocument, Stopped(result).Select(member => member.Key));
        }
    }

    /// <summary>
    /// The help says, once for every command and again wherever a listing's document is described, what a command asked
    /// for --json answers with where it stops - a command line the parser refuses among the stops - its own document
    /// holding nothing, exitCode and summary first, which only the ledger, the agent document and the check-mutations
    /// --list listing carry on every exit, or an array empty - and that --help, -h or -? is the one exit without it, which
    /// holds; and the architecture says which document each command answers with, that a refused command line and a
    /// body's own usage error or refusal are answered with it too, how a refused command line's flags are read, and the
    /// same one exit without it.
    /// </summary>
    [Fact]
    public async Task TheHelpAndTheArchitectureSayWhatAStoppedCommandAnswersWith()
    {
        const string Said = "Asked for --json, a command answers with one document on every exit but two: --help, -h or -? prints its usage "
            + "instead, exit 0, whatever else its command line holds, even what the parser would otherwise refuse; and --json given "
            + "twice, a value each, asks for no document, so the parser's refusal is said alone, exit 10. Stopped before it "
            + "has anything of its own to say - a command line the parser refuses, a --directory that is not there, a repository not initialised, another usage "
            + "error, or a refusal met before anything is weighed - it answers with its own document holding nothing, exitCode and "
            + "summary first, which the leg ledger, the agent document and the check-mutations --list listing carry on every exit and "
            + "every other document only then, or, where its document is an array, with that array empty.";

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
                + "`install-missing-tools`, `check-ci-legs`, `check-anchor-citations`, `check-anchor-balance`, `fix-line-endings`, `validate-workflow`, "
                + "`export-workflow`, `up`, `down` and `stop-run` with their own document holding nothing, after `exitCode` and `summary` (`ReportJson.Stopped`); the run verbs "
                + "(`run-orchestrator`, `resume-orchestrator`, `answer-orchestrator`, `pause-orchestrator`, "
                + "`stop-orchestrator`) with the flow document - the session they would have run, empty "
                + "(`OrchestrationReports.FlowStopped`); `check-mutations --list` with "
                + "its listing, no legs; `list-agent-providers` with its listing, no provider - and with `--template` with the template's "
                + "own document, `template` null (`AgentProviderReports.Stopped`, `AgentProviderReports.TemplateStopped`); and "
                + "`read-anchor` and `read-anchors`, whose document is an array, with that array empty - the "
                + "process's exit code and the line it ends on say the rest. Each command registers which where it is wired "
                + "(`CommandRunner.Wire`, `JsonAnswer`), so the plumbing answers for it without choosing by its name: where the parser "
                + "refuses its command line, the first error the line it ends on, wherever the parser itself read `--json` as that "
                + "command's own and set - never as another option's value, after `--`, before the command's name or given twice with "
                + "a value each (`CommandRunner.UnparsedAsync`); where it stops the command itself; and where the body answers with no "
                + "document - a usage error or a refusal returned before anything is weighed (the session's own report, `CommandSession.RunAsync`): a command asked "
                + "for `--json` answers with one document on every exit save its usage and a `--json` given twice with a value each.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "One reader reads a command line's flags wherever the parser may have refused it (`CommandFlags`): whether `--json` is "
                + "set, and each flag a stopped document says - `fix-line-endings`' `check`, `install-missing-tools`' `dryRun`, "
                + "whether `check-mutations` answers with its listing, and whether `list-agent-providers` answers with its template. "
                + "It never throws: a flag whose own value the parser refused, "
                + "given twice with a value each, reads as not given. A command's body reads `--json` as that reader read it where "
                + "the command was wired (`CommandContext.Json`), never from the option again.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "The one exit of a command asked for `--json` without its document is its usage: `--help`, `-h` or `-?` prints it "
                + "instead, exit 0, whatever else the command line holds, even what the parser would otherwise refuse - the parser "
                + "answers it before any command's action, its errors cleared.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A leg-running command asked for `--json` answers with its ledger whatever ended it: a command line the parser refuses, a "
                + "refusal before any leg was placed, a selection no host could take,",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "A command stopped before anything is weighed - a command line the parser refuses, a `--directory` that is not there, a "
                + "repository not initialised, another usage error, or a refusal met before then, an agent that is not there among them - answers with the same document, empty "
                + "(`OrchestrationReports.Stopped`): `applied` false, `request`, `heldNow` and `heldUnread` null, no `lines`, no "
                + "`needs`, no `refusals`, and `folded` and `planned` null, its `exitCode` and `summary` the code it exits with and the line it ends on.",
            architecture,
            StringComparison.Ordinal);

        // The one exit without the document holds as both say it: the usage, exit 0, a part the parser would refuse and all.
        foreach (var arguments in new string[][]
        {
            ["legs", "--json", "--help"],
            ["fold-agent", "--json", "-h", "--no-such-option"],
            ["build", "--json", "-?"],
        })
        {
            var result = await CliRunner.RunAsync(arguments, Token);

            Assert.Equal(HarnessExit.Success, result.ExitCode);
            Assert.Equal(arguments[0], CliRunner.UsageLine(result.StandardOutput)?.Split(' ')[1]);
            Assert.Equal(string.Empty, result.StandardError.Trim());
        }
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
