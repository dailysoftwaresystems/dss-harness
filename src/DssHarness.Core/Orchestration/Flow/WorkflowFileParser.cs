using System.Diagnostics;
using DssHarness.Core.Configuration;
using DssHarness.Core.Output;
using DssHarness.Core.Results;
using DssHarness.Core.Yaml;
using YamlDotNet.RepresentationModel;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Reads a workflow's definition file: <c>.harness-config/workflows/&lt;name&gt;/&lt;name&gt;.yml</c>.
/// </summary>
/// <remarks>
/// <para>
/// Parsed through the one YAML reader, <see cref="YamlReading"/>, which owns the reasons it reads
/// YamlDotNet's representation model rather than a serialiser: the position of the key that was
/// wrong, and the fact that a key was not recognised at all. It holds a workflow file to the bounds
/// this reader asks of it (<see cref="Bounds"/>) before the file is read into nodes.
/// </para>
/// <para>
/// Every problem in a file is collected and reported together, each with the line and column it was
/// read at. What a definition <em>means</em> - that a step it names is declared, that an expression
/// reads something that exists - is <see cref="WorkflowValidator"/>'s to check, on the parsed
/// definition; this reads the file's shape alone.
/// </para>
/// </remarks>
public static class WorkflowFileParser
{
    /// <summary>How deep steps may nest: a group's steps inside a group's steps, and so on.</summary>
    /// <remarks>
    /// Bounded here, in the reader, the one owner of the rule: a step nested deeper is refused before anything reads
    /// it. The file's YAML is bounded before that, more loosely (<see cref="MaxYamlDepth"/>), so that no file can nest
    /// deeper than the reader itself can bear.
    /// </remarks>
    public const int MaxDepth = 10;

    /// <summary>
    /// How deep the file's YAML may nest at all - mappings and lists inside one another - before it is read into
    /// nodes. A definition that keeps to <see cref="MaxDepth"/> nests a few levels for each level of steps; one past
    /// this bound is no definition, and reading it whole could exhaust the reader before any rule saw it.
    /// </summary>
    public const int MaxYamlDepth = 64;

    /// <summary>
    /// What a workflow file's YAML is held to before it is read into nodes, by the one reader: no alias - <c>*name</c>,
    /// a merge key's included - which repeats a part of the file by reference, and so lets a few hundred bytes stand for
    /// millions of steps; and no nesting past <see cref="MaxYamlDepth"/>, which reading into nodes would recurse through.
    /// </summary>
    private static readonly YamlBounds Bounds = new(
        MaxYamlDepth,
        "a definition writes each of its steps out, so that what it says is what it reads",
        $"no definition does, and steps themselves nest at most {MaxDepth}");

    /// <summary>Top-level keys a workflow file takes.</summary>
    private static readonly IReadOnlyList<string> FileKeys = KeyDescription.Names(WorkflowKeys.File);

