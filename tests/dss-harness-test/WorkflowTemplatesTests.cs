using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DssHarness.Core.Agents;
using DssHarness.Core.Execution;
using DssHarness.Core.FileSystem;
using DssHarness.Core.Orchestration.Flow;
using DssHarness.Core.Results;
using DssHarness.Core.Platform;
using DssHarness.Core.Repository;
using DssHarness.Core.Schemas;
using NamedFiles = DssHarness.Core.Orchestration.Flow.WorkflowFiles;

namespace DssHarness.Tests;

/// <summary>
/// The implementation workflow this build ships: embedded whole under the workflow's own layout, read back and held to every
/// rule a repository's is, and written by init file by file, keeping every file there.
/// </summary>
public sealed class WorkflowTemplatesTests
{
    /// <summary>How this machine compares paths, as init hands it to the write-back of the shipped files.</summary>
    private static StringComparison PathComparison => new HostPlatform().PathComparison;

    /// <summary>
    /// The repair prompts start from the digest: mend's first instruction names the digest's first - the first error
    /// in the order the tool met it, the lines its log range names one read away in the log file the entry names, as
    /// that file numbers them - and says what a red entry holds of it, and diagnose and rediagnose say the same, so no
    /// model is told to search a log for what a file already holds, nor a directory for the file. The architecture says
    /// what the prompt says, in its own words, never a sentence no prompt holds.
    /// </summary>
    [Fact]
    public void TheRepairPrompts_StartFromTheDigestsFirst_NeverALogToSearch()
    {
        var mend = Prose.Words(WorkflowTemplates.TextOf("item-pipeline/prompts/mend.md")!);
        var diagnose = Prose.Words(WorkflowTemplates.TextOf("prompts/diagnose.md")!);
        var rediagnose = Prose.Words(WorkflowTemplates.TextOf("prompts/rediagnose.md")!);

        Assert.Contains("1. Start from the digest's `first`: it is the first thing that went wrong, in the order the tool met it. The lines its `log` names are one read away in the entry's `log` file", mend, StringComparison.Ordinal);
        Assert.Contains("Each line reaches you once: where the digest holds an error or a failure and the entry names its `log`, its `unparsed` holds the lines no parser held - the last ones, nearest the failure, `unparsedLeftOut` saying how many more there were - and the `tail` is empty; otherwise the `tail` holds the phase's last lines and `unparsed` those lines no parser held that the `tail` does not. The entry's `log` is the file that phase wrote, on this machine - a leg another machine ran has its log brought back here - and null only where it could not be brought back.", mend, StringComparison.Ordinal);
        Assert.Contains(
            "`mend.md` starts from the digest's `first`, the first thing that went wrong in the order the tool met it, the lines its `log` names one read away in the entry's `log` file; `diagnose.md` and `rediagnose.md` say the same,",
            Prose.Document("docs", "architecture.md"),
            StringComparison.Ordinal);
        Assert.Contains("1. Start from the digest's `first` - the first thing that went wrong, in the order the tool met it - and read the", diagnose, StringComparison.Ordinal);
        Assert.Contains("the lines a digest's `log` names are those of the entry's own `log` file, numbered as that file numbers them.", diagnose, StringComparison.Ordinal);
        Assert.Contains("each red entry's digest, its `first` the first thing that went wrong in the order the tool met it, the lines its `log` names those of the entry's own `log` file.", rediagnose, StringComparison.Ordinal);
        Assert.DoesNotContain("Read the output and find the first thing that went wrong", mend + diagnose, StringComparison.Ordinal);
    }

    /// <summary>
    /// This build's own implementation workflow holds: parsed and validated against every rule a repository's is held to -
    /// a build whose own workflow does not hold is a build that says so, never one that writes a definition it refuses.
    /// </summary>
    [Fact]
    public void ThisBuildsOwnImplementationWorkflow_HoldsAgainstEveryRule()
    {
        var definition = WorkflowTemplates.ShippedDefinition();

        Assert.Equal(WorkflowTemplates.ShippedWorkflowName, definition.Name);
        Assert.Equal(99, Count(definition.Steps));
        Assert.Equal(67, Count(definition.Recover!.Steps));
        Assert.Equal(62, Count([.. definition.Steps, .. definition.Recover.Steps], "reason"));
        Assert.Equal(6, definition.Inputs.Count);
        Assert.Contains(definition.Inputs, input => input.Name == "finalTests" && input.Default == "affected");
        Assert.Equal(["affected", "all"], definition.Inputs.Single(input => input.Name == "finalTests").Options);
        Assert.DoesNotContain(definition.Inputs, input => input.Name == "affectedTests");
    }

    /// <summary>
    /// The item pipeline is written once: the two places that build a work item - the rounds and the recovery - both
    /// use the one workflow, and the flow's own file holds no copy of its steps. The recovery's instance holds
    /// <c>redo</c> and <c>undo</c> too, held off there by the pipeline's <c>alternatives</c> input, which is why the
    /// recovery counts 48 where it counted 46 and the reason steps with it.
    /// </summary>
    [Fact]
    public void TheItemPipeline_IsOneWorkflow_UsedByTheRoundsAndTheRecoveryAlike()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var items = StepAt(definition.Steps, "rounds", "items");
        var remedies = StepAt(definition.Recover!.Steps, "remedies");

        Assert.NotNull(items.Use);
        Assert.NotNull(remedies.Use);
        Assert.Equal("./item-pipeline/item-pipeline.yml", items.Use.Source);
        Assert.Equal("./item-pipeline/item-pipeline.yml", remedies.Use.Source);
        Assert.Equal("implementation/item-pipeline", items.Use.Name);
        Assert.Equal("implementation/item-pipeline", remedies.Use.Name);

        // Each use fills the pipeline's inputs as the place it builds at reads them: the rounds with the implement
        // prompt, its alternatives held ready and failures given to the run's recovery; the recovery with the remedy
        // prompt, reading its own diagnosis, its alternatives held off and its failures asked, for it cannot call
        // itself.
        Assert.Equal("prompts/implement.md", items.With["implementPrompt"]);
        Assert.Equal("prompts/remedy.md", remedies.With["implementPrompt"]);
        Assert.Equal("recover", items.With["failTo"]);
        Assert.Equal("pause", remedies.With["failTo"]);
        Assert.Equal("true", items.With["alternatives"]);
        Assert.Equal("false", remedies.With["alternatives"]);
        Assert.DoesNotContain("diagnose", items.With.Keys);
        Assert.Equal("diagnose.latest", remedies.With["diagnose"]);

        // The rounds hand the pipeline the plan's summary their builder reads; the recovery hands it none, for its
        // remedy reads the diagnosis and a failure it puts right may come before anything is planned - a red build,
        // an issue the request's triage met that was there before the run - so the pipeline requires no plan of a use.
        Assert.Equal("planning.plan.latest.summary", items.With["planSummary"]);
        Assert.DoesNotContain("planSummary", remedies.With.Keys);
        Assert.False(items.Use.Inputs.Single(input => input.Name == "planSummary").Required);

        // The two uses name the one file: its fingerprint is one, and the steps the flow holds are the pipeline's,
        // placed at each use.
        Assert.Equal(items.Use.Fingerprint, remedies.Use.Fingerprint);
        Assert.Equal([.. items.Steps.Select(step => step.Name)], [.. remedies.Steps.Select(step => step.Name)]);

