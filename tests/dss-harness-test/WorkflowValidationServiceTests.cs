using DssHarness.Core.Configuration;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Runners;

namespace DssHarness.Tests;

/// <summary>
/// What only the repository can answer about the workflows it declares: that each definition file is there, reads,
/// holds, and names files that are there - the one command that reads a workflow's whole definition and its files
/// together, changing nothing.
/// </summary>
public sealed class WorkflowValidationServiceTests
{
    /// <summary>
    /// The default workflow, initialized into a repository and declared in its config, passes its check: the one line
    /// says what it is, and the outcome says every one checked.
    /// </summary>
    [Fact]
    public async Task TheDefaultWorkflow_InitializedIntoARepository_PassesItsCheck()
    {
        using var repository = await RepositoryAsync(config: null);
        var cancellationToken = TestContext.Current.CancellationToken;

        var report = await Service(repository).CheckAsync(repository.Path, named: null, cancellationToken);

        Assert.True(report.Passed);
        Assert.Equal("ok .harness-config/workflows/default/default.yml (94 steps, 31 reasoning)", Assert.Single(report.Lines));

        var outcome = WorkflowValidationReports.Render(report, json: false);

        Assert.Equal(HarnessExit.Success, outcome.ExitCode);
        Assert.Equal("1 workflow(s) checked, all valid", outcome.Message);
    }

    /// <summary>A workflow whose file is not there is said so, naming where it was looked for and what writes it.</summary>
    [Fact]
    public async Task AWorkflowWhoseFileIsNotThere_IsSaidSo()
    {
        using var repository = await RepositoryAsync(config: null);
        File.Delete(Path.Combine(repository.Path, ".harness-config", "workflows", "default", "default.yml"));

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            ".harness-config/workflows/default/default.yml is not there; a workflow is defined by that file, written by 'dssharness init' where it is the default one",
            Assert.Single(report.Lines));

