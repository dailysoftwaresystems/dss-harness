using DssHarness.Core.Configuration;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// A workflow's definition, read from <c>.harness-config/workflows/&lt;name&gt;/&lt;name&gt;.yml</c>: a
/// deterministic flow of typed steps that depend on one another by name.
/// </summary>
/// <param name="Name">The workflow's name, as <c>workflows</c> in <c>config.json</c> declares it.</param>
/// <param name="Description">What the workflow is for, shown by <c>validate-workflow</c>.</param>
/// <param name="MaxReasoningAgents">Reasoning agents the run may have at once, where the file says.</param>
/// <param name="MaxParallelProcesses">Steps that run legs the run may have at once, where the file says.</param>
/// <param name="MaxOpenAgents">Agents with a worktree the run may have open at once, where the file says.</param>
/// <param name="MaxRetries">Retries an incomplete step is given, where the file says.</param>
/// <param name="MaxInstances">The most step instances a run of this workflow may have, where the file says.</param>
/// <param name="PushCheckpoints">Whether checkpoint commits are pushed as they are made.</param>
/// <param name="HandoffSeconds">How long a paused reasoning step is given to write its handoff.</param>
/// <param name="Budget">What a whole run's reasoning may cost, and the reserve that pauses the run first.</param>
/// <param name="Recommendations">The file holding the repository's bar for recommendations, relative to the workflow's directory.</param>
/// <param name="Advisor">The agent that recommends an answer to a question the engine raises itself.</param>
/// <param name="Reporter">The agent that words what a run is doing for a person who asks.</param>
/// <param name="Amend">Everything the workflow says about a request amended, or an answer changed, while a run works.</param>
/// <param name="Answers">The answers the configuration gives to question kinds, beforehand.</param>
/// <param name="Inputs">The values a run of this workflow is started with.</param>
/// <param name="Params">The workflow's own knobs, defaulted in the file and read as <c>param.&lt;name&gt;</c>.</param>
/// <param name="Outputs">What the whole workflow declares it produces.</param>
/// <param name="Steps">The flow's steps, in the order they are declared.</param>
/// <param name="Path">Where the definition was read from, quoted in every refusal about it.</param>
public sealed record WorkflowDefinition(
    string Name,
    string? Description,
    int? MaxReasoningAgents,
    int? MaxParallelProcesses,
    int? MaxOpenAgents,
    int? MaxRetries,
    int? MaxInstances,
    bool? PushCheckpoints,
    int? HandoffSeconds,
    FlowBudget? Budget,
    string? Recommendations,
    FlowAdvisor? Advisor,
    FlowReporter? Reporter,
    FlowAmend? Amend,
    IReadOnlyDictionary<string, FlowAnswer> Answers,
    IReadOnlyList<FlowInput> Inputs,
    IReadOnlyList<FlowParam> Params,
    IReadOnlyList<WorkflowOutput> Outputs,
    IReadOnlyList<WorkflowStep> Steps,
    string Path)
{
    /// <summary>The inputs, by name, as a prompt or a template reads them.</summary>
    public IReadOnlyDictionary<string, FlowInput> InputsByName
        => new Dictionary<string, FlowInput>(Inputs.Select(input => new KeyValuePair<string, FlowInput>(input.Name, input)), StringComparer.Ordinal);

    /// <summary>The params, by name, as a prompt or a template reads them.</summary>
    public IReadOnlyDictionary<string, FlowParam> ParamsByName
        => new Dictionary<string, FlowParam>(Params.Select(param => new KeyValuePair<string, FlowParam>(param.Name, param)), StringComparer.Ordinal);
}

/// <summary>What a whole run's reasoning may cost, and the reserve that pauses the run before it is spent.</summary>
/// <param name="Usd">What the run's reasoning may cost, in United States dollars.</param>
/// <param name="Tokens">What the run's reasoning may spend, in tokens.</param>
/// <param name="Minutes">How long the run's reasoning may run, in minutes.</param>
/// <param name="ReservePercent">The share of the budget left when the run pauses instead of spending it.</param>
public sealed record FlowBudget(double? Usd, long? Tokens, double? Minutes, int? ReservePercent);

