using DssHarness.Core.Configuration;

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
    public sealed record StepType(
        string Type,
        IReadOnlyList<KeyDescription> Keys,
        IReadOnlyList<string> TemplateKeys,
        string? FailOnDefault)
    {
        /// <summary>Whether the type checks something, so that its verdicts can be red.</summary>
        public bool IsCheck => FailOnDefault is not null;

        /// <summary>Which of the type's own keys the step must name: those <see cref="Keys"/> marks required.</summary>
        public IReadOnlyList<string> Required => [.. Keys.Where(key => key.Required).Select(key => key.Name)];
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

    /// <summary>The keys a group takes, beyond the common ones: it names no type.</summary>
    public static IReadOnlyList<KeyDescription> Group =>
    [
        new("steps", "the group's own steps") { Required = true },
        new("forEach", "an expression yielding the work items the group runs its steps for, once each"),
        new("repeat", "the group's bound: when its iterations end, and what is left") { Keys = RepeatKeys },
        new("gather", "members gathered from the group's steps into one list, named as its members read them"),
    ];

    /// <summary>Every type, in the order <c>help workflows</c> lists them.</summary>
    public static IReadOnlyList<StepType> All { get; } =
    [
        new("orient", [], [], null),
        new(
            "reason",
            [
                new("agent", "the agent definition, as agents/<name>.md in the workflow's directory; absent, the agent of the step it resumes"),
                new("tier", "a name for a class of model, resolved by the configuration's agent tiers"),
                new("model", "the model outright, whose provider, key and client follow from it"),
                new("effort", "the effort the model is asked for"),
                new("fallback", "models to go on with, in order, when the one serving declines or its account is at its limit"),
                new("provider", "the provider, where the model alone does not say it"),
                new("client", "the client, where the provider is reached two ways"),
                new("prompt", "the prompt file the step is asked with") { Required = true },
                new("resumePrompt", "the prompt file a step that goes on with its own work (resume names itself) is asked with when it runs again, in place of prompt"),
                new("outputs", "the JSON schema file the step's answer is held to") { Required = true },
                new("access", "read (the default), or write: in an agent's worktree, with 'in' naming it"),
                new("in", "the create-agent step whose agent's worktree the step works in; required where access is write"),
                new("resume", "the reason step whose work this one goes on with"),
                new("independent", "never given the author's work to go on with"),
                new("restrictions", "restrictions the agent works under, beyond the workflow's own"),
                new("writes", "the paths a writing step may write"),
                new("ask", "the step may put its questions to a person; one that may not writes its assumptions down"),
                new("askRounds", "how often a step that asks may ask before it goes on with what it has, from 1 to 5; absent, 2"),
                new("backoffSeconds", $"seconds the step waits before it is tried again after its agent failed, twice as long before each later try, at most {WorkflowStep.MostBackoffSeconds}; absent, the workflow's backoffSeconds"),
            ],
            [],
            null),
        new("build", LegKeys, [], "red"),
        new("test", [.. LegKeys, .. TestKeys], ["filter", "exclude", "labels"], "red"),
        new("run", [.. LegKeys, new("runner", "the predefined runner the step runs") { Required = true }, RunInputs], ["inputs"], "red"),
        new(
            "check-mutations",
            [.. LegKeys, new("arms", "the arms it runs, by id; absent, every arm"), new("pending", "drive only the arms with no recorded passed that still holds")],
            [],
            "red"),
        new(
            "create-agent",
            [new("agent", "the agent definition, as agents/<name>.md in the workflow's directory") { Required = true }, new("empty", "make the agent with nothing handed to it")],
            [],
            null),
        new("fold", FoldKeys, [], null),
        new(
            "delete-agent",
            [.. FoldKeys, new("discardUncommitted", "discard what is not committed rather than folding it; it then folds nothing, and takes no allow and no within")],
            [],
            null),
        new(
            "refresh",
            [
                new("of", "the create-agent step whose agent the step works on"),
                new("others", "a create-agent step: the step works on every other live agent of the run that has a worktree, each on its own"),
                new("paths", "the paths it weighs; absent, what its of and its keys say"),
                new("all", "weigh every path the main tree moved, in place of paths"),
                new("except", "paths the agent changed, left as it changed them"),
                new("stale", "paths whose copy in the agent is not its work, handed the main tree's copy"),
                new("merge", "paths both trees changed whose clean three-way merge it takes"),
            ],
            [],
            null),
        new(
            "rebase",
            [
                new("of", "the create-agent step whose agent the step works on"),
                new("others", "a create-agent step: the step works on every other live agent of the run that has a worktree, each on its own"),
                new("settled", "paths reconciled by hand: written out only, a person's answer"),
                new("merge", "paths both trees changed whose clean three-way merge it takes"),
            ],
            [],
            null),
        new(
            "validate-plan",
            [new("items", "an expression yielding the work items to check") { Required = true }, new("scope", "an expression yielding the pieces of a plan update, checked with them")],
            [],
            null),
        new(
            "anchor-balance",
            [new("base", "the commit it measures from"), new("rows", "the row ids it is held to; absent, the whole tree"), new("failOn", "what a red finding does: red (the default) or never")],
            ["base"],
            "red"),
        new(
            "anchor-citations",
            [new("subject", "what it judges: currentTree (the default), currentCommit or currentPullRequest"), new("paths", "the files it is held to; absent, the whole tree"), new("failOn", "what a red finding does: red (the default) or never")],
            [],
            "red"),
        new(
            "ci-read",
            [new("branch", "the branch it reads"), new("limit", "how many of the branch's runs it looks at"), new("timeoutMinutes", "minutes the read is given"), new("failOn", "what a red leg does: never (the default) or red")],
            ["branch"],
            "never"),
        new(
            "ci-wait",
            [
                new("commit", "the commit whose checks it waits for; absent, the commit this run last pushed"),
                new("appearSeconds", "how long it waits for the first check to appear"),
                new("waitMinutes", "how long it waits, from its start, for every check to end"),
                new("pollSeconds", "how often it reads the checks while it waits"),
                new("ignore", "checks it does not count, by name"),
                new("timeoutMinutes", "minutes one read is given"),
                new("failOn", "what a red check does: red (the default) or never"),
            ],
            ["commit"],
            "red"),
        new(
            "commit",
            [new("message", "the commit message it writes; required where the config declares no commit.template, and not used where it declares one"), new("variables", "the values the message template reads"), new("timeoutMinutes", "minutes git is given")],
            ["message", "variables"],
            null),
        new(
            "push",
            [new("remote", "the remote it pushes to; absent, the tracked one"), new("checkpoint", "push a checkpoint commit"), new("timeoutMinutes", "minutes git is given")],
            [],
            null),
        new(
            "pull-request",
            [
                new("title", "the pull request's title") { Required = true },
                new("body", "the file holding the pull request's body") { Required = true },
                new("base", "the branch it goes to; absent, the remote's default branch"),
                new("draft", "open it as a draft"),
                new("timeoutMinutes", "minutes the forge is given"),
            ],
            ["title", "base"],
            null),
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
            null),
    ];

    /// <summary>The type <paramref name="type"/> names, or <see langword="null"/> where none does.</summary>
    /// <param name="type">The value of a step's <c>type</c> key.</param>
    /// <remarks>
    /// Compared exactly, as an action's <c>uses</c> is: the file is tracked, so a spelling that only
    /// differs by case is a typo, and accepting it would make two files that read differently run
    /// the same step.
    /// </remarks>
    public static StepType? Find(string type) => All.FirstOrDefault(known => string.Equals(known.Type, type, StringComparison.Ordinal));

    /// <summary>The keys <paramref name="type"/>'s steps take, spelled as the file spells them.</summary>
    /// <param name="type">A step type.</param>
    public static IReadOnlyList<string> KeysOf(StepType type)
    {
        ArgumentNullException.ThrowIfNull(type);

        return KeyDescription.Names(type.Keys);
    }

    /// <summary>Every key any step type takes, flattened: the list a step's unknown keys are refused against.</summary>
    public static IReadOnlyList<string> AllKeys { get; } = [.. All.SelectMany(KeysOf)];

    /// <summary>The keys every leg step takes, shared by <c>build</c>, <c>test</c>, <c>run</c> and <c>check-mutations</c>.</summary>
    private static IReadOnlyList<KeyDescription> LegKeys =>
    [
        new("legs", "the legs it runs, by name; absent, every declared leg"),
        new("in", "the create-agent step whose agent's worktree it works in"),
        new("failOn", "what a red leg does: red (the default) or never"),
    ];

    /// <summary>The keys only a <c>test</c> takes, beyond the leg keys.</summary>
    private static IReadOnlyList<KeyDescription> TestKeys =>
    [
        new("filter", "the tests it runs, in the leg's runner's own filter"),
        new("exclude", "tests it leaves out, by name"),
        new("labels", "labels it selects its tests by"),
        new("skipBuild", "skip the build and test what is there"),
    ];

    /// <summary>The run step's <c>inputs</c> map.</summary>
    private static KeyDescription RunInputs => new("inputs", "the values it hands its runner's inputs");

    /// <summary>The keys the fold family takes, shared by <c>fold</c> and <c>delete-agent</c>.</summary>
    private static IReadOnlyList<KeyDescription> FoldKeys =>
    [
        new("of", "the create-agent step whose agent the step works on") { Required = true },
        new("allow", "what it may carry, member for member the fold command's options") { Keys = AllowKeys },
        new("within", "the paths it may write"),
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

    /// <summary>What one answer of an <c>answers</c> map takes.</summary>
    public static IReadOnlyList<KeyDescription> AnswerKeys =>
    [
        new("answer", "the answer, spelled as the question's options spell it") { Required = true },
        new("times", "how often more, retry or compact is given, from 1 to 20, before the question is asked of a person - or answered then"),
        new("then", "the answer given once times are spent, one that ends the matter: accept, allow, merge, stay, skip, fail or abort"),
    ];
}
