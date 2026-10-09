using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using RepoHarness.Core.Build;
using RepoHarness.Core.Configuration;
using RepoHarness.Core.Execution;
using RepoHarness.Core.FileSystem;
using RepoHarness.Core.Git;
using RepoHarness.Core.Hosts;
using RepoHarness.Core.Legs;
using RepoHarness.Core.Platform;
using RepoHarness.Core.Processes;
using RepoHarness.Core.Repository;
using RepoHarness.Core.Results;
using RepoHarness.Core.Runs;
using RepoHarness.Core.Tools;

namespace RepoHarness.Tests;

/// <summary>
/// End-to-end coverage of the built CLI: parsing, wiring and exit codes.
/// </summary>
public sealed partial class CliEndToEndTests
{
    [Fact]
    public async Task Help_ListsEveryCommand()
    {
        var result = await CliRunner.RunAsync(["--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);

        string[] commands =
        [
            "init", "verify-git", "create-worktree", "delete-worktree", "list-worktree",
            "create-orchestrator", "delete-orchestrator", "list-orchestrator",
            "create-agent", "seed-agent", "refresh-agent", "rebase-agent", "fold-agent", "delete-agent",
            "check-root-litter", "check-anchor-citations", "fix-line-endings", "check-ci-legs",
            "legs", "install-missing-tools", "sync", "build", "test", "run", "host-exec", "help",
        ];

        foreach (var command in commands)
        {
            Assert.Contains(command, result.StandardOutput, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// <c>init</c> and <c>install-missing-tools</c> share one provisioning engine, and either of them
    /// can stop to ask for a superuser password. The flag that refuses to ask therefore has to reach
    /// both: declared on one command alone, the other would turn it away as an option it does not know.
    /// </summary>
    [Theory]
    [InlineData("init")]
    [InlineData("install-missing-tools")]
    public async Task NoPrompt_IsOfferedByEveryCommandThatCanProvision(string command)
    {
        var result = await CliRunner.RunAsync([command, "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("--no-prompt", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// fold-agent and delete-agent read --new and --accept-lost as often as they are given - the second one here as well as
    /// the first - and refuse, as usage errors before any repository is looked for, an id no anchor could have, a cell that
    /// is not ID:cell, and either beside --discard-uncommitted.
    /// </summary>
    [Theory]
    [InlineData(new[] { "fold-agent", "o1", "ag", "--accept-lost", "D-X-Y-Z:status" }, "--accept-lost 'D-X-Y-Z:status' is not <ID>:<cell>")]
    [InlineData(new[] { "delete-agent", "o1", "ag", "--new", "D-X-Y-Z", "--new", "D-X Y" }, "--new 'D-X Y' is not an anchor id")]
    [InlineData(new[] { "delete-agent", "o1", "ag", "--accept-lost", "D-X-Y-Z:closing", "--discard-uncommitted" }, "--accept-lost lets a fold through what it otherwise refuses, and --discard-uncommitted folds nothing")]
    public async Task WhatAFoldIsLetThrough_IsReadEachTimeGiven_AndRefusedAsAUsageErrorWhereItCannotBeOne(string[] arguments, string refusal)
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(arguments, TestContext.Current.CancellationToken, workingDirectory: temp.Path);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains(refusal, result.StandardOutput + result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// One sync reaches every host, and taking a directory over deletes what the source does not
    /// have. Saying which machine that applies to is the whole point, so the flag cannot be a bare
    /// yes that a reader could take to mean "this one" while it means "all of them".
    /// </summary>
    [Fact]
    public async Task Adopt_RefusesToBeABareYes_AndMustNameItsHosts()
    {
        // In a directory of its own, deliberately. Without one the real command line runs with this
        // process's working directory, which is inside this repository: were the option's arity ever
        // to allow none, this test would sync against whatever hosts this tree declares.
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(
            ["sync", "--adopt"],
            TestContext.Current.CancellationToken,
            workingDirectory: temp.Path);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("--adopt", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("argument", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Version_IsReportedWithoutBuildMetadata()
    {
        // The release pipelines compare this with the version they published. The SDK
        // appends the commit by default, and then the two would never match.
        var result = await CliRunner.RunAsync(["--version"], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Matches(BareVersionPattern(), result.TrimmedOutput);
    }

    [Fact]
    public async Task AnUnknownCommand_IsAUsageError_ReportedOnStandardError()
    {
        var result = await CliRunner.RunAsync(["definitely-not-a-command"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.NotEmpty(result.StandardError.Trim());
    }

    [Fact]
    public async Task AMissingDirectory_IsAUsageError()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(
            ["list-worktree", "-C", temp.Combine("absent")],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
        Assert.Contains("does not exist", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARelativeDirectory_IsResolvedAgainstTheWorkingDirectory()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = Directory.CreateDirectory(temp.Combine("repo")).FullName;
        await new HarnessFactory().InitializeGitRepositoryAsync(repository, cancellationToken);

        var result = await CliRunner.RunAsync(["verify-git", "-C", "repo"], cancellationToken, workingDirectory: temp.Path);

        Assert.Equal((int)VerifyGitStatus.Success, result.ExitCode);
        Assert.Contains(repository, result.StandardOutput, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task VerifyGit_ReturnsNotARepository_OutsideOne()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal((int)VerifyGitStatus.NotAGitRepository, result.ExitCode);
    }

    [Fact]
    public async Task VerifyGit_Succeeds_InsideARepository()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal((int)VerifyGitStatus.Success, result.ExitCode);
        Assert.Contains("verify-git: OK", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VerifyGit_UsesTheWorkingDirectory_WhenNoDirectoryIsGiven()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git"], TestContext.Current.CancellationToken, workingDirectory: temp.Path);

        Assert.Equal((int)VerifyGitStatus.NotAGitRepository, result.ExitCode);
    }

    [Fact]
    public async Task Init_CreatesTheLayout_AndIsIdempotent()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, cancellationToken);

        var first = await CliRunner.RunAsync(["init", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, first.ExitCode);
        Assert.True(File.Exists(HarnessFactory.ConfigPath(temp.Path)));

        var second = await CliRunner.RunAsync(["init", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, second.ExitCode);
        Assert.Contains("already present", second.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_Refuses_OutsideAGitRepository()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["init", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.False(Directory.Exists(temp.Combine(".harness-config")));
    }

    [Fact]
    public async Task Failures_AreReportedOnStandardError_SoOutputStaysPipeable()
    {
        using var temp = new TempDirectory();

        var result = await CliRunner.RunAsync(["verify-git", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Contains("FAIL", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("FAIL", result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Worktrees_CanBeCreatedListedAndDeleted()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);
        Assert.Contains("created worktree 'wt'", created.StandardOutput, StringComparison.Ordinal);
        Assert.True(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));

        var listed = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, listed.ExitCode);
        Assert.Contains("list-worktree: wt", listed.StandardOutput, StringComparison.Ordinal);

        var deleted = await CliRunner.RunAsync(["delete-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));

        var empty = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], cancellationToken);
        Assert.Contains("no worktrees", empty.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CreateWorktree_WithRandom_ReportsTheNameItChose()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["create-worktree", "--random", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        var match = CreatedNamePattern().Match(result.StandardOutput);
        Assert.True(match.Success, result.StandardOutput);
        Assert.True(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, match.Groups["name"].Value)));
    }

    [Fact]
    public async Task CreateWorktree_WithNeitherANameNorRandom_IsAUsageError()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["create-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task CreateWorktree_WithANameAndRandom_IsAUsageError_EvenBeforeInit()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(
            ["create-worktree", "wt", "--random", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);
    }

    [Fact]
    public async Task ListWorktree_ReportsNotInitialised_BeforeInit()
    {
        using var temp = new TempDirectory();
        await new HarnessFactory().InitializeGitRepositoryAsync(temp.Path, TestContext.Current.CancellationToken);

        var result = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.NotInitialized, result.ExitCode);
    }

    [Theory]
    [InlineData("{ this is not json")]
    [InlineData("""{ "unknownSetting": 1 }""")]
    [InlineData("""{ "defaults": { "buildCores": 0 } }""")]
    public async Task AnInvalidConfiguration_IsReportedAsSuch(string json)
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        File.WriteAllText(HarnessFactory.ConfigPath(temp.Path), json);

        var result = await CliRunner.RunAsync(["list-worktree", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, result.ExitCode);
        Assert.Contains("config.json", result.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteWorktree_OfAWorktreeThatDoesNotExist_IsRefused()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var result = await CliRunner.RunAsync(["delete-worktree", "missing", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
    }

    [Fact]
    public async Task DeleteWorktree_WithUncommittedWork_IsRefused_UntilForced()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "wt");

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var refused = await CliRunner.RunAsync(["delete-worktree", "wt", "-C", temp.Path], cancellationToken);

        // One line on standard error, naming what would be lost and the way past.
        Assert.Equal(HarnessExit.Refused, refused.ExitCode);
        Assert.Equal(
            "delete-worktree: FAIL - Worktree 'wt' was not deleted, because it has 1 uncommitted change(s) that would be lost: notes.txt (commit them to a branch, run 'git stash -u', or pass --discard-uncommitted to delete them with the worktree); fix that, or pass --force to delete it anyway.",
            Assert.Single(refused.StandardError.ReplaceLineEndings("\n").Trim().Split('\n')));
        Assert.True(File.Exists(Path.Combine(path, "notes.txt")), "The refused delete removed uncommitted work.");

        var forced = await CliRunner.RunAsync(["delete-worktree", "wt", "--force", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, forced.ExitCode);
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public async Task DeleteWorktree_WithDiscardUncommitted_DeletesUncommittedWork_AndSaysWhatItDiscarded()
    {
        using var temp = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);
        var path = HarnessFactory.WorktreePath(temp.Path, "wt");

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);
        File.WriteAllText(Path.Combine(path, "notes.txt"), "never committed");

        var deleted = await CliRunner.RunAsync(["delete-worktree", "wt", "--discard-uncommitted", "-C", temp.Path], cancellationToken);

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.Contains(
            "delete-worktree: discarded 1 uncommitted change(s): notes.txt",
            deleted.StandardOutput.ReplaceLineEndings("\n").Split('\n'));
        Assert.False(Directory.Exists(path));
    }

    /// <summary>
    /// A caller whose environment names another repository through any variable git reads one from:
    /// with GIT_COMMON_DIR pointing elsewhere, the deletion was refused and the worktree left registered,
    /// because only three of git's fifteen such variables were cleared. Malformed -c settings travel the
    /// same way and are cleared with them.
    /// </summary>
    [Fact]
    public async Task DeleteWorktree_IsNotSteered_ByTheCallersRepositoryVariables()
    {
        using var temp = new TempDirectory();
        using var other = new TempDirectory();
        var cancellationToken = TestContext.Current.CancellationToken;
        await PrepareRepositoryAsync(temp);
        await new HarnessFactory().InitializeGitRepositoryAsync(other.Path, cancellationToken);
        var otherGit = Path.Combine(other.Path, ".git");

        var created = await CliRunner.RunAsync(["create-worktree", "wt", "-C", temp.Path], cancellationToken);
        Assert.Equal(HarnessExit.Success, created.ExitCode);

        var deleted = await CliRunner.RunAsync(
            ["delete-worktree", "wt", "-C", temp.Path],
            cancellationToken,
            environment: new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GIT_DIR"] = otherGit,
                ["GIT_WORK_TREE"] = other.Path,
                ["GIT_INDEX_FILE"] = Path.Combine(otherGit, "index"),
                ["GIT_COMMON_DIR"] = otherGit,
                ["GIT_OBJECT_DIRECTORY"] = Path.Combine(otherGit, "objects"),
                ["GIT_CONFIG_PARAMETERS"] = "'not a setting",
                ["GIT_CONFIG_COUNT"] = "not a number",
            });

        Assert.Equal(HarnessExit.Success, deleted.ExitCode);
        Assert.False(Directory.Exists(HarnessFactory.WorktreePath(temp.Path, "wt")));
        var listed = await new HarnessFactory().GitClient.ListWorktreesAsync(temp.Path, cancellationToken);
        Assert.Single(listed);
    }

    /// <summary>delete-agent with --discard-uncommitted and no --apply is a dry run: the command line abandons and removes nothing.</summary>
    [Fact]
    public async Task DeleteAgentWithoutApply_IsADryRun_EvenAbandoning()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        var worktree = await kit.CreateAgentAsync("ag");

        var result = await CliRunner.RunAsync(["delete-agent", "o1", "ag", "--discard-uncommitted", "-C", kit.Main], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("dry run", result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal(Core.Orchestration.AgentStates.Live, kit.Record("ag").State);
        Assert.True(Directory.Exists(worktree));
    }

    /// <summary>list-orchestrator --json prints its one JSON document on standard output, and nothing else.</summary>
    [Fact]
    public async Task ListOrchestratorJson_PrintsOneJsonDocumentAndNothingElse()
    {
        using var temp = new TempDirectory();
        var kit = await OrchestrationKit.PrepareAsync(temp);
        await kit.CreateAgentAsync("ag");

        var result = await CliRunner.RunAsync(["list-orchestrator", "--json", "-C", kit.Main], TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var document = System.Text.Json.Nodes.JsonNode.Parse(result.StandardOutput)!;
        Assert.Equal("o1", (string?)document["orchestrators"]![0]!["name"]);
        Assert.Equal("ag", (string?)document["orchestrators"]![0]!["agents"]![0]!["name"]);
    }

    /// <summary>
    /// The defect that rode the layout change: a configuration one verb called valid and another
    /// refused. Both verbs read the same file through the same reader, so a spelling wrong enough
    /// for one is wrong for the other, and the run that finds out is never the first to say so.
    /// </summary>
    [Theory]
    [InlineData("corpus.yml")]
    [InlineData("../outside.yml")]
    [InlineData("a/../../outside.yml")]
    [InlineData("corpus/steps.yml")]
    [InlineData("corpus/nested/corpus.yml")]
    public async Task LegsAndRun_RefuseTheSameBadActionPath_WithTheSameExitCode(string action)
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        WriteRunner(temp, action);

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], TestContext.Current.CancellationToken);
        var run = await CliRunner.RunAsync(["run", "corpus", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, legs.ExitCode);
        Assert.Equal(legs.ExitCode, run.ExitCode);
    }

    /// <summary>
    /// The spelling is legal, so the configuration reader passes it; only the file system knows the
    /// action is not there. Both verbs still answer the same way, because both resolve the path
    /// before a leg is placed rather than leaving it to whichever one happened to open the file.
    /// </summary>
    [Fact]
    public async Task LegsAndRun_RefuseAnActionThatIsNotThere_WithTheSameExitCode()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);
        WriteRunner(temp, "corpus/corpus.yml");

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], TestContext.Current.CancellationToken);
        var run = await CliRunner.RunAsync(["run", "corpus", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.ConfigInvalid, legs.ExitCode);
        Assert.Equal(legs.ExitCode, run.ExitCode);
        Assert.Contains("corpus", legs.StandardError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Init_ScaffoldsTheActionsDirectory_WithAPlaceholderGitTracks()
    {
        using var temp = new TempDirectory();
        await PrepareRepositoryAsync(temp);

        var placeholder = Path.Combine(
            temp.Path,
            ".harness-config",
            "runner",
            "actions",
            ".gitkeep");

        Assert.True(File.Exists(placeholder), $"init left no placeholder at '{placeholder}'.");
    }

    [Theory]
    [InlineData("runners")]
    [InlineData("layout")]
    public async Task Help_PrintsTheDirectoryPerActionLayout(string topic)
    {
        var result = await CliRunner.RunAsync(["help", topic], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("actions/<name>/<name>.yml", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>Declares one runner naming <paramref name="action"/>, over the seeded config.</summary>
    private static void WriteRunner(TempDirectory temp, string action)
        => File.WriteAllText(
            HarnessFactory.ConfigPath(temp.Path),
            $$"""
            {
              "predefinedRunners": { "corpus": { "action": {{System.Text.Json.JsonSerializer.Serialize(action)}} } }
            }
            """);

    /// <summary>
    /// The line a migration's acceptance gate reads. A leg that reached no verdict used to be
    /// counted among the legs that passed, so a run where nothing ran at all printed
    /// "OK - n leg(s) passed" and exited 0 — which is the one number a gate compares.
    /// </summary>
    [Fact]
    public async Task ARunWhereALegReachedNoVerdict_IsNotReportedAsPassed()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, result.ExitCode);
        Assert.DoesNotContain("OK -", result.StandardOutput, StringComparison.Ordinal);

        // Both halves are named: how many did report, and which ones did not.
        Assert.Contains("1 of 2 leg(s) passed", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("elsewhere", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A manual step through the real parser: a plain run leaves it out and lists it; --manual-step runs it
    /// alone, reading an input only it declares; a runner naming it runs it as its own; and a step name the
    /// action lacks, an input of a step the run leaves out, and a named step that runs on none of the
    /// run's legs are each refused before anything runs.
    /// </summary>
    [Fact]
    public async Task AManualStep_RunsOnlyWhereARunNamesIt()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" } },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig { Action = "probe/probe.yml" },
                ["bench"] = new RunnerConfig { Action = "probe/probe.yml", Steps = ["bench"] },
            },
        });

        // A system this machine is not, for a step that runs only there.
        var elsewhere = platform.PlatformKey == PlatformNames.Linux ? PlatformNames.Windows : PlatformNames.Linux;

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            $$"""
            name: probe
            steps:
              - name: version
                run: dotnet --version
              - name: bench
                manual: true
                inputs:
                  runs:
                    default: '1'
                successPattern: '^\d+\.\d+'
                run: dotnet --version
              - name: elsewhere
                manual: true
                runOn: [{{elsewhere}}]
                needs: [version]
                successPattern: '^\d+\.\d+'
                run: dotnet --version
            """);

        static JsonElement Leg(ProcessResult result)
            => JsonDocument.Parse(result.StandardOutput).RootElement.GetProperty("legs")[0].Clone();

        static IEnumerable<string?> Names(JsonElement leg, string property)
            => leg.TryGetProperty(property, out var names) ? names.EnumerateArray().Select(name => name.GetString()) : [];

        var plain = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, plain.ExitCode);
        Assert.Equal(["version"], Names(Leg(plain), "ranSteps"));
        Assert.Equal(["bench", "elsewhere"], Names(Leg(plain), "unselectedSteps"));
        Assert.Empty(Names(Leg(plain), "manualSteps"));

        var named = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--manual-step", "bench", "--input", "runs=2", "--json", "-C", temp.Path],
            token);

        Assert.Equal(HarnessExit.Success, named.ExitCode);
        Assert.Equal(["bench"], Names(Leg(named), "ranSteps"));
        Assert.Equal(["bench"], Names(Leg(named), "manualSteps"));
        Assert.Equal(["version", "elsewhere"], Names(Leg(named), "unselectedSteps"));

        var ownRunner = await CliRunner.RunAsync(["run", "bench", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, ownRunner.ExitCode);
        Assert.Equal(["bench"], Names(Leg(ownRunner), "manualSteps"));

        var mistyped = await CliRunner.RunAsync(["run", "probe", "--manual-step", "bnech", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.UsageError, mistyped.ExitCode);
        Assert.Contains("its manual steps are bench", mistyped.StandardError, StringComparison.Ordinal);

        var unread = await CliRunner.RunAsync(["run", "probe", "--input", "runs=2", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.UsageError, unread.ExitCode);
        Assert.Contains("which only step(s) 'bench' reads", unread.StandardError, StringComparison.Ordinal);

        // Named for a system this leg is not, it would leave the leg only what it needs - and a pass.
        // Refused before a run begins: no run directory is made for it.
        var runs = temp.Combine(".harness-config", "runs");
        var begun = Directory.GetDirectories(runs).Length;

        var nowhere = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--manual-step", "elsewhere", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Refused, nowhere.ExitCode);
        Assert.Contains("runs none of the steps this run names", nowhere.StandardError, StringComparison.Ordinal);
        Assert.Equal(begun, Directory.GetDirectories(runs).Length);
    }

    /// <summary>
    /// A run check whose runner's steps cannot be read leaves the run to go on, its legs counted heavy and not built for
    /// it, and says so as the run begins: refused here, a run was refused for a check that may never run, and the check
    /// that runs it refuses it as it always did; taken as light, its legs could build with no slot at all.
    /// </summary>
    [Fact]
    public async Task ARunCheckWhoseStepsCannotBeRead_CountsHeavy_SayingSo_AndTheRunGoesOn()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" } },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig
                {
                    Action = "probe/probe.yml",
                    ExpectedExceptions =
                    [
                        ExpectedExceptionMatcherTests.Entry(
                            messages: ["the device was busy"],
                            runChecks: [new RunCheck { PredefinedRunner = "confirm", Expects = new RunCheckExpectation { Success = true } }]),
                    ],
                },
                ["confirm"] = new RunnerConfig { Action = "confirm/confirm.yml" },
            },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            """
            name: probe
            steps:
              - name: version
                run: dotnet --version
            """);

        var run = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, run.ExitCode);
        Assert.Contains(
            "run check 'confirm' of runner 'probe' could not be read to know how heavy it is or whether it needs the build, "
            + "so the legs of 'probe' count as heavy, and are not built for it: ",
            run.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Through 'run', the runner the command line names is offered --input for an input with no value
    /// that its step names: the command line reaches that runner, and only that one.
    /// </summary>
    [Fact]
    public async Task ARunNamingAnInputWithNoValue_IsOfferedTheCommandLine()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await PrepareInputWithNoValueAsync(temp, token);

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "-C", temp.Path], token);

        Assert.NotEqual(HarnessExit.Success, result.ExitCode);
        Assert.Contains(
            "'stage' run line names '{area}', an input of the action, which has no value: give it one with --input area=<value>",
            result.StandardOutput + result.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A run refused inside its leg before any leg reached a verdict prints no table: the heading alone,
    /// above the FAIL line saying why, read as a table whose rows had gone missing. Where its records
    /// are is still said.
    /// </summary>
    [Fact]
    public async Task ARunStoppedBeforeAnyLegReachedAVerdict_PrintsNoTable_OnlyWhyItStopped()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        await PrepareInputWithNoValueAsync(temp, token);

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "-C", temp.Path], token);
        var output = result.StandardOutput + result.StandardError;

        Assert.NotEqual(HarnessExit.Success, result.ExitCode);
        Assert.Contains("run: FAIL - ", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("which has no value: give it one with --input area=<value>", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("logs: ", output, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerReport.Headings[1], output, StringComparison.Ordinal);
        Assert.DoesNotContain(LedgerReport.Headings[2], output, StringComparison.Ordinal);
    }

    /// <summary>
    /// A heavy runner's leg, through the real binary, on a machine that declares admission, takes a slot in the record
    /// kept among the user's own data - named by what tells this machine apart - and gives it back as its work ends;
    /// its line names the record, as every command names where what it keeps lands.
    /// </summary>
    [Fact]
    public async Task AHeavyRunnersLeg_IsAdmittedAgainstThisUsersOwnRecord()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (environment, holds) = OwnUserData(temp);
        await PrepareRunnerAsync(temp, heavy: true);

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token, environment: environment);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var admission = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("admission");
        var record = Path.Combine(Path.GetDirectoryName(holds)!, HeavyLegSlots.FileNameFor(new HostPlatform().MachineId.Id));

        Assert.True(admission.GetProperty("admitted").GetBoolean());
        Assert.Equal(record, admission.GetProperty("record").GetString());
        Assert.Equal("[]", File.ReadAllText(record).Trim());
    }

    [Fact]
    public async Task ARunWhereEveryLegReported_IsStillReportedAsPassed()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains("1 leg(s) passed", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run that did not succeed says why on its FAIL line whether or not --json was asked for.
    /// The JSON branch composed no message at all, so it printed "FAIL - " and nothing after the
    /// dash - and a host is always asked for JSON, so every failure on another machine read that
    /// way on the machine that sent it.
    /// </summary>
    [Fact]
    public async Task ARunThatDidNotSucceed_SaysWhyOnItsFailLine_WithOrWithoutJson()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);
        var token = TestContext.Current.CancellationToken;

        var table = await CliRunner.RunAsync(["run", "probe", "--legs", "native,elsewhere", "-C", temp.Path], token);
        var json = await CliRunner.RunAsync(["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path], token);

        const string Expected = "run: FAIL - 1 of 2 leg(s) passed; 1 did no work: elsewhere";

        Assert.Equal(HarnessExit.Incomplete, table.ExitCode);
        Assert.Equal(HarnessExit.Incomplete, json.ExitCode);
        Assert.Contains(Expected, table.StandardError, StringComparison.Ordinal);
        Assert.Contains(Expected, json.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The description names where worktrees go by default and that configuration may move them,
    /// rather than stating the default as the only place: a repository that sets worktrees.root
    /// was told, by the command it runs, somewhere its worktrees are not.
    /// </summary>
    [Fact]
    public async Task CreateWorktree_DescribesTheRootAsTheDefault_NotAsTheOnlyPlace()
    {
        var result = await CliRunner.RunAsync(["create-worktree", "--help"], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains(WorktreeSettings.DefaultRoot, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains(WorktreeSettings.SeededRoot, result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("worktrees.root", result.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// B2's whole point is that a green nobody earned must not be reported, and the JSON ledger is
    /// the machine-readable half of that — the half a gate actually parses.
    /// </summary>
    [Fact]
    public async Task TheJsonLedgerOfARunWithAnUnreportedLeg_SaysItIsNotComplete()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Incomplete, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        // The document and the process agree, and both say the same thing the table said.
        Assert.Equal(HarnessExit.Incomplete, root.GetProperty("exitCode").GetInt32());
        Assert.True(root.GetProperty("passed").GetBoolean(), "nothing failed, so this is not a red run");
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.False(root.GetProperty("cancelled").GetBoolean());

        var legs = root.GetProperty("legs").EnumerateArray().Select(leg => leg.GetProperty("leg").GetString()).ToList();
        Assert.Contains("native", legs);
        Assert.Contains("elsewhere", legs);

        // The run is named as its records are kept: the line it starts with, and the directory it wrote.
        var runId = root.GetProperty("runId").GetString()!;

        Assert.Equal(runId, Path.GetFileName(root.GetProperty("runDirectory").GetString()));
        Assert.StartsWith($"run: run {runId}", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// Asked for data, a run is answered with data whatever ended it. A selection no host can take
    /// answered with text, which the machine that dispatched the leg read as a host whose answer
    /// could not be read - where the host had said exactly why each leg could not run. The run is
    /// named all the same, in its first line and as the document's runId, though it keeps no records:
    /// that id is all there is to cite it by.
    /// </summary>
    [Fact]
    public async Task ARunNoHostCanTake_AnswersWithTheLedger_NamingEachLegsVerdict()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(LegsExit.Unavailable, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(LegsExit.Unavailable, root.GetProperty("exitCode").GetInt32());
        Assert.Equal("no selected leg can run", root.GetProperty("summary").GetString());
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());

        var leg = Assert.Single(root.GetProperty("legs").EnumerateArray());
        Assert.Equal("elsewhere", leg.GetProperty("leg").GetString());
        Assert.Equal("skipped-unavailable", leg.GetProperty("verdict").GetString());

        var runId = root.GetProperty("runId").GetString()!;

        Assert.Matches("^[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$", runId);
        Assert.StartsWith($"run: run {runId}", result.StandardError, StringComparison.Ordinal);
        Assert.False(root.TryGetProperty("runDirectory", out _), "a run refused before it had a directory names none");
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", runId)));
    }

    /// <summary>
    /// A run refused before any leg had a line - a leg nobody declared - is named too, in its first
    /// line and as the document's runId, and keeps no records.
    /// </summary>
    [Fact]
    public async Task ARunRefusedBeforeAnyLegHadALine_IsNamedAllTheSame()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "nobody-declared-this", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.NotEqual(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;
        var runId = root.GetProperty("runId").GetString()!;

        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.StartsWith($"run: run {runId}", result.StandardError, StringComparison.Ordinal);
        Assert.Contains("run: FAIL - ", result.StandardError, StringComparison.Ordinal);
        Assert.False(Directory.Exists(temp.Combine(".harness-config", "runs", runId)));
    }

    /// <summary>
    /// A hold asked of the DssHarness on a host, served by the real binary as a host agent serves it, goes on once
    /// the agent has answered and ended - a process of its own - holding the machine with its keepAwake, given that
    /// process; and it ends when it is ended, as a command's own keepAwake ends it there, taking its keepAwake with it.
    /// </summary>
    [Fact]
    public async Task AHold_OutlivesTheAgentThatStartedIt_AndEndsWhenItIsEnded()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var watched = temp.Combine("watched.txt");
        var (environment, holds) = OwnUserData(temp);

        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Hold,
                HoldAwakeSeconds = 60,
                KeepAwake = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, watched, "{pid}"],
                KeepAwakeEnvironment = new() { [TestHost.ChildModeVariable] = "watch-process" },
                Nonce = "0123456789abcdef0123456789abcdef",
            },
            HostAgentProtocol.JsonOptions);

        var served = await CliRunner.RunAsync(["host-agent"], token, standardInput: request + "\n", environment: environment);

        Assert.Equal(HarnessExit.Success, served.ExitCode);

        // The agent has answered and ended; the hold it started holds the machine on. Waited for as a whole line: the
        // keepAwake writes the file while this reads it, and a read caught part way saw it held, or half written.
        string? started = null;

        await EventuallyAsync(
            () => (started = Written(watched)) is { } text && text.StartsWith("started ", StringComparison.Ordinal) && text.EndsWith('\n'),
            token);

        var holder = int.Parse(started!["started ".Length..].Trim(), CultureInfo.InvariantCulture);
        Assert.NotEqual(Environment.ProcessId, holder);

        await Task.Delay(TimeSpan.FromSeconds(1), token);
        Assert.True(Running(holder), "the hold ended before anything ended it");

        Assert.True(File.Exists(holds), "the hold was not kept in the user's data the CLI was given");
        new HoldAwakeStore(new PhysicalFileSystem(FilePermissionsFactory.Create()), holds).End();

        await EventuallyAsync(() => !Running(holder), token);

        static bool Running(int id)
        {
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(id);
                return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        // What another process has written to the file so far, read as its writer allows; nothing where it is not there
        // yet, or is held as it is written.
        static string? Written(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A command the real binary runs for another machine, as its host agent runs one, writes the host's home as
    /// <c>~</c> in its failure line and in the ledger it answers with, which the machine that asked puts in its
    /// own output; the same command typed there names the directory in full.
    /// </summary>
    [Fact]
    public async Task ACommandAHostRunsForAnotherMachine_WritesItsHomeAsTilde_AndTypedThereNamesItInFull()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (environment, _) = OwnUserData(temp);

        // The home the CLI reads: its HOME, and on Windows the account's own folder, which no environment moves -
        // there the test's directories are under it only where the system keeps temporary files there.
        var home = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : environment["HOME"]!;
        var work = OperatingSystem.IsWindows() ? temp.Combine("work") : Path.Combine(home, "work");
        Directory.CreateDirectory(work);

        Assert.SkipUnless(
            PathContainment.IsStrictlyInside(home, work, StringComparison.OrdinalIgnoreCase),
            $"this machine keeps temporary files outside its account's home, '{home}', which the CLI reads whatever its environment says");

        const string Nonce = "0123456789abcdef0123456789abcdef";
        var shown = "~" + work[home.Length..];
        var request = JsonSerializer.Serialize(
            new HostAgentRequest { Kind = HostAgentRequestKind.Run, Directory = work, Arguments = ["test", "--json"], Nonce = Nonce },
            HostAgentProtocol.JsonOptions);

        var served = await CliRunner.RunAsync(["host-agent"], token, standardInput: request + "\n", environment: environment);
        var typed = await CliRunner.RunAsync(["test", "--json"], token, workingDirectory: work, environment: environment);

        using var answer = JsonDocument.Parse(HostAgentProtocol.SinceServing(served.StandardOutput, Nonce));
        var said = $"'{shown}' is not inside a git repository.";

        Assert.Equal(HarnessExit.Refused, served.ExitCode);
        Assert.Equal(said, answer.RootElement.GetProperty("summary").GetString());
        Assert.Contains($"test: FAIL - {said}", served.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(work, served.StandardError, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HarnessExit.Refused, typed.ExitCode);
        Assert.Contains("work' is not inside a git repository.", typed.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("'~", typed.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A defect on a host answering another machine is reported with the host's home as <c>~</c> - its failure
    /// line, and under <c>--verbose</c> its stack trace, which repeats the message and reaches the reason of
    /// every leg the machine that asked reports.
    /// </summary>
    [Fact]
    public async Task ADefectOnAHostAnsweringAnotherMachine_IsReportedWithItsHomeAsTilde_TraceIncluded()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var (environment, _) = OwnUserData(temp);
        var platform = new HostPlatform();

        var home = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) : environment["HOME"]!;
        var named = OperatingSystem.IsWindows() ? temp.Combine("named") : Path.Combine(home, "named");

        Assert.SkipUnless(
            PathContainment.IsStrictlyInside(home, named, StringComparison.OrdinalIgnoreCase),
            $"this machine keeps temporary files outside its account's home, '{home}', which the CLI reads whatever its environment says");

        // A witness pattern config.json would refuse, which nothing on a host anticipates: it ends in the
        // handler for defects, and the message the regular expression engine gives quotes the pattern.
        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Info,
                Emulators = new Dictionary<string, EmulatorConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["broken"] = new EmulatorConfig
                    {
                        HostOs = platform.PlatformKey,
                        HostProcessor = platform.Processor,
                        Processor = "riscv64",
                        Launcher = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath],
                        Env = { [TestHost.ChildModeVariable] = "echo-args" },
                        Witness = new EmulatorWitness { Command = ["riscv64"], Pattern = "(" + named },
                    },
                },
            },
            HostAgentProtocol.JsonOptions);

        var served = await CliRunner.RunAsync(
            [HostAgentProtocol.CommandName, HostAgentProtocol.VerboseOption],
            token,
            standardInput: request + "\n",
            environment: environment);

        Assert.Equal(HarnessExit.InternalError, served.ExitCode);
        Assert.StartsWith($"{HostAgentProtocol.CommandName}: FAIL - Unexpected ", served.StandardError, StringComparison.Ordinal);
        Assert.Contains("   at ", served.StandardError, StringComparison.Ordinal);
        Assert.Contains("(~" + named[home.Length..], served.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain(named, served.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A leg the real binary runs for another machine, as its host agent runs one, asks the host's heavy-leg slots under
    /// the run the request says it is a leg of, beside its command: behind a slot only that run's legs hold, its wait is
    /// its own command's, said so and never counted - here against a wait of about a second the host allows, which it
    /// outlasts - and it starts once that leg gives its slot back.
    /// </summary>
    [Fact]
    public async Task ALegAHostRunsForAnotherMachine_AsksItsSlotsUnderTheRunThatSentIt_SoAWaitBehindThatRunsLegsIsNotCounted()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var (environment, holds) = OwnUserData(temp);

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            SshItems = { "pi" },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig
                    {
                        RepositoryPath = "~/repo",

                        // One slot, looked at every second, and a limit of 100% of the memory, which any reading short of a
                        // full machine is below.
                        Admission = new AdmissionSettings
                        {
                            HeavyLegs = 1,
                            MaxMemoryPercent = 100,
                            SettleSeconds = [0, 0],
                            PollSeconds = 1,
                            MaxWaitMinutes = 0.02,
                            MinFreeGiB = 0,
                        },
                    },
                },
            },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs =
            {
                ["sent-here"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
        });

        // The slot held by another leg of the run that sends this one, in the record the CLI keeps in the user's data it
        // was given: this process's, which lives while the leg waits.
        var record = Path.Combine(Path.GetDirectoryName(holds)!, HeavyLegSlots.FileNameFor(platform.MachineId.Id));
        var run = RunId.New();
        using var held = AdmissionKit.Slots(harness, record).Ask(run.Value, "test", "another", "ssh pi", "~/repo", "debug", 1);

        const string Nonce = "0123456789abcdef0123456789abcdef";
        var request = JsonSerializer.Serialize(
            new HostAgentRequest
            {
                Kind = HostAgentRequestKind.Run,
                Directory = temp.Path,
                Arguments = ["test", "--no-build", "--legs", "sent-here", "--json", RemoteLegRunner.HereOption, "ssh pi"],
                Nonce = Nonce,
                RunId = run.Value,
            },
            HostAgentProtocol.JsonOptions);

        var serving = CliRunner.RunAsync(["host-agent"], token, standardInput: request + "\n", environment: environment);

        // In line behind the slot, and given a few of its looks there - each past the second it may wait - before the slot
        // is given back.
        await EventuallyAsync(() => Read(record)?.Contains("\"sent-here\"", StringComparison.Ordinal) == true, token);
        await Task.Delay(TimeSpan.FromSeconds(3), token);
        held.Dispose();

        var served = await serving;

        Assert.Equal(HarnessExit.Success, served.ExitCode);
        Assert.Contains(
            "sent-here: waits for one of this machine's 1 heavy-leg slot(s), 1 leg(s) ahead, each its own command's, which does not "
            + "count against the",
            served.StandardError,
            StringComparison.Ordinal);

        using var answer = JsonDocument.Parse(HostAgentProtocol.SinceServing(served.StandardOutput, Nonce));
        Assert.Equal("passed", Assert.Single(answer.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("verdict").GetString());

        // What the record holds as the CLI writes it; nothing where it is not there yet, or is held as it is written.
        static string? Read(string path)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException)
            {
                return null;
            }
        }
    }

    /// <summary>
    /// A shared tool working in a build directory of another tree of the repository on this machine - here a worktree's -
    /// is named as that tree's leg's while a leg's tests run, and while a runner's step does, through the real binary: the
    /// trees a leg's work began among reach its tests and its runner as they reach its build.
    /// </summary>
    [Fact]
    public async Task ASharedToolInAnotherTreesBuildDirectory_IsNamedAsThatTreesLeg_WhileTestsAndARunnerRun()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var config = new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Contention = new ContentionConfig { SharedResourceTools = ["dotnet"] },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig
                {
                    Phases = [new RunnerPhase { Name = "probe", Command = ["dotnet", "--version"], WatchContention = true }],
                },
            },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, config);

        // Another tree of the repository, below the worktrees root, as create-worktree makes one.
        var layout = new HarnessLayout(temp.Path, temp.Path);
        var worktree = Path.Combine(layout.WorktreesDirectoryUnder(config.Worktrees.Root), "plain");
        await harness.RunGitAsync(temp.Path, ["worktree", "add", "--quiet", "--detach", worktree], token);

        // A shared tool at work in that tree's build of the leg - a test binary there - for as long as the commands run.
        var theirs = Path.Combine(VariantKey.For(config, config.Legs["native"], platform.PlatformKey).DirectoryUnder(worktree), "tests", "suite");
        var start = new System.Diagnostics.ProcessStartInfo(TestHost.DotnetExecutable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add(TestHost.AssemblyPath);
        start.ArgumentList.Add("120000");
        start.ArgumentList.Add(theirs);
        start.Environment[TestHost.ChildModeVariable] = "sleep";
        using var tool = System.Diagnostics.Process.Start(start)!;

        try
        {
            foreach (var command in new[] { new[] { "test", "--no-build" }, ["run", "probe"] })
            {
                var result = await CliRunner.RunAsync([.. command, "--legs", "native", "-C", temp.Path], token);

                Assert.Equal(HarnessExit.Success, result.ExitCode);
                Assert.Contains(
                    $"dotnet (pid {tool.Id}), working in the build directory of worktree plain's leg 'native'",
                    result.StandardError,
                    StringComparison.Ordinal);
            }
        }
        finally
        {
            tool.Kill(entireProcessTree: true);
            await tool.WaitForExitAsync(CancellationToken.None);
        }
    }

    /// <summary>Waits, a little at a time, for <paramref name="done"/>, and fails the test when it never comes.</summary>
    private static async Task EventuallyAsync(Func<bool> done, CancellationToken cancellationToken)
    {
        for (var waited = 0; waited < 600 && !done(); waited++)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }

        Assert.True(done(), "what was waited for did not happen within a minute");
    }

    /// <summary>
    /// An environment giving the CLI a user's data of its own, below <paramref name="temp"/>, and the file it then
    /// keeps the hold between commands in: a test reaching a hold never reaches the one the machine's user holds.
    /// </summary>
    private static (Dictionary<string, string?> Environment, string Holds) OwnUserData(TempDirectory temp)
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

        return (environment, Path.Combine(data, "dssharness", "hold-awake.json"));
    }

    /// <summary>
    /// legs -v, through the real binary, says the room on this machine where the tree is; without -v it says
    /// nothing of it.
    /// </summary>
    [Fact]
    public async Task LegsVerbose_SaysTheRoomOnEachHost()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var verbose = await CliRunner.RunAsync(["legs", "--legs", "native", "-v", "-C", temp.Path], TestContext.Current.CancellationToken);
        var quiet = await CliRunner.RunAsync(["legs", "--legs", "native", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, verbose.ExitCode);
        Assert.Matches(@"local: [0-9.]+ [KMGT]?i?B(ytes)? free of [0-9.]+ [KMGT]?i?B(ytes)? on '", verbose.StandardOutput);
        Assert.DoesNotContain(" free of ", quiet.StandardOutput, StringComparison.Ordinal);
    }

    /// <summary>
    /// A command typed in a copy the harness synced to a host, whose legs name hosts it cannot reach, says each
    /// host's own refusal and then where the command belongs - once, through the real binary, however many
    /// hosts refused it.
    /// </summary>
    [Fact]
    public async Task ACommandInASyncedCopy_SaysWhereItBelongsOnce_HoweverManyHostsItCannotReach()
    {
        using var temp = new TempDirectory();

        await new HarnessFactory().InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            SshItems = { "pi", "mac" },
            Hosts = new HostsConfig
            {
                Ssh =
                {
                    ["pi"] = new SshHostConfig { RepositoryPath = "/home/pi/repo" },
                    ["mac"] = new SshHostConfig { RepositoryPath = "/Users/harness/repo" },
                },
            },
            Legs =
            {
                ["arm"] = new LegConfig { Os = "linux", Processor = "arm64", Config = "debug", Ssh = "pi" },
                ["mac"] = new LegConfig { Os = "macos", Processor = "arm64", Config = "debug", Ssh = "mac" },
            },
        });

        temp.WriteFile(Path.Combine(HarnessLayout.DirectoryName, HarnessLayout.SyncedCopyMarkerName), "{}");

        var result = await CliRunner.RunAsync(["legs", "--legs", "arm,mac", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.NotEqual(HarnessExit.Success, result.ExitCode);
        Assert.Contains("sshItems/pi", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("sshItems/mac", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Single((result.StandardError + result.StandardOutput).Split(HostConnector.SyncedCopyNotice)[1..]);
    }

    /// <summary>
    /// clean, through the real binary, removes a leg's build directory in the tree it is typed in and says
    /// what it removed as data; a leg no host can take is said as that, and nothing of it is touched.
    /// </summary>
    [Fact]
    public async Task Clean_RemovesALegsBuildDirectory_AndSaysWhatItRemoved()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var platform = new HarnessFactory().Platform;
        var leg = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" };
        var directory = VariantKey
            .For(new HarnessConfig { BuildConfigs = { ["debug"] = new BuildConfiguration() }, Legs = { ["native"] = leg } }, leg, platform.PlatformKey)
            .DirectoryUnder(temp.Path);

        temp.WriteFile(Path.Combine(Path.GetRelativePath(temp.Path, directory), "obj", "a.o"), new string('a', 300));

        var result = await CliRunner.RunAsync(["clean", "--legs", "native", "--json", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var line = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("passed", line.GetProperty("verdict").GetString());
        Assert.Equal(300, line.GetProperty("space").GetProperty("buildBytes").GetInt64());
        Assert.True(line.GetProperty("space").GetProperty("removed").GetBoolean());
        Assert.False(Directory.Exists(directory));
    }

    /// <summary>
    /// clean, dispatched here by another machine, leaves that machine the lines it says for itself - that the legs are
    /// starting, where each starts, and each one's verdict - since it relays every line said here: said here too, a
    /// consumer saw each leg's line twice, and a start line for every leg beside the run's own. The leg's line still
    /// reaches it, in the document.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clean_DispatchedHere_LeavesTheDispatchingMachineTheLinesItSays(bool dryRun)
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["clean", "--legs", "native", "--json", RemoteLegRunner.HereOption, "local", .. dryRun ? new[] { CleanService.DryRunOption } : [], "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("passed", Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("verdict").GetString());
        Assert.DoesNotContain("clean: starting", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("clean: native:", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a refusal of the whole run answers with the ledger too, carrying the refusal's own code
    /// and words - as its FAIL line does, on standard error, where a reader of the terminal sees it.
    /// </summary>
    [Fact]
    public async Task ARefusedRun_AnswersWithTheLedger_WithTheRefusalsCodeAndWords()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        // A program this machine has and the repository never declared: the leg is placed, then
        // refused before anything starts.
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: git --version\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(HarnessExit.Refused, root.GetProperty("exitCode").GetInt32());
        Assert.Contains("'git' is not declared under 'tools'", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.False(root.GetProperty("passed").GetBoolean());
        Assert.False(root.GetProperty("complete").GetBoolean());
        Assert.False(root.TryGetProperty("verdict", out _), "a stopped run reached no verdict of its own");
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.Contains("'git' is not declared under 'tools'", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A step for another operating system is left out of a leg - here one starting a program nobody
    /// declared, which would refuse the whole run where the step runs, and naming {product}, which
    /// would build the leg first where it runs: this leg names no project, so it cannot be built -
    /// and the leg passes on the steps it did run, naming the one it left out as it runs and on its
    /// line.
    /// </summary>
    [Fact]
    public async Task AStepForAnotherSystem_IsLeftOut_AndNamedOnTheLegsLine()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            $"name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n"
            + $"  - name: fetch\n    runOn: [{ElsewhereOs}]\n    run: curl --output {{product}} https://example.invalid\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Contains($"native: skipped 'fetch', which runs on {ElsewhereOs} only", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal("passed", leg.GetProperty("verdict").GetString());
        Assert.Equal(["fetch"], leg.GetProperty("skippedSteps").EnumerateArray().Select(step => step.GetString()));
    }

    /// <summary>
    /// --dry-run reaches the leg's host and installs nothing: the tool it would install is named
    /// with the command that would run, and the answer is still "not provisioned". The install
    /// declared here is harmless, so a dry run that ran it anyway fails this rather than a machine.
    /// </summary>
    [Fact]
    public async Task InstallMissingTools_DryRun_NamesTheCommand_AndRunsNothing()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
            Tools = { new ToolConfig { Name = "dssharness-absent-tool", Install = { ["all"] = new ToolInstall { Command = ["dotnet", "--version"] } } } },
        });

        var result = await CliRunner.RunAsync(["install-missing-tools", "--dry-run", "--json", "-C", temp.Path], token);

        Assert.Equal(ToolsExit.NotProvisioned, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var tool = Assert.Single(Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("tools").EnumerateArray());

        Assert.True(document.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal("would install", tool.GetProperty("state").GetString());
        Assert.Equal("would run 'dotnet --version'", tool.GetProperty("detail").GetString());
    }

    /// <summary>
    /// init installs tools only when given --install-tools, and otherwise says how.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Init_InstallsTools_OnlyWithTheFlag(bool installTools)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs = { ["native"] = new LegConfig { Os = harness.Platform.PlatformKey, Processor = harness.Platform.Processor, Config = "debug" } },
        });

        var result = await CliRunner.RunAsync(installTools ? ["init", "--install-tools", "-C", temp.Path] : ["init", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);
        Assert.Equal(installTools, result.StandardOutput.Contains("tools   native on local", StringComparison.Ordinal));
        Assert.Equal(!installTools, result.StandardOutput.Contains("tools   not checked", StringComparison.Ordinal));
    }

    /// <summary>
    /// --input gives a declared input its value over the default - here the program the step starts,
    /// which as the default is one nobody declared - and a value for an input the action does not
    /// declare refuses the run before anything starts, naming what it does declare.
    /// </summary>
    [Fact]
    public async Task AnInputGivenOnTheCommandLine_ReachesTheStep_AndAnUndeclaredOneIsRefused()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);
        var token = TestContext.Current.CancellationToken;

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\ninputs:\n  program:\n    default: curl\nsteps:\n  - name: version\n    run: \"{program} --version\"\n");

        var given = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--input", "program=dotnet", "-C", temp.Path], token);
        var undeclared = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--input", "programme=dotnet", "--json", "-C", temp.Path],
            token);

        Assert.Equal(HarnessExit.Success, given.ExitCode);
        Assert.Equal(HarnessExit.UsageError, undeclared.ExitCode);
        Assert.Contains("--input names 'programme'", undeclared.StandardError, StringComparison.Ordinal);
        Assert.Contains("it declares program.", undeclared.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(undeclared.StandardOutput);

        Assert.Equal(HarnessExit.UsageError, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");
    }

    /// <summary>
    /// A leg on whose operating system no step runs would pass having run nothing, so the run is
    /// refused before a host is measured or a run begins, naming the leg. Left to each leg, one
    /// that no host can take made the run merely incomplete, and one a host could take passed.
    /// </summary>
    [Fact]
    public async Task ARunWithALegOnWhoseSystemNoStepRuns_IsRefusedBeforeAnythingStarts()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            $"name: probe\nsteps:\n  - name: version\n    runOn: [{new HarnessFactory().Platform.PlatformKey}]\n    run: dotnet --version\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Contains($"leg 'elsewhere' ({ElsewhereOs})", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("leg 'native'", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal(HarnessExit.Refused, document.RootElement.GetProperty("exitCode").GetInt32());
        Assert.Empty(document.RootElement.GetProperty("legs").EnumerateArray());
        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");
    }

    /// <summary>
    /// The document is the whole of standard output from the command's first line: 'run' reads its
    /// action before any leg is surveyed, and under --verbose says so, which went to standard output
    /// in front of the document a script was about to parse.
    /// </summary>
    [Fact]
    public async Task UnderVerbose_TheLedgerIsStillTheWholeOfStandardOutput()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native", "--json", "--verbose", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal("native", Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("leg").GetString());
        Assert.Contains("reading action file", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// And before any leg is placed, too: a leg nobody declared is a usage error, answered with a
    /// ledger holding no legs - so a script reading standard output has one document to read on
    /// every exit, where it had nothing at all.
    /// </summary>
    [Fact]
    public async Task ARunStoppedBeforeAnyLegWasPlaced_StillAnswersWithTheLedger()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        var result = await CliRunner.RunAsync(
            ["test", "--no-build", "--legs", "no-such-leg", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.Equal(HarnessExit.UsageError, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var root = document.RootElement;

        Assert.Equal(HarnessExit.UsageError, root.GetProperty("exitCode").GetInt32());
        Assert.Contains("no-such-leg", root.GetProperty("summary").GetString(), StringComparison.Ordinal);
        Assert.Empty(root.GetProperty("legs").EnumerateArray());
        Assert.Contains("no-such-leg", result.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// The chain the consumer's gate broke on, end to end: a leg whose test runner no directory on
    /// this machine holds is turned away by the survey, naming the program, and a test run that
    /// selects it reports it as skipped for a missing tool - incomplete, exit 21 - rather than starting
    /// it and poisoning the run, exit 70. Its sibling still runs. And only a command that starts the
    /// runner turns the leg away for it: a runner whose steps run something else runs there.
    /// </summary>
    [Fact]
    public async Task ALegWhoseTestRunnerIsNowhere_IsTurnedAwayForItsTests_AndOnlyForThem()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var missing = "rh-missing-" + Guid.NewGuid().ToString("N")[..8];

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
                ["broken"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig { All = new TestInvocation { Runner = missing, SuccessPattern = "passed" } },
                },
            },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");

        var legs = await CliRunner.RunAsync(["legs", "-C", temp.Path], token);

        Assert.Contains($"'{missing}' is not installed there", legs.StandardError, StringComparison.Ordinal);

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native,broken", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Incomplete, test.ExitCode);

        using (var document = JsonDocument.Parse(test.StandardOutput))
        {
            var verdicts = document.RootElement.GetProperty("legs").EnumerateArray()
                .ToDictionary(leg => leg.GetProperty("leg").GetString()!, leg => leg.GetProperty("verdict").GetString());

            Assert.Equal("passed", verdicts["native"]);
            Assert.Equal("skipped-tool-missing", verdicts["broken"]);
        }

        // A runner whose steps never start the test runner is not turned away for it.
        var run = await CliRunner.RunAsync(["run", "probe", "--legs", "broken", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, run.ExitCode);
    }

    /// <summary>
    /// A label given to <c>test</c> reaches the leg's runner through its labelArg, end to end. One no
    /// labelArg can carry is refused rather than dropped: dropped, the leg would run every test and
    /// report the result as the labelled subset's.
    /// </summary>
    [Theory]
    [InlineData("-L", HarnessExit.Success)]
    [InlineData(null, HarnessExit.UsageError)]
    public async Task ALabel_ReachesTheLegsRunner_OrIsRefusedWhereNothingCanCarryIt(string? labelArg, int exitCode)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = TestHost.DotnetExecutable,
                            Args = ["exec", TestHost.AssemblyPath],
                            Env = new Dictionary<string, string>(StringComparer.Ordinal) { [TestHost.ChildModeVariable] = "echo-command-line" },
                            LabelArg = labelArg,
                            SuccessPattern = @"\[-L\]\s+\[unit\]",
                        },
                    },
                },
            },
        });

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "--label", "unit", "-C", temp.Path], token);

        Assert.True(exitCode == test.ExitCode, $"exit {test.ExitCode}: {test.StandardError}");

        if (labelArg is null)
        {
            Assert.Contains("declare no labelArg", test.StandardError, StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// A leg a host runs for the machine that dispatched it - in that host's copy of the repository, an
    /// ssh host's or a WSL distribution's - leaves out the invocation's remoteExcludes, end to end; the same
    /// leg run where it was asked for leaves out only what it was asked to, and the witness its runner
    /// would print is not there.
    /// </summary>
    [Theory]
    [InlineData("ssh mac", HarnessExit.Success)]
    [InlineData("wsl Example-Linux", HarnessExit.Success)]
    [InlineData(null, LegExit.Unwitnessed)]
    public async Task ALegAHostRuns_LeavesOutTheRemoteExcludes_AndOneRunHereDoesNot(string? dispatchedAs, int exitCode)
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = TestHost.DotnetExecutable,
                            Args = ["exec", TestHost.AssemblyPath],
                            Env = new Dictionary<string, string>(StringComparer.Ordinal) { [TestHost.ChildModeVariable] = "echo-command-line" },
                            ExcludeArg = "-LE",
                            RemoteExcludes = ["git-state"],
                            SuccessPattern = @"\[-LE\]\s+\[git-state\]",
                        },
                    },
                },
            },
        });

        string[] here = dispatchedAs is null ? [] : ["--here", dispatchedAs];
        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", .. here, "-C", temp.Path], token);

        Assert.True(exitCode == test.ExitCode, $"exit {test.ExitCode}: {test.StandardError}");
    }

    /// <summary>
    /// A leg whose build failed carries the last lines the build printed on its line, whichever command
    /// built it: what a reader whose log is on another host has. Here the tree holds no project file, so
    /// the build fails the same way on every machine, and says so.
    /// </summary>
    [Theory]
    [InlineData("build")]
    [InlineData("test")]
    [InlineData("run")]
    public async Task ALegWhoseBuildFailed_CarriesWhatTheBuildPrintedLast_WhicheverCommandBuiltIt(string verb)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        string[] command = verb == "run" ? ["run", "probe"] : [verb];

        await ABuildThatFailsAsync(temp, new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" }, token);

        var result = await CliRunner.RunAsync([.. command, "--legs", "native", "--json", "-C", temp.Path], token);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var legs = document.RootElement.GetProperty("legs").EnumerateArray().ToList();

        Assert.True(legs.Count == 1, $"exit {result.ExitCode}: {result.StandardError}");
        Assert.Equal("failed", legs[0].GetProperty("verdict").GetString());
        Assert.Contains(legs[0].GetProperty("logTail").EnumerateArray(), line => line.GetString()!.Contains("MSB1003", StringComparison.Ordinal));
    }

    /// <summary>
    /// A step naming {product}, named with --manual-step through a runner that declares no requireBuild, builds its
    /// leg first - limited by runOn to this leg's system or not, and on the host a leg was sent to as on this machine,
    /// since that host works out what to build from the same steps: here the build fails, as it does on every machine -
    /// the tree holds no project file - and the leg says so. Unbuilt, the step was handed a product that was not there,
    /// and the leg passed. A plain run of the same runner, whose step names nothing the build makes, builds nothing, and
    /// passes.
    /// </summary>
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "ssh mac")]
    [InlineData(false, "wsl Example-Linux")]
    public async Task AStepNamingTheProduct_BuildsItsLegFirst_ThroughARunnerThatDeclaresNoBuild(bool limitedToThisSystem, string? dispatchedAs)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["sdk"] = new ToolchainConfig { Platforms = [platform.PlatformKey] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = ".", BuildOutputs = [BuildOutput.Everywhere("app.dll")] } },
            Tools = { new ToolConfig { Name = "git" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "sdk" } },
            PredefinedRunners = { ["self-test"] = new RunnerConfig { Action = "probe/probe.yml", Steps = ["self-test"] } },
        });

        var runOn = limitedToThisSystem ? $"    runOn: [{platform.PlatformKey}]\n" : string.Empty;

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n"
            + "  - name: self-test\n    manual: true\n    successPattern: '^git version'\n    run: git --version\n"
            + $"  - name: measure\n    manual: true\n{runOn}    successPattern: '^git version'\n"
            + "    run: git -c harness.product=\"{product}\" --version\n");

        string[] here = dispatchedAs is null ? [] : ["--here", dispatchedAs];

        var plain = await CliRunner.RunAsync(["run", "self-test", "--legs", "native", .. here, "--json", "-C", temp.Path], token);

        Assert.True(plain.ExitCode == HarnessExit.Success, $"exit {plain.ExitCode}: {plain.StandardError}");

        // A failed build's last lines are on the leg's line, in the ledger on standard output.
        Assert.DoesNotContain("MSB1003", plain.StandardOutput, StringComparison.Ordinal);

        var measured = await CliRunner.RunAsync(
            ["run", "self-test", "--manual-step", "measure", "--legs", "native", .. here, "--json", "-C", temp.Path],
            token);

        using var document = JsonDocument.Parse(measured.StandardOutput);
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.True(leg.GetProperty("verdict").GetString() == "failed", measured.StandardError);
        Assert.Contains(leg.GetProperty("logTail").EnumerateArray(), line => line.GetString()!.Contains("MSB1003", StringComparison.Ordinal));
    }

    /// <summary>
    /// A step naming {product}, where the leg's project declares no product for its system, is refused before anything
    /// starts - no host measured, no run begun, nothing built - naming the leg and the step. Since naming the product
    /// builds the leg first, found where the step began it would have cost a build; here that build would fail, as it
    /// does on every machine, and the leg would have said its build failed instead.
    /// </summary>
    [Fact]
    public async Task AStepNamingAProductThatIsNotDeclared_IsRefusedBeforeTheLegBuilds()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var platform = new HarnessFactory().Platform;

        await ABuildThatFailsAsync(temp, new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" }, token);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: measure\n    run: dotnet --version --product=\"{product}\"\n");

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.True(result.ExitCode == HarnessExit.ConfigInvalid, $"exit {result.ExitCode}: {result.StandardError}");
        Assert.Contains(
            $"  - Leg 'native': step 'measure' names {{product}}, and project 'app' declares no buildOutputs for {platform.PlatformKey}.",
            result.StandardError,
            StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Empty(document.RootElement.GetProperty("legs").EnumerateArray());
        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");
    }

    /// <summary>
    /// A step naming {buildDir} on a leg that names no project would build the leg first, which cannot be built: refused
    /// before anything starts, naming the leg, why and the step. Left to where the leg's build begins - as it was for a
    /// runner requiring the build - it would end the whole run once its host was measured, saying only that the leg
    /// names no project.
    /// </summary>
    [Fact]
    public async Task AStepNamingTheBuildDirectory_OnALegNamingNoProject_IsRefusedBeforeAnythingStarts()
    {
        using var temp = new TempDirectory();
        await PrepareRunnerAsync(temp);

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: deps\n    workingDirectory: '{buildDir}'\n    run: dotnet --version\n");

        var result = await CliRunner.RunAsync(
            ["run", "probe", "--legs", "native,elsewhere", "--json", "-C", temp.Path],
            TestContext.Current.CancellationToken);

        Assert.True(result.ExitCode == HarnessExit.ConfigInvalid, $"exit {result.ExitCode}: {result.StandardError}");
        Assert.Contains(
            "  - Leg 'native' cannot be built: it names no project, and neither defaults.project nor a single declared "
            + "project supplies one. The run builds it first because step 'deps' names {buildDir}.",
            result.StandardError,
            StringComparison.Ordinal);

        // A leg no host could take is refused with the rest, as a fact of the configuration, rather than skipped as
        // unavailable: the configuration is what has to change, whichever machine is up.
        Assert.Contains("  - Leg 'elsewhere' cannot be built: it names no project", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.False(document.RootElement.TryGetProperty("runDirectory", out _), "no run began, so none has records");

        // A build of such a leg is refused saying the same, from the same place.
        var build = await CliRunner.RunAsync(["build", "--legs", "native", "-C", temp.Path], TestContext.Current.CancellationToken);

        Assert.True(build.ExitCode == HarnessExit.ConfigInvalid, $"exit {build.ExitCode}: {build.StandardError}");
        Assert.Contains(
            "Leg 'native' cannot be built: it names no project, and neither defaults.project nor a single declared project "
            + "supplies one.",
            build.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A runner whose run checks name a runner needing the build - by a step naming {product}, or requiring it - builds its
    /// own legs first, since a check runs on the leg as the runner carrying it left it, and is never built itself: read
    /// from the run checks as the run begins, so here, on a leg naming no project, the run is refused before anything
    /// starts, naming the check. Left out, the check confirmed an expected exception against whatever the last build
    /// left.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ARunCheckThatNeedsTheBuild_BuildsTheCarriersLegFirst(bool requireBuild)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" } },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig
                {
                    Action = "probe/probe.yml",
                    ExpectedExceptions =
                    [
                        ExpectedExceptionMatcherTests.Entry(
                            messages: ["the device was busy"],
                            runChecks: [new RunCheck { PredefinedRunner = "confirm", Expects = new RunCheckExpectation { Success = true } }]),
                    ],
                },
                ["confirm"] = new RunnerConfig { Action = "confirm/confirm.yml", RequireBuild = requireBuild },
            },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");
        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "confirm", "confirm.yml"),
            requireBuild
                ? "name: confirm\nsteps:\n  - name: again\n    run: dotnet --version\n"
                : "name: confirm\nsteps:\n  - name: again\n    run: dotnet --version --product=\"{product}\"\n");

        var run = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.True(run.ExitCode == HarnessExit.ConfigInvalid, $"exit {run.ExitCode}: {run.StandardError}");
        Assert.Contains(
            requireBuild
                ? "The run builds it first because runner 'confirm', which a run check names, requires the build."
                : "The run builds it first because step 'again' of runner 'confirm', which a run check names, names {product}.",
            run.StandardError,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A test run the runner could not be given as asked is refused before the leg builds: everything its
    /// command is made from is known then. Here the build would fail - the tree holds no project file - so
    /// a refusal that came after it would never be reached, and the leg would say its build failed.
    /// </summary>
    [Fact]
    public async Task ATestThatCannotBeGivenAsAsked_IsRefusedBeforeTheLegBuilds()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await ABuildThatFailsAsync(
            temp,
            new TestInvocation { Runner = "dotnet", Args = ["--version"], ExcludeArg = "--exclude", SuccessPattern = @"^\d+\.\d+" },
            token);

        var test = await CliRunner.RunAsync(["test", "--legs", "native", "--exclude", "", "-C", temp.Path], token);

        Assert.True(test.ExitCode == HarnessExit.UsageError, $"exit {test.ExitCode}: {test.StandardError}");
        Assert.Contains("given empty, or as spaces alone", test.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A leg testing a build it does not make names the compilers that build was configured with,
    /// read from what CMake answered then: its verdict is about binaries they produced.
    /// </summary>
    [Fact]
    public async Task ATestOfABuildItDoesNotMake_NamesTheCompilersItsDirectoryWasConfiguredWith()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await ConfiguredWithGnuAsync(temp, new ToolchainConfig { Env = { ["CC"] = "cc" } }, token);

        var result = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var compiler = Assert.Single(Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("compilers").EnumerateArray());

        Assert.Equal("GNU", compiler.GetProperty("id").GetString());
        Assert.Equal("13.2.0", compiler.GetProperty("version").GetString());
    }

    /// <summary>
    /// A leg testing a build it does not make holds that build to its toolchain's compilerId, as the
    /// build that made it would have been: a directory CMake configured with another compiler is failed
    /// rather than tested, and a declared language CMake named nothing for is unwitnessed.
    /// </summary>
    [Theory]
    [InlineData("C", "Clang", HarnessExit.CommandFailed, "failed", "CMake configured this build with another compiler than toolchain 'cc' declares: C with GNU 13.2.0, not Clang")]
    [InlineData("CXX", "GNU", LegExit.Unwitnessed, "unwitnessed", "toolchain 'cc' declares the compiler for CXX, and CMake named none for it")]
    public async Task ATestOfABuildItDoesNotMake_HoldsItToTheToolchainsCompilerId(string language, string id, int exitCode, string verdict, string detail)
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await ConfiguredWithGnuAsync(temp, new ToolchainConfig { Env = { ["CC"] = "cc" }, CompilerId = { [language] = id } }, token);

        var result = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(exitCode, result.ExitCode);

        using var document = JsonDocument.Parse(result.StandardOutput);
        var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

        Assert.Equal(verdict, leg.GetProperty("verdict").GetString());
        Assert.StartsWith(detail, leg.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    /// <summary>
    /// Rewrites the record a build left in <paramref name="buildDirectory"/> as 0.5.8 wrote one for a build
    /// it marked unordered - the mark alone on its first line, the variant, and a fingerprint line for each
    /// input, with no reason, no dates and no newest file - so the next build starts from clean.
    /// </summary>
    private static void Unordered(string buildDirectory)
    {
        var record = Path.Combine(buildDirectory, ".harness-build");

        File.WriteAllText(record, EarlierRecord.Written(File.ReadAllText(record), unordered: true));
    }

    /// <summary>
    /// Asserts that a build that said <paramref name="said"/>, and kept its records in
    /// <paramref name="records"/>, asked the compiler of each of <paramref name="languages"/> its version
    /// and found it as CMake identified it: the line the question was put in is among the leg's records,
    /// nothing said a compiler could not be asked, and nothing said one changed. A question never put, or
    /// never answered, would say neither.
    /// </summary>
    private static void AskedEachCompiler(string said, string records, string leg, params string[] languages)
    {
        Assert.DoesNotContain("a changed compiler", said, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be asked", said, StringComparison.Ordinal);

        foreach (var language in languages)
        {
            var asked = Path.Combine(records, leg, $"compiler-version-{language}{(language == "C" ? ".c" : ".cpp")}");

            Assert.True(File.Exists(asked), $"{language}'s compiler was never asked its version: '{asked}' is not there. {said}");
        }
    }

    /// <summary>Where the run <paramref name="result"/> reports on keeps its records, as its JSON names them.</summary>
    private static string RecordsOf(ProcessResult result)
    {
        using var document = JsonDocument.Parse(result.StandardOutput);

        return document.RootElement.GetProperty("runDirectory").GetString()!;
    }

    /// <summary>
    /// A repository whose one leg builds a .NET project, testing it with <paramref name="test"/>, from a tree
    /// that holds no project file: its build runs and fails the same way on every machine, saying MSB1003.
    /// A predefined runner, 'probe', builds before it runs.
    /// </summary>
    private static async Task ABuildThatFailsAsync(TempDirectory temp, TestInvocation test, CancellationToken token)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["sdk"] = new ToolchainConfig { Platforms = [platform.PlatformKey] } },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects = { new ProjectConfig { Name = "app", Type = "dotnet", Path = ".", Test = new TestConfig { All = test } } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "sdk" } },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml", RequireBuild = true } },
        });

        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"), "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");
    }

    /// <summary>
    /// A repository whose one leg builds with <paramref name="toolchain"/>, named cc, whose build
    /// directory CMake last configured with GNU 13.2.0 for C, as its file API answered.
    /// </summary>
    private static async Task ConfiguredWithGnuAsync(TempDirectory temp, ToolchainConfig toolchain, CancellationToken token)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        toolchain.Platforms.Clear();
        toolchain.Platforms.Add(platform.PlatformKey);

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = toolchain },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
        });

        var replies = Path.Combine("build", $"{platform.Processor}-cc-debug", ".cmake", "api", "v1", "reply");
        temp.WriteFile(Path.Combine(replies, "index-2026-09-19T16-16-05-0385.json"), """{ "reply": { "toolchains-v1": { "jsonFile": "toolchains-v1-a.json" } } }""");
        temp.WriteFile(
            Path.Combine(replies, "toolchains-v1-a.json"),
            """{ "toolchains": [ { "language": "C", "compiler": { "id": "GNU", "version": "13.2.0" } } ] }""");
    }

    /// <summary>
    /// A real configure, by the CMake on this machine: the compiler it resolved is named on the leg's
    /// line by build and by a runner that builds, read back from what CMake itself wrote. Skipped
    /// where this machine has no CMake, no Ninja or no C compiler - and failed there instead, where it
    /// says it is meant to hold every build tool (<see cref="BuildTools"/>).
    /// </summary>
    [Fact]
    public async Task ARealConfigure_IsNamedOnTheLegsLine_ByBuildAndByARunnerThatBuilds()
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";

        BuildTools.Need(harness.ProcessRunner, "a real configure", "cmake", "ninja", compiler);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, which a build system orders by the times of the files they write, and records
        // held to answers by theirs: on a clock that steps, what they do proves nothing here. Watched
        // from before the files they build from are written.
        using var clock = new ClockWatch();

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = compiler } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    BuildOutputs = [BuildOutput.Keyed([new("windows", "probe.exe"), new("all", "probe")])],
                },
            },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml", RequireBuild = true } },
        });

        temp.WriteFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_executable(probe main.c)\n");
        temp.WriteFile("main.c", "int main(void) { return 0; }\n");
        temp.WriteFile(Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"), "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");

        try
        {
            var build = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token);
            var run = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

            // The runner builds again, asking the compiler its version first: what it says, put together as
            // CMake puts a version together, is what CMake recorded, or the directory would start from clean
            // for a compiler that never changed.
            AskedEachCompiler(run.StandardError, RecordsOf(run), "native", "C");

            // A record marked unordered, as an earlier version marked one, starts the next build from clean,
            // and a runner that built first says so on its leg's line, as a build's own line does.
            Unordered(temp.Combine("build", $"{platform.Processor}-cc-debug"));

            var rerun = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

            using (var rerunDocument = JsonDocument.Parse(rerun.StandardOutput))
            {
                Assert.Contains(
                    Assert.Single(rerunDocument.RootElement.GetProperty("legs").EnumerateArray()).GetProperty("timingNotes").EnumerateArray(),
                    note => note.GetString()!.StartsWith("rebuilt from clean: an unordered build", StringComparison.Ordinal));
            }

            foreach (var result in new[] { build, run })
            {
                using var document = JsonDocument.Parse(result.StandardOutput);
                var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
                var configured = Assert.Single(leg.GetProperty("compilers").EnumerateArray());

                Assert.Equal("C", configured.GetProperty("language").GetString());
                Assert.False(string.IsNullOrEmpty(configured.GetProperty("id").GetString()), result.StandardError);
                Assert.Contains("compiler: ", result.StandardError, StringComparison.Ordinal);
            }
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A real configure, by the CMake, Ninja and C and C++ compilers on this machine, of a C++ project whose C only a
    /// subproject's own <c>project()</c> enables, as a dependency such as googletest enables it: CMake's answer names C's
    /// compiler with a path and no id, and C is identified from CMake's record of it and held to what the toolchain
    /// declares - for the configure that asked, and for the directory read afresh - though that record is dated after the
    /// answer, as a clock that stepped forward for a moment dates one. Told apart by when each was written, the record was
    /// a later configure's, and a consumer's leg was unwitnessed. A leg built from that project, its toolchain declaring
    /// both compilers, passes. Skipped where this machine lacks one of the tools - and failed there instead, where it says
    /// it is meant to hold every build tool (<see cref="BuildTools"/>).
    /// </summary>
    /// <remarks>
    /// The record is dated after the answer once that configure is done, and the leg built in a directory of its own: a
    /// build finding a record dated ahead of its build files has Ninja run CMake again until the clock passes it, which a
    /// project this small does a hundred times, and fails, within the 24 seconds a consumer's clock stepped.
    /// </remarks>
    [Fact]
    public async Task ARealConfigureOfALanguageOnlyASubprojectEnables_IdentifiesIt_ThoughItsRecordIsDatedAfterTheAnswer_AndItsLegPasses()
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var (c, cxx) = OperatingSystem.IsWindows() ? ("gcc", "g++") : ("cc", "c++");

        BuildTools.Need(harness.ProcessRunner, "a real configure of a project whose C only a subproject enables", "cmake", "ninja", c, cxx);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, ordered by the times of the files they write: on a clock that steps, they prove nothing here.
        using var clock = new ClockWatch();

        var repository = temp.Combine("r");
        var compilers = new Dictionary<string, string?> { ["CC"] = c, ["CXX"] = cxx };

        Directory.CreateDirectory(repository);
        File.WriteAllText(Path.Combine(repository, "CMakeLists.txt"), "cmake_minimum_required(VERSION 3.20)\nproject(app CXX)\nadd_subdirectory(dep)\nadd_executable(app main.cpp)\ntarget_link_libraries(app PRIVATE dep)\n");
        File.WriteAllText(Path.Combine(repository, "main.cpp"), "extern \"C\" int dep(void);\nint main() { return dep() == 7 ? 0 : 1; }\n");
        Directory.CreateDirectory(Path.Combine(repository, "dep"));
        File.WriteAllText(Path.Combine(repository, "dep", "CMakeLists.txt"), "project(dep C)\nadd_library(dep STATIC dep.c)\n");
        File.WriteAllText(Path.Combine(repository, "dep", "dep.c"), "int dep(void) { return 7; }\n");

        try
        {
            // Configured once outside any leg, for what CMake answers about C and the ids it identifies here: what the
            // leg's toolchain then declares.
            var reader = new CMakeToolchainReader(harness.FileSystem);
            var probe = temp.Combine("probe");
            var asked = reader.Ask(probe);
            var configured = await harness.ProcessRunner.RunAsync(
                new ProcessRequest { FileName = "cmake", Arguments = ["-S", repository, "-B", probe, "-G", "Ninja"], Environment = compilers },
                token);

            Assert.True(configured.ExitCode == 0, configured.StandardError + configured.StandardOutput);

            var replies = Path.Combine(probe, ".cmake", "api", "v1", "reply");

            using (var toolchains = JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(replies, "toolchains-v1-*.json").Single())))
            {
                var answered = toolchains.RootElement.GetProperty("toolchains").EnumerateArray()
                    .Single(toolchain => toolchain.GetProperty("language").GetString() == "C")
                    .GetProperty("compiler");

                Assert.True(answered.TryGetProperty("path", out _) && !answered.TryGetProperty("id", out _), answered.GetRawText());
            }

            // CMake's record of identifying C, dated after its answer by as much as a consumer's clock stepped.
            var record = Directory.EnumerateFiles(Path.Combine(probe, "CMakeFiles"), "CMakeCCompiler.cmake", SearchOption.AllDirectories).Single();

            File.SetLastWriteTimeUtc(record, File.GetLastWriteTimeUtc(Directory.EnumerateFiles(replies, "index-*.json").Single()).AddSeconds(24));

            var readings = new[] { reader.Read(probe, asked), reader.Read(probe) };
            var ids = readings[0].Compilers.ToDictionary(compiler => compiler.Language, compiler => compiler.Id);

            Assert.True(ids.ContainsKey("C") && ids.ContainsKey("CXX"), string.Join(", ", ids) + " " + string.Join("; ", readings[0].Unidentified));

            var config = new HarnessConfig
            {
                Toolchains =
                {
                    ["cc"] = new ToolchainConfig
                    {
                        Platforms = [platform.PlatformKey],
                        Generator = "Ninja",
                        Env = { ["CC"] = c, ["CXX"] = cxx },
                        CompilerId = { ["C"] = ids["C"], ["CXX"] = ids["CXX"] },
                    },
                },
                BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
                Projects = { new ProjectConfig { Name = "app", Type = "cmake", Path = ".", BuildOutputs = [BuildOutput.Keyed([new("windows", "app.exe"), new("all", "app")])] } },
                Tools = { new ToolConfig { Name = "cmake" } },
                Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            };

            foreach (var reading in readings)
            {
                Assert.Equal(readings[0].Compilers, reading.Compilers);
                Assert.False(reading.Unidentified.ContainsKey("C"), reading.Unidentified.GetValueOrDefault("C"));
                Assert.Null(CompilerFacts.HeldTo(config, "cc", reading));
            }

            await harness.InitializeHarnessAsync(repository, token, config);

            // Ignored, as a repository ignores its builds: committed, a build's own files would be inputs it changes.
            File.AppendAllText(Path.Combine(repository, ".gitignore"), "build/\n");
            await harness.CommitAllAsync(repository, "a C++ project whose C a subproject enables", token);

            var built = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", repository], token);

            Assert.True(built.ExitCode == HarnessExit.Success, built.StandardError + built.StandardOutput);

            using var document = JsonDocument.Parse(built.StandardOutput);
            var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());

            Assert.Equal(
                ["C " + ids["C"], "CXX " + ids["CXX"]],
                leg.GetProperty("compilers").EnumerateArray().Select(compiler => $"{compiler.GetProperty("language").GetString()} {compiler.GetProperty("id").GetString()}").Order(StringComparer.Ordinal));
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A real incremental build, by the cmake, ninja and C compiler on this machine, of a tree one of whose targets
    /// was renamed away since its last build: that target's object is still in the build directory, deeper than the
    /// path budget's reserve, and ninja says no target produces it any more. It is left out of the check and noted,
    /// naming what removes it, and the warning a consumer's builds gave every time is not given. Skipped where this
    /// machine has no CMake, no Ninja, or no C compiler - and failed there instead, where it says it is meant to hold
    /// every build tool (<see cref="BuildTools"/>).
    /// </summary>
    [Fact]
    public async Task ARealIncrementalBuild_LeavesATargetRenamedAwayOutOfThePathBudget()
    {
        const string Renamed = "a_target_with_a_long_name_that_a_later_commit_renames_away_from_the_project";

        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var compiler = OperatingSystem.IsWindows() ? "gcc" : "cc";

        BuildTools.Need(harness.ProcessRunner, "a real build", "cmake", "ninja", compiler);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, which ninja orders by the times of the files they write: on a clock that steps, what they
        // do proves nothing here.
        using var clock = new ClockWatch();

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = compiler } } },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    BuildOutputs = [BuildOutput.Keyed([new("windows", "probe.exe"), new("all", "probe")])],
                },
            },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },

            // Past everything a build of this tree writes but the renamed target's object.
            Worktrees = new WorktreeSettings { PathBudgetReserve = 80, PathBudgetMargin = 2 },
        });

        // Ignored, as a repository ignores its builds: committed, a build's own files would be inputs it changes.
        temp.WriteFile(".gitignore", "build/\n");
        temp.WriteFile("main.c", "int main(void) { return 0; }\n");
        temp.WriteFile("other.c", "int main(void) { return 1; }\n");
        temp.WriteFile("CMakeLists.txt", $"cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_executable(probe main.c)\nadd_executable({Renamed} other.c)\n");
        await harness.CommitAllAsync(temp.Path, "two targets", token);

        try
        {
            var first = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token);

            Assert.True(first.ExitCode == HarnessExit.Success, first.StandardError + first.StandardOutput);

            temp.WriteFile("CMakeLists.txt", "cmake_minimum_required(VERSION 3.20)\nproject(probe C)\nadd_executable(probe main.c)\nadd_executable(short other.c)\n");
            await harness.CommitAllAsync(temp.Path, "one renamed", token);

            var second = await CliRunner.RunAsync(["build", "--legs", "native", "--json", "-C", temp.Path], token);

            Assert.True(second.ExitCode == HarnessExit.Success, second.StandardError + second.StandardOutput);
            Assert.Contains("output(s) below this build directory are ones no target of this build produces any more", second.StandardError, StringComparison.Ordinal);
            Assert.Contains(Renamed, second.StandardError, StringComparison.Ordinal);
            Assert.DoesNotContain("WARN - native: the deepest path below this build directory", second.StandardError, StringComparison.Ordinal);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// A real configure of a C++ project whose C only a dependency's project() enables, as googletest's
    /// does, by the CMake on this machine: its answer names C's compiler with no id, and C is identified
    /// from CMake's own record of it - named on the leg's line, and held to the toolchain's compilerId,
    /// a matching one building and another failing the leg, never leaving it unwitnessed. A test of the
    /// build it did not make identifies C the same way, from a record the answer ties to it. After a
    /// configure since that identified C again and failed - writing no answer, and leaving a record dated
    /// after the last - C is unwitnessed where CMake's error index says a configure failed since, and
    /// named from the record where CMake before 4 leaves nothing to say so, the record naming the
    /// compiler the answer names; after one that identified another program for C, nothing ties the
    /// record to the build, and C is unwitnessed rather than named from it. Never by the record's date.
    /// Skipped where this machine has no CMake, no Ninja, or no C or C++ compiler - and failed there instead, where it
    /// says it is meant to hold every build tool (<see cref="BuildTools"/>).
    /// </summary>
    [Fact]
    public async Task ALanguageOnlyADependencyEnables_IsIdentified_AndHeldToTheToolchain()
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var (c, cxx) = OperatingSystem.IsWindows() ? ("gcc", "g++") : ("cc", "c++");
        BuildTools.Need(harness.ProcessRunner, "a real configure", "cmake", "ninja", c, cxx);

        var programs = new[] { "cmake", "ninja", c, cxx }.ToDictionary(program => program, harness.ProcessRunner.FindExecutable);

        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        // Real builds, which a build system orders by the times of the files they write: on a clock that
        // steps, what they do proves nothing here. Watched from before the files they build from are written.
        using var clock = new ClockWatch();

        HarnessConfig Config(params (string Language, string Id)[] declared)
        {
            var toolchain = new ToolchainConfig { Platforms = [platform.PlatformKey], Generator = "Ninja", Env = { ["CC"] = c, ["CXX"] = cxx } };

            foreach (var (language, id) in declared)
            {
                toolchain.CompilerId[language] = id;
            }

            return new HarnessConfig
            {
                Toolchains = { ["cc"] = toolchain },
                BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
                Projects =
                {
                    new ProjectConfig
                    {
                        Name = "app",
                        Type = "cmake",
                        Path = ".",
                        BuildOutputs = [BuildOutput.Keyed([new("windows", "probe.exe"), new("all", "probe")])],
                        Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                    },
                },
                Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            };
        }

        async Task<(JsonElement Leg, string Said, string Records)> RunAsync(params string[] command)
        {
            var result = await CliRunner.RunAsync([.. command, "--legs", "native", "--json", "-C", temp.Path], token);

            using var document = JsonDocument.Parse(result.StandardOutput);

            return (Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray()).Clone(), result.StandardError, RecordsOf(result));
        }

        Task<(JsonElement Leg, string Said, string Records)> BuildAsync() => RunAsync("build");

        // A configure of the leg's directory from outside the harness, run with --fresh so it identifies C
        // again - with cCompiler - and failing after the dependency enabled C. Returns the error index CMake
        // left in place of an answer, by file name, as CMake 4 does; or null where it left none.
        async Task<string?> FailedConfigureAsync(string cCompiler)
        {
            var directory = temp.Combine("build", $"{platform.Processor}-cc-debug");
            var failed = await harness.ProcessRunner.RunAsync(
                new ProcessRequest
                {
                    FileName = programs["cmake"]!,
                    Arguments = ["--fresh", "-S", temp.Path, "-B", directory, "-G", "Ninja", $"-DCMAKE_MAKE_PROGRAM={programs["ninja"]}", "-DFAIL=ON"],
                    Environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["CC"] = cCompiler, ["CXX"] = cxx },
                    AppendToPath = [.. programs.Values.Select(found => Path.GetDirectoryName(found)!).Distinct(StringComparer.Ordinal)],
                },
                token);

            Assert.True(failed.ExitCode != 0, $"the configure meant to fail passed: {failed.StandardOutput}");

            return Directory.EnumerateFiles(Path.Combine(directory, ".cmake", "api", "v1", "reply"), "error-*.json")
                .Select(Path.GetFileName)
                .Order(StringComparer.Ordinal)
                .LastOrDefault();
        }

        await harness.InitializeHarnessAsync(temp.Path, token, Config());

        // FAIL, never set by a leg, fails a configure only after the dependency has enabled C.
        temp.WriteFile(
            "CMakeLists.txt",
            "cmake_minimum_required(VERSION 3.20)\nproject(probe CXX)\nadd_subdirectory(dependency)\n"
            + "if(FAIL)\n  message(FATAL_ERROR \"failing after C was identified\")\nendif()\n"
            + "add_executable(probe main.cpp)\ntarget_link_libraries(probe PRIVATE dependency)\n");
        temp.WriteFile("main.cpp", "int dependency();\nint main() { return dependency(); }\n");

        // Declares C and C++ by leaving its languages out, as googletest's project() does, and compiles no C.
        temp.WriteFile(Path.Combine("dependency", "CMakeLists.txt"), "project(dependency)\nadd_library(dependency STATIC dependency.cpp)\n");
        temp.WriteFile(Path.Combine("dependency", "dependency.cpp"), "int dependency() { return 0; }\n");

        try
        {
            var (named, said, _) = await BuildAsync();
            var ids = named.GetProperty("compilers").EnumerateArray().ToDictionary(
                compiler => compiler.GetProperty("language").GetString()!,
                compiler => compiler.GetProperty("id").GetString()!);

            Assert.Equal("passed", named.GetProperty("verdict").GetString());
            Assert.True(ids.ContainsKey("C") && ids.ContainsKey("CXX"), said);
            Assert.All(ids.Values, id => Assert.False(string.IsNullOrEmpty(id)));

            harness.WriteConfig(temp.Path, Config(("C", ids["C"]), ("CXX", ids["CXX"])));

            var (held, heldSaid, heldRecords) = await BuildAsync();

            // Both compilers asked their versions as this build began, C's from the record alone: each is
            // what CMake recorded, or the build would have started from clean for a compiler that never changed.
            AskedEachCompiler(heldSaid, heldRecords, "native", "C", "CXX");

            Assert.True(held.GetProperty("verdict").GetString() == "passed", heldSaid);

            harness.WriteConfig(temp.Path, Config(("C", "NoSuchCompiler"), ("CXX", ids["CXX"])));

            var (contradicted, _, _) = await BuildAsync();

            Assert.Equal("failed", contradicted.GetProperty("verdict").GetString());
            Assert.Contains($"C with {ids["C"]}", contradicted.GetProperty("detail").GetString(), StringComparison.Ordinal);
            Assert.Contains("not NoSuchCompiler", contradicted.GetProperty("detail").GetString(), StringComparison.Ordinal);

            harness.WriteConfig(temp.Path, Config(("C", ids["C"]), ("CXX", ids["CXX"])));

            // A record marked unordered starts the next build from clean, and a test run that built first
            // says so on its leg's line, as a build's own line does.
            Unordered(temp.Combine("build", $"{platform.Processor}-cc-debug"));

            var (rebuiltTested, rebuiltSaid, _) = await RunAsync("test");

            Assert.True(rebuiltTested.GetProperty("verdict").GetString() == "passed", rebuiltSaid);
            Assert.Contains(
                rebuiltTested.GetProperty("timingNotes").EnumerateArray(),
                note => note.GetString()!.StartsWith("rebuilt from clean: an unordered build", StringComparison.Ordinal));

            var (tested, testedSaid, _) = await RunAsync("test", "--no-build");

            Assert.True(tested.GetProperty("verdict").GetString() == "passed", testedSaid);
            Assert.Contains(
                tested.GetProperty("compilers").EnumerateArray(),
                compiler => compiler.GetProperty("language").GetString() == "C" && compiler.GetProperty("id").GetString() == ids["C"]);

            // The same compiler identified again by a configure that failed: from CMake 4 its error index says one
            // failed since the answer, and before it nothing does, so the record is read as it names that compiler.
            var failedSince = await FailedConfigureAsync(c);
            var (again, againSaid, _) = await RunAsync("test", "--no-build");

            if (failedSince is { } errorIndex)
            {
                Assert.True(again.GetProperty("verdict").GetString() == "unwitnessed", againSaid);
                Assert.Contains($"CMake's '{errorIndex}' says one failed since that answer", again.GetProperty("detail").GetString(), StringComparison.Ordinal);
            }
            else
            {
                Assert.True(again.GetProperty("verdict").GetString() == "passed", againSaid);
            }

            // Another program identified for C by one: whatever CMake leaves of the configure, nothing ties the record to the answer.
            await FailedConfigureAsync(cxx);

            var (untied, untiedSaid, _) = await RunAsync("test", "--no-build");

            Assert.True(untied.GetProperty("verdict").GetString() == "unwitnessed", untiedSaid);
            Assert.Contains("CMake identified none for it", untied.GetProperty("detail").GetString(), StringComparison.Ordinal);
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// An msvc leg builds from a plain shell: the environment Visual Studio sets up is set up for it
    /// on this machine, CMake configures its C and its C++ with MSVC - which its toolchain's compilerId
    /// holds it to - and its line names both. A unit including nothing, one including only a header
    /// ninja takes for the system's, and one including a header beside it pass the dependency check;
    /// so do the units built from a precompiled header under /Yu and /FI, in C and in C++ - one holding
    /// only a compile-time assertion, one using a header it never includes itself, one including a
    /// header the precompiled header holds and guards, which cl never opens again - each rebuilt through
    /// the object compiling the precompiled header, which records what it holds. The check reads every
    /// object and excuses exactly those that include nothing ninja keeps or are rebuilt that way. Skipped
    /// where this machine has no Visual Studio with the C++ build tools, or no CMake or Ninja in the
    /// environment it sets up - and failed there instead, where a Windows machine says it is meant to
    /// hold every build tool (<see cref="BuildTools"/>).
    /// </summary>
    [Fact]
    public async Task AnMsvcLeg_BuildsFromAPlainShell_InTheEnvironmentVisualStudioSetsUp()
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var visualStudio = new DeveloperEnvironmentConfig { Kind = DeveloperEnvironmentKinds.VisualStudio };
        var probe = new DeveloperEnvironmentProbe(platform, harness.ProcessRunner);

        var found = await probe.CheckAsync(visualStudio, token);

        if (!found.CanSetUp)
        {
            BuildTools.Lacks($"This machine has no Visual Studio with the C++ build tools: {found.Reason}", couldHold: OperatingSystem.IsWindows());
        }

        var setUp = await new DeveloperEnvironmentProvider(platform, harness.ProcessRunner, harness.FileSystem, harness.Output)
            .SetUpAsync("visualStudio", found, platform.Processor, new Dictionary<string, string>(), token);

        Assert.Null(setUp.Failure);

        var path = setUp.Environment.TryGetValue("PATH", out var set) ? set : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var reachable = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);

        if (!new[] { "cmake.exe", "ninja.exe" }.All(program => reachable.Any(directory => File.Exists(Path.Combine(directory, program)))))
        {
            BuildTools.Lacks("This machine has no CMake or no Ninja in the environment Visual Studio sets up.");
        }

        using var temp = new TempDirectory();

        // Real builds, which a build system orders by the times of the files they write, and records
        // held to answers by theirs: on a clock that steps, what they do proves nothing here. Watched
        // from before the files they build from are written.
        using var clock = new ClockWatch();

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            DeveloperEnvironments = { ["visualStudio"] = visualStudio },
            Toolchains =
            {
                ["msvc"] = new ToolchainConfig
                {
                    Platforms = ["windows"],
                    Generator = "Ninja",
                    Env = { ["CC"] = "cl", ["CXX"] = "cl" },
                    CompilerId = { ["C"] = "MSVC", ["CXX"] = "MSVC" },
                    DeveloperEnvironment = "visualStudio",
                },
            },
            BuildConfigs = { ["debug"] = new BuildConfiguration { CmakeBuildType = "Debug" } },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    BuildOutputs = [BuildOutput.Keyed([new("windows", "probe.exe")])],

                    // Passes only where the test starts in what Visual Studio set up.
                    Test = new TestConfig
                    {
                        All = new TestInvocation { Runner = "cmd", Args = ["/d", "/c", "if defined VCToolsVersion echo set up"], SuccessPattern = "^set up" },
                    },
                },
            },
            Legs = { ["msvc"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "msvc" } },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig
                {
                    Phases = [new RunnerPhase { Name = "env", Command = ["cmd", "/d", "/c", "if not defined VCToolsVersion exit 1"] }],
                },
            },
        });

        temp.WriteFile(
            "CMakeLists.txt",
            "cmake_minimum_required(VERSION 3.20)\nproject(probe C CXX)\nadd_executable(probe main.c system.c local.c)\n"
            + "add_library(precompiled STATIC stub.c uses.c holds.c)\ntarget_precompile_headers(precompiled PRIVATE probe.h held.h)\n"
            + "add_library(precompiledcxx STATIC stub.cpp holds.cpp)\ntarget_precompile_headers(precompiledcxx PRIVATE held.h)\n");
        temp.WriteFile("main.c", "int main(void) { return 0; }\n");
        temp.WriteFile("system.c", "#include <stdio.h>\nint system_only(void) { return printf(\"\"); }\n");
        temp.WriteFile("local.c", "#include \"probe.h\"\nint local(void) { return PROBE; }\n");
        temp.WriteFile("probe.h", "#define PROBE 0\n");
        temp.WriteFile("held.h", "#pragma once\n#define HELD 1\n");

        // None records a header: cl reads the precompiled header compiled, and never opens again a
        // header it holds and guards that the unit includes itself. For C++, CMake holds the
        // precompiled header's includes under #ifdef __cplusplus.
        temp.WriteFile("stub.c", "typedef char int_is_wide_enough[sizeof(int) >= 2 ? 1 : -1];\n");
        temp.WriteFile("uses.c", "int uses(void) { return PROBE; }\n");
        temp.WriteFile("holds.c", "#include \"held.h\"\nint holds(void) { return HELD; }\n");
        temp.WriteFile("stub.cpp", "static_assert(sizeof(int) >= 2, \"int is wide enough\");\n");
        temp.WriteFile("holds.cpp", "#include \"held.h\"\nint holds_too() { return HELD; }\n");

        try
        {
            var build = await CliRunner.RunAsync(["build", "--legs", "msvc", "--json", "-C", temp.Path], token);

            Assert.True(build.ExitCode == HarnessExit.Success, build.StandardError);

            using var document = JsonDocument.Parse(build.StandardOutput);
            var leg = Assert.Single(document.RootElement.GetProperty("legs").EnumerateArray());
            var environment = leg.GetProperty("developerEnvironment");

            Assert.Equal("passed", leg.GetProperty("verdict").GetString());

            // Ten objects: three of the program, and a precompiled header's object and its units in each
            // language. Seven record nothing, each legitimately: two include nothing ninja keeps, and five
            // are built from a precompiled header.
            Assert.Equal("10 object(s) read, 7 excused", leg.GetProperty("detail").GetString());
            Assert.All(leg.GetProperty("compilers").EnumerateArray(), compiler => Assert.Equal("MSVC", compiler.GetProperty("id").GetString()));
            Assert.Equal(["C", "CXX"], leg.GetProperty("compilers").EnumerateArray().Select(compiler => compiler.GetProperty("language").GetString()).Order());
            Assert.Equal("visualStudio", environment.GetProperty("name").GetString());
            Assert.Equal(found.InstallationPath, environment.GetProperty("installationPath").GetString());
            Assert.Equal(setUp.Fact!.ToolsVersion, environment.GetProperty("toolsVersion").GetString());
            Assert.Contains("developer environment: visualStudio (Visual Studio ", build.StandardError, StringComparison.Ordinal);

            // Built again, each compiler asked its version first through the environment Visual Studio sets
            // up: what cl says, put together as CMake puts MSVC's version together, is what CMake recorded, or
            // the directory would start from clean for a compiler that never changed.
            var again = await CliRunner.RunAsync(["build", "--legs", "msvc", "--json", "-C", temp.Path], token);

            Assert.True(again.ExitCode == HarnessExit.Success, again.StandardError);
            AskedEachCompiler(again.StandardError, RecordsOf(again), "msvc", "C", "CXX");

            foreach (var command in new[] { new[] { "test", "--no-build" }, ["run", "probe"] })
            {
                var result = await CliRunner.RunAsync([.. command, "--legs", "msvc", "--json", "-C", temp.Path], token);

                Assert.True(result.ExitCode == HarnessExit.Success, result.StandardError);

                using var answered = JsonDocument.Parse(result.StandardOutput);
                var line = Assert.Single(answered.RootElement.GetProperty("legs").EnumerateArray());

                Assert.Equal("passed", line.GetProperty("verdict").GetString());
                Assert.Equal("visualStudio", line.GetProperty("developerEnvironment").GetProperty("name").GetString());
            }
        }
        catch (Exception ex) when (clock.Explains(ex))
        {
            Assert.Skip($"Its builds did not run on an honest clock - {clock.Seen}: {ex.Message}");
        }
    }

    /// <summary>
    /// Each command asks a host only for what it will start there. A leg whose compiler no machine
    /// has is turned away by the survey and by a build, and still tested when the build is skipped;
    /// a runner whose step starts a program nothing has is turned away before it starts, with the
    /// program named, rather than failing halfway through.
    /// </summary>
    [Fact]
    public async Task EachCommand_TurnsALegAwayOnlyForWhatItWillStart()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var compiler = "rh-missing-cc-" + Guid.NewGuid().ToString("N")[..8];
        var tool = "rh-missing-tool-" + Guid.NewGuid().ToString("N")[..8];

        var toolchain = new ToolchainConfig { Platforms = [platform.PlatformKey] };
        toolchain.Env["CC"] = compiler;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            Toolchains = { ["cc"] = toolchain },
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Projects =
            {
                new ProjectConfig
                {
                    Name = "app",
                    Type = "cmake",
                    Path = ".",
                    Test = new TestConfig { All = new TestInvocation { Runner = "dotnet", Args = ["--version"], SuccessPattern = @"^\d+\.\d+" } },
                },
            },
            Tools = { new ToolConfig { Name = tool } },
            Legs = { ["compiled"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug", Toolchain = "cc" } },
            PredefinedRunners = { ["absent"] = new RunnerConfig { Action = "absent/absent.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "absent", "absent.yml"),
            $"name: absent\nsteps:\n  - name: use\n    run: {tool} --version\n");

        var legs = await CliRunner.RunAsync(["legs", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, legs.ExitCode);
        Assert.Contains($"'{compiler}'", legs.StandardError, StringComparison.Ordinal);

        var build = await CliRunner.RunAsync(["build", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, build.ExitCode);
        Assert.Contains($"'{compiler}'", build.StandardError, StringComparison.Ordinal);

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, test.ExitCode);

        var run = await CliRunner.RunAsync(["run", "absent", "--legs", "compiled", "-C", temp.Path], token);

        Assert.Equal(LegsExit.Unavailable, run.ExitCode);
        Assert.Contains($"'{tool}' is not installed there", run.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A runs directory nobody can write - an earlier run under sudo left it to root - refuses the
    /// run, exit 13, naming the file it could not write, and still answers --json with the ledger.
    /// Escaping as an error, it read as exit 70, a defect in this tool.
    /// </summary>
    [Fact]
    public async Task ARunsDirectoryNobodyCanWrite_RefusesTheRun_NamingIt()
    {
        using var temp = new TempDirectory();
        var token = TestContext.Current.CancellationToken;

        await PrepareRunnerAsync(temp);

        var runs = temp.Combine(".harness-config", "runs");

        if (Directory.Exists(runs))
        {
            Directory.Delete(runs, recursive: true);
        }

        await File.WriteAllTextAsync(runs, "not a directory", token);

        var result = await CliRunner.RunAsync(["run", "probe", "--legs", "native", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Refused, result.ExitCode);
        Assert.Contains("could not be written", result.StandardError, StringComparison.Ordinal);

        using var document = JsonDocument.Parse(result.StandardOutput);

        Assert.Equal(HarnessExit.Refused, document.RootElement.GetProperty("exitCode").GetInt32());
    }

    /// <summary>
    /// A host's own environment reaches what a leg starts - its test runner and a runner's steps -
    /// beneath what each declares itself. A host running a leg another machine dispatched to it takes
    /// the section that machine names it by, not 'local', which in the configuration the two share is
    /// the machine that dispatched it; and a host that is named in no spelling a host has is a usage
    /// error, never a guess.
    /// </summary>
    [Fact]
    public async Task TheHostsEnvironment_ReachesWhatALegStarts_AsTheHostItRunsAs()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;

        // The test child prints the variable it is given; its own mode is set the way every other
        // variable here is, so what it prints is what the leg's environment held.
        static TestInvocation Printing(string variable, string expected, params (string Name, string Value)[] env)
        {
            var environment = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "print-env" };

            foreach (var (name, value) in env)
            {
                environment[name] = value;
            }

            return new TestInvocation
            {
                Runner = TestHost.DotnetExecutable,
                Args = ["exec", TestHost.AssemblyPath, variable],
                SuccessPattern = $"^{expected}$",
                Env = environment,
            };
        }

        LegConfig Leg(TestInvocation invocation) => new()
        {
            Os = platform.PlatformKey,
            Processor = platform.Processor,
            Config = "debug",
            Test = new TestConfig { All = invocation },
        };

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            SshItems = { "pi" },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig { Env = { ["RH_HOST_VAR"] = "from-local", ["RH_ORDER"] = "host" } },
                Ssh = { ["pi"] = new SshHostConfig { RepositoryPath = "~/repo", Env = { ["RH_HOST_VAR"] = "from-pi" } } },
            },
            Legs =
            {
                ["host-var"] = Leg(Printing("RH_HOST_VAR", "from-local")),
                ["invocation-wins"] = Leg(Printing("RH_ORDER", "invocation", ("RH_ORDER", "invocation"))),
                ["sent-here"] = Leg(Printing("RH_HOST_VAR", "from-pi")),
            },
            PredefinedRunners =
            {
                ["print"] = new RunnerConfig
                {
                    Phases =
                    [
                        new RunnerPhase
                        {
                            Name = "print",
                            Command = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, "RH_HOST_VAR"],
                            Env = { [TestHost.ChildModeVariable] = "print-env" },
                            SuccessPattern = "^from-local$",
                        },
                    ],
                },
            },
        });

        // One at a time: legs of one variant would share a build directory, which a run refuses.
        foreach (var leg in new[] { "host-var", "invocation-wins" })
        {
            var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", leg, "--json", "-C", temp.Path], token);

            Assert.Equal(HarnessExit.Success, test.ExitCode);
        }

        var sent = await CliRunner.RunAsync(["test", "--no-build", "--legs", "sent-here", "--json", RemoteLegRunner.HereOption, "ssh pi", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, sent.ExitCode);

        var run = await CliRunner.RunAsync(["run", "print", "--legs", "host-var", "--json", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.Success, run.ExitCode);

        var misnamed = await CliRunner.RunAsync(["test", "--no-build", "--legs", "host-var", RemoteLegRunner.HereOption, "pi", "-C", temp.Path], token);

        Assert.Equal(HarnessExit.UsageError, misnamed.ExitCode);
        Assert.Contains("names a host as 'local', 'wsl <distribution>' or 'ssh <name>'", misnamed.StandardError, StringComparison.Ordinal);
    }

    /// <summary>
    /// A host's keepAwake is started while a leg's own work runs, filled in with the DssHarness
    /// process running it. Here the command writes what it was given, and the leg's test waits for
    /// that file before it can pass: the command ran during the work, and was given a process - the
    /// CLI's own, which runs apart from this test and whose id only it knows. Starting, it ends the
    /// hold standing between commands: here one kept in the user's data the CLI was given, so the
    /// hold the machine's own user may have standing is never the one it ends.
    /// </summary>
    [Fact]
    public async Task AHostsKeepAwake_RunsDuringALegsWork_GivenTheProcessRunningIt_AndEndsTheHoldBetweenCommands()
    {
        using var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var platform = harness.Platform;
        var token = TestContext.Current.CancellationToken;
        var awake = temp.Combine("awake.txt");
        var (environment, holds) = OwnUserData(temp);

        new HoldAwakeStore(new PhysicalFileSystem(FilePermissionsFactory.Create()), holds)
            .Write(new HoldAwakeState("0123456789abcdef", DateTimeOffset.UtcNow.AddHours(1), [], [], []));

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Hosts = new HostsConfig
            {
                Local = new LocalHostConfig
                {
                    KeepAwake = [TestHost.DotnetExecutable, "exec", TestHost.AssemblyPath, awake, "{pid}"],
                    Env = { [TestHost.ChildModeVariable] = "write-file" },
                },
            },
            Legs =
            {
                ["native"] = new LegConfig
                {
                    Os = platform.PlatformKey,
                    Processor = platform.Processor,
                    Config = "debug",
                    Test = new TestConfig
                    {
                        All = new TestInvocation
                        {
                            Runner = TestHost.DotnetExecutable,
                            Args = ["exec", TestHost.AssemblyPath, awake],
                            Env = new Dictionary<string, string> { [TestHost.ChildModeVariable] = "stream" },
                            SuccessPattern = "second",
                        },
                    },
                },
            },
        });

        var test = await CliRunner.RunAsync(["test", "--no-build", "--legs", "native", "-C", temp.Path], token, environment: environment);

        Assert.Equal(HarnessExit.Success, test.ExitCode);
        Assert.True(
            int.TryParse(await File.ReadAllTextAsync(awake, token), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0,
            "keepAwake was not given the process running the leg");
        Assert.False(File.Exists(holds), "the hold standing in the user's data the CLI was given was not ended");
    }

    /// <summary>The operating system of the leg <see cref="PrepareRunnerAsync"/> declares that no host provides.</summary>
    private static string ElsewhereOs
        => new HarnessFactory().Platform.PlatformKey == PlatformNames.Linux ? PlatformNames.MacOs : PlatformNames.Linux;

    /// <summary>
    /// A repository with one leg this machine can run and one no host can, and a runner that does
    /// something trivial on whichever of them runs.
    /// </summary>
    private static async Task PrepareRunnerAsync(TempDirectory temp, bool heavy = false)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, TestContext.Current.CancellationToken, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },

            // Where the runner is heavy, its machine admits it: one slot, so a record left holding it would show, and a limit
            // of 100%, which any reading short of a full machine is below. This machine's memory in use is whatever it is:
            // above the built-in limit (AdmissionSettings.DefaultMaxMemoryPercent), the leg would wait for it to fall, and
            // the test with it.
            Defaults = heavy ? new HarnessDefaults { Admission = new AdmissionSettings { HeavyLegs = 1, MaxMemoryPercent = 100 } } : new HarnessDefaults(),
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs =
            {
                ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" },

                // An operating system no declared host provides: this machine is the only host.
                ["elsewhere"] = new LegConfig
                {
                    Os = ElsewhereOs,
                    Processor = platform.Processor,
                    Config = "debug",
                },
            },
            PredefinedRunners =
            {
                ["probe"] = new RunnerConfig { Action = "probe/probe.yml", Heavy = heavy ? true : null },
            },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            "name: probe\nsteps:\n  - name: version\n    run: dotnet --version\n");
    }

    /// <summary>
    /// A repository with one leg this machine can run, and a runner whose step names an input of its
    /// action that has no value: a run of it is refused inside the leg, before any leg reaches a verdict.
    /// </summary>
    private static async Task PrepareInputWithNoValueAsync(TempDirectory temp, CancellationToken token)
    {
        var harness = new HarnessFactory();
        var platform = harness.Platform;

        await harness.InitializeHarnessAsync(temp.Path, token, new HarnessConfig
        {
            BuildConfigs = { ["debug"] = new BuildConfiguration() },
            Tools = { new ToolConfig { Name = "dotnet" } },
            Legs = { ["native"] = new LegConfig { Os = platform.PlatformKey, Processor = platform.Processor, Config = "debug" } },
            PredefinedRunners = { ["probe"] = new RunnerConfig { Action = "probe/probe.yml" } },
        });

        temp.WriteFile(
            Path.Combine(".harness-config", "runner", "actions", "probe", "probe.yml"),
            """
            name: probe
            inputs:
              area:
                description: where the rows are staged
            steps:
              - name: stage
                run: |
                  dotnet {area}
            """);
    }

    /// <summary>An initialised repository whose path budget any temporary directory fits.</summary>
    private static Task PrepareRepositoryAsync(TempDirectory temp)
        => new HarnessFactory().InitializeHarnessAsync(
            temp.Path,
            TestContext.Current.CancellationToken,
            new HarnessConfig { Worktrees = new WorktreeSettings { PathBudgetReserve = 5, PathBudgetMargin = 2 } });

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex BareVersionPattern();

    [GeneratedRegex("created worktree '(?<name>[a-z0-9]+)'")]
    private static partial Regex CreatedNamePattern();
}
