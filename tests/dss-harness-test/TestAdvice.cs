using DssHarness.Core.Orchestration.Flow;

namespace DssHarness.Tests;

/// <summary>
/// The advisor every workflow a run is started of names (V43) and every question the engine raises is recommended by,
/// for tests whose fixtures are about something else: the block a definition or a workflow file that names none is
/// given - the agent and prompt names init writes, on the configuration's default tier - with the files it names, the
/// providers init writes for it to run on, and a runner that answers every asking at once with no recommendation,
/// spending nothing - so a question is asked as it is in a run, its recommendation sought and said not obtained, and
/// nothing else of the test's run changes.
/// </summary>
internal static class TestAdvice
{
    /// <summary>The advisor block a test's definition is given where it names none: the agent and prompt init writes, its model the configuration's default tier.</summary>
    public static FlowAdvisor Advisor { get; } = new("advisor", "prompts/recommend.md", Tier: null, Model: null, Effort: null, Client: null);

    /// <summary>Why the test's advisor obtains no recommendation, as every question it is asked of says it.</summary>
    public const string NotObtained = "the test's advisor recommends nothing";

    /// <summary>The test's advisor as a workflow file names it at its top level: <see cref="Advisor"/>'s agent and prompt.</summary>
    public const string Block = "advisor:\n  agent: advisor\n  prompt: prompts/recommend.md\n";

