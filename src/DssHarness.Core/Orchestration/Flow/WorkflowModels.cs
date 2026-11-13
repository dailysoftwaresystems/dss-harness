using System.Text.Json.Serialization;
using DssHarness.Core.Configuration;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A workflow's definition, read from <c>.harness-config/workflows/&lt;name&gt;/&lt;name&gt;.yml</c>: a
/// deterministic flow of typed steps that depend on one another by name.
/// </summary>
/// <param name="Name">The workflow's name, as <c>workflows</c> in <c>config.json</c> declares it.</param>
/// <param name="Description">What the workflow is for, in the definition's own words.</param>
/// <param name="MaxReasoningAgents">Reasoning agents the run may have at once, where the file says.</param>
/// <param name="MaxParallelProcesses">Steps that run legs the run may have at once, where the file says.</param>
/// <param name="MaxOpenAgents">Agents with a worktree the run may have open at once, where the file says.</param>
/// <param name="MaxRetries">
/// Retries an incomplete step is given, and a reasoning step whose agent failed, on each model it may use, where the file
/// says.
/// </param>
/// <param name="MaxInstances">The most step instances a run of this workflow may have, where the file says.</param>
/// <param name="PushCheckpoints">Whether checkpoint commits are pushed as they are made.</param>
/// <param name="HandoffSeconds">How long a paused reasoning step is given to write its handoff.</param>
/// <param name="Budget">What a whole run's reasoning may cost, and the reserve that pauses the run first.</param>
/// <param name="Recommendations">The file holding the repository's bar for recommendations, relative to the workflow's directory.</param>
/// <param name="Advisor">The agent that recommends an answer to a question the engine raises itself.</param>
/// <param name="Reporter">The agent that words what a run is doing for a person who asks.</param>
/// <param name="Amend">Everything the workflow says about a request amended, or an answer changed, while a run works.</param>
/// <param name="Texts">
/// The workflow's texts: each name a markdown file of its own directory, read in a prompt as <c>{text.&lt;name&gt;}</c> -
/// a general mechanism of which <c>recommendations</c> is the shipped flow's own name, kept for the repositories that
/// read it. Null where the workflow declares none.
/// </param>
/// <param name="Answers">The answers the configuration gives to question kinds, beforehand.</param>
/// <param name="Inputs">The values a run of this workflow is started with.</param>
/// <param name="Params">The workflow's own knobs, defaulted in the file and read as <c>param.&lt;name&gt;</c>.</param>
/// <param name="Outputs">What the whole workflow declares it produces.</param>
/// <param name="Steps">The flow's steps, in the order they are declared.</param>
/// <param name="Path">Where the definition was read from, quoted in every refusal about it.</param>
public sealed record WorkflowDefinition(
    string Name,
    IReadOnlyDictionary<string, FlowAnswer> Answers,
    IReadOnlyList<FlowInput> Inputs,
    IReadOnlyList<FlowParam> Params,
    IReadOnlyList<WorkflowOutput> Outputs,
    IReadOnlyList<WorkflowStep> Steps,
    string Path,
    string? Description = null,
    int? MaxReasoningAgents = null,
    int? MaxParallelProcesses = null,
    int? MaxOpenAgents = null,
    int? MaxRetries = null,
    int? MaxInstances = null,
    bool? PushCheckpoints = null,
    int? HandoffSeconds = null,
    FlowBudget? Budget = null,
    string? Recommendations = null,
    FlowAdvisor? Advisor = null,
    FlowReporter? Reporter = null,
    FlowAmend? Amend = null,
    IReadOnlyDictionary<string, string>? Texts = null)
{
    /// <summary>The most step instances a run may have where the file says nothing (<c>maxInstances</c>).</summary>
    public const int DefaultMaxInstances = 5_000;

    /// <summary>The retries a step is given where neither it nor the workflow says (<c>maxRetries</c>).</summary>
    public const int DefaultMaxRetries = 3;

    /// <summary>
    /// The retries <paramref name="step"/> is given: its own where it says, the workflow's where it does not, and
    /// <see cref="DefaultMaxRetries"/> where neither does - the one rule the engine's retry table and a reasoning step's
    /// agent runner both read.
    /// </summary>
    /// <param name="step">The step.</param>
    public int RetriesOf(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Retries ?? MaxRetries ?? DefaultMaxRetries;
    }

    /// <summary>The inputs, by name, as a prompt or a template reads them.</summary>
    public IReadOnlyDictionary<string, FlowInput> InputsByName
        => new Dictionary<string, FlowInput>(Inputs.Select(input => new KeyValuePair<string, FlowInput>(input.Name, input)), StringComparer.Ordinal);

    /// <summary>
    /// The seconds a reasoning step waits before it is tried again after its agent failed, where the file says: twice
    /// as long before each later try, at most <see cref="WorkflowStep.MostBackoffSeconds"/>; a step's own wins.
    /// </summary>
    public int? BackoffSeconds { get; init; } = null;

    /// <summary>The params, by name, as a prompt or a template reads them.</summary>
    public IReadOnlyDictionary<string, FlowParam> ParamsByName
        => new Dictionary<string, FlowParam>(Params.Select(param => new KeyValuePair<string, FlowParam>(param.Name, param)), StringComparer.Ordinal);

    /// <summary>
    /// The steps a step gives its failure to where it says <c>onFailure: recover</c>: they find what went wrong and put
    /// it right, and the run goes where their <c>onSuccess</c> says. <see langword="null"/> where the workflow declares
    /// none, and then no step may say <c>recover</c>.
    /// </summary>
    public FlowRecover? Recover { get; init; } = null;

    /// <summary>
    /// The shape of the run's one codebase graph, said once for the run since every <c>graphify-codebase</c> step syncs
    /// the same graph; <see langword="null"/> where the workflow says nothing, and every default holds.
    /// </summary>
    public FlowCodebaseGraph? CodebaseGraph { get; init; } = null;
}

/// <summary>
/// The workflow's <c>codebaseGraph</c>: what the run's one graph is made of, as the file says it - each key left out
/// <see langword="null"/>, a list given empty kept empty for the check to refuse. Each key is optional where the run's
/// frozen definition is read back, for one left out is left out of the record too.
/// </summary>
/// <param name="Include">Patterns only the paths one of which matches are graphed.</param>
/// <param name="Exclude">Patterns the paths one of which matches are not graphed.</param>
/// <param name="Languages">The implementations used, by name.</param>
/// <param name="MaxFileBytes">The largest file read.</param>
/// <param name="Generated">What becomes of generated code: <c>low</c> or <c>exclude</c>.</param>
/// <param name="MapTokens">The cap, in tokens, of the run's repository map, made once and read as it is by every step after; 0 asks for none.</param>
public sealed record FlowCodebaseGraph(
    IReadOnlyList<string>? Include = null,
    IReadOnlyList<string>? Exclude = null,
    IReadOnlyList<string>? Languages = null,
    int? MaxFileBytes = null,
    string? Generated = null,
    int? MapTokens = null)
{
    /// <summary>
    /// The shape the block gives the run's graph, every key it leaves out at its default: read from the block, never
    /// written with it - the run's frozen definition, and its digest, hold what the file says, not this build's defaults.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Codebase.CodebaseGraphShape Shape => Codebase.CodebaseGraphShape.Of(Include, Exclude, Languages, MaxFileBytes, Generated, MapTokens);
}

