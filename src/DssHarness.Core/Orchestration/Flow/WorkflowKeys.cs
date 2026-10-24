using DssHarness.Core.Configuration;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// Every key a workflow's yml file takes at its root, with what each does: the parser reads exactly
/// these, and <c>help workflows</c> lists them.
/// </summary>
/// <remarks>
/// One list for both, as an action file's keys are, so a key cannot be read without being listed,
/// nor listed without being read. A step's keys are <see cref="WorkflowStepTypes"/>' to list: a step
/// is one of the types there, and what it takes follows from that.
/// </remarks>
public static class WorkflowKeys
{
    /// <summary>What the file itself takes, beside its steps.</summary>
    public static IReadOnlyList<KeyDescription> File =>
    [
        new("name", "the workflow's name, the same as its directory's and its file's") { Required = true },
        new("description", "what the workflow is for"),
        new("maxReasoningAgents", "reasoning agents a run has at once; absent, 2"),
        new("maxParallelProcesses", "steps that run legs a run has at once; absent, 1"),
        new("maxOpenAgents", "agents with a worktree a run has open at once; absent, maxReasoningAgents"),
        new("maxRetries", "retries an incomplete step is given, and a reasoning step whose agent failed, on each model it may use; absent, 3"),
        new("backoffSeconds", $"seconds a reasoning step waits before it is tried again, twice as long before each later try, at most {WorkflowStep.MostBackoffSeconds}; absent, {WorkflowStep.DefaultBackoffSeconds}"),
        new("maxInstances", $"the most step instances a run may have; absent, {WorkflowDefinition.DefaultMaxInstances}"),
        new("pushCheckpoints", "push checkpoint commits as they are made; absent, the final push alone"),
        new("handoffSeconds", "how long a paused reasoning step is given to write its handoff; absent, 180"),
        new("budget", "what a whole run's reasoning may cost, and the reserve that pauses the run first") { Keys = Budget },
        new("recommendations", "the file holding the repository's bar for recommendations, in this directory"),
        new("advisor", "the agent that recommends an answer to a question the engine raises itself") { Keys = Advisor },
        new("reporter", "the agent that words what a run is doing for a person who asks") { Keys = Reporter },
        new("amend", "everything the workflow says about a request amended, or an answer changed, while a run works") { Keys = Amend },
        new("answers", "the answers the configuration gives to question kinds, beforehand") { Keys = WorkflowStepTypes.AnswerKeys },
        new("inputs", "the values a run is started with") { Keys = Input },
        new("params", "the workflow's own knobs, defaulted here and read as param.<name>") { Keys = Param },
        new("outputs", "what the whole workflow declares it produces") { Keys = Output },
        new("steps", "the flow's steps, in the order they are declared") { Required = true, Keys = Step },
        new("recover", "the steps a step gives its failure to where it says onFailure: recover: they find what went wrong and put it right") { Keys = Recover },
        new("codebaseGraph", "the shape of the run's one codebase graph, which every graphify-codebase step syncs; absent, every default") { Keys = CodebaseGraph },
    ];

    /// <summary>What the <c>codebaseGraph</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> CodebaseGraph =>
    [
        new("include", "patterns: only the paths one of them matches are graphed; absent, every file the tree lists"),
        new("exclude", "patterns: the paths one of them matches are not graphed; absent, none"),
        new("languages", "the implementations used, by name - 'help codebase-graph' lists them; a file whose claimant is left out is only listed; absent, every one"),
        new("maxFileBytes", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the largest file read, from {Codebase.CodebaseGraphShape.LeastMaxFileBytes:N0} to {Codebase.CodebaseGraphShape.MostMaxFileBytes:N0}; a larger one is listed tooLarge; absent, {Codebase.CodebaseGraphShape.DefaultMaxFileBytes:N0}")),
        new("generated", $"what becomes of generated code: {Codebase.CodebaseGraphShape.GeneratedLow} (the default), its declarations kept, its file's entry naming why it is generated, or {Codebase.CodebaseGraphShape.GeneratedExclude}, the file listed and its declarations left out"),
        new("mapTokens", string.Create(System.Globalization.CultureInfo.InvariantCulture, $"the cap of a later slice's repository map, from 0 to {Codebase.CodebaseGraphShape.MostMapTokens:N0} tokens - nothing reads it yet; 0 asks for no map; absent, {Codebase.CodebaseGraphShape.DefaultMapTokens:N0}")),
    ];

    /// <summary>What the <c>recover</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Recover =>
    [
        new("steps", $"the recovery's own steps; each {ConfigKeys.TakesKeysOf(typeof(WorkflowStep))}, reads the step that failed as source, and sees every step of the flow") { Required = true },
        new("onSuccess", "where the run goes once the recovery's steps are done: failed-step, the step that gave it its failure, which runs again from where it stopped") { Required = true },
        new("onFailure", "what a failure inside the recovery does once its steps' retries are spent: pause, a person is asked, or stop - never recover, since no step calls itself") { Required = true },
        new("maxVisits", $"how often the recovery may take the failures of one step in one scope, from 1 to {FlowRecover.MostVisits}; absent, the engine's bound of {FlowRecover.MostVisits} visits of one step in one scope; past it the run asks (recoveryBound), with the advisor's recommended fix"),
    ];

    /// <summary>What one declared input takes.</summary>
    public static IReadOnlyList<KeyDescription> Input =>
    [
        new("name", "the input's name, as --input name=value spells it") { Required = true },
        new("description", "what the value is for, shown when it is missing"),
        new("kind", "text (the default) or file: a path inside the repository, checked when the run starts"),
        new("default", "what to use when nothing supplies it; empty is the input not given, and is always taken"),
        new("required", "refuse a run that gives it no value and declares no default"),
        new("options", "the values a text input takes; a run started with another is refused"),
    ];

    /// <summary>What one declared param takes.</summary>
    public static IReadOnlyList<KeyDescription> Param =>
    [
        new("name", "the param's name, as param.<name> reads it") { Required = true },
        new("description", "what the value is for"),
        new("default", "what to use where nothing supplies it"),
        new("required", "refuse a run that gives it no value and declares no default"),
    ];

    /// <summary>What one declared output takes.</summary>
    public static IReadOnlyList<KeyDescription> Output =>
    [
        new("name", "the output's name, as the workflow's report names it") { Required = true },
        new("description", "what the value holds"),
        new("from", "an expression naming the step and the member that hold it") { Required = true },
    ];

    /// <summary>What the <c>budget</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Budget =>
    [
        new("usd", "what the run's reasoning may cost, in dollars; at least one of usd, tokens, minutes"),
        new("tokens", "what the run's reasoning may spend, in tokens"),
        new("minutes", "how long the run's reasoning may run, in minutes"),
        new("reservePercent", "the share of the budget left when the run pauses instead of spending it; absent, 10"),
    ];

    /// <summary>What the <c>advisor</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Advisor =>
    [
        new("agent", "the agent definition, as agents/<name>.md in the workflow's directory") { Required = true },
        new("prompt", "the prompt file the recommendation is asked with") { Required = true },
        new("tier", "a name for a class of model: one of the configuration's tierConfig, its models in the order they are tried"),
        new("model", "the model outright, whose provider, key and client follow from it"),
        new("effort", "the effort the model is asked for: one level for every model it may run on, or a map of all and models' codes giving each its own"),
        new("client", "the client, where the provider is reached two ways"),
        new("kinds", "the question kinds this advisor is asked about; absent, every one"),
        new("retries", "how often its agent is tried again where it failed, on each model it may use; absent, maxRetries"),
        new("backoffSeconds", $"seconds it waits before it is tried again, twice as long before each later try, at most {WorkflowStep.MostBackoffSeconds}; absent, the workflow's backoffSeconds"),
    ];

    /// <summary>What the <c>reporter</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Reporter =>
    [
        new("agent", "the agent definition, as agents/<name>.md in the workflow's directory") { Required = true },
        new("statusPrompt", "the prompt file a status question is answered with") { Required = true },
        new("askPrompt", "the prompt file a free question is answered with") { Required = true },
        new("retries", "how often its agent is tried again where it failed, on each model it may use; absent, maxRetries"),
        new("backoffSeconds", $"seconds it waits before it is tried again, twice as long before each later try, at most {WorkflowStep.MostBackoffSeconds}; absent, the workflow's backoffSeconds"),
        new("tier", "a name for a class of model: one of the configuration's tierConfig, its models in the order they are tried"),
        new("model", "the model outright, whose provider, key and client follow from it"),
        new("effort", "the effort the model is asked for: one level for every model it may run on, or a map of all and models' codes giving each its own"),
        new("client", "the client, where the provider is reached two ways"),
    ];

    /// <summary>What the <c>amend</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Amend =>
    [
        new("input", "the text input an amendment adds to; absent, a run takes no amended request; at least one of input and changedAnswerMinutes"),
        new("changedAnswerMinutes", "how long the last answer a person gave can be changed for, from 1 to 60 minutes; absent, an answer is never changed"),
        new("before", "the step past which nothing is planned again"),
        new("step", "the step that plans the update, by its path from the flow's root") { Required = true },
        new("then", "the step the updated plan goes to next, beside the step that planned it"),
        new("scope", "an expression yielding the pieces of the update") { Required = true },
        new("whenAddScope", "the step that builds scope an update adds, by its path from the flow's root") { Required = true },
        new("whenChangeScope", "the step that does again an item an update changes, by its path from the flow's root") { Required = true },
        new("whenRemoveScope", "the step that takes back an item an update withdraws, by its path from the flow's root") { Required = true },
        new("maxAddedIterations", "iterations a run at the end of its loop is given to carry an update"),
    ];

    /// <summary>What one step takes: the common keys, the group's, those of every step that runs, and the types', each under its own name.</summary>
    public static IReadOnlyList<KeyDescription> Step =>
    [
        .. WorkflowStepTypes.Common,
        .. WorkflowStepTypes.Group,
        .. WorkflowStepTypes.Running,
        .. WorkflowStepTypes.All.SelectMany(type => type.Keys.Select(key => new KeyDescription($"{type.Type}.{key.Name}", key.Meaning) { Required = key.Required })),
    ];
}
