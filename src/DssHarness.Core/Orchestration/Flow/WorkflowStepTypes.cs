using DssHarness.Core.Configuration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The step types a workflow's <c>type</c> key names, and the keys each takes: the one owner of
/// required keys, allowed keys and which keys are templates, read by the parser, the validator and
/// <c>help workflows</c> alike.
/// </summary>
/// <remarks>
/// <para>
/// A key no table here lists is refused where the file is read, as an unknown key is; a key listed
/// for a type the step is not is refused with the type that takes it. Nothing reads a key that no
/// parser reads, and nothing documents a key that no file may hold.
/// </para>
/// <para>
/// Which of a type's string keys are templates - text handed on, to a command, to git, to the forge,
/// to a person, and so filled from the run's own names - is said here and nowhere else, by the rule
/// that a key whose value is text handed on is a template and a key that names something of the
/// definition (a step, an agent, a leg, a runner, a file, a remote) is not.
/// </para>
/// </remarks>
public static class WorkflowStepTypes
{
    /// <summary>One step type: its own keys, beyond the common ones, and its rules.</summary>
    /// <param name="Type">The type, as a step's <c>type</c> key spells it.</param>
    /// <param name="Keys">
    /// The keys this type takes beyond the common ones, each marked where the step must name it: the parser refuses a
    /// step without one, and <c>help workflows</c> says so, from this one list.
    /// </param>
    /// <param name="TemplateKeys">
    /// Which of the type's keys are templates, filled from the run's own names: a scalar key by its
    /// whole value, a list key by each member, a map key by each value.
    /// </param>
    /// <param name="FailOnDefault">
    /// The default of the type's <c>failOn</c> key - <c>red</c> or <c>never</c> - for the types that
    /// check something; <see langword="null"/> for the types that check nothing.
    /// </param>
    /// <param name="Calls">The runner of the type and the services it calls, as the plan's table names them: read by nothing at run time; a test pins each.</param>
    /// <param name="Claims">What a step of the type holds while it runs, as the plan's table names each.</param>
    /// <param name="CountsAgainst">The limit a step of the type counts against, or what its slot does; <see langword="null"/> where it counts against nothing.</param>
    /// <param name="Output">
    /// The type's built-in output schema, in the subset <see cref="Schemas.JsonSchemaSubset"/> reads, which a path into a
    /// step of the type is typed by when the definition is loaded; <see langword="null"/> where the type has none - a
    /// reason step's is the schema file it names, and a type the plan lists no output for has none, so a read of one is
    /// refused.
    /// </param>
    /// <param name="Asks">
    /// Whether a step of the type only asks a person and runs nothing: it takes none of <see cref="Running"/>, for there is
    /// nothing of its own to bound.
    /// </param>
    public sealed record StepType(
        string Type,
        IReadOnlyList<KeyDescription> Keys,
        IReadOnlyList<string> TemplateKeys,
        string? FailOnDefault,
        string Calls = "",
        IReadOnlyList<string>? Claims = null,
        string? CountsAgainst = null,
        string? Output = null,
        bool Asks = false)
    {
        /// <summary>Whether the type checks something, so that its verdicts can be red.</summary>
        public bool IsCheck => FailOnDefault is not null;

        /// <summary>Which of the type's own keys the step must name: those <see cref="Keys"/> marks required.</summary>
        public IReadOnlyList<string> Required => [.. Keys.Where(key => key.Required).Select(key => key.Name)];

        /// <summary>What a step of the type holds while it runs; empty where it holds nothing.</summary>
        public IReadOnlyList<string> ClaimsOf => Claims ?? [];

        /// <summary>
        /// What the type's <c>failOn</c> key takes, where it takes one: a check's <c>red</c> or <c>never</c>, unless the type
        /// says its own - a graph, which is no check, fails on what it could not read.
        /// </summary>
        public IReadOnlyList<string> FailOnValues { get; init; } = ["red", "never"];

        /// <summary>
        /// The time limit of a step of the type that says no <c>timeoutMinutes</c>, in minutes; <see langword="null"/> where
        /// such a step has none.
        /// </summary>
        public int? DefaultTimeoutMinutes { get; init; }
    }