/// <summary>
/// The workflow's recovery: the steps a step gives its failure to where it says <c>onFailure: recover</c> (itself, or a
/// group around it) and no handler took the failure first. They read the step that failed as <c>source</c> and see
/// every step of the flow. Where the run goes once they are done is <paramref name="OnSuccess"/>'s to say, and what a
/// failure of their own does <paramref name="OnFailure"/>'s - both written in the file, as a step's own wiring is.
/// </summary>
/// <param name="MaxVisits">
/// How often the recovery may take the failures of one step in one scope, where the file says; past it the run asks
/// (<c>recoveryBound</c>), with the advisor's recommendation of a fix and the choices. Within it - and where it says
/// nothing, within the engine's bound of <see cref="MostVisits"/> visits of one step in one scope - a step that fails
/// again after the recovery goes to it again, by its own <c>onFailure: recover</c>; the recovery never sends itself
/// anything.
/// </param>
/// <param name="Steps">The recovery's own steps, which take every key the flow's steps take.</param>
/// <param name="OnSuccess">Where the run goes once the recovery's steps are done: <see cref="FailedStep"/>.</param>
/// <param name="OnFailure">
/// What a failure inside the recovery that nothing in it takes does, once its own steps' retries are spent:
/// <see cref="PauseOnFailure"/> or <see cref="StopOnFailure"/> - never the recovery again, which would be the recovery
/// calling itself.
/// </param>
public sealed record FlowRecover(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MaxVisits,
    IReadOnlyList<WorkflowStep> Steps,
    string OnSuccess,
    string OnFailure)
{
    /// <summary>The most a recovery may be given: the engine's bound on the visits of one step in one scope.</summary>
    public const int MostVisits = 99;

    /// <summary>
    /// How many visits the recovery may make for one step in one scope before the run asks: its <c>maxVisits</c>, else
    /// <see cref="MostVisits"/> - the one bound the engine holds the recovery to and the facts of a run count by.
    /// </summary>
    [JsonIgnore]
    public int VisitsAllowed => MaxVisits ?? MostVisits;

    /// <summary>
    /// The recovery as a group of the flow: its steps under the reserved name (<see cref="FlowScope.Recovery"/>), its
    /// <c>onFailure</c> the policy of every one of them that says none - what the engine makes each visit of, and what the
    /// definition's check and the facts of a run read its steps' policy through.
    /// </summary>
    [JsonIgnore]
    public WorkflowStep Declaration => new() { Name = FlowScope.Recovery, Steps = Steps, OnFailure = OnFailure };

    /// <summary>
    /// The <c>onSuccess</c> that sends the run back to the step that gave the recovery its failure, to run again from
    /// where it stopped - a build, a test, a commit, whatever failed - once what stood in its way was put right. That step
    /// is never a reasoning step, which is never recovered, so it runs again whole.
    /// </summary>
    public const string FailedStep = "failed-step";

    /// <summary>Every <c>onSuccess</c> a recovery may say, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Successes { get; } = [FailedStep];

    /// <summary>The <c>onFailure</c> that asks a person about a failure inside the recovery (the question <c>failure</c>).</summary>
    public const string PauseOnFailure = "pause";

    /// <summary>The <c>onFailure</c> that ends the run failed on a failure inside the recovery.</summary>
    public const string StopOnFailure = "stop";

    /// <summary>Every <c>onFailure</c> a recovery may say, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Failures { get; } = [PauseOnFailure, StopOnFailure];
}

/// <summary>What a whole run's reasoning may cost, and the reserve that pauses the run before it is spent.</summary>
/// <param name="Usd">What the run's reasoning may cost, in United States dollars.</param>
/// <param name="Tokens">What the run's reasoning may spend, in tokens.</param>
/// <param name="Minutes">How long the run's reasoning may run, in minutes.</param>
/// <param name="ReservePercent">The share of the budget left when the run pauses instead of spending it.</param>
/// <remarks>
/// Each amount is optional where it is given, so each is one the run's own record of its definition holds as absent
/// where the workflow named none - a record is never guessed at, and a budget that names one amount round-trips as the
/// one it is.
/// </remarks>
public sealed record FlowBudget(double? Usd = null, long? Tokens = null, double? Minutes = null, int? ReservePercent = null);

/// <summary>The agent that recommends an answer to a question the engine raises itself.</summary>
/// <param name="Agent">The agent definition, as a file under the workflow's <c>agents</c> directory.</param>
/// <param name="Prompt">The prompt file the recommendation is asked with.</param>
/// <param name="Tier">A name for a class of model, resolved by the configuration's tiers.</param>
/// <param name="Model">The model outright, whose provider, key and client follow from it.</param>
/// <param name="Effort">The effort the model is asked for: one level, or <see langword="null"/> where <see cref="FlowAdvisor.EffortByModel"/> holds the map.</param>
/// <param name="Client">The client, where the provider is reached two ways.</param>
/// <param name="Kinds">The question kinds this advisor is asked about; every one, where the file says nothing.</param>
public sealed record FlowAdvisor(
    string Agent,
    string Prompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Effort,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Client,
    IReadOnlyList<string> Kinds)
{
    /// <summary>How often its agent is tried again where it failed, on each model it may use, where the file says.</summary>
    public int? Retries { get; init; } = null;

    /// <summary>The seconds it waits before it is tried again, doubling, where the file says.</summary>
    public int? BackoffSeconds { get; init; } = null;

    /// <summary>
    /// The effort for each model by its code, where the effort is a map: <c>all</c> gives every model the map does not
    /// name. One level in <see cref="FlowAdvisor.Effort"/> reads as <c>{all: that level}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EffortByModel { get; init; } = null;
}

/// <summary>The agent that words what a run is doing for a person who asks.</summary>
/// <param name="Agent">The agent definition, as a file under the workflow's <c>agents</c> directory.</param>
/// <param name="StatusPrompt">The prompt file a status question is answered with.</param>
/// <param name="AskPrompt">The prompt file a free question is answered with.</param>
/// <param name="Tier">A name for a class of model, resolved by the configuration's tiers.</param>
/// <param name="Model">The model outright, whose provider, key and client follow from it.</param>
/// <param name="Effort">The effort the model is asked for: one level, or <see langword="null"/> where <see cref="FlowReporter.EffortByModel"/> holds the map.</param>
/// <param name="Client">The client, where the provider is reached two ways.</param>
public sealed record FlowReporter(
    string Agent,
    string StatusPrompt,
    string AskPrompt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Tier,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Model,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Effort,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Client)
{
    /// <summary>How often its agent is tried again where it failed, on each model it may use, where the file says.</summary>
    public int? Retries { get; init; } = null;

    /// <summary>The seconds it waits before it is tried again, doubling, where the file says.</summary>
    public int? BackoffSeconds { get; init; } = null;

    /// <summary>
    /// The effort for each model by its code, where the effort is a map: <c>all</c> gives every model the map does not
    /// name. One level in <see cref="FlowReporter.Effort"/> reads as <c>{all: that level}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EffortByModel { get; init; } = null;
}