/// <summary>The agent that recommends an answer to a question the engine raises itself.</summary>
/// <param name="Agent">The agent definition, as a file under the workflow's <c>agents</c> directory.</param>
/// <param name="Prompt">The prompt file the recommendation is asked with.</param>
/// <param name="Tier">A name for a class of model, resolved by the configuration's tiers.</param>
/// <param name="Model">The model outright, whose provider, key and client follow from it.</param>
/// <param name="Effort">The effort the model is asked for.</param>
/// <param name="Fallback">The models to go on with, in order, when the one serving declines or its account is at its limit.</param>
/// <param name="Provider">The provider, where the model alone does not say it.</param>
/// <param name="Client">The client, where the provider is reached two ways.</param>
/// <param name="Kinds">The question kinds this advisor is asked about; every one, where the file says nothing.</param>
public sealed record FlowAdvisor(
    string Agent,
    string Prompt,
    string? Tier,
    string? Model,
    string? Effort,
    IReadOnlyList<string> Fallback,
    string? Provider,
    string? Client,
    IReadOnlyList<string> Kinds);

/// <summary>The agent that words what a run is doing for a person who asks.</summary>
/// <param name="Agent">The agent definition, as a file under the workflow's <c>agents</c> directory.</param>
/// <param name="StatusPrompt">The prompt file a status question is answered with.</param>
/// <param name="AskPrompt">The prompt file a free question is answered with.</param>
/// <param name="Tier">A name for a class of model, resolved by the configuration's tiers.</param>
/// <param name="Model">The model outright, whose provider, key and client follow from it.</param>
/// <param name="Effort">The effort the model is asked for.</param>
/// <param name="Fallback">The models to go on with, in order, when the one serving declines or its account is at its limit.</param>
/// <param name="Provider">The provider, where the model alone does not say it.</param>
/// <param name="Client">The client, where the provider is reached two ways.</param>
public sealed record FlowReporter(
    string Agent,
    string StatusPrompt,
    string AskPrompt,
    string? Tier,
    string? Model,
    string? Effort,
    IReadOnlyList<string> Fallback,
    string? Provider,
    string? Client);

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
/// <param name="MaxAddedIterations">How many iterations a run at the end of its loop is given to carry an update.</param>
public sealed record FlowAmend(
    string? Input,
    int? ChangedAnswerMinutes,
    string? Before,
    string Step,
    string? Then,
    string Scope,
    string WhenAddScope,
    string WhenChangeScope,
    string WhenRemoveScope,
    int? MaxAddedIterations);

/// <summary>One answer a configuration gives a question kind beforehand, and how often it holds.</summary>
/// <param name="Answer">The answer, spelled as the question's options spell it.</param>
/// <param name="Times">How often the same answer is given where the question lets the same thing go on.</param>
/// <param name="Then">The step the run is sent to once the answer is given, where the question allows one.</param>
public sealed record FlowAnswer(string Answer, int? Times, string? Then);

/// <summary>One value a run of the workflow is started with.</summary>
/// <param name="Name">The input's name, as <c>--input name=value</c> spells it.</param>
/// <param name="Description">What the value is for, shown when it is missing.</param>
/// <param name="Kind"><c>text</c>, or <c>file</c>: a path inside the repository, which must be a file when the run starts.</param>
/// <param name="Default">What to use when nothing supplies it, or <see langword="null"/>; empty is the input not given, and is always taken.</param>
/// <param name="Required">Whether the run is refused when nothing supplies it and there is no default.</param>
/// <param name="Options">The values a <c>text</c> input takes; a run started with another is refused.</param>
public sealed record FlowInput(
    string Name,
    string? Description,
    string Kind,
    string? Default,
    bool Required,
    IReadOnlyList<string> Options)
{
    /// <summary>The kind a file that says nothing is read as.</summary>
    public const string TextKind = "text";

    /// <summary>The other kind: a path inside the repository, checked to be a file when the run starts.</summary>
    public const string FileKind = "file";

    /// <summary>Both kinds, in the order a refusal lists them.</summary>
    public static IReadOnlyList<string> Kinds { get; } = [TextKind, FileKind];
}

/// <summary>
/// One of a workflow's own knobs, defaulted in the file and read as <c>param.&lt;name&gt;</c> wherever a
/// prompt or a step's key is a template: the workflow's settings apart from what a person gives the run.
/// </summary>
/// <param name="Name">The param's name, as <c>param.&lt;name&gt;</c> reads it.</param>
/// <param name="Description">What the value is for.</param>
/// <param name="Default">What to use where a value is neither given nor defaulted.</param>
/// <param name="Required">Whether the run is refused when nothing supplies it and there is no default.</param>
public sealed record FlowParam(string Name, string? Description, string? Default, bool Required);