        Assert.Equal(HarnessExit.Refused, WorkflowValidationReports.Render(report, json: false).ExitCode);
    }

    /// <summary>A workflow nobody declared is a usage error, naming what is declared and where the file would be.</summary>
    [Fact]
    public async Task AWorkflowNobodyDeclared_IsAUsageError()
    {
        using var repository = await RepositoryAsync(config: null);
        var refusal = await Assert.ThrowsAsync<HarnessException>(
            () => Service(repository).CheckAsync(repository.Path, "release", TestContext.Current.CancellationToken));

        Assert.Equal(HarnessExit.UsageError, refusal.ExitCode);
        Assert.Equal(
            "'release' is not declared under workflows. Declared: default. A workflow is defined by '.harness-config/workflows/release/release.yml'.",
            refusal.Message);
    }

    /// <summary>
    /// A file that names itself what its directory and its file are not is refused: a copied directory must not pass as
    /// the workflow it was copied from.
    /// </summary>
    [Fact]
    public async Task AFileNamingAnotherName_IsRefused()
    {
        using var repository = await RepositoryAsync(config: null);
        var file = Path.Combine(repository.Path, ".harness-config", "workflows", "default", "default.yml");
        var read = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);

        await File.WriteAllTextAsync(
            file,
            read.Replace("name: default", "name: release", StringComparison.Ordinal),
            TestContext.Current.CancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, named: null, TestContext.Current.CancellationToken);

        Assert.False(report.Passed);
        Assert.StartsWith(
            ".harness-config/workflows/default/default.yml names itself 'release'",
            Assert.Single(report.Lines),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A file the definition names that is not there is said, beside the definition that names it - its agent, its
    /// prompt, its schema - and a runner or a leg no configuration declares is said the same way.
    /// </summary>
    [Fact]
    public async Task FilesTheDefinitionNames_AndRunnersAndLegsNobodyDeclares_AreEachSaid()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "small.yml"),
            """
            name: small
            steps:
              - name: build
                type: build
                legs: [ghost]
              - name: make
                type: create-agent
                agent: implementer
              - name: work
                type: reason
                agent: worker
                prompt: prompts/work.md
                outputs: schemas/work.json
                dependsOn: [make]
              - name: corpus
                type: run
                runner: missing-runner
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            [
                ".harness-config/workflows/small/small.yml: step 'build' names the leg 'ghost', which 'ghost' is not declared under legs. Nothing is declared there.",
                ".harness-config/workflows/small/small.yml: 'agents/implementer.md', step 'make''s agent, is not at '.harness-config/workflows/small/agents/implementer.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'agents/worker.md', step 'work''s agent, is not at '.harness-config/workflows/small/agents/worker.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'prompts/work.md', step 'work''s prompt, is not at '.harness-config/workflows/small/prompts/work.md'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: 'schemas/work.json', step 'work''s outputs, is not at '.harness-config/workflows/small/schemas/work.json'; write it there, beside the definition that names it",
                ".harness-config/workflows/small/small.yml: step 'corpus' runs 'missing-runner', which 'missing-runner' is not declared under predefinedRunners. Nothing is declared there.",
            ],
            report.Lines);
    }

    /// <summary>
    /// A definition whose file names a path leaving the workflow's directory is refused: what a reviewer reads there is
    /// what the workflow runs, as an action's files are to its own directory.
    /// </summary>
    [Fact]
    public async Task APathLeavingTheWorkflowsOwnDirectory_IsRefused()
    {
        using var repository = await RepositoryAsync(new HarnessConfig { Workflows = ["small"] });
        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");
        Directory.CreateDirectory(Path.Combine(directory, "agents"));
        Directory.CreateDirectory(Path.Combine(directory, "schemas"));
        await File.WriteAllTextAsync(Path.Combine(directory, "agents", "implementer.md"), "works\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(directory, "schemas", "work.json"), "{}\n", cancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "small.yml"),
            """
            name: small
            steps:
              - name: work
                type: reason
                agent: implementer
                prompt: ../default/prompts/work.md
                outputs: schemas/work.json
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        var line = Assert.Single(report.Lines);

        Assert.StartsWith(".harness-config/workflows/small/small.yml", line, StringComparison.Ordinal);
        Assert.Contains("leaves the workflow's directory", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// A run step whose runner writes the tree names no 'in': it would work in the main tree, which a runner that
    /// changes files never does.
    /// </summary>
    [Fact]
    public async Task ARunStepWhoseRunnerWritesTheTree_NamesAnAgentToWorkIn()
    {
        using var repository = await RepositoryAsync(
            new HarnessConfig
            {
                Workflows = ["small"],
                PredefinedRunners = new Dictionary<string, RunnerConfig>(StringComparer.OrdinalIgnoreCase)
                {
                    ["formatter"] = new RunnerConfig
                    {
                        WritesTree = true,
                        Phases = [new RunnerPhase { Name = "format", Command = ["tool"] }],
                    },
                },
            });

        var cancellationToken = TestContext.Current.CancellationToken;
        var directory = Path.Combine(repository.Path, ".harness-config", "workflows", "small");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(
            Path.Combine(directory, "small.yml"),
            """
            name: small
            steps:
              - name: format
                type: run
                runner: formatter
            """,
            cancellationToken);

        var report = await Service(repository).CheckAsync(repository.Path, "small", cancellationToken);

        Assert.False(report.Passed);
        Assert.Equal(
            [".harness-config/workflows/small/small.yml: step 'format' runs 'formatter', which writes the tree, and names no 'in'; a runner that changes files works in an agent's worktree, never in the main tree"],
            report.Lines);
    }

    private static WorkflowValidationService Service(Repository repository)
        => new(repository.Harness.ContextLoader, repository.Harness.FileSystem, repository.Harness.Platform);

    /// <summary>
    /// A repository of one commit whose config declares the workflows asked for, with the default's files written where
    /// it is declared.
    /// </summary>
    private static async Task<Repository> RepositoryAsync(HarnessConfig? config)
    {
        var temp = new TempDirectory();
        var harness = new HarnessFactory();
        var cancellationToken = TestContext.Current.CancellationToken;

        temp.WriteFile("file.txt", "content\n");
        await harness.InitializeHarnessAsync(
            temp.Path,
            cancellationToken,
            config ?? new HarnessConfig { Workflows = [WorkflowTemplates.DefaultWorkflowName] });
        await harness.CommitAllAsync(temp.Path, "initial", cancellationToken);

        if (config is null || (config.Workflows ?? []).Contains(WorkflowTemplates.DefaultWorkflowName))
        {
            WorkflowTemplates.EnsureDefault(Path.Combine(temp.Path, ".harness-config", "workflows"), harness.FileSystem);
        }

        return new Repository(harness, temp);
    }

    /// <summary>The repository a check ran in, disposable with its tree.</summary>
    private sealed record Repository(HarnessFactory Harness, TempDirectory Tree) : IDisposable
    {
        public string Path => Tree.Path;

        public void Dispose() => Tree.Dispose();
    }
}