/// <summary>Everything a workflow says about a request amended, or an answer changed, while a run works.</summary>
/// <param name="Input">The input that holds the request, kept as it was given and read with its amendments.</param>
/// <param name="ChangedAnswerMinutes">How long the last answer a person gave can be changed for.</param>
/// <param name="Before">The step past which nothing is planned again: the change has no round to join.</param>
/// <param name="Step">The step that plans the update, by its path from the flow's root.</param>
/// <param name="Then">The step the updated plan goes to next, beside the step that planned it.</param>
/// <param name="Scope">An expression yielding the pieces of the update: what it adds, changes and withdraws.</param>
/// <param name="WhenAddScope">The step that builds scope an update adds, by its path from the flow's root.</param>
/// <param name="WhenChangeScope">The step that does again an item an update changes, by its path from the flow's root.</param>
/// <param name="WhenRemoveScope">The step that takes back an item an update withdraws, by its path from the flow's root.</param>
/// <param name="MaxAddedIterations">How many iterations, or rounds of a work-item group with no loop around it, a run is given to carry updates past its own, as the file says it; null where it says none.</param>
public sealed record FlowAmend(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Input,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? ChangedAnswerMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Before,
    string Step,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Then,
    string Scope,
    string WhenAddScope,
    string WhenChangeScope,
    string WhenRemoveScope,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MaxAddedIterations)
{
    /// <summary>What <see cref="MaxAddedIterations"/> is where the file says nothing: one iteration or round over the run.</summary>
    public const int DefaultMaxAddedIterations = 1;

    /// <summary>
    /// How many iterations or rounds plan updates may add, over the whole run, for work an update owes: an iteration of a
    /// loop at its bound, or a round of a work-item group with no loop around it whose round has ended. The file's
    /// <c>maxAddedIterations</c>, or <see cref="DefaultMaxAddedIterations"/> where it says none - the one owner of the
    /// default the gates and the engine both read.
    /// </summary>
    [JsonIgnore]
    public int AddedIterationsAllowed => MaxAddedIterations ?? DefaultMaxAddedIterations;
}

/// <summary>One answer a configuration gives a question kind beforehand, and how often it holds.</summary>
/// <param name="Answer">The answer, spelled as the question's options spell it.</param>
/// <param name="Times">
/// How often an answer that lets the same thing go on - <c>more</c>, <c>retry</c> or <c>compact</c> - is given before the
/// question is asked of a person, or answered with <paramref name="Then"/>.
/// </param>
/// <param name="Then">
/// The answer given once <paramref name="Times"/> is spent: one of the kind's answers that ends the matter
/// (<c>accept</c>, <c>allow</c>, <c>merge</c>, <c>stay</c>, <c>skip</c>, <c>fail</c> or <c>abort</c>).
/// </param>
public sealed record FlowAnswer(
    string Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Times,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Then);

/// <summary>One value a run of the workflow is started with, or a workflow used as a step is handed.</summary>
/// <param name="Name">The input's name, as <c>--input name=value</c> spells it, or as a <c>with:</c> value spells it.</param>
/// <param name="Description">What the value is for, shown when it is missing.</param>
/// <param name="Kind">
/// <c>text</c>, or <c>file</c>: a path inside the repository, which must be a file when the run starts - and, of a
/// workflow another uses as a step, also <c>value</c>: any typed value, read as an expression, and <c>step</c>: a name
/// its steps say in <c>onFailure</c> or <c>then</c> through <c>{input.&lt;name&gt;}</c>, resolved in the caller.
/// </param>
/// <param name="Default">What to use when nothing supplies it, or <see langword="null"/>; empty is the input not given, and is always taken.</param>
/// <param name="Required">Whether the run is refused when nothing supplies it and there is no default.</param>
/// <param name="Options">The values a <c>text</c> input takes; a run started with another is refused.</param>
public sealed record FlowInput(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    string Kind,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Default,
    bool Required,
    IReadOnlyList<string> Options)
{
    /// <summary>The kind a file that says nothing is read as.</summary>
    public const string TextKind = "text";

    /// <summary>The kind a path inside the repository is: checked to be a file when the run starts.</summary>
    public const string FileKind = "file";

    /// <summary>The kind any typed value takes, read as an expression: a used workflow's alone.</summary>
    public const string ValueKind = "value";

    /// <summary>The kind a name its steps say in onFailure or then takes, resolved in the caller: a used workflow's alone.</summary>
    public const string StepKind = "step";

    /// <summary>Every kind, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [TextKind, FileKind, ValueKind, StepKind];
}

/// <summary>
/// One of a workflow's own knobs, defaulted in the file and read as <c>param.&lt;name&gt;</c> wherever a
/// prompt or a step's key is a template: the workflow's settings apart from what a person gives the run.
/// </summary>
/// <param name="Name">The param's name, as <c>param.&lt;name&gt;</c> reads it.</param>
/// <param name="Description">What the value is for.</param>
/// <param name="Default">What to use where a value is neither given nor defaulted.</param>
/// <param name="Required">Whether the run is refused when nothing supplies it and there is no default.</param>
public sealed record FlowParam(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Default,
    bool Required);

/// <summary>What a whole workflow declares it produces, so a later run or a person can be pointed at it.</summary>
/// <param name="Name">The output's name, as the workflow's report names it.</param>
/// <param name="Description">What the value holds.</param>
/// <param name="From">An expression naming the step and the member that hold it.</param>
public sealed record WorkflowOutput(
    string Name,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Description,
    string From);

/// <summary>
/// The run-level settings of a workflow another uses as a step, carried where the use says
/// <c>useParentLimits: false</c> and applied to its steps key by key (<see cref="FlowUse.Governing"/>): a key it
/// declares governs its steps, counted across every instance of the use, and a key it declares none for is the next
/// governing use's out that declares it, or the run's own - a use never widens a run's limits by declaring nothing. Its
/// budget and its reporter are carried and not applied (<see cref="FlowUse.NotApplied"/>): the run's budget counts its steps'
/// spend and the run's reporter words them. An amend is never carried here, for an amend is a run's own and is refused
/// where the key says false.
/// </summary>
/// <param name="Budget">What the used workflow's own budget says - carried, never applied: the run's budget counts its steps' spend.</param>
/// <param name="MaxReasoningAgents">Reasoning agents its steps may have at once, where the file says.</param>
/// <param name="MaxParallelProcesses">Steps running legs its steps may have at once, where the file says.</param>
/// <param name="MaxOpenAgents">Agents with a worktree its steps may have open at once, where the file says.</param>
/// <param name="MaxRetries">Retries its steps fall back to where they say none.</param>
/// <param name="BackoffSeconds">The seconds its steps wait before another try, doubling.</param>
/// <param name="MaxInstances">The most step instances its steps may open, counted in the place of the run's own cap for them.</param>
/// <param name="HandoffSeconds">How long a paused reasoning step of it is given to write its handoff.</param>
/// <param name="Advisor">The agent that recommends an answer to a question its steps raise.</param>
/// <param name="Reporter">The used workflow's own reporter - carried, never applied: the run's reporter words the run as a whole.</param>
/// <param name="Answers">
/// The answers the used workflow gives to question kinds, consulted for its steps' questions after the step's own and
/// the groups within it, and before the caller's groups and the run's.
/// </param>
/// <param name="CodebaseGraph">The shape of the run's one graph where the run's own workflow declares none (<see cref="FlowUse.GraphOf"/>).</param>
/// <param name="Amends">Whether the used workflow declares an amend - never applied, for an amend is a run's own, and refused where the use says false.</param>
public sealed record FlowUseOwn(
    FlowBudget? Budget = null,
    int? MaxReasoningAgents = null,
    int? MaxParallelProcesses = null,
    int? MaxOpenAgents = null,
    int? MaxRetries = null,
    int? BackoffSeconds = null,
    int? MaxInstances = null,
    int? HandoffSeconds = null,
    FlowAdvisor? Advisor = null,
    FlowReporter? Reporter = null,
    IReadOnlyDictionary<string, FlowAnswer>? Answers = null,
    FlowCodebaseGraph? CodebaseGraph = null,
    bool Amends = false)
{
    /// <summary>Whether the used workflow declares nothing of its own, so every key stays the caller's.</summary>
    [JsonIgnore]
    public bool DeclaresNothing => Declared.Count == 0;

    /// <summary>Whether the used workflow declares <paramref name="key"/>, so it governs that key for its steps.</summary>
    /// <param name="key">The run-level key.</param>
    public bool Declares(FlowUseKey key)
        => key switch
        {
            FlowUseKey.MaxReasoningAgents => MaxReasoningAgents is not null,
            FlowUseKey.MaxParallelProcesses => MaxParallelProcesses is not null,
            FlowUseKey.MaxOpenAgents => MaxOpenAgents is not null,
            FlowUseKey.MaxRetries => MaxRetries is not null,
            FlowUseKey.BackoffSeconds => BackoffSeconds is not null,
            FlowUseKey.MaxInstances => MaxInstances is not null,
            FlowUseKey.HandoffSeconds => HandoffSeconds is not null,
            FlowUseKey.Advisor => Advisor is not null,
            _ => throw new ArgumentOutOfRangeException(nameof(key), key, "no run-level key a use governs by"),
        };

    /// <summary>The keys it declares, in the order a line says them; empty where it declares none.</summary>
    [JsonIgnore]
    public IReadOnlyList<string> Declared
    {
        get
        {
            var declared = new List<string>();

            if (Budget is not null)
            {
                declared.Add("budget");
            }

            if (MaxReasoningAgents is not null || MaxParallelProcesses is not null || MaxOpenAgents is not null || MaxRetries is not null || BackoffSeconds is not null || HandoffSeconds is not null)
            {
                declared.Add("limits");
            }

            if (MaxInstances is not null)
            {
                declared.Add("maxInstances");
            }

            if (Advisor is not null)
            {
                declared.Add("advisor");
            }

            if (Reporter is not null)
            {
                declared.Add("reporter");
            }

            if ((Answers?.Count ?? 0) > 0)
            {
                declared.Add("answers");
            }

            if (CodebaseGraph is not null)
            {
                declared.Add("codebaseGraph");
            }

            return declared;
        }
    }
}