    /// <summary>
    /// Parses <paramref name="text"/> as the workflow definition at <paramref name="path"/>.
    /// </summary>
    /// <param name="path">Where the text came from, quoted in every refusal about it.</param>
    /// <param name="text">The file's contents.</param>
    /// <exception cref="HarnessException">The file is not usable. Every problem found is reported together.</exception>
    public static WorkflowDefinition Parse(string path, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(text);

        var problems = new List<string>();
        var root = YamlReading.Root(
            text,
            "a workflow's definition holds one, so that nothing has to decide which of them a run ran",
            problems,
            Bounds);

        if (root is null)
        {
            throw Refuse(path, problems);
        }

        var keys = CollectKeys(root, "the file's top level", FileKeys, problems);

        string? name = null;
        string? description = null;
        int? maxReasoningAgents = null;
        int? maxParallelProcesses = null;
        int? maxOpenAgents = null;
        int? maxRetries = null;
        int? backoffSeconds = null;
        int? maxInstances = null;
        bool? pushCheckpoints = null;
        int? handoffSeconds = null;
        FlowBudget? budget = null;
        string? recommendations = null;
        FlowAdvisor? advisor = null;
        FlowReporter? reporter = null;
        FlowAmend? amend = null;
        var answers = new Dictionary<string, FlowAnswer>(StringComparer.Ordinal);
        var inputs = new List<FlowInput>();
        var parameters = new List<FlowParam>();
        var outputs = new List<WorkflowOutput>();
        List<WorkflowStep>? steps = null;
        FlowRecover? recover = null;
        FlowCodebaseGraph? codebaseGraph = null;

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "name":
                    name = YamlReading.RequireScalar(value, "name", problems);
                    break;

                case "description":
                    description = YamlReading.RequireScalar(value, "description", problems);
                    break;

                case "maxReasoningAgents":
                    maxReasoningAgents = RequireInt(value, "maxReasoningAgents", problems);
                    break;

                case "maxParallelProcesses":
                    maxParallelProcesses = RequireInt(value, "maxParallelProcesses", problems);
                    break;

                case "maxOpenAgents":
                    maxOpenAgents = RequireInt(value, "maxOpenAgents", problems);
                    break;

                case "maxRetries":
                    maxRetries = RequireInt(value, "maxRetries", problems);
                    break;

                case "backoffSeconds":
                    backoffSeconds = RequireInt(value, "backoffSeconds", problems);
                    break;

                case "maxInstances":
                    maxInstances = RequireInt(value, "maxInstances", problems);
                    break;

                case "pushCheckpoints":
                    pushCheckpoints = YamlReading.RequireBoolean(value, "pushCheckpoints", problems);
                    break;

                case "handoffSeconds":
                    handoffSeconds = RequireInt(value, "handoffSeconds", problems);
                    break;

                case "budget":
                    budget = ReadBudget(value, problems);
                    break;

                case "recommendations":
                    recommendations = YamlReading.RequireScalar(value, "recommendations", problems);
                    break;

                case "advisor":
                    advisor = ReadAdvisor(value, problems);
                    break;

                case "reporter":
                    reporter = ReadReporter(value, problems);
                    break;

                case "amend":
                    amend = ReadAmend(value, problems);
                    break;

                case "answers":
                    // YAML refuses a key written twice only where both are written alike; one tagged apart (!!str) comes
                    // through, and is refused here by name.
                    foreach (var (kind, answer) in ReadAnswers(value, "answers", problems))
                    {
                        if (!answers.TryAdd(kind, answer))
                        {
                            problems.Add(YamlReading.At(value, $"answers names the kind '{kind}' more than once"));
                        }
                    }

                    break;

                case "inputs":
                    inputs.AddRange(ReadDeclarations(value, "inputs", WorkflowKeys.Input, problems, ReadInput));
                    break;

                case "params":
                    parameters.AddRange(ReadDeclarations(value, "params", WorkflowKeys.Param, problems, ReadParam));
                    break;

                case "outputs":
                    outputs.AddRange(ReadDeclarations(value, "outputs", WorkflowKeys.Output, problems, ReadOutput));
                    break;

                case "steps":
                    steps = [.. ReadSteps(value, 1, problems)];
                    break;

                case "recover":
                    recover = ReadRecover(value, problems);
                    break;

                case "codebaseGraph":
                    codebaseGraph = ReadCodebaseGraph(value, problems);
                    break;

                default:
                    throw Unread(key);
            }
        }

        if (name is null)
        {
            problems.Add("the file names no 'name'; a workflow's name is its directory's and its file's, said again in the file so a copied directory cannot pass as the workflow it was copied from");
        }

        if (steps is null)
        {
            problems.Add("the file declares no 'steps'; a workflow that runs nothing is a file whose run reports success having done no work.");
        }

        return problems.Count == 0
            ? new WorkflowDefinition(
                name!,
                answers,
                inputs,
                parameters,
                outputs,
                steps ?? [],
                path,
                description,
                maxReasoningAgents,
                maxParallelProcesses,
                maxOpenAgents,
                maxRetries,
                maxInstances,
                pushCheckpoints,
                handoffSeconds,
                budget,
                recommendations,
                advisor,
                reporter,
                amend)
            {
                Recover = recover,
                BackoffSeconds = backoffSeconds,
                CodebaseGraph = codebaseGraph,
            }
            : throw Refuse(path, problems);
    }

    /// <summary>
    /// Reads the <c>codebaseGraph</c> section: each list as written - one given empty kept empty, for the check to refuse
    /// (V52) - and each value as the file holds it.
    /// </summary>
    private static FlowCodebaseGraph? ReadCodebaseGraph(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'codebaseGraph' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "codebaseGraph", KeyDescription.Names(WorkflowKeys.CodebaseGraph), problems);

        IReadOnlyList<string>? include = null;
        IReadOnlyList<string>? exclude = null;
        IReadOnlyList<string>? languages = null;
        int? maxFileBytes = null;
        string? generated = null;
        int? mapTokens = null;

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "include":
                    include = RequireStringList(value, "codebaseGraph.include", problems);
                    break;

                case "exclude":
                    exclude = RequireStringList(value, "codebaseGraph.exclude", problems);
                    break;

                case "languages":
                    languages = RequireStringList(value, "codebaseGraph.languages", problems);
                    break;

                case "maxFileBytes":
                    maxFileBytes = RequireInt(value, "codebaseGraph.maxFileBytes", problems);
                    break;

                case "generated":
                    generated = YamlReading.RequireScalar(value, "codebaseGraph.generated", problems);
                    break;

                case "mapTokens":
                    mapTokens = RequireInt(value, "codebaseGraph.mapTokens", problems);
                    break;

                default:
                    throw Unread(key);
            }
        }

        return new FlowCodebaseGraph(include, exclude, languages, maxFileBytes, generated, mapTokens);
    }

    private static FlowRecover? ReadRecover(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'recover' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "recover", KeyDescription.Names(WorkflowKeys.Recover), problems);
        var maxVisits = IntIn(keys, "recover.maxVisits", problems);
        var onSuccess = RequireScalarIn(keys, "recover.onSuccess", problems);
        var onFailure = RequireScalarIn(keys, "recover.onFailure", problems);
        var stepsNode = NodeIn(keys, "steps");

        if (onSuccess is not null && !FlowRecover.Successes.Contains(onSuccess, StringComparer.Ordinal))
        {
            problems.Add(YamlReading.At(NodeIn(keys, "onSuccess")!, $"'recover.onSuccess' is '{onSuccess}', which is none of {string.Join(", ", FlowRecover.Successes.Select(known => $"'{known}'"))}; a recovery sends the run back to the step that gave it its failure, to run again once it was put right."));
            onSuccess = null;
        }

        // A step's other policies - continue, recover - are read here and refused by the validator in words of their own; a
        // value that is no policy at all is refused here, naming what a recovery may say.
        if (onFailure is not null && !WorkflowStep.FailurePolicies.Contains(onFailure, StringComparer.Ordinal))
        {
            problems.Add(YamlReading.At(NodeIn(keys, "onFailure")!, $"'recover.onFailure' is '{onFailure}', which is none of {string.Join(", ", FlowRecover.Failures.Select(known => $"'{known}'"))}; a failure the recovery's own work meets asks a person, or ends the run."));
            onFailure = null;
        }

        if (stepsNode is null)
        {
            problems.Add(YamlReading.At(node, "'recover' holds no 'steps'; a recovery that runs nothing would put nothing right, and the step that failed would run again unchanged."));
            return null;
        }

        var steps = ReadSteps(stepsNode, 1, problems).ToList();

        return onSuccess is null || onFailure is null ? null : new FlowRecover(maxVisits, steps, onSuccess, onFailure);
    }

    private static FlowBudget? ReadBudget(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'budget' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "budget", KeyDescription.Names(WorkflowKeys.Budget), problems);

        double? usd = null;
        long? tokens = null;
        double? minutes = null;
        int? reservePercent = null;

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "usd":
                    usd = RequireDouble(value, "budget.usd", problems);
                    break;

                case "tokens":
                    tokens = RequireLong(value, "budget.tokens", problems);
                    break;

                case "minutes":
                    minutes = RequireDouble(value, "budget.minutes", problems);
                    break;

                case "reservePercent":
                    reservePercent = RequireInt(value, "budget.reservePercent", problems);
                    break;

                default:
                    throw Unread(key);
            }
        }

        return new FlowBudget(usd, tokens, minutes, reservePercent);
    }

    private static FlowAdvisor? ReadAdvisor(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'advisor' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "advisor", KeyDescription.Names(WorkflowKeys.Advisor), problems, RetiredAgentKey);
        var read = ReadAgentSelection(keys, "advisor", problems);
        var agent = RequireScalarIn(keys, "advisor.agent", problems);
        var prompt = RequireScalarIn(keys, "advisor.prompt", problems);
        var kinds = StringListIn(keys, "advisor.kinds", problems) ?? [];

        // V43: the kinds an advisor recommends for, read as every kind where the key is left out.
        RefuseGivenEmpty(keys, "advisor.kinds", "name the kinds it recommends for", "every kind", problems);

        if (agent is null || prompt is null)
        {
            return null;
        }

        return new FlowAdvisor(agent, prompt, read.Tier, read.Model, read.Effort, read.Client, kinds)
        {
            Retries = IntIn(keys, "advisor.retries", problems),
            BackoffSeconds = IntIn(keys, "advisor.backoffSeconds", problems),
            EffortByModel = read.EffortByModel,
        };
    }

    private static FlowReporter? ReadReporter(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'reporter' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "reporter", KeyDescription.Names(WorkflowKeys.Reporter), problems, RetiredAgentKey);
        var read = ReadAgentSelection(keys, "reporter", problems);
        var agent = RequireScalarIn(keys, "reporter.agent", problems);
        var statusPrompt = RequireScalarIn(keys, "reporter.statusPrompt", problems);
        var askPrompt = RequireScalarIn(keys, "reporter.askPrompt", problems);

        if (agent is null || statusPrompt is null || askPrompt is null)
        {
            return null;
        }

        return new FlowReporter(agent, statusPrompt, askPrompt, read.Tier, read.Model, read.Effort, read.Client)
        {
            Retries = IntIn(keys, "reporter.retries", problems),
            BackoffSeconds = IntIn(keys, "reporter.backoffSeconds", problems),
            EffortByModel = read.EffortByModel,
        };
    }

    /// <summary>The keys <c>advisor</c> and <c>reporter</c> share: how each picks the model its agent runs on.</summary>
    private static (string? Tier, string? Model, string? Effort, IReadOnlyDictionary<string, string>? EffortByModel, string? Client)
        ReadAgentSelection(List<(string Key, YamlNode Value)> keys, string owner, List<string> problems)
    {
        string? tier = null;
        string? model = null;
        string? client = null;
        (string? Effort, IReadOnlyDictionary<string, string>? ByModel) effort = (null, null);

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "tier":
                    tier = YamlReading.RequireScalar(value, $"{owner}.tier", problems);
                    break;

                case "model":
                    model = YamlReading.RequireScalar(value, $"{owner}.model", problems);
                    break;

                case "effort":
                    effort = ReadEffort(value, $"{owner}.effort", problems);
                    break;

                case "client":
                    client = YamlReading.RequireScalar(value, $"{owner}.client", problems);
                    break;

                // Read by the caller, which owns them: the agent each runs, which both require, the
                // prompts each is asked with, the kinds an advisor is asked about, and how each is tried again.
                case "retries":
                case "backoffSeconds":
                case "agent":
                case "prompt":
                case "statusPrompt":
                case "askPrompt":
                case "kinds":
                    break;

                default:
                    throw Unread(key);
            }
        }

        return (tier, model, effort.Effort, effort.ByModel, client);
    }

    /// <summary>
    /// One effort, as the file writes it: a single level, for every model the owner may run on, or a map whose keys are
    /// <c>all</c> and models' codes, giving each model its own - <c>effort: high</c> reads as <c>{all: high}</c>. A map
    /// with no entry is refused, as a key given nothing.
    /// </summary>
    private static (string? Effort, IReadOnlyDictionary<string, string>? ByModel) ReadEffort(YamlNode node, string what, List<string> problems)
    {
        if (node is not YamlMappingNode map)
        {
            return (YamlReading.RequireScalar(node, what, problems), null);
        }

        var byModel = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in map.Children)
        {
            var code = YamlReading.ScalarOf(key);
            var level = YamlReading.RequireScalar(value, $"{what}.{code ?? "''"}", problems);

            if (level is null)
            {
                continue;
            }

            if (code is null)
            {
                problems.Add(YamlReading.At(key, $"'{what}' holds a key that is not one name: a map's keys are all and models' codes"));
                continue;
            }

            if (!byModel.TryAdd(code, level))
            {
                problems.Add(YamlReading.At(key, $"'{what}' names '{code}' more than once"));
            }
        }

        if (byModel.Count == 0)
        {
            problems.Add(YamlReading.At(node, $"'{what}' holds no entry; a map of efforts names all or a model's code, each with the level it runs at"));
            return (null, null);
        }

        return (null, byModel);
    }

    private static FlowAmend? ReadAmend(YamlNode node, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "'amend' is not a mapping of keys."));
            return null;
        }

        var keys = CollectKeys(mapping, "amend", KeyDescription.Names(WorkflowKeys.Amend), problems);

        string? input = null;
        int? changedAnswerMinutes = null;
        string? before = null;
        string? then = null;
        int? maxAddedIterations = null;

        foreach (var (key, value) in keys)
        {
            switch (key)
            {
                case "input":
                    input = YamlReading.RequireScalar(value, "amend.input", problems);
                    break;

                case "changedAnswerMinutes":
                    changedAnswerMinutes = RequireInt(value, "amend.changedAnswerMinutes", problems);
                    break;

                case "before":
                    before = YamlReading.RequireScalar(value, "amend.before", problems);
                    break;

                case "step":
                case "scope":
                case "whenAddScope":
                case "whenChangeScope":
                case "whenRemoveScope":
                    // Read below, after the loop, so the required ones are refused once, together.
                    break;

                case "then":
                    then = YamlReading.RequireScalar(value, "amend.then", problems);
                    break;

                case "maxAddedIterations":
                    maxAddedIterations = RequireInt(value, "amend.maxAddedIterations", problems);
                    break;

                default:
                    throw Unread(key);
            }
        }

        var step = RequireScalarIn(keys, "amend.step", problems);
        var scope = RequireScalarIn(keys, "amend.scope", problems);
        var whenAddScope = RequireScalarIn(keys, "amend.whenAddScope", problems);
        var whenChangeScope = RequireScalarIn(keys, "amend.whenChangeScope", problems);
        var whenRemoveScope = RequireScalarIn(keys, "amend.whenRemoveScope", problems);

        if (step is null || scope is null || whenAddScope is null || whenChangeScope is null || whenRemoveScope is null)
        {
            return null;
        }

        return new FlowAmend(input, changedAnswerMinutes, before, step, then, scope, whenAddScope, whenChangeScope, whenRemoveScope, maxAddedIterations);
    }

    /// <summary>Reads an <c>answers</c> map: a kind to the answer the configuration gives it.</summary>
    private static IEnumerable<(string Kind, FlowAnswer Answer)> ReadAnswers(YamlNode node, string owner, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, $"'{owner}' is not a mapping of question kind to answer."));
            yield break;
        }

        var answerKeys = KeyDescription.Names(WorkflowStepTypes.AnswerKeys);

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var kind = YamlReading.KeyOf(keyNode, problems);

            if (kind is null)
            {
                continue;
            }

            // The short spelling: the answer alone, written as a scalar.
            if (valueNode is YamlScalarNode { Value: { } plain })
            {
                yield return (kind, new FlowAnswer(plain, null, null));
                continue;
            }

            if (valueNode is not YamlMappingNode declaration)
            {
                problems.Add(YamlReading.At(valueNode, $"{owner}.{kind} is neither the answer alone nor a mapping of '{string.Join("', '", answerKeys)}'"));
                continue;
            }

            var keys = CollectKeys(declaration, $"{owner}.{kind}", answerKeys, problems);
            var answer = RequireScalarIn(keys, $"{owner}.{kind}.answer", problems);
            var times = IntIn(keys, $"{owner}.{kind}.times", problems);
            var then = ScalarIn(keys, $"{owner}.{kind}.then", problems);

            if (answer is null)
            {
                continue;
            }

            yield return (kind, new FlowAnswer(answer, times, then));
        }
    }

    private static IEnumerable<FlowInput> ReadInput(YamlMappingNode declaration, string owner, string name, List<string> problems)
    {
        var keys = CollectKeys(declaration, owner, KeyDescription.Names(WorkflowKeys.Input), problems);

        var description = ScalarIn(keys, $"{owner}.description", problems);
        var kind = ScalarIn(keys, $"{owner}.kind", problems) ?? FlowInput.TextKind;
        var fallback = ScalarIn(keys, $"{owner}.default", problems);
        var required = BooleanIn(keys, $"{owner}.required", problems) ?? false;
        var options = StringListIn(keys, $"{owner}.options", problems) ?? [];

        RefuseGivenEmpty(keys, $"{owner}.options", "list the values it takes", "any value", problems);

        if (kind is not null && !FlowInput.Kinds.Contains(kind, StringComparer.Ordinal))
        {
            problems.Add(YamlReading.At(declaration, $"{owner}.kind is '{kind}', which is neither of {string.Join(" nor ", FlowInput.Kinds)}."));
            kind = null;
        }

        if (kind == FlowInput.FileKind && options.Count > 0)
        {
            problems.Add(YamlReading.At(declaration, $"{owner} declares options and the kind '{FlowInput.FileKind}': options list what a '{FlowInput.TextKind}' input takes, and a file input is a path, checked when the run starts."));
        }

        yield return new FlowInput(name, description, kind ?? FlowInput.TextKind, fallback, required, options);
    }

    private static IEnumerable<FlowParam> ReadParam(YamlMappingNode declaration, string owner, string name, List<string> problems)
    {
        var keys = CollectKeys(declaration, owner, KeyDescription.Names(WorkflowKeys.Param), problems);

        var description = ScalarIn(keys, $"{owner}.description", problems);
        var fallback = ScalarIn(keys, $"{owner}.default", problems);
        var required = BooleanIn(keys, $"{owner}.required", problems) ?? false;

        yield return new FlowParam(name, description, fallback, required);
    }

    private static IEnumerable<WorkflowOutput> ReadOutput(YamlMappingNode declaration, string owner, string name, List<string> problems)
    {
        var keys = CollectKeys(declaration, owner, KeyDescription.Names(WorkflowKeys.Output), problems);

        var description = ScalarIn(keys, $"{owner}.description", problems);
        var from = RequireScalarIn(keys, $"{owner}.from", problems);

        if (from is null)
        {
            yield break;
        }

        yield return new WorkflowOutput(name, description, from);
    }

    /// <summary>Reads one of the file's declared lists - <c>inputs</c>, <c>params</c>, <c>outputs</c> - each a sequence of mappings that name themselves.</summary>
    private static IEnumerable<TDeclaration> ReadDeclarations<TDeclaration>(
        YamlNode node,
        string owner,
        IReadOnlyList<KeyDescription> declarationKeys,
        List<string> problems,
        Func<YamlMappingNode, string, string, List<string>, IEnumerable<TDeclaration>> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, $"'{owner}' is not a sequence."));
            yield break;
        }

        var names = new List<string>();

        foreach (var child in sequence.Children)
        {
            if (child is not YamlMappingNode mapping)
            {
                problems.Add(YamlReading.At(child, $"an entry of '{owner}' is not a mapping of '{string.Join("', '", KeyDescription.Names(declarationKeys))}'"));
                continue;
            }

            var keys = CollectKeys(mapping, $"an entry of '{owner}'", KeyDescription.Names(declarationKeys), problems);
            var name = RequireScalarIn(keys, $"{owner}.name", problems);

            if (name is null)
            {
                continue;
            }

            if (names.Contains(name, StringComparer.Ordinal))
            {
                problems.Add(YamlReading.At(child, $"'{owner}' names '{name}' more than once; a name is one value, and a second would leave what everything reads it as depending on where it sat in the file."));
                continue;
            }

            names.Add(name);

            foreach (var declared in read(mapping, $"{owner}.{name}", name, problems))
            {
                yield return declared;
            }
        }
    }

    private static IEnumerable<WorkflowStep> ReadSteps(YamlNode node, int depth, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, "'steps' is not a sequence."));
            yield break;
        }

        if (sequence.Children.Count == 0)
        {
            problems.Add(YamlReading.At(node, "'steps' is empty; a group that runs nothing would end at once, and its run with it."));
            yield break;
        }

        foreach (var child in sequence.Children)
        {
            foreach (var step in ReadStep(child, depth, problems))
            {
                yield return step;
            }
        }
    }

    /// <summary>
    /// The keys of the amend block that no step and no block a step holds takes: met on a step, each is a plan update's
    /// key written in the wrong place.
    /// </summary>
    private static readonly IReadOnlyList<string> PlanUpdateKeys =
    [
        .. KeyDescription.Names(WorkflowKeys.Amend).Except(
            [
                .. KeyDescription.Names(WorkflowStepTypes.Common),
                .. KeyDescription.Names(WorkflowStepTypes.Group),
                .. WorkflowStepTypes.AllKeys,
                .. KeyDescription.Names(WorkflowStepTypes.OnKeys),
                .. KeyDescription.Names(WorkflowStepTypes.RepeatKeys),
                .. KeyDescription.Names(WorkflowStepTypes.AllowKeys),
                .. KeyDescription.Names(WorkflowStepTypes.PreAnswerKeys),
                .. KeyDescription.Names(WorkflowStepTypes.QuoteKeys),
                .. KeyDescription.Names(WorkflowStepTypes.AnswerKeys),
            ],
            StringComparer.Ordinal),
    ];

    /// <summary>
    /// V44: everything about a plan update is said by the workflow's amend block, and no step carries a key of one - so
    /// such a key met on a step is refused saying where it belongs, where any other unknown key lists those a step takes.
    /// </summary>
    private static string? PlanUpdateKey(string key)
        => PlanUpdateKeys.Contains(key, StringComparer.Ordinal)
            ? "it is a key of amend, at the top of the file, the one place a plan update is said"
            : null;

    /// <summary>
    /// The agent keys a step, the advisor and the reporter took before the configuration owned them: each is refused
    /// naming what replaced it, so a reader moves the key's meaning instead of deleting its line.
    /// </summary>
    private static string? RetiredAgentKey(string key)
        => key switch
        {
            "fallback" => "a fallback is the order of the tier's models in tierConfig",
            "provider" => "a model's code is listed by one provider, which it names",
            _ => null,
        };

    /// <summary>
    /// Reads one step, at <paramref name="depth"/> levels of nesting: a typed step, or a group
    /// holding steps of its own.
    /// </summary>
    private static IEnumerable<WorkflowStep> ReadStep(YamlNode node, int depth, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, "a step is not a mapping of keys."));
            yield break;
        }

        var keys = CollectKeys(mapping, "a step", [.. KeyDescription.Names(WorkflowStepTypes.Common), .. KeyDescription.Names(WorkflowStepTypes.Group), .. WorkflowStepTypes.AllKeys], problems, key => PlanUpdateKey(key) ?? RetiredAgentKey(key));
        // A name or a type written as a list or a mapping is refused naming the key, never read as one left out.
        var nameNode = keys.FirstOrDefault(entry => entry.Key == "name").Value;
        var name = nameNode is null ? null : YamlReading.ScalarOf(nameNode);

        if (nameNode is not null && name is null)
        {
            problems.Add(YamlReading.At(nameNode, "a step's 'name' is not a single value."));
        }

        // The step's name, bare, as every key under it is named in a problem: 'orient.dependsOn'.
        var where = name is null ? "a step" : name;
        var typeNode = keys.FirstOrDefault(entry => entry.Key == "type").Value;
        var type = typeNode is null ? null : YamlReading.RequireScalar(typeNode, $"{where}.type", problems);
        var typed = type is null ? null : WorkflowStepTypes.Find(type);

        if (type is not null && typed is null)
        {
            problems.Add(YamlReading.At(typeNode!, $"'{type}' is not a step type. Known: {string.Join(", ", WorkflowStepTypes.All.Select(known => $"'{known.Type}'"))}."));
        }

        if (type is not null && keys.Any(entry => entry.Key == "steps"))
        {
            problems.Add(YamlReading.At(node, $"{where} names the type '{type}' and holds steps; a group holds steps and names no type, and a typed step runs one thing."));
        }

        // A group's own keys, checked against the group's table; a typed step's against its type's.
        IReadOnlyList<string> allowed = typed is null
            ? [.. KeyDescription.Names(WorkflowStepTypes.Common), .. KeyDescription.Names(WorkflowStepTypes.Group)]
            : [.. KeyDescription.Names(WorkflowStepTypes.Common), .. WorkflowStepTypes.KeysOf(typed)];

        foreach (var (key, value) in keys)
        {
            // A key no table lists was refused where the keys were collected; what is left to refuse
            // here is a key that belongs to a step this one is not - a type's key on a group or on a
            // step of another type, and a group's key on a typed step, which would otherwise be read
            // by nothing and obeyed by nothing.
            if (!allowed.Contains(key, StringComparer.Ordinal))
            {
                var owner = typed is null ? "a group" : $"a {typed.Type} step";
                var taken = WorkflowStepTypes.All.FirstOrDefault(other => other != typed && WorkflowStepTypes.KeysOf(other).Contains(key, StringComparer.Ordinal));
                var takenBy = KeyDescription.Names(WorkflowStepTypes.Running).Contains(key, StringComparer.Ordinal)
                    ? $"; it is one of every step that runs, and {(typed is null ? "a group runs nothing of its own" : $"a {typed.Type} step only asks")}"
                    : taken is not null
                    ? $"; it is one of a {taken.Type} step"
                    : typed is not null && KeyDescription.Names(WorkflowStepTypes.Group).Contains(key, StringComparer.Ordinal) ? "; it is one of a group, which names no type" : "";

                problems.Add(YamlReading.At(value, $"'{key}' is not a key of {owner}{takenBy}."));
            }
        }

        RefusePlaceholders([.. keys.Where(entry => allowed.Contains(entry.Key, StringComparer.Ordinal))], typed, where, problems);

        var step = new WorkflowStep
        {
            Name = name ?? string.Empty,
            Type = type,
            Description = ScalarIn(keys, $"{where}.description", problems),
            DependsOn = StringListIn(keys, $"{where}.dependsOn", problems) ?? [],
            When = ScalarIn(keys, $"{where}.when", problems),
            OnFailure = EnumIn(keys, $"{where}.onFailure", problems, WorkflowStep.FailurePolicies),
            Retries = IntIn(keys, $"{where}.retries", problems),
            Always = BooleanIn(keys, $"{where}.always", problems),
            Alone = BooleanIn(keys, $"{where}.alone", problems),
            Skippable = BooleanIn(keys, $"{where}.skippable", problems),
        };

        var answersNode = NodeIn(keys, "answers");

        if (answersNode is not null)
        {
            // A kind written twice comes through YAML where a tag sets the two apart, and is refused here by name.
            var stepAnswers = new Dictionary<string, FlowAnswer>(StringComparer.Ordinal);

            foreach (var (kind, answer) in ReadAnswers(answersNode, $"{where}.answers", problems))
            {
                if (!stepAnswers.TryAdd(kind, answer))
                {
                    problems.Add(YamlReading.At(answersNode, $"{where}.answers names the kind '{kind}' more than once"));
                }
            }

            step = step with { Answers = stepAnswers };
        }

        var onNode = NodeIn(keys, "on");

        if (onNode is not null)
        {
            if (onNode is not YamlMappingNode onMapping)
            {
                problems.Add(YamlReading.At(onNode, $"'{where}.on' is not a mapping of '{string.Join("', '", KeyDescription.Names(WorkflowStepTypes.OnKeys))}'"));
            }
            else
            {
                var onKeys = CollectKeys(onMapping, $"{where}.on", KeyDescription.Names(WorkflowStepTypes.OnKeys), problems);
                var onStep = RequireScalarIn(onKeys, $"{where}.on.step", problems);
                var outcome = RequireScalarIn(onKeys, $"{where}.on.outcome", problems);
                var codes = IntListIn(onKeys, $"{where}.on.codes", problems) ?? [];

                if (outcome is not null && !FlowOn.Outcomes.Contains(outcome, StringComparer.Ordinal))
                {
                    problems.Add(YamlReading.At(onNode, $"{where}.on.outcome is '{outcome}', which is none of {string.Join(", ", FlowOn.Outcomes.Select(known => $"'{known}'"))}."));
                    outcome = null;
                }

                if (onStep is not null && outcome is not null)
                {
                    step = step with { On = new FlowOn(onStep, outcome, codes) };
                }
            }
        }

        step = step with
        {
            Then = ScalarIn(keys, $"{where}.then", problems),
            MaxVisits = IntIn(keys, $"{where}.maxVisits", problems),
        };

        if (typed is null)
        {
            // The group's own keys, checked only where the file holds them.
            var stepsNode = NodeIn(keys, "steps");

            // A type the file names that no table knows, or that is no single value, is refused above;
            // what is left to refuse here is a step that names no type at all.
            if (stepsNode is null && typeNode is null)
            {
                problems.Add(YamlReading.At(node, $"{where} names no type and holds no steps; a step does one thing, and a group holds steps of their own."));
            }
            else if (depth >= MaxDepth && stepsNode is not null)
            {
                problems.Add(YamlReading.At(stepsNode, $"{where} holds steps {depth + 1} levels deep; steps nest at most {MaxDepth}."));
            }
            else if (stepsNode is not null)
            {
                step = step with { Steps = [.. ReadSteps(stepsNode, depth + 1, problems)] };
            }

            step = step with
            {
                ForEach = ScalarIn(keys, $"{where}.forEach", problems),
                Repeat = ReadRepeat(keys, where, problems),
                Gather = StringListIn(keys, $"{where}.gather", problems) ?? [],
            };
        }
        else
        {
            step = ReadTypedKeys(step, typed, keys, where, problems);
        }

        if (name is null)
        {
            if (nameNode is null)
            {
                problems.Add(YamlReading.At(node, "a step names no 'name'; what dependsOn, on, resume and expressions read would have nothing to read."));
            }

            yield break;
        }

        foreach (var required in typed?.Required ?? [])
        {
            if (!keys.Any(entry => string.Equals(entry.Key, required, StringComparison.Ordinal)))
            {
                problems.Add(YamlReading.At(node, $"{where} names no '{required}'; a {typed!.Type} step is refused without it."));
            }
        }

        yield return step;
    }

    /// <summary>
    /// Refuses a placeholder in a key of a step that is not a template (V16-V19): only the text a step hands on is
    /// filled (<see cref="WorkflowStepTypes.StepType.TemplateKeys"/>), and every other key is read as it is written, so a
    /// placeholder there would be handed on as its own braces, or name nothing. An expression is the grammar's to read,
    /// a step's name and type are refused for what they are, and a group's steps are each read as a step.
    /// </summary>
    private static void RefusePlaceholders(IReadOnlyList<(string Key, YamlNode Value)> keys, WorkflowStepTypes.StepType? type, string where, List<string> problems)
    {
        IReadOnlyList<string> filled = type?.TemplateKeys ?? [];
        var owner = type is null ? "a group" : $"a {type.Type} step";
        var fills = filled switch
        {
            [] => "none of its keys",
            [var one] => one,
            _ => $"{string.Join(", ", filled.Take(filled.Count - 1))} and {filled[^1]}",
        };

        foreach (var (key, value) in keys.Where(entry => entry.Key is not ("name" or "type" or "steps") && !filled.Contains(entry.Key, StringComparer.Ordinal)))
        {
            foreach (var (path, scalar) in TextsIn(key, value, inList: false))
            {
                foreach (var read in WorkflowExpressions.TemplateReads(scalar.Value!))
                {
                    problems.Add(YamlReading.At(scalar, $"'{where}.{path}' holds {{{read.Whole}}}, and {path} is no template: it is read as it is written, never filled; {owner} fills {fills}."));
                }
            }
        }
    }

    /// <summary>
    /// Every scalar under <paramref name="node"/>, the value of the step's key at <paramref name="path"/>, that is no
    /// expression: a key of <see cref="WorkflowStepTypes.ExpressionKeys"/> is one, and so is a key of
    /// <see cref="WorkflowStepTypes.ListOrExpressionKeys"/> given a single value; a list such a key is given is names.
    /// </summary>
    private static IEnumerable<(string Path, YamlScalarNode Scalar)> TextsIn(string path, YamlNode node, bool inList)
    {
        switch (node)
        {
            case YamlScalarNode { Value: not null } scalar:
                if (inList || !(WorkflowStepTypes.ExpressionKeys.Contains(path, StringComparer.Ordinal) || WorkflowStepTypes.ListOrExpressionKeys.Contains(path, StringComparer.Ordinal)))
                {
                    yield return (path, scalar);
                }

                break;

            case YamlSequenceNode sequence:
                foreach (var found in sequence.Children.SelectMany(child => TextsIn(path, child, inList: child is YamlScalarNode)))
                {
                    yield return found;
                }

                break;

            case YamlMappingNode mapping:
                foreach (var (key, value) in mapping.Children)
                {
                    if (YamlReading.ScalarOf(key) is { } name)
                    {
                        foreach (var found in TextsIn($"{path}.{name}", value, inList: false))
                        {
                            yield return found;
                        }
                    }
                }

                break;
        }
    }

    /// <summary>Reads the keys that belong to a step's type alone.</summary>
    private static WorkflowStep ReadTypedKeys(WorkflowStep step, WorkflowStepTypes.StepType type, List<(string Key, YamlNode Value)> keys, string where, List<string> problems)
    {
        var failOn = EnumIn(keys, $"{where}.failOn", problems, type.FailOnValues);

        step = step with
        {
            FailOn = failOn,
            Legs = StringListIn(keys, $"{where}.legs", problems) ?? [],
            In = ScalarIn(keys, $"{where}.in", problems),
            TimeoutMinutes = IntIn(keys, $"{where}.timeoutMinutes", problems),
        };

        switch (type.Type)
        {
            case "reason":
                var effort = NodeIn(keys, "effort") is { } effortNode ? ReadEffort(effortNode, $"{where}.effort", problems) : (Effort: (string?)null, ByModel: (IReadOnlyDictionary<string, string>?)null);

                step = step with
                {
                    Agent = ScalarIn(keys, $"{where}.agent", problems),
                    Tier = ScalarIn(keys, $"{where}.tier", problems),
                    Model = ScalarIn(keys, $"{where}.model", problems),
                    Effort = effort.Effort,
                    EffortByModel = effort.ByModel,
                    Client = ScalarIn(keys, $"{where}.client", problems),
                    Prompt = ScalarIn(keys, $"{where}.prompt", problems),
                    ResumePrompt = ScalarIn(keys, $"{where}.resumePrompt", problems),
                    Outputs = ScalarIn(keys, $"{where}.outputs", problems),
                    Access = EnumIn(keys, $"{where}.access", problems, ["read", "write"]),
                    Resume = ScalarIn(keys, $"{where}.resume", problems),
                    Independent = BooleanIn(keys, $"{where}.independent", problems),
                    OnPreExisting = ScalarIn(keys, $"{where}.onPreExisting", problems),
                    Raised = EnumIn(keys, $"{where}.raised", problems, [WorkflowStep.RaisedByPreExisting]),
                    OnAddress = EnumIn(keys, $"{where}.onAddress", problems, [WorkflowStep.Recover]),
                    Restrictions = StringListIn(keys, $"{where}.restrictions", problems) ?? [],
                    Search = NodeIn(keys, "search") is { } search ? RequireStringList(search, $"{where}.search", problems) : null,
                    Writes = FlowListIn(keys, $"{where}.writes", problems),
                    Ask = BooleanIn(keys, $"{where}.ask", problems),
                    AskRounds = IntIn(keys, $"{where}.askRounds", problems),
                    BackoffSeconds = IntIn(keys, $"{where}.backoffSeconds", problems),
                };

                RefuseGivenEmpty(keys, $"{where}.search", "name sources or tags", "agentWebSearch.defaults", problems);
                break;

            case "build":
                // Its keys are the leg keys every leg step shares, read before this switch.
                break;

            case "test":
                step = step with
                {
                    Filter = ScalarIn(keys, $"{where}.filter", problems),
                    FilterWhen = ScalarIn(keys, $"{where}.filterWhen", problems),
                    Exclude = StringListIn(keys, $"{where}.exclude", problems) ?? [],
                    Labels = StringListIn(keys, $"{where}.labels", problems) ?? [],
                    SkipBuild = BooleanIn(keys, $"{where}.skipBuild", problems),
                };
                break;

            case "run":
                step = step with
                {
                    Runner = ScalarIn(keys, $"{where}.runner", problems),
                    Inputs = StringMapIn(keys, $"{where}.inputs", problems),
                };
                break;

            case "check-mutations":
                step = step with
                {
                    Arms = StringListIn(keys, $"{where}.arms", problems) ?? [],
                    Pending = BooleanIn(keys, $"{where}.pending", problems),
                };
                break;

            case "create-agent":
                step = step with
                {
                    Agent = ScalarIn(keys, $"{where}.agent", problems),
                    Empty = BooleanIn(keys, $"{where}.empty", problems),
                };
                break;

            case "fold":
            case "delete-agent":
                step = step with
                {
                    Of = ScalarIn(keys, $"{where}.of", problems),
                    Allow = ReadAllow(keys, where, problems),
                    Within = FlowListIn(keys, $"{where}.within", problems),
                };

                if (type.Type == "delete-agent")
                {
                    step = step with { DiscardUncommitted = BooleanIn(keys, $"{where}.discardUncommitted", problems) };
                }

                break;

            case "refresh":
                step = step with
                {
                    Of = ScalarIn(keys, $"{where}.of", problems),
                    Others = ScalarIn(keys, $"{where}.others", problems),
                    Paths = FlowListIn(keys, $"{where}.paths", problems),
                    All = BooleanIn(keys, $"{where}.all", problems),
                    Except = FlowListIn(keys, $"{where}.except", problems),
                    Stale = FlowListIn(keys, $"{where}.stale", problems),
                    Merge = FlowListIn(keys, $"{where}.merge", problems),
                };
                break;

            case "rebase":
                step = step with
                {
                    Of = ScalarIn(keys, $"{where}.of", problems),
                    Others = ScalarIn(keys, $"{where}.others", problems),
                    Settled = PersonsListIn(keys, where, "settled", problems),
                    Merge = FlowListIn(keys, $"{where}.merge", problems),
                };
                break;

            case "validate-plan":
                step = step with
                {
                    Items = ScalarIn(keys, $"{where}.items", problems),
                    Scope = ScalarIn(keys, $"{where}.scope", problems),
                };
                break;

            case "anchor-balance":
                step = step with
                {
                    Base = ScalarIn(keys, $"{where}.base", problems),
                    Rows = FlowListIn(keys, $"{where}.rows", problems),
                };

                RefuseGivenEmpty(keys, $"{where}.rows", "name the rows it is held to", "the whole tree judged", problems);
                break;

            case "anchor-citations":
                step = step with
                {
                    Subject = EnumIn(keys, $"{where}.subject", problems, Anchors.AnchorCitationSubjects.Names),
                    AnchorPaths = FlowListIn(keys, $"{where}.paths", problems),
                };

                RefuseGivenEmpty(keys, $"{where}.paths", "name the files it is held to", "the whole tree judged", problems);
                break;

            case "ci-read":
                step = step with
                {
                    Branch = ScalarIn(keys, $"{where}.branch", problems),
                    Limit = IntIn(keys, $"{where}.limit", problems),
                };
                break;

            case "ci-wait":
                step = step with
                {
                    Commit = ScalarIn(keys, $"{where}.commit", problems),
                    AppearSeconds = IntIn(keys, $"{where}.appearSeconds", problems),
                    WaitMinutes = IntIn(keys, $"{where}.waitMinutes", problems),
                    PollSeconds = IntIn(keys, $"{where}.pollSeconds", problems),
                    Ignore = StringListIn(keys, $"{where}.ignore", problems) ?? [],
                };

                RefuseGivenEmpty(keys, $"{where}.ignore", "name the checks it does not count", "every check counted", problems);
                break;

            case "commit":
                step = step with
                {
                    Message = ScalarIn(keys, $"{where}.message", problems),
                    Variables = StringMapIn(keys, $"{where}.variables", problems),
                };
                break;

            case "push":
                step = step with
                {
                    Remote = ScalarIn(keys, $"{where}.remote", problems),
                    Checkpoint = BooleanIn(keys, $"{where}.checkpoint", problems),
                };
                break;

            case "pull-request":
                step = step with
                {
                    Title = ScalarIn(keys, $"{where}.title", problems),
                    Body = ScalarIn(keys, $"{where}.body", problems),
                    Base = ScalarIn(keys, $"{where}.base", problems),
                    Draft = BooleanIn(keys, $"{where}.draft", problems),
                };
                break;

            case "pause":
                step = step with
                {
                    Question = ScalarIn(keys, $"{where}.question", problems),
                    Options = StringListIn(keys, $"{where}.options", problems) ?? [],
                    Text = StringListIn(keys, $"{where}.text", problems) ?? [],
                    Person = BooleanIn(keys, $"{where}.person", problems),
                    Questions = ScalarIn(keys, $"{where}.questions", problems),
                    PreAnswer = ReadPreAnswers(keys, where, problems),
                };
                break;

            case "orient":
                break;

            case "graphify-codebase":
                step = step with { Rebuild = BooleanIn(keys, $"{where}.rebuild", problems) };
                break;

            default:
                throw Unread(type.Type);
        }

        return step;
    }

    private static FlowRepeat? ReadRepeat(List<(string Key, YamlNode Value)> keys, string where, List<string> problems)
    {
        var node = NodeIn(keys, "repeat");

        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, $"'{where}.repeat' is not a mapping of '{string.Join("', '", KeyDescription.Names(WorkflowStepTypes.RepeatKeys))}'"));
            return null;
        }

        var repeatKeys = CollectKeys(mapping, $"{where}.repeat", KeyDescription.Names(WorkflowStepTypes.RepeatKeys), problems);

        // blockersLimitRounds is a number of rounds, or false for none but the workflow's budget.
        var limitNode = NodeIn(repeatKeys, "blockersLimitRounds");
        var unbounded = limitNode is YamlScalarNode { Value: "false" };

        return new FlowRepeat(
            RequireScalarIn(repeatKeys, $"{where}.repeat.until", problems) ?? string.Empty,
            IntIn(repeatKeys, $"{where}.repeat.maxIterations", problems),
            IntIn(repeatKeys, $"{where}.repeat.rounds", problems),
            ScalarIn(repeatKeys, $"{where}.repeat.blocking", problems),
            unbounded ? null : IntIn(repeatKeys, $"{where}.repeat.blockersLimitRounds", problems),
            ScalarIn(repeatKeys, $"{where}.repeat.leftover", problems),
            unbounded);
    }

    private static FlowAllow? ReadAllow(List<(string Key, YamlNode Value)> keys, string where, List<string> problems)
    {
        var node = NodeIn(keys, "allow");

        if (node is null)
        {
            return null;
        }

        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, $"'{where}.allow' is not a mapping of '{string.Join("', '", KeyDescription.Names(WorkflowStepTypes.AllowKeys))}'"));
            return null;
        }

        var allowKeys = CollectKeys(mapping, $"{where}.allow", KeyDescription.Names(WorkflowStepTypes.AllowKeys), problems);

        return new FlowAllow(
            PersonsListIn(allowKeys, where, "allow.settled", problems),
            FlowListIn(allowKeys, $"{where}.allow.new", problems),
            PersonsListIn(allowKeys, where, "allow.acceptLost", problems),
            FlowListIn(allowKeys, $"{where}.allow.merge", problems),
            PersonsListIn(allowKeys, where, "allow.acceptOver", problems));
    }

    /// <summary>
    /// A list only a person gives - the paths a fold, a deletion or a rebase leaves out as settled by hand, and the lost
    /// and over cells a fold accepts - written out, never read from an expression: a single value there is one, and is
    /// refused as V29 words it, whatever else it says. Anything else that is not a list is refused as any list is.
    /// </summary>
    /// <param name="keys">The keys of the mapping that holds it.</param>
    /// <param name="where">The step's name, as every key under it is named in a problem.</param>
    /// <param name="key">The key, as the step spells it beneath itself: <c>allow.settled</c>, or a rebase's <c>settled</c>.</param>
    /// <param name="problems">Where a problem is told.</param>
    private static IReadOnlyList<string> PersonsListIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string where, string key, List<string> problems)
    {
        if (NodeIn(keys, KeyNameOf(key)) is YamlScalarNode { Value: not null } scalar)
        {
            problems.Add(YamlReading.At(scalar, $"step '{where}' takes {key} from an output: paths settled by hand, and lost and over cells, are a person's to accept; only new rows and merges may be read from one."));
            return [];
        }

        return StringListIn(keys, $"{where}.{key}", problems);
    }

    private static IReadOnlyList<FlowPreAnswer> ReadPreAnswers(List<(string Key, YamlNode Value)> keys, string where, List<string> problems)
    {
        var node = NodeIn(keys, "preAnswer");

        if (node is null)
        {
            return [];
        }

        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, $"'{where}.preAnswer' is not a sequence."));
            return [];
        }

        RefuseGivenEmpty(keys, $"{where}.preAnswer", "list the answers a person may give with the run", "no answer given beforehand", problems);

        var answers = new List<FlowPreAnswer>();

        foreach (var child in sequence.Children)
        {
            if (child is not YamlMappingNode mapping)
            {
                problems.Add(YamlReading.At(child, $"an entry of '{where}.preAnswer' is not a mapping of '{string.Join("', '", KeyDescription.Names(WorkflowStepTypes.PreAnswerKeys))}'"));
                continue;
            }

            var entryKeys = CollectKeys(mapping, $"an entry of '{where}.preAnswer'", KeyDescription.Names(WorkflowStepTypes.PreAnswerKeys), problems);
            var when = RequireScalarIn(entryKeys, $"{where}.preAnswer.when", problems);
            var answer = RequireScalarIn(entryKeys, $"{where}.preAnswer.answer", problems);
            var text = ScalarIn(entryKeys, $"{where}.preAnswer.text", problems);
            var quoteNode = NodeIn(entryKeys, "quote");
            FlowQuote? quote = null;

            if (quoteNode is not null)
            {
                if (quoteNode is not YamlMappingNode quoteMapping)
                {
                    problems.Add(YamlReading.At(quoteNode, $"'{where}.preAnswer.quote' is not a mapping of '{string.Join("', '", KeyDescription.Names(WorkflowStepTypes.QuoteKeys))}'"));
                }
                else
                {
                    var quoteKeys = CollectKeys(quoteMapping, $"{where}.preAnswer.quote", KeyDescription.Names(WorkflowStepTypes.QuoteKeys), problems);
                    var quoteText = RequireScalarIn(quoteKeys, $"{where}.preAnswer.quote.text", problems);
                    var quoteInput = RequireScalarIn(quoteKeys, $"{where}.preAnswer.quote.input", problems);

                    if (quoteText is not null && quoteInput is not null)
                    {
                        quote = new FlowQuote(quoteText, quoteInput);
                    }
                }
            }

            if (when is not null && answer is not null)
            {
                answers.Add(new FlowPreAnswer(when, answer, text, quote));
            }
        }

        return answers;
    }

    /// <summary>
    /// One mapping's keys, read once: a key no list names is refused here, before any reader looks at its value, and so is
    /// a key written twice - YAML refuses one only where both are written alike, and one tagged apart (<c>!!str</c>) would
    /// otherwise be read in place of nothing. A key <paramref name="belongsElsewhere"/> places is refused saying where it
    /// goes, in place of every key the mapping takes.
    /// </summary>
    private static List<(string Key, YamlNode Value)> CollectKeys(
        YamlMappingNode mapping,
        string what,
        IReadOnlyList<string> known,
        List<string> problems,
        Func<string, string?>? belongsElsewhere = null)
    {
        var keys = new List<(string Key, YamlNode Value)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = YamlReading.KeyOf(keyNode, problems);

            if (key is null)
            {
                continue;
            }

            if (!seen.Add(key))
            {
                problems.Add(YamlReading.At(keyNode, $"'{key}' is written twice in {what}; a key is one value"));
                continue;
            }

            if (!known.Contains(key, StringComparer.Ordinal))
            {
                problems.Add(YamlReading.At(keyNode, belongsElsewhere?.Invoke(key) is { } where
                    ? $"'{key}' is not a key of {what}; {where}."
                    : $"'{key}' is not a key of {what}. Known: '{string.Join("', '", known)}'."));
                continue;
            }

            keys.Add((key, valueNode));
        }

        return keys;
    }

    private static string? ScalarIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal)).Select(entry => YamlReading.RequireScalar(entry.Value, what, problems)).FirstOrDefault();

    private static string? RequireScalarIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
    {
        var found = keys.FirstOrDefault(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal));

        if (found.Value is null)
        {
            problems.Add($"'{what}' is named nowhere; it is required.");
            return null;
        }

        return YamlReading.RequireScalar(found.Value, what, problems);
    }

    private static YamlNode? NodeIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string key)
        => keys.FirstOrDefault(entry => string.Equals(entry.Key, key, StringComparison.Ordinal)).Value;

    private static bool? BooleanIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal)).Select(entry => YamlReading.RequireBoolean(entry.Value, what, problems)).FirstOrDefault();

    private static int? RequireInt(YamlNode node, string what, List<string> problems)
    {
        var text = YamlReading.RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        problems.Add(YamlReading.At(node, $"'{what}' is '{text}', which is not a whole number."));
        return null;
    }

    private static int? IntIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal)).Select(entry => RequireInt(entry.Value, what, problems)).FirstOrDefault();

    private static long? RequireLong(YamlNode node, string what, List<string> problems)
    {
        var text = YamlReading.RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (long.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            return value;
        }

        problems.Add(YamlReading.At(node, $"'{what}' is '{text}', which is not a whole number."));
        return null;
    }

    private static double? RequireDouble(YamlNode node, string what, List<string> problems)
    {
        var text = YamlReading.RequireScalar(node, what, problems);

        if (text is null)
        {
            return null;
        }

        if (double.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out var value) && double.IsFinite(value))
        {
            return value;
        }

        problems.Add(YamlReading.At(node, $"'{what}' is '{text}', which is not a number."));
        return null;
    }

    private static IReadOnlyList<string> RequireStringList(YamlNode node, string what, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a list."));
            return [];
        }

        var values = new List<string>();

        foreach (var child in sequence.Children)
        {
            if (YamlReading.RequireScalar(child, $"one entry of '{what}'", problems) is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<string> StringListIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal)).SelectMany(entry => RequireStringList(entry.Value, what, problems)).ToList();

    private static IReadOnlyList<int> RequireIntList(YamlNode node, string what, List<string> problems)
    {
        if (node is not YamlSequenceNode sequence)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a list."));
            return [];
        }

        var values = new List<int>();

        foreach (var child in sequence.Children)
        {
            if (RequireInt(child, $"one entry of '{what}'", problems) is { } value)
            {
                values.Add(value);
            }
        }

        return values;
    }

    private static IReadOnlyList<int> IntListIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal)).SelectMany(entry => RequireIntList(entry.Value, what, problems)).ToList();

    private static IReadOnlyDictionary<string, string> RequireStringMap(YamlNode node, string what, List<string> problems)
    {
        if (node is not YamlMappingNode mapping)
        {
            problems.Add(YamlReading.At(node, $"'{what}' is not a mapping of name to value."));
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            var key = YamlReading.KeyOf(keyNode, problems);

            if (key is null)
            {
                continue;
            }

            // A name written twice comes through YAML where a tag sets the two apart, and is refused here by name.
            if (YamlReading.RequireScalar(valueNode, $"{what}.{key}", problems) is { } value && !values.TryAdd(key, value))
            {
                problems.Add(YamlReading.At(keyNode, $"'{what}' names '{key}' more than once"));
            }
        }

        return values;
    }

    private static IReadOnlyDictionary<string, string> StringMapIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
        => keys.Where(entry => string.Equals(entry.Key, KeyNameOf(what), StringComparison.Ordinal))
            .Select(entry => RequireStringMap(entry.Value, what, problems))
            .FirstOrDefault() ?? new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Reads one key whose values are a closed list, refusing any other with the list.</summary>
    private static string? EnumIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems, IReadOnlyList<string> values)
    {
        var text = ScalarIn(keys, what, problems);

        if (text is null)
        {
            return null;
        }

        if (values.Contains(text, StringComparer.Ordinal))
        {
            return text;
        }

        problems.Add($"'{what}' is '{text}', which is none of {string.Join(", ", values.Select(known => $"'{known}'"))}.");
        return null;
    }

    /// <summary>
    /// Reads one list-or-expression key: a scalar is an expression that yields the list, a sequence
    /// is the list written out. Which keys take this shape is said where each type's keys are read,
    /// by reading them through this.
    /// </summary>
    private static FlowList? FlowListIn(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, List<string> problems)
    {
        var node = NodeIn(keys, KeyNameOf(what));

        if (node is null)
        {
            return null;
        }

        if (node is YamlScalarNode { Value: { } expression })
        {
            return new FlowList([], expression);
        }

        return new FlowList(RequireStringList(node, what, problems), null);
    }

    /// <summary>
    /// Refuses a list written out with nothing in it, where leaving the key out says <paramref name="leftOut"/>: the
    /// model reads a list given empty as the key left out, so an empty one would be taken for what the file never said.
    /// </summary>
    private static void RefuseGivenEmpty(IReadOnlyList<(string Key, YamlNode Value)> keys, string what, string name, string leftOut, List<string> problems)
    {
        if (NodeIn(keys, KeyNameOf(what)) is YamlSequenceNode { Children.Count: 0 } empty)
        {
            problems.Add(YamlReading.At(empty, $"'{what}' is given empty, which is never read as {leftOut}: {name}, or leave the key out for {leftOut}."));
        }
    }

    /// <summary>The key a dotted <c>what</c> names, its last segment.</summary>
    private static string KeyNameOf(string what) => what.Split('.')[^1];

    /// <summary>
    /// A key <see cref="WorkflowKeys"/> or <see cref="WorkflowStepTypes"/> lists that nothing here
    /// reads: a defect in this tool, never a problem in the file, since a key read by nothing would
    /// be one the file sets and nothing obeys.
    /// </summary>
    private static UnreachableException Unread(string key)
        => new($"'{key}' is a key workflow files take, and nothing reads it.");

    private static HarnessException Refuse(string path, IReadOnlyList<string> problems)
    {
        var detail = string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem));

        return new HarnessException(
            HarnessExit.ConfigInvalid,
            $"'{path}' has {ReportText.Counted(problems.Count, "problem")}:{Environment.NewLine}{detail}");
    }
}