    /// <summary>The keys every step takes, group or typed step alike.</summary>
    public static IReadOnlyList<KeyDescription> Common =>
    [
        new("name", "names the step; what dependsOn, on, resume and expressions read") { Required = true },
        new("description", "what the step is for"),
        new("type", "what runs: one of the types 'help workflows' lists; absent, the step is a group"),
        new("dependsOn", "steps this one waits for, by name, each declared before it among its siblings"),
        new("when", "an expression that says whether the step runs at all"),
        new("onFailure", "what a failure nothing handles does: stop, pause, continue, or recover - give it to the workflow's recover; absent, its group's, and at the top stop; never back to the step itself, which says retries"),
        new("retries", "retries the step is given where it ends incomplete - a reasoning step also where its agent failed, on each model it may use; absent, maxRetries"),
        new("always", "run even where the dependencies did not reach this step"),
        new("alone", "run with nothing beside it"),
        new("skippable", "a question about the step's failure offers skip, which ends it skipped and lets what depends on it go on"),
        new("answers", "answers this step gives to question kinds, beforehand") { Keys = AnswerKeys },
        new("on", "the ending of another step that raises this one; it is never started by the schedule") { Keys = OnKeys },
        new("then", "the step the run goes back to once this handler is done"),
        new("maxVisits", "how often this handler may be raised in one scope, from 1 to 20; absent, 3; past it the run asks"),
    ];

    /// <summary>
    /// The keys every step that runs takes, beyond the common ones: every typed step but one whose type only asks
    /// (<see cref="StepType.Asks"/>). A group runs nothing of its own, and takes none of them either.
    /// </summary>
    public static IReadOnlyList<KeyDescription> Running =>
    [
        new(
            "timeoutMinutes",
            $"the longest one attempt of the step may take, in whole minutes from 1 to {WorkflowStep.MostTimeoutMinutes.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}; absent, its type's own where the type says one beside its name, and none otherwise; how it is counted and how a step past it ends is the time limit's paragraph above"),
    ];

    /// <summary>
    /// The keys a group takes, beyond the common ones: it names no type. Its <c>steps</c> hold its own kind again, and say
    /// so as the configuration's key guard does (<see cref="ConfigKeys.TakesKeysOf"/>): what a step takes is listed once.
    /// </summary>
    public static IReadOnlyList<KeyDescription> Group =>
    [
        new("steps", $"the group's own steps; each {ConfigKeys.TakesKeysOf(typeof(WorkflowStep))}") { Required = true },
        new("forEach", "an expression yielding the work items the group runs its steps for, once each"),
        new("repeat", "the group's bound: when its iterations end, and what is left") { Keys = RepeatKeys },
        new("gather", "members gathered from the group's steps into one list, named as its members read them"),
    ];

    /// <summary>Every type, in the order <c>help workflows</c> lists them.</summary>
    public static IReadOnlyList<StepType> All { get; } =
    [
        new("orient", [], [], null, "OrientRunner: FlowCheckout over IGitClient and GitRemotes, LegSelection.Resolve, TestInvocationResolver, AnchorCitationService.RootsOf, CiSettings.Unset, MutationService.Declares, OrchestrationRules.OpenAgents, TreeHolders", ["read main"], null, Output: OrientOutput),
        new(
            "graphify-codebase",
            [
                new("in", "the create-agent step whose agent's worktree it syncs the view of, held against the base - the base synced first where the run has none; an agent that only reads has the base for its view"),
                new("rebuild", "extract every file of the tree it syncs again, reusing no extract"),
                new("failOn", "what a graph with files it could not read does: never (the default), the step ends done and lists them, or incomplete, it fails"),
            ],
            [],
            null,
            "GraphifyCodebaseRunner: ICodebaseGraphService.SyncAsync over IGitClient, CodeGraphExtractors and CodeGraphLinker, through OrchestrationStore",
            ["read main", "read the agent's tree where in names one"],
            null,
            GraphifyOutput)
        {
            FailOnValues = [GraphIncomplete, "never"],
            DefaultTimeoutMinutes = 30,
        },
        new(
            "reason",
            [
                new("agent", "the agent definition, as agents/<name>.md in the workflow's directory; absent, the agent of the step it resumes"),
                new("tier", "a name for a class of model: one of the configuration's tierConfig, its models in the order they are tried"),
                new("model", "the model outright, whose provider, key and client follow from it"),
                new("effort", "the effort the model is asked for: one level for every model the step may run on, or a map of all and models' codes giving each its own"),
                new("client", "the client, where the provider is reached two ways"),
                new("prompt", "the prompt file the step is asked with") { Required = true },
                new("resumePrompt", "the prompt file a step that goes on with its own work (resume names itself) is asked with when it runs again, in place of prompt"),
                new("outputs", "the JSON schema file the step's answer is held to") { Required = true },
                new("access", "read (the default), or write: in an agent's worktree, with 'in' naming it"),
                new("in", "the create-agent step whose agent's worktree the step works in, which it waits for through dependsOn; required where access is write"),
                new("resume", "the reason step whose work this one goes on with"),
                new("independent", "never given the author's work to go on with"),
                new("restrictions", "restrictions the agent works under, beyond the workflow's own"),
                new("writes", "the paths a writing step may write"),
                new("ask", "the step may put its questions to a person; one that may not writes its assumptions down"),
                new("askRounds", "how often a step that asks may ask before it goes on with what it has, from 1 to 5; absent, 2"),
                new("backoffSeconds", $"seconds the step waits before it is tried again after its agent failed, twice as long before each later try, at most {WorkflowStep.MostBackoffSeconds}; absent, the workflow's backoffSeconds"),
                new("onPreExisting", $"the step of the flow's top level that triages each issue this step reports as there before the run; one that says raised: {WorkflowStep.RaisedByPreExisting}"),
                new("raised", $"{WorkflowStep.RaisedByPreExisting}: the step is raised once for each issue a step reports as there before the run, and never scheduled"),
                new("onAddress", $"{WorkflowStep.Recover}: an issue the step decides to fix is given to the workflow's recover, which fixes it at its root; required beside raised"),
            ],
            [],
            null,
            "IAgentClient; a read-only one that begins work first makes its agent (IAgentService.CreateAsync, no worktree)",
            ["read main", "write the agent's tree where its access is write"],
            "maxReasoningAgents"),
        new("build", LegKeys, [], "red", "BuildStepRunner: IBuildLegs over LegRunService", ["run(tree)"], "maxParallelProcesses", LegsOutput(arms: false)),
        new("test", [.. LegKeys, .. TestKeys], ["filter", "exclude", "labels"], "red", "TestStepRunner: ITestLegs over LegRunService", ["run(tree)"], "maxParallelProcesses", LegsOutput(arms: false)),
        new("run", [.. LegKeys, new("runner", "the predefined runner the step runs") { Required = true }, RunInputs], ["inputs"], "red", "RunStepRunner: IRunnerLegs over LegRunService, HarnessConfig.WritingRunners", ["run(tree)", "write(tree) where its runner says writesTree"], "maxParallelProcesses", LegsOutput(arms: false)),
        new(
            "check-mutations",
            [.. LegKeys, new("arms", "the arms it runs, by id; absent, every arm"), new("pending", "drive only the arms with no recorded passed that still holds")],
            [],
            "red",
            "SweepStepRunner: IMutationLegs through MutationService, over LegRunService",
            ["run(tree)"],
            "maxParallelProcesses",
            LegsOutput(arms: true)),
        new(
            "create-agent",
            [new("agent", "the agent definition, as agents/<name>.md in the workflow's directory") { Required = true }, new("empty", "make the agent with nothing handed to it")],
            [],
            null,
            "CreateAgentRunner: IAgentService.CreateAsync, SeedAsync and DeleteAsync",
            ["write main"],
            "an open-agent slot"),
        new("fold", FoldKeys, [], null, "FoldStepRunner: IAgentService.FoldAsync held to its dry run, FoldLedger, FoldWithin", ["write main", "write the agent's tree"], null, FoldOutput),
        new(
            "delete-agent",
            [.. FoldKeys, new("discardUncommitted", "discard what is not committed rather than folding it; it then folds nothing, and takes no allow and no within")],
            [],
            null,
            "DeleteAgentStepRunner: IAgentService.DeleteAsync held to its dry run, FoldLedger, FoldWithin",
            ["write main", "write the agent's tree"],
            "releases the open-agent slot",
            FoldOutput),
        new(
            "refresh",
            [
                new("of", "the create-agent step whose agent the step works on, which it waits for through dependsOn"),
                new("others", "a create-agent step: the step works on every other live agent of the run that has a worktree, each on its own"),
                new("paths", "the paths it weighs; absent, what its of and its keys say"),
                new("all", "weigh every path the main tree moved, in place of paths"),
                new("except", "paths the agent changed, left as it changed them"),
                new("stale", "paths whose copy in the agent is not its work, handed the main tree's copy"),
                new("merge", "paths both trees changed whose clean three-way merge it takes"),
            ],
            [],
            null,
            "RefreshStepRunner: IAgentService.RefreshAsync",
            ["write main", "write its target"],
            null),
        new(
            "rebase",
            [
                new("of", "the create-agent step whose agent the step works on, which it waits for through dependsOn"),
                new("others", "a create-agent step: the step works on every other live agent of the run that has a worktree, each on its own"),
                new("settled", "paths reconciled by hand: written out only, a person's answer"),
                new("merge", "paths both trees changed whose clean three-way merge it takes"),
            ],
            [],
            null,
            "RebaseStepRunner: IAgentService.RebaseAsync",
            ["write its target"],
            null),
        new(
            "validate-plan",
            [new("items", "an expression yielding the work items to check") { Required = true }, new("scope", "an expression yielding the pieces of a plan update, checked with them")],
            [],
            null,
            "PlanCheckRunner: PlanValidator with FlowRunPaths over IPathBudget.Check and CopyPathBudget",
            ["read main"],
            null,
            ObjectOf(("ok", Flag), ("problems", ListOf(Text)), ("items", UntypedList))),
        new(
            "anchor-balance",
            [new("base", "the commit it measures from"), new("rows", "the row ids it is held to; absent, the whole tree"), new("failOn", "what a red finding does: red (the default) or never")],
            ["base"],
            "red",
            "AnchorBalanceStepRunner: IAnchorBalanceService.CheckAsync, AnchorBalanceReport.HeldTo, AnchorReports.BalanceFindings",
            ["read main"],
            null,
            AnchorOutput),
        new(
            "anchor-citations",
            [new("subject", "what it judges: currentTree (the default), currentCommit or currentPullRequest"), new("paths", "the files it is held to; absent, the whole tree"), new("failOn", "what a red finding does: red (the default) or never")],
            [],
            "red",
            "AnchorCitationsStepRunner: IAnchorCitationService.CheckAsync, AnchorCitationReport.HeldTo, AnchorCitationReports.Line",
            ["read main"],
            null,
            AnchorOutput),
        new(
            "ci-read",
            [new("branch", "the branch it reads"), new("limit", "how many of the branch's runs it looks at"), new("failOn", "what a red leg does: never (the default) or red")],
            ["branch"],
            "never",
            "CiReadStepRunner: ICiLegsService.CheckAsync over GhCiJobSource and GhProcess",
            [],
            null,
            ObjectOf(("passed", Flag), ("branch", Text), ("matrixRan", Flag), ("legs", UntypedList), ("red", UntypedList))),
        new(
            "ci-wait",
            [
                new("commit", "the commit whose checks it waits for; absent, the commit this run last pushed"),
                new("appearSeconds", "how long it waits for the first check to appear"),
                new("waitMinutes", $"how long it waits, from its start, for every check to end; absent, {WorkflowStep.DefaultWaitMinutes}"),
                new("pollSeconds", "how often it reads the checks while it waits"),
                new("ignore", "checks it does not count, by name: a job's own, as its workflow names it, or a status's context"),
               new("failOn", "what a red check does: red (the default) or never"),
            ],
            ["commit"],
            "red",
            "CiWaitStepRunner: ICiChecksService over GhCiJobSource and GhProcess, ICiLegsService.ClassifyRunAsync",
            [],
            null,
            CiWaitOutput),
        new(
            "commit",
            [new("message", "the commit message it writes; required where the config declares no commit.template, and not used where it declares one"), new("variables", "the values the message template reads")],
            ["message", "variables"],
            null,
            "IGitPublisher",
            ["write main"],
            null,
            ObjectOf(("committed", Flag), ("sha", TextOrNull), ("paths", ListOf(Text)))),
        new(
            "push",
            [new("remote", "the remote it pushes to; absent, the tracked one"), new("checkpoint", "push a checkpoint commit")],
            [],
            null,
            "IGitPublisher",
            ["the remote resource"],
            null,
            ObjectOf(("pushed", Flag), ("remote", Text), ("branch", Text), ("sha", Text))),
        new(
            "pull-request",
            [
                new("title", "the pull request's title") { Required = true },
                new("body", "the file holding the pull request's body") { Required = true },
                new("base", "the branch it goes to; absent, the remote's default branch"),
                new("draft", "open it as a draft"),
           ],
            ["title", "base"],
            null,
            "IGitPublisher",
            ["the remote resource"],
            null,
            ObjectOf(("number", WholeNumber), ("url", Text), ("created", Flag))),
        new(
            "pause",
            [
                new("question", "the question it puts to a person") { Required = true },
                new("options", "the answers it offers"),
                new("text", "the options that need a text with them"),
                new("person", "only a person may answer, and no configuration answers beforehand"),
                new("questions", "an expression yielding a list of questions to show under the step's own"),
                new("preAnswer", "answers the person gave beforehand with the run's input") { Keys = PreAnswerKeys },
            ],
            ["question"],
            null,
            "the engine itself",
            [],
            null,
            ObjectOf(("answer", Text), ("text", Text)),
            Asks: true),
    ];

    /// <summary>
    /// The keys of a step whose value is an expression, by their path in the step - a <c>repeat</c>'s and a
    /// pre-answer's under theirs: read by the grammar, never filled as a template.
    /// </summary>
    public static IReadOnlyList<string> ExpressionKeys { get; } =
    [
        "when", "filterWhen", "forEach", "repeat.until", "repeat.blocking", "repeat.leftover", "items", "scope", "questions",
        "preAnswer.when", "preAnswer.quote.text",
    ];

    /// <summary>
    /// The keys of a step that take a list written out, or an expression that yields one: an expression where the value
    /// is a single one, and a list of names where it is a list.
    /// </summary>
    public static IReadOnlyList<string> ListOrExpressionKeys { get; } =
        ["writes", "within", "paths", "except", "stale", "merge", "rows", "allow.new", "allow.merge"];

    /// <summary>The <c>failOn</c> a <c>graphify-codebase</c> step fails under where its graph has files it could not read.</summary>
    public const string GraphIncomplete = "incomplete";

    /// <summary>
    /// The time limit of one attempt of <paramref name="step"/>, in minutes: its own <c>timeoutMinutes</c>, else its type's
    /// (<see cref="StepType.DefaultTimeoutMinutes"/>); <see langword="null"/> where it has none.
    /// </summary>
    /// <param name="step">A step of a workflow.</param>
    public static int? TimeLimitOf(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.TimeoutMinutes ?? (step.Type is { } type ? Find(type)?.DefaultTimeoutMinutes : null);
    }