/// <summary>
/// The workflow a step that says <c>use:</c> is, as resolution found it: everything of its own the step needs to run it
/// - its inputs, its declared outputs, its recovery - beside the fingerprint of what it says, which the run records so a
/// resume is refused where that changed, and the run-level keys of its own that no step applies.
/// </summary>
/// <param name="Source">The path as written, <c>./pipeline/pipeline.yml</c> among the callers.</param>
/// <param name="Name">
/// The called workflow's name, under whose directory its files are read: its directory under the repository's workflows
/// directory, <c>implementation/pipeline</c> for a pipeline the implementation workflow uses; for a remote one, or one
/// a relative use inside a remote one names, its cache place, <c>remote/&lt;host&gt;/&lt;owner&gt;/&lt;repo&gt;/&lt;commit&gt;/&lt;dir&gt;</c>.
/// </param>
/// <param name="Fingerprint">
/// What the run records of the called workflow's resolution: for a local one the digest of its parsed definition, taken
/// as the run's own definition's is, so a comment or line endings change nothing; for a remote one, or one inside it,
/// the commit it was read at.
/// </param>
/// <param name="Inputs">The called workflow's declared inputs, which the step's <c>with:</c> values fill.</param>
/// <param name="Params">The called workflow's own params, read as <c>param.&lt;name&gt;</c> inside it.</param>
/// <param name="Outputs">The called workflow's declared outputs: the step's own outputs once it ends.</param>
/// <param name="Recover">The called workflow's recovery, which a failure inside the step reaches first; null where it declares none.</param>
/// <param name="NotApplied">
/// The run-level keys the called workflow declares that no step applies, said by <c>validate-workflow</c> so nothing is
/// applied in silence: its budget and reporter, which a use that governs itself carries but nothing applies yet, its
/// <c>pushCheckpoints</c> and <c>recommendations</c>, a run's own alone, and - unless the use says
/// <c>useParentLimits: false</c> - its advisor, amend, answers, limits and <c>codebaseGraph</c>, the calling run's own
/// holding; its <c>codebaseGraph</c> even then where the run's own workflow declares one, whose shape the run's one graph
/// has (<see cref="FlowUse.GraphOf"/>).
/// </param>
/// <param name="Own">
/// The called workflow's run-level settings, carried where the use says <c>useParentLimits: false</c> and applied to
/// its steps key by key (<see cref="Governing"/>); null where the calling run's settings govern.
/// </param>
/// <param name="Texts">
/// The called workflow's texts: each name a markdown file of its own directory, read inside it as
/// <c>{text.&lt;name&gt;}</c>. Null where it declares none, and then no text of its own is read inside it.
/// </param>
public sealed record FlowUse(
    string Source,
    string Name,
    string Fingerprint,
    IReadOnlyList<FlowInput> Inputs,
    IReadOnlyList<FlowParam> Params,
    IReadOnlyList<WorkflowOutput> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FlowRecover? Recover,
    IReadOnlyList<string> NotApplied,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FlowUseOwn? Own = null,
    IReadOnlyDictionary<string, string>? Texts = null)
{
    /// <summary>
    /// The use that governs <paramref name="key"/> for the step at <paramref name="path"/>: the nearest use around it,
    /// outward, that says <c>useParentLimits: false</c> and declares that key - one that declares nothing of it governs
    /// nothing of it, so the next one out that declares it does, or none does and the run's own holds. The one owner of
    /// which use governs a step, which the engine's gates, retries, handoffs, instance caps and advisor and each step's
    /// runner all ask by the step's declaration path: a step of the flow's own recovery (<c>recover.&lt;step&gt;</c>) is
    /// the run's wherever the failure it was taken for happened, and a step of a used workflow's recovery
    /// (<c>&lt;use&gt;.recover.&lt;step&gt;</c>) is that workflow's. Null where no use governs the key.
    /// </summary>
    /// <param name="definition">The resolved definition the run holds.</param>
    /// <param name="path">The step's declaration path, dotted, from the flow's root.</param>
    /// <param name="key">The run-level key asked of.</param>
    public static FlowGoverning? Governing(WorkflowDefinition definition, string path, FlowUseKey key)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        FlowGoverning? governing = null;
        var parts = path.Split('.');
        var steps = definition.Steps;
        var start = 0;
        WorkflowStep? group = null;

        // The flow's own recovery hangs from the root under its reserved name, and a used workflow's from the step that
        // uses it, as the definition's names are walked (FlowScope).
        if (parts[0] == FlowScope.Recovery && definition.Recover is { } root)
        {
            steps = root.Steps;
            start = 1;
            group = root.Declaration;
        }

        // The step itself is never its own governing use: only the groups around it are walked.
        for (var at = start; at + 1 < parts.Length; at++)
        {
            if (parts[at] == FlowScope.Recovery && group?.Use?.Recover is { } called)
            {
                steps = called.Steps;
                continue;
            }

            if (steps.FirstOrDefault(step => string.Equals(step.Name, parts[at], StringComparison.Ordinal)) is not { } found)
            {
                break;
            }

            if (found.Use?.Own is { } own && own.Declares(key))
            {
                governing = new FlowGoverning(string.Join('.', parts[..(at + 1)]), found, own);
            }

            group = found;
            steps = found.Steps;
        }

        return governing;
    }

    /// <summary>
    /// The retries the step at <paramref name="path"/> is given: its own where it says, the <c>maxRetries</c> of the use
    /// that governs it where one declares it (<see cref="Governing"/>), and else the workflow's rule
    /// (<see cref="WorkflowDefinition.RetriesOf"/>) - the one rule the engine's retry table and a reasoning step's agent
    /// runner both read.
    /// </summary>
    /// <param name="definition">The resolved definition the run holds.</param>
    /// <param name="path">The step's declaration path.</param>
    /// <param name="step">The step.</param>
    public static int RetriesOf(WorkflowDefinition definition, string path, WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        return step.Retries ?? Governing(definition, path, FlowUseKey.MaxRetries)?.Own.MaxRetries ?? definition.RetriesOf(step);
    }

    /// <summary>
    /// The seconds the step at <paramref name="path"/> waits before it is tried again: its own where it says, the
    /// <c>backoffSeconds</c> of the use that governs it where one declares it, and else the workflow's; null where none
    /// says, and the runner's default holds.
    /// </summary>
    /// <param name="definition">The resolved definition the run holds.</param>
    /// <param name="path">The step's declaration path.</param>
    /// <param name="step">The step.</param>
    public static int? BackoffSecondsOf(WorkflowDefinition definition, string path, WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(step);

        return step.BackoffSeconds ?? Governing(definition, path, FlowUseKey.BackoffSeconds)?.Own.BackoffSeconds ?? definition.BackoffSeconds;
    }

    /// <summary>
    /// The shape of the run's one codebase graph, settled once for the run: the run's own workflow's where it declares
    /// one - a governing use's then said as not applied (<see cref="NotApplied"/>) - else the first a use that governs
    /// itself declares, in declaration order - the uses in recoveries among them - and null where none does, every
    /// default then holding. Every sync, every serving and every graphify step of the run reads this one shape, for one
    /// graph synced under two shapes would list and drop files between its syncs - so where two governing uses declare a
    /// shape and the run's own workflow none, they give the graph one, for two that differ are refused (V66), and the
    /// first in declaration order is the shape they all give.
    /// </summary>
    /// <param name="definition">The resolved definition the run holds.</param>
    public static FlowCodebaseGraph? GraphOf(WorkflowDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return definition.CodebaseGraph
            ?? FlowScope.Declared(definition).Select(declared => declared.Step.Use?.Own?.CodebaseGraph).FirstOrDefault(graph => graph is not null);
    }
}

/// <summary>The run-level keys a use that governs itself governs its steps by, each on its own (<see cref="FlowUse.Governing"/>).</summary>
public enum FlowUseKey
{
    /// <summary><c>maxReasoningAgents</c>: the reasoning agents its steps may have at once.</summary>
    MaxReasoningAgents,

    /// <summary><c>maxParallelProcesses</c>: the steps running legs its steps may have at once.</summary>
    MaxParallelProcesses,

    /// <summary><c>maxOpenAgents</c>: the agents with a worktree its steps may have open at once.</summary>
    MaxOpenAgents,

    /// <summary><c>maxRetries</c>: the retries its steps fall back to.</summary>
    MaxRetries,

    /// <summary><c>backoffSeconds</c>: the wait before another try.</summary>
    BackoffSeconds,

    /// <summary><c>maxInstances</c>: the step instances its steps may open.</summary>
    MaxInstances,

    /// <summary><c>handoffSeconds</c>: how long a paused reasoning step of it is given to write its handoff.</summary>
    HandoffSeconds,

    /// <summary><c>advisor</c>: the agent that recommends an answer to its steps' questions.</summary>
    Advisor,
}

/// <summary>The use that governs a key for a step (<see cref="FlowUse.Governing"/>).</summary>
/// <param name="Path">The use step's declaration path: what tells one use from another, every instance of it counted as one.</param>
/// <param name="Step">The use step.</param>
/// <param name="Own">The used workflow's own settings, which declare the key.</param>
public sealed record FlowGoverning(string Path, WorkflowStep Step, FlowUseOwn Own);

/// <summary>
/// One step of a workflow: a definition, of one <c>type</c>, or a group holding <c>steps</c> of its own.
/// </summary>
/// <remarks>
/// <para>
/// One flat record with every per-type field nullable, the codebase's shape for discriminated things:
/// the parser reads a key against the table <see cref="WorkflowStepTypes"/> holds for the step's type
/// and refuses a key that does not apply to it, with the reason.
/// </para>
/// <para>
/// The list keys that take either a list written out or an expression that yields one
/// (<see cref="Writes"/>, <see cref="Within"/>, the fold family's) are <see cref="FlowList"/> values:
/// written out, or an expression - never both, and never a list a template fills.
/// </para>
/// </remarks>
public sealed record WorkflowStep
{
    /// <summary>The step's name, unique among its siblings and never a reserved word.</summary>
    public required string Name { get; init; }

    /// <summary>What the step is for, in the definition's own words.</summary>
    public string? Description { get; init; } = null;

    /// <summary>The step's type, checked against <see cref="WorkflowStepTypes"/>; absent on a group.</summary>
    public string? Type { get; init; } = null;

    /// <summary>The group's own steps; a group has these and no <see cref="Type"/>.</summary>
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];

    /// <summary>
    /// The workflow this step is, as written: <c>use: ./pipeline.yml</c>. Read by the parser, cleared by resolution -
    /// which puts the called workflow's steps in <see cref="Steps"/> and its record in <see cref="Use"/> - so a
    /// definition that reaches the engine holding one was never resolved, and is refused.
    /// </summary>
    public string? UseSource { get; init; } = null;

    /// <summary>The values the step hands the workflow it uses, as written: one scalar for each of its inputs.</summary>
    public IReadOnlyDictionary<string, string> With { get; init; } = NoWith;

    /// <summary>
    /// Which run's settings govern the workflow this step uses: <see langword="null"/> or <see langword="true"/> the calling
    /// run's, as from the first release of <c>use</c>, with each run-level key the used workflow declares said as not
    /// applied; <see langword="false"/> the used workflow's own, key by key (<see cref="FlowUse.Governing"/>): a key is
    /// governed by the nearest such use around a step that declares it, and one none declares stays the run's. Its budget
    /// and reporter are carried and not applied (<see cref="FlowUse.NotApplied"/>), and an amend is refused there, for
    /// an amend is a run's own.
    /// </summary>
    public bool? UseParentLimits { get; init; } = null;

    /// <summary>
    /// The called workflow, once the step is resolved: its name among the repository's workflows, the fingerprint of its
    /// file, its inputs, outputs and recovery, and the run-level keys of its own that no step applies. Null on every
    /// other step, and on this one before resolution.
    /// </summary>
    public FlowUse? Use { get; init; } = null;

    /// <summary>
    /// The workflow this step is declared in, where that is not the run's own: the called workflow's name, under which
    /// its files - its agents, prompts and schemas - are read. Null on every step of the run's own workflow.
    /// </summary>
    public string? WorkflowOf { get; init; } = null;

    /// <summary>No with values at all, the shape a step that names none holds.</summary>
    private static readonly IReadOnlyDictionary<string, string> NoWith = new Dictionary<string, string>();

    /// <summary>The steps this one waits for, by name, each declared before it among its siblings.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>An expression that says whether the step runs at all.</summary>
    public string? When { get; init; } = null;

    /// <summary>
    /// What a failure of the step that no handler took does: <c>stop</c> (the default), <c>pause</c>, <c>continue</c>,
    /// or <c>recover</c>, given to the workflow's recovery; absent, the nearest group's around it.
    /// </summary>
    public string? OnFailure { get; init; } = null;

    /// <summary>The <c>onFailure</c> that gives a step's failure to the workflow's recovery.</summary>
    public const string Recover = "recover";

    /// <summary>Every <c>onFailure</c> a step may say, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> FailurePolicies { get; } = ["stop", "pause", "continue", Recover];

    /// <summary>
    /// Retries the step is given where it ends incomplete - a reasoning step also where its agent failed, on each model it
    /// may use - where the file says.
    /// </summary>
    public int? Retries { get; init; } = null;

    /// <summary>Whether the step runs even where its dependencies did not reach it.</summary>
    public bool? Always { get; init; } = null;

    /// <summary>Whether the step runs with nothing beside it.</summary>
    public bool? Alone { get; init; } = null;

    /// <summary>Whether the step may be skipped, said so in the report, where its <c>when</c> does not hold.</summary>
    public bool? Skippable { get; init; } = null;

    /// <summary>The answers this step gives to question kinds, beforehand.</summary>
    public IReadOnlyDictionary<string, FlowAnswer> Answers { get; init; } = NoAnswers;

    /// <summary>The ending of another step that raises this one: it is never started by the schedule.</summary>
    public FlowOn? On { get; init; } = null;

    /// <summary>The step the run goes back to once this handler is done.</summary>
    public string? Then { get; init; } = null;

    /// <summary>How often this handler may be raised; past it the run asks.</summary>
    public int? MaxVisits { get; init; } = null;

    /// <summary>How often a handler may be raised in one scope where its file names no <c>maxVisits</c>.</summary>
    public const int DefaultMaxVisits = 3;

    /// <summary>
    /// How often this handler may be raised in one scope before the run asks: its <c>maxVisits</c>, else
    /// <see cref="DefaultMaxVisits"/> - the one bound the engine holds a handler to and the facts of a run count by.
    /// </summary>
    [JsonIgnore]
    public int VisitsAllowed => MaxVisits ?? DefaultMaxVisits;

    /// <summary>
    /// The longest one attempt of a step that runs may take, in whole minutes, where the file says: counted by the engine
    /// from each start of the step and never while it waits on a question, and past it the step is stopped and ends
    /// incomplete (21). Absent, its type's own limit where the type says one (<see cref="WorkflowStepTypes.TimeLimitOf"/>),
    /// else none. A reason step's handoff turn at a pause is bounded by the workflow's top-level <c>handoffSeconds</c>
    /// (<see cref="WorkflowDefinition.HandoffSeconds"/>), not by this. Never a group's or a pause step's key.
    /// </summary>
    public int? TimeoutMinutes { get; init; } = null;

    /// <summary>The most minutes <see cref="TimeoutMinutes"/> may say: a day.</summary>
    public const int MostTimeoutMinutes = 1_440;

    /// <summary>An expression yielding the work items a <c>forEach</c> group runs its steps for, once each.</summary>
    public string? ForEach { get; init; } = null;

    /// <summary>A <c>repeat</c> group's bound: when its iterations end, and what is left.</summary>
    public FlowRepeat? Repeat { get; init; } = null;

    /// <summary>The members a block gathers from its members' outputs into one list, named as its members read them.</summary>
    public IReadOnlyList<string> Gather { get; init; } = [];

    /// <summary>The agent definition a reason step runs, as a file under the workflow's <c>agents</c> directory.</summary>
    public string? Agent { get; init; } = null;

    /// <summary>A name for a class of model, resolved by the configuration's tiers.</summary>
    public string? Tier { get; init; } = null;

    /// <summary>The model outright, whose provider, key and client follow from it.</summary>
    public string? Model { get; init; } = null;

    /// <summary>
    /// The effort the model is asked for: one level for every model the step may run on, or <see langword="null"/>
    /// where <see cref="EffortByModel"/> holds a level for each.
    /// </summary>
    public string? Effort { get; init; } = null;

    /// <summary>
    /// The effort for each model the step may run on, by its code, where the effort is a map: <c>all</c> gives every
    /// model the map does not name, and a model named by no entry and by no <c>all</c> runs at its own default.
    /// <see cref="WorkflowStep.Effort"/> given one level reads as <c>{all: that level}</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EffortByModel { get; init; } = null;

    /// <summary>The client, where the provider is reached two ways.</summary>
    public string? Client { get; init; } = null;

    /// <summary>The prompt file the step is asked with, relative to the workflow's directory.</summary>
    public string? Prompt { get; init; } = null;

    /// <summary>
    /// The prompt file a step that goes on with its own work (<see cref="Resume"/> names itself) is asked with when it
    /// runs again, in place of <see cref="Prompt"/>.
    /// </summary>
    public string? ResumePrompt { get; init; } = null;

    /// <summary>The JSON schema file the step's answer is held to, relative to the workflow's directory.</summary>
    public string? Outputs { get; init; } = null;

    /// <summary>Whether the reason step writes the tree (<c>write</c>, in an agent's worktree) or only reads (<c>read</c>, the default).</summary>
    public string? Access { get; init; } = null;

    /// <summary>The <c>create-agent</c> step whose agent's worktree the step works in.</summary>
    public string? In { get; init; } = null;

    /// <summary>The reason step whose work this one goes on with.</summary>
    public string? Resume { get; init; } = null;

    /// <summary>
    /// The step of the flow's top level that triages each issue this reason step reports as there before the run: one
    /// that says <c>raised: preExisting</c>, raised once for each issue.
    /// </summary>
    public string? OnPreExisting { get; init; } = null;

    /// <summary>
    /// What raises this reason step in place of the schedule: <see cref="RaisedByPreExisting"/>, an issue a step reported as
    /// there before the run. A raised step is never scheduled.
    /// </summary>
    public string? Raised { get; init; } = null;

    /// <summary>The <c>raised</c> of a step raised by each pre-existing issue a step reports.</summary>
    public const string RaisedByPreExisting = "preExisting";

    /// <summary>
    /// Where a step raised by a pre-existing issue sends one it decides to fix: <see cref="Recover"/>, the recovery, which
    /// fixes it at its root as it fixes a failure.
    /// </summary>
    public string? OnAddress { get; init; } = null;

    /// <summary>Whether the step is never given the author's work to go on with.</summary>
    public bool? Independent { get; init; } = null;

    /// <summary>Restrictions the agent works under, beyond the workflow's own.</summary>
    public IReadOnlyList<string> Restrictions { get; init; } = [];

    /// <summary>
    /// The sources of <c>agentWebSearch</c> a reason step's <c>web_search</c> asks, by name or tag, as written - one given
    /// empty kept empty, for the check to refuse; <see langword="null"/> where it says none, and asks <c>defaults</c>.
    /// </summary>
    public IReadOnlyList<string>? Search { get; init; } = null;

    /// <summary>The paths a writing reason step may write, written out or as an expression.</summary>
    public FlowList? Writes { get; init; } = null;

    /// <summary>Whether the reason step may put its questions to a person; one that may not writes its assumptions down.</summary>
    public bool? Ask { get; init; } = null;

    /// <summary>How often a step that asks may ask before it goes on with what it has.</summary>
    public int? AskRounds { get; init; } = null;

    /// <summary>
    /// The seconds a reasoning step waits before it is tried again after its agent failed, where the file says: twice as
    /// long before each later try, at most <see cref="MostBackoffSeconds"/>; absent, the workflow's.
    /// </summary>
    public int? BackoffSeconds { get; init; } = null;

    /// <summary>The seconds a reasoning step waits, where neither it nor the workflow says.</summary>
    public const int DefaultBackoffSeconds = 30;

    /// <summary>The longest a reasoning step ever waits before it is tried again, however the waits have doubled.</summary>
    public const int MostBackoffSeconds = 900;

    /// <summary>
    /// The step's use of the run's codebase graph: <c>off</c> (none), <c>map</c> (its context pack alone) or <c>tools</c>
    /// (the pack and the one tool <c>graph</c>). Absent, <see cref="GraphOf"/> decides - <c>tools</c> where a graph is
    /// made upstream and the step's agent reads, <c>map</c> where one is and its agent does not, <c>off</c> where none is.
    /// </summary>
    public string? Graph { get; init; } = null;

    /// <summary>Every <c>graph</c> a reason step may say, in the order a refusal lists them.</summary>
    public static readonly IReadOnlyList<string> GraphModes = ["off", "map", "tools"];

    /// <summary>
    /// Where the step's context pack starts from: <see cref="GraphFromTask"/> (the default), <see cref="GraphFromChanges"/>,
    /// or the reason step - one of its own scope or one around it - whose working set seeds it in place of the work the run
    /// holds. A step that goes on with another's work may say it, and its own is what shapes its pack, for its pack is its own.
    /// </summary>
    public string? GraphFrom { get; init; } = null;

    /// <summary>The default value of <see cref="GraphFrom"/>: the pack starts from what the step's task names, and the working sets of the work the run holds for it.</summary>
    public const string GraphFromTask = "task";

    /// <summary>The change-first value of <see cref="GraphFrom"/>: the pack starts from what the step's tree holds differently from its base.</summary>
    public const string GraphFromChanges = "changes";

    /// <summary>
    /// The words <see cref="GraphFrom"/> says a seed with, in the order the help lists them - every other value names a
    /// reason step - read by the definition's check, the help and the serving alike: a step named one of them cannot be
    /// named as a seed, for the word means the seed.
    /// </summary>
    public static readonly IReadOnlyList<string> GraphFromWords = [GraphFromTask, GraphFromChanges];

    /// <summary>
    /// The cap of the step's context pack, in estimated tokens, from 0 to <see cref="MostGraphPromptTokens"/> - 0 asks for
    /// no pack; absent, <see cref="DefaultGraphPromptTokens"/>. A step that goes on with another's work may say its own.
    /// </summary>
    public int? GraphPromptTokens { get; init; } = null;

    /// <summary>The cap of a step's context pack where neither the step nor anything else says one.</summary>
    public const int DefaultGraphPromptTokens = 3_000;

    /// <summary>The most tokens a step may cap its context pack at.</summary>
    public const int MostGraphPromptTokens = 20_000;

    /// <summary>Whether the step's reads are narrowed to the graph: <c>narrow</c> (the default) or <c>guide</c>.</summary>
    public string? GraphReads { get; init; } = null;

    /// <summary>Every <c>graphReads</c> a reason step may say, in the order a refusal lists them.</summary>
    public static readonly IReadOnlyList<string> GraphReadsModes = ["narrow", "guide"];

    /// <summary>The narrowing of a step's whole-file reads to the graph: the default of <c>graphReads</c>.</summary>
    public const string GraphReadsNarrow = "narrow";

    /// <summary>
    /// The step's use of the run's graph, its default resolved: its own <c>graph</c>; else <c>tools</c> where one is made
    /// upstream and its agent reads, <c>map</c> where one is and its agent does not, <c>off</c> where none is - an agent
    /// the definition does not name is read as one that reads, its file's own problems said where it is read.
    /// </summary>
    /// <param name="step">The reason step - for one that goes on with another's work, the step its chain began with, whose <c>graph</c> its conversation holds.</param>
    /// <param name="agent">The agent the step works as, where its definition was read.</param>
    /// <param name="graphUpstream">
    /// Whether the step has a graph to be given: a graphify step upstream of it - or, for a step of the recovery, one of the
    /// run that ended done before it, as the run stands; the definition's check, which has no run, asks whether one is declared.
    /// </param>
    public static string GraphOf(WorkflowStep step, AgentDefinition? agent, bool graphUpstream)
        => step.Graph ?? (graphUpstream ? agent?.May.Contains("read") == false ? "map" : "tools" : "off");

    /// <summary>The legs a leg step runs, by name; every declared leg where the file says nothing.</summary>
    public IReadOnlyList<string> Legs { get; init; } = [];

    /// <summary>What a red verdict of a check does to the step: <c>red</c> or <c>never</c>.</summary>
    public string? FailOn { get; init; } = null;

    /// <summary>The test filter a test step passes on, a template.</summary>
    public string? Filter { get; init; } = null;

    /// <summary>
    /// An expression that says whether the step's <see cref="Filter"/> is given: where it reads false the step gives no
    /// filter and every test of its legs runs; absent, the filter is given as written.
    /// </summary>
    public string? FilterWhen { get; init; } = null;

    /// <summary>The tests a test step leaves out, by name, each a template.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>The labels a test step selects its tests by, each a template.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Whether a test step skips the build and tests what is there.</summary>
    public bool? SkipBuild { get; init; } = null;

    /// <summary>The predefined runner a <c>run</c> step runs.</summary>
    public string? Runner { get; init; } = null;

    /// <summary>The values a <c>run</c> step hands its runner's inputs, each a template.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = NoInputValues;

    /// <summary>The arms a sweep runs, by id; every arm where the file says nothing.</summary>
    public IReadOnlyList<string> Arms { get; init; } = [];

    /// <summary>Whether a sweep drives only the arms with no recorded <c>passed</c> that still holds.</summary>
    public bool? Pending { get; init; } = null;

    /// <summary>Whether a <c>graphify-codebase</c> step extracts every file of the tree it syncs again, reusing no extract.</summary>
    public bool? Rebuild { get; init; } = null;

    /// <summary>Whether a <c>create-agent</c> step makes its agent with nothing handed to it.</summary>
    public bool? Empty { get; init; } = null;

    /// <summary>The <c>create-agent</c> step whose agent the fold family step works on.</summary>
    public string? Of { get; init; } = null;

    /// <summary>
    /// The <c>create-agent</c> step whose agent a refresh or a rebase leaves out: it works on every other live agent of
    /// the run that has a worktree, each on its own.
    /// </summary>
    public string? Others { get; init; } = null;

    /// <summary>What a fold or a deletion is allowed to carry, member for member the fold command's options.</summary>
    public FlowAllow? Allow { get; init; } = null;

    /// <summary>The paths a fold may write, written out or as an expression.</summary>
    public FlowList? Within { get; init; } = null;

    /// <summary>Whether a deletion discards what is not committed rather than folding it.</summary>
    public bool? DiscardUncommitted { get; init; } = null;

    /// <summary>The paths a refresh weighs, written out or as an expression.</summary>
    public FlowList? Paths { get; init; } = null;

    /// <summary>Whether a refresh weighs every path the main tree moved, in place of <see cref="Paths"/>.</summary>
    public bool? All { get; init; } = null;

    /// <summary>The paths a refresh leaves as the agent changed them, written out or as an expression.</summary>
    public FlowList? Except { get; init; } = null;

    /// <summary>The paths whose copy in the agent is not its work, written out or as an expression.</summary>
    public FlowList? Stale { get; init; } = null;

    /// <summary>The paths both trees changed that a merge takes, written out or as an expression.</summary>
    public FlowList? Merge { get; init; } = null;

    /// <summary>The paths a rebase is told were reconciled by hand: written out only, a person's answer.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>An expression yielding the list of work items a plan check checks.</summary>
    public string? Items { get; init; } = null;

    /// <summary>An expression yielding the pieces of a plan update, checked with the items.</summary>
    public string? Scope { get; init; } = null;

    /// <summary>
    /// The commit an anchor balance measures from, or the branch a pull request goes to - both a
    /// template, read by the step's own type.
    /// </summary>
    public string? Base { get; init; } = null;

    /// <summary>The row ids an anchor balance is held to, written out or as an expression.</summary>
    public FlowList? Rows { get; init; } = null;

    /// <summary>What an anchor citations check judges: <c>currentTree</c> (the default), <c>currentCommit</c> or <c>currentPullRequest</c>.</summary>
    public string? Subject { get; init; } = null;

    /// <summary>The files an anchor citations check is held to, written out or as an expression.</summary>
    public FlowList? AnchorPaths { get; init; } = null;

    /// <summary>The branch a CI read reads, a template.</summary>
    public string? Branch { get; init; } = null;

    /// <summary>How many of a branch's runs a CI read looks at, where the file says.</summary>
    public int? Limit { get; init; } = null;

    /// <summary>The commit whose checks a CI wait waits for, a template.</summary>
    public string? Commit { get; init; } = null;

    /// <summary>How long a CI wait waits for the first check to appear.</summary>
    public int? AppearSeconds { get; init; } = null;

    /// <summary>How long a CI wait waits, from its start, for every check to end; absent, <see cref="DefaultWaitMinutes"/>.</summary>
    public int? WaitMinutes { get; init; } = null;

    /// <summary>How long a CI wait waits for every check to end where the file says not: an hour.</summary>
    public const int DefaultWaitMinutes = Ci.CiChecksRequest.DefaultWaitMinutes;

    /// <summary>How often a CI wait reads the checks while it waits.</summary>
    public int? PollSeconds { get; init; } = null;

    /// <summary>The checks a CI wait does not count, by name.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];

    /// <summary>The commit message a commit step writes, a template.</summary>
    public string? Message { get; init; } = null;

    /// <summary>The values a commit message template reads, each a template.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = NoInputValues;

    /// <summary>The remote a push pushes to, where the file says.</summary>
    public string? Remote { get; init; } = null;

    /// <summary>Whether a push pushes a checkpoint commit.</summary>
    public bool? Checkpoint { get; init; } = null;

    /// <summary>The pull request's title, a template.</summary>
    public string? Title { get; init; } = null;

    /// <summary>The file holding the pull request's body, relative to the workflow's directory.</summary>
    public string? Body { get; init; } = null;

    /// <summary>Whether the pull request is opened as a draft.</summary>
    public bool? Draft { get; init; } = null;

    /// <summary>The question a pause step puts to a person, a template.</summary>
    public string? Question { get; init; } = null;

    /// <summary>The answers a pause step offers.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The options that need a text with them.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>Whether only a person may answer, and no configuration answers beforehand.</summary>
    public bool? Person { get; init; } = null;

    /// <summary>An expression yielding a list of questions to show under the step's own.</summary>
    public string? Questions { get; init; } = null;

    /// <summary>Answers the person gave beforehand with the run's input, each matched by an expression.</summary>
    public IReadOnlyList<FlowPreAnswer> PreAnswer { get; init; } = [];

    /// <summary>Whether this step is a group: it holds <see cref="Steps"/> and names no <see cref="Type"/>.</summary>
    public bool IsGroup => Type is null;

    private static readonly IReadOnlyDictionary<string, FlowAnswer> NoAnswers =
        new Dictionary<string, FlowAnswer>(StringComparer.Ordinal);

    private static readonly IReadOnlyDictionary<string, string> NoInputValues =
        new Dictionary<string, string>(StringComparer.Ordinal);
}