/// <summary>What a whole workflow declares it produces, so a later run or a person can be pointed at it.</summary>
/// <param name="Name">The output's name, as the workflow's report names it.</param>
/// <param name="Description">What the value holds.</param>
/// <param name="From">An expression naming the step and the member that hold it.</param>
public sealed record WorkflowOutput(string Name, string? Description, string From);

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

    /// <summary>What the step is for, shown by <c>validate-workflow</c>.</summary>
    public string? Description { get; init; }

    /// <summary>The step's type, checked against <see cref="WorkflowStepTypes"/>; absent on a group.</summary>
    public string? Type { get; init; }

    /// <summary>The group's own steps; a group has these and no <see cref="Type"/>.</summary>
    public IReadOnlyList<WorkflowStep> Steps { get; init; } = [];

    /// <summary>The steps this one waits for, by name, each declared before it among its siblings.</summary>
    public IReadOnlyList<string> DependsOn { get; init; } = [];

    /// <summary>An expression that says whether the step runs at all.</summary>
    public string? When { get; init; }

    /// <summary>What a failure of the step does: <c>stop</c> (the default), <c>pause</c> or <c>continue</c>.</summary>
    public string? OnFailure { get; init; }

    /// <summary>Retries the step is given where it ends incomplete, where the file says.</summary>
    public int? Retries { get; init; }

    /// <summary>Whether the step runs even where its dependencies did not reach it.</summary>
    public bool? Always { get; init; }

    /// <summary>Whether the step runs with nothing beside it.</summary>
    public bool? Alone { get; init; }

    /// <summary>Whether the step may be skipped, said so in the report, where its <c>when</c> does not hold.</summary>
    public bool? Skippable { get; init; }

    /// <summary>The answers this step gives to question kinds, beforehand.</summary>
    public IReadOnlyDictionary<string, FlowAnswer> Answers { get; init; } = NoAnswers;

    /// <summary>The ending of another step that raises this one: it is never started by the schedule.</summary>
    public FlowOn? On { get; init; }

    /// <summary>The step the run goes back to once this handler is done.</summary>
    public string? Then { get; init; }

    /// <summary>How often this handler may be raised; past it the run asks.</summary>
    public int? MaxVisits { get; init; }

    /// <summary>An expression yielding the work items a <c>forEach</c> group runs its steps for, once each.</summary>
    public string? ForEach { get; init; }

    /// <summary>A <c>repeat</c> group's bound: when its iterations end, and what is left.</summary>
    public FlowRepeat? Repeat { get; init; }

    /// <summary>The members a block gathers from its members' outputs into one list, named as its members read them.</summary>
    public IReadOnlyList<string> Gather { get; init; } = [];

    /// <summary>The agent definition a reason step runs, as a file under the workflow's <c>agents</c> directory.</summary>
    public string? Agent { get; init; }

    /// <summary>A name for a class of model, resolved by the configuration's tiers.</summary>
    public string? Tier { get; init; }

    /// <summary>The model outright, whose provider, key and client follow from it.</summary>
    public string? Model { get; init; }

    /// <summary>The effort the model is asked for.</summary>
    public string? Effort { get; init; }

    /// <summary>The models to go on with, in order, when the one serving declines or its account is at its limit.</summary>
    public IReadOnlyList<string> Fallback { get; init; } = [];

    /// <summary>The provider, where the model alone does not say it.</summary>
    public string? Provider { get; init; }

    /// <summary>The client, where the provider is reached two ways.</summary>
    public string? Client { get; init; }

    /// <summary>The prompt file the step is asked with, relative to the workflow's directory.</summary>
    public string? Prompt { get; init; }

    /// <summary>The prompt file a step that goes on with another's work is asked with, in place of <see cref="Prompt"/>.</summary>
    public string? ResumePrompt { get; init; }

    /// <summary>The JSON schema file the step's answer is held to, relative to the workflow's directory.</summary>
    public string? Outputs { get; init; }

    /// <summary>Whether the reason step writes the tree (<c>write</c>, in an agent's worktree) or only reads (<c>read</c>, the default).</summary>
    public string? Access { get; init; }

    /// <summary>The <c>create-agent</c> step whose agent's worktree the step works in.</summary>
    public string? In { get; init; }

    /// <summary>The reason step whose work this one goes on with.</summary>
    public string? Resume { get; init; }

    /// <summary>Whether the step is never given the author's work to go on with.</summary>
    public bool? Independent { get; init; }

    /// <summary>Restrictions the agent works under, beyond the workflow's own.</summary>
    public IReadOnlyList<string> Restrictions { get; init; } = [];

    /// <summary>The paths a writing reason step may write, written out or as an expression.</summary>
    public FlowList? Writes { get; init; }

    /// <summary>Whether the reason step may put its questions to a person; one that may not writes its assumptions down.</summary>
    public bool? Ask { get; init; }

    /// <summary>How often a step that asks may ask before it goes on with what it has.</summary>
    public int? AskRounds { get; init; }

    /// <summary>The legs a leg step runs, by name; every declared leg where the file says nothing.</summary>
    public IReadOnlyList<string> Legs { get; init; } = [];

    /// <summary>What a red verdict of a check does to the step: <c>red</c> or <c>never</c>.</summary>
    public string? FailOn { get; init; }

    /// <summary>The test filter a test step passes on, a template.</summary>
    public string? Filter { get; init; }

    /// <summary>The tests a test step leaves out, by name, each a template.</summary>
    public IReadOnlyList<string> Exclude { get; init; } = [];

    /// <summary>The labels a test step selects its tests by, each a template.</summary>
    public IReadOnlyList<string> Labels { get; init; } = [];

    /// <summary>Whether a test step skips the build and tests what is there.</summary>
    public bool? SkipBuild { get; init; }

    /// <summary>The predefined runner a <c>run</c> step runs.</summary>
    public string? Runner { get; init; }

    /// <summary>The values a <c>run</c> step hands its runner's inputs, each a template.</summary>
    public IReadOnlyDictionary<string, string> Inputs { get; init; } = NoInputValues;

    /// <summary>The arms a sweep runs, by id; every arm where the file says nothing.</summary>
    public IReadOnlyList<string> Arms { get; init; } = [];

    /// <summary>Whether a sweep drives only the arms with no recorded <c>passed</c> that still holds.</summary>
    public bool? Pending { get; init; }

    /// <summary>Whether a <c>create-agent</c> step makes its agent with nothing handed to it.</summary>
    public bool? Empty { get; init; }

    /// <summary>The <c>create-agent</c> step whose agent the fold family step works on.</summary>
    public string? Of { get; init; }

    /// <summary>Whether the step works on every live agent of the run but its own.</summary>
    public bool? Others { get; init; }

    /// <summary>What a fold or a deletion is allowed to carry, member for member the fold command's options.</summary>
    public FlowAllow? Allow { get; init; }

    /// <summary>The paths a fold may write, written out or as an expression.</summary>
    public FlowList? Within { get; init; }

    /// <summary>Whether a deletion discards what is not committed rather than folding it.</summary>
    public bool? DiscardUncommitted { get; init; }

    /// <summary>The paths a refresh weighs, written out or as an expression.</summary>
    public FlowList? Paths { get; init; }

    /// <summary>Whether a refresh weighs every path the main tree moved, in place of <see cref="Paths"/>.</summary>
    public bool? All { get; init; }

    /// <summary>The paths a refresh leaves as the agent changed them, written out or as an expression.</summary>
    public FlowList? Except { get; init; }

    /// <summary>The paths whose copy in the agent is not its work, written out or as an expression.</summary>
    public FlowList? Stale { get; init; }

    /// <summary>The paths both trees changed that a merge takes, written out or as an expression.</summary>
    public FlowList? Merge { get; init; }

    /// <summary>The paths a rebase is told were reconciled by hand: written out only, a person's answer.</summary>
    public IReadOnlyList<string> Settled { get; init; } = [];

    /// <summary>An expression yielding the list of work items a plan check checks.</summary>
    public string? Items { get; init; }

    /// <summary>An expression yielding the pieces of a plan update, checked with the items.</summary>
    public string? Scope { get; init; }

    /// <summary>
    /// The commit an anchor balance measures from, or the branch a pull request goes to - both a
    /// template, read by the step's own type.
    /// </summary>
    public string? Base { get; init; }

    /// <summary>The row ids an anchor balance is held to, written out or as an expression.</summary>
    public FlowList? Rows { get; init; }

    /// <summary>What an anchor citations check judges: <c>currentTree</c> (the default), <c>currentCommit</c> or <c>currentPullRequest</c>.</summary>
    public string? Subject { get; init; }

    /// <summary>The files an anchor citations check is held to, written out or as an expression.</summary>
    public FlowList? AnchorPaths { get; init; }

    /// <summary>The branch a CI read reads, a template.</summary>
    public string? Branch { get; init; }

    /// <summary>How many of a branch's runs a CI read looks at, where the file says.</summary>
    public int? Limit { get; init; }

    /// <summary>The commit whose checks a CI wait waits for, a template.</summary>
    public string? Commit { get; init; }

    /// <summary>How long a CI wait waits for the first check to appear.</summary>
    public int? AppearSeconds { get; init; }

    /// <summary>How long a CI wait waits, from its start, for every check to end.</summary>
    public int? WaitMinutes { get; init; }

    /// <summary>How often a CI wait reads the checks while it waits.</summary>
    public int? PollSeconds { get; init; }

    /// <summary>The checks a CI wait does not count, by name.</summary>
    public IReadOnlyList<string> Ignore { get; init; } = [];

    /// <summary>Minutes a short call of git or the forge is given, where the file says.</summary>
    public int? TimeoutMinutes { get; init; }

    /// <summary>The commit message a commit step writes, a template.</summary>
    public string? Message { get; init; }

    /// <summary>The values a commit message template reads, each a template.</summary>
    public IReadOnlyDictionary<string, string> Variables { get; init; } = NoInputValues;

    /// <summary>The remote a push pushes to, where the file says.</summary>
    public string? Remote { get; init; }

    /// <summary>Whether a push pushes a checkpoint commit.</summary>
    public bool? Checkpoint { get; init; }

    /// <summary>The pull request's title, a template.</summary>
    public string? Title { get; init; }

    /// <summary>The file holding the pull request's body, relative to the workflow's directory.</summary>
    public string? Body { get; init; }

    /// <summary>Whether the pull request is opened as a draft.</summary>
    public bool? Draft { get; init; }

    /// <summary>The question a pause step puts to a person, a template.</summary>
    public string? Question { get; init; }

    /// <summary>The answers a pause step offers.</summary>
    public IReadOnlyList<string> Options { get; init; } = [];

    /// <summary>The options that need a text with them.</summary>
    public IReadOnlyList<string> Text { get; init; } = [];

    /// <summary>Whether only a person may answer, and no configuration answers beforehand.</summary>
    public bool? Person { get; init; }

    /// <summary>An expression yielding a list of questions to show under the step's own.</summary>
    public string? Questions { get; init; }

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
public sealed record FlowRepeat(
    string Until,
    int? MaxIterations,
    int? Rounds,
    string? Blocking,
    int? BlockersLimitRounds,
    string? Leftover);

/// <summary>What a fold or a deletion is allowed to carry, member for member the fold command's options.</summary>
/// <param name="Settled">Paths reconciled by hand. A person's answer, written out only.</param>
/// <param name="New">The new rows named, of those the agent filed, written out or as an expression.</param>
/// <param name="AcceptLost">Cells of existing rows whose stored text the fold would not keep. A person's answer, written out only.</param>
/// <param name="Merge">Paths both trees changed whose clean three-way merge the fold takes, written out or as an expression.</param>
/// <param name="AcceptOver">Cells of rows an earlier fold applied that both the agent and the registries changed since. A person's answer, written out only.</param>
public sealed record FlowAllow(
    IReadOnlyList<string> Settled,
    FlowList? New,
    IReadOnlyList<string> AcceptLost,
    FlowList? Merge,
    IReadOnlyList<string> AcceptOver);

/// <summary>
/// One answer a person gave beforehand with the run's input, matched by an expression.
/// </summary>
/// <param name="When">The expression that says whether this answer is the person's.</param>
/// <param name="Answer">The answer it gives.</param>
/// <param name="Text">A text the answer carries, where the question's option takes one.</param>
/// <param name="Quote">Words looked for in the run's input before the answer counts, quoted in the report.</param>
public sealed record FlowPreAnswer(string When, string Answer, string? Text, FlowQuote? Quote);

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
public sealed record FlowList(IReadOnlyList<string> Written, string? Expression)
{
    /// <summary>Reads a scalar as an expression and a sequence as a written-out list.</summary>
    /// <param name="written">The list as written out.</param>
    /// <param name="expression">The expression, where the value is one.</param>
    public static FlowList Of(IReadOnlyList<string> written, string? expression) => new(written, expression);
}