        // The recovery's redo and undo never run there: the pipeline's own gate holds them off.
        Assert.Equal("input.alternatives", StepAt(remedies.Steps, "redo").When);
        Assert.Null(StepAt(definition.Steps, "rounds", "items", "redo").DependsOn.SingleOrDefault());
    }

    /// <summary>
    /// The shipped flow makes its graph of a tree the repository's own compiler just built green (the user,
    /// 2026-10-23): the baseline is split in two - <c>build</c>, a build step that fails red and is taken by the
    /// recovery, and <c>baseline</c>, the test step it was - and <c>codebase</c> makes the graph after <c>build</c>,
    /// beside the baseline's tests, passed over where it cannot be made; every later step has it upstream, through
    /// <c>intake</c>. Which type is served what the twenty-run measurement of 2026-11-11 set (README measurement 24),
    /// by the user's ruling: the graph stays where it costs equal or less, and where what it bought - correction
    /// rounds avoided, each worth more than the step's own overhead - outweighs its cost. The steps that work on code
    /// keep the tool - the implementer and everything that resumes it, the six reviewers, the self-audit, the
    /// diagnosis, and the pre-existing-issue triage (which that measurement never served: raised outside every step's
    /// waits, it is given the run's graph as the recovery is) - the reviewers, the self-audit, the
    /// diagnosis and the steps that verify and fix starting their packs from their changes (<c>graphFrom: changes</c>);
    /// the request triage, whose work is text the map does not light, asks none (measured costlier with no quality
    /// gained); the plan audit and the item audit - the plan audit's agent on a later round's items - are given the tool
    /// (<c>graph: tools</c>): measured with none before the graph's <c>sites</c> was built, they search a class's sites,
    /// which <c>sites</c> answers, beside their own text search (spec 02, "Who may call it"); no step says
    /// <c>graphPromptTokens</c>, for a step with the tool is pushed no pack and no map by default - the measurement of the
    /// graph arm against <c>graph: off</c> found what was pushed all but unused - and a step at <c>map</c> is pushed both at
    /// their default sizes; the prioritiser and a review's scope keep the map (the scope's whole overhead
    /// is a fifth of a cent beside the review rounds it steers; the prioritiser is unmeasured, serving only a run with
    /// no request); the scribes ask none as before. The planner, which resumes itself and so may say a graph key of its
    /// own, keeps its default, the tool, as it was: the measurement found it costlier with the graph and did not move
    /// it. The round's mutation sweep drives only the arms with no recorded pass that still holds (the user's
    /// <c>--pending</c>).
    /// </summary>
    [Fact]
    public void TheShippedFlow_MakesItsGraphAfterTheBaselineBuild_AndServesEachStepTypeAsTheTableGivesIt()
    {
        var definition = WorkflowTemplates.ShippedDefinition();

        // The head: build, then the graph and the baseline's tests beside it, all reading the main tree only.
        var build = StepAt(definition.Steps, "build");

        Assert.Equal("build", build.Type);
        Assert.Equal(["orient"], build.DependsOn);
        Assert.Equal("orient.hasLegs", build.When);
        Assert.True(build.Alone);
        Assert.Equal(WorkflowStep.Recover, build.OnFailure);

        var codebase = StepAt(definition.Steps, "codebase");

        Assert.Equal("graphify-codebase", codebase.Type);
        Assert.Equal(["build"], codebase.DependsOn);
        Assert.Equal("continue", codebase.OnFailure);
        Assert.Null(codebase.When);

        var baseline = StepAt(definition.Steps, "baseline");

        Assert.Equal("test", baseline.Type);
        Assert.Equal(["build"], baseline.DependsOn);
        Assert.Equal("orient.hasLegs", baseline.When);
        Assert.Equal("never", baseline.FailOn);
        Assert.True(baseline.Skippable);
        Assert.True(baseline.Alone);
        Assert.Equal(WorkflowStep.Recover, baseline.OnFailure);

        Assert.Equal(["codebase", "baseline", "ci"], StepAt(definition.Steps, "intake").DependsOn);

        // The round's sweep drives only what no recorded pass still holds.
        Assert.True(StepAt(definition.Steps, "rounds", "mutations").Pending);

        // Each type of reason step, in the flow and in the recovery alike, as the table gives it.
        var expected = new (string Name, string? Graph, string? From)[]
        {
            ("priority", "map", null),
            ("triage", "off", null),
            ("plan", null, null),
            ("plan-update", null, null),
            ("plan-audit", "tools", null),
            ("item-audit", "tools", null),
            ("implement", null, null),
            ("unblock", null, null),
            ("redo", null, null),
            ("undo", null, null),
            ("mend", null, "changes"),
            ("settle", null, "changes"),
            ("cite", null, "changes"),
            ("close-class", null, "changes"),
            ("home-audit", null, "changes"),
            ("rearm-work", null, "changes"),
            ("review-scope", "map", "changes"),
            ("correctness", null, "changes"),
            ("errors", null, "changes"),
            ("tests", null, "changes"),
            ("types", null, "changes"),
            ("comments", null, "changes"),
            ("simplicity", null, "changes"),
            ("closure", "tools", "changes"),
            ("rework", null, "changes"),
            ("remend", null, "changes"),
            ("reclose-class", null, "changes"),
            ("rehome-audit", null, "changes"),
            ("resettle", null, "changes"),
            ("recite", null, "changes"),
            ("self-audit", null, "changes"),
            ("deferrals", "off", null),
            ("handoff", "off", null),
            ("notes-cite", null, null),
            ("diagnose", null, "changes"),
            ("redirect", null, "changes"),
            ("triage-pre-existing-issue", null, null),
        };

        var seen = new List<(string Path, string? Graph, string? From, int? Tokens)>();

        Walk(definition.Steps, parent: null);
        Walk(definition.Recover!.Steps, parent: "recover");

        // Every reason step of the flow is one the table names, and every site of a name is served the same.
        Assert.Equal(
            [.. expected.Select(row => (row.Name, row.Graph, row.From)).OrderBy(row => row.Item1, StringComparer.Ordinal)],
            [.. seen.GroupBy(row => row.Path.Split('.')[^1], StringComparer.Ordinal)
                .Select(group => group.First() with { })
                .Select(row => (row.Path.Split('.')[^1], row.Graph, row.From))
                .OrderBy(row => row.Item1, StringComparer.Ordinal)]);

        // A name the table gives that the flow no longer holds, or one it holds the table does not, is told by the
        // group assertion above only where both sides hold it; this holds the counts: the reviewers appear twice,
        // once in the flow and once in the recovery.
        Assert.Equal(2, seen.Count(row => row.Path.EndsWith(".correctness", StringComparison.Ordinal)));
        Assert.Equal(2, seen.Count(row => row.Path.EndsWith(".review-scope", StringComparison.Ordinal)));

        // No step of the flow says graphPromptTokens: a step with the tool is pushed no pack by default and asks the tool
        // for one where it needs it - the audits among them, whose sites searches are what it answers - and a step at map
        // is pushed its pack at the default size.
        Assert.DoesNotContain(seen, row => row.Tokens is not null);
        Assert.Equal(0, WorkflowStep.GraphPromptTokensOf("tools", StepAt(definition.Steps, "planning", "plan-audit").GraphPromptTokens));
        Assert.Equal(0, WorkflowStep.GraphPromptTokensOf("tools", StepAt(definition.Steps, "rounds", "item-audit").GraphPromptTokens));

        return;

        void Walk(IReadOnlyList<WorkflowStep> steps, string? parent)
        {
            foreach (var step in steps)
            {
                if (step.Type == "reason")
                {
                    seen.Add((parent is null ? step.Name : $"{parent}.{step.Name}", step.Graph, step.GraphFrom, step.GraphPromptTokens));
                }

                Walk(step.Steps, parent is null ? step.Name : $"{parent}.{step.Name}");
            }
        }
    }

    /// <summary>
    /// The shipped flow is held to its own files, not only parsed beside them: where its diagnosis schema holds no
    /// <c>choices</c>, the question that shows them to a person is refused, and where the diagnosis prompt reads the
    /// iteration before, that prompt is - each named as the definition writes it.
    /// </summary>
    [Fact]
    public void TheShippedFlow_IsTypedByItsOwnSchemas_AndItsPromptsAreRead()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var diagnosis = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/diagnosis.schema.json")!)!.AsObject();
        var required = diagnosis["required"]!.AsArray();
        var prompt = WorkflowTemplates.TextOf("prompts/diagnose.md")!;
        var added = prompt.Count(character => character == '\n') + 1; // The line the read is added on, below the last.

        diagnosis["properties"]!.AsObject().Remove("choices");
        required.RemoveAt(required.Select(name => name!.GetValue<string>()).ToList().IndexOf("choices"));

        var problems = WorkflowValidator.Validate(definition, hasCommitTemplate: false, (file, owner) => owner is null
            ? file switch
            {
                "schemas/diagnosis.schema.json" => diagnosis.ToJsonString(),
                "prompts/diagnose.md" => prompt + "{previous.course}\n",
                _ => WorkflowTemplates.TextOf(file),
            }
            : null);

        Assert.Equal(
            [
                $"step 'recover.diagnose' prompt 'prompts/diagnose.md' line {added} reads 'previous.course': a prompt is what the first iteration is asked with too, which has none before it; the iteration before is read in a resumePrompt",
                "'recover.direction.question' reads 'diagnose.choices', and the schema 'schemas/diagnosis.schema.json' of step 'recover.diagnose' has no property 'choices'",
            ],
            problems);
    }

    /// <summary>
    /// The embedded set is whole: every file the templates declare is in the assembly, and every file the assembly
    /// carries under the workflow's root is one the templates declare - no orphan the definition cannot name, no named
    /// file the build lacks.
    /// </summary>
    [Fact]
    public void TheEmbeddedSet_IsWholeAndNoMore()
    {
        var carried = DirectoryDeclarations();

        // Every file the workflow names is embedded, and nothing the workflow does not name rides its set: the only
        // files embedded beyond the workflow's own are the tool's own analyst's - read from the build alone, never
        // written into a repository, exported or given back.
        var carriedSet = carried.ToHashSet(StringComparer.Ordinal);
        Assert.Contains(WorkflowTemplates.Files, file => carriedSet.Contains(file));

        Assert.Equal(
            [.. WorkflowTemplates.ToolOwnFiles.Order(StringComparer.Ordinal)],
            [.. carried.Where(file => !WorkflowTemplates.Files.Contains(file, StringComparer.Ordinal)).Order(StringComparer.Ordinal)]);

        return;

        static IReadOnlyList<string> DirectoryDeclarations()
        {
            // The names this assembly carries, read back as EnsureShipped writes them: the workflow's
            // own files below its name, with '/' whatever separator the building machine used.
            var prefix = typeof(WorkflowTemplates).Assembly.GetName().Name + ".WorkflowTemplates/";

            return
            [
                .. typeof(WorkflowTemplates).Assembly.GetManifestResourceNames()
                    .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
                    .Select(BelowTheWorkflowsName)
                    .Select(name => name.Replace('\\', '/')),
            ];

            static string BelowTheWorkflowsName(string resource)
            {
                var below = resource[(resource.LastIndexOf(".WorkflowTemplates/", StringComparison.Ordinal) + ".WorkflowTemplates/".Length)..];
                var cut = below.IndexOfAny(['/', '\\']);

                return cut < 0 ? below : below[(cut + 1)..];
            }
        }
    }

    /// <summary>
    /// The implementation workflow is written file by file: where none of it is there, every file, in order; where some is, only
    /// what is missing, and every file there is kept as it is - an edited one too - and counted.
    /// </summary>
    [Fact]
    public void TheImplementationWorkflow_IsWrittenFileByFile_KeepingEveryFileThere()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");

        var written = WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        Assert.Equal([.. WorkflowTemplates.Files.Select(file => $".harness-config/workflows/{name}/{file}")], written.Created);
        Assert.Equal(0, written.Kept);
        Assert.Equal(WorkflowTemplates.Files.Count, Directory.GetFiles(workflows, "*", SearchOption.AllDirectories).Length);

        File.Delete(Path.Combine(workflows, name, "recommendations.md"));
        File.Delete(Path.Combine(workflows, name, "closure.md"));
        File.WriteAllText(Path.Combine(workflows, name, "agents", "advisor.md"), "edited\n");

        var finished = WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        Assert.Equal([$".harness-config/workflows/{name}/recommendations.md", $".harness-config/workflows/{name}/closure.md"], finished.Created);
        Assert.Equal(WorkflowTemplates.Files.Count - 2, finished.Kept);
        Assert.Equal(WorkflowTemplates.TextOf("recommendations.md"), File.ReadAllText(Path.Combine(workflows, name, "recommendations.md")));
        Assert.Equal(WorkflowTemplates.TextOf("closure.md"), File.ReadAllText(Path.Combine(workflows, name, "closure.md")));
        Assert.Equal("edited\n", File.ReadAllText(Path.Combine(workflows, name, "agents", "advisor.md")));
    }

    /// <summary>
    /// Where the repository has its own definition of the workflow, init writes back only the missing shipped files that
    /// definition names: a file the repository took out with its step stays out (part 05), and a definition that does
    /// not read writes nothing more, since only its check can say what it names.
    /// </summary>
    [Fact]
    public void ARepositorysOwnDefinition_GetsBackOnlyTheShippedFilesItNames()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var directory = Path.Combine(workflows, name);

        WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        // The repository keeps one step of the shipped flow, and its files; every other file is gone.
        foreach (var file in Directory.GetFiles(directory, "*", SearchOption.AllDirectories))
        {
            File.Delete(file);
        }

        File.WriteAllText(
            Path.Combine(directory, $"{name}.yml"),
            $"""
            name: {name}
            steps:
              - name: triage
                type: reason
                agent: triage
                prompt: prompts/triage.md
                outputs: schemas/triage.schema.json
            """);

        var written = WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        Assert.Equal(
            [
                $".harness-config/workflows/{name}/agents/triage.md",
                $".harness-config/workflows/{name}/prompts/triage.md",
                $".harness-config/workflows/{name}/schemas/triage.schema.json",
            ],
            written.Created);
        Assert.Equal(1, written.Kept);

        // A definition that does not read says nothing of what it names: nothing is written in its place, and the refusal
        // says why, as configuration that does not hold.
        File.WriteAllText(Path.Combine(directory, $"{name}.yml"), "name: [unclosed\n");
        File.Delete(Path.Combine(directory, "prompts", "triage.md"));

        var refusal = Assert.Throws<HarnessException>(() => WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.False(File.Exists(Path.Combine(directory, "prompts", "triage.md")));
    }

    /// <summary>
    /// Where the repository took the item pipeline out whole, its definition still using it, init writes it back whole
    /// in one run: the pipeline's yml, missing, is read from the copy init is about to write, so every file it names is
    /// known before anything is written.
    /// </summary>
    [Fact]
    public void AUsedPipelineTakenOutWhole_IsWrittenBackWholeInOneRun()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var pipeline = WorkflowTemplates.Files.Where(file => file.StartsWith("item-pipeline/", StringComparison.Ordinal)).ToList();

        WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);
        Directory.Delete(Path.Combine(workflows, name, "item-pipeline"), recursive: true);

        var written = WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        Assert.Equal([.. pipeline.Select(file => $".harness-config/workflows/{name}/{file}")], written.Created);
        Assert.Equal(WorkflowTemplates.Files.Count - pipeline.Count, written.Kept);
    }

    /// <summary>
    /// A repository's own definition that uses a workflow that does not resolve - one of its own that is not there, and
    /// no shipped one stands in for it - refuses as configuration that does not hold, naming every problem: which
    /// shipped files the tree names cannot be told, and none is written. A remote use names no file of the
    /// repository's, and is no problem of init's.
    /// </summary>
    [Fact]
    public void ARepositorysOwnDefinitionUsingWhatDoesNotResolve_IsRefused_AndARemoteUseIsNone()
    {
        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var directory = Path.Combine(workflows, name);

        WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);
        File.Delete(Path.Combine(directory, "prompts", "triage.md"));
        File.WriteAllText(
            Path.Combine(directory, $"{name}.yml"),
            $"""
            name: {name}
            steps:
              - name: triage
                type: reason
                agent: triage
                prompt: prompts/triage.md
                outputs: schemas/triage.schema.json
              - name: mine
                use: ./mine/mine.yml
            """);

        var refusal = Assert.Throws<HarnessException>(() => WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison));

        Assert.Equal(HarnessExit.ConfigInvalid, refusal.ExitCode);
        Assert.Equal(
            $".harness-config/workflows/{name}/{name}.yml is the repository's own definition of the {name} workflow, and uses what does not resolve, so which shipped files it names cannot be told, "
                + $"and none was written; run 'dssharness init' again once it does ('dssharness validate-workflow {name}' says the same):{Environment.NewLine}"
                + $"  - step 'mine' uses './mine/mine.yml', and no workflow file is at '{name}/mine/mine.yml' under the repository's workflows directory",
            refusal.Message);
        Assert.False(File.Exists(Path.Combine(directory, "prompts", "triage.md")));

        File.WriteAllText(
            Path.Combine(directory, $"{name}.yml"),
            $"""
            name: {name}
            steps:
              - name: triage
                type: reason
                agent: triage
                prompt: prompts/triage.md
                outputs: schemas/triage.schema.json
              - name: review
                use: acme/shared/workflows/review/review.yml@v1
            """);

        Assert.Equal([$".harness-config/workflows/{name}/prompts/triage.md"], WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison).Created);
    }

    /// <summary>
    /// A repository's own definition that could not be read, where a shipped file is missing, refuses (13) saying why:
    /// which files it names cannot be told, and none is written in its place.
    /// </summary>
    [Fact]
    public void ARepositorysOwnDefinitionThatCouldNotBeRead_IsRefused_AndNothingIsWritten()
    {
        using var temp = new TempDirectory();
        var real = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");
        var directory = Path.Combine(workflows, name);

        WorkflowTemplates.EnsureShipped(workflows, real, PathComparison);
        File.Delete(Path.Combine(directory, "prompts", "plan.md"));

        var refusal = Assert.Throws<HarnessException>(() => WorkflowTemplates.EnsureShipped(workflows, new UnreadDefinition(real, $"{name}.yml"), PathComparison));

        Assert.Equal(HarnessExit.Refused, refusal.ExitCode);
        Assert.Equal(
            $".harness-config/workflows/{name}/{name}.yml is the repository's own definition of the {name} workflow, and could not be read "
                + "(it is held), so which shipped files it names cannot be told, and none was written; run 'dssharness init' again once it reads.",
            refusal.Message);
        Assert.False(File.Exists(Path.Combine(directory, "prompts", "plan.md")));
    }

    /// <summary>
    /// Every reasoning step of the shipped flow but the triage answers with a schema that holds preExisting - required, a
    /// list of what it met that was there before the run - and the triage's own schema holds what it decides.
    /// </summary>
    [Fact]
    public void EveryShippedReasoningSchema_HoldsPreExisting_AndTheTriagesHoldsWhatItDecides()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var schemas = NamedFiles.Steps(definition)
            .Where(entry => entry.Step.Type == "reason" && entry.Step.Raised is null)
            .Select(entry => entry.Step.WorkflowOf is { } owner
                ? $"{owner[(WorkflowTemplates.ShippedWorkflowName.Length + 1)..]}/{entry.Step.Outputs!}"
                : entry.Step.Outputs!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(
            [
                "item-pipeline/schemas/implement.schema.json",
                "item-pipeline/schemas/review-scope.schema.json",
                "item-pipeline/schemas/review.schema.json",
                "schemas/diagnosis.schema.json",
                "schemas/plan-audit.schema.json",
                "schemas/plan.schema.json",
                "schemas/priority.schema.json",
                "schemas/self-audit.schema.json",
                "schemas/summary.schema.json",
                "schemas/triage.schema.json",
            ],
            [.. schemas.Order(StringComparer.Ordinal)]);

        foreach (var schema in schemas)
        {
            var root = JsonNode.Parse(WorkflowTemplates.TextOf(schema)!)!.AsObject();
            var reported = root["properties"]!["preExisting"]!;

            Assert.Contains("preExisting", root["required"]!.AsArray().Select(member => member!.GetValue<string>()));
            Assert.Equal("array", reported["type"]!.GetValue<string>());
            Assert.Equal(["title", "description", "where", "evidence", "blocks"], reported["items"]!["required"]!.AsArray().Select(member => member!.GetValue<string>()));
        }

        var triage = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/pre-existing.schema.json")!)!.AsObject();

        Assert.Equal(
            ["course", "title", "description", "where", "proof", "size", "blocks", "related", "critical", "why", "duplicates", "joins"],
            triage["required"]!.AsArray().Select(member => member!.GetValue<string>()));
        Assert.Equal(["fix", "next", "list", "duplicate", "unproven", "join"], triage["properties"]!["course"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()));
    }

    /// <summary>
    /// The shipped triage agent says every course its schema lets it answer, each where it is chosen - read from the
    /// schema's own list, so a course added there is never one the agent was not told of - and what becomes of each: a
    /// fix built by the recovery, a join with the fix it joins, a next by the first work item of the next round, sized.
    /// </summary>
    [Fact]
    public void TheTriageAgent_SaysEveryCourseItsSchemaHas()
    {
        var courses = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/pre-existing.schema.json")!)!["properties"]!["course"]!["enum"]!.AsArray().Select(value => value!.GetValue<string>()).ToList();
        var agent = Prose.Words(WorkflowTemplates.TextOf("agents/pre-existing-triage.md")!);
        var work = agent[agent.IndexOf("## How you work", StringComparison.Ordinal)..agent.IndexOf("## What you do not do", StringComparison.Ordinal)];

        // Each course is said where the agent works out which to answer.
        Assert.Equal([], courses.Where(course => !work.Contains($"course `{course}`", StringComparison.Ordinal) && !work.Contains($"course is `{course}`", StringComparison.Ordinal)));
        Assert.Contains("description: Checks an issue a step met that was there before the run, and decides what the run does with it - fixes it now, joins it to a fix, sends it to the next round, or lists it for the person", agent, StringComparison.Ordinal);
        Assert.Contains(
            "Where none holds and it is a defect in code this run's change does not touch, worth a round of its own, the course is `next`: it becomes the first work item of the run's next round, and nothing of this round waits for it. "
                + "Size it - the files the fix touches, one at least, what must be built and what must be measured first - for its item is built to that size; for every other course the size is empty. Where none of these holds, the course is `list`.",
            agent,
            StringComparison.Ordinal);
        Assert.Contains("You fix nothing. A `fix` is built by the run's recovery, as a failure's fix is, a `join` with the fix it joins, and a `next` by the first work item of the run's next round; a listed issue is shown to the person at the end.", agent, StringComparison.Ordinal);
    }

    /// <summary>
    /// No shipped agent file names a model, a tier or an effort: which model a step runs on is the step's to say, in the
    /// workflow's yml, so an agent file a repository reuses in a workflow of its own brings no choice of model with it.
    /// </summary>
    [Fact]
    public void NoShippedAgentFile_NamesAModelATierOrAnEffort()
    {
        string[] chosen = ["model", "tier", "effort"];
        var agents = WorkflowTemplates.Files.Where(file =>
            file.StartsWith("agents/", StringComparison.Ordinal) || file.StartsWith("item-pipeline/agents/", StringComparison.Ordinal)).ToList();

        Assert.Equal(19, agents.Count);

        foreach (var agent in agents)
        {
            var lines = WorkflowTemplates.TextOf(agent)!.Split('\n');
            var close = Array.IndexOf(lines, "---", 1);

            Assert.True(lines[0] == "---" && close > 0, $"{agent} opens with no front matter");

            var keys = lines[1..close].Select(line => line.Split(':')[0].Trim());

            Assert.Empty(keys.Intersect(chosen, StringComparer.Ordinal));
        }
    }

    /// <summary>
    /// The method this build ships plans and reviews by (user, 2026-10-17): a plan's items are sized to one builder's
    /// cycle each, every item - a further round's and a recovery's too - and the plan itself carry a definition of
    /// done, an item's said in statements that hold whatever the run's other work does beside it, and the review loop
    /// stands while a critical, high or medium finding does: three rounds, three more while
    /// one stands, and what is left then is carried to the end of the run, written down for the person who reads the
    /// pull request, rather than stopping the run. Held on the shipped files that say it, so a change of method is a
    /// change of those files and of this test together.
    /// </summary>
    [Fact]
    public void TheShippedMethod_PlansByDefinitionsOfDone_AndStandsWhileAMediumFindingDoes()
    {
        var schema = JsonDocument.Parse(Read("schemas/plan.schema.json")).RootElement;

        Assert.Contains(schema.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");
        Assert.Contains("one builder's cycle", schema.GetProperty("properties").GetProperty("items").GetProperty("description").GetString(), StringComparison.Ordinal);

        // The work item is one definition, shared by every list that holds one: the plan's items and a scope piece's
        // item both refer to it, and it alone says what a work item is - its definition of done among that, and now
        // its classes and its noClass.
        var item = schema.GetProperty("$defs").GetProperty("workItem");

        Assert.Equal("#/$defs/workItem", schema.GetProperty("properties").GetProperty("items").GetProperty("items").GetProperty("$ref").GetString());
        Assert.Contains(item.GetProperty("required").EnumerateArray(), required => required.GetString() == "done");
        Assert.Contains(item.GetProperty("required").EnumerateArray(), required => required.GetString() == "classes");
        Assert.Contains(item.GetProperty("required").EnumerateArray(), required => required.GetString() == "noClass");
        Assert.Equal("string", item.GetProperty("properties").GetProperty("done").GetProperty("type").GetString());
        Assert.Equal("#/$defs/closureClass", item.GetProperty("properties").GetProperty("classes").GetProperty("items").GetProperty("$ref").GetString());

        // The one description of an item's done every writer of one reads - the planner's, the self-audit's and the
        // diagnostician's, whose items refer to it - says its statements hold whatever the run's other work, built beside
        // the item, does to the same files: a remedy pinned to a count of lines its round's own item changed could never
        // be met (live, 2026-11-26).
        Assert.Equal(
            "The item's definition of done: what must hold for the item to be finished, as statements a person or a check can answer one by one - "
                + "the behaviours built, the test that pins each, and what is true of the tree when the builder reports. Each statement says what "
                + "this item makes true in a way that holds whatever the run's other work, built beside it, does to the same files: never a count "
                + "of lines, a line number, a whole output, or a comparison of a whole file or the whole tree with the base, which that other work "
                + "may change. The builder works to it, the review weighs it and the run's final audit checks it.",
            item.GetProperty("properties").GetProperty("done").GetProperty("description").GetString());

        var piece = schema.GetProperty("properties").GetProperty("scope").GetProperty("items").GetProperty("properties").GetProperty("item");

        Assert.Contains(piece.GetProperty("anyOf").EnumerateArray(), shape => shape.TryGetProperty("$ref", out var reference) && reference.GetString() == "#/$defs/workItem");

        // A further round's items and a recovery's hold no shape of their own: each names the plan's one definition,
        // which carries its definition of done and its classes with it wherever it is shared.
        foreach (var further in new[] { "schemas/self-audit.schema.json", "schemas/diagnosis.schema.json" })
        {
            var items = JsonDocument.Parse(Read(further)).RootElement.GetProperty("properties").GetProperty("items").GetProperty("items");

            Assert.Equal("plan.schema.json#/$defs/workItem", items.GetProperty("$ref").GetString());
        }

        var pipeline = Read("item-pipeline/item-pipeline.yml");

        Assert.Contains("rounds: 3", pipeline, StringComparison.Ordinal);
        Assert.Contains(
            "blocking: \"any(union(route.rework, route.homeAudit), it.severity != 'low' && it.severity != 'nit')\"",
            pipeline,
            StringComparison.Ordinal);
        Assert.DoesNotContain("it.production && it.severity", pipeline, StringComparison.Ordinal);

        var fix = StepAt(WorkflowTemplates.ShippedDefinition().Steps, "rounds", "items", "fix");

        Assert.Equal(3, fix.Repeat!.BlockersLimitRounds);
        Assert.Equal("union(route.rework, route.homeAudit)", fix.Repeat.Leftover);
        Assert.Equal("route", fix.Repeat.DecideAfter);
        Assert.Equal(new FlowAnswer("accept", null, null), fix.Answers["loopBound"]);
    }

    /// <summary>
    /// A plan holds as many work items as its work needs, and no shipped file sets a count of them: the plan's prompt, the
    /// planner's instructions, the self-audit's and the schemas the items are asked under say each item is one builder's
    /// cycle, and none says a plan or a round holds at most so many - a round runs its items in waves of the run's open
    /// agents, and the plan check refuses no count.
    /// </summary>
    [Fact]
    public void TheShippedPrompts_SetNoCountOfWorkItems()
    {
        var assembly = typeof(WorkflowTemplates).Assembly;
        var shipped = assembly.GetManifestResourceNames()
            .Where(resource => resource.Replace('\\', '/').Contains(".WorkflowTemplates/", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(WorkflowTemplates.Files.Count + WorkflowTemplates.ToolOwnFiles.Count, shipped.Count);
        Assert.DoesNotContain(shipped, resource =>
        {
            using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
            var text = reader.ReadToEnd();

            return text.Contains("at most 8", StringComparison.OrdinalIgnoreCase) || text.Contains("8 items", StringComparison.OrdinalIgnoreCase);
        });

        Assert.Contains("split it into items by sets of files, as many as it needs,", Read("prompts/plan.md"), StringComparison.Ordinal);
        Assert.Contains("A plan holds as many items as\n   the work needs", Read("agents/planner.md").ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("shaped as the plan's items are, as many as the findings need:", Read("prompts/self-audit.md"), StringComparison.Ordinal);
        Assert.StartsWith(
            "The work items, as many as the work needs. Each is sized to one builder's cycle",
            JsonDocument.Parse(Read("schemas/plan.schema.json")).RootElement.GetProperty("properties").GetProperty("items").GetProperty("description").GetString(),
            StringComparison.Ordinal);
        Assert.Contains(
            "As many as the findings need, shaped as a plan's",
            JsonDocument.Parse(Read("schemas/self-audit.schema.json")).RootElement.GetProperty("properties").GetProperty("items").GetProperty("description").GetString(),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every repair of the item pipeline that goes on with the implementation - and so may change its code - goes back
    /// through the item's tests before anything is folded: one the item's own steps raise goes on to the item's block,
    /// whose verify runs again (a blocked answer asked first), and one the review loop's revise raises goes on to reverify.
    /// The class-closure check's own repairs are among them - close-class consolidates sites and arms the class, home-audit
    /// refactors the home and its callers - for a repair that went on to the check alone would fold code no test ran on,
    /// under a checkpoint naming the tests run before it.
    /// </summary>
    [Fact]
    public void EveryRepairThatGoesOnWithTheImplementation_GoesBackThroughTheItemsTests()
    {
        var items = StepAt(WorkflowTemplates.ShippedDefinition().Steps, "rounds", "items");
        var revise = StepAt(items.Steps, "fix", "revise");

        static bool GoesOn(WorkflowStep step) => step.Resume == "implement" || step.Steps.Any(GoesOn);

        Assert.Equal(
            [("unblock", "item-blocked"), ("mend", "item-blocked"), ("close-class", "item-blocked"), ("home-audit", "item-blocked"), ("reconcile", "item-blocked"), ("cite", "item-blocked"), ("rearm", "item-arms")],
            [.. items.Steps.Where(step => step.On is not null && GoesOn(step)).Select(step => (step.Name, step.Then ?? string.Empty))]);
        Assert.Equal(
            [("remend", "reverify"), ("reclose-class", "reverify"), ("rehome-audit", "reverify"), ("rereconcile", "reverify"), ("recite", "reverify")],
            [.. revise.Steps.Where(step => step.On is not null && GoesOn(step)).Select(step => (step.Name, step.Then ?? string.Empty))]);
    }

    /// <summary>
    /// The review loop reads both lists its routing makes alike - the rework and the home audits: a class reopened twice
    /// or more, graded by the worst of its findings, keeps the loop going past its rounds as a rework of that grade does,
    /// and what the loop leaves at its bound holds it, so an audit still owed is written to the backlog and named in the
    /// pull request; a loop whose routing left nothing blocks nothing and leaves nothing. The pipeline's own leftover
    /// output reads the same lists of the loop's last routing, and is empty for an item a person answered skip, whose
    /// loop never ran - never a failure of the use that built nothing.
    /// </summary>
    [Fact]
    public void TheReviewLoop_ReadsTheHomeAuditsItsRoutingLeaves_AsItReadsTheRework()
    {
        var items = StepAt(WorkflowTemplates.ShippedDefinition().Steps, "rounds", "items");
        var repeat = StepAt(items.Steps, "fix").Repeat!;
        var audit = new JsonObject { ["reopenedClass"] = "c-rate", ["rounds"] = 2, ["severity"] = "high", ["findings"] = new JsonArray() };
        var nit = new JsonObject { ["title"] = "a name", ["severity"] = "nit", ["class"] = "introduced" };

        (bool Blocking, string Leftover) Decided(JsonArray rework, JsonArray homeAudit)
        {
            // What the route answered, as the loop reads the step: its root, the tree walking the member read.
            FlowValue Read(FlowExpression.Path path) => path.Root == "route"
                ? FlowValue.Of(new JsonObject { ["rework"] = rework.DeepClone(), ["homeAudit"] = homeAudit.DeepClone(), ["reopened"] = new JsonArray() })
                : throw new InvalidOperationException($"the loop reads '{path.Whole}'");

            return (
                FlowExpression.Parse(repeat.Blocking!).Evaluate(Read).AsBoolean(() => repeat.Blocking!),
                FlowExpression.Parse(repeat.Leftover!).Evaluate(Read).ToNode()!.ToJsonString());
        }

        // The pipeline's output, read as the use ends: the loop's last routing, through the loop - and, for an item a person
        // answered skip, whose loop never ran, a routing that reads nothing, which leaves nothing rather than failing the use.
        var left = Assert.Single(items.Use!.Outputs, output => output.Name == "leftover").From;

        string Left(JsonArray? rework, JsonArray? homeAudit)
            => FlowExpression.Parse(left).Evaluate(path => path.Root == "fix"
                ? rework is null || homeAudit is null
                    ? FlowValue.Missing
                    : FlowValue.Of(new JsonObject { ["route"] = new JsonObject { ["rework"] = rework.DeepClone(), ["homeAudit"] = homeAudit.DeepClone(), ["reopened"] = new JsonArray() } })
                : throw new InvalidOperationException($"the output reads '{path.Whole}'")).ToNode()!.ToJsonString();

        Assert.Equal((true, new JsonArray(audit.DeepClone()).ToJsonString()), Decided([], [audit.DeepClone()]));
        Assert.Equal((false, new JsonArray(nit.DeepClone()).ToJsonString()), Decided([nit.DeepClone()], []));
        Assert.Equal((false, "[]"), Decided([], []));
        Assert.Equal(new JsonArray(nit.DeepClone(), audit.DeepClone()).ToJsonString(), Left([nit.DeepClone()], [audit.DeepClone()]));
        Assert.Equal("[]", Left([], []));
        Assert.Equal("[]", Left(null, null));
        Assert.Equal("empty(route.rework) && empty(route.homeAudit)", repeat.Until);
        Assert.Equal("union(route.rework, route.homeAudit)", repeat.Leftover);
    }

    /// <summary>
    /// The item's own sweep runs the arms its builder's answer lists, pending, on the item's own commit; an arm that is
    /// not as it should be - violated, survived or unattributed, the verdicts only an arm reaches - goes back to the
    /// builder by the rearm, and every other failure of the sweep is the machine's. Each class-closure check of the item
    /// - the first, the rearm's and the review fix's - holds the same answer's arms to the record it judges.
    /// </summary>
    [Fact]
    public void TheItemsSweep_RunsTheArmsItsBuilderLists_AndOnlyAnArmsOwnVerdictsGoBackToTheBuilder()
    {
        var items = StepAt(WorkflowTemplates.ShippedDefinition().Steps, "rounds", "items");
        var sweep = StepAt(items.Steps, "item-arms");
        var rearm = StepAt(items.Steps, "rearm");

        Assert.Equal(
            ("check-mutations", true, "implement.latest.arms", SweepStepRunner.ItemBase),
            (sweep.Type, sweep.Pending, sweep.Arms?.Expression, sweep.Base));
        Assert.Equal(("item-arms", "failure", "item-arms"), (rearm.On!.Step, rearm.On.Outcome, rearm.Then));
        Assert.Equal([LegExit.Violated, LegExit.Survived, LegExit.Unattributed], rearm.On.Codes);
        Assert.Equal(
            [("class-closure", "implement.latest.arms"), ("rearm-closure", "implement.latest.arms"), ("reclass-closure", "implement.latest.arms")],
            [
                .. new[] { StepAt(items.Steps, "class-closure"), StepAt(items.Steps, "rearm", "rearm-closure"), StepAt(items.Steps, "fix", "revise", "reclass-closure") }
                    .Select(step => (step.Name, step.Arms?.Expression)),
            ]);
    }

    /// <summary>
    /// The shipped flow declares its texts - the bar for recommendations and its closure, the universe a repository's
    /// decisions range over - and <c>init</c> writes <c>closure.md</c> once and never over a repository's own, exactly
    /// as it keeps <c>recommendations.md</c>: the file is the repository's from the day it is written.
    /// </summary>
    [Fact]
    public void TheShippedFlow_DeclaresItsTexts_AndShipsClosureMdAsTheRepositorysOwn()
    {
        var definition = WorkflowTemplates.ShippedDefinition();

        Assert.Equal(
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["recommendations"] = "recommendations.md",
                ["closure"] = "closure.md",
            },
            definition.Texts);
        Assert.Null(definition.Recommendations);

        using var temp = new TempDirectory();
        var fileSystem = new PhysicalFileSystem(FilePermissionsFactory.Create());
        var name = WorkflowTemplates.ShippedWorkflowName;
        var workflows = temp.Combine("tree", ".harness-config", "workflows");

        WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);
        File.WriteAllText(Path.Combine(workflows, name, "closure.md"), "# The repository's own universe\n");
        File.Delete(Path.Combine(workflows, name, "recommendations.md"));

        var finished = WorkflowTemplates.EnsureShipped(workflows, fileSystem, PathComparison);

        Assert.Equal([$".harness-config/workflows/{name}/recommendations.md"], finished.Created);
        Assert.Equal("# The repository's own universe\n", File.ReadAllText(Path.Combine(workflows, name, "closure.md")));
    }

    /// <summary>
    /// <c>closure.md</c> says what reads it, as what ships does: an agent is given it only where its step's prompt
    /// reads <c>{text.closure}</c> - the planner's, the plan audit's, the item audit's and the builders', the prompts
    /// that plan, check and close the classes of the work items - and the file tells a repository its words weigh on
    /// what those prompts decide; the README says the same.
    /// </summary>
    [Fact]
    public void ClosureMd_IsReadByThePromptsThatPlanCheckAndCloseTheClasses()
    {
        Assert.Equal(
            ["item-pipeline/prompts/implement.md", "item-pipeline/prompts/remedy.md", "prompts/item-audit.md", "prompts/plan-audit.md", "prompts/plan.md"],
            [.. WorkflowTemplates.Files
                .Where(file => file != "closure.md" && WorkflowTemplates.TextOf(file)!.Contains("{text.closure}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)]);
        Assert.Contains(
            "an agent is given it where its step's prompt reads `{text.closure}`, as the file stands when the prompt is given. The "
                + "prompts shipped beside it that read it are the plan's, the plan audit's, the item audit's and the builders' - the "
                + "implement prompt and the recovery's remedy - which plan, check and close the classes of the work items: what is "
                + "written here weighs on what they decide a class covers, so keep it true of the repository.",
            Prose.Words(WorkflowTemplates.TextOf("closure.md")!),
            StringComparison.Ordinal);
        Assert.Contains(
            "The planner's, the plan audit's, the item audit's and the builders' prompts read `{text.closure}`, and the file says so: what a repository writes there weighs on what they decide a class covers.",
            Prose.Document("README.md"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The pull request's body carries the counts by which a repository sees whether its rounds converge, as the report
    /// does: each class's claim, each item's counts, the classes reopened, the hotspots and what became of each issue
    /// carried to a later round - and of the closure's run texts it reads every one but the records whole, which are the
    /// self-audit's.
    /// </summary>
    [Fact]
    public void ThePullRequestsBody_CarriesEveryCountOfTheClosure()
    {
        var body = WorkflowTemplates.TextOf("prompts/pr-body.md")!;

        Assert.Equal(
            [WorkflowExpressions.RunClaims, WorkflowExpressions.RunCounts, WorkflowExpressions.RunHotspots, "preExisting", WorkflowExpressions.RunReopened],
            [.. WorkflowExpressions.RunTexts.Where(text => body.Contains($"{{run.{text}}}", StringComparison.Ordinal)).Except(["ran", "notRun", "red", "leftover", "assumptions"]).Order(StringComparer.Ordinal)]);
    }

    /// <summary>
    /// The prompts that search for a class's sites name the graph's <c>sites</c> operation first and text search after
    /// it - the plan's, the plan audit's and the item audit's, and the plan's searches - so a class's site list is
    /// never searched by text alone where the graph joins the data, and what the graph lists only is said by the
    /// answer's coverage lines rather than assumed covered: the planner's text search for what the graph does not see,
    /// each said in notCovered as the plan's schema says, and each audit's own text search over the whole tree beside the
    /// graph's, never the plan's searches (spec 01, the plan audit's step 11), so the audit stays the independent check.
    /// </summary>
    [Fact]
    public void ThePromptsThatSearchForAClassSites_NameTheGraphsSitesFirst_AndTextSearchAfterIt()
    {
        Assert.Equal(
            ["prompts/item-audit.md", "prompts/plan-audit.md", "prompts/plan.md"],
            [.. WorkflowTemplates.Files
                .Where(file => file.StartsWith("prompts/", StringComparison.Ordinal) && Prose.Words(WorkflowTemplates.TextOf(file)!).Contains("graph's sites first", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)]);
        Assert.Contains(
            "graph's sites first: it joins every place that decides the data - what defines, calls, uses, makes, references, inherits, implements or overrides it, every literal that holds it and every key it names - and its coverage lines say what it searched by text alone, for nothing is assumed of a language with no extractor. "
                + "Its find, node and related beside it for the names and for what reads and calls them, its impact for everything a change of a node reaches, its around and its outline - of a file or a directory - to know where a node stands before reading any file whole, and text search for what the graph does not see, each such text search said in notCovered.",
            Prose.Words(WorkflowTemplates.TextOf("prompts/plan.md")!),
            StringComparison.Ordinal);
        Assert.Contains(
            "the graph's sites first, which joins every place that decides the data and says by its coverage lines what it searched by text alone - its find, node and related beside it for the names and for what reads and calls them, its impact for everything a change of a node reaches, its around and its outline to know where a node stands before reading a file whole - and text search for what it does not see, each such search said in notCovered.",
            WorkflowTemplates.TextOf("schemas/plan.schema.json")!,
            StringComparison.Ordinal);
        Assert.Contains(
            "search the data yourself - the kind, the key, the diagnostic, the verb, the literal - the graph's sites first - it joins every place that decides the data, and its coverage lines say what it searched by text alone - and your own text search beside it over the whole tree, across every tier, format, target and file kind and not only what the graph does not answer, never the plan's searches;",
            Prose.Words(WorkflowTemplates.TextOf("prompts/plan-audit.md")!),
            StringComparison.Ordinal);
        Assert.Contains(
            "Search the data yourself - the kind, the key, the diagnostic, the verb, the literal - the graph's sites first - it joins every place that decides the data, and its coverage lines say what it searched by text alone - and your own text search beside it over the whole tree, not only what the graph does not answer, never the plan's searches, and never the file the defect was seen in alone: across every tier, format, target and file kind.",
            Prose.Words(WorkflowTemplates.TextOf("prompts/item-audit.md")!),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Every shipped prompt that sends its agent to the graph - its words name the graph - belongs only to steps offered
    /// the graph's tool: each step that reads it, its graph resolved as the run resolves it (<see cref="WorkflowStep.GraphOf"/>
    /// by the step its conversation began with, <see cref="ResumeChain.Root"/>, and that step's agent as its own workflow's
    /// <c>agents/</c> file reads it, the shipped flow's graphify step upstream of every step that reads a prompt), is
    /// <c>tools</c>. A prompt that names an operation no tool of its step answers sends the agent to a tool it does not have.
    /// </summary>
    [Fact]
    public void EveryPromptThatNamesTheGraph_BelongsToAStepOfferedTheGraphsTool()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var naming = new List<string>();

        foreach (var (path, step) in NamedFiles.Steps(definition).Where(entry => entry.Step.Type == "reason"))
        {
            // A prompt the definition gives by an expression is read where the step starts, from text another step wrote.
            foreach (var prompt in new[] { step.Prompt, step.ResumePrompt }.OfType<string>().Where(prompt => !prompt.StartsWith('{')))
            {
                var file = Below(step.WorkflowOf, prompt);

                if (!System.Text.RegularExpressions.Regex.IsMatch(WorkflowTemplates.TextOf(file)!, @"\bgraph\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    continue;
                }

                naming.Add(file);

                // The graph a step is given is its conversation's: the step its chain of work began with, by that step's agent.
                var root = ResumeChain.Root(definition, path, step);
                var agent = root.Agent is { } named
                    ? AgentDefinitionParser.Parse(WorkflowTemplates.TextOf(Below(root.WorkflowOf, $"agents/{named}.md"))!, named).Definition
                    : null;
                var mode = WorkflowStep.GraphOf(root, agent, graphUpstream: true);

                Assert.True(mode == "tools", $"'{file}' names the graph, and step '{path}' is given the graph '{mode}'");
            }
        }

        // The prompts that name the graph today, so a new one is held as it comes.
        Assert.Equal(
            ["item-pipeline/prompts/home-audit.md", "item-pipeline/prompts/rework.md", "prompts/item-audit.md", "prompts/plan-audit.md", "prompts/plan.md", "prompts/pre-existing.md"],
            [.. naming.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)]);

        // A file of a workflow the shipped one uses is named under that workflow's place, as the embedded set holds it.
        static string Below(string? owner, string named)
            => NamedFiles.PlaceOf(WorkflowTemplates.ShippedWorkflowName, owner) is { Length: > 0 } place ? $"{place}/{named}" : named;
    }

    /// <summary>
    /// Every prompt of the item pipeline that builds or goes on with an item reads the item's classes: the builders'
    /// prompts hold the class-closure paragraph beside the item's classes, its noClass reason and the repository's
    /// universe, the implementer's definition holds the paragraph, and every prompt that goes on with an item - mend
    /// (which serves remend too), rework, reconcile (resettle), anchors (recite), unblock, redo and undo - reads them
    /// with the record so far under "The class you are closing", so a fresh agent is given the class, never trusted to
    /// remember it. The recovery's remedy builds fresh, so it holds the builders' part alone: its record starts empty,
    /// and a step's prompt reads no step that is not before it. No other shipped prompt reads the classes: the
    /// reviewers judge what the change-first pack shows them. The record is what the implement step's answer schema
    /// requires, closure and arms among its members.
    /// </summary>
    [Fact]
    public void EveryPromptThatBuildsOrGoesOnWithAnItem_IsGivenTheClassItCloses_AndTheRecordSoFar()
    {
        string[] goesOn =
        [
            "item-pipeline/prompts/anchors.md",
            "item-pipeline/prompts/closure-record.md",
            "item-pipeline/prompts/home-audit.md",
            "item-pipeline/prompts/mend.md",
            "item-pipeline/prompts/rearm.md",
            "item-pipeline/prompts/redo.md",
            "item-pipeline/prompts/reconcile.md",
            "item-pipeline/prompts/rework.md",
            "item-pipeline/prompts/undo.md",
            "item-pipeline/prompts/unblock.md",
        ];

        foreach (var prompt in goesOn)
        {
            var text = WorkflowTemplates.TextOf(prompt)!;

            Assert.Contains("## The class you are closing", text, StringComparison.Ordinal);
            Assert.Contains("{input.item.classes}", text, StringComparison.Ordinal);
            Assert.Contains("{implement.latest.closure}", text, StringComparison.Ordinal);
        }

        foreach (var builds in new[] { "item-pipeline/prompts/implement.md", "item-pipeline/prompts/remedy.md" })
        {
            var text = Prose.Words(WorkflowTemplates.TextOf(builds)!);

            Assert.Contains(
                "For every class of your item, before you edit: read its invariant, its one home and its site list below, and check them in the code; a site the list missed is yours to add",
                text,
                StringComparison.Ordinal);
            Assert.Contains("Nothing is folded without the record.", text, StringComparison.Ordinal);
            Assert.Contains(
                "That check reads your answer as you give it, over your tree as it then stands: what it finds wrong with the record is told you here, in this conversation, to put right in the answer you give again.",
                text,
                StringComparison.Ordinal);
            Assert.Contains("{input.item.classes}", text, StringComparison.Ordinal);
            Assert.Contains("{input.item.noClass}", text, StringComparison.Ordinal);
            Assert.Contains("{text.closure}", text, StringComparison.Ordinal);
        }

        Assert.Contains(
            "For every class of your item, before you edit: read its invariant, its one home and its site list below, and check them in the code",
            Prose.Words(WorkflowTemplates.TextOf("item-pipeline/agents/implementer.md")!),
            StringComparison.Ordinal);

        // The builders, the goings-on and the review alone read the classes; no other shipped file does.
        Assert.Equal(
            [.. goesOn.Append("item-pipeline/prompts/implement.md").Append("item-pipeline/prompts/remedy.md").Append("item-pipeline/prompts/review.md").Order(StringComparer.Ordinal)],
            [.. WorkflowTemplates.Files
                .Where(file => WorkflowTemplates.TextOf(file)!.Contains("{input.item.classes}", StringComparison.Ordinal))
                .Order(StringComparer.Ordinal)]);

        var schema = JsonDocument.Parse(Read("item-pipeline/schemas/implement.schema.json")).RootElement;

        Assert.Contains(schema.GetProperty("required").EnumerateArray(), member => member.GetString() == "closure");

        // Each class's arm is its own entry's: the record names it there, and the answer gathers the ids into its own
        // list, which the item's sweep runs.
        Assert.Contains(schema.GetProperty("required").EnumerateArray(), member => member.GetString() == "arms");
        Assert.Equal("array", schema.GetProperty("properties").GetProperty("arms").GetProperty("type").GetString());

        // The builders measure on declared legs alone, and name any other reference unmeasured; a neighbour's result is
        // what the call it cites answered for its cell, as the class-closure check holds it to that call's own record, and
        // a cell is measured again after its fix, the same leg, variant, filter and cell, for the check holds the cell to the
        // newest call of that measurement.
        foreach (var builds in new[] { "item-pipeline/prompts/implement.md", "item-pipeline/prompts/remedy.md" })
        {
            Assert.Contains(
                "Only a leg this run's configuration declares is measured; a reference that is no declared leg - a tool, a service, another implementation - is named unmeasured, with the reason, and so is a cell the tool answers unmeasured.",
                Prose.Words(WorkflowTemplates.TextOf(builds)!),
                StringComparison.Ordinal);
            Assert.Contains(
                "Your record's `measuredBy` cites the id of the call that measured, and its `result` is what that call answered for that cell: `passed` where it passed, `failed-fixed` where it failed and you fixed it in this item. "
                    + "A fix is shown, never said: after you fix a failed cell, measure it again - the same leg, variant, filter and cell - and the check holds the cell to the newest call of that measurement, which must answer passed, for `passed` and `failed-fixed` alike; your record may still cite the call that failed.",
                Prose.Words(WorkflowTemplates.TextOf(builds)!),
                StringComparison.Ordinal);
        }
    }

    /// <summary>
    /// The shipped flow never stops for no reason (user, 2026-10-17): a run of it ends done, or asks a person only what
    /// a person must settle. Every step says what its failure does, in the file: onFailure: recover on each step that
    /// builds, tests, folds, checks, commits, pushes or waits - the check steps of a block whose handler takes the
    /// block's failure first say it through the block - and the three reads told on and never judged say continue;
    /// a reasoning step is never recovered (user, 2026-10-17): its agent is tried again and on its fallback, quota or a
    /// failing provider pauses the run, and what still fails is asked (onFailure: pause). The recovery says, in the
    /// file too, that the step that gave it its failure runs again once it is done (onSuccess: failed-step), and that a
    /// failure of its own work is asked (onFailure: pause) - no step calls itself; none of its steps gives it a failure. What is left to a person is the list below: what blocks the request, the plan's
    /// approval, what a builder says only a person can give, and what a diagnosis says only a person can lift - each
    /// with the advisor's recommendation and the choices.
    /// </summary>
    [Fact]
    public void TheShippedFlow_GivesEveryFailureToItsRecovery_AndAsksAPersonOnlyWhereOneMust()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var recover = Assert.IsType<FlowRecover>(definition.Recover);

        Assert.Equal(FlowRecover.FailedStep, recover.OnSuccess);
        Assert.Equal(FlowRecover.PauseOnFailure, recover.OnFailure);
        Assert.Equal(5, recover.MaxVisits);
        Assert.Equal("diagnose", recover.Steps[0].Resume);
        Assert.Equal(["diagnose", "direction", "redirect", "remedy-check", "remedies", "publish"], [.. recover.Steps.Select(step => step.Name)]);

        var passedOver = new List<string>();
        var untried = new List<string>();
        var untriaged = new List<string>();
        var otherwise = new List<string>();
        var asking = new List<string>();

        Walk(definition.Steps, parent: null, inherited: null, recovering: false, failTo: null);
        Walk(recover.Steps, parent: "recover", inherited: null, recovering: true, failTo: null);

        // Only the reads that are told on, never judged, pass a failure over; every reasoning step asks, every other
        // step of the flow gives its failure to the recovery - through the pipeline's failTo input, filled 'recover'
        // by the rounds' use - and no step of the recovery does: the pipeline's machine steps are filled 'pause'
        // there, so each that fails asks a person on its own, the remedies beside it going on, rather than ending
        // the remedies with nothing more started.
        Assert.Equal(["codebase", "ci", "rounds.items.reconcile.catch-up", "rounds.items.fix.revise.rereconcile.recatch-up", "recover.remedies.reconcile.catch-up", "recover.remedies.fix.revise.rereconcile.recatch-up"], passedOver);
        Assert.Empty(otherwise);

        // The file's own header, which init writes into a repository, says the same of its steps.
        var header = Prose.Words(string.Join(' ', Read("implementation.yml").Split('\n').TakeWhile(line => line.StartsWith('#')).Select(line => line.TrimStart('#'))));
        Assert.Contains(
            "A reasoning step asks ('onFailure: pause'); the graph of the codebase, the CI read and the catch-ups pass a failure over ('onFailure: continue'); "
                + "every other step says 'onFailure: recover', which gives a failure no handler takes to 'recover'",
            header,
            StringComparison.Ordinal);

        // A limit of a reasoning step is answered a counted number of times and then asked, never failed into a recovery.
        foreach (var limit in new[] { "turnLimit", "truncated", "contextLimit" })
        {
            Assert.Null(definition.Answers[limit].Then);
        }

        // Every agent the flow runs says on itself how it is tried again before it asks: each reasoning step, the
        // advisor and the reporter, three times on each model, waiting 30 seconds and twice as long each time.
        Assert.Empty(untried);
        Assert.Equal((3, 30), (definition.Advisor!.Retries, definition.Advisor.BackoffSeconds));
        Assert.Equal((3, 30), (definition.Reporter!.Retries, definition.Reporter.BackoffSeconds));
        Assert.Equal((3, 30), (definition.MaxRetries, definition.BackoffSeconds));

        Assert.Equal(["blocked", "planning.approval", "rounds.items.item-blocked", "recover.direction", "recover.remedies.item-blocked"], asking);

        // Every reasoning step - the recovery's among them - sends what it met that was there before the run to the one
        // triage step, which is raised by such an issue, never scheduled, and gives one it decides to fix to the recovery.
        // It reproduces the issue on the commit the run began on, which its tree is, and only reads.
        Assert.Empty(untriaged);
        var triage = Assert.Single(definition.Steps, step => step.Raised is not null);
        Assert.Equal(
            ("triage-pre-existing-issue", "reason", WorkflowStep.RaisedByPreExisting, WorkflowStep.Recover, (string?)null, "{orient.startCommit}", (string?)null, (string?)null),
            (triage.Name, triage.Type, triage.Raised, triage.OnAddress, triage.OnPreExisting, triage.Commit, triage.Access, triage.In));

        return;

        void Walk(IReadOnlyList<WorkflowStep> steps, string? parent, string? inherited, bool recovering, string? failTo)
        {
            foreach (var step in steps)
            {
                var path = parent is null ? step.Name : $"{parent}.{step.Name}";

                // The pipeline's steps read their failure policy from its failTo input, filled by each use: 'recover'
                // where the rounds use it, 'pause' where the recovery does.
                var written = step.OnFailure == "{input.failTo}" ? failTo : step.OnFailure;
                var effective = written ?? inherited;
                var expected = step.Type == "reason" ? "pause" : recovering ? "pause" : WorkflowStep.Recover;

                if (step.OnFailure == "continue")
                {
                    passedOver.Add(path);
                }
                else if (!step.IsGroup && effective != expected && !(recovering && step.Type != "reason" && effective is null or "pause"))
                {
                    otherwise.Add($"{path}: {effective ?? "nothing"}");
                }

                if (step.Type == "reason" && (step.Retries, step.BackoffSeconds) != (3, 30))
                {
                    untried.Add(path);
                }

                if (step.Type == "reason" && step.Raised is null && step.OnPreExisting != "triage-pre-existing-issue")
                {
                    untriaged.Add(path);
                }

                if (step.Type == "pause" && !step.Answers.ContainsKey("pause"))
                {
                    asking.Add(path);
                }

                Walk(step.Steps, path, effective, recovering, step.With.TryGetValue("failTo", out var filled) ? filled : failTo);
            }
        }
    }

    /// <summary>
    /// What the shipped flow builds is what a plan check read, after the last word on it, and a later round is audited
    /// before it is built. The rounds' items wait for the item audit as well as for the check, so the audit reads the
    /// tree they are built on and the self-audit after the build reads what it found; the round is judged by that
    /// self-audit, so the item audit's go - said before the build that may settle it - is no term of the rounds' until.
    /// The recovery checks the diagnosis's items once the person's answer, where one was given, made its last word
    /// (redirect) - the diagnostician told what the check finds within its own step, as it answers, so no step after the
    /// check sends its problems back again - and builds the items that check read, never the diagnosis's own list.
    /// </summary>
    [Fact]
    public void WhatTheShippedFlowBuilds_IsWhatAPlanCheckRead_AfterTheLastWordOnIt()
    {
        var definition = WorkflowTemplates.ShippedDefinition();
        var recover = definition.Recover!.Steps;

        Assert.Equal(["item-problems", "item-audit"], StepAt(definition.Steps, "rounds", "items").DependsOn);
        Assert.Equal("item-check.items", StepAt(definition.Steps, "rounds", "items").ForEach);
        Assert.Equal(
            "self-audit.clean && (gate.passed ?? true) && (mutations.passed ?? true) && empty(round-checks.findings) && empty(run.nextRound)",
            StepAt(definition.Steps, "rounds").Repeat!.Until);

        Assert.Equal(["direction"], StepAt(recover, "redirect").DependsOn);
        Assert.Equal("(direction.answer ?? '') == 'continue'", StepAt(recover, "redirect").When);
        Assert.Equal(["redirect"], StepAt(recover, "remedy-check").DependsOn);
        Assert.Equal("diagnose.latest.items", StepAt(recover, "remedy-check").Items);
        Assert.Equal(["remedy-check"], StepAt(recover, "remedies").DependsOn);
        Assert.Equal("remedy-check.items", StepAt(recover, "remedies").ForEach);
        Assert.Equal(["remedy-check"], [.. recover.Where(step => step.Type == "validate-plan").Select(step => step.Name)]);

        // The diagnostician is told the check's problems within its own step, for the check reads the newest answer of
        // the conversation the redirect goes on with - what it said first, or on the person's answer - and no other step
        // goes on with it to say them again.
        Assert.Equal(["diagnose", "redirect"], [.. recover.Where(step => step.Resume == "diagnose").Select(step => step.Name)]);
        Assert.DoesNotContain("prompts/remedy-revise.md", WorkflowTemplates.Files);
    }

    /// <summary>
    /// The help, the README and the architecture say how a pre-existing issue is reported, triaged, fixed at its root or
    /// listed, as the grammar has it.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhereAPreExistingIssueGoes()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            $"That step says raised: {WorkflowStep.RaisedByPreExisting} - it is raised once per issue: a repeat of the same title at the same place raises nothing while a triage of it holds the issue, the run's records saying so, unless it stops the work of a step that triage will not run again, and one whose triage failed, or met again after its fix was done, is raised anew; and it is never scheduled, as the recovery is not - and "
                + "onAddress: recover: it reproduces the issue on the run's base commit, gives the recovery one that is in a step's way, related to "
                + "the work or critical, to fix at its root",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "lists the rest, proven, with a title and a description, in the run's `pre-existing-issues.json`, shown in the pull request's body and the handoff",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "The validator holds the wiring (V51): the triage is at the top level, waits for nothing, nothing waits for it and no handler sends the run to it",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The help, the README and the architecture say when the recovery takes an issue sent to fix - at once, beside any
    /// other fix at work, save one a step of a visit reported or one a visit at work fixes already - that a step two issues
    /// stop runs again once, and what an issue being fixed holds: its triage's paths and its remedies', which a work item
    /// that has not begun waits on, every other going on; and the orchestrators topic says such a wait among what a run
    /// waits for.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayWhenTheRecoveryTakesAnIssue_AndWhatItsFixHolds()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "An issue sent to fix is the recovery's at once, its visit beside any other at work - save one a step of a visit of the recovery reported, which waits for that visit to end, for the recovery never calls itself, and one a visit at work fixes already for another step it stopped, which waits for that visit; a step two issues stop runs again once, after both are settled. "
                + "From the moment it is sent to fix until its visit ends, the issue holds the files its triage's where names - each a path from the repository's root, never a command - and, once a check of its visit has read the remedy items its diagnosis gives, the paths those items own: an instance of the work-item group that has not begun and owns one of them, or whose item names the issue's id in its needs - the fix it builds on - starts once that visit has ended, building on the tree the fix leaves,",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "an instance waiting on another, on the resume, on room - a claim, the run to be quiet, a counter of its limits - or on the fix of an issue that holds a file its work item owns or that the item needs,",
            Prose.Words(DssHarness.Core.Help.HelpTopics.Render("orchestrators")),
            StringComparison.Ordinal);
        Assert.Contains(
            "An issue sent to fix is the recovery's at once, its fix beside any other at work - save one that a step of the recovery itself reported, which waits for that visit to end, and one a fix at work already mends for another step, which waits for that fix; a step two issues stop runs again once, after both are settled.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "a work item that has not begun and owns one of them, or names the issue under `needs` - the fix it builds on - starts once that fix is done, building on the tree it leaves, and `ask-orchestrator-status` says what it waits for; every other item goes on at once.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "only one that a step of a visit reported waits, for that visit to end - the recovery never calls itself (V50) - and one a visit at work fixes already, for another step it stopped, waits for that visit,",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"the issue holds the files its triage's `where` names, each entry a path from the repository's root and never the command it may name instead (`{nameof(FlowIssueHold)}.{nameof(FlowIssueHold.IsPath)}`), and, once a check of its visit has read the remedy items its diagnosis gives, every path those items own: `{nameof(FlowIssueHold)}`, the one owner of what an issue holds and which of it a work item owns,",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"its record's `waitsOn` names the fix, `{FlowWaits.Fix("<title>", "<visit>", ["<paths>"])}` (`{nameof(FlowWaits)}.{nameof(FlowWaits.Fix)}`), `{FlowWaits.Fix("<title>", "<visit>", [], needed: true)["the fix of '<title>' (<visit>), ".Length..]}` where the item names it, and `ask-orchestrator-status` says it among what the run waits for.",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"its `needs`, read by `{nameof(FlowWorkItems)}.{nameof(FlowWorkItems.NeedsOf)}` alone and met by `{nameof(FlowIssueHold)}.{nameof(FlowIssueHold.NeededBy)}`, the issue's id (`{nameof(FlowIssueHold)}.{nameof(FlowIssueHold.Issue)}`) its triage's,",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            $"and `{nameof(PlanCheckRunner)}` refuses an item whose `needs` names another,",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The workflows help, the README and the architecture say how issues of one root cause take one fix: the ids every
    /// step told the issues reads, the join a triage answers, refused where it names an issue taken after its own; the
    /// joined issue going with the fix it joined, read by its visit, settled by its end and said fixed with it; one that
    /// joined while a visit was diagnosing taking its own after; and one whose joined fix ended without it triaged again.
    /// </summary>
    [Fact]
    public void TheHelpTheReadmeAndTheArchitecture_SayHowIssuesOfOneRootCauseTakeOneFix()
    {
        var help = Prose.Words(DssHarness.Core.Help.HelpTopics.Render("workflows"));
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains(
            "The triage is told them too, its own left out, with the ids of those it may join, and names by its id the one its issue repeats (duplicate, in duplicates) or whose root cause it shares (join, in joins) - only one taken before its own, or the step fails, so no two issues ever name each other.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "An issue joined to another, being triaged or being fixed, takes no visit of its own: it goes with that issue's fix - waiting for its triage, where it is still triaged - and a visit whose diagnosis began after the join reads every issue joined to it as source.joined, each with its id, its end settling all of them, run.preExisting saying each 'fixed with <id>' and a step one of them stopped running again. "
                + "One that joined while a visit of that fix was diagnosing takes a visit of its own once that visit has fixed it, which reads what it did as source.joinedTo; and where the issue it joined ends without a fix, it is triaged again on its own.",
            help,
            StringComparison.Ordinal);
        Assert.Contains(
            "Issues of one root cause take one fix: a triage that finds its issue's cause is that of an issue taken before it, being triaged or being fixed, answers `join` with that issue's id, and its issue takes no visit of its own.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "One that joins while a visit of that fix is already diagnosing takes a visit of its own once that visit has fixed it, told what it fixed; and where the issue it joined ends without a fix, it is triaged again on its own.",
            readme,
            StringComparison.Ordinal);
        Assert.Contains(
            "each `<id>: <what became of it> - <title> (<where>)`, the id a listed issue's `pe-<n>`, one owed the next round's `nx-<n>`, and every other's its triage instance's (`FlowCoordinator.Said`)",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "An issue whose triage answers `join` takes no visit of its own: it goes with the fix of the issue it joined, read from the run's state alone (`FlowCoordinator.JoiningOf`,",
            architecture,
            StringComparison.Ordinal);
        Assert.Contains(
            "the joined issue is triaged again on its own: `SettleJoins` raises a triage of the same step for it, as its reporter's report raised the first, the records saying why,",
            architecture,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner's prompts and the self-audit leave an issue the recovery is fixing to the recovery: each reads
    /// <c>{run.preExisting}</c> and, right after it, the workflow's text <c>fixes</c> - the one file that says that no work
    /// item fixes an issue the list says is being fixed or rewrites its lines, and that an item needing its fix names it
    /// under <c>needs</c> and is held until that fix has ended - a plan that folded the fix into an item of its own would
    /// build the fix's lines beside the recovery's remedy. No other shipped file says it, so the promise of a wait is made
    /// once, where the engine keeps it (<see cref="FlowIssueHold.NeededBy"/>).
    /// </summary>
    [Fact]
    public void ThePlannersPromptsAndTheSelfAudit_LeaveAnIssueBeingFixedToTheRecovery()
    {
        const string Rule = "An issue the list says is being fixed is the recovery's: no work item fixes it or rewrites its lines. An item that needs its fix names the issue under needs, by its id as the list gives it, and the run holds the item until that fix has ended, so it builds on the tree the fix leaves; an item that owns a file the fix holds waits for it alike.";
        string[] told = ["prompts/plan-update.md", "prompts/plan.md", "prompts/replan.md", "prompts/self-audit.md"];

        Assert.Equal("fixes.md", WorkflowTemplates.ShippedDefinition().Texts!["fixes"]);
        Assert.Equal<IEnumerable<string>>(
            ["fixes.md"],
            [.. WorkflowTemplates.Files.Where(file => Prose.Words(WorkflowTemplates.TextOf(file)!).Contains("is the recovery's: no work item fixes it", StringComparison.Ordinal))]);
        Assert.StartsWith(Rule, Prose.Words(WorkflowTemplates.TextOf("fixes.md")!), StringComparison.Ordinal);
        Assert.Equal<IEnumerable<string>>(
            told,
            [.. WorkflowTemplates.Files.Where(file => WorkflowTemplates.TextOf(file)!.Contains("{text.fixes}", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);

        foreach (var prompt in told)
        {
            var words = Prose.Words(WorkflowTemplates.TextOf(prompt)!);

            // The list the text names is the one the prompt reads, right before it, and the text is read once.
            Assert.Contains("its title and where; empty where none is: {run.preExisting} {text.fixes}", words, StringComparison.Ordinal);
            Assert.Equal(words.IndexOf("{text.fixes}", StringComparison.Ordinal), words.LastIndexOf("{text.fixes}", StringComparison.Ordinal));
        }

        // The member the text names is the work item's, and its description says what it holds, not the rule again.
        var needs = JsonNode.Parse(WorkflowTemplates.TextOf("schemas/plan.schema.json")!)!["$defs"]!["workItem"]!["properties"]!["needs"]!;

        Assert.Equal("array", needs["type"]!.GetValue<string>());
        Assert.DoesNotContain("waits", needs["description"]!.GetValue<string>(), StringComparison.Ordinal);
    }

    /// <summary>
    /// The recovery's reasoning prompts give the issues joined to the one a visit fixes, which its fix settles too - the
    /// diagnosis, its second word after a failure and the redirect on a person's answer, each reading
    /// <c>{source.joined}</c> once - and the diagnosis, beside it, what the fix an issue waited for
    /// did (<c>{source.joinedTo}</c>); the triage's prompt reads no list of its own, for the engine tells it the issues it
    /// may name at its end, and asks for the id it joins.
    /// </summary>
    [Fact]
    public void TheRecoverysPrompts_GiveTheIssuesJoinedToTheOneItFixes_AndTheTriagesAsksWhichItJoins()
    {
        string[] told = ["prompts/diagnose.md", "prompts/rediagnose.md", "prompts/redirect.md"];

        Assert.Equal<IEnumerable<string>>(
            told,
            [.. WorkflowTemplates.Files.Where(file => WorkflowTemplates.TextOf(file)!.Contains("{source.joined}", StringComparison.Ordinal)).Order(StringComparer.Ordinal)]);

        foreach (var prompt in told)
        {
            var text = WorkflowTemplates.TextOf(prompt)!;
            Assert.Equal(text.IndexOf("{source.joined}", StringComparison.Ordinal), text.LastIndexOf("{source.joined}", StringComparison.Ordinal));
        }

        var diagnose = Prose.Words(WorkflowTemplates.TextOf("prompts/diagnose.md")!);
        Assert.Contains("which this visit's fix settles too - each with its id, title, description, where and proof; empty where none is: {source.joined}", diagnose, StringComparison.Ordinal);
        Assert.Contains("start from the tree they left; empty otherwise: {source.joinedTo}", diagnose, StringComparison.Ordinal);
        Assert.Contains("that the proof no longer shows the issue - nor the proof of any issue joined to it.", diagnose, StringComparison.Ordinal);

        var triage = Prose.Words(WorkflowTemplates.TextOf("prompts/pre-existing.md")!);
        Assert.DoesNotContain("{run.preExisting}", triage, StringComparison.Ordinal);
        Assert.Contains("is given at the end of this prompt, each with its id, what became of it, its title and where, with the ids of those yours may join", triage, StringComparison.Ordinal);
        Assert.Contains("the id it duplicates, where it does; and the id it joins, where it does.", triage, StringComparison.Ordinal);
    }

    /// <summary>
    /// The README and the architecture say how a failure reaches the recovery and where the run goes after it, as the
    /// grammar has it: a step says onFailure: recover, the recovery says onSuccess and onFailure, and a reasoning step is
    /// never recovered.
    /// </summary>
    [Fact]
    public void TheReadmeAndTheArchitecture_SayHowAFailureReachesTheRecovery_AndWhereTheRunGoesAfter()
    {
        var readme = Prose.Document("README.md");
        var architecture = Prose.Document("docs", "architecture.md");

        Assert.Contains("A step gives a failure nothing handles to the workflow's `recover` by saying `onFailure: recover`, itself or through a group around it", readme, StringComparison.Ordinal);
        Assert.Contains($"`onSuccess` says - `{FlowRecover.FailedStep}`, the step that gave it its failure, which runs again from where it stopped", readme, StringComparison.Ordinal);
        Assert.Contains("A reasoning step is never recovered: a failed agent is tried again (`retries`, waiting `backoffSeconds`, twice as long each time)", readme, StringComparison.Ordinal);
        Assert.Contains($"`onSuccess` says - `{FlowRecover.FailedStep}`, the step that gave the recovery its failure, which runs again from where it stopped", architecture, StringComparison.Ordinal);
        Assert.Contains($"`onFailure` says once its steps' retries are spent: `{FlowRecover.PauseOnFailure}` or `{FlowRecover.StopOnFailure}` - never the recovery again", architecture, StringComparison.Ordinal);
        Assert.Contains("A reasoning step is never recovered, by its own word or a group's", architecture, StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner is told the issues the run owes its next round wherever it writes the plan - planning it, revising it
    /// and updating it - as the self-audit is, each to be a work item naming its id in <c>settles</c>: the round's check
    /// refuses a round that leaves one unsettled, and a plan update the check raises for one adds the item that settles it.
    /// </summary>
    [Fact]
    public void ThePlannersPrompts_ListTheIssuesOwedTheNextRound_EachAWorkItemThatSettlesIt()
    {
        const string Listed = "The issues this run owes its next round, each with its id and its size - the files its fix touches, what must be built, and what must be measured first: {run.nextRound}";

        foreach (var prompt in new[] { "prompts/plan.md", "prompts/replan.md", "prompts/plan-update.md", "prompts/self-audit.md" })
        {
            Assert.Contains(Listed, Prose.Words(Read(prompt)), StringComparison.Ordinal);
        }

        Assert.Contains(
            "Each issue listed becomes a work item of the plan, which its first round builds before the rest: make it one, with its size and a class - it is a defect too, and the rules of a class below apply to it - and name the issue it settles in `settles`, by its id exactly. The round's check refuses a round whose items leave one unsettled.",
            Prose.Words(Read("prompts/plan.md")),
            StringComparison.Ordinal);
        Assert.Contains(
            "Each issue listed is a work item of the plan, which its first round builds before the rest, naming the issue it settles in `settles` by its id exactly: keep the one the plan has, or make one, with its size and a class, where it has none.",
            Prose.Words(Read("prompts/replan.md")),
            StringComparison.Ordinal);
        Assert.Contains(
            "an issue the check says is owed the next round and settled by no work item is an `added` item that settles it, as below.",
            Prose.Words(Read("prompts/plan-update.md")),
            StringComparison.Ordinal);
        Assert.Contains(
            "Each issue listed becomes a work item of the round, built before the rest, naming the issue it settles in `settles` by its id exactly: where no item of the plan settles it yet, add one, with its size and a class, as an `added` piece.",
            Prose.Words(Read("prompts/plan-update.md")),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner names the run's base commit, never a branch, where an item's definition of done compares with the tree
    /// before the work: a builder works in a repository of its own that holds no branch, where <c>main</c> names nothing,
    /// and a statement that names it can never hold there.
    /// </summary>
    [Fact]
    public void ThePlanner_NamesTheBaseCommit_NeverABranch_InADefinitionOfDone()
    {
        var plan = Read("prompts/plan.md").ReplaceLineEndings("\n");

        Assert.Contains(
            "a statement that compares with the tree before the work naming the run's base commit,\n   `{orient.startCommit}`, never a branch: a builder works in a repository of its own that holds no branch, so\n   `main` names nothing there;",
            plan,
            StringComparison.Ordinal);
        Assert.Contains(
            "a statement that compares with the tree before the work names the\n   run's base commit, `{orient.startCommit}`, never a branch, which names nothing in the repository of its own a\n   builder works in.",
            Read("prompts/plan-audit.md").ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// The planner writes each axis of a universe whole, from a search of the tree for every cell of its kind, and a
    /// definition of done the base commit would fail for what the work leaves alone narrowed to what it changes; the
    /// auditor names every cell an axis leaves out in one finding, and the replan closes the axis whole - so a plan
    /// converges, never held back round after round for the next cell of the same axis.
    /// </summary>
    [Fact]
    public void ThePlannerAndItsAuditor_CloseAnAxisWhole_AndNarrowADefinitionOfDoneTheBaseFails()
    {
        var plan = Read("prompts/plan.md").ReplaceLineEndings("\n");

        Assert.Contains(
            "An axis the decision ranges over gives its values whole, from\n   a search of the tree for every cell of its kind - for the legs and the references, every file of every service that\n   builds, tests, lints or checks the repository, never only the CI it names first - recorded as a class's searches\n   are,",
            plan,
            StringComparison.Ordinal);
        Assert.Contains(
            "a statement the base commit would already fail for what the work does not touch - a\n   lint, a limit or a count over a whole file or the tree - checked there first, and narrowed to what the work changes\n   where the base fails it;",
            plan,
            StringComparison.Ordinal);
        Assert.Contains(
            "an axis\n    that leaves out a cell searched whole by you too, every cell of it the universe leaves out named in the one\n    finding,",
            Read("prompts/plan-audit.md").ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
        Assert.Contains(
            "the axis was not searched whole: search the tree for every cell of that\naxis, as a class's searches are made, and hold every one you find, never only the one it named",
            Read("prompts/replan.md").ReplaceLineEndings("\n"),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// A class's universe answers every axis the repository's closure text lists by its structure, so an omission is the
    /// plan step's own to correct and never costs an audit or another planning iteration: the plan's schema holds a
    /// member for each - its values and where they are declared, or why it does not apply - and the further axes beside
    /// them, and the gate every client's answer passes sends back, within the step, a universe that leaves an axis
    /// unanswered, an axis said not to apply with no reason, an axis said to apply with no value, and a further axis with
    /// no name; a universe that answers each reads.
    /// </summary>
    [Fact]
    public void APlanWhoseUniverseLeavesAnAxisUnanswered_IsSentBackWithinThePlanStep()
    {
        var gate = new StructuredOutputGate(new AgentOutput("plan", Read("schemas/plan.schema.json")), new Masking());
        const string At = "$.output.items[0].classes[0].universe";

        Assert.Empty(gate.Problems(Envelopes.Of(PlanAnswer(Universes.Of(("formats", ["cs", "md"]), ("region", ["eu", "us"]))))));

        var unanswered = Universes.Of(("formats", ["cs", "md"]));
        unanswered.Remove("configurations");
        var unreasoned = Universes.Of(("formats", ["cs", "md"]));
        unreasoned["forms"] = Universes.NotApplying(" ");
        var valueless = Universes.Of(("formats", ["cs", "md"]));
        valueless["tiers"] = Universes.Applies();
        var unnamed = Universes.Of(("formats", ["cs", "md"]));
        unnamed["further"] = new JsonArray(new JsonObject { ["name"] = "", ["answer"] = Universes.Applies("eu") });

        Assert.Equal([$"{At}: 'configurations' is required and missing"], gate.Problems(Envelopes.Of(PlanAnswer(unanswered))));
        // Where neither shape of an axis answers, the problem says what each objected to, so the planner sees which it meant.
        Assert.Equal(
            [$"{At}.forms: answers none of the 2 shapes its schema offers - shape 1 ({At}.forms: 'values' is required and missing; {At}.forms: 'declaredIn' is required and missing; {At}.forms.why: is said by no property of the schema), shape 2 ({At}.forms.why: \" \" does not match its pattern)"],
            gate.Problems(Envelopes.Of(PlanAnswer(unreasoned))));
        Assert.Equal(
            [$"{At}.tiers: answers none of the 2 shapes its schema offers - shape 1 ({At}.tiers.values: holds 0 items and at least 1 is asked), shape 2 ({At}.tiers: 'why' is required and missing; {At}.tiers.values: is said by no property of the schema; {At}.tiers.declaredIn: is said by no property of the schema)"],
            gate.Problems(Envelopes.Of(PlanAnswer(valueless))));
        Assert.Equal([$"{At}.further[0].name: \"\" does not match its pattern"], gate.Problems(Envelopes.Of(PlanAnswer(unnamed))));
    }

    /// <summary>
    /// The universe is defined once: a self-audit's and a diagnosis's items take the plan's work item whole, the class,
    /// its universe and the two answers an axis has among it, never a second definition; the axes it answers are the
    /// eight the closure text lists, and the closure text names each by its member, and no other.
    /// </summary>
    [Fact]
    public void TheUniverse_IsDefinedOnce_AndTheClosureTextNamesEachOfItsAxes()
    {
        var plan = JsonNode.Parse(Read("schemas/plan.schema.json"))!;

        foreach (var sharing in new[] { "schemas/self-audit.schema.json", "schemas/diagnosis.schema.json" })
        {
            var whole = JsonSchemaSubset.Whole(Read(sharing), beside => NamedFiles.RootOf(WorkflowTemplates.TextOf(NamedFiles.Beside(sharing, beside))));

            Assert.Empty(whole.Problems);

            foreach (var definition in new[] { "closureClass", "axisApplies", "axisDoesNotApply" })
            {
                Assert.True(JsonNode.DeepEquals(plan["$defs"]![definition], whole.Schema!["$defs"]![definition]), $"{sharing} holds the plan's {definition}");
            }
        }

        Assert.Equal(["kinds", "targets", "configurations", "formats", "entryPoints", "tiers", "forms", "directions"], Universes.Shipped);

        var closure = Read("closure.md").ReplaceLineEndings("\n");
        var axes = closure[closure.IndexOf("## The axes", StringComparison.Ordinal)..closure.IndexOf("## The references", StringComparison.Ordinal)];

        Assert.Equal(Universes.Shipped, [.. Regex.Matches(axes, @"\(`([A-Za-z]+)`\)").Select(match => match.Groups[1].Value)]);
    }

    /// <summary>
    /// The audits judge each answer of a universe - an axis said not to apply that the code shows the decision ranges
    /// over, an axis whose values leave out a cell - and never look for an axis left out, which the plan's schema holds:
    /// an audit that holds a plan back for an omission a machine sees costs a whole planning iteration.
    /// </summary>
    [Fact]
    public void TheAudits_JudgeEachAnswerOfAUniverse_NeverAnAxisLeftOut()
    {
        var planAudit = Prose.Words(Read("prompts/plan-audit.md"));
        var itemAudit = Prose.Words(Read("prompts/item-audit.md"));

        Assert.Contains(
            "its schema has it answer every axis the text lists, so what you judge is each answer: an axis said not to apply that the code shows the decision ranges over, with what shows it, and an axis that leaves out a cell",
            planAudit,
            StringComparison.Ordinal);
        Assert.Contains(
            "its schema has it answer every axis the text lists: an axis it says does not apply that the code shows the decision ranges over is a finding, with what shows it, and so is an axis whose values leave out a cell",
            itemAudit,
            StringComparison.Ordinal);

        foreach (var audit in new[] { planAudit, itemAudit, Prose.Words(Read("schemas/plan-audit.schema.json")) })
        {
            Assert.DoesNotContain("without a reason", audit, StringComparison.Ordinal);
        }
    }

    /// <summary>A plan as the planner answers it: one work item, deciding one class over <paramref name="universe"/>.</summary>
    private static JsonObject PlanAnswer(JsonObject universe)
        => new()
        {
            ["summary"] = "One item closes the rate.",
            ["done"] = "the rate is decided in one place",
            ["source"] = "made",
            ["departures"] = new JsonArray(),
            ["items"] = new JsonArray(new JsonObject
            {
                ["name"] = "one",
                ["title"] = "The rate is decided in one place",
                ["instructions"] = "Decide the rate at its home.",
                ["done"] = "the rate is decided at its home",
                ["ownedPaths"] = new JsonArray("src/**"),
                ["forbiddenPaths"] = new JsonArray(),
                ["reuses"] = new JsonArray(new JsonObject { ["path"] = "src/Home.cs", ["what"] = "the rate's home" }),
                ["creates"] = new JsonArray(),
                ["replaces"] = new JsonArray(),
                ["tests"] = new JsonArray("HomeTests"),
                ["noTests"] = "",
                ["classes"] = new JsonArray(new JsonObject
                {
                    ["id"] = "C1",
                    ["invariant"] = "The rate is decided by the home for every format",
                    ["home"] = new JsonObject { ["path"] = "src/Home.cs", ["symbol"] = "Home", ["exists"] = true },
                    ["sites"] = new JsonArray(new JsonObject { ["path"] = "src/Home.cs", ["symbol"] = "Home", ["decides"] = "the rate", ["then"] = "home" }),
                    ["searches"] = new JsonArray(new JsonObject { ["data"] = "the rate", ["via"] = "graph", ["query"] = "rate:", ["hits"] = 1 }),
                    ["universe"] = universe.DeepClone(),
                    ["action"] = "fix-at-home",
                    ["notCovered"] = "",
                }),
                ["noClass"] = "",
            }),
            ["scope"] = new JsonArray(),
            ["preExisting"] = new JsonArray(),
        };

    private static string Read(string below)
    {
        var assembly = typeof(WorkflowTemplates).Assembly;

        // The name's tail is spelt with the machine's separator; the match is made on the '/' form.
        var name = assembly.GetManifestResourceNames()
            .Single(resource => resource.Replace('\\', '/').Contains(".WorkflowTemplates/", StringComparison.Ordinal)
                && resource.Replace('\\', '/').EndsWith("/" + below, StringComparison.Ordinal));

        using var reader = new StreamReader(assembly.GetManifestResourceStream(name)!);

        return reader.ReadToEnd();
    }

    private static WorkflowStep StepAt(IReadOnlyList<WorkflowStep> steps, params string[] path)
    {
        var step = steps.Single(candidate => candidate.Name == path[0]);

        return path.Length == 1 ? step : StepAt(step.Steps, path[1..]);
    }

    private static int Count(IReadOnlyList<WorkflowStep> steps, string? type = null)
        => steps.Count(step => type is null || step.Type == type)
            + steps.Where(step => step.Steps.Count > 0).Sum(step => Count(step.Steps, type));

    /// <summary>The real file system, but one file by its name cannot be read, as one another process holds.</summary>
    private sealed class UnreadDefinition(IFileSystem inner, string name) : PassThroughFileSystem(inner)
    {
        public override Stream OpenRead(string path)
            => Path.GetFileName(path) == name ? throw new IOException("it is held.") : base.OpenRead(path);
    }
}