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
        new("maxRetries", "retries an incomplete step is given; absent, 3"),
        new("maxInstances", "the most step instances a run may have; absent, 5000"),
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
        new("tier", "a name for a class of model, resolved by the configuration's agent tiers"),
        new("model", "the model outright, whose provider, key and client follow from it"),
        new("effort", "the effort the model is asked for"),
        new("fallback", "models to go on with, in order, when the one serving declines or its account is at its limit"),
        new("provider", "the provider, where the model alone does not say it"),
        new("client", "the client, where the provider is reached two ways"),
        new("kinds", "the question kinds this advisor is asked about; absent, every one"),
    ];

    /// <summary>What the <c>reporter</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Reporter =>
    [
        new("agent", "the agent definition, as agents/<name>.md in the workflow's directory") { Required = true },
        new("statusPrompt", "the prompt file a status question is answered with") { Required = true },
        new("askPrompt", "the prompt file a free question is answered with") { Required = true },
        new("tier", "a name for a class of model, resolved by the configuration's agent tiers"),
        new("model", "the model outright, whose provider, key and client follow from it"),
        new("effort", "the effort the model is asked for"),
        new("fallback", "models to go on with, in order, when the one serving declines or its account is at its limit"),
        new("provider", "the provider, where the model alone does not say it"),
        new("client", "the client, where the provider is reached two ways"),
    ];

    /// <summary>What the <c>amend</c> section takes.</summary>
    public static IReadOnlyList<KeyDescription> Amend =>
    [
        new("input", "the input that holds the request; absent, the workflow's first text input"),
        new("changedAnswerMinutes", "how long the last answer a person gave can be changed for"),
        new("before", "the step past which nothing is planned again"),
        new("step", "the step that plans the update, by its path from the flow's root") { Required = true },
        new("then", "the step the updated plan goes to next, beside the step that planned it"),
        new("scope", "an expression yielding the pieces of the update") { Required = true },
        new("whenAddScope", "the step that builds scope an update adds, by its path from the flow's root") { Required = true },
        new("whenChangeScope", "the step that does again an item an update changes, by its path from the flow's root") { Required = true },
        new("whenRemoveScope", "the step that takes back an item an update withdraws, by its path from the flow's root") { Required = true },
        new("maxAddedIterations", "iterations a run at the end of its loop is given to carry an update"),
    ];

    /// <summary>What one step takes: the common keys, the group's, and the types', each under its own name.</summary>
    public static IReadOnlyList<KeyDescription> Step =>
    [
        .. WorkflowStepTypes.Common,
        .. WorkflowStepTypes.Group,
        .. WorkflowStepTypes.All.SelectMany(type => type.Keys.Select(key => new KeyDescription($"{type.Type}.{key.Name}", key.Meaning) { Required = key.Required })),
    ];
}