/// <summary>The ending of one step that raises another.</summary>
/// <param name="Step">The step whose ending raises the handler, a sibling.</param>
/// <param name="Outcome">The ending that raises it: <c>failure</c>, <c>success</c> or <c>end</c>.</param>
/// <param name="Codes">The exit codes a <c>failure</c> answers to; every one where the file says nothing.</param>
public sealed record FlowOn(string Step, string Outcome, IReadOnlyList<int> Codes)
{
    /// <summary>The ending that raises a handler, and how each is spelled.</summary>
    public static IReadOnlyList<string> Outcomes { get; } = ["failure", "success", "end"];
}

/// <summary>A <c>repeat</c> group's bound: when its iterations end, and what is left.</summary>
/// <param name="Until">The expression every iteration is followed by while it is false.</param>
/// <param name="MaxIterations">The most iterations, however the <c>until</c> reads.</param>
/// <param name="Rounds">The rounds in which every false <c>until</c> is followed.</param>
/// <param name="Blocking">The expression that says, past the rounds, whether what is left must still be dealt with.</param>
/// <param name="BlockersLimitRounds">Further rounds past <paramref name="Rounds"/> while <paramref name="Blocking"/> holds.</param>
/// <param name="Leftover">Where what the loop left is written, read as the run's leftover.</param>
/// <param name="DecideAfter">
/// The step of each iteration the loop's decision - its <c>until</c>, its <c>blocking</c> and its bound - is read at
/// when it ends, instead of at the iteration's end: what follows that step in the iteration starts only where the loop
/// goes on, and a loop that ends at its bound leaves what that step found, none of it acted on. Null where the decision
/// is read at each iteration's end, as it always was.
/// </param>
/// <param name="BlockersUnbounded">
/// Whether <c>blockersLimitRounds</c> says <c>false</c>: no bound past the rounds but the workflow's budget.
/// </param>
public sealed record FlowRepeat(
    string Until,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? MaxIterations,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? Rounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Blocking,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] int? BlockersLimitRounds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Leftover,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? DecideAfter,
    bool BlockersUnbounded = false)
{
    /// <summary>The further rounds past <see cref="Rounds"/> while blocking holds, where the file names none.</summary>
    public const int DefaultBlockersLimitRounds = 3;

    /// <summary>The further rounds past <see cref="Rounds"/> while blocking holds: the file's, else <see cref="DefaultBlockersLimitRounds"/>.</summary>
    [JsonIgnore]
    public int BlockersRounds => BlockersLimitRounds ?? DefaultBlockersLimitRounds;

    /// <summary>
    /// The iterations the loop's own bound allows, with <paramref name="extra"/> more its answers or a plan update gave:
    /// its <c>maxIterations</c>, or its rounds and the further ones its blocking holds for - <see cref="int.MaxValue"/>
    /// where <c>blockersLimitRounds</c> is <c>false</c>, which no count of iterations ever reaches. The one bound the
    /// engine holds a loop to and the facts of a run count what is left by.
    /// </summary>
    /// <param name="extra">The iterations given beyond the file's bound.</param>
    public int Bound(int extra)
        => Rounds is { } rounds
            ? BlockersUnbounded ? int.MaxValue : rounds + BlockersRounds + extra
            : (MaxIterations ?? 0) + extra;
}

