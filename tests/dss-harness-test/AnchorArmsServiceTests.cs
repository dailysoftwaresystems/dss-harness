using System.Text.Json;
using DssHarness.Core.Anchors;
using DssHarness.Core.Build;
using DssHarness.Core.Configuration;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Mutations;
using DssHarness.Core.Platform;
using DssHarness.Core.Results;
using DssHarness.Core.Sync;
using DssHarness.Core.Worktrees;

namespace DssHarness.Tests;

/// <summary>
/// What check-anchor-arms holds of anchor rows' claims about mutation arms, over the one reading a listing is made of:
/// an arm a done row names must be unmoved here with a recorded passed on the legs its S row names, a pass cited beside
/// a run id must be spelled as history, and a backticked id no A row holds is an arm only where git history once
/// declared it.
/// </summary>
public sealed class AnchorArmsServiceTests
{
    private const string One = "D-AREA-TOPIC-ONE";
    private const string Two = "D-AREA-TOPIC-TWO";
    private const string Run = "20261021-192254-56514608";

    /// <summary>One BUILD-RED arm and its paired control, citing four texts and one site.</summary>
    private const string Registry = """
        A | one | src/site.txt | texts/one.before | texts/one.after | BUILD-RED | app | - | 0 | PAIRED-CONTROL | why one
        B | one | texts/one.control-before | texts/one.control-after | another builds
        """;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>
    /// A done row naming an arm no leg has swept is a finding that fails the check, worded as the listing words the
    /// arm's first reason.
    /// </summary>
    [Fact]
    public async Task ADoneRowNamingAnArmNeverSwept_IsAFinding_InTheListingsOwnWords()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(AnchorFindingSeverity.Fatal, finding.Severity);
        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'native' it is pending: never swept on this leg",
            finding.Message);

        var outcome = AnchorReports.Arms(report, json: false);

        Assert.Equal(AnchorExit.Findings, outcome.ExitCode);
        Assert.Contains(MutationListing.Note, string.Join("\n", outcome.Data), StringComparison.Ordinal);
    }

    /// <summary>
    /// A done row naming an arm whose recorded passed nothing read here moved for holds: the check passes, saying the
    /// arm is unmoved here and never that it holds.
    /// </summary>
    [Fact]
    public async Task ADoneRowWhoseArmIsUnmovedWithARecordedPassed_Holds()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await SweepRecordedAsync(harness, repository, "native", "one");
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.True(report.Passed, string.Join(" | ", report.Findings.Select(finding => finding.Message)));
        Assert.Equal(["one"], report.NamedArms);
        Assert.Equal((1, 1, 1), (report.Rows, report.DoneRows, report.NamedArms.Count));

        var outcome = AnchorReports.Arms(report, json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Contains("every arm a done row names is unmoved here with a recorded passed", outcome.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An arm is judged on each leg separately: pending on the leg that never swept it, unmoved with a recorded passed
    /// on the leg that did, and the finding names the one leg alone.
    /// </summary>
    [Fact]
    public async Task AnArmPendingOnOneLeg_IsAFindingForThatLegAlone()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp, legs: ["native", "second"]);
        await SweepRecordedAsync(harness, repository, "second", "one");
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.Equal(["native", "second"], report.Legs);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(
            $"anchor '{One}' names arm 'one', and on leg 'native' it is pending: never swept on this leg",
            finding.Message);
    }

    /// <summary>
    /// A done row's arm on a leg that cannot be swept is pending with the leg's own reason, said as the listing says it.
    /// </summary>
    [Fact]
    public async Task ALegThatCannotBeSwept_IsTheReasonTheFindingNames()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry + "\nS | one | awkward | why one is scoped\n",
            legs: ["native", "awkward"],
            awkward: true);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        var finding = Assert.Single(report.Findings);

        Assert.Contains($"on leg 'awkward' it is pending: ", finding.Message, StringComparison.Ordinal);
        Assert.True(finding.Message.Contains("builds with toolchain 'other'", StringComparison.Ordinal), finding.Message);
    }

    /// <summary>
    /// A pass word followed by a run id, spelled by no form of history, is a finding: a pass holds only of the bytes
    /// that run read.
    /// </summary>
    [Fact]
    public async Task APassCitedBesideARunId_OutsideAnyHistoryForm_IsAFinding()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"the frame shipped; PASSED ({Run}) by the sweep") { Status = "closed" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Contains($"states a pass of run '{Run}'", finding.Message, StringComparison.Ordinal);
        Assert.Contains("a span matching a form of anchors.historyForms", finding.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A pass cited inside a span matching a configured history form is the row's own record of when, and is not
    /// reported; one outside it still is, so the forms spell history rather than turn the rule off.
    /// </summary>
    [Fact]
    public async Task APassCitedInsideAConfiguredHistoryForm_IsNotReported()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            new AnchorSettings
            {
                HistoryForms = ["closed at {commit}, passed ({run})"],
            });
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"closed at 0123456789abcdef0123456789abcdef01234567, passed ({Run})") { Status = "closed" },
            dryRun: false,
            Token);

        var history = await service.CheckAsync(repository, null, Token);

        Assert.True(history.Passed, string.Join(" | ", history.Findings.Select(finding => finding.Message)));

        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(Two, "P2", $"but PASSED ({Run}) is not history"),
            dryRun: false,
            Token);

        var outside = await service.CheckAsync(repository, null, Token);

        Assert.Contains(outside.Findings, finding => finding.Message.Contains($"states a pass of run '{Run}'", StringComparison.Ordinal));
    }

    /// <summary>
    /// A backticked id no A row holds is an arm where git history shows an A row declared it once - renamed, or retired -
    /// and is a finding; a backticked token that never was an arm is left alone.
    /// </summary>
    [Fact]
    public async Task ARetiredArmId_OneDeclaredInHistory_IsAFinding_AnIdNeverDeclaredIsNot()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(
            temp,
            registry: Registry.Replace("| one |", "| old-name |", StringComparison.Ordinal));
        RegistryWritten(temp, Registry);
        await harness.RunGitAsync(repository, ["add", "--all"], Token);
        await harness.RunGitAsync(repository, ["commit", "--quiet", "-m", "the arm renamed"], Token);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `old-name` and `never-was`" },
            dryRun: false,
            Token);

        var report = await service.CheckAsync(repository, null, Token);

        Assert.False(report.Passed);
        var finding = Assert.Single(report.Findings);

        Assert.Equal(
            $"anchor '{One}' names arm 'old-name', which no A row of 'arms.md' holds: an A row declared it once, "
                + "so it was renamed or retired, and the row cites what the registry no longer declares",
            finding.Message);
    }

    /// <summary>A shallow clone's history is cut, and the check says so in a note that fails nothing.</summary>
    [Fact]
    public async Task AShallowCloneCutHistory_IsANoteThatFailsNothing()
    {
        using var temp = new TempDirectory();
        var (harness, repository, service) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", "the report frame") { Status = "closed", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);
        await harness.CommitAllAsync(repository, "the rows", Token);

        var clone = temp.Combine("clone");
        await harness.RunGitAsync(temp.Path, ["clone", "--quiet", "--depth", "1", repository.Replace('\\', '/').Insert(0, "file:///"), clone], Token);

        var report = await service.CheckAsync(clone, null, Token);

        var note = report.Findings.Single(finding => finding.Severity == AnchorFindingSeverity.Note);

        Assert.Contains("this clone's history is shallow", note.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every exit answers in the command's own document: a finding's, a pass's, and one stopped before it read
    /// anything, each with its code and the members it always holds.
    /// </summary>
    [Fact]
    public async Task EveryExitAnswersInItsOwnDocument()
    {
        using var temp = new TempDirectory();
        var (harness, repository, _) = await PrepareAsync(temp);
        await harness.AnchorRegistryService.WriteAsync(
            repository,
            new AnchorWriteRequest(One, "P1", $"the frame shipped; PASSED ({Run}) by the sweep") { Status = "closed" },
            dryRun: false,
            Token);

        var findings = await CliRunner.RunAsync(["check-anchor-arms", "--json", "-C", repository], Token);

        Assert.True(AnchorExit.Findings == findings.ExitCode, findings.StandardError + findings.StandardOutput);

        using (var document = JsonDocument.Parse(findings.StandardOutput.TrimEnd()))
        {
            Assert.Equal(
                ["armsRegistry", "mutationRecord", "legs", "rows", "doneRows", "namedArms", "passed", "findings"],
                document.RootElement.EnumerateObject().Select(member => member.Name));
            Assert.Equal("arms.md", document.RootElement.GetProperty("armsRegistry").GetString());
            Assert.Contains($"anchor '{One}' states a pass of run '{Run}'", document.RootElement.GetProperty("findings").EnumerateArray().Single().GetProperty("message").GetString(), StringComparison.Ordinal);
        }

        await SweepRecordedAsync(harness, repository, "native", "one");
        await harness.AnchorRegistryService.SetAsync(
            repository,
            new AnchorSetRequest(One) { Trigger = "the frame shipped, and the arm is named below", CrossRefs = "closed with `one`" },
            dryRun: false,
            Token);

        var clean = await CliRunner.RunAsync(["check-anchor-arms", "--json", "-C", repository], Token);

        Assert.Equal(HarnessExit.Success, clean.ExitCode);

        using (var document = JsonDocument.Parse(clean.StandardOutput.TrimEnd()))
        {
            Assert.True(document.RootElement.GetProperty("passed").GetBoolean());
            Assert.Equal(["one"], document.RootElement.GetProperty("namedArms").EnumerateArray().Select(arm => arm.GetString()));
        }

        var stopped = await CliRunner.RunAsync(["check-anchor-arms", "--json", "--legs", "no-such-leg", "-C", repository], Token);

        Assert.Equal(HarnessExit.UsageError, stopped.ExitCode);

        using (var document = JsonDocument.Parse(stopped.StandardOutput.TrimEnd()))
        {
            Assert.Equal(HarnessExit.UsageError, document.RootElement.GetProperty("exitCode").GetInt32());
            Assert.Equal(0, document.RootElement.GetProperty("rows").GetInt32());
            Assert.False(document.RootElement.GetProperty("passed").GetBoolean());
            Assert.Empty(document.RootElement.GetProperty("findings").EnumerateArray());
        }
    }

    /// <summary>The listing the reading serves is unchanged: check-mutations --list still answers as it did.</summary>
    [Fact]
    public async Task TheListingAListAnswers_IsUnchanged()
    {
        using var temp = new TempDirectory();
        var (_, repository, _) = await PrepareAsync(temp);

        var listed = await CliRunner.RunAsync(["check-mutations", "--list", "-C", repository], Token);

        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.Contains("listing 1 arm(s) of 'arms.md' on 1 leg(s); nothing is swept and no host is asked", listed.StandardOutput + listed.StandardError, StringComparison.Ordinal);
        Assert.Contains("'unmoved here' is not 'holds'", listed.StandardOutput + listed.StandardError, StringComparison.Ordinal);
    }

    /// <summary>The repository: one arm of two texts and a site, the legs the test names, and the service over it all.</summary>
    private static async Task<(HarnessFactory Harness, string Repository, AnchorArmsService Service)> PrepareAsync(
        TempDirectory temp,
        AnchorSettings? anchors = null,
        string registry = Registry,
        string[]? legs = null,
        bool awkward = false)
    {
        var harness = new HarnessFactory();
        var legNames = legs ?? ["native"];
        var legToolchains = legNames.ToDictionary(name => name, name => awkward && name == "awkward" ? "other" : "ninja", StringComparer.Ordinal);

        await harness.InitializeHarnessAsync(
            temp.Path,
            Token,
            new HarnessConfig
            {
                Toolchains =
                {
                    ["ninja"] = new ToolchainConfig { Generator = "Ninja", Env = { ["CC"] = "cc", ["CXX"] = "c++" } },
                    ["other"] = new ToolchainConfig { Generator = "Visual Studio 17 2022", Env = { ["CC"] = "cc", ["CXX"] = "c++" } },
                },
                BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
                Projects = { Project() },
                Legs = legNames.ToDictionary(
                    name => name,
                    name => new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug", Toolchain = legToolchains[name] },
                    StringComparer.Ordinal),
                Worktrees = new WorktreeSettings { Root = ".worktrees" },
                Mutations = new MutationSettings { Registry = "arms.md", TextDirectory = "texts" },
                Anchors = anchors ?? new AnchorSettings(),
            });

        RegistryWritten(temp, registry);
        await harness.CommitAllAsync(temp.Path, "the arms registry", Token);

        return (harness, temp.Path, new AnchorArmsService(
            harness.ContextLoader,
            harness.AnchorRegistryLocator,
            harness.AnchorRegistryLock,
            new ArmListingReader(SyncKit.Service(harness), harness.FileSystem, harness.Platform),
            harness.GitClient,
            harness.FileSystem));
    }

    /// <summary>Writes the registry and every file it cites.</summary>
    private static void RegistryWritten(TempDirectory temp, string registry)
    {
        temp.WriteFile("arms.md", registry.EndsWith('\n') ? registry : registry + "\n");
        temp.WriteFile(Path.Combine("src", "site.txt"), "site\n");

        (string Name, string Text)[] texts =
        [
            ("one.before", "before\n"),
            ("one.after", "after\n"),
            ("one.control-before", "control before\n"),
            ("one.control-after", "control after\n"),
        ];

        foreach (var (name, text) in texts)
        {
            temp.WriteFile(Path.Combine("texts", name), text);
        }
    }

    /// <summary>The project the legs build, of the kind a sweep can read the build of.</summary>
    private static ProjectConfig Project() => new()
    {
        Name = "app",
        Type = "cmake",
        Path = ".",
        BuildOutputs = [BuildOutput.Keyed([new(PlatformNames.Windows, "app.exe"), new(PlatformScope.Every, "app")])],
    };

    /// <summary>
    /// Records a passed of <paramref name="arm"/> on <paramref name="leg"/> that nothing this machine reads has moved
    /// for: what it was judged on read as the one reading judges it, its key of those parts.
    /// </summary>
    private static async Task SweepRecordedAsync(HarnessFactory harness, string repository, string leg, string arm)
    {
        var context = await harness.ContextLoader.LoadAsync(repository, Token);
        var declared = MutationRegistryParser.Parse(
            MutationRegistryParser.Lines(File.ReadAllText(Path.Combine(repository, "arms.md"))),
            texts: null).Registry.Find(arm)
            ?? throw new InvalidOperationException($"no A row declares '{arm}'");

        var on = new JudgedOn
        {
            Judge = ArmJudge.Revision,
            Rows = JudgedOn.RowsOf(declared),
            Texts = JudgedOn.TextsOf(declared).ToDictionary(
                path => path,
                path => (string?)FileContentHash.Of(File.ReadAllBytes(Path.Combine(repository, path.Replace('/', Path.DirectorySeparatorChar)))),
                StringComparer.Ordinal),
            Sites = declared.Sites.ToDictionary(
                site => site.Site,
                site => (string?)FileContentHash.Of(File.ReadAllBytes(Path.Combine(repository, site.Site.Replace('/', Path.DirectorySeparatorChar)))),
                StringComparer.Ordinal),
            Built = new Dictionary<string, string>(StringComparer.Ordinal),
            Compilers = [],
            Settings = SweepSettings.Of(context.Config, context.Config.Legs[leg], context.Config.Hosts.SettingsFor("local")),
            RunReads = new Dictionary<string, string>(StringComparer.Ordinal),
            Host = "local",
        };

        _ = new ArmVerdictRecord(harness.FileSystem, context.Layout).Merge(
            leg,
            arm,
            new RecordedVerdict
            {
                Verdict = "passed",
                Detail = "it was passed",
                Run = "20261012-145059-ba433418",
                At = new DateTimeOffset(2026, 10, 12, 14, 58, 11, TimeSpan.Zero),
                SweptIn = RepositoryTree.MainCheckout,
                Key = on.Key(),
                JudgedOn = on,
            },
            []);
    }
}