    /// <summary>The type <paramref name="type"/> names, or <see langword="null"/> where none does.</summary>
    /// <param name="type">The value of a step's <c>type</c> key.</param>
    /// <remarks>
    /// Compared exactly, as an action's <c>uses</c> is: the file is tracked, so a spelling that only
    /// differs by case is a typo, and accepting it would make two files that read differently run
    /// the same step.
    /// </remarks>
    public static StepType? Find(string type) => All.FirstOrDefault(known => string.Equals(known.Type, type, StringComparison.Ordinal));

    /// <summary>
    /// Whether <paramref name="step"/> folds an agent's work into the main tree, as written: a <c>fold</c>, and a
    /// <c>delete-agent</c> that does not discard it. The one rule the definition's check asks a fold's questions of it by
    /// and its runner folds through the run's ledger by; the runner leaves out, besides, a deletion of an agent whose
    /// deletion already began, which folds nothing more.
    /// </summary>
    /// <param name="step">A step of a workflow.</param>
    public static bool Folds(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step.Type == "fold" || (step.Type == "delete-agent" && step.DiscardUncommitted is not true);
    }

    /// <summary>
    /// Whether <paramref name="step"/> makes an agent when it runs, which the run names itself
    /// (<see cref="OrchestrationRules.GeneratedAgentPrefix"/> and a number): a create-agent step, and a reason step that
    /// reads and begins work of its own. A reason step that writes works in the agent its <c>in</c> names, and one that
    /// goes on with another's work in that step's agent.
    /// </summary>
    /// <param name="step">A step of a workflow.</param>
    public static bool MakesAgent(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Type == "create-agent"
            || (step.Type == "reason"
                && step.Access != "write"
                && (step.Resume is null || string.Equals(step.Resume, step.Name, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The keys <paramref name="type"/>'s steps take beyond the common ones, spelled as the file spells them: its own, and
    /// <see cref="Running"/>'s where it runs.
    /// </summary>
    /// <param name="type">A step type.</param>
    public static IReadOnlyList<string> KeysOf(StepType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return type.Asks ? KeyDescription.Names(type.Keys) : [.. KeyDescription.Names(type.Keys), .. KeyDescription.Names(Running)];
    }

    /// <summary>Every key any step type takes, flattened and each once: the list a step's unknown keys are refused against.</summary>
    public static IReadOnlyList<string> AllKeys { get; } = [.. All.SelectMany(KeysOf).Distinct(StringComparer.Ordinal)];

    /// <summary>The keys every leg step takes, shared by <c>build</c>, <c>test</c>, <c>run</c> and <c>check-mutations</c>.</summary>
    private static IReadOnlyList<KeyDescription> LegKeys =>
    [
        new("legs", "the legs or leg sets it runs, by name; absent, every declared leg"),
        new("in", "the create-agent step whose agent's worktree it works in, which it waits for through dependsOn"),
        new("failOn", "what a red leg does: red (the default) or never"),
    ];

    /// <summary>The keys only a <c>test</c> takes, beyond the leg keys.</summary>
    private static IReadOnlyList<KeyDescription> TestKeys =>
    [
        new("filter", "the tests it runs, in the leg's runner's own filter"),
        new("filterWhen", "an expression that says whether the filter is given: where it reads false, every test of the step's legs runs; absent, the filter is given"),
        new("exclude", "tests it leaves out, by name"),
        new("labels", "labels it selects its tests by"),
        new("skipBuild", "skip the build and test what is there"),
    ];

    /// <summary>The run step's <c>inputs</c> map.</summary>
    private static KeyDescription RunInputs => new("inputs", "the values it hands its runner's inputs");

    /// <summary>The keys the fold family takes, shared by <c>fold</c> and <c>delete-agent</c>.</summary>
    private static IReadOnlyList<KeyDescription> FoldKeys =>
    [
        new("of", "the create-agent step whose agent the step works on, which it waits for through dependsOn") { Required = true },
        new("allow", "what it may carry, member for member the fold command's options") { Keys = AllowKeys },
        new("within", "the paths, as patterns, its fold may write or delete in the main tree; a path outside them asks foldOutside, and the registries its rows change are never held to it"),
    ];

    /// <summary>What one <c>on</c> block takes.</summary>
    public static IReadOnlyList<KeyDescription> OnKeys =>
    [
        new("step", "the step whose ending raises this one") { Required = true },
        new("outcome", "the ending that raises it: failure, success or end") { Required = true },
        new("codes", "the exit codes a failure answers to; absent, every one"),
    ];

    /// <summary>What one <c>repeat</c> block takes.</summary>
    public static IReadOnlyList<KeyDescription> RepeatKeys =>
    [
        new("until", "the expression every iteration is followed by while it is false") { Required = true },
        new("maxIterations", "the most iterations, however until reads, from 1 to 20"),
        new("rounds", "the rounds in which every false until is followed, from 1 to 20; given with blocking, not with maxIterations"),
        new("blocking", "the expression that says, past the rounds, whether what is left must still be dealt with"),
        new("blockersLimitRounds", "further rounds past rounds while blocking holds, from 1 to 20 (absent, 3); false, none but the workflow's budget"),
        new("leftover", "where what the loop left is written"),
    ];

    /// <summary>What one <c>allow</c> block takes.</summary>
    public static IReadOnlyList<KeyDescription> AllowKeys =>
    [
        new("settled", "paths reconciled by hand: written out only, a person's answer"),
        new("new", "the new rows named, of those the agent filed"),
        new("acceptLost", "cells whose stored text the fold would not keep, '<id>:<cell>': written out only, a person's answer"),
        new("merge", "paths both trees changed whose clean three-way merge it takes"),
        new("acceptOver", "cells both the agent and the registries changed since the last fold, '<id>:<cell>': written out only, a person's answer"),
    ];

    /// <summary>What one <c>preAnswer</c> entry takes.</summary>
    public static IReadOnlyList<KeyDescription> PreAnswerKeys =>
    [
        new("when", "the expression that says whether this answer is the person's") { Required = true },
        new("answer", "the answer it gives") { Required = true },
        new("text", "a text the answer carries, where the question's option takes one"),
        new("quote", "words looked for in the run's input before the answer counts") { Keys = QuoteKeys },
    ];

    /// <summary>What one <c>quote</c> block takes.</summary>
    public static IReadOnlyList<KeyDescription> QuoteKeys =>
    [
        new("text", "an expression yielding the words to look for") { Required = true },
        new("input", "the input to look in, one the workflow declares") { Required = true },
    ];

    /// <summary>
    /// What an <c>orient</c> step answers: the run's branch and the one its pull request goes to, the commit it started
    /// on, and what the repository has - a remote, legs and how each tests, anchors, CI, mutation arms - with what is
    /// dirty, which agents are open and what is in flight.
    /// </summary>
    private static string OrientOutput => ObjectOf(
        ("branch", Text),
        ("baseBranch", TextOrNull),
        ("startCommit", Text),
        ("hasRemote", Flag),
        ("hasLegs", Flag),
        ("legCount", WholeNumber),
        ("anchors", Flag),
        ("ciConfigured", Flag),
        ("hasMutations", Flag),
        ("tests", ListOf(ObjectOf(("name", Text), ("program", Text), ("filterArg", TextOrNull), ("exclude", ListOf(Text)), ("remoteExcludes", ListOf(Text))))),
        ("dirtyPaths", ListOf(Text)),
        ("openAgents", UntypedList),
        ("inFlight", UntypedList));

    /// <summary>
    /// What a step that runs legs answers - <c>build</c>, <c>test</c>, <c>run</c> and, naming each red entry's
    /// <paramref name="arms"/>, <c>check-mutations</c>: whether it passed, the command's exit code, each leg's verdict,
    /// the red legs alone with the tail of the phase that did not pass, and where the run's records are.
    /// </summary>
    private static string LegsOutput(bool arms) => ObjectOf(
        ("passed", Flag),
        ("exitCode", WholeNumber),
        ("verdicts", ListOf(ObjectOf(("leg", Text), ("verdict", Text)))),
        ("red", ListOf(ObjectOf([("leg", Text), ("verdict", Text), ("phase", TextOrNull), ("detail", Text), ("tail", ListOf(Text)), .. arms ? [("arm", TextOrNull)] : Array.Empty<(string, string)>()]))),
        ("runDirectory", TextOrNull));

    /// <summary>What a fold and a deletion answer: the paths written and deleted, the registries changed, and the ids of the rows applied.</summary>
    private static string FoldOutput => ObjectOf(("written", ListOf(Text)), ("deleted", ListOf(Text)), ("registries", ListOf(Text)), ("rows", ListOf(Text)));

    /// <summary>
    /// What a sync of the codebase graph answers: the graph's directory, the view it synced and the commit and dirtiness
    /// of its tree, how many files it keeps, read, reused and removed, the nodes and edges, each implementation's part,
    /// every file it holds no declaration of with its state and why, and how long it took.
    /// </summary>
    private static string GraphifyOutput => ObjectOf(
        ("graph", Text),
        ("view", Text),
        ("commit", TextOrNull),
        ("dirty", Flag),
        ("files", WholeNumber),
        ("extracted", WholeNumber),
        ("reused", WholeNumber),
        ("removed", WholeNumber),
        ("nodes", WholeNumber),
        ("edges", WholeNumber),
        ("languages", ListOf(ObjectOf(("language", Text), ("files", WholeNumber), ("nodes", WholeNumber)))),
        ("skipped", ListOf(ObjectOf(("path", Text), ("state", Text), ("why", TextOrNull)))),
        ("seconds", Number));

    /// <summary>What an anchor check answers: whether it passed, and each finding as a line the check prints.</summary>
    private static string AnchorOutput => ObjectOf(("passed", Flag), ("findings", ListOf(Text)));

    /// <summary>What a wait for the checks answers: whether they passed, the commit, each check, the red ones with their logs, and those still pending or unsettled.</summary>
    private static string CiWaitOutput => ObjectOf(
        ("passed", Flag),
        ("commit", Text),
        ("checks", ListOf(ObjectOf(("name", Text), ("state", Text), ("conclusion", TextOrNull), ("url", Text)))),
        ("red", ListOf(ObjectOf(("name", Text), ("job", TextOrNull), ("workflow", TextOrNull), ("step", TextOrNull), ("leg", TextOrNull), ("overran", Flag), ("url", Text), ("tail", ListOf(Text)), ("log", TextOrNull), ("conclusion", TextOrNull), ("description", TextOrNull), ("unread", TextOrNull)))),
        ("pending", UntypedList),
        ("unsettled", UntypedList));

    /// <summary>A schema of a text.</summary>
    private static string Text => """{"type":"string"}""";

    /// <summary>A schema of a text, or null where there is none.</summary>
    private static string TextOrNull => """{"type":["string","null"]}""";

    /// <summary>A schema of a boolean.</summary>
    private static string Flag => """{"type":"boolean"}""";

    /// <summary>A schema of a whole number.</summary>
    private static string WholeNumber => """{"type":"integer"}""";

    /// <summary>A schema of a number, whole or not.</summary>
    private static string Number => """{"type":"number"}""";

    /// <summary>A schema of a list whose items nothing types.</summary>
    private static string UntypedList => """{"type":"array"}""";

    /// <summary>A schema of a list whose items are each <paramref name="items"/>.</summary>
    private static string ListOf(string items) => $$"""{"type":"array","items":{{items}}}""";

    /// <summary>A schema of an object holding exactly <paramref name="properties"/>, each named and none other, as the subset closes every object.</summary>
    private static string ObjectOf(params (string Name, string Schema)[] properties)
        => $$$"""{"type":"object","additionalProperties":false,"properties":{{{{string.Join(",", properties.Select(property => $"\"{property.Name}\":{property.Schema}"))}}}}}""";

    /// <summary>What one answer of an <c>answers</c> map takes.</summary>
    public static IReadOnlyList<KeyDescription> AnswerKeys =>
    [
        new("answer", "the answer, spelled as the question's options spell it") { Required = true },
        new("times", "how often more, retry or compact is given, from 1 to 20, before the question is asked of a person - or answered then"),
        new("then", "the answer given once times are spent, one that ends the matter: accept, allow, merge, stay, skip, fail or abort"),
    ];
}