    /// <summary>
    /// The files <see cref="Block"/> names, by their paths in the workflow's directory: an agent definition that only
    /// reads, and a prompt that reads the question - each as the definition's check holds it to be there.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Files { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["agents/advisor.md"] = "---\nname: advisor\ndescription: Recommends an answer to a question a run puts to a person\nwrites: false\n---\nYou recommend the answer you would give, and why.\n",
        ["prompts/recommend.md"] = "Recommend an answer to the question of {question.step}: {question.problem}\n\nThe answers: {question.answers}\n",
    };

    /// <summary>
    /// A configuration declaring <paramref name="workflows"/> and the agent providers init writes - their tiers and the
    /// default tier with them - as a repository init made holds them: the advisor every workflow names runs on a model,
    /// and a configuration that declares none refuses it (V38), as it refuses any agent of a workflow.
    /// </summary>
    /// <param name="workflows">The workflows the configuration declares.</param>
    public static DssHarness.Core.Configuration.HarnessConfig Config(params string[] workflows)
        => new()
        {
            Workflows = [.. workflows],
            Defaults = new() { AgentTier = AgentTier },
            Providers = Providers,
            TierConfig = TierConfig,
        };

    /// <summary>The default tier init writes (<see cref="Config"/>), for a test that builds its own configuration.</summary>
    public static string? AgentTier => DssHarness.Core.Agents.AgentProviderTemplates.Read().AgentTier;

    /// <summary>The agent providers init writes (<see cref="Config"/>), a list of the test's own each time.</summary>
    public static List<DssHarness.Core.Configuration.AgentProviderConfig> Providers => [.. DssHarness.Core.Agents.AgentProviderTemplates.Read().Providers];

    /// <summary>The tiers init writes (<see cref="Config"/>), a table of the test's own each time.</summary>
    public static Dictionary<string, List<string>> TierConfig
        => new(DssHarness.Core.Agents.AgentProviderTemplates.Read().TierConfig, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// <paramref name="workflow"/>, a workflow file's text, as a run is started of it: naming the test's advisor at its
    /// top level, after its name, where it names none (<see cref="NamesOne"/>), and as written where it names one.
    /// </summary>
    /// <param name="workflow">The file's text.</param>
    public static string Named(string workflow)
    {
        if (NamesOne(workflow))
        {
            return workflow;
        }

        var lines = workflow.Split('\n').ToList();
        var name = lines.FindIndex(line => line.StartsWith("name:", StringComparison.Ordinal));
        var end = workflow.Contains("\r\n", StringComparison.Ordinal) ? "\r" : string.Empty;
        lines.InsertRange(name + 1, Block.TrimEnd('\n').Split('\n').Select(line => line + end));
        return string.Join('\n', lines);
    }

    /// <summary>The files a run of <paramref name="workflow"/> reads of the test's advisor: <see cref="Files"/> where <see cref="Named(string)"/> names it, none where the workflow names its own.</summary>
    /// <param name="workflow">The file's text, as the test wrote it.</param>
    public static IReadOnlyDictionary<string, string> FilesOf(string workflow)
        => NamesOne(workflow) ? new Dictionary<string, string>(StringComparer.Ordinal) : Files;

    /// <summary>
    /// Writes <paramref name="workflow"/> as the file of the workflow <paramref name="name"/> in <paramref name="repository"/>'s
    /// workflows directory, as a run is started of it (<see cref="Named(string)"/>), with the files of the test's advisor beside it
    /// where that is the advisor it names.
    /// </summary>
    /// <param name="repository">The repository.</param>
    /// <param name="name">The workflow's name, which is its directory's and its file's.</param>
    /// <param name="workflow">The file's text, as the test wrote it.</param>
    public static void WriteWorkflow(TempDirectory repository, string name, string workflow)
    {
        repository.WriteFile($".harness-config/workflows/{name}/{name}.yml", Named(workflow));

        foreach (var (path, text) in FilesOf(workflow))
        {
            repository.WriteFile($".harness-config/workflows/{name}/{path}", text);
        }
    }

    /// <summary>
    /// Writes <paramref name="workflow"/> at <paramref name="file"/>, a workflow's file, as a run is started of it
    /// (<see cref="Named(string)"/>), with the files of the test's advisor in its directory where that is the advisor it names.
    /// </summary>
    /// <param name="file">The workflow's file, its full path.</param>
    /// <param name="workflow">The file's text, as the test wrote it.</param>
    public static void WriteWorkflowFile(string file, string workflow)
    {
        var directory = Path.GetDirectoryName(file)!;

        Directory.CreateDirectory(directory);
        File.WriteAllText(file, Named(workflow));

        foreach (var (path, text) in FilesOf(workflow))
        {
            var written = Path.Combine([directory, .. path.Split('/')]);

            Directory.CreateDirectory(Path.GetDirectoryName(written)!);
            File.WriteAllText(written, text);
        }
    }

    /// <summary>Whether <paramref name="workflow"/> names an advisor of its own at its top level.</summary>
    /// <param name="workflow">The file's text.</param>
    public static bool NamesOne(string workflow)
        => workflow.Split('\n').Any(line => line.StartsWith("advisor:", StringComparison.Ordinal));

    /// <summary>A runner that answers every asking at once, recommending nothing and spending nothing.</summary>
    public static IFlowAdvisorRunner None { get; } = new Nothing();

    /// <summary>
    /// <paramref name="definition"/> as a run is started of it: with the test's advisor where it names none, its own
    /// where it names one.
    /// </summary>
    /// <param name="definition">A test's definition.</param>
    public static WorkflowDefinition Named(WorkflowDefinition definition)
        => definition.Advisor is null ? definition with { Advisor = Advisor } : definition;

    /// <summary>
    /// A run of <paramref name="definition"/> as a session of a run is built: its workflow naming an advisor - the test's,
    /// where it names none (<see cref="Named(WorkflowDefinition)"/>) - and given a runner for it - the test's own, where it gives one, else
    /// <see cref="None"/> - so every question its steps raise is recommended before it is asked, as a run's is. Every other
    /// argument is the coordinator's own, by the same names and in the same order.
    /// </summary>
    public static FlowCoordinator Coordinator(
        WorkflowDefinition definition,
        IFlowStepRunner runner,
        IFlowRunnerFacts runnerFacts,
        TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        IFlowRecords? records = null,
        FlowResumeState? resume = null,
        string? runName = null,
        FlowRunPlace? place = null,
        IReadOnlyDictionary<string, string>? agentModels = null,
        Func<string?, string, string>? workflowFile = null,
        Func<string, string, CancellationToken, Task<DssHarness.Core.Results.CommandOutcome>>? deleteAgent = null,
        IFlowAdvisorRunner? advisor = null,
        IFlowStreamAccount? streamAccount = null,
        Func<FlowUpdatesFile?>? updates = null,
        Func<string, string?>? workflowFileIfThere = null,
        Func<FlowInstance, string, string?>? runFacts = null,
        Func<string, bool>? agentLive = null,
        Func<string, bool>? startTreeDirectories = null)
        => new(
            Named(definition),
            runner,
            runnerFacts,
            clock,
            inputs,
            records,
            resume,
            runName,
            place,
            agentModels,
            workflowFile,
            deleteAgent,
            advisor ?? None,
            streamAccount,
            updates,
            workflowFileIfThere,
            runFacts,
            agentLive,
            startTreeDirectories);

    /// <summary>The runner <see cref="None"/> is: no recommendation, at once.</summary>
    private sealed class Nothing : IFlowAdvisorRunner
    {
        /// <inheritdoc/>
        public Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken)
            => Task.FromResult(new FlowAdvice(Answer: null, Why: null, Text: null, NotObtained: NotObtained));
    }
}