/// <summary>What a fold or a deletion is allowed to carry, member for member the fold command's options.</summary>
/// <param name="Settled">Paths reconciled by hand. A person's answer, written out only.</param>
/// <param name="New">The new rows named, of those the agent filed, written out or as an expression.</param>
/// <param name="AcceptLost">Cells of existing rows whose stored text the fold would not keep. A person's answer, written out only.</param>
/// <param name="Merge">Paths both trees changed whose clean three-way merge the fold takes, written out or as an expression.</param>
/// <param name="AcceptOver">Cells of rows an earlier fold applied that both the agent and the registries changed since. A person's answer, written out only.</param>
public sealed record FlowAllow(
    IReadOnlyList<string> Settled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FlowList? New,
    IReadOnlyList<string> AcceptLost,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FlowList? Merge,
    IReadOnlyList<string> AcceptOver);

/// <summary>
/// One answer a person gave beforehand with the run's input, matched by an expression.
/// </summary>
/// <param name="When">The expression that says whether this answer is the person's.</param>
/// <param name="Answer">The answer it gives.</param>
/// <param name="Text">A text the answer carries, where the question's option takes one.</param>
/// <param name="Quote">Words looked for in the run's input before the answer counts, quoted in the report.</param>
public sealed record FlowPreAnswer(
    string When,
    string Answer,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Text,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] FlowQuote? Quote);

/// <summary>The words a pre-answer quotes from the run's input, looked for there before the answer counts.</summary>
/// <param name="Text">An expression yielding the words to look for.</param>
/// <param name="Input">The input to look in, one the workflow declares.</param>
public sealed record FlowQuote(string Text, string Input);

/// <summary>
/// A key that takes a list of strings: written out, or an expression that yields one - never both,
/// and never put together by a template.
/// </summary>
/// <param name="Written">The list as written out, or empty where the value is an expression.</param>
/// <param name="Expression">The expression, where the value is one; otherwise <see langword="null"/>.</param>
public sealed record FlowList(IReadOnlyList<string> Written, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Expression);
