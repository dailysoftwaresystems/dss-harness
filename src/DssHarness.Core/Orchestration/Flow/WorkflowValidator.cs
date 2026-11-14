using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Anchors;
using DssHarness.Core.Repository;
using DssHarness.Core.Schemas;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The structural rules a workflow's definition must hold, checked on the parsed definition alone:
/// that every name a step, an expression or a template reads is declared, and that every key means
/// what its step can do with it.
/// </summary>
/// <remarks>
/// <para>
/// Run by <c>validate-workflow</c> on a repository's definition, and on the definition this build ships by
/// <see cref="WorkflowTemplates.ShippedDefinition"/>, which <c>export-workflow --shipped</c> reads it through; the engine
/// will run it at the start of a run. Never run by the configuration reader, which reads no files. Every problem is
/// collected and reported together, because a definition usually goes wrong in the same way several times over.
/// </para>
/// <para>
/// What this checks is what a definition <em>says</em>: that a step it names exists, that an input
/// it reads is declared, that a group's bounds are whole. What a definition <em>does</em> - whether
/// an expression is true, whether a step's answer holds to its schema - is the engine's, when it
/// runs; and that a file the definition names is there is the validation service's, which reads
/// files.
/// </para>
/// </remarks>
public static class WorkflowValidator
{
    /// <summary>The question kinds there are, and how each is spelled.</summary>
    public static readonly IReadOnlyList<string> QuestionKinds =
    [
        "loopBound", "visitBound", "recoveryBound", "openAgents",
        "foldNewRows", "foldLostRows", "foldOverCells", "foldTwoChanges", "foldConflicts", "foldOutside",
        "turnLimit", "truncated", "stepBudget", "contextLimit", "runBudget",
        "noChecks", "checksUnsettled", "checksPending",
        "failure", "pause", "ask",
    ];

    /// <summary>
    /// The answers each kind offers, which a configuration may give it beforehand; a kind absent here takes no
    /// configured answer at all - a person's (<c>foldLostRows</c>, <c>foldOverCells</c>), or one its own key
    /// answers (<c>failure</c> by <c>onFailure</c>, <c>ask</c> by <c>ask</c>) - and <c>pause</c> offers the step's
    /// own options.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AnswersOf = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        ["loopBound"] = ["more", "accept", "abort"],
        ["visitBound"] = ["more", "accept", "fail", "abort"],
        ["recoveryBound"] = ["more", "direct", "skip", "abort"],
        ["openAgents"] = ["retry", "stay", "abort"],
        ["foldNewRows"] = ["allow", "retry", "fail", "abort"],
        ["foldTwoChanges"] = ["merge", "retry", "fail", "abort"],
        ["foldConflicts"] = ["retry", "fail", "abort"],
        ["foldOutside"] = ["allow", "retry", "fail", "abort"],
        ["turnLimit"] = ["retry", "more", "skip", "fail", "abort"],
        ["stepBudget"] = ["retry", "more", "skip", "fail", "abort"],
        ["truncated"] = ["retry", "more", "skip", "fail", "abort"],
        ["contextLimit"] = ["retry", "compact", "skip", "fail", "abort"],
        ["runBudget"] = ["more", "abort"],
        ["noChecks"] = ["retry", "accept", "fail", "abort"],
        ["checksUnsettled"] = ["retry", "accept", "fail", "abort"],
        ["checksPending"] = ["more", "accept", "fail", "abort"],
    };

    /// <summary>
    /// The answers after which the same question can come again, and so are given a counted number of times. The
    /// engine and this check read the one list: a <c>times</c> a configuration gives is counted by the engine that
    /// gives the answer.
    /// </summary>
    public static readonly IReadOnlyList<string> GoesOn = ["more", "retry", "compact"];

    /// <summary>The answers that end the matter, which alone may follow a counted one as its <c>then</c>.</summary>
    private static readonly IReadOnlyList<string> Ends = ["accept", "allow", "merge", "stay", "skip", "fail", "abort"];

    /// <summary>
    /// Which step raises each kind a configuration may answer, by its type or its keys - given the step and the
    /// <c>onFailure</c> that decides for it, its own or the nearest group's - as the table of question kinds says when
    /// each is raised. An answer given beforehand is to a kind its step, or on a group or the workflow some step in it,
    /// raises: one to a kind nothing raises would be read by nothing. <c>runBudget</c> is the run's own, raised by its
    /// budget and answered by the workflow alone.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, Func<WorkflowStep, string?, bool>> RaisedBy = new Dictionary<string, Func<WorkflowStep, string?, bool>>(StringComparer.Ordinal)
    {
        // A repeat at its bound with its until still false.
        ["loopBound"] = (step, _) => step.Repeat is not null,

        // A handler raised once more than its maxVisits.
        ["visitBound"] = (step, _) => step.On is not null,

        // The recovery taking a step's failure once more than its maxVisits.
        ["recoveryBound"] = (_, onFailure) => onFailure == WorkflowStep.Recover,

        // A redirect leaving a group while an agent made in it still has its worktree.
        ["openAgents"] = (step, _) => step.Then is not null,

        // A fold's, or a deletion's that folds, and those a refresh and a rebase share with it.
        ["foldNewRows"] = (step, _) => WorkflowStepTypes.Folds(step),
        ["foldOutside"] = (step, _) => WorkflowStepTypes.Folds(step),
        ["foldTwoChanges"] = (step, _) => WorkflowStepTypes.Folds(step) || step.Type is "refresh" or "rebase",
        ["foldConflicts"] = (step, _) => WorkflowStepTypes.Folds(step) || step.Type is "refresh" or "rebase",

        // A reasoning step's conversation at a limit of its own.
        ["turnLimit"] = (step, _) => step.Type == "reason",
        ["stepBudget"] = (step, _) => step.Type == "reason",
        ["truncated"] = (step, _) => step.Type == "reason",
        ["contextLimit"] = (step, _) => step.Type == "reason",

        // A wait for the checks that found none, found them still running, or found one neither green nor red.
        ["noChecks"] = (step, _) => step.Type == "ci-wait",
        ["checksPending"] = (step, _) => step.Type == "ci-wait",
        ["checksUnsettled"] = (step, _) => step.Type == "ci-wait",
    };

    /// <summary>V26: the most steps one workflow may declare, the recovery's among them, for they run. How deep a flow nests is <see cref="WorkflowFileParser.MaxDepth"/>'s own bound.</summary>
    internal const int MostSteps = 200;

    /// <summary>Returns every problem found, in the order the definition declares what they concern.</summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    /// <param name="hasCommitTemplate">
    /// Whether the configuration the definition is checked under declares a <c>commit.template</c>: where it does, the
    /// template writes a commit's message and a step's own <c>message</c> is not used; where it does not, every commit
    /// step names its message. Every caller says which configuration it checks under, so none is checked under one it
    /// did not choose.
    /// </param>
    public static IReadOnlyList<string> Validate(WorkflowDefinition definition, bool hasCommitTemplate)
        => Validate(definition, hasCommitTemplate, files: null);

    /// <summary>
    /// Returns every problem found, in the order the definition declares what they concern, typing a reason step's
    /// output by the schema file it names and checking each prompt's placeholders against what its step can read.
    /// </summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    /// <param name="hasCommitTemplate">Whether the configuration the definition is checked under declares a <c>commit.template</c>.</param>
    /// <param name="files">
    /// The text of a file the definition names, by the path it names it with and the workflow whose directory it is
    /// read in - the run's own where the second is <see langword="null"/>, the called workflow a step of a used one
    /// names it in otherwise; or <see langword="null"/> for the whole delegate where files cannot be given, which the
    /// caller that reads files says on its own. A schema that is not one the subset reads types nothing. Where no files
    /// are given at all, a reason step's output may be anything and no prompt is read.
    /// </param>
    /// <param name="providers">
    /// The configuration's providers and tiers, resolved; or <see langword="null"/> where the configuration cannot be
    /// given, which leaves V38's config half unchecked - a definition is never checked against a configuration the
    /// caller did not choose.
    /// </param>
    /// <param name="languages">
    /// The names <c>codebaseGraph.languages</c> may hold, as the extractors' registry gives them
    /// (<see cref="Codebase.CodeGraphExtractors.Languages"/>) - this check keeps no list of its own; or
    /// <see langword="null"/> where the caller has no registry, which leaves that one rule of V52 unchecked.
    /// </param>
    /// <param name="search">
    /// The configuration's web search, resolved; or <see langword="null"/> where the configuration cannot be given, which
    /// leaves V55's names unchecked, a name given twice refused all the same.
    /// </param>
    /// <param name="skills">
    /// The lookup of the skills a prompt names, in the three places; or <see langword="null"/> where the caller reads no
    /// files of the repository, which leaves every <c>{skill.&lt;name&gt;}</c> unlooked-for - a check that reads no file
    /// cannot say one is missing.
    /// </param>
    public static IReadOnlyList<string> Validate(
        WorkflowDefinition definition,
        bool hasCommitTemplate,
        Func<string, string?, string?>? files,
        DssHarness.Core.Agents.AgentProviders? providers = null,
        IReadOnlyList<string>? languages = null,
        Web.Search.WebSearchSources? search = null,
        AgentSkills? skills = null)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var check = new Check(definition, hasCommitTemplate, files, providers, languages, search, skills);
        return check.Problems;
    }

        /// <summary>
        /// What each agent of the definition runs at, one line apiece - for a definition that holds, said by
        /// <c>validate-workflow</c>: each reason step, the advisor and the reporter, with each model it may run on and the
        /// level it resolves to (its own default, where the step gives it none) - the step's keys over its agent
        /// definition's, each resolved on its own. Nothing where the caller gave no providers, which is no configuration
        /// to resolve against; agent definitions are read only where the caller gave files.
        /// </summary>
        /// <param name="definition">A workflow's parsed definition.</param>
        /// <param name="providers">The configuration's providers and tiers, resolved.</param>
        /// <param name="files">The named files of the workflow's directory, by the path a definition names them with; null where the caller reads none.</param>
        public static IReadOnlyList<string> ModelLevels(
            WorkflowDefinition definition,
            DssHarness.Core.Agents.AgentProviders providers,
            Func<string, string?, string?>? files = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(providers);

            return new Check(definition, hasCommitTemplate: false, files, providers).Levels();
        }

        /// <summary>
        /// Every model an agent of the definition may run on - each reason step, the advisor and the reporter, on the model
        /// it names or every model of its tier, its agent definition's below its own - each once, in the order first met:
        /// what <c>validate-workflow</c> says the prices of. Resolved as the definition's check resolves them; nothing
        /// where what an agent names resolves to none, and no agent definitions are read where the caller gave no files.
        /// </summary>
        /// <param name="definition">A workflow's parsed definition.</param>
        /// <param name="providers">The configuration's providers and tiers, resolved.</param>
        /// <param name="files">The named files of the workflow's directory, by the path a definition names them with; null where the caller reads none.</param>
        public static IReadOnlyList<(DssHarness.Core.Configuration.AgentProviderConfig Provider, DssHarness.Core.Configuration.AgentModelConfig Model)> ModelsRunOn(
            WorkflowDefinition definition,
            DssHarness.Core.Agents.AgentProviders providers,
            Func<string, string?, string?>? files = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(providers);

            return new Check(definition, hasCommitTemplate: false, files, providers).Models();
        }

        /// <summary>The model each create-agent step makes its agent for, by its path: the first model of the first reason
        /// step that works in it (<c>in</c>), as that step resolves it - its agent definition's tier or model below its
        /// own, where files are given to read them by - or - where no reason step works in it - the first model of the
        /// configuration's default tier (part 04, "What a create-agent step hands the service"). A step whose model
        /// resolves to none is not among them; the definition's check refuses it.
        /// </summary>
        /// <param name="definition">A workflow's parsed definition.</param>
        /// <param name="providers">The configuration's providers and tiers, resolved.</param>
        /// <param name="files">The named files of the workflow's directory, by the path a definition names them with; null where the caller reads none.</param>
        public static IReadOnlyDictionary<string, string> AgentModels(
            WorkflowDefinition definition,
            DssHarness.Core.Agents.AgentProviders providers,
            Func<string, string?, string?>? files = null)
        {
            ArgumentNullException.ThrowIfNull(definition);
            ArgumentNullException.ThrowIfNull(providers);

            return new Check(definition, hasCommitTemplate: false, files, providers).AgentModels();
        }

    /// <summary>
    /// The keys of one provider a definition's steps would read and the slot does not hold, with the steps that would
    /// read them: first as a step's first model, then as a later model of its tier.
    /// </summary>
    /// <param name="Provider">The provider that appoints them.</param>
    /// <param name="Missing">Every name of its <c>apiKeyNames</c> the slot does not hold, in its order.</param>
    /// <param name="ReadBy">Each step that would read one, as a sentence names it - a later model of its tier said so - each once.</param>
    public sealed record KeyNeed(DssHarness.Core.Configuration.AgentProviderConfig Provider, IReadOnlyList<string> Missing, IReadOnlyList<string> ReadBy);

    /// <summary>
    /// The keys a definition's steps would read that the slot does not hold, one need for each provider - for a
    /// definition that holds, said by <c>validate-workflow</c>, which passes: a machine that only checks a
    /// configuration needs no key, and a run refuses a step whose first model's keys the slot holds none of, never starting
    /// its agent (<see cref="DssHarness.Core.Agents.AgentNotStartedBecause.KeyMissing"/>). A step would read a provider's keys where
    /// one of its models is the provider's and the client that reaches it signs each request with the provider's key
    /// (<see cref="DssHarness.Core.Agents.AgentProviders.KeysRead"/>, the one place that says it); the advisor and the
    /// reporter are asked the same. Every name the slot lacks is said, one beside a name it holds among them.
    /// </summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    /// <param name="providers">The configuration's providers and tiers, resolved.</param>
    /// <param name="held">The names the slot declares, holding something.</param>
    public static IReadOnlyList<KeyNeed> MissingKeys(WorkflowDefinition definition, DssHarness.Core.Agents.AgentProviders providers, IReadOnlyCollection<string> held)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(held);

        return new Check(definition, hasCommitTemplate: false, files: null, providers).Keys(held);
    }

    /// <summary>One check of one definition, holding what the walk built so no rule reads a fact twice.</summary>
    private sealed class Check
    {
        private readonly WorkflowDefinition _definition;
        private readonly bool _hasCommitTemplate;
        private readonly List<string> _problems = [];

        /// <summary>Every step, in the order the definition declares them.</summary>
        private readonly List<Entry> _entries = [];

        /// <summary>
        /// Every step's full path, from the flow's root, to its entry: the first declared where siblings share a name,
        /// as <see cref="FlowScope"/> finds one.
        /// </summary>
        private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);

        /// <summary>
        /// The place the recovery's steps hang from, where the workflow declares one: placed after every step of the
        /// flow, so that its steps see them all, and found by no name a step of the flow could read.
        /// </summary>
        private Entry? _recover;

        /// <summary>
        /// The places the recoveries of workflows used as steps hang from, each under the step that uses it, in the
        /// order their uses are declared: their steps are checked as the flow's own recovery's are, and read by no name
        /// a step outside their use could read.
        /// </summary>
        private readonly List<Entry> _useRecoveries = [];

        /// <summary>
        /// The groups whose <c>forEach</c> was refused for itself - not written whole, reading a name nothing provides, or
        /// yielding what is no list - which no later rule reads again.
        /// </summary>
        private readonly HashSet<string> _refusedForEach = new(StringComparer.Ordinal);

        internal Check(
            WorkflowDefinition definition,
            bool hasCommitTemplate,
            Func<string, string?, string?>? files,
            DssHarness.Core.Agents.AgentProviders? providers = null,
            IReadOnlyList<string>? languages = null,
            Web.Search.WebSearchSources? search = null,
            AgentSkills? skills = null)
        {
            _definition = definition;
            _hasCommitTemplate = hasCommitTemplate;
            _files = files;
            _providers = providers;
            _languages = languages;
            _search = search;
            _skills = skills;

            DefinitionRules();
            Index(definition.Steps, parent: null);
            IndexRecover();
            Shadowing();
            Size();
            Cycle();
            WorkflowAnswers();
            Steps();
            Uses();
            AgentBodies();
            CodebaseGraph();
            Amend();
            Checkpoints();
            SharedEndings();
            Agents();
            AgentPrompts();
            Outputs();
            PullRequests();
            Pushes();
        }

        internal IReadOnlyList<string> Problems => _problems;

        /// <summary>
        /// The place of a read made once every step may have run - an output's <c>from</c>, read when the run has ended,
        /// and a plan update's <c>scope</c>, read when the update stands: after every step, so every step is behind it.
        /// </summary>
        private const int AfterEveryStep = int.MaxValue;

        /// <summary>One step, placed in the flow: the group it is declared in is <paramref name="Parent"/>, none at the top level.</summary>
        private sealed record Entry(WorkflowStep Step, string Path, int Order, Entry? Parent)
        {
            /// <summary>The path of the group it is declared in - the scope its names are read in - or <see langword="null"/> at the top level.</summary>
            public string? ParentPath => Parent?.Path;

            /// <summary>Whether <paramref name="other"/> is declared inside this step's own subtree.</summary>
            public bool Owns(Entry other) => other.Path.StartsWith(Path + ".", StringComparison.Ordinal);
        }

        private void DefinitionRules()
        {
            LimitRules(
                new FlowUseOwn(
                    MaxReasoningAgents: _definition.MaxReasoningAgents,
                    MaxParallelProcesses: _definition.MaxParallelProcesses,
                    MaxOpenAgents: _definition.MaxOpenAgents,
                    MaxRetries: _definition.MaxRetries,
                    BackoffSeconds: _definition.BackoffSeconds,
                    MaxInstances: _definition.MaxInstances,
                    HandoffSeconds: _definition.HandoffSeconds),
                lead: string.Empty);

            if (_definition.Budget is { } budget)
            {
                if (budget.Usd is null && budget.Tokens is null && budget.Minutes is null)
                {
                    _problems.Add("budget holds no usd, tokens or minutes; a budget with nothing to count is no budget. Give at least one, or take the section out to have none.");
                }

                // Every amount given is one the run can spend down to its reserve: a zero would pause the run before it
                // began, beside whatever else the budget counts.
                foreach (var (key, amount) in new[] { ("usd", budget.Usd), ("tokens", (double?)budget.Tokens), ("minutes", budget.Minutes) })
                {
                    if (amount is <= 0)
                    {
                        _problems.Add($"budget.{key} must be above zero, found {amount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                }

                Within(budget.ReservePercent, 1, 50, "budget.reservePercent");
            }

            if (_definition.Recover is { } recover)
            {
                RecoverRules(recover, lead: string.Empty);
            }

            ModelRules(_definition.Advisor?.Tier, _definition.Advisor?.Model, "advisor");
            ModelRules(_definition.Reporter?.Tier, _definition.Reporter?.Model, "reporter");
            Within(_definition.Advisor?.Retries, 1, 20, "advisor.retries");
            Within(_definition.Advisor?.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, "advisor.backoffSeconds");
            Within(_definition.Reporter?.Retries, 1, 20, "reporter.retries");
            Within(_definition.Reporter?.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, "reporter.backoffSeconds");

            foreach (var input in _definition.Inputs)
            {
                DeclaredNameRules(input.Name, "input");

                if (input.Required && input.Default is not null)
                {
                    _problems.Add($"input '{input.Name}' is required and declares a default: a default always supplies the value, so required would never refuse. Give the default, or require the value.");
                }

                if (input.Options.Count > 0 && input.Default is { Length: > 0 } && !input.Options.Contains(input.Default, StringComparer.Ordinal))
                {
                    _problems.Add($"input '{input.Name}' defaults to '{input.Default}', which is not one of its options ({string.Join(", ", input.Options.Select(option => $"'{option}'"))}); a run that used the default would be refused for the value the file itself gave");
                }

                foreach (var repeated in input.Options.GroupBy(option => option, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
                {
                    _problems.Add($"input '{input.Name}' lists '{repeated}' among its options more than once; an option is one value a run may be started with, and a second says nothing the first did not");
                }
            }

            foreach (var parameter in _definition.Params)
            {
                DeclaredNameRules(parameter.Name, "param");

                if (parameter.Required && parameter.Default is not null)
                {
                    _problems.Add($"param '{parameter.Name}' is required and declares a default: a default always supplies the value, so required would never refuse. Give the default, or require the value.");
                }
            }

            if (_definition.Advisor is { } advisor)
            {
                AdvisorKinds(advisor, lead: string.Empty);
            }
        }

        /// <summary>
        /// The bounds of a run's limits - its own, or those of a workflow a use that governs itself
        /// (<c>useParentLimits: false</c>) applies to its steps - each said after <paramref name="lead"/>: what a run's
        /// own key may not hold, a use may not declare for the steps it governs.
        /// </summary>
        /// <param name="limits">The limits, as a run's own or a governing use's settings hold them.</param>
        /// <param name="lead">What each refusal starts with: nothing for the run's own, the governing use for a use's.</param>
        private void LimitRules(FlowUseOwn limits, string lead)
        {
            Within(limits.MaxReasoningAgents, 1, 32, $"{lead}maxReasoningAgents");
            Positive(limits.MaxParallelProcesses, $"{lead}maxParallelProcesses");
            Positive(limits.MaxOpenAgents, $"{lead}maxOpenAgents");
            Within(limits.MaxRetries, 1, 20, $"{lead}maxRetries");
            Within(limits.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, $"{lead}backoffSeconds");
            Within(limits.MaxInstances, 100, 100_000, $"{lead}maxInstances");
            Within(limits.HandoffSeconds, 30, 900, $"{lead}handoffSeconds");
        }

        /// <summary>
        /// What a recovery's own keys may say - the run's own, and that of each workflow a step uses, alike: its
        /// <c>maxVisits</c> in its range, and a failure of its own work never given to a recovery, which would be the
        /// recovery calling itself, nor passed over, which would leave the step that gave it its failure neither put right
        /// nor failed.
        /// </summary>
        /// <param name="recover">The recovery.</param>
        /// <param name="lead">What each refusal starts with: nothing for the run's own, the use for a used workflow's.</param>
        private void RecoverRules(FlowRecover recover, string lead)
        {
            Within(recover.MaxVisits, 1, FlowRecover.MostVisits, $"{lead}recover.maxVisits");

            if (recover.OnFailure == WorkflowStep.Recover)
            {
                _problems.Add(CallsItself($"{lead}recover", "gives a failure of its own work to the recovery (onFailure: recover)"));
            }
            else if (recover.OnFailure == "continue")
            {
                _problems.Add($"{lead}recover passes a failure of its own work over (onFailure: continue); the step that gave it its failure would be neither put right nor failed - say pause or stop");
            }
        }

        /// <summary>
        /// V66: what a use that governs itself (<c>useParentLimits: false</c>) applies to its steps holds to every rule the
        /// run's own key of that name does - its limits, its <c>maxInstances</c>, its advisor's tier, model, tries and
        /// kinds, its answers, and the shape it gives the run's graph - for a use cannot declare what a run could not. Its
        /// budget and its reporter are carried and applied to nothing yet, so nothing is held of them here. Each refusal
        /// names the used workflow and the step that uses it.
        /// </summary>
        /// <param name="entry">The step that uses the workflow and governs itself.</param>
        /// <param name="use">The workflow it uses.</param>
        /// <param name="own">The workflow's own run-level settings, which the use applies.</param>
        private void GoverningRules(Entry entry, FlowUse use, FlowUseOwn own)
        {
            var governing = Governing(entry, use);
            var lead = $"{governing}: ";

            LimitRules(own, lead);

            if (own.Advisor is { } advisor)
            {
                ModelRules(advisor.Tier, advisor.Model, $"{lead}advisor");
                Within(advisor.Retries, 1, 20, $"{lead}advisor.retries");
                Within(advisor.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, $"{lead}advisor.backoffSeconds");
                AdvisorKinds(advisor, lead);
            }

            // Its answers are consulted for its steps' questions, after their own and before the run's: they answer as a
            // group's would, at the step that uses the workflow.
            foreach (var (kind, answer) in own.Answers ?? new Dictionary<string, FlowAnswer>())
            {
                AnswerRules(kind, answer, governing, entry);
            }

            if (own.CodebaseGraph is { } graph)
            {
                GraphShapeRules(graph, lead);
            }
        }

        /// <summary>How a refusal names the workflow a use that governs itself runs, and the step that uses it.</summary>
        private static string Governing(Entry entry, FlowUse use)
            => $"the workflow '{use.Name}' governing step '{entry.Path}' (useParentLimits: false)";

        /// <summary>Walks the flow once, placing every step and checking every sibling list's names.</summary>
        private void Index(IReadOnlyList<WorkflowStep> steps, Entry? parent)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var parentPath = parent?.Path;

            foreach (var step in steps)
            {
                var where = parentPath is null ? step.Name : $"{parentPath}.{step.Name}";

                // V60, the resolution rule: a step that still names a use was never resolved, and no check after this
                // one can know what it is - the workflow it names was never read.
                if (step.UseSource is not null)
                {
                    _problems.Add($"step '{where}' uses '{step.UseSource}', and the definition was never resolved; a use is read before the definition is checked, so this is a defect of the tool that read it");
                }

                if (FlowScope.IsReserved(step.Name))
                {
                    _problems.Add($"step '{where}' is named '{step.Name}', a word a definition reads for its own; a read could not tell the step from the word");
                }
                else if (!FlowScope.IsWellFormedName(step.Name))
                {
                    _problems.Add($"step '{where}' is named '{step.Name}', which is not a lower-case word with dashes within; a read's segments are told apart by dots, so a name may not hold one");
                }

                if (!names.Add(step.Name))
                {
                    _problems.Add($"two steps of '{parentPath ?? "the flow's root"}' are named '{step.Name}'; what depends on it, or reads it, could only ever mean one of them");
                }

                var entry = new Entry(step, where, _entries.Count, parent);
                _entries.Add(entry);
                _byPath.TryAdd(where, entry);

                if (step.Steps.Count > 0)
                {
                    Index(step.Steps, entry);
                }
            }
        }

        /// <summary>
        /// Places the recovery's steps, where the workflow declares them, under a place of their own after every step
        /// of the flow: they read every step of it, and nothing of the flow reads them.
        /// </summary>
        private void IndexRecover()
        {
            if (_definition.Recover is { } recover)
            {
                // A policy a step of a used workflow reads from a step-kind input is the one its use fills it with, as
                // the run reads it: a use that fills failTo with recover gives the recovery its steps' failures.
                if (!_entries.Any(entry => (entry.Step.OnFailure is { } written && PolicyFilled(entry, written) == WorkflowStep.Recover) || entry.Step.OnAddress == WorkflowStep.Recover))
                {
                    _problems.Add("the workflow declares recover, and no step gives it a failure or an issue to fix; a step says onFailure: recover, itself or through a group around it, for its failures to reach it, or onAddress: recover for the pre-existing issues it decides to fix");
                }

                // The recovery's own onFailure is what a failure of one of its steps does where the step says none, as a
                // group's is for its members. Its place is the recovery's whatever a step of the flow is named, as
                // FlowScope reads the path.
                _recover = new Entry(recover.Declaration, FlowScope.Recovery, _entries.Count, null);
                _entries.Add(_recover);
                _byPath[FlowScope.Recovery] = _recover;
                Index(recover.Steps, _recover);
            }

            IndexUseRecoveries();
        }

        /// <summary>
        /// Places the recovery of each workflow a step uses, under the step that uses it: its steps are the called
        /// workflow's own, read by no name outside its use, and reached by a failure inside it before the caller's
        /// handling of the step. A use among a recovery's own steps brings a recovery of its own, placed in its turn - the
        /// walk goes on over every step it places, as <see cref="FlowScope.Declared"/> and the run do.
        /// </summary>
        private void IndexUseRecoveries()
        {
            for (var at = 0; at < _entries.Count; at++)
            {
                if (_entries[at] is not { Step.Use.Recover: { } called } used)
                {
                    continue;
                }

                var place = new Entry(called.Declaration, $"{used.Path}.{FlowScope.Recovery}", _entries.Count, used);
                _useRecoveries.Add(place);
                _entries.Add(place);
                _byPath.TryAdd(place.Path, place);
                Index(called.Steps, place);
            }
        }

        /// <summary>
        /// Refuses a step named as a step it can see from outside its own group by bare name: a step beside a group
        /// around it, or that group itself - and for the recovery's steps, a step of the flow's top level. A name is looked
        /// up from the inside out, so the inner step would be read wherever the outer one was meant, and the outer one
        /// could no longer be named from there at all.
        /// </summary>
        private void Shadowing()
        {
            // FlowScope owns which name an inner step can see; the wording is this check's.
            foreach (var (path, shadowed) in FlowScope.Shadowings(_definition))
            {
                _problems.Add($"step '{path}' has the name of step '{shadowed}', which it can see from where it is: names are looked up from the inside out, so the inner one would be read in its place");
            }
        }

        private void WorkflowAnswers()
        {
            foreach (var (kind, answer) in _definition.Answers)
            {
                AnswerRules(kind, answer, "the workflow", owner: null);
            }
        }

        /// <summary>
        /// V26, the flow's size a reader cannot see: a workflow declares at most 200 steps, the recovery's among
        /// them, for they run. How deep a flow may nest is the reader's own bound, refused with its line and
        /// column where the file is read (<see cref="WorkflowFileParser.MaxDepth"/>), and no definition that deep
        /// reaches this check.
        /// </summary>
        private void Size()
        {
            var declared = _entries.Count(entry => !ReferenceEquals(entry, _recover) && !_useRecoveries.Any(place => ReferenceEquals(entry, place)));

            if (declared > MostSteps)
            {
                _problems.Add($"the workflow declares {declared} steps; at most {MostSteps}");
            }
        }

        /// <summary>V15, the one cycle: <see cref="FlowGraph"/> owns the walk, and the first cycle it closes is said whole.</summary>
        private void Cycle()
        {
            if (FlowGraph.FindCycle(_definition) is { } cycle)
            {
                _problems.Add($"the workflow has a dependency cycle: {string.Join(" -> ", cycle)}");
            }
        }

        /// <summary>Checks every step's own keys, once the whole flow is placed.</summary>
        private void Steps()
        {
            foreach (var entry in _entries)
            {
                if (ReferenceEquals(entry, _recover) || _useRecoveries.Any(place => ReferenceEquals(entry, place)))
                {
                    // A recovery's own place takes no keys: its steps are checked as every step is.
                    continue;
                }

                CommonStepRules(entry);

                if (entry.Step.IsGroup)
                {
                    GroupRules(entry);
                    continue;
                }

                var type = WorkflowStepTypes.Find(entry.Step.Type ?? string.Empty);
                var where = $"step '{entry.Path}'";

                if (type is null)
                {
                    // Refused where the file was read; nothing more can be checked of a step of no type.
                    continue;
                }

                // Every step that runs may bound one attempt; a group and a step that only asks were refused the key
                // where the file was read.
                var limitHolds = Within(entry.Step.TimeoutMinutes, 1, WorkflowStep.MostTimeoutMinutes, $"{where}: timeoutMinutes");

                switch (type.Type)
                {
                    case "reason":
                        ReasonRules(entry);
                        break;

                    case "build":
                    case "test":
                    case "run":
                    case "check-mutations":
                    case "graphify-codebase":
                        if (entry.Step.In is not null)
                        {
                            WorksIn(entry, entry.Step.In);
                        }

                        break;

                    case "anchor-balance":
                        if (entry.Step.Rows?.Expression is { } rows)
                        {
                            Expression(entry, rows, "rows");
                        }

                        break;

                    case "anchor-citations":
                        if (entry.Step.AnchorPaths?.Expression is { } paths)
                        {
                            Expression(entry, paths, "paths");
                        }

                        break;

                    case "ci-read":
                    case "ci-wait":
                        Positive(entry.Step.Limit, $"{where}: limit");
                        Within(entry.Step.AppearSeconds, 30, 3_600, $"{where}: appearSeconds");
                        var waitHolds = Within(entry.Step.WaitMinutes, 1, 720, $"{where}: waitMinutes");
                        Within(entry.Step.PollSeconds, 10, 600, $"{where}: pollSeconds");

                        // One attempt of a wait waits as long as its waitMinutes say, and reads its checks once more as
                        // they end: a time limit not past that would end a wait that ran its course as incomplete, or race
                        // its own answer. Weighed only where both are in their ranges, each refused on its own otherwise.
                        if (type.Type == "ci-wait"
                            && limitHolds
                            && waitHolds
                            && entry.Step.TimeoutMinutes is { } limit
                            && (entry.Step.WaitMinutes ?? WorkflowStep.DefaultWaitMinutes) is var wait
                            && limit <= wait)
                        {
                            _problems.Add(string.Create(
                                System.Globalization.CultureInfo.InvariantCulture,
                                $"{where}: timeoutMinutes ({limit}) is not more than its waitMinutes ({wait}{(entry.Step.WaitMinutes is null ? ", the default" : string.Empty)}); one attempt waits that long for its checks, so its time limit is longer"));
                        }

                        break;

                    case "fold":
                    case "delete-agent":
                    case "refresh":
                    case "rebase":
                        FoldRules(entry);
                        break;

                    case "validate-plan":
                        if (entry.Step.Items is { } items)
                        {
                            Expression(entry, items, "items");
                        }

                        if (entry.Step.Scope is not null)
                        {
                            Expression(entry, entry.Step.Scope, "scope");
                        }

                        break;

                    case "commit":
                        // Where the configuration declares a template, the template writes the message and a step's own is
                        // not used: one definition then holds under a configuration with a template and one without.
                        if (entry.Step.Message is null && !_hasCommitTemplate)
                        {
                            _problems.Add($"{where} {Configuration.CommitTemplate.NoMessage}");
                        }

                        CommitVariablesWithoutATemplate(entry);
                        break;

                    case "pause":
                        PauseRules(entry);
                        break;

                    default:
                        break;
                }

                Templates(entry, type);
                Prompts(entry);
                Skills(entry);
            }
        }

        /// <summary>
        /// V61, the one boundary of a used workflow: the step that uses it, nearest above <paramref name="entry"/> -
        /// null at the flow's own levels. Every step under it reads the called workflow's inputs and params as
        /// <c>input</c> and <c>param</c>, and no step of the workflow that called it.
        /// </summary>
        private Entry? UseOf(Entry entry)
        {
            for (var at = entry.Parent; at is not null; at = at.Parent)
            {
                if (at.Step.Use is not null)
                {
                    return at;
                }
            }

            return null;
        }

        /// <summary>
        /// The texts that declare the name <paramref name="named"/> for a step: the nearest use around it whose called
        /// workflow declares it, and else the run's own where the run does. Where neither does, the owner to name in
        /// the refusal: the nearest use around the step - the world it reads in - or the run.
        /// </summary>
        /// <param name="entry">The step reading <c>{text.&lt;named&gt;}</c>.</param>
        /// <param name="named">The text's name.</param>
        private (string? Owner, IReadOnlyDictionary<string, string>? Texts) TextsFor(Entry entry, string named)
        {
            string? nearest = null;

            for (var at = entry.Parent; at is not null; at = at.Parent)
            {
                if (at.Step.Use is not { } use)
                {
                    continue;
                }

                nearest ??= use.Name;

                if (use.Texts is { } own && own.ContainsKey(named))
                {
                    return (use.Name, own);
                }
            }

            return _definition.Texts is { } texts && texts.ContainsKey(named) ? (null, texts) : (nearest, null);
        }

        /// <summary>Checks every step that uses a workflow: the values it fills the workflow's inputs with, read as each input's kind declares.</summary>
        private void Uses()
        {
            foreach (var entry in _entries)
            {
                if (entry.Step.Use is { } use)
                {
                    UseRules(entry, use);
                }
            }
        }

        /// <summary>
        /// The values one use fills the inputs of the workflow it uses with - each with value, and the default of each
        /// input it leaves to one - read as the input's kind declares, where the use sits, as the run reads them when the
        /// use opens; the used workflow's recovery, held to the run's own recovery's rules; and the settings a use that
        /// governs itself applies, held to the run's own.
        /// </summary>
        private void UseRules(Entry entry, FlowUse use)
        {
            var where = $"step '{entry.Path}'";
            var declared = use.Inputs.ToDictionary(input => input.Name, StringComparer.Ordinal);

            foreach (var name in entry.Step.With.Keys.Where(name => !declared.ContainsKey(name)))
            {
                _problems.Add($"{where} fills '{name}' with, and the workflow '{use.Name}' it uses declares no input of that name");
            }

            foreach (var input in use.Inputs.Where(input => input.Required && input.Default is null && !entry.Step.With.ContainsKey(input.Name)))
            {
                _problems.Add($"{where} names no '{input.Name}' in with, and the workflow '{use.Name}' it uses requires the input");
            }

            foreach (var input in use.Inputs)
            {
                // A default is read where the with value it stands for would be - in the caller, where the use sits -
                // whatever workflow wrote it. An empty default is the input not given, except where the kind is an
                // expression's, which reads it as one and says nothing.
                var given = entry.Step.With.TryGetValue(input.Name, out var with) ? with : input.Default;

                if (given is null || (with is null && given.Length == 0 && input.Kind != FlowInput.ValueKind))
                {
                    continue;
                }

                var key = with is not null ? $"with.{input.Name}" : $"inputs.{input.Name}.default";
                FillRules(entry, use, input, given, key, $"{where}.{key}");
            }

            if (use.Recover is { } called)
            {
                RecoverRules(called, $"{where} uses the workflow '{use.Name}', whose ");
            }

            // A use that governs itself (useParentLimits: false) applies the used workflow's run-level keys to its
            // steps - every one but an amend, which is a run's own and is refused here, never silently dropped.
            if (use.Own is { } own)
            {
                if (own.Amends)
                {
                    _problems.Add($"{where} says useParentLimits: false on the workflow '{use.Name}' it uses, which declares an amend; an amend is a run's own - a request amended, or an answer changed, while it works - and no step of a run applies one");
                }

                GoverningRules(entry, use, own);
            }

            // Each declared output of the used workflow, checked as the workflow's own are, inside the use and after
            // every step of it - which is what a later step of the caller reads as the use step's output. Its from is
            // read within the use, as the engine reads it there when the use ends.
            foreach (var output in use.Outputs)
            {
                Expression(AfterItsSteps(entry), output.From, $"outputs.{output.Name}.from", within: true);
            }
        }

        /// <summary>
        /// One value a use fills an input of the workflow it uses with - its with value, or the input's default - read as
        /// the input's kind declares, where the use sits: an expression for a <c>value</c>, a template written whole for a
        /// <c>text</c>, a path of the used workflow's directory for a <c>file</c>, and a single name for a <c>step</c>.
        /// </summary>
        /// <param name="entry">The step that uses the workflow.</param>
        /// <param name="use">The workflow it uses.</param>
        /// <param name="input">The input filled.</param>
        /// <param name="value">What fills it.</param>
        /// <param name="key">The key the value is said under, after the use's path: <c>with.&lt;name&gt;</c>, or <c>inputs.&lt;name&gt;.default</c> for a default.</param>
        /// <param name="what">The value as a refusal names it.</param>
        private void FillRules(Entry entry, FlowUse use, FlowInput input, string value, string key, string what)
        {
            switch (input.Kind)
            {
                case FlowInput.TextKind:
                    // Filled as a template is when the use opens: one not written whole fails the use there.
                    if (WorkflowExpressions.TemplateSyntaxProblem(value) is { } broken)
                    {
                        _problems.Add($"'{entry.Path}.{key}' is not written whole: {broken}, and nothing it reads past there would be checked");
                        break;
                    }

                    foreach (var read in WorkflowExpressions.TemplateReads(value))
                    {
                        TemplateRead(entry, read, key);
                    }

                    break;

                case FlowInput.FileKind:
                    if (value.Length == 0 || value.Contains('{') || value.Contains(':') || value.StartsWith('/') || value.StartsWith('\\') || value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => part is "." or ".."))
                    {
                        _problems.Add($"{what} is '{value}', which is no path relative to the directory of the workflow '{use.Name}' it uses; a file input is such a path, read inside that directory");
                    }

                    break;

                case FlowInput.StepKind:
                    StepInputRules(entry, use, value, what);
                    break;

                default:
                    Expression(entry, value, key);
                    break;
            }
        }

        /// <summary>
        /// The place a read made inside the use <paramref name="use"/> once every step of it may have run is made from: a
        /// declared output's <c>from</c>, read when the use ends.
        /// </summary>
        private static Entry AfterItsSteps(Entry use) => new(use.Step, use.Path, AfterEveryStep, use);

        /// <summary>
        /// One <c>step</c>-kind value a use fills an input with - its with value or the input's default: a single name,
        /// which where a step of the used workflow reads it into <c>onFailure</c> is one of the failure policies, and where
        /// into <c>then</c> a step of the caller it sees - so a policy names a recovery to reach, and a name a step beside
        /// the use. The recovery a policy reaches is the one a failure of a step of the used workflow reaches
        /// (<see cref="RecoveryFrom"/>): the used workflow's own, or that of a workflow around it.
        /// </summary>
        private void StepInputRules(Entry entry, FlowUse use, string value, string what)
        {
            if (value.Length == 0 || value.Contains('{') || value.Contains('.') || value.Contains('/') || value.Contains('\\') || value.Contains(' '))
            {
                _problems.Add($"{what} is '{value}', which is no single name: a step input is one word, the policy a step's onFailure takes or the step a handler's then sends the run to");
                return;
            }

            if (WorkflowStep.FailurePolicies.Contains(value, StringComparer.Ordinal))
            {
                if (value == WorkflowStep.Recover && RecoveryFrom(entry) is null)
                {
                    _problems.Add($"{what} is 'recover', and neither the workflow '{use.Name}' it uses nor the calling workflow declares a recover; one of them must, for the failures it hands there");
                }

                return;
            }

            if (Scope(entry, value) is null)
            {
                _problems.Add($"{what} is '{value}', which is no step the use sees and no failure policy; a step input names a step beside the use, or one of {string.Join(", ", WorkflowStep.FailurePolicies.Select(known => $"'{known}'"))}");
            }
        }

        /// <summary>The keys every step takes, group or typed step alike.</summary>
        private void CommonStepRules(Entry entry)
        {
            var step = entry.Step;
            var where = step.IsGroup ? $"group '{entry.Path}'" : $"step '{entry.Path}'";

            Within(step.Retries, 1, 20, $"{where}: retries");
            HandlerRules(entry, where);

            if (step.OnFailure is { } policy && policy.Contains('{'))
            {
                // V62, the one place a failure policy is read rather than written: a step of a workflow used as a step
                // takes it from one of the used workflow's step-kind inputs, filled by the use - every other step says
                // one of the policies itself. What the use fills it with is known where the file is read, and is one
                // of the policies: an input it fills with nothing, and gives no default, would leave the step's failure
                // to go where a step that wrote nothing's goes, in silence.
                if (UseOf(entry) is not { Step.Use: { } use })
                {
                    _problems.Add($"{where} reads its onFailure from '{policy}', and only a step of a workflow used as a step does: a step of the workflow itself says one of {string.Join(", ", WorkflowStep.FailurePolicies.Select(known => $"'{known}'"))}");
                }
                else if (InputRead(entry, policy, FlowInput.StepKind) is not { } read)
                {
                    _problems.Add($"{where} reads its onFailure from '{policy}', which is not one read of a step-kind input; it is {{input.<name>}} whole, the name of an input the workflow '{use.Name}' declares of the kind 'step'");
                }
                else if (read.Given is not { Length: > 0 } given)
                {
                    _problems.Add($"{where} reads its onFailure from '{policy}', and '{read.Holder.Path}' fills the input with no name; a step's onFailure is one of {string.Join(", ", WorkflowStep.FailurePolicies.Select(known => $"'{known}'"))}, and the use fills it with one");
                }
                else if (!given.Contains('{') && !WorkflowStep.FailurePolicies.Contains(given, StringComparer.Ordinal))
                {
                    _problems.Add($"{where} reads its onFailure from '{policy}', which '{read.Holder.Path}' fills with '{given}', and that is none of {string.Join(", ", WorkflowStep.FailurePolicies.Select(known => $"'{known}'"))}");
                }
            }

            foreach (var (askedKey, askedWith) in new[] { ("prompt", step.Prompt), ("resumePrompt", step.ResumePrompt) })
            {
                if (askedWith is null || !askedWith.Contains('{'))
                {
                    continue;
                }

                // V63, the one place a file a step is asked with is read rather than written: a step of a workflow
                // used as a step is asked with a file one of the used workflow's file-kind inputs names, filled by
                // the use - every other step names its file itself.
                if (UseOf(entry) is not { Step.Use: { } use })
                {
                    _problems.Add($"{where} is asked with '{askedWith}', and only a step of a workflow used as a step reads its {askedKey} so: it is {{input.<name>}} whole, the name of an input the used workflow declares of the kind 'file'");
                }
                else if (InputRead(entry, askedWith, FlowInput.FileKind) is not { } read)
                {
                    _problems.Add($"{where} is asked with '{askedWith}', which is not one read of a file-kind input; it is {{input.<name>}} whole, the name of an input the workflow '{use.Name}' declares of the kind 'file'");
                }
                else if (read.Given is not { Length: > 0 } file || file.Contains('{'))
                {
                    _problems.Add($"{where} is asked with '{askedWith}', and '{read.Holder.Path}' fills the input with no path; a step is asked with a file, and the use fills it with one");
                }
            }

            // A failure given to the recovery reaches the one the run finds for it (RecoveryFrom): a step of that
            // recovery gives it its own failure, and so calls itself - written out, or read from an input its use fills
            // with recover. Where a written recover reaches none, it is said here; where a use's fill does, it is said
            // where the use fills it.
            if (step.OnFailure is { } own && PolicyFilled(entry, own) == WorkflowStep.Recover)
            {
                var reached = RecoveryFrom(entry.Parent);
                var filled = own.Contains('{') && InputRead(entry, own, FlowInput.StepKind) is { } read
                    ? $", which reads its onFailure from '{own}', filled with '{WorkflowStep.Recover}' by '{read.Holder.Path}',"
                    : null;

                if (reached is null && filled is null)
                {
                    _problems.Add($"{where} gives its failure to the recovery (onFailure: recover), and neither the workflow nor any workflow it uses around it declares a recover; declare one, or say what the failure does");
                }
                else if (reached is not null && reached.Owns(entry))
                {
                    _problems.Add(CallsItself($"{where}{filled}", "is a step of the recovery and gives its failure to the recovery"));
                }
            }

            // A reasoning step is never recovered, by its own word or a group's, written out or read from an input its use
            // fills with recover: its agent is tried again and on its fallback, an account at its limit or a provider that
            // fails pauses the run, and what still fails is asked.
            if (step.Type == "reason" && EffectiveOnFailure(entry) is { Policy: WorkflowStep.Recover } recovered)
            {
                var filledBy = recovered.Written.Contains('{') && InputRead(recovered.Owner, recovered.Written, FlowInput.StepKind) is { } read
                    ? read.Holder.Path
                    : null;

                _problems.Add((ReferenceEquals(recovered.Owner, entry), filledBy) switch
                {
                    (true, null) => $"{where} is a reasoning step and gives its failure to the recovery; a reasoning step is never recovered - its agent is tried again and on its fallback, and what still fails is asked: say pause",
                    (true, { } holder) => $"{where} reads its onFailure from '{recovered.Written}', filled with '{WorkflowStep.Recover}' by '{holder}', and a reasoning step is never recovered - its agent is tried again and on its fallback, and what still fails is asked: say pause",
                    (false, null) => $"{where} is a reasoning step, and its failure would go to the recovery by the onFailure of '{recovered.Owner.Path}'; a reasoning step is never recovered - say pause on it",
                    (false, { } holder) => $"{where} is a reasoning step, and its failure would go to the recovery by the onFailure of '{recovered.Owner.Path}', '{recovered.Written}' filled with '{WorkflowStep.Recover}' by '{holder}'; a reasoning step is never recovered - say pause on it",
                });
            }

            if (step.DependsOn.Any(name => string.Equals(name, step.Name, StringComparison.Ordinal)))
            {
                _problems.Add($"{where} depends on itself; a step waits for what runs before it, and this one would wait for itself");
            }

            foreach (var duplicated in step.DependsOn.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
            {
                _problems.Add($"{where} depends on '{duplicated}' more than once");
            }

            foreach (var name in step.DependsOn)
            {
                if (Sibling(entry, name) is not { } dependency)
                {
                    _problems.Add($"{where} depends on '{name}', which is no step declared beside it; a step waits for its own siblings, and a step of another group is waited for by the group that holds it");
                    continue;
                }

                if (dependency.Order > entry.Order)
                {
                    _problems.Add($"{where} depends on '{name}', which is declared after it; steps are declared in the order they run, and a dependency declared after the step that waits for it is met never or too late");
                }

                if (dependency.Step.Always is true)
                {
                    _problems.Add($"{where} depends on '{name}', an always step; an always step is started by the end of the session, and no step waits for it");
                }
            }

            if (step.When is not null)
            {
                Expression(entry, step.When, "when");
            }

            if (step.Type == "test" && step.FilterWhen is { } filterWhen)
            {
                // As when is, with the same reads and the same refusals, and it must yield a boolean (V16-V19).
                Expression(entry, filterWhen, "filterWhen");
            }

            foreach (var (kind, answer) in step.Answers)
            {
                AnswerRules(kind, answer, where, entry);
            }
        }

        /// <summary>What a handler's keys say: that its source is beside it, its codes are codes, and where it sends the run.</summary>
        private void HandlerRules(Entry entry, string where)
        {
            var step = entry.Step;

            if (step.On is null)
            {
                if (step.Then is not null)
                {
                    _problems.Add($"{where} says then without on; then is where a handler sends the run when it is done, and a step the schedule starts has nothing to send back");
                }

                if (step.MaxVisits is not null)
                {
                    _problems.Add($"{where} says maxVisits without on; it bounds how often a handler is raised, and the schedule starts a step once");
                }

                return;
            }

            var raisedBy = step.On;

            if (string.Equals(raisedBy.Step, step.Name, StringComparison.Ordinal))
            {
                _problems.Add(CallsItself(where, "is raised by its own ending"));
            }
            else if (Sibling(entry, raisedBy.Step) is not { } source)
            {
                _problems.Add($"{where} is raised by '{raisedBy.Step}', which is no step declared beside it; a handler is raised by its own siblings");
            }
            else if (source.Step.Always is true)
            {
                _problems.Add($"{where} is raised by '{raisedBy.Step}', an always step; an always step runs at the session's end, when nothing is left to handle its ending");
            }

            if (raisedBy.Codes.Any(code => code is < 1 or > 255))
            {
                _problems.Add($"{where} is raised by the codes {string.Join(", ", raisedBy.Codes)}, and an exit code is from 1 to 255");
            }

            foreach (var duplicated in raisedBy.Codes.GroupBy(code => code).Where(group => group.Count() > 1).Select(group => group.Key))
            {
                _problems.Add($"{where} is raised by the code {duplicated} more than once");
            }

            if (raisedBy.Codes.Count > 0 && raisedBy.Outcome == "success")
            {
                _problems.Add($"{where} is raised by a success with codes; a step that ended done ended with 0, and codes are a failure's");
            }

            if (step.DependsOn.Count > 0)
            {
                _problems.Add($"{where} is raised by '{raisedBy.Step}' and also depends on {string.Join(", ", step.DependsOn.Select(name => $"'{name}'"))}: a handler waits for its source's ending and for nothing else");
            }

            if (step.Always is true)
            {
                _problems.Add($"{where} is raised by '{raisedBy.Step}' and is always; an always step is started by the end of the session, never by a step");
            }

            Within(step.MaxVisits, 1, 20, $"{where}: maxVisits");

            if (step.Then is not { } written)
            {
                return;
            }

            // A handler of a used workflow may take its target from a step-kind input of that workflow, filled by the
            // use: where the value is known where the file is read, the target is checked as a written one is.
            var then = written;

            if (written.Contains('{'))
            {
                if (InputRead(entry, written, FlowInput.StepKind) is not { } read)
                {
                    _problems.Add($"{where} sends the run to '{written}', which is not one read of a step-kind input; a handler's then is a step's name, or {{input.<name>}} whole for one a used workflow's step input fills");
                    return;
                }

                if (read.Given is not { Length: > 0 } given)
                {
                    _problems.Add($"{where} sends the run to '{written}', and '{read.Holder.Path}' fills the input with no name; a handler's then is a step's name, and the use fills it with one");
                    return;
                }

                if (given.Contains('{'))
                {
                    // No single name, refused where the use fills it.
                    return;
                }

                then = given;
            }

            if (AlwaysAround(entry) is { } always)
            {
                _problems.Add($"{where} sends the run to '{then}' from inside the always group '{always.Path}'; an always group runs once, at the session's end, and is never gone back over");
            }

            if (string.Equals(then, step.Name, StringComparison.Ordinal))
            {
                _problems.Add(CallsItself(where, "sends the run back to itself"));
            }
            else if (Scope(entry, then) is { Step.Raised: not null } raised)
            {
                _problems.Add($"{where} sends the run to '{raised.Path}', which is raised by a pre-existing issue and never scheduled; nothing sends the run to it");
            }
            else if (Scope(entry, then) is not { } target)
            {
                _problems.Add($"{where} sends the run to '{then}', which is no step it sees; a redirect goes to a step beside it or beside a group around it, never into a group from outside");
            }
            else if (target.Step.Always is true)
            {
                _problems.Add($"{where} sends the run to '{then}', an always step, which runs once at the session's end and is never gone back to");
            }
            else if (AncestorBeside(entry, target) is { } holder && target.Step.DependsOn.Contains(holder.Step.Name, StringComparer.Ordinal))
            {
                _problems.Add($"{where} sends the run to '{then}', which depends on '{holder.Step.Name}', the group the handler is in: it waits for that group, and would start no sooner");
            }
        }

        /// <summary>Checks what one group holds: its loop, its gathers, and the two together.</summary>
        private void GroupRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"group '{entry.Path}'";

            if (step.ForEach is not null && step.Repeat is not null)
            {
                _problems.Add($"{where} says both forEach and repeat; a group runs its steps once for each work item, or again and again until its bound is met, and a group that did both would never tell which it was doing");
            }

            if (step.Gather.Count > 0 && (step.ForEach is not null || step.Repeat is not null))
            {
                _problems.Add($"{where} gathers and loops; gather is a block's, which runs its steps once and gathers what they wrote into one list");
            }

            GatherNames(entry);

            if (step.Repeat is { } repeat)
            {
                var hasIterations = repeat.MaxIterations is not null;
                var hasRounds = repeat.Rounds is not null;

                if (hasIterations && hasRounds)
                {
                    _problems.Add($"{where} repeats to maxIterations {repeat.MaxIterations} and to rounds {repeat.Rounds}; one bound says the most iterations, the other says the rounds to follow and what keeps the loop going past them, and a loop with both would never tell which it was asked");
                }

                if (!hasIterations && !hasRounds)
                {
                    _problems.Add($"{where} repeats with no bound; give maxIterations, or rounds with blocking, so the loop always has an end");
                }

                if (hasRounds && repeat.Blocking is null)
                {
                    _problems.Add($"{where} repeats in rounds and says no blocking; blocking is what says, past the rounds, whether what is left must still be dealt with, and a loop without it would end with the rounds whatever was left");
                }

                if (!hasRounds && (repeat.Blocking is not null || repeat.BlockersLimitRounds is not null || repeat.BlockersUnbounded))
                {
                    _problems.Add($"{where} says {(repeat.Blocking is not null ? "blocking" : "blockersLimitRounds")} without rounds; past the rounds is the only place either is read");
                }

                // A loop's own keys are read once an iteration has ended, from within the group: its members are
                // read by their bare names there, as a member reads its siblings.
                var whole = Expression(entry, repeat.Until, "repeat.until", within: true);

                // An until is read once each iteration has ended, to say whether another is needed: one that reads none of
                // what the iterations produce says the same after every one of them.
                if (whole && !WorkflowExpressions.ReadsOf(repeat.Until).Any(read => ReadsOwnSteps(entry, read)))
                {
                    _problems.Add($"{where} repeats until '{repeat.Until}', which reads nothing its own steps produce; no iteration could change what it says, so the loop would stop after its first or run to its bound");
                }

                var blockingWhole = repeat.Blocking is not null && Expression(entry, repeat.Blocking, "repeat.blocking", within: true);
                var leftoverWhole = repeat.Leftover is not null && Expression(entry, repeat.Leftover, "repeat.leftover", within: true);

                Within(repeat.MaxIterations, 1, 20, $"{where}: repeat.maxIterations");
                Within(repeat.Rounds, 1, 20, $"{where}: repeat.rounds");
                Within(repeat.BlockersLimitRounds, 1, 20, $"{where}: repeat.blockersLimitRounds");

                if (repeat.BlockersUnbounded && _definition.Budget is null)
                {
                    _problems.Add($"{where}: repeat.blockersLimitRounds is false and the workflow has no budget: nothing would bound the loop");
                }

                if (repeat.DecideAfter is { } decide)
                {
                    // The deciding step is a direct step of the repeated group, one the schedule draws in its course:
                    // the loop reads its decision at that step's ending, so a step that never ends in the course of an
                    // iteration - a handler, a step raised by an issue, an always step - would leave it undecided.
                    if (entry.Step.Steps.FirstOrDefault(member => string.Equals(member.Name, decide, StringComparison.Ordinal)) is not { } decider)
                    {
                        _problems.Add($"{where} decides after '{decide}', which is no step of the group it repeats");
                    }
                    else if (decider.On is not null || decider.Raised is not null || decider.Always == true)
                    {
                        _problems.Add($"{where} decides after '{decide}', which never runs in the course of an iteration - {(decider.On is not null ? "a handler" : decider.Raised is not null ? "a step raised by an issue" : "an always step")} - so the loop would read its decision nowhere; name a step the schedule draws");
                    }
                    else if (_byPath.GetValueOrDefault($"{entry.Path}.{decide}") is { } deciding)
                    {
                        DecisionRules(entry, deciding, [("until", whole ? repeat.Until : null), ("blocking", blockingWhole ? repeat.Blocking : null), ("leftover", leftoverWhole ? repeat.Leftover : null)]);
                    }
                }
            }

            if (step.ForEach is not null)
            {
                var before = _problems.Count;

                Expression(entry, step.ForEach, "forEach");

                if (_problems.Count > before)
                {
                    _refusedForEach.Add(entry.Path);
                }
            }
        }

        /// <summary>
        /// V64 and V65, a repeat that decides after one of its steps (<c>decideAfter</c>): the loop reads its decision -
        /// its <c>until</c>, its <c>blocking</c>, its bound, and its <c>leftover</c> where it ends - where that step of the
        /// iteration ends, so what they read of the iteration is the deciding step and the steps it waits for, which have
        /// ended by then (V64): any other step of it may not have run, and would read nothing. And the iteration is ended
        /// where the loop ends there, so every other step of it runs before the deciding step - is one it waits for - or
        /// after it - waits for it (V65): one beside it, ordered neither way, could still be running when the loop ends,
        /// and run on in an iteration that has ended. An always step runs after its group's end, never in its course, and
        /// a step raised by an issue is refused in a group for itself; neither is held to the order.
        /// </summary>
        /// <param name="group">The repeated group.</param>
        /// <param name="deciding">The step it decides after, one of its own the schedule draws.</param>
        /// <param name="keys">The loop's keys read at the decision, each with its expression where it is written whole.</param>
        private void DecisionRules(Entry group, Entry deciding, IReadOnlyList<(string Key, string? Expression)> keys)
        {
            var decider = deciding.Step.Name;

            foreach (var (key, expression) in keys)
            {
                if (expression is null)
                {
                    continue;
                }

                foreach (var read in WorkflowExpressions.ReadsOf(expression).Where(read => !WorkflowExpressions.IsRoot(read.Root)))
                {
                    if (Resolve(group, read, within: true) is not { Step: var resolved } || !group.Owns(resolved) || MemberOf(group, resolved) is not { } member)
                    {
                        continue;
                    }

                    if (!ReferenceEquals(member, deciding) && !WaitsFor(deciding, member))
                    {
                        _problems.Add($"'{group.Path}.repeat.{key}' reads '{read.Whole}', and '{member.Path}' is neither '{decider}' nor a step it waits for: the loop decides where '{decider}' ends (decideAfter), and '{member.Step.Name}' may not have run by then - read what '{decider}' found, or what a step it waits for found");
                    }
                }
            }

            foreach (var member in _entries.Where(member => ReferenceEquals(member.Parent, group) && !ReferenceEquals(member, deciding) && member.Step.Always is not true && member.Step.Raised is null))
            {
                if (!WaitsFor(deciding, member) && !WaitsFor(member, deciding))
                {
                    _problems.Add($"group '{group.Path}' decides after '{decider}', and its step '{member.Step.Name}' neither waits for it nor is waited for by it: the loop may end where '{decider}' ends while '{member.Step.Name}' still runs beside it - make '{decider}' wait for it, or make it wait for '{decider}'");
                }
            }
        }

        /// <summary>The step declared directly in <paramref name="group"/> that is <paramref name="step"/> or holds it; null where the group does not hold it.</summary>
        private static Entry? MemberOf(Entry group, Entry step)
        {
            for (var at = step; at is not null; at = at.Parent)
            {
                if (ReferenceEquals(at.Parent, group))
                {
                    return at;
                }
            }

            return null;
        }

        /// <summary>
        /// Whether <paramref name="read"/>, in an expression of the group <paramref name="group"/>, reads what one of the
        /// group's own steps produces: a member's output or facts, or the previous iteration's.
        /// </summary>
        private bool ReadsOwnSteps(Entry group, WorkflowExpressions.Read read)
        {
            if (read.Root == "previous")
            {
                return true;
            }

            return !WorkflowExpressions.IsRoot(read.Root) && Resolve(group, read, within: true) is { } resolved && group.Owns(resolved.Step);
        }

        private void ReasonRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"step '{entry.Path}'";
            var resumesItself = string.Equals(step.Resume, step.Name, StringComparison.Ordinal);
            var resumesAnother = step.Resume is not null && !resumesItself;

            // The agent definition a reason step works by is the one at the root of its chain of work, read as its run reads
            // it (ResumeChain.Root): its own where it begins its own work - resuming no step, or itself - and else that of
            // the step whose work it goes on with. A root that names none is refused once, at the root, never again at
            // each step that goes on with it; a resume that leads to no reason step is refused below for that.
            if (!resumesAnother && ResumeChain.Root(_definition, entry.Path, step).Agent is null)
            {
                _problems.Add(resumesItself
                    ? $"{where} names no agent and resumes itself; a step that goes on with its own work works by the agent definition it began it with, which it names"
                    : $"{where} names no agent and resumes no step; a reason step runs an agent of its own, or goes on with the agent of the step it resumes");
            }

            if (step.Resume is not null)
            {
                var resumed = Scope(entry, step.Resume);

                if (resumed is null)
                {
                    _problems.Add($"{where} resumes '{step.Resume}', which is no step an enclosing group declares");
                }
                else if (resumed.Step.Type != "reason")
                {
                    _problems.Add($"{where} resumes '{step.Resume}', which is a {resumed.Step.Type ?? "group"} step; only a reason step leaves a conversation to go on with");
                }
                else if (resumed.Order > entry.Order)
                {
                    _problems.Add($"{where} resumes '{step.Resume}', which is declared after it; work cannot be gone on with before it is begun");
                }
                else if (resumesItself && !RunsAgain(entry))
                {
                    _problems.Add($"{where} resumes itself and runs once; a step goes on with its own work only where it runs again - inside a repeat, where a handler's then sends the run back to it, or as a step of the recovery");
                }
            }

            // Another's work runs in that step's agent, on its model and in its worktree: a step that goes on with it says
            // none of them, and writes what that step writes.
            if (resumesAnother && ResumedKeys(step) is [_, ..] named)
            {
                _problems.Add($"{where} resumes '{step.Resume}' and names {string.Join(", ", named)}; a step that goes on with another's work runs in that step's agent, on its model and in its worktree, and names none of agent, client, tier, model, access, in or writes");
            }

            if (step.ResumePrompt is not null && !resumesItself)
            {
                _problems.Add($"{where} says resumePrompt and does not resume itself; resumePrompt is what a step that goes on with its own work is asked with when it runs again, and any other step is asked with its prompt");
            }

            // V55: a step's search names the sources its web_search asks. One that goes on with another's work asks the
            // sources that step's search names, its conversation's tool listing them, and names none of its own. Whether the
            // step's agent has the web is its definition's to say, read with the definitions.
            if (step.Search is { } search)
            {
                if (resumesAnother)
                {
                    _problems.Add($"{where} resumes '{step.Resume}' and says search; a step that goes on with another's work asks the sources that step's search names, and names none of its own");
                }
                else
                {
                    _problems.AddRange(_search is { } sources
                        ? sources.ListProblems(search, $"the search of {where}", $"{where} searches")
                        : Web.Search.WebSearchSources.TwiceProblems(search, $"the search of {where}"));
                }
            }

            // V53: the graph a step asks is the run's own, made by a graphify-codebase step upstream of the step that
            // asks it - or, in the recovery, ended before it - and a step that goes on with another's work asks the
            // graph that step's conversation holds, saying none of its own. V54: each graph key only where it does
            // something - the cap and the pack's seed only where a pack is given, the narrowing of reads only under the
            // tool, and the tool only where the step's agent reads.
            GraphRules(entry, where, resumesAnother);

            if (step.Independent is true && step.Resume is not null)
            {
                _problems.Add($"{where} is independent and resumes '{step.Resume}'; independence is never being given the author's work, and resuming is going on with exactly that");
            }

            // A step that goes on with another's work has that step's access, worktree and writes, and was refused above for
            // naming any of them.
            if (!resumesAnother)
            {
                AccessRules(entry, where);
            }

            if (step.AskRounds is not null && step.Ask is not true)
            {
                _problems.Add($"{where} says askRounds without ask; how often a step may ask is a step that asks's own bound");
            }

            Within(step.AskRounds, 1, 5, $"{where}: askRounds");
            Within(step.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, $"{where}: backoffSeconds");
            ModelRules(step.Tier, step.Model, where);
            PreExistingRules(entry, where);
        }

        /// <summary>A reasoning step's own access: one that writes works in the worktree it names, and one that reads names none and writes nothing.</summary>
        private void AccessRules(Entry entry, string where)
        {
            var step = entry.Step;

            if (step.Access == "write")
            {
                if (step.In is null)
                {
                    _problems.Add($"{where} writes and names no 'in'; a writing step works in an agent's worktree, and the create-agent step that made it is the one to name");
                }
                else
                {
                    WorksIn(entry, step.In);
                }
            }
            else
            {
                if (step.In is not null)
                {
                    _problems.Add($"{where} reads and names 'in'; a reading step has no worktree to work in, and reads the main tree");
                }

                if (step.Writes is not null)
                {
                    _problems.Add($"{where} names writes and only reads; a step that writes says access: write and names the create-agent step it works in");
                }
            }

            if (step.Writes?.Expression is { } writes)
            {
                Expression(entry, writes, "writes");
            }
            else
            {
                // V32's other half, where the key lives: a reason step's written-out writes are patterns, and a
                // pattern that could name nothing is a typo the run would refuse at its start.
                foreach (var pattern in step.Writes?.Written ?? [])
                {
                    if (GlobPattern.Problem(pattern) is { } typo)
                    {
                        _problems.Add($"{where} names the pattern '{pattern}' in writes, and it {typo}");
                    }
                }
            }
        }

        /// <summary>
        /// V53 and V54 over a reason step's four graph keys: the graph a step asks is the run's own, made by a
        /// graphify-codebase step upstream of it - or, in the recovery, declared (<see cref="GraphifyUpstream"/>) - and a
        /// step that goes on with another's work asks the graph that step's conversation holds, its mode that step's,
        /// saying none of its own while it may say <c>graphFrom</c> and <c>graphPromptTokens</c> for its own pack; each
        /// key only where it does something, judged by the mode the run resolves - the pack's cap and seed only where a
        /// pack is given, the narrowing of reads only under the tool, and the tool only where the step's agent reads -
        /// and a seed that names a step one the step can see, never a step named like a seed's word.
        /// </summary>
        /// <param name="entry">The reason step.</param>
        /// <param name="where">The step's path, as its problems name it.</param>
        /// <param name="resumesAnother">Whether the step goes on with another step's work.</param>
        private void GraphRules(Entry entry, string where, bool resumesAnother)
        {
            var step = entry.Step;
            var upstream = GraphifyUpstream(entry);
            var mode = GraphModeOf(entry);

            if (step.Graph is "map" or "tools" && !upstream)
            {
                _problems.Add($"{where} says graph: {step.Graph}, and no graphify-codebase step is upstream of it; the graph a step asks is the run's own, made upstream of it - or, in the recovery, ended before it");
            }

            if (resumesAnother && step.Graph is not null)
            {
                _problems.Add($"{where} resumes '{step.Resume}' and says graph; a step that goes on with another's work asks the graph that step's conversation holds, and may say graphFrom and graphPromptTokens for its own pack");
            }

            if (resumesAnother && step.GraphReads is not null)
            {
                _problems.Add($"{where} resumes '{step.Resume}' and says graphReads; the narrowing of a step's reads is its conversation's own, begun where the step it goes on with began");
            }

            if (step.GraphReads is not null && mode != "tools")
            {
                _problems.Add($"{where} says graphReads and its graph is {mode}; the narrowing of reads holds a step's whole-file reads to the graph only where it is given the tool (graph: tools)");
            }

            if (step.Graph == "tools" && WorksAs(entry).Definition is { } definition && !definition.May.Contains("read"))
            {
                _problems.Add($"{where} says graph: tools and its agent does not read; the graph tool is an agent's read turned to the graph, and an agent that cannot read is given the map alone");
            }

            var pack = mode != "off";

            if (step.GraphPromptTokens is not null && !pack)
            {
                _problems.Add($"{where} says graphPromptTokens and no pack is given it; the cap does nothing where a step asks no graph");
            }

            if (step.GraphFrom is { } from)
            {
                if (!pack)
                {
                    _problems.Add($"{where} says graphFrom and no pack is given it; where a pack starts from does nothing where a step asks no graph");
                }
                else if (WorkflowStep.GraphFromWords.Contains(from, StringComparer.Ordinal))
                {
                    // The word means the seed: a step named like it is never the one a graphFrom names, and is said so
                    // where the name finds one, rather than seeding from the word in silence.
                    if (Scope(entry, from) is { Step.Type: "reason" } named)
                    {
                        _problems.Add($"{where} says graphFrom: '{from}', which is the pack's own seed and names the reason step '{named.Path}' too; a step named {string.Join(" or ", WorkflowStep.GraphFromWords)} cannot seed a pack - rename it to seed from it");
                    }
                }
                else
                {
                    if (Scope(entry, from) is not { } seeded)
                    {
                        _problems.Add($"{where} says graphFrom: '{from}', which is no step an enclosing group declares");
                    }
                    else if (seeded.Step.Type != "reason")
                    {
                        _problems.Add($"{where} says graphFrom: '{from}', which is a {seeded.Step.Type ?? "group"} step; a working set is what a reason step's own work leaves");
                    }
                }
            }

            Within(step.GraphPromptTokens, 0, WorkflowStep.MostGraphPromptTokens, $"{where}: graphPromptTokens");
        }

        /// <summary>
        /// The step's use of the run's graph with its default resolved, as the run resolves it (<see cref="WorkflowStep.GraphOf"/>):
        /// by the <c>graph</c> of the step its chain of work began with (<see cref="ResumeChain.Root"/>, whose walk stops at
        /// a ring) and that step's agent, with the step's own upstream graphify step - a step that goes on with another's
        /// work asks the graph that step's conversation holds.
        /// </summary>
        /// <param name="entry">The reason step.</param>
        private string GraphModeOf(Entry entry)
        {
            var (root, definition) = WorksAs(entry);

            return WorkflowStep.GraphOf(root, definition, GraphifyUpstream(entry));
        }

        /// <summary>
        /// Whether a <c>graphify-codebase</c> step is upstream of <paramref name="entry"/> - a step its waits reach,
        /// through its own dependencies and those of every group around it (<see cref="FlowGraph.Waits"/>) - or, where
        /// the step is the recovery's own, one is declared. The run gives a step of the recovery a graph only where a
        /// graphify step of the run has ended done before it starts, which no definition can say; one declared is the
        /// check's approximation of that, the most the run could give.
        /// </summary>
        /// <param name="entry">The step.</param>
        private bool GraphifyUpstream(Entry entry)
            => InRecovery(entry)
                ? _entries.Any(other => other.Step.Type == "graphify-codebase")
                : _entries.Where(other => other.Step.Type == "graphify-codebase").Any(other => FlowGraph.Waits(_definition, entry.Path, other.Path));

        /// <summary>
        /// The keys <paramref name="step"/> names that a step going on with another's work takes from that step - its
        /// agent and how the agent's model is chosen, its access, its worktree and what it writes - in the order the
        /// refusal lists them.
        /// </summary>
        /// <param name="step">The step.</param>
        private static IReadOnlyList<string> ResumedKeys(WorkflowStep step)
            => [.. new (string Key, bool Named)[]
                {
                    ("agent", step.Agent is not null),
                    ("client", step.Client is not null),
                    ("tier", step.Tier is not null),
                    ("model", step.Model is not null),
                    ("access", step.Access is not null),
                    ("in", step.In is not null),
                    ("writes", step.Writes is not null),
                }.Where(key => key.Named).Select(key => key.Key)];

        /// <summary>
        /// Whether <paramref name="entry"/> can run again, and so go on with its own work: it is inside a repeat, a
        /// handler's <c>then</c> can send the run back to it or to a group holding it, or it is a step of the recovery, which
        /// a step that fails again goes back to.
        /// </summary>
        private bool RunsAgain(Entry entry)
            => RepeatAround(entry) is not null
                || InRecovery(entry)
                || _entries.Any(handler => handler.Step.Then is { } then
                    && Scope(handler, then) is { } target
                    && (ReferenceEquals(target, entry) || target.Owns(entry)));

        /// <summary>Checks the create-agent step a step names with <c>in</c>: one it sees, that makes an agent, declared before it.</summary>
        private void WorksIn(Entry entry, string name)
        {
            var where = $"step '{entry.Path}'";

            if (Scope(entry, name) is not { } worksIn)
            {
                _problems.Add($"{where} works in '{name}', which is no step an enclosing group declares");
            }
            else if (worksIn.Step.Type != "create-agent")
            {
                _problems.Add($"{where} works in '{name}', which is a {worksIn.Step.Type ?? "group"} step; a worktree is made by a create-agent step");
            }
            else if (worksIn.Order > entry.Order)
            {
                _problems.Add($"{where} works in '{name}', which is declared after it; a worktree cannot be worked in before it is made");
            }
            else
            {
                WaitsForTheAgent(entry, worksIn, "works in");
            }
        }

        /// <summary>
        /// A step that works in or on the agent <paramref name="create"/> makes waits for it to end: through its own
        /// dependencies, or those of a group around it, as far as they go. One that does not could start before the agent
        /// is made - its tree not there, and its records placed before the agent they belong to.
        /// </summary>
        private void WaitsForTheAgent(Entry entry, Entry create, string does)
        {
            // An alternative to the step that builds a work item runs in its place, with its dependencies (V44): it - and,
            // where it is a group, every step inside it, as inside any group - waits for what the builder waits for,
            // beside what its own dependencies reach. A step of a used workflow whose gate this use closes never starts:
            // nothing of it works in the agent. One whose gate the use opens starts as any step does, and waits.
            var waits = FlowGraph.Waits(_definition, entry.Path, create.Path)
                || (_definition.Amend is { } amend
                    && new[] { amend.WhenChangeScope, amend.WhenRemoveScope }.Any(alternative => entry.Path == alternative || entry.Path.StartsWith(alternative + ".", StringComparison.Ordinal))
                    && FlowGraph.Waits(_definition, amend.WhenAddScope, create.Path))
                || HeldOffByItsUse(entry);

            if (!waits)
            {
                _problems.Add($"step '{entry.Path}' {does} the agent '{create.Path}' makes, and does not wait for it: name '{create.Step.Name}', or a step that depends on it, under dependsOn - a step that works in or on an agent starts once the agent is made");
            }
        }

        /// <summary>A tier and a model at one level: the model would be used, and the tier never read.</summary>
        private void ModelRules(string? tier, string? model, string where)
        {
            if (tier is not null && model is not null)
            {
                _problems.Add($"{where} names a model and a tier: the model would be used and the tier never read");
            }
        }

        private void FoldRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"step '{entry.Path}'";

            if (step.Of is not null && step.Others is not null)
            {
                _problems.Add($"{where} names 'of' and 'others'; it works on one agent, or on every other one, and never both");
            }

            if (step.Of is null && step.Others is null && step.Type is "refresh" or "rebase")
            {
                _problems.Add($"{where} names neither 'of' nor 'others'; it works on one agent, or on every live one but the agent 'others' names");
            }

            foreach (var (key, named) in new[] { ("of", step.Of), ("others", step.Others) })
            {
                if (named is null)
                {
                    continue;
                }

                if (Scope(entry, named) is not { } agent)
                {
                    _problems.Add($"{where} names '{named}' as its {key}, which is no step an enclosing group declares");
                }
                else if (agent.Step.Type != "create-agent")
                {
                    _problems.Add($"{where} names '{named}' as its {key}, which is a {agent.Step.Type ?? "group"} step; an agent is made by a create-agent step");
                }
                else if (key == "of")
                {
                    WaitsForTheAgent(entry, agent, "works on");
                }
            }

            if (step.DiscardUncommitted is true && (step.Allow is not null || step.Within is not null))
            {
                _problems.Add($"{where} discards what is not committed and says what its fold may carry; a deletion that discards folds nothing, so allow and within would be read by nothing");
            }

            foreach (var (key, list) in new[] { ("within", step.Within), ("paths", step.Paths), ("except", step.Except), ("stale", step.Stale), ("merge", step.Merge) })
            {
                if (list?.Expression is { } expression)
                {
                    Expression(entry, expression, key);
                }
            }

            if (step.Allow?.New?.Expression is { } allowed)
            {
                Expression(entry, allowed, "allow.new");
            }

            if (step.Allow?.Merge?.Expression is { } merged)
            {
                Expression(entry, merged, "allow.merge");
            }

            // V32's patterns: within is a pattern list, and a pattern that could name nothing is a typo the run
            // would refuse at its start. (writes is a reason step's key; its patterns are checked there.)
            foreach (var pattern in step.Within?.Written ?? [])
            {
                if (GlobPattern.Problem(pattern) is { } typo)
                {
                    _problems.Add($"{where} takes within: the pattern '{pattern}' {typo}");
                }
            }

            // V29: the fold family's keys, read to the requests the verbs themselves take, each rule's wording the
            // request's and spelt in a step's keys, written out here, when the definition is loaded, so a key that
            // names what the run would refuse at its start is refused before a run begins. That allow's settled, lost
            // and over and a rebase's settled are written out is the reader's own refusal, where the file is read.
            if (step.Allow is { } allow && new FoldAllowances
                {
                    Merge = [.. allow.Merge?.Written ?? []],
                    Settled = allow.Settled,
                    New = [.. allow.New?.Written ?? []],
                    AcceptLost = allow.AcceptLost,
                    AcceptOver = allow.AcceptOver,
                }.Problem(OptionSpelling.Key) is { } spelling)
            {
                _problems.Add($"{where} takes allow: {spelling}");
            }

            if (step.Type == "refresh")
            {
                var request = new RefreshRequest
                {
                    Paths = [.. step.Paths?.Written ?? []],
                    All = step.All is true,
                    Except = [.. step.Except?.Written ?? []],
                    Stale = [.. step.Stale?.Written ?? []],
                    Merge = [.. step.Merge?.Written ?? []],
                };

                // All beside paths an expression yields is the request's own rule, said in its words: the request built
                // from what is written out never holds those paths, which are known only when the step runs.
                if ((step.Paths?.Expression is not null && step.All is true
                        ? RefreshRequest.AllBesidePathsProblem(OptionSpelling.Key)
                        : request.Problem(OptionSpelling.Key)) is { } refresh)
                {
                    _problems.Add($"{where} takes refresh: {refresh}");
                }
            }

            if (step.Type == "rebase" && new RebaseRequest
                {
                    Merge = [.. step.Merge?.Written ?? []],
                    Settled = [.. step.Settled ?? []],
                }.Problem(OptionSpelling.Key) is { } rebase)
            {
                _problems.Add($"{where} takes a rebase: {rebase}");
            }
        }

        private void PauseRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"step '{entry.Path}'";

            if (step.Options.Count == 0)
            {
                _problems.Add($"{where} offers no options; a question with nothing to answer leaves a person typing into a run that reads nothing");
            }

            var options = new HashSet<string>(step.Options, StringComparer.Ordinal);

            if (options.Count != step.Options.Count)
            {
                _problems.Add($"{where} offers one option more than once");
            }

            foreach (var text in step.Text.Where(option => !options.Contains(option)))
            {
                _problems.Add($"{where} says the option '{text}' takes a text, and offers no such option");
            }

            if (step.Questions is not null)
            {
                Expression(entry, step.Questions, "questions");
            }

            foreach (var before in step.PreAnswer)
            {
                var whole = Expression(entry, before.When, "preAnswer.when");

                if (!options.Contains(before.Answer))
                {
                    _problems.Add($"{where} is answered beforehand with '{before.Answer}', which is not one of the options it offers");
                }
                else if (step.Text.Contains(before.Answer, StringComparer.Ordinal) && before.Text is null)
                {
                    _problems.Add($"{where} is answered beforehand with '{before.Answer}' and no text; '{before.Answer}' needs a text with it, and the answer would carry none");
                }
                else if (!step.Text.Contains(before.Answer, StringComparer.Ordinal) && before.Text is not null)
                {
                    _problems.Add($"{where} is answered beforehand with '{before.Answer}' and a text; '{before.Answer}' takes no text, and this one would be read by nothing");
                }

                // A pre-answer is what the person said with the run, so it is found in what they gave: an input its when
                // reads, or words it quotes from one. One that reads neither would answer whatever was typed.
                if (before.Quote is null
                    && whole
                    && !WorkflowExpressions.ReadsOf(before.When).Any(read => read.Root == "input"))
                {
                    _problems.Add($"{where} is answered beforehand by an entry that reads no input and quotes none: a pre-answer is what the person gave with the run, and an answer the configuration gives whatever was typed is written under answers");
                }

                if (before.Quote is { } quote)
                {
                    Expression(entry, quote.Text, "preAnswer.quote.text");

                    if (!_definition.InputsByName.TryGetValue(quote.Input, out var quoted))
                    {
                        _problems.Add($"{where} quotes the input '{quote.Input}', which the workflow does not declare");
                    }
                    else if (quoted.Kind != FlowInput.TextKind)
                    {
                        _problems.Add($"{where} quotes the input '{quote.Input}', a {quoted.Kind} input; a quote is looked for in the words a person gave, which a text input holds");
                    }
                }
            }
        }

        private void Amend()
        {
            if (_definition.Amend is not { } amend)
            {
                return;
            }

            if (amend.Input is not null && !_definition.InputsByName.ContainsKey(amend.Input))
            {
                _problems.Add($"amend.input names '{amend.Input}', which the workflow does not declare as an input");
            }

            if (amend.Before is not null)
            {
                if (_byPath.TryGetValue(amend.Before, out var before))
                {
                    if (before.ParentPath is not null)
                    {
                        _problems.Add($"amend.before names '{amend.Before}', which is a step inside the group '{before.ParentPath}'; a change is planned again only while the flow's own stages still have a step to plan it");
                    }
                }
                else
                {
                    _problems.Add($"amend.before names '{amend.Before}', which is no step of the flow");
                }
            }

            if (_byPath.TryGetValue(amend.Step, out var planner))
            {
                if (planner.Step.Type != "reason")
                {
                    _problems.Add($"amend.step names '{amend.Step}', a {planner.Step.Type} step; a plan is updated by a reasoning agent, and no other step holds the conversation it is updated in");
                }

                // The updated plan is written once everything may have run: nothing is behind the scope's own reads.
                Expression(planner with { Order = AfterEveryStep }, amend.Scope, "amend.scope");

                if (amend.Then is not null && Sibling(planner, amend.Then) is null)
                {
                    _problems.Add($"amend.then names '{amend.Then}', which is no step declared beside '{amend.Step}'");
                }
            }
            else
            {
                _problems.Add($"amend.step names '{amend.Step}', which is no step of the flow");
            }

            foreach (var (key, named) in new[] { ("whenAddScope", amend.WhenAddScope), ("whenChangeScope", amend.WhenChangeScope), ("whenRemoveScope", amend.WhenRemoveScope) })
            {
                if (!_byPath.ContainsKey(named))
                {
                    _problems.Add($"amend.{key} names '{named}', which is no step of the flow");
                }
            }

            foreach (var (key, named) in new[] { ("step", amend.Step), ("before", amend.Before), ("whenAddScope", amend.WhenAddScope), ("whenChangeScope", amend.WhenChangeScope), ("whenRemoveScope", amend.WhenRemoveScope) })
            {
                if (named is not null && (named == FlowScope.Recovery || named.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal)))
                {
                    _problems.Add($"amend.{key} names '{named}', a step of the recovery; a plan update is planned and placed by the flow's own steps");
                }
            }

            Within(amend.ChangedAnswerMinutes, 1, 60, "amend.changedAnswerMinutes");
            Within(amend.MaxAddedIterations, 0, 10, "amend.maxAddedIterations");

            PlanUpdateRules(amend);
        }

        private void Outputs()
        {
            // An output declared twice was refused where the file was read.
            foreach (var output in _definition.Outputs)
            {
                DeclaredNameRules(output.Name, "output");

                // An output is read when the run has ended, at the flow's top level: the whole flow is behind it. It is
                // named as the file names the key, 'outputs.<name>.from'.
                Expression(new Entry(new WorkflowStep { Name = output.Name }, $"outputs.{output.Name}", AfterEveryStep, null), output.From, "from");
            }
        }

        /// <summary>
        /// Checks one answer a configuration gives a question kind beforehand, given where it is written: the
        /// workflow's own <c>answers</c> (<paramref name="owner"/> <see langword="null"/>), a group's or a step's.
        /// </summary>
        private void AnswerRules(string kind, FlowAnswer answer, string where, Entry? owner)
        {
            if (!QuestionKinds.Contains(kind, StringComparer.Ordinal))
            {
                _problems.Add($"{where} answers the kind '{kind}', which is no question kind. Known: {string.Join(", ", QuestionKinds.Select(known => $"'{known}'"))}.");
                return;
            }

            if (string.IsNullOrWhiteSpace(answer.Answer))
            {
                _problems.Add($"{where} answers the kind '{kind}' with nothing; an answer is spelled as the question's options spell it");
                return;
            }

            switch (kind)
            {
                case "foldLostRows" or "foldOverCells":
                    _problems.Add($"{where} answers '{kind}': a lost or an over cell at a fold is a person's to accept, and no line of a configuration accepts it");
                    return;

                case "failure":
                    _problems.Add($"{where} answers 'failure': what a step's failure does is its onFailure, which answers that question beforehand");
                    return;

                case "ask":
                    _problems.Add($"{where} answers 'ask': whether a step may put its questions to a person is the step's own ask");
                    return;

                case "runBudget" when owner is not null:
                    _problems.Add($"{where} answers 'runBudget': the run's budget is the workflow's, and only the workflow's own answers answer for it");
                    return;

                case "pause":
                    if (owner?.Step.Type != "pause")
                    {
                        _problems.Add($"{where} answers 'pause': a pause step's question is answered on that step alone");
                    }
                    else if (owner.Step.Person is true)
                    {
                        _problems.Add($"{where} answers 'pause' and says person: the step is one only a person answers");
                    }
                    else if (!owner.Step.Options.Contains(answer.Answer, StringComparer.Ordinal))
                    {
                        _problems.Add($"{where} answers 'pause' with '{answer.Answer}', which is not one of the options the step offers");
                    }
                    else
                    {
                        PauseAnswerGivenOnce(answer, where);
                    }

                    return;
            }

            if (RaisedBy.TryGetValue(kind, out var raises) && !Raises(owner, raises))
            {
                _problems.Add(owner is null
                    ? $"{where} answers '{kind}', which no step of it raises; the answer would be read by nothing"
                    : owner.Step.IsGroup
                        ? $"{where} answers '{kind}', which no step in it raises; the answer would be read by nothing"
                        : $"{where} answers '{kind}', which it never raises; the answer would be read by nothing");
                return;
            }

            var offered = AnswersOf[kind];

            if (!offered.Contains(answer.Answer, StringComparer.Ordinal))
            {
                _problems.Add($"{where} answers '{kind}' with '{answer.Answer}', which it does not offer. Its answers: {string.Join(", ", offered.Select(known => $"'{known}'"))}.");
                return;
            }

            if (answer.Times is null && GoesOn.Contains(answer.Answer, StringComparer.Ordinal))
            {
                _problems.Add($"{where} answers '{kind}' with {answer.Answer} and no times: say how many times, from 1 to 20");
            }

            if (answer.Answer == "skip" || answer.Then == "skip")
            {
                // skip is offered only where the step is skippable, so the engine gives it nowhere else: every step this
                // answer would answer - the nearest answers that name the kind - must be one.
                foreach (var answered in Answered(owner, kind, raises).Where(answered => answered.Step.Skippable is not true))
                {
                    _problems.Add($"{where} answers '{kind}' {(answer.Answer == "skip" ? "with skip" : "then skip")}, and '{answered.Path}' is no skippable step; skip ends a step skipped only where it says skippable");
                }
            }

            if (answer.Times is not null)
            {
                Within(answer.Times, 1, 20, $"{where}: the times of the answer to '{kind}'");

                if (!GoesOn.Contains(answer.Answer, StringComparer.Ordinal))
                {
                    _problems.Add($"{where} answers '{kind}' with {answer.Answer} {answer.Times} times; times count an answer after which the same question can come again - more, retry or compact - and {answer.Answer} ends the matter");
                }
            }

            if (answer.Then is { } then)
            {
                if (answer.Times is null)
                {
                    _problems.Add($"{where} answers '{kind}' then {then} with no times; then is what is answered once the counted answers are spent");
                }

                if (!offered.Contains(then, StringComparer.Ordinal) || !Ends.Contains(then, StringComparer.Ordinal))
                {
                    _problems.Add($"{where} answers '{kind}' then '{then}', which is not one of its answers that end the matter: {string.Join(", ", offered.Where(known => Ends.Contains(known, StringComparer.Ordinal)).Select(known => $"'{known}'"))}");
                }
            }
        }

        /// <summary>
        /// Whether a kind <paramref name="raises"/> describes can come where an answer to it is written: at the step that
        /// writes it, on a group at the group or any step in it, and on the workflow (<paramref name="owner"/>
        /// <see langword="null"/>) at any step of it, the recovery's included.
        /// </summary>
        private bool Raises(Entry? owner, Func<WorkflowStep, string?, bool> raises)
            => _entries.Any(entry => !ReferenceEquals(entry, _recover)
                && (owner is null || ReferenceEquals(entry, owner) || owner.Owns(entry))
                && raises(entry.Step, EffectiveOnFailure(entry)?.Policy));

        /// <summary>
        /// The steps an answer written at <paramref name="owner"/> answers, as the engine finds the nearest <c>answers</c>
        /// that names the kind: each step that raises it - the owner itself, a step in it, any step for the workflow's own -
        /// with no answers of its own, or of a group between it and the owner, that name the kind first.
        /// </summary>
        private IEnumerable<Entry> Answered(Entry? owner, string kind, Func<WorkflowStep, string?, bool>? raises)
            => _entries.Where(entry => !ReferenceEquals(entry, _recover)
                && (owner is null || ReferenceEquals(entry, owner) || owner.Owns(entry))
                && (raises is null || raises(entry.Step, EffectiveOnFailure(entry)?.Policy))
                && !AnsweredNearer(entry, owner, kind));

        /// <summary>Whether <paramref name="entry"/>, or a group around it inside <paramref name="owner"/>, names the kind in answers of its own, which the engine reads first.</summary>
        private static bool AnsweredNearer(Entry entry, Entry? owner, string kind)
        {
            for (var at = entry; at is not null && !ReferenceEquals(at, owner); at = at.Parent)
            {
                if (at.Step.Answers.ContainsKey(kind))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Checks every template key of one typed step, against the step's own place in the flow.</summary>
        private void Templates(Entry entry, WorkflowStepTypes.StepType type)
        {
            var step = entry.Step;

            foreach (var key in type.TemplateKeys)
            {
                foreach (var text in FlowStepTemplates.Texts(step, key))
                {
                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _problems.Add($"'{entry.Path}.{key}' is empty; a template is the text a step hands on, and an empty one hands on nothing");
                        continue;
                    }

                    if (WorkflowExpressions.TemplateSyntaxProblem(text) is { } broken)
                    {
                        _problems.Add($"'{entry.Path}.{key}' is not written whole: {broken}, and nothing it reads past there would be checked");
                        continue;
                    }

                    foreach (var read in WorkflowExpressions.TemplateReads(text))
                    {
                        TemplateRead(entry, read, $"{entry.Path}.{key}");
                    }
                }
            }
        }

        /// <summary>
        /// Checks one expression: that it says something, is written whole, and reads only the flow's own names - read
        /// from where <paramref name="entry"/> sits, or from within the group itself where <paramref name="within"/>
        /// says its loop reads what its iterations produced. Answers whether it was whole, so that a rule over what it
        /// reads is asked only of one that parses.
        /// </summary>
        private bool Expression(Entry entry, string expression, string key, bool within = false)
        {
            if (string.IsNullOrWhiteSpace(expression))
            {
                _problems.Add($"'{entry.Path}.{key}' is empty; an expression is read for what it says, and an empty one says nothing");
                return false;
            }

            if (WorkflowExpressions.SyntaxProblem(expression) is { } broken)
            {
                _problems.Add($"'{entry.Path}.{key}' is not written whole: {broken}, and nothing it reads past there would be checked");
                return false;
            }

            // The grammar itself, beside wholeness: what the engine will one day read must parse today, or the
            // refusal surfaces when a run is already going instead of when the definition is loaded.
            if (FlowExpression.GrammarProblem(expression) is { } grammar)
            {
                _problems.Add($"'{entry.Path}.{key}' is not written whole: {grammar}, and nothing past there would be checked");
                return false;
            }

            var before = _problems.Count;

            foreach (var read in WorkflowExpressions.ReadsOf(expression))
            {
                CheckRead(entry, read, $"{entry.Path}.{key}", within);
            }

            // Typed where every name it reads is one the definition holds: a read already refused is not refused again
            // for what it would have been.
            if (_problems.Count == before)
            {
                Types(entry, expression, key, within);
            }

            return true;
        }

        /// <summary>
        /// Checks one read - of an expression or of a template - against the steps, inputs and params
        /// the definition declares, and the names only some prompts read against the text it is read in
        /// (<paramref name="readIn"/>).
        /// </summary>
        private void CheckRead(Entry entry, WorkflowExpressions.Read read, string what, bool within = false, ReadIn readIn = ReadIn.Key)
        {
            var segments = read.Names;

            switch (read.Root)
            {
                case "input":
                    if (segments.Count < 2)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', which names no input; read 'input.<name>'");
                        return;
                    }

                    if (UseOf(entry) is { Step.Use: { } use })
                    {
                        // Inside a used workflow, an input is one the called workflow declares - what the use fills
                        // with - never one of the calling run's.
                        if (!use.Inputs.Any(input => string.Equals(input.Name, segments[1], StringComparison.Ordinal)))
                        {
                            _problems.Add($"'{what}' reads '{read.Whole}', and the workflow '{use.Name}' declares no input '{segments[1]}'");
                        }

                        return;
                    }

                    if (!_definition.InputsByName.ContainsKey(segments[1]))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and the workflow declares no input '{segments[1]}'");
                    }

                    return;

                case "param":
                    if (segments.Count < 2)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', which names no param; read 'param.<name>'");
                        return;
                    }

                    if (UseOf(entry) is { Step.Use: { } used })
                    {
                        if (!used.Params.Any(parameter => string.Equals(parameter.Name, segments[1], StringComparison.Ordinal)))
                        {
                            _problems.Add($"'{what}' reads '{read.Whole}', and the workflow '{used.Name}' declares no param '{segments[1]}'");
                        }

                        return;
                    }

                    if (!_definition.ParamsByName.ContainsKey(segments[1]))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and the workflow declares no param '{segments[1]}'");
                    }

                    return;

                case "item":
                    // The work item of the nearest forEach around the reader, inside the world it reads in: a step of a
                    // used workflow reads its own forEach's item, never one of the workflow that called it (V61).
                    if (Inside(entry, group => group.ForEach is not null))
                    {
                        return;
                    }

                    _problems.Add(Nearest(entry, group => group.ForEach is not null) is { } forEach
                        ? Beyond(what, read, $"the work item of the forEach '{forEach.Path}' is")
                        : $"'{what}' reads '{read.Whole}', whose 'item' names the work item of a forEach group, and no group holding '{entry.Path}' runs one");

                    return;

                case "previous":
                    {
                        // The iteration before this one: readable inside a repeat group, whose loop's
                        // own keys (within) are read once an iteration has ended, and whose members
                        // name one another as the iteration before theirs. A repeat group's other
                        // keys are read before its first iteration, so only a group around it
                        // answers for them. The step it names is found as a bare name is, from where
                        // it is read, and is one the repeat holds - inside the world it reads in: a step
                        // of a used workflow reads its own repeat's iterations, never one of the workflow
                        // that called it (V61).
                        var from = within ? entry : entry.Parent;
                        var repeated = RepeatAround(from, UseOf(entry));

                        if (repeated is null)
                        {
                            _problems.Add(RepeatAround(from, boundary: null) is { } loop
                                ? Beyond(what, read, $"the iteration of the repeat '{loop.Path}' is")
                                : $"'{what}' reads '{read.Whole}', whose 'previous' names the iteration of a repeat group before this one, and no group holding '{entry.Path}' runs one");
                            return;
                        }

                        if (segments.Count > 1
                            && FlowScope.Resolve(segments[1], _definition, ScopeOf(entry, within)) is { } beyond
                            && UseOf(entry) is { } world
                            && _byPath.GetValueOrDefault(beyond.Path) is { } reached
                            && !world.Owns(reached))
                        {
                            _problems.Add(Beyond(what, read, $"'{beyond.Path}' is"));
                            return;
                        }

                        if (segments.Count > 1
                            && !(FlowScope.Resolve(segments[1], _definition, ScopeOf(entry, within)) is { } named
                                && _byPath.GetValueOrDefault(named.Path) is { } held
                                && repeated.Owns(held)))
                        {
                            // A step the repeat holds deeper than a bare name reaches from here is read by its path
                            // from the repeat's own members, which every reader inside it sees by bare name.
                            var deeper = FlowScope.Inside(segments[1], _definition, repeated.Path);

                            _problems.Add(deeper is []
                                ? $"'{what}' reads '{read.Whole}', and '{segments[1]}' is no step of the group '{repeated.Path}' repeats"
                                : $"'{what}' reads '{read.Whole}', and '{segments[1]}' is a step of the group '{repeated.Path}' repeats, nested deeper than a bare name reaches from here: read {string.Join(" or ", deeper.Select(found => $"'previous.{found.Path[(repeated.Path.Length + 1)..]}'"))}");
                        }

                        return;
                    }

                case "run":
                    // How the run stands without its always steps is an always step's to read, so that none reads its own
                    // effect; a member of an always group is one.
                    if (segments.Count > 1
                        && WorkflowExpressions.RunStatuses.Contains(segments[1], StringComparer.Ordinal)
                        && entry.Step.Always is not true
                        && AlwaysAround(entry) is null)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', how the run stands without its always steps, and '{entry.Path}' is neither an always step nor in an always group; only an always step reads how the rest of the flow came to, so that none reads its own effect");
                    }

                    return;

                case "carried":
                    if (!string.Equals(entry.Step.Type, "commit", StringComparison.Ordinal))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', whose 'carried' says what the checks upstream of a commit checked, and '{entry.Path}' is no commit step");
                    }

                    return;

                case "it":
                    if (!read.InsideAny)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', whose 'it' names the member an any(...) test reads, and this read is in none");
                    }

                    return;

                case AgentSkills.Root:
                    // A reason step's prompt takes a skill's text; whether the skill is there and can be used is the
                    // lookup's own check, over the prompt again with the place and line each name stands on.
                    if (readIn == ReadIn.StepPrompt)
                    {
                        return;
                    }

                    _problems.Add(readIn == ReadIn.Key
                        ? $"'{what}' names a skill, and a skill's text is a prompt's alone; the step's own keys read the run's names, never a skill"
                        : $"'{what}' names {{{read.Whole}}}: a skill is text for an agent, and only a reason step's prompt takes one");
                    return;

                case "recommendations" or "question" or "status" or "update":
                    if (PromptAloneUnread(entry, read, readIn) is { } unread)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}': {unread}");
                    }

                    return;

                case "text":
                {
                    if (PromptAloneUnread(entry, read, readIn) is { } alone)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}': {alone}");
                        return;
                    }

                    // A text is named by one segment alone - {text.<name>} - and the name is one the workflow that
                    // reads it declares: the nearest use around the reading step that declares it, and else the run.
                    if (read.Names is not ["text", var named])
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and a text is read by its name alone: {{text.<name>}} with nothing under it");
                        return;
                    }

                    var (owner, texts) = TextsFor(entry, named);

                    if (texts is null)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and the workflow '{owner ?? _definition.Name}' declares no text '{named}'");
                    }

                    return;
                }

                case "piece":
                    PieceRead(entry, read, what);
                    return;

                case "source":
                    // The step whose ending raised a handler around the reader, inside the world it reads in: a step of a
                    // used workflow reads its own handlers' sources, never that of a handler of the workflow that called
                    // it (V61). The failure a recovery puts right is read by the recovery's own steps alone - a use the
                    // recovery holds is handed it through an input, as every fact of the caller.
                    if (Inside(entry, scope => scope.On is not null) || InOwnRecovery(entry) || entry.Step.Raised is not null)
                    {
                        return;
                    }

                    _problems.Add(Nearest(entry, scope => scope.On is not null) is { } handler
                        ? Beyond(what, read, $"the step whose ending raised '{handler.Path}' is")
                        : InRecovery(entry)
                            ? Beyond(what, read, "the failure the recovery around it puts right is")
                            : $"'{what}' reads '{read.Whole}', whose 'source' names the step whose ending raised a handler, and '{entry.Path}' is neither a handler, nor in one, nor a step of the recovery, nor one raised by a pre-existing issue");

                    return;

                case "issue":
                    if (!InOwnRecovery(entry) && entry.Step.Raised is null)
                    {
                        _problems.Add(InRecovery(entry)
                            ? Beyond(what, read, "the pre-existing issue the recovery around it fixes is")
                            : $"'{what}' reads '{read.Whole}', whose 'issue' is the pre-existing issue a step reported, and '{entry.Path}' is neither raised by one nor a step of the recovery that fixes one");
                    }

                    return;

                case FlowScope.Recovery:
                    _problems.Add($"'{what}' reads '{read.Whole}': the recovery's steps are read by their own names inside it, and nothing outside it reads them");
                    return;

                default:
                    if (WorkflowExpressions.IsRoot(read.Root))
                    {
                        return;
                    }

                    var located = Resolve(entry, read, within, out var why);

                    if (why is not null)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and {why}");
                        return;
                    }

                    // The boundary of a used workflow, before any order is weighed: a step inside it reads the steps of
                    // the workflow that called it never, whatever order they run in - its world is the called one's own.
                    if (UseOf(entry) is { } boundary && !(located is { Step: var inside } && boundary.Owns(inside)))
                    {
                        _problems.Add(Beyond(what, read, located is { } found ? $"'{found.Step.Path}' is" : "it is"));
                        return;
                    }

                    // Where a name is, FlowScope says; whether the step it finds is there to be read, this check: one
                    // declared before the reader, or one of a group's own members where the key is its loop's own
                    // (within), read once an iteration has ended. Every other key of a group - its when, its forEach - is
                    // read before any member has run, by bare name and by path alike.
                    if (located is not { } resolved || !(resolved.Step.Order < entry.Order || (within && entry.Owns(resolved.Step))))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', which no step before '{entry.Path}', no input and no param provides");
                        return;
                    }

                    // A group around the reader is declared before it, and has not ended while it runs: it is not
                    // upstream of it. A read made once every step may have run is made after the group's end too.
                    if (entry.Order != AfterEveryStep && resolved.Step.Owns(entry))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and '{resolved.Step.Path}' is not upstream of it: '{resolved.Step.Path}' holds '{entry.Path}', and has not ended while it runs");
                        return;
                    }

                    if (resolved.After is [var fact, ..] && fact is "latest" or "assumptions" && resolved.Step.Step.Type != "reason")
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and '{resolved.Step.Path}' is {(resolved.Step.Step.IsGroup ? "a group" : $"a {resolved.Step.Step.Type} step")}: only a reason step's work has {(fact == "latest" ? "a latest output" : "assumptions")}");
                    }

                    return;
            }
        }

        /// <summary>
        /// The step <paramref name="read"/> names where <paramref name="entry"/> sits - or from within the group itself,
        /// where <paramref name="within"/> says its loop reads it - with the names after it, the members of its output or
        /// the engine's facts of it; <see langword="null"/> where it names none. <see cref="FlowScope.Read"/> owns where
        /// the name is, and <paramref name="why"/> is its reason where a <c>forEach</c> holds the member read.
        /// </summary>
        private (Entry Step, IReadOnlyList<string> After)? Resolve(Entry entry, WorkflowExpressions.Read read, bool within, out string? why)
            => FlowScope.Read(read.Names, _definition, ScopeOf(entry, within), out why) is { } found
                && _byPath.GetValueOrDefault(found.Step.Path) is { } step
                    ? (step, found.After)
                    : null;

        /// <inheritdoc cref="Resolve(Entry, WorkflowExpressions.Read, bool, out string?)"/>
        private (Entry Step, IReadOnlyList<string> After)? Resolve(Entry entry, WorkflowExpressions.Read read, bool within)
            => Resolve(entry, read, within, out _);

        /// <summary>
        /// The scope <paramref name="entry"/> reads its names in: the group it is declared in, or the group itself where
        /// <paramref name="within"/> says its loop reads what its iterations produced.
        /// </summary>
        private static string? ScopeOf(Entry entry, bool within) => within ? entry.Path : entry.ParentPath;

        /// <summary>The sibling of <paramref name="entry"/> named <paramref name="name"/>, as <see cref="FlowScope.SiblingOf"/> finds it, or <see langword="null"/>.</summary>
        private Entry? Sibling(Entry entry, string name)
            => FlowScope.SiblingOf(name, _definition, entry.ParentPath) is { } found ? _byPath.GetValueOrDefault(found.Path) : null;

        /// <summary>
        /// The <c>onFailure</c> that decides for <paramref name="entry"/>: its own, or the nearest group's around it that
        /// writes one, with the step that writes it and what it writes - and the policy it is, as the run reads it
        /// (<see cref="PolicyFilled"/>): one read from a step-kind input is the one the use around its writer fills it
        /// with, null where that is none. The one reading of a step's policy every rule that weighs one asks: a reasoning
        /// step is never recovered, a recovery never calls itself, and <c>recoveryBound</c> is raised where it is recover.
        /// </summary>
        private (string? Policy, Entry Owner, string Written)? EffectiveOnFailure(Entry entry)
            => FlowScope.OnFailureOf(entry.Path, _definition) is { Step.OnFailure: { } written } deciding && _byPath.GetValueOrDefault(deciding.Path) is { } owner
                ? (PolicyFilled(owner, written), owner, written)
                : null;

        /// <summary>
        /// The policy <paramref name="written"/>, the <c>onFailure</c> <paramref name="writer"/> writes, is as the run
        /// reads it: as written where it names one, and where it reads a step-kind input of the used workflow it is a
        /// step of, what the use around it fills that input with - its with value, else the input's default - where that
        /// is one of the policies; null where it is none.
        /// </summary>
        private string? PolicyFilled(Entry writer, string written)
            => !written.Contains('{')
                ? written
                : InputRead(writer, written, FlowInput.StepKind) is { Given: { } given } && WorkflowStep.FailurePolicies.Contains(given, StringComparer.Ordinal)
                    ? given
                    : null;

        /// <summary>
        /// A key of <paramref name="entry"/>, a step of a used workflow, read whole from one of that workflow's inputs of
        /// <paramref name="kind"/> - <c>{input.&lt;name&gt;}</c> and nothing else - with the step that uses the workflow
        /// and what it fills the input with: its with value, else the input's default, null where neither gives one. Null
        /// where the step is of no used workflow, or <paramref name="written"/> is no such read. The one reading of a
        /// key a step takes from its use: its <c>onFailure</c> and a handler's <c>then</c> from a step-kind input, the
        /// file it is asked with from a file-kind one.
        /// </summary>
        private (Entry Holder, string? Given)? InputRead(Entry entry, string written, string kind)
            => UseOf(entry) is { Step.Use: { } use } holder
                && WorkflowExpressions.TemplateReads(written) is [{ Root: "input", Names: [_, { } named] }]
                && written.Equals($"{{input.{named}}}", StringComparison.Ordinal)
                && use.Inputs.FirstOrDefault(input => string.Equals(input.Name, named, StringComparison.Ordinal)) is { } input
                && input.Kind == kind
                    ? (holder, holder.Step.With.GetValueOrDefault(named) ?? input.Default)
                    : null;

        /// <summary>
        /// The recovery a failure handed to the recovery from inside <paramref name="from"/> reaches, as the run finds it:
        /// the recovery of the nearest workflow used around it that declares one - <paramref name="from"/> itself among
        /// them - and else the run's own; null where none does. A step's own failure is handed from the group it is
        /// declared in, never from itself.
        /// </summary>
        private Entry? RecoveryFrom(Entry? from)
        {
            for (var at = from; at is not null; at = at.Parent)
            {
                if (at.Step.Use?.Recover is not null)
                {
                    return _byPath.GetValueOrDefault($"{at.Path}.{FlowScope.Recovery}");
                }
            }

            return _recover;
        }

        /// <summary>
        /// The refusal of a read that reaches past the boundary of the workflow used as a step it is made in (V61):
        /// <paramref name="whose"/> - what it reaches, and the verb - is of the workflow that called it.
        /// </summary>
        private static string Beyond(string what, WorkflowExpressions.Read read, string whose)
            => $"'{what}' reads '{read.Whole}', and a workflow used as a step reads only its own steps and its own names: {whose} of the workflow that called it. Give the fact to the called workflow as one of its inputs";

        /// <summary>The nearest group around <paramref name="entry"/> - never the step itself - that says <c>always</c>, or <see langword="null"/>.</summary>
        private Entry? AlwaysAround(Entry entry)
        {
            for (var scope = entry.Parent;
                scope is not null;
                scope = scope.Parent)
            {
                if (scope.Step.Always is true)
                {
                    return scope;
                }
            }

            return null;
        }

        /// <summary>Whether <paramref name="entry"/> is one of the recovery's steps, or inside one - the flow's own, or a used workflow's.</summary>
        private bool InRecovery(Entry entry)
            => (_recover is not null && _recover.Owns(entry)) || _useRecoveries.Any(place => place.Owns(entry));

        /// <summary>
        /// Whether <paramref name="entry"/> is a step of a recovery inside the world it reads in (V61): the recovery its own
        /// workflow declares, or one nested in it - never the recovery of a workflow that calls it, whose source and issue
        /// a use the recovery holds is handed through its inputs.
        /// </summary>
        private bool InOwnRecovery(Entry entry)
        {
            var boundary = UseOf(entry);

            return (_recover is not null ? [_recover, .. _useRecoveries] : _useRecoveries)
                .Any(place => place.Owns(entry) && (boundary is null || boundary.Owns(place)));
        }

        /// <summary>
        /// The group holding <paramref name="entry"/> - or <paramref name="entry"/> itself - that is declared beside
        /// <paramref name="target"/>: what a redirect to <paramref name="target"/> leaves, or <see langword="null"/>.
        /// </summary>
        private Entry? AncestorBeside(Entry entry, Entry target)
        {
            for (var scope = entry; scope is not null; scope = scope.Parent)
            {
                if (scope.ParentPath == target.ParentPath && !ReferenceEquals(scope, target))
                {
                    return scope;
                }
            }

            return null;
        }

        /// <summary>
        /// The step a bare name resolves to where <paramref name="entry"/> sits, as <see cref="FlowScope.Resolve"/> finds
        /// it: itself, or a step of a scope an enclosing group holds - the scopes a step's own <c>of</c>, <c>in</c>,
        /// <c>resume</c> and <c>then</c> name, which reach the agents and worktrees of the groups around them.
        /// </summary>
        private Entry? Scope(Entry entry, string name)
            => FlowScope.Resolve(name, _definition, entry.ParentPath) is { } found ? _byPath.GetValueOrDefault(found.Path) : null;

        /// <summary>
        /// Whether any group <paramref name="entry"/> is part of - itself, or one holding it - holds what
        /// <paramref name="holds"/> says, inside the world it reads in: a group's own loop is its own expressions' to read,
        /// as its members' is, and a step of a used workflow looks no further than the step that uses it (V61), whose own
        /// keys - its forEach, its handler's raise - are the calling workflow's.
        /// </summary>
        private bool Inside(Entry entry, Func<WorkflowStep, bool> holds)
        {
            var boundary = UseOf(entry);

            for (var scope = entry; scope is not null && scope.Path != boundary?.Path; scope = scope.Parent)
            {
                if (holds(scope.Step))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// The nearest group <paramref name="entry"/> is part of that repeats - itself, or one holding it - whose
        /// iteration before this one <c>previous</c> names; none where <paramref name="entry"/> is none, above the top
        /// level. Where <paramref name="boundary"/> is given, the walk stops at it, the use the reader's world ends at.
        /// </summary>
        private static Entry? RepeatAround(Entry? entry, Entry? boundary = null)
        {
            for (var scope = entry; scope is not null && scope.Path != boundary?.Path; scope = scope.Parent)
            {
                if (scope.Step.Repeat is not null)
                {
                    return scope;
                }
            }

            return null;
        }

        private void DeclaredNameRules(string name, string what)
        {
            if (FlowScope.IsReserved(name))
            {
                _problems.Add($"the {what} '{name}' is named with a word a definition reads for its own; name it another way");
            }
            else if (!FlowScope.IsWellFormedDeclaredName(name))
            {
                _problems.Add($"the {what} '{name}' is not a word with a lower-case letter first: letters, digits and dashes within");
            }
        }

        private void Positive(int? value, string what)
        {
            if (value is < 1)
            {
                _problems.Add($"{what} is {value}, and so not one or more");
            }
        }

        /// <summary>
        /// The one rule every self-call breaks: no step sends its own failure, ending or reported issues back to itself, since
        /// a step that is to run again after it failed says <c>retries</c>, and the recovery's own steps have theirs.
        /// </summary>
        private static string CallsItself(string where, string how)
            => $"{where} {how}, and so calls itself; a step never does - one that is to run again after it failed says retries";

        /// <summary>
        /// V51: a reason step's pre-existing issues go to a step of the flow's top level that is raised by them, never
        /// scheduled, and that sends one it decides to fix to the recovery; where a workflow has such a step, every other
        /// reason step names it, so no issue a step meets is lost.
        /// </summary>
        private void PreExistingRules(Entry entry, string where)
        {
            var step = entry.Step;

            if (step.Raised is not null)
            {
                RaisedRules(entry, where);
            }
            else if (step.OnAddress is not null)
            {
                _problems.Add($"{where} says onAddress and is not raised by a pre-existing issue; onAddress says where a step raised by one sends an issue it decides to fix");
            }

            if (step.Raised is not null)
            {
                return;
            }

            if (step.OnPreExisting is { } named)
            {
                // A step of the flow's top level, found as every name a sibling only is.
                var triage = FlowScope.SiblingOf(named, _definition, scope: null);

                if (triage is null)
                {
                    _problems.Add($"{where} says onPreExisting: '{named}', which is no step of the flow's top level; the step that triages a reported issue is one the whole flow names");
                }
                else if (triage.Step.Raised != WorkflowStep.RaisedByPreExisting)
                {
                    _problems.Add($"{where} says onPreExisting: '{named}', which says no raised: {WorkflowStep.RaisedByPreExisting}; the step that triages a reported issue is raised by it, never scheduled");
                }
            }
            else if (Triages().FirstOrDefault() is { } triage)
            {
                _problems.Add($"{where} says no onPreExisting while '{triage.Step.Name}' triages pre-existing issues; every reasoning step names the step its issues go to, so none it meets is lost");
            }
        }

        /// <summary>A step raised by a pre-existing issue: at the top level, triggered only, and sending a fix to the recovery.</summary>
        private void RaisedRules(Entry entry, string where)
        {
            var step = entry.Step;

            if (entry.ParentPath is not null)
            {
                _problems.Add($"{where} says raised: {WorkflowStep.RaisedByPreExisting} and is not a step of the flow's top level; the step that triages a reported issue is one every step of the flow can name");
            }

            // Triggered only, as the recovery is: nothing schedules it, so nothing it waits for and nothing that waits for it.
            if (step.DependsOn is [var first, ..])
            {
                _problems.Add($"{where} is raised by a pre-existing issue and also depends on '{first}'; a raised step waits for the issue it is given and for nothing else");
            }

            // What waits for it names it under dependsOn, found as a dependency is: the sibling of that name, the first
            // declared where siblings share one.
            if (_entries.FirstOrDefault(other => other.Step.DependsOn.Any(name => ReferenceEquals(Sibling(other, name), entry))) is { } waiting)
            {
                _problems.Add($"{where} is raised by a pre-existing issue, and '{waiting.Path}' depends on it; a raised step is never scheduled, so nothing waits for it");
            }

            if (step.On is not null)
            {
                _problems.Add($"{where} is raised by a pre-existing issue and by another step's ending (on); a step is raised one way");
            }

            if (step.When is not null)
            {
                _problems.Add($"{where} is raised by a pre-existing issue and says when; every issue raised to it is triaged");
            }

            if (step.Always is true)
            {
                _problems.Add($"{where} is raised by a pre-existing issue and is always; a step runs at the session's end or for an issue, never both");
            }

            if (step.OnAddress is null)
            {
                _problems.Add($"{where} is raised by a pre-existing issue and says no onAddress; an issue it decides to fix goes to the recovery: say onAddress: {WorkflowStep.Recover}");
            }
            else if (_definition.Recover is null)
            {
                _problems.Add($"{where} says onAddress: {WorkflowStep.Recover}, and the workflow declares no recover; an issue to fix at its root is the recovery's work");
            }

            if (step.OnPreExisting is not null)
            {
                _problems.Add(CallsItself(where, "is raised by a pre-existing issue and sends the issues it meets to be triaged (onPreExisting)"));
            }
        }

        /// <summary>The steps of the flow's top level raised by a pre-existing issue, in the order declared.</summary>
        private IEnumerable<Entry> Triages()
            => _entries.Where(entry => entry.ParentPath is null && entry != _recover && entry.Step.Raised == WorkflowStep.RaisedByPreExisting);

        /// <summary>
        /// V52: the workflow's <c>codebaseGraph</c> shapes the graph its <c>graphify-codebase</c> steps make, held to
        /// <see cref="GraphShapeRules"/>. A graphify step's <c>in</c> is checked with every machine step's. V66: where the
        /// workflow declares none, the uses that govern themselves and declare one give the run's graph one shape.
        /// </summary>
        private void CodebaseGraph()
        {
            if (_definition.CodebaseGraph is { } graph)
            {
                GraphShapeRules(graph, lead: string.Empty);
                return;
            }

            // A run has one graph, of one shape: where the run's own workflow declares none, the shape a use that governs
            // itself declares is the run's - and two such uses that declare different shapes would leave which one it is
            // to whichever ran first. Uses of one workflow declare one shape.
            var shaped = _entries.Where(entry => entry.Step.Use?.Own?.CodebaseGraph is not null).ToList();

            foreach (var other in shaped.Skip(1).Where(other => !SameShape(shaped[0].Step.Use!.Own!.CodebaseGraph!, other.Step.Use!.Own!.CodebaseGraph!)))
            {
                _problems.Add($"steps '{shaped[0].Path}' and '{other.Path}' govern themselves (useParentLimits: false) by the workflows '{shaped[0].Step.Use!.Name}' and '{other.Step.Use!.Name}', which shape the run's graph differently, and the run's own workflow declares no codebaseGraph: a run has one graph, of one shape - declare it in the run's own workflow, or give the two one shape");
            }
        }

        /// <summary>
        /// V52 over one <c>codebaseGraph</c> block - the run's own, or one a use that governs itself declares - each
        /// refusal said after <paramref name="lead"/>: it stands only beside a graphify-codebase step of the run, its
        /// patterns are patterns, its lists are not given empty, its languages are the registry's, each once, and its
        /// values are in their ranges.
        /// </summary>
        /// <param name="graph">The block.</param>
        /// <param name="lead">What each refusal starts with: nothing for the run's own, the governing use for a use's.</param>
        private void GraphShapeRules(FlowCodebaseGraph graph, string lead)
        {
            if (!_entries.Any(entry => entry.Step.Type == "graphify-codebase"))
            {
                _problems.Add($"{lead}codebaseGraph shapes the run's graph, and no step of the workflow is a graphify-codebase step: say where the graph is made, or leave the block out");
            }

            foreach (var (key, patterns, leftOut) in new[] { ("include", graph.Include, "every path graphed"), ("exclude", graph.Exclude, "no path left out") })
            {
                if (patterns is { Count: 0 })
                {
                    _problems.Add($"{lead}codebaseGraph.{key} is given empty, which is never read as {leftOut}: name its patterns, or leave the key out for {leftOut}");
                }

                foreach (var pattern in patterns ?? [])
                {
                    if (GlobPattern.Problem(pattern) is { } typo)
                    {
                        _problems.Add($"{lead}codebaseGraph.{key} names the pattern '{pattern}', and it {typo}");
                    }
                }
            }

            if (graph.Languages is { Count: 0 })
            {
                _problems.Add($"{lead}codebaseGraph.languages is given empty, which is never read as every one used: name the implementations, or leave the key out for every one");
            }

            foreach (var twice in (graph.Languages ?? []).GroupBy(language => language, StringComparer.Ordinal).Where(group => group.Count() > 1))
            {
                _problems.Add($"{lead}codebaseGraph.languages names '{twice.Key}' more than once");
            }

            if (_languages is { } known)
            {
                foreach (var unknown in (graph.Languages ?? []).Where(language => !known.Contains(language, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal))
                {
                    _problems.Add($"{lead}codebaseGraph extracts '{unknown}', which this tool has no extractor for; it extracts: {string.Join(", ", known)}");
                }
            }

            Within(graph.MaxFileBytes, Codebase.CodebaseGraphShape.LeastMaxFileBytes, Codebase.CodebaseGraphShape.MostMaxFileBytes, $"{lead}codebaseGraph.maxFileBytes");
            Within(graph.MapTokens, 0, Codebase.CodebaseGraphShape.MostMapTokens, $"{lead}codebaseGraph.mapTokens");

            if (graph.Generated is { } generated && !Codebase.CodebaseGraphShape.GeneratedValues.Contains(generated, StringComparer.Ordinal))
            {
                _problems.Add($"{lead}codebaseGraph.generated is '{generated}', which is none of {string.Join(", ", Codebase.CodebaseGraphShape.GeneratedValues.Select(value => $"'{value}'"))}: generated code's declarations are kept, its file's entry naming why it is generated, or left out with the file listed");
            }
        }

        /// <summary>
        /// Whether two <c>codebaseGraph</c> blocks give the run's graph one shape, as <see cref="FlowCodebaseGraph.Shape"/>
        /// reads them - every default held where a block says nothing. A block whose patterns are no patterns is refused
        /// for them, and is weighed as it is written.
        /// </summary>
        private static bool SameShape(FlowCodebaseGraph one, FlowCodebaseGraph other)
        {
            static bool Readable(FlowCodebaseGraph graph)
                => (graph.Include ?? []).Concat(graph.Exclude ?? []).All(pattern => GlobPattern.Problem(pattern) is null);

            static bool Same(IReadOnlyList<string>? first, IReadOnlyList<string>? second)
                => first is null ? second is null : second is not null && first.SequenceEqual(second, StringComparer.Ordinal);

            if (!Readable(one) || !Readable(other))
            {
                return Same(one.Include, other.Include) && Same(one.Exclude, other.Exclude) && Same(one.Languages, other.Languages)
                    && one.MaxFileBytes == other.MaxFileBytes && one.Generated == other.Generated && one.MapTokens == other.MapTokens;
            }

            var (first, second) = (one.Shape, other.Shape);

            return first.Include.Select(pattern => pattern.ToString()).SequenceEqual(second.Include.Select(pattern => pattern.ToString()), StringComparer.Ordinal)
                && first.Exclude.Select(pattern => pattern.ToString()).SequenceEqual(second.Exclude.Select(pattern => pattern.ToString()), StringComparer.Ordinal)
                && Same(first.Languages, second.Languages)
                && first.MaxFileBytes == second.MaxFileBytes
                && first.ExcludeGenerated == second.ExcludeGenerated
                && first.MapTokens == second.MapTokens;
        }

        /// <summary>Refuses <paramref name="value"/> outside <paramref name="least"/> to <paramref name="most"/>; whether it holds, absent among them.</summary>
        private bool Within(int? value, int least, int most, string what)
        {
            if (value < least || value > most)
            {
                _problems.Add($"{what} must be from {least.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} to {most.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}, found {value?.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                return false;
            }

            return true;
        }

        private static string IsAre(int count) => count == 1 ? "is" : "are";

        /// <summary>
        /// The roots of the run's own names that only a prompt reads, each with why: a step's own keys - its templates and
        /// its expressions - and a pull request's body, which the engine fills and no agent reads, read none of them; each
        /// prompt reads those <see cref="PromptAloneUnread"/> says.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, string> PromptsAlone = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // V43.
            ["recommendations"] = "the repository's bar for a recommendation is text for an agent, and only a reason step's prompt and the advisor's read it",
            ["text"] = "a workflow's texts are prose for an agent, and only a reason step's prompt and the advisor's read them",
            ["question"] = "only the advisor's prompt reads a question",

            // V45.
            ["status"] = "only the reporter's prompts read the status",

            // V44.
            ["update"] = "a plan update is told to an agent, and only the prompts of the step that plans it and of the steps of its scope read one",
        };

        /// <summary>V45: why the reporter's status prompt reads no question.</summary>
        private const string NoStatusQuestion = "the status is the tool's own question, and no person wrote one";

        /// <summary>
        /// Why <paramref name="read"/>, of a root only a prompt reads (<see cref="PromptsAlone"/>), is not read in the text
        /// <paramref name="readIn"/> says, or <see langword="null"/> where it is: the bar in a reason step's prompt and
        /// the advisor's (V43); a question in the advisor's alone (V43); the status in the reporter's two, and the
        /// person's question in its askPrompt alone (V45); a plan update in the prompts of the step that plans it and of
        /// the steps of its scope (V44).
        /// </summary>
        private string? PromptAloneUnread(Entry entry, WorkflowExpressions.Read read, ReadIn readIn)
            => (read.Root, readIn) switch
            {
                ("recommendations", ReadIn.StepPrompt or ReadIn.AdvisorPrompt) => null,
                ("text", ReadIn.StepPrompt or ReadIn.AdvisorPrompt) => null,
                ("question", ReadIn.AdvisorPrompt) => null,
                ("status", ReadIn.AskPrompt) => null,
                ("status", ReadIn.StatusPrompt) => read.Names is [_, "question", ..] ? NoStatusQuestion : null,
                ("update", ReadIn.StepPrompt) => UpdateUnread(entry),
                _ => PromptsAlone[read.Root],
            };

        /// <summary>
        /// V44: why a reason step's prompt reads no plan update, or <see langword="null"/> where it does - a prompt of the
        /// step that plans one or of a step of its scope, the group that step is declared in; at the top level, every
        /// step of the flow. Where amend.step names no step, or one in the recovery, it was refused already, and no
        /// prompt is held to it.
        /// </summary>
        private string? UpdateUnread(Entry entry)
        {
            if (_definition.Amend is not { } amend)
            {
                return "a plan update is told to an agent, and the workflow takes none: it declares no amend block";
            }

            if (_byPath.GetValueOrDefault(amend.Step) is not { } planner || InRecovery(planner))
            {
                return null;
            }

            return InRecovery(entry) || (planner.Parent is { } scope && !scope.Owns(entry)) ? PromptsAlone["update"] : null;
        }

        /// <summary>
        /// The text a placeholder is read in, which says which of the names only some prompts read it may name: a step's
        /// own key, a reason step's <c>prompt</c> or <c>resumePrompt</c>, a pull request's <c>body</c>, the advisor's
        /// prompt, or one of the reporter's two.
        /// </summary>
        private enum ReadIn
        {
            Key,
            StepPrompt,
            Body,
            AdvisorPrompt,
            StatusPrompt,
            AskPrompt,
        }

        /// <summary>
        /// The kinds an advisor is asked about are question kinds; and V43: the advisor recommends on the questions the
        /// engine raises itself, and two kinds are none of them: an agent that asks gives its own recommendation with its
        /// question, and a run at its budget's reserve spends nothing more on reasoning, which the advisor is.
        /// </summary>
        /// <param name="advisor">The run's own advisor, or a governing use's.</param>
        /// <param name="lead">What each refusal starts with: nothing for the run's own, the governing use for a use's.</param>
        private void AdvisorKinds(FlowAdvisor advisor, string lead)
        {
            if (advisor.Kinds.Count == 0)
            {
                return;
            }

            var unknown = advisor.Kinds.Where(kind => !QuestionKinds.Contains(kind, StringComparer.Ordinal)).ToList();

            if (unknown.Count > 0)
            {
                _problems.Add($"{lead}advisor.kinds names {string.Join(", ", unknown.Select(kind => $"'{kind}'"))}, which {IsAre(unknown.Count)} no question kind. Known: {string.Join(", ", QuestionKinds.Select(kind => $"'{kind}'"))}.");
            }

            if (advisor.Kinds.Contains("ask", StringComparer.Ordinal))
            {
                _problems.Add($"{lead}advisor is to recommend for 'ask': an agent that asks gives its own recommendation");
            }

            if (advisor.Kinds.Contains("runBudget", StringComparer.Ordinal))
            {
                _problems.Add($"{lead}advisor is to recommend for 'runBudget': a run at its budget's reserve spends nothing more on reasoning, and the advisor reasons");
            }
        }

        /// <summary>
        /// V39: what a block gathers is read after its name as its members are - <c>review.findings</c> - so each name is
        /// gathered once, and is neither one of the engine's facts of a step nor the name of a member, either of which
        /// that path would read in its place.
        /// </summary>
        private void GatherNames(Entry entry)
        {
            var where = $"group '{entry.Path}'";

            foreach (var twice in entry.Step.Gather.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1).Select(group => group.Key))
            {
                _problems.Add($"{where} gathers '{twice}' twice; a block gathers one list of each name");
            }

            foreach (var name in entry.Step.Gather.Distinct(StringComparer.Ordinal))
            {
                if (WorkflowExpressions.StepFacts.Contains(name, StringComparer.Ordinal))
                {
                    _problems.Add($"{where} gathers '{name}', which after a step's name is one of the engine's own facts of it ({string.Join(", ", WorkflowExpressions.StepFacts)}): '{entry.Path}.{name}' would read that fact and the gathered list at once. Gather it under another name");
                }
                else if (entry.Step.Steps.Any(member => string.Equals(member.Name, name, StringComparison.Ordinal)))
                {
                    _problems.Add($"{where} gathers '{name}', the name of one of its members: '{entry.Path}.{name}' would read that member and the gathered list at once. Gather it under another name");
                }
            }
        }

        /// <summary>
        /// V30, where the configuration declares no commit template: the step's message is the commit's, and variables,
        /// which fill a template's placeholders, would be read by nothing. Where it declares one, the validation service
        /// holds the step's variables to the ones the template declares, from the configuration itself.
        /// </summary>
        private void CommitVariablesWithoutATemplate(Entry entry)
        {
            if (!_hasCommitTemplate && entry.Step.Variables.Count > 0)
            {
                _problems.Add($"step '{entry.Path}' gives variables and the configuration declares no commit.template; variables fill a template's placeholders, and with none its message is the commit's and they would be read by nothing");
            }
        }

        /// <summary>
        /// V40: an answer to a pause step is one of its options, given once each time the step asks - never counted, as an
        /// answer after which the same question can come again is, and so never given <c>times</c> or a <c>then</c>.
        /// </summary>
        private void PauseAnswerGivenOnce(FlowAnswer answer, string where)
        {
            if (answer.Times is not null)
            {
                _problems.Add($"{where} answers 'pause' with {answer.Answer} {answer.Times} times; times count an answer after which the same question can come again - more, retry or compact - and a pause step's answer ends the matter");
            }

            if (answer.Then is not null)
            {
                _problems.Add($"{where} answers 'pause' then {answer.Then}; then is what is answered once the counted answers are spent, and a pause step's answer is never counted");
            }
        }

        /// <summary>
        /// V31: <c>pushCheckpoints</c> pushes the checkpoint commits as they are made, so a workflow that says it and has
        /// no push step that is a checkpoint says something nothing would do.
        /// </summary>
        private void Checkpoints()
        {
            if (_definition.PushCheckpoints is true && !_entries.Any(entry => entry.Step is { Type: "push", Checkpoint: true }))
            {
                _problems.Add("the workflow sets pushCheckpoints, and none of its push steps is a checkpoint: the key would change nothing");
            }
        }

        /// <summary>
        /// V36: two handlers of one source that one ending could raise never both name a <c>then</c>: both are raised, and
        /// the run can go to one step only, so which it went to would be nobody's choice.
        /// </summary>
        private void SharedEndings()
        {
            var redirecting = _entries.Where(entry => entry.Step is { On: not null, Then: not null }).ToList();

            for (var first = 0; first < redirecting.Count; first++)
            {
                for (var second = first + 1; second < redirecting.Count; second++)
                {
                    var (one, other) = (redirecting[first], redirecting[second]);

                    // A handler's source is a sibling, so two handlers of one source are declared beside each other.
                    if (string.Equals(one.ParentPath, other.ParentPath, StringComparison.Ordinal)
                        && string.Equals(one.Step.On!.Step, other.Step.On!.Step, StringComparison.Ordinal)
                        && SharedEnding(one.Step.On, other.Step.On) is { } ending)
                    {
                        _problems.Add($"steps '{one.Path}' and '{other.Path}' are both raised when '{one.Step.On.Step}' {ending}, and both name a then: the run can go to one step");
                    }
                }
            }
        }

        /// <summary>
        /// The ending of one source that raises both <paramref name="one"/> and <paramref name="other"/>, in words - that it
        /// succeeds, that it fails, or that it fails with the codes both answer to - or <see langword="null"/> where no
        /// ending raises both. A step that ended done ended with 0, so an <c>end</c> that names codes is raised by a failure
        /// with one of them alone.
        /// </summary>
        private static string? SharedEnding(FlowOn one, FlowOn other)
        {
            static bool BySuccess(FlowOn on) => on.Outcome == "success" || (on.Outcome == "end" && on.Codes.Count == 0);
            static bool ByFailure(FlowOn on) => on.Outcome is "failure" or "end";

            if (BySuccess(one) && BySuccess(other))
            {
                return "succeeds";
            }

            if (!ByFailure(one) || !ByFailure(other))
            {
                return null;
            }

            if (one.Codes.Count == 0 && other.Codes.Count == 0)
            {
                return "fails";
            }

            IReadOnlyList<int> shared = one.Codes.Count == 0
                ? [.. other.Codes.Distinct()]
                : [.. one.Codes.Distinct().Where(code => other.Codes.Count == 0 || other.Codes.Contains(code))];

            return shared switch
            {
                [] => null,
                [var code] => $"fails with {code}",
                _ => $"fails with {string.Join(", ", shared.SkipLast(1))} or {shared[^1]}",
            };
        }

        /// <summary>
        /// V38's config half, for each agent the definition runs - a reasoning step, the advisor and the reporter:
        /// what a step names is resolved against the configuration's providers and tiers, so a model no provider
        /// lists, a tier nothing defines, an effort a model does not take and a client that does not reach the
        /// provider are all refused with the step that names them, when the definition is checked. What a step's
        /// agent definition names stands below its own: the step's model or tier wins over the definition's as one pick
        /// (<see cref="DssHarness.Core.Agents.AgentResolution.Picked"/>), its effort and client each on their own, and
        /// the definition's effort is read through after the step's. A step that goes on with another's work is checked
        /// as the step at the root of its chain (<see cref="ResumeChain.Root"/>), whose definition, tier, model and client
        /// it runs with.
        /// </summary>
        private void Agents()
        {
            if (_providers is not { } providers)
            {
                return;
            }

            foreach (var entry in _entries)
            {
                if (entry.Step.Type == "reason")
                {
                    var (root, definition) = WorksAs(entry);
                    AgentRules(providers, root.Tier, root.Model, entry.Step.Effort, entry.Step.EffortByModel, root.Client, definition, runBudget: true, $"workflow '{_definition.Name}' step '{entry.Path}'");
                }
                else if (entry.Step.Type == "create-agent" && WorkedInBy(entry) is null && DefaultTierProblem(providers) is { } problem)
                {
                    // A reason step that works in the agent says its model, and is checked as one; an agent no reason
                    // step works in is made for the default tier's first model, which must then be there.
                    _problems.Add(
                        $"workflow '{_definition.Name}' step '{entry.Path}' makes an agent no reason step works in, which is made for the first model of the default tier, and "
                        + problem + (providers.DefaultTier is null ? "; name the tier there" : string.Empty));
                }
            }

            foreach (var (advisor, where, workflow, _) in Advisors())
            {
                AgentRules(providers, advisor.Tier, advisor.Model, advisor.Effort, advisor.EffortByModel, advisor.Client, DefinitionOf(advisor.Agent, workflow), runBudget: true, where);
            }

            if (_definition.Reporter is { } reporter)
            {
                AgentRules(providers, reporter.Tier, reporter.Model, reporter.Effort, reporter.EffortByModel, reporter.Client, DefinitionOf(reporter.Agent), runBudget: false, "reporter");
            }
        }

        /// <summary>
        /// Every advisor a run may ask a recommendation of: the run's own, and the one each use that governs itself
        /// (<c>useParentLimits: false</c>) declares, which serves its steps' questions - each with how a refusal names
        /// it, the workflow whose directory holds its files (the run's own where null), and the use that declares it.
        /// </summary>
        private IEnumerable<(FlowAdvisor Advisor, string Where, string? Workflow, Entry? Use)> Advisors()
        {
            if (_definition.Advisor is { } own)
            {
                yield return (own, "advisor", null, null);
            }

            foreach (var entry in _entries)
            {
                if (entry.Step.Use is { Own.Advisor: { } advisor } use)
                {
                    yield return (advisor, $"{Governing(entry, use)}: advisor", use.Name, entry);
                }
            }
        }

        /// <summary>
        /// The agent definition <paramref name="agent"/> names, as its <c>agents/&lt;name&gt;.md</c> file reads it in the
        /// workflow <paramref name="workflow"/> names - the run's own, or the called workflow a step of which names it:
        /// read once wherever it is named, its file's problems said once. Null where no files are given, the file is not
        /// there or does not read - the service that reads files says a missing one, the parser's problems are said here.
        /// </summary>
        private AgentDefinition? DefinitionOf(string? agent, string? workflow = null)
        {
            if (agent is null || _files is null)
            {
                return null;
            }

            var key = (workflow ?? string.Empty, agent);

            if (_agents.TryGetValue(key, out var read))
            {
                return read;
            }

            if (_files($"agents/{agent}.md", workflow) is not { } text)
            {
                return _agents[key] = null;
            }

            var (definition, problems) = AgentDefinitionParser.Parse(text, agent);

            foreach (var problem in problems)
            {
                _problems.Add($"agents/{agent}.md: {problem}");
            }

            // The body is the system prompt, sent as written: a skill's text is a step's prompt's to name, and nothing
            // fills a placeholder there. Said at the file's own line, past the frontmatter.
            if (definition is not null && Yaml.Frontmatter.Of(text) is { Body: { } body, BodyLine: var first })
            {
                var lines = body.Split('\n');

                for (var line = 0; line < lines.Length; line++)
                {
                    foreach (var named in WorkflowExpressions.TemplateReads(lines[line]).Where(named => named.Root == AgentSkills.Root))
                    {
                        _problems.Add($"agent '{agent}' names {{{named.Whole}}} in its body (line {first + line}): the body is the system prompt and is sent as written. Name the skill in a step's prompt");
                    }
                }
            }

            return _agents[key] = definition;
        }

        /// <summary>The model each create-agent step makes its agent for, and each reason step that names no agent its own read-only one, by its path, where one resolves (<see cref="WorkflowValidator.AgentModels"/>).</summary>
        internal IReadOnlyDictionary<string, string> AgentModels()
        {
            var models = new Dictionary<string, string>(StringComparer.Ordinal);

            if (_providers is not { } providers)
            {
                return models;
            }

            foreach (var entry in _entries.Where(entry => entry.Step.Type == "create-agent"))
            {
                (string? Tier, string? Model) picked = WorkedInBy(entry) is { } reasonStep && WorksAs(reasonStep) is var (root, definition)
                    ? DssHarness.Core.Agents.AgentResolution.Picked(root.Tier, root.Model, definition)
                    : (null, null);

                if (providers.ModelsFor(picked.Tier, picked.Model) is [var first, ..])
                {
                    models[entry.Path] = first.Model.Code;
                }
            }

            // A reason step that makes an agent of its own - one that reads and begins work of its own - is given the
            // engine's own read-only one, made for the model the step itself resolves to. A create-agent's own entry
            // is above; this is the reason steps' alone.
            foreach (var entry in _entries.Where(entry => entry.Step.Type == "reason" && WorkflowStepTypes.MakesAgent(entry.Step) && entry.Step.In is null))
            {
                var (root, definition) = WorksAs(entry);
                var picked = DssHarness.Core.Agents.AgentResolution.Picked(root.Tier, root.Model, definition);

                if (providers.ModelsFor(picked.Tier, picked.Model) is [var own, ..])
                {
                    models[entry.Path] = own.Model.Code;
                }
            }

            return models;
        }

        /// <summary>The first reason step, in the order declared, that works in the agent the create-agent step <paramref name="create"/> makes; null where none does.</summary>
        private Entry? WorkedInBy(Entry create)
            => _entries.FirstOrDefault(entry => entry.Step.Type == "reason" && entry.Step.In is { } name && ReferenceEquals(Scope(entry, name), create));

        /// <summary>
        /// What a reason step works as, as its run resolves it: the step at the root of its chain of resumes
        /// (<see cref="ResumeChain.Root"/>) - itself where it begins its own work - whose tier, model and client it runs
        /// on, and the agent definition that step names, which it works by. A step that goes on with another's work
        /// names none of these itself; its effort stays its own.
        /// </summary>
        private (WorkflowStep Root, AgentDefinition? Definition) WorksAs(Entry reason)
        {
            var root = ResumeChain.Root(_definition, reason.Path, reason.Step);

            return (root, DefinitionOf(root.Agent, root.WorkflowOf));
        }

        /// <summary>
        /// Why the configuration's default tier gives no model, as a refusal ends: it names none, or one tierConfig does
        /// not define; null where it gives one.
        /// </summary>
        private static string? DefaultTierProblem(DssHarness.Core.Agents.AgentProviders providers)
            => providers.DefaultTier is not { } tier
                ? "the configuration names no defaults.agentTier"
                : providers.ModelsOf(tier).Count == 0
                    ? $"defaults.agentTier names '{tier}', which tierConfig does not define. Defined: {string.Join(", ", providers.Tiers.OrderBy(name => name, StringComparer.Ordinal))}"
                    : null;

        /// <summary>
        /// One line for each agent of the definition that names a model, a tier or an effort: each model it may run on
        /// and the level it resolves to, so a person reads what every step will run at.
        /// </summary>
        internal IReadOnlyList<string> Levels()
        {
            if (_providers is not { } providers)
            {
                return [];
            }

            var lines = new List<string>();

            foreach (var entry in _entries.Where(entry => entry.Step.Type == "reason"))
            {
                var (root, definition) = WorksAs(entry);
                AddLevels($"step '{entry.Path}'", root.Tier, root.Model, entry.Step.Effort, entry.Step.EffortByModel, definition, providers, lines);
            }

            foreach (var (advisor, where, workflow, _) in Advisors())
            {
                AddLevels(where, advisor.Tier, advisor.Model, advisor.Effort, advisor.EffortByModel, DefinitionOf(advisor.Agent, workflow), providers, lines);
            }

            if (_definition.Reporter is { } reporter)
            {
                AddLevels("reporter", reporter.Tier, reporter.Model, reporter.Effort, reporter.EffortByModel, DefinitionOf(reporter.Agent), providers, lines);
            }

            return lines;
        }

        /// <summary>
        /// One need for each provider with a key the slot lacks that a step would read, with every step that would read
        /// one - first models before later models of a tier, for a run refuses only where the first model of one of its
        /// steps has none of its keys, and a later model is said and passed over.
        /// </summary>
        internal IReadOnlyList<KeyNeed> Keys(IReadOnlyCollection<string> held)
        {
            if (_providers is not { } providers)
            {
                return [];
            }

            var needed = new Dictionary<string, (Configuration.AgentProviderConfig Provider, List<string> Missing, List<string> First, List<string> Later)>(StringComparer.Ordinal);

            void Need(string where, string? tier, string? model, string? client)
            {
                foreach (var (provider, at) in providers.ModelsFor(tier, model).Select((entry, at) => (entry.Provider, at)))
                {
                    var missing = providers.KeysRead(provider, client).Where(name => !held.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList();

                    if (missing.Count == 0)
                    {
                        continue;
                    }

                    if (!needed.TryGetValue(provider.Name, out var steps))
                    {
                        steps = (provider, missing, [], []);
                        needed[provider.Name] = steps;
                    }

                    // A step that reads the provider's keys for its first model reads them for a later one too, and a
                    // tier that tries two of its models says the step once either way.
                    if (at == 0)
                    {
                        if (!steps.First.Contains(where, StringComparer.Ordinal))
                        {
                            steps.First.Add(where);
                        }
                    }
                    else if (!steps.First.Contains(where, StringComparer.Ordinal) && !steps.Later.Contains(where, StringComparer.Ordinal))
                    {
                        steps.Later.Add(where);
                    }
                }
            }

            void NeedOf(string where, string? tier, string? model, string? client, AgentDefinition? definition)
            {
                var picked = DssHarness.Core.Agents.AgentResolution.Picked(tier, model, definition);
                Need(where, picked.Tier, picked.Model, client ?? definition?.Client);
            }

            foreach (var entry in _entries.Where(entry => entry.Step.Type == "reason"))
            {
                var (root, definition) = WorksAs(entry);
                NeedOf($"step '{entry.Path}'", root.Tier, root.Model, root.Client, definition);
            }

            foreach (var (advisor, where, workflow, _) in Advisors())
            {
                NeedOf(where, advisor.Tier, advisor.Model, advisor.Client, DefinitionOf(advisor.Agent, workflow));
            }

            if (_definition.Reporter is { } reporter)
            {
                NeedOf("reporter", reporter.Tier, reporter.Model, reporter.Client, DefinitionOf(reporter.Agent));
            }

            return [.. needed.Values.Select(entry => new KeyNeed(
                entry.Provider,
                entry.Missing,
                [.. entry.First, .. entry.Later.Select(where => $"{where} (a later model of its tier)")]))];
        }

        /// <summary>Every model an agent may run on, each once, in the order first met.</summary>
        internal IReadOnlyList<(Configuration.AgentProviderConfig Provider, Configuration.AgentModelConfig Model)> Models()
        {
            if (_providers is not { } providers)
            {
                return [];
            }

            IEnumerable<(string? Tier, string? Model)> agents = _entries
                .Where(entry => entry.Step.Type == "reason")
                .Select(WorksAs)
                .Select(agent => DssHarness.Core.Agents.AgentResolution.Picked(agent.Root.Tier, agent.Root.Model, agent.Definition))
                .Concat(Advisors().Select(advisor => DssHarness.Core.Agents.AgentResolution.Picked(advisor.Advisor.Tier, advisor.Advisor.Model, DefinitionOf(advisor.Advisor.Agent, advisor.Workflow))))
                .Concat(_definition.Reporter is { } reporter ? [DssHarness.Core.Agents.AgentResolution.Picked(reporter.Tier, reporter.Model, DefinitionOf(reporter.Agent))] : []);

            return [.. agents
                .SelectMany(agent => providers.ModelsFor(agent.Tier, agent.Model))
                .DistinctBy(entry => entry.Model.Code, StringComparer.Ordinal)];
        }

        private void AddLevels(
            string where,
            string? tier,
            string? model,
            string? effort,
            IReadOnlyDictionary<string, string>? effortByModel,
            AgentDefinition? definition,
            DssHarness.Core.Agents.AgentProviders providers,
            List<string> lines)
        {
            // What the definition names stands below the step's, by the one pick the run resolves by: a model named
            // outright, by the step or its definition, comes of no tier.
            var picked = DssHarness.Core.Agents.AgentResolution.Picked(tier, model, definition);
            var models = providers.ModelsFor(picked.Tier, picked.Model);

            if (models.Count == 0)
            {
                return;
            }

            var on = picked.Model is null ? $" ({picked.Tier ?? providers.DefaultTier})" : string.Empty;

            lines.Add($"{where}{on}: {string.Join(", ", models.Select(entry => $"{entry.Model.Code} at {DssHarness.Core.Agents.AgentResolution.LevelFor(entry.Model.Code, effort, effortByModel, definition?.Effort, definition?.EffortByModel)?.Level ?? "its own default"}"))}");
        }

        private void AgentRules(
            DssHarness.Core.Agents.AgentProviders providers,
            string? tier,
            string? model,
            string? effort,
            IReadOnlyDictionary<string, string>? effortByModel,
            string? client,
            AgentDefinition? definition,
            bool runBudget,
            string where)
        {
            // What the definition names stands below the step's, by the one pick the run resolves by: a step that names
            // neither a tier nor a model runs on what its definition names - its model, else its tier, never a tier of
            // one and a model of the other - and a client it does not name is the definition's.
            (tier, model) = DssHarness.Core.Agents.AgentResolution.Picked(tier, model, definition);
            client ??= definition?.Client;

            // A configuration that declares no providers has no model at all, so whatever the step names - a tier, a
            // model, or neither - it is told how to add them before anything is looked for in them.
            if (providers.HasNoProvidersAtAll)
            {
                var names = model is not null ? $"names model '{model}'" : tier is not null ? $"names tier '{tier}'" : "names neither a tier nor a model";

                _problems.Add($"{where} {names} and the config declares no providers and no tierConfig: {DssHarness.Core.Agents.AgentProvidersService.TemplateHint}");
                return;
            }

            if (model is not null)
            {
                if (providers.Of(model) is not { } named)
                {
                    _problems.Add($"{where} resolves to model '{model}', which no provider lists. Listed: {string.Join("; ", providers.Providers.Select(provider => $"{provider.Name}: {string.Join(", ", provider.Models.Select(entry => entry.Code))}"))}. Add it to its provider's models under providers");
                    return;
                }

                if (client is not null && !ClientReaches(client, named.Provider))
                {
                    _problems.Add($"{where} resolves to client '{client}', which does not reach provider '{named.Provider.Name}', whose model '{model}' it names");
                }

                if (effort is null && effortByModel is null && definition?.Effort is null && definition?.EffortByModel is null && named.Model.Efforts is [])
                {
                    _problems.Add($"{where} names model '{model}', whose entry takes no efforts");
                }

                EffortRules([(named.Provider, named.Model)], null, effort, effortByModel, definition, where);
                BudgetPrices(providers, [(named.Provider, named.Model)], client, definition, runBudget, where);
                return;
            }

            if (tier is null)
            {
                // A step that names neither a tier nor a model, on itself or in its definition, runs on the
                // configuration's default tier, which nothing builds in.
                if (DefaultTierProblem(providers) is { } problem)
                {
                    _problems.Add($"{where} names neither a tier nor a model, and {problem}{(providers.DefaultTier is null ? "; say one on the step, in its agent's definition, or under defaults.agentTier" : string.Empty)}");
                    return;
                }

                var defaults = providers.ModelsOf(providers.DefaultTier!);

                // The default tier is a tier like a named one: its models are checked each on its own.
                if (client is not null)
                {
                    foreach (var (provider, _) in defaults.Where(entry => !ClientReaches(client, entry.Provider)))
                    {
                        _problems.Add($"{where} resolves to client '{client}', which does not reach provider '{provider.Name}', whose models serve tier '{providers.DefaultTier}'");
                    }
                }

                EffortRules(defaults, providers.DefaultTier, effort, effortByModel, definition, where);
                BudgetPrices(providers, defaults, client, definition, runBudget, where);
                return;
            }

            var models = providers.ModelsOf(tier);

            if (models.Count == 0)
            {
                _problems.Add($"{where} names tier '{tier}', which tierConfig does not define. Defined: {string.Join(", ", providers.Tiers.OrderBy(name => name, StringComparer.Ordinal))}");
                return;
            }

            if (client is not null)
            {
                foreach (var (provider, _) in models.Where(entry => !ClientReaches(client, entry.Provider)))
                {
                    _problems.Add($"{where} resolves to client '{client}', which does not reach provider '{provider.Name}', whose models serve tier '{tier}'");
                }
            }

            EffortRules(models, tier, effort, effortByModel, definition, where);
            BudgetPrices(providers, models, client, definition, runBudget, where);
        }

        /// <summary>
        /// Each model of an agent budgeted in dollars - by its definition's <c>maxBudgetUsd</c>, or by the run's budget where
        /// it is charged to it - checked for the prices of every kind its run is billed by, in the words of the one owner of
        /// the rule (<see cref="DssHarness.Core.Agents.AgentPrices.BudgetRefusal"/>): a budget is charged with the cost
        /// counted from them. Each model is checked against the client the run reaches it through
        /// (<see cref="DssHarness.Core.Agents.AgentResolution.ClientOf"/>, each entry of the order on its own) and what that
        /// client's kind is billed by given its entry (<see cref="DssHarness.Core.Agents.AgentProviders.BilledBy"/>) - the
        /// one fact the run asks before any client is taken - so a model a run would refuse is refused here, before the run.
        /// </summary>
        private void BudgetPrices(
            DssHarness.Core.Agents.AgentProviders providers,
            IReadOnlyList<(DssHarness.Core.Configuration.AgentProviderConfig Provider, DssHarness.Core.Configuration.AgentModelConfig Model)> models,
            string? client,
            AgentDefinition? definition,
            bool runBudget,
            string where)
        {
            if ((!runBudget || _definition.Budget?.Usd is null) && definition?.MaxBudgetUsd is null)
            {
                return;
            }

            foreach (var (provider, model) in models)
            {
                var through = DssHarness.Core.Agents.AgentResolution.ClientOf(client, definition, provider);
                var billedBy = DssHarness.Core.Agents.AgentProviders.BilledBy(through, providers.ClientOf(through));

                if (DssHarness.Core.Agents.AgentPrices.BudgetRefusal(provider, model, billedBy) is { } priceless)
                {
                    _problems.Add($"{where} runs under a budget in dollars through client '{through}', and {priceless}");
                }
            }
        }

        /// <summary>Each model's effort checked against what it takes, by the one owner of the check (<see cref="DssHarness.Core.Agents.AgentEffortRules"/>).</summary>
        private void EffortRules(
            IReadOnlyList<(DssHarness.Core.Configuration.AgentProviderConfig Provider, DssHarness.Core.Configuration.AgentModelConfig Model)> models,
            string? tier,
            string? effort,
            IReadOnlyDictionary<string, string>? effortByModel,
            AgentDefinition? definition,
            string where)
            => _problems.AddRange(DssHarness.Core.Agents.AgentEffortRules.Problems(models, tier, effort, effortByModel, definition, where));

        /// <summary>Whether <paramref name="client"/> is one the tool builds in and reaches <paramref name="provider"/>'s type.</summary>
        private static bool ClientReaches(string client, DssHarness.Core.Configuration.AgentProviderConfig provider)
            => DssHarness.Core.Agents.AgentProviders.Reaches(client, provider);

        /// <summary>
        /// The work-item group: the <c>forEach</c> group the step <c>amend.whenAddScope</c> names is declared directly in,
        /// one instance of which builds each work item; <see langword="null"/> where the workflow has no amend block, or its
        /// whenAddScope names no step of the flow declared so.
        /// </summary>
        private Entry? WorkItemGroup()
            => _definition.Amend is { } amend
                && _byPath.GetValueOrDefault(amend.WhenAddScope) is { Parent: { Step.ForEach: not null } group }
                && !InRecovery(group)
                    ? group
                    : null;

        /// <summary>
        /// V46: <c>piece</c> is the part of a plan update one instance of the work-item group carries, so only a step of
        /// that group reads it; outside it, and in a workflow with no such group, no piece is there to read. A step of a
        /// used workflow whose gate this use closes (<see cref="HeldOffByItsUse"/>) never runs, so nothing it would read
        /// is read; one whose gate the use opens runs, and is held to the rule as any step is.
        /// </summary>
        private void PieceRead(Entry entry, WorkflowExpressions.Read read, string what)
        {
            if (WorkItemGroup() is not { } group)
            {
                _problems.Add($"'{what}' reads '{read.Whole}', whose 'piece' is the part of a plan update a work item carries, and the workflow has no work-item group: amend.whenAddScope names the step of a forEach group that builds one");
            }
            else if (!group.Owns(entry) && !HeldOffByItsUse(entry))
            {
                _problems.Add($"'{what}' reads '{read.Whole}', whose 'piece' is the part of a plan update a work item carries, and '{entry.Path}' is no step of the work-item group '{group.Path}'");
            }
        }

        /// <summary>
        /// Whether <paramref name="entry"/> says a <c>when</c> that reads one input of a used workflow alone: the used
        /// workflow's own gate over its steps, filled by each use, which holds what it gates off where a use fills the
        /// input so they do not run there.
        /// </summary>
        private static bool GatedByOneInput(Entry entry)
            => entry.Step.When is { } gated
                && WorkflowExpressions.ReadsOf(gated) is [var read]
                && read.Root == "input"
                && read.Names.Count == 2;

        /// <summary>
        /// Whether <paramref name="entry"/>, a step of a used workflow gated by one of its inputs
        /// (<see cref="GatedByOneInput"/>), is held off by the use around it: the use's with value - or, where it gives
        /// none, the input's default - is known where the file is read and makes the <c>when</c> false, so the step never
        /// starts where this use runs the workflow. A fill read from anything the run holds, or none at all, is known only
        /// when the use opens, and holds nothing off here. The gate is read as the run reads it when the step is drawn.
        /// </summary>
        private bool HeldOffByItsUse(Entry entry)
        {
            if (!GatedByOneInput(entry)
                || UseOf(entry) is not { Step.Use: { } use } holder
                || WorkflowExpressions.ReadsOf(entry.Step.When!) is not [{ Names: [_, { } named] }]
                || use.Inputs.FirstOrDefault(input => string.Equals(input.Name, named, StringComparison.Ordinal)) is not { } input
                || (holder.Step.With.GetValueOrDefault(named) ?? input.Default) is not { } given
                || Known(input, given) is not { } value)
            {
                return false;
            }

            try
            {
                return FlowExpression.Parse(entry.Step.When!).Evaluate(path => path.Root == "input" && path.Steps is [{ Key: { } key }, ..] && key == named
                        ? new FlowExpression.PathRead(value, 1)
                        : new FlowExpression.PathRead(FlowValue.Missing, 0))
                    is var gate && gate.IsScalar && gate.AsScalar(entry.Step.When!) is false;
            }
            catch (Exception ex) when (ex is UnresolvedReadException or ArgumentException)
            {
                // A gate that cannot be read with this fill fails the step where it is drawn: it holds nothing off.
                return false;
            }
        }

        /// <summary>
        /// The value a use fills <paramref name="input"/> with, where it is known before the run: an expression that reads
        /// nothing, read as the run reads it; a template with no placeholder, its text; a path or a name, as written. Null
        /// where it reads what only the run holds, or is not written whole.
        /// </summary>
        private static FlowValue? Known(FlowInput input, string given)
        {
            try
            {
                return input.Kind switch
                {
                    FlowInput.ValueKind => FlowExpression.Parse(given) is { } expression && expression.Reads().Count == 0
                        ? expression.Evaluate(_ => FlowValue.Missing)
                        : null,
                    FlowInput.TextKind => WorkflowExpressions.TemplateSyntaxProblem(given) is null && WorkflowExpressions.TemplateReads(given).Count == 0
                        ? FlowValue.Of(FlowTemplate.Fill(given, _ => new FlowExpression.PathRead(FlowValue.Missing, 0), out _))
                        : null,
                    _ => FlowValue.Of(given),
                };
            }
            catch (Exception ex) when (ex is UnresolvedReadException or ArgumentException)
            {
                return null;
            }
        }

        /// <summary>
        /// What the amend block says beyond each step it names being there: that it takes an amended request or a changed
        /// answer, from a text input (V44); that the step that plans an update is one step, raised by an update alone
        /// (V44); and that the three steps that carry its pieces are alternatives at one place of the work-item group,
        /// which expands from the items of the check it waits for (V46, V47). A step of the recovery, already refused for
        /// it, is held to nothing more.
        /// </summary>
        private void PlanUpdateRules(FlowAmend amend)
        {
            if (amend.Input is null && amend.ChangedAnswerMinutes is null)
            {
                _problems.Add("amend names neither input nor changedAnswerMinutes; at least one is named - the text input an amendment adds to, or the minutes the last answer can be changed for - and neither has a default");
            }
            else if (amend.Input is { } named && _definition.InputsByName.TryGetValue(named, out var input) && input.Kind != FlowInput.TextKind)
            {
                _problems.Add($"amend.input names '{named}', a {input.Kind} input; an amendment adds words to the request, which a text input holds");
            }

            if (_byPath.GetValueOrDefault(amend.Step) is { } planner && !InRecovery(planner))
            {
                PlannerRules(amend, planner);
            }

            if (_byPath.GetValueOrDefault(amend.WhenAddScope) is { } builder && !InRecovery(builder))
            {
                WorkItemRules(amend, builder);
            }
        }

        /// <summary>
        /// V44: the step that plans an update is raised as a handler is, by an update alone, in the newest instance of its
        /// scope - so one step, in no <c>forEach</c> and no <c>always</c> group; never raised by an ending, passed by,
        /// waiting or counted against a bound; going on with the work of a step of its own scope; and never sending the
        /// run back to itself.
        /// </summary>
        private void PlannerRules(FlowAmend amend, Entry planner)
        {
            var named = $"amend.step names '{amend.Step}'";

            if (GroupAround(planner, group => group.ForEach is not null) is { } many)
            {
                _problems.Add($"{named}, which is inside the forEach '{many.Path}': the step that plans an update is one step, not one of many");
            }

            if (AlwaysAround(planner) is { } always)
            {
                _problems.Add($"{named}, which is inside the always group '{always.Path}': the step that plans an update is raised while the run works, and an always group runs once, at the session's end");
            }

            var step = planner.Step;
            IReadOnlyList<string> raisedOtherwise =
            [
                .. new (string Key, bool Says)[] { ("on", step.On is not null), ("when", step.When is not null), ("dependsOn", step.DependsOn.Count > 0), ("maxVisits", step.MaxVisits is not null) }
                    .Where(key => key.Says)
                    .Select(key => key.Key),
            ];

            if (raisedOtherwise.Count > 0)
            {
                _problems.Add($"{named}, which says {string.Join(", ", raisedOtherwise)}: the step that plans an update is raised by an update alone - never by an ending, never passed by, waiting for nothing and counted against no bound");
            }

            // A resume nothing declares is the reason step's own refusal; one that reaches a step around the planner's
            // scope is this one's.
            if (step.Resume is { } resumed && Sibling(planner, resumed) is null && Scope(planner, resumed) is not null)
            {
                _problems.Add($"{named}, which goes on with '{resumed}', and '{resumed}' is not declared beside it: the step that plans an update goes on with the work of a step of its own scope");
            }

            if (amend.Then is { } then && string.Equals(then, step.Name, StringComparison.Ordinal))
            {
                _problems.Add(CallsItself("amend.step", $"sends the run back to itself (amend.then: {then})"));
            }
        }

        /// <summary>
        /// V46 and V47: <c>whenAddScope</c> names the step that builds a work item, declared directly in a <c>forEach</c>
        /// group - the work-item group - outside every <c>always</c> group, and no handler. <c>whenChangeScope</c> and
        /// <c>whenRemoveScope</c> each name that step or an alternative beside it, of its type, which runs in its place: with
        /// its dependencies, and named by its name, so the alternative says none of its own and no other step names it.
        /// The group expands from the items of a <c>validate-plan</c> step beside it, which it waits for, so the work items
        /// an update places in a round are checked with the round's own.
        /// </summary>
        private void WorkItemRules(FlowAmend amend, Entry builder)
        {
            if (builder.Parent is not { Step.ForEach: not null } group)
            {
                _problems.Add($"amend.whenAddScope names '{amend.WhenAddScope}', which is declared directly in no forEach group: a work item is built by a step of the group that runs once for each, the work-item group");
                return;
            }

            if (AlwaysAround(builder) is { } always)
            {
                _problems.Add($"amend.whenAddScope names '{amend.WhenAddScope}', which is inside the always group '{always.Path}': a work item is built while the run works, and an always group runs once, at the session's end");
            }

            if (builder.Step.On is { } on)
            {
                _problems.Add($"amend.whenAddScope names '{amend.WhenAddScope}', a handler raised by '{on.Step}': the step that builds a work item begins its instance's work, and a handler is raised by another step's ending");
            }

            var alternatives = new List<Entry>();

            foreach (var (key, named) in new[] { ("whenChangeScope", amend.WhenChangeScope), ("whenRemoveScope", amend.WhenRemoveScope) })
            {
                // A step that is not there, or a step of the recovery, was refused by name already.
                if (_byPath.GetValueOrDefault(named) is not { } alternative || InRecovery(alternative) || ReferenceEquals(alternative, builder))
                {
                    continue;
                }

                if (!string.Equals(alternative.ParentPath, builder.ParentPath, StringComparison.Ordinal))
                {
                    _problems.Add($"amend.{key} names '{named}', which is not declared beside '{amend.WhenAddScope}': the three are alternatives at one place of one forEach group");
                    continue;
                }

                if (!alternatives.Contains(alternative))
                {
                    alternatives.Add(alternative);
                    AlternativeRules(alternative, builder, $"amend.{key} names '{named}'");
                }
            }

            ExpandsFromACheck(group);
        }

        /// <summary>
        /// An alternative to the step that builds a work item runs in its place in an instance: of its type, with its
        /// dependencies and under its name, beginning the instance's work - so it says no dependsOn, on, resume or
        /// always of its own, and no other step names it. It may say one thing of its own: a <c>when</c> that reads
        /// one input of the used workflow it is a step of alone, the workflow's own gate, which holds the alternative
        /// off where a use fills that input so its alternatives do not run there.
        /// </summary>
        private void AlternativeRules(Entry alternative, Entry builder, string named)
        {
            var step = alternative.Step;

            if (!string.Equals(step.Type, builder.Step.Type, StringComparison.Ordinal))
            {
                _problems.Add($"{named}, {KindOf(step)}, and '{builder.Path}' is {KindOf(builder.Step)}: an alternative runs in that step's place, and is of its type");
            }

            IReadOnlyList<string> ownWiring =
            [
                .. new (string Key, bool Says)[] { ("dependsOn", step.DependsOn.Count > 0), ("on", step.On is not null), ("resume", step.Resume is not null), ("always", step.Always is true) }
                    .Where(key => key.Says)
                    .Select(key => key.Key),
            ];

            if (step.When is not null && !(UseOf(alternative) is not null && GatedByOneInput(alternative)))
            {
                ownWiring = [.. ownWiring, "when"];
            }

            if (ownWiring.Count > 0)
            {
                _problems.Add($"{named}, which says {string.Join(", ", ownWiring)}: it runs in the place of '{builder.Step.Name}', with that step's dependencies, and begins the work of its instance - the one thing it may say of its own is a when that reads one input of the used workflow alone, which holds it off where a use fills that input");
            }

            var why = $"'{step.Name}' runs in the place of '{builder.Step.Name}', and is named by that step's name";

            foreach (var other in _entries.Where(other => !ReferenceEquals(other, alternative) && !ReferenceEquals(other, _recover)))
            {
                var where = other.Step.IsGroup ? $"group '{other.Path}'" : $"step '{other.Path}'";

                if (other.Step.DependsOn.Any(name => ReferenceEquals(Sibling(other, name), alternative)))
                {
                    _problems.Add($"{where} depends on '{step.Name}': {why}");
                }

                if (other.Step.Resume is { } resumed && ReferenceEquals(Scope(other, resumed), alternative))
                {
                    _problems.Add($"{where} resumes '{step.Name}': {why}");
                }

                if (other.Step.On is { } on && ReferenceEquals(Sibling(other, on.Step), alternative))
                {
                    _problems.Add($"{where} is raised by '{step.Name}': {why}");
                }

                if (other.Step.Then is { } then && ReferenceEquals(Scope(other, then), alternative))
                {
                    _problems.Add($"{where} sends the run to '{step.Name}': {why}");
                }
            }
        }

        /// <summary>
        /// V47: where a workflow takes plan updates, the work-item group expands from the <c>items</c> of a
        /// <c>validate-plan</c> step declared beside it, which it waits for, directly or through the steps it depends on:
        /// the work items of a round are checked together with those an update places in it, by the check the group
        /// expands from. A <c>forEach</c> refused for itself already - not written whole, reading a name nothing provides
        /// or yielding what is no list - is read for nothing more: one defect is said once.
        /// </summary>
        private void ExpandsFromACheck(Entry group)
        {
            var forEach = group.Step.ForEach!;

            if (_refusedForEach.Contains(group.Path))
            {
                return;
            }

            var reads = WorkflowExpressions.ReadsOf(forEach);

            if (reads.Count == 0)
            {
                _problems.Add(UncheckedItems(group, forEach));
            }

            foreach (var read in reads)
            {
                if (WorkflowExpressions.IsRoot(read.Root)
                    || Resolve(group, read, within: false) is not { Step.Step.Type: "validate-plan", After: ["items"] } found
                    || read.Whole.Contains('[', StringComparison.Ordinal)
                    || !string.Equals(found.Step.ParentPath, group.ParentPath, StringComparison.Ordinal))
                {
                    _problems.Add(UncheckedItems(group, read.Whole));
                }
                else if (!WaitsFor(group, found.Step))
                {
                    _problems.Add($"group '{group.Path}' expands from '{read.Whole}', and waits for '{found.Step.Step.Name}' neither directly nor through the steps it depends on: the work items it runs are the ones that check checked, once it has ended");
                }
            }
        }

        private static string UncheckedItems(Entry group, string read)
            => $"group '{group.Path}' expands from '{read}', which no validate-plan step checked: with an amend block the work items of a round are checked together with those an update places in it, by the check the group expands from";

        /// <summary>
        /// Whether <paramref name="waiter"/> waits for <paramref name="awaited"/>, a step declared beside it: depends on it
        /// or is a handler raised by its ending - as <see cref="FlowGraph"/> draws the edges - or does either to a step
        /// that does, however many steps along.
        /// </summary>
        private bool WaitsFor(Entry waiter, Entry awaited)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var waiting = new Queue<Entry>([waiter]);

            while (waiting.TryDequeue(out var next))
            {
                foreach (var name in next.Step.On is { } raised ? next.Step.DependsOn.Append(raised.Step) : next.Step.DependsOn)
                {
                    if (Sibling(next, name) is not { } dependency || !seen.Add(dependency.Path))
                    {
                        continue;
                    }

                    if (ReferenceEquals(dependency, awaited))
                    {
                        return true;
                    }

                    waiting.Enqueue(dependency);
                }
            }

            return false;
        }

        /// <summary>The nearest group around <paramref name="entry"/> - never the step itself - that <paramref name="holds"/> says of, or <see langword="null"/>.</summary>
        private static Entry? GroupAround(Entry entry, Func<WorkflowStep, bool> holds)
        {
            for (var scope = entry.Parent; scope is not null; scope = scope.Parent)
            {
                if (holds(scope.Step))
                {
                    return scope;
                }
            }

            return null;
        }

        /// <summary>What a step is, as a refusal says it: a group, or a step of its type.</summary>
        private static string KindOf(WorkflowStep step) => step.IsGroup ? "a group" : $"a {step.Type} step";

        /// <summary>The text of a file the definition names, by the path it names it with; <see langword="null"/> where no files are given.</summary>
        private readonly Func<string, string?, string?>? _files;

        /// <summary>The configuration's providers and tiers, where the caller gave them: V38's config half is checked only under a configuration the caller chose.</summary>
        private readonly DssHarness.Core.Agents.AgentProviders? _providers;

        /// <summary>The names <c>codebaseGraph.languages</c> may hold, where the caller gave the registry's; <see langword="null"/> where it gave none.</summary>
        private readonly IReadOnlyList<string>? _languages;

        /// <summary>The configuration's web search, where the caller gave it: V55's names are checked only under a configuration the caller chose.</summary>
        private readonly Web.Search.WebSearchSources? _search;

        /// <summary>
        /// The lookup of the skills a prompt names, in the three places; <see langword="null"/> where the caller reads
        /// no files of the repository, which leaves every <c>{skill.&lt;name&gt;}</c> unlooked-for.
        /// </summary>
        private readonly AgentSkills? _skills;

        /// <summary>Each reason step's schema as a reading, by the path its <c>outputs</c> names; <see langword="null"/> where it types nothing.</summary>
        private readonly Dictionary<(string Workflow, string Named), JsonSchemaSubset.Reading?> _schemas = new();

        /// <summary>
        /// Each agent definition the definition's steps, its advisor and its reporter name, as its file read it - or
        /// null where the file does not read or no files are given, its problems said once by the first that names it.
        /// </summary>
        private readonly Dictionary<(string Workflow, string Agent), AgentDefinition?> _agents = new();

        /// <summary>The expressions being typed, by the step and the key: one whose reading needs its own is read as anything.</summary>
        private readonly HashSet<(Entry Entry, string Key)> _yielding = [];

        /// <summary>Every typing refusal said: one path read twice in one key is said once.</summary>
        private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

        /// <summary>Each built-in type's output schema as a reading, read once.</summary>
        private static readonly Lazy<IReadOnlyDictionary<string, JsonSchemaSubset.Reading>> BuiltIn = new(() =>
            WorkflowStepTypes.All
                .Where(type => type.Output is not null)
                .ToDictionary(type => type.Type, type => JsonSchemaSubset.Read(JsonNode.Parse(type.Output!)!), StringComparer.Ordinal));

        /// <summary>
        /// What each key that holds an expression must yield, and why, where it must be one kind (V16-V19, and the keys
        /// that take a list, checked as a <c>forEach</c> is); a key not here - a <c>leftover</c>, an output's
        /// <c>from</c> - takes anything.
        /// </summary>
        private static readonly IReadOnlyDictionary<string, (string Kind, string Why)> Yields = new Dictionary<string, (string Kind, string Why)>(StringComparer.Ordinal)
        {
            ["when"] = ("boolean", TrueOrFalse),
            ["filterWhen"] = ("boolean", TrueOrFalse),
            ["repeat.until"] = ("boolean", TrueOrFalse),
            ["repeat.blocking"] = ("boolean", TrueOrFalse),
            ["preAnswer.when"] = ("boolean", TrueOrFalse),
            ["forEach"] = ("array", "the group runs its steps once for each of its items"),
            ["items"] = ("array", "the plan check checks the work items it holds"),
            ["scope"] = ("array", "a plan update is the pieces it holds"),
            ["amend.scope"] = ("array", "a plan update is the pieces it holds"),
            ["questions"] = ("array", "the step shows each question it holds under its own"),
            ["writes"] = ("array", Listed),
            ["within"] = ("array", Listed),
            ["paths"] = ("array", Listed),
            ["except"] = ("array", Listed),
            ["stale"] = ("array", Listed),
            ["merge"] = ("array", Listed),
            ["rows"] = ("array", Listed),
            ["allow.new"] = ("array", Listed),
            ["allow.merge"] = ("array", Listed),
            ["preAnswer.quote.text"] = ("string", "a quote is the words looked for in what the person gave"),
        };

        /// <summary>Why a boolean is what a test reads.</summary>
        private const string TrueOrFalse = "nothing is read as true or as false but true and false";

        /// <summary>Why a key that takes a list is read as one.</summary>
        private const string Listed = "the key takes a list, written out or as an expression that yields one";

        /// <summary>Says a typing refusal, once however many times its read is met in one key.</summary>
        private void Report(string problem)
        {
            if (_reported.Add(problem))
            {
                _problems.Add(problem);
            }
        }

        /// <summary>
        /// Types one expression whose every read the definition holds: each path is held to what holds it - a step's
        /// schema, the engine's facts of it, an input, the run's own names - each operator to what it takes, and the
        /// whole to what its key reads (<see cref="Yields"/>).
        /// </summary>
        private void Types(Entry entry, string expression, string key, bool within)
        {
            var what = $"{entry.Path}.{key}";
            var yielded = Yield(entry, expression, key, within, Report);

            // A placeholder in an expression is filled as a template is: its path is held to what holds it all the same.
            foreach (var read in WorkflowExpressions.ReadsOf(expression).Where(read => read.Template))
            {
                _ = Typed(entry, read, what, within, template: true, Report);
            }

            if (Yields.TryGetValue(key, out var yields) && !yielded.MayBe(yields.Kind))
            {
                _problems.Add($"'{what}' yields {FlowExpression.Said(yielded)}, where {KindSaid(yields.Kind)} is read: {yields.Why}");
            }
        }

        /// <summary><paramref name="kind"/>, one the subset names, as a sentence says it.</summary>
        private static string KindSaid(string kind) => FlowExpression.Said(JsonSchemaSubset.Reading.Of(kind));

        /// <summary>
        /// What <paramref name="expression"/>, the <paramref name="key"/> of <paramref name="entry"/>, yields, saying each
        /// misuse to <paramref name="report"/> - or to nothing, where it is read only for what it yields. One whose
        /// reading needs its own, or that does not parse, may be anything.
        /// </summary>
        private JsonSchemaSubset.Reading Yield(Entry entry, string expression, string key, bool within, Action<string>? report)
        {
            if (!_yielding.Add((entry, key)))
            {
                return JsonSchemaSubset.Reading.Anything;
            }

            try
            {
                var what = $"{entry.Path}.{key}";

                // A path's refusal is said whole where the path is read; an operator's after the key's name.
                return FlowExpression.Parse(expression).TypeOf(
                    path => Reading(entry, path.Root, path.Steps, path.Whole, what, within, template: false, report),
                    problem => report?.Invoke($"'{what}' {problem}"));
            }
            catch (ArgumentException)
            {
                // Refused for its grammar where the key is checked.
                return JsonSchemaSubset.Reading.Anything;
            }
            finally
            {
                _yielding.Remove((entry, key));
            }
        }

        /// <summary>Checks one placeholder of a template, read in what <paramref name="readIn"/> says, and types it where the definition holds its name.</summary>
        private void TemplateRead(Entry entry, WorkflowExpressions.Read read, string what, ReadIn readIn = ReadIn.Key)
        {
            var before = _problems.Count;

            CheckRead(entry, read, what, readIn: readIn);

            if (_problems.Count == before)
            {
                _ = Typed(entry, read, what, within: false, template: true, Report);
            }
        }

        /// <summary>What one read yields, its names walked as keys: a placeholder takes no index.</summary>
        private JsonSchemaSubset.Reading Typed(Entry entry, WorkflowExpressions.Read read, string what, bool within, bool template, Action<string>? report)
            => Reading(entry, read.Root, [.. read.Names.Skip(1).Select(name => new FlowExpression.Segment(name, null))], read.Whole, what, within, template, report);


        /// <summary>
        /// What a path from <paramref name="root"/> through <paramref name="steps"/> yields, read from where
        /// <paramref name="entry"/> sits, its name already found by <see cref="CheckRead"/>: a step's output by its
        /// schema, the engine's facts of a step, an input or a param as a text, the work item of the nearest
        /// <c>forEach</c>, the step a handler's <c>source</c> names, the iteration before, and the run's own names - the
        /// texts of a question, the status, a plan update and a piece, the bar and the run facts among them. Where nothing
        /// holds the path, <paramref name="report"/> is told why and it may be anything; the names this check does not
        /// type - a skill, an issue, and a <c>source</c> in the recovery - may be anything.
        /// </summary>
        private JsonSchemaSubset.Reading Reading(
            Entry entry,
            string root,
            IReadOnlyList<FlowExpression.Segment> steps,
            string whole,
            string what,
            bool within,
            bool template,
            Action<string>? report)
        {
            var said = new Subject(whole, what, report);

            switch (root)
            {
                case "input" or "param":
                    if (root == "input"
                        && UseOf(entry) is { Step.Use: { } use } boundary
                        && steps is [{ Key: { } input }, ..]
                        && use.Inputs.FirstOrDefault(declared => string.Equals(declared.Name, input, StringComparison.Ordinal)) is { } filled)
                    {
                        // An input of a used workflow yields what the expression that fills it yields - its with value, or
                        // its default - read whole in the caller, where the use sits, as the run reads it when the use
                        // opens; or a string, the kinds that are templates, paths and names. One that fills it with
                        // nothing, or whose reading needs its own, may be anything.
                        var value = boundary.Step.With.GetValueOrDefault(input) ?? filled.Default;

                        return said.Walked(
                            filled.Kind is FlowInput.TextKind or FlowInput.FileKind or FlowInput.StepKind
                                ? JsonSchemaSubset.Reading.Of("string")
                                : string.IsNullOrWhiteSpace(value)
                                    ? JsonSchemaSubset.Reading.Anything
                                    : Yield(boundary, value, $"with.{input}", within: false, report: null),
                            $"input.{input}",
                            steps.Skip(1),
                            $"the input '{input}' of the workflow '{use.Name}'");
                    }

                    return steps is [{ Key: { } declared }, ..]
                        ? said.Walked(JsonSchemaSubset.Reading.Of("string"), $"{root}.{declared}", steps.Skip(1), $"{root} '{declared}'")
                        : JsonSchemaSubset.Reading.Anything;

                case "index" or "iteration" or "visit":
                    return said.Walked(JsonSchemaSubset.Reading.Of("integer"), root, steps, "the run");

                case "item":
                    return Nearest(entry, scope => scope.ForEach is not null) is { } group
                        ? said.Walked(Yield(group, group.Step.ForEach!, "forEach", within: false, report: null).Items ?? JsonSchemaSubset.Reading.Anything, root, steps, $"the work item of group '{group.Path}'")
                        : JsonSchemaSubset.Reading.Anything;

                case "run":
                    return RunReading(steps, template, said);

                case "carried":
                    return CarriedReading(steps, template, said);

                case "question" or "status" or "update" or "piece":
                    return TextsReading(root, steps, said);

                case "recommendations":
                    return said.Walked(JsonSchemaSubset.Reading.Of("string"), root, steps, "the bar for a recommendation");

                case "text":
                    return steps is [{ Key: { } named }, ..]
                        ? said.Walked(JsonSchemaSubset.Reading.Of("string"), $"text.{named}", steps.Skip(1), $"the text '{named}'")
                        : JsonSchemaSubset.Reading.Anything;

                case var fact when WorkflowExpressions.RunFacts.Contains(fact, StringComparer.Ordinal):
                    if (!template)
                    {
                        said.RefusedAs("which is a run fact, text a template reads, not a value for an expression: an expression reads the run by run.<name>");
                        return JsonSchemaSubset.Reading.Anything;
                    }

                    return said.Walked(JsonSchemaSubset.Reading.Of("string"), root, steps, "the run");

                case "previous":
                    {
                        var keys = steps.Where(step => step.Key is not null).Select(step => step.Key!).ToList();

                        if (FlowScope.Read(keys, _definition, ScopeOf(entry, within), out _) is not { } found
                            || _byPath.GetValueOrDefault(found.Step.Path) is not { } before)
                        {
                            return JsonSchemaSubset.Reading.Anything;
                        }

                        var (written, rest) = Split(steps, keys.Count - found.After.Count);
                        return StepReading(before, rest, $"previous{written}", said);
                    }

                case "source":
                    {
                        // A handler, or a step inside one, reads the step whose ending raised it; the recovery's steps
                        // and a step raised by a pre-existing issue read whichever step it was, which nothing types.
                        if (Nearest(entry, scope => scope.On is not null) is not { Step.On: { } on } handler
                            || Sibling(handler, on.Step) is not { } raised)
                        {
                            return JsonSchemaSubset.Reading.Anything;
                        }

                        var (member, written, rest) = Descend(raised, steps);
                        return StepReading(member, rest, $"source{written}", said);
                    }

                default:
                    {
                        if (WorkflowExpressions.IsRoot(root))
                        {
                            return JsonSchemaSubset.Reading.Anything;
                        }

                        List<string> names = [root, .. steps.Where(step => step.Key is not null).Select(step => step.Key!)];

                        if (FlowScope.Read(names, _definition, ScopeOf(entry, within), out _) is not { } found
                            || _byPath.GetValueOrDefault(found.Step.Path) is not { } step)
                        {
                            return JsonSchemaSubset.Reading.Anything;
                        }

                        var (written, rest) = Split(steps, names.Count - found.After.Count - 1);
                        return StepReading(step, rest, $"{root}{written}", said);
                    }
            }
        }

        /// <summary>
        /// One read being typed, and where its refusal goes: the read whole and the key it is read in, said as every
        /// refusal of a read is, <c>'{what}' reads '{whole}', and ...</c>.
        /// </summary>
        private sealed record Subject(string Whole, string What, Action<string>? Report)
        {
            /// <summary>
            /// What <paramref name="steps"/> walk to from <paramref name="start"/>, a value written
            /// <paramref name="at"/> that <paramref name="owner"/> says; where they find nothing, the refusal says so and
            /// the value may be anything.
            /// </summary>
            public JsonSchemaSubset.Reading Walked(JsonSchemaSubset.Reading start, string at, IEnumerable<FlowExpression.Segment> steps, string owner)
            {
                if (FlowExpression.Walk(start, at, steps, out var why) is { } walked)
                {
                    return walked;
                }

                Refused($"{owner} {why}");
                return JsonSchemaSubset.Reading.Anything;
            }

            /// <summary>Says why the read is refused, after the read itself.</summary>
            public void Refused(string why) => Report?.Invoke($"'{What}' reads '{Whole}', and {why}");

            /// <summary>Says the read is refused for what it is, with nothing after the read but <paramref name="why"/>.</summary>
            public void RefusedAs(string why) => Report?.Invoke($"'{What}' reads '{Whole}', {why}");
        }

        /// <summary>
        /// What a read of the run's own names yields: a value an expression reads, or a text a template alone takes;
        /// any other name the run has none of.
        /// </summary>
        private static JsonSchemaSubset.Reading RunReading(IReadOnlyList<FlowExpression.Segment> steps, bool template, Subject said)
        {
            var values = string.Join(", ", WorkflowExpressions.RunValues.Keys.Select(name => $"run.{name}"));
            var texts = string.Join(", ", WorkflowExpressions.RunTexts.Select(name => $"run.{name}"));

            if (steps is not [{ Key: { } name }, ..])
            {
                said.RefusedAs($"which names none of the run's own: an expression reads {values}, and a template {texts} too");
                return JsonSchemaSubset.Reading.Anything;
            }

            var under = steps.Skip(1);

            if (WorkflowExpressions.RunValues.TryGetValue(name, out var value))
            {
                return said.Walked(value, $"run.{name}", under, "the run");
            }

            if (WorkflowExpressions.RunTexts.Contains(name, StringComparer.Ordinal))
            {
                if (template)
                {
                    return said.Walked(JsonSchemaSubset.Reading.Of("string"), $"run.{name}", under, "the run");
                }

                said.RefusedAs("which is text for a template, not a value for an expression: the engine writes it from its step records for a person to read");
                return JsonSchemaSubset.Reading.Anything;
            }

            said.Refused($"the run has no '{name}': an expression reads {values}, and a template {texts} too");
            return JsonSchemaSubset.Reading.Anything;
        }

        /// <summary>
        /// The roots whose texts the engine gives by name - a question, the status, a plan update and a piece - each with
        /// what a refusal calls it and the texts it gives (<see cref="WorkflowExpressions"/> owns each list).
        /// </summary>
        private static readonly IReadOnlyDictionary<string, (string Owner, IReadOnlyList<string> Texts)> TextsUnder =
            new Dictionary<string, (string Owner, IReadOnlyList<string> Texts)>(StringComparer.Ordinal)
            {
                ["question"] = ("a question", WorkflowExpressions.QuestionTexts),
                ["status"] = ("the status", WorkflowExpressions.StatusTexts),
                ["update"] = ("a plan update", WorkflowExpressions.UpdateTexts),
                ["piece"] = ("a piece", WorkflowExpressions.PieceTexts),
            };

        /// <summary>What a read of a question, the status, a plan update or a piece yields: one of the texts it gives, and nothing else.</summary>
        private static JsonSchemaSubset.Reading TextsReading(string root, IReadOnlyList<FlowExpression.Segment> steps, Subject said)
        {
            var (owner, texts) = TextsUnder[root];

            if (steps is not [{ Key: { } name }, ..] || !texts.Contains(name, StringComparer.Ordinal))
            {
                said.Refused($"{owner} is {string.Join(", ", texts.Select(text => $"{root}.{text}"))}, and nothing else");
                return JsonSchemaSubset.Reading.Anything;
            }

            return said.Walked(JsonSchemaSubset.Reading.Of("string"), $"{root}.{name}", steps.Skip(1), owner);
        }

        /// <summary>What a read of what a commit carries yields: a text for the commit's message and its variables, and nothing else.</summary>
        private static JsonSchemaSubset.Reading CarriedReading(IReadOnlyList<FlowExpression.Segment> steps, bool template, Subject said)
        {
            if (steps is not [{ Key: { } name }, ..] || !WorkflowExpressions.CarriedTexts.Contains(name, StringComparer.Ordinal))
            {
                said.Refused($"what a commit carries is {string.Join(", ", WorkflowExpressions.CarriedTexts.Select(text => $"carried.{text}"))}, and nothing else");
                return JsonSchemaSubset.Reading.Anything;
            }

            if (!template)
            {
                said.RefusedAs("which is text for a template, not a value for an expression: the engine writes it from its step records for a person to read");
                return JsonSchemaSubset.Reading.Anything;
            }

            return said.Walked(JsonSchemaSubset.Reading.Of("string"), $"carried.{name}", steps.Skip(1), "the run");
        }

        /// <summary>
        /// What a read of <paramref name="step"/>, written <paramref name="start"/>, yields through
        /// <paramref name="rest"/>: one of the engine's facts of it - <c>latest</c> its own output - or its output.
        /// </summary>
        private JsonSchemaSubset.Reading StepReading(Entry step, IReadOnlyList<FlowExpression.Segment> rest, string start, Subject said)
        {
            if (rest is [{ Key: { } fact }, ..] && WorkflowExpressions.StepFacts.Contains(fact, StringComparer.Ordinal))
            {
                IReadOnlyList<FlowExpression.Segment> after = [.. rest.Skip(1)];

                return fact == "latest"
                    ? Output(step, after, $"{start}.{fact}", said)
                    : said.Walked(WorkflowExpressions.StepFactReadings[fact], $"{start}.{fact}", after, $"the engine's fact '{fact}' of step '{step.Path}'");
            }

            return Output(step, rest, start, said);
        }

        /// <summary>
        /// What the output of <paramref name="step"/>, written <paramref name="start"/>, yields through
        /// <paramref name="rest"/>: a reason step's by the schema file it names, a built-in type's by its built-in
        /// schema - a plan check's items by what its <c>items</c> yields - and a block's by what it gathers. A type with
        /// no output, and a group that gathers nothing, have nothing to read.
        /// </summary>
        private JsonSchemaSubset.Reading Output(Entry step, IReadOnlyList<FlowExpression.Segment> rest, string start, Subject said)
        {
            var path = step.Path;

            if (step.Step.IsGroup)
            {
                // The declared outputs of a workflow used as a step are the step's own, once it ends: each typed by
                // what its from-expression yields inside the used workflow, after every step of it.
                if (step.Step.Use is { } use)
                {
                    if (rest is [])
                    {
                        return JsonSchemaSubset.Reading.Of("object");
                    }

                    if (rest[0].Key is { } named && use.Outputs.FirstOrDefault(held => string.Equals(held.Name, named, StringComparison.Ordinal)) is { } declared)
                    {
                        // Typed by the whole expression, read where the use's own check reads it: within the use, once
                        // every step of it may have run.
                        var read = string.IsNullOrWhiteSpace(declared.From)
                            ? JsonSchemaSubset.Reading.Anything
                            : Yield(AfterItsSteps(step), declared.From, $"outputs.{named}.from", within: true, report: null);

                        return said.Walked(read, $"{start}.{named}", rest.Skip(1), $"the output '{named}' the workflow '{use.Name}' declares");
                    }
                }

                if (step.Step.Gather.Count == 0)
                {
                    said.Refused(rest is [{ Key: { } named }, ..]
                        ? $"group '{path}' holds no step '{named}' and gathers nothing"
                        : $"group '{path}' gathers nothing: its steps are read by their own names");
                    return JsonSchemaSubset.Reading.Anything;
                }

                if (rest is [])
                {
                    return JsonSchemaSubset.Reading.Of("object");
                }

                if (rest[0].Key is { } gathered && step.Step.Gather.Contains(gathered, StringComparer.Ordinal))
                {
                    return said.Walked(Gathered(step, gathered), $"{start}.{gathered}", rest.Skip(1), $"what group '{path}' gathers");
                }

                said.Refused(rest[0].Key is { } other
                    ? $"group '{path}' holds no step '{other}' and gathers none of that name"
                    : $"what group '{path}' gathers says '{start}' is an object, which is no list to index");
                return JsonSchemaSubset.Reading.Anything;
            }

            if (WorkflowStepTypes.Find(step.Step.Type ?? string.Empty) is not { } type)
            {
                return JsonSchemaSubset.Reading.Anything;
            }

            if (type.Type == "reason")
            {
                return step.Step.Outputs is { } outputs && SchemaOf(outputs, step.Step.WorkflowOf) is { } schema
                    ? said.Walked(schema, start, rest, $"the schema '{outputs}' of step '{path}'")
                    : JsonSchemaSubset.Reading.Anything;
            }

            if (!BuiltIn.Value.TryGetValue(type.Type, out var output))
            {
                said.Refused($"step '{path}', {Article(type.Type)} {type.Type} step, has no output to read");
                return JsonSchemaSubset.Reading.Anything;
            }

            // What a plan check checked is what its items key yields, so a work item it hands a forEach is typed as one.
            if (type.Type == "validate-plan" && rest is [{ Key: "items" }, ..] && step.Step.Items is { } items)
            {
                return said.Walked(Yield(step, items, "items", within: false, report: null), $"{start}.items", rest.Skip(1), $"the work items step '{path}' checks");
            }

            return said.Walked(output, start, rest, $"the output of step '{path}', {Article(type.Type)} {type.Type} step,");
        }

        /// <summary>
        /// The list a block gathers under <paramref name="name"/>: the items of that list in each member's output, and in
        /// what a nested group among them gathers of that name, as any one of them.
        /// </summary>
        private JsonSchemaSubset.Reading Gathered(Entry group, string name)
        {
            JsonSchemaSubset.Reading? items = null;
            var silent = new Subject(string.Empty, string.Empty, Report: null);

            foreach (var member in group.Step.Steps)
            {
                if (_byPath.GetValueOrDefault($"{group.Path}.{member.Name}") is not { } entry)
                {
                    continue;
                }

                var list = member.IsGroup
                    ? member.Gather.Contains(name, StringComparer.Ordinal) ? Gathered(entry, name) : null
                    : Output(entry, [], member.Name, silent).Member(name);

                if (list?.Items is { } each)
                {
                    items = items is null ? each : items.Or(each);
                }
            }

            return JsonSchemaSubset.Reading.ListOf(items ?? JsonSchemaSubset.Reading.Anything);
        }

        /// <summary>
        /// The schema the reason step's <paramref name="outputs"/> names, as a reading; <see langword="null"/> where no
        /// files are given, the file is not there, or it is no schema the subset reads - each said by whoever reads files.
        /// </summary>
        private JsonSchemaSubset.Reading? SchemaOf(string outputs, string? workflow = null)
        {
            if (_files is null)
            {
                return null;
            }

            if (!_schemas.TryGetValue((workflow ?? string.Empty, outputs), out var reading))
            {
                try
                {
                    reading = _files(outputs, workflow) is { } text
                        && JsonNode.Parse(text) is JsonObject schema
                        && JsonSchemaSubset.SubsetProblems(schema).Count == 0
                            ? JsonSchemaSubset.Read(schema)
                            : null;
                }
                catch (JsonException)
                {
                    reading = null;
                }

                _schemas[(workflow ?? string.Empty, outputs)] = reading;
            }

            return reading;
        }

        /// <summary>The nearest of <paramref name="entry"/> and the groups around it that <paramref name="holds"/> says, or <see langword="null"/>.</summary>
        private static Entry? Nearest(Entry entry, Func<WorkflowStep, bool> holds)
        {
            for (var scope = entry; scope is not null; scope = scope.Parent)
            {
                if (holds(scope.Step))
                {
                    return scope;
                }
            }

            return null;
        }

        /// <summary>
        /// The step <paramref name="steps"/> name inside <paramref name="step"/>, a member's name after its group's, with
        /// how much of the read that took and what is left after it.
        /// </summary>
        private (Entry Step, string Written, IReadOnlyList<FlowExpression.Segment> Left) Descend(Entry step, IReadOnlyList<FlowExpression.Segment> steps)
        {
            var taken = 0;

            while (taken < steps.Count
                && steps[taken].Key is { } name
                && step.Step.Steps.Any(each => each.Name == name)
                && _byPath.GetValueOrDefault($"{step.Path}.{name}") is { } member)
            {
                step = member;
                taken++;
            }

            return (step, string.Concat(steps.Take(taken).Select(each => $".{each.Key}")), [.. steps.Skip(taken)]);
        }

        /// <summary>
        /// <paramref name="steps"/> split after their first <paramref name="keys"/> keys: what those keys and the indexes
        /// among them write, and what is left.
        /// </summary>
        private static (string Written, IReadOnlyList<FlowExpression.Segment> Left) Split(IReadOnlyList<FlowExpression.Segment> steps, int keys)
        {
            var written = new StringBuilder();
            var index = 0;

            for (var counted = 0; index < steps.Count && counted < keys; index++)
            {
                written.Append(steps[index].IsKey ? $".{steps[index].Key}" : $"[{steps[index].Index}]");
                counted += steps[index].IsKey ? 1 : 0;
            }

            return (written.ToString(), [.. steps.Skip(index)]);
        }

        /// <summary>The article a type's name takes: <c>an orient step</c>, <c>a build step</c>.</summary>
        private static string Article(string type) => type[0] is 'a' or 'e' or 'i' or 'o' or 'u' ? "an" : "a";

        /// <summary>
        /// Checks every placeholder of the files a step is asked with - a reason step's <c>prompt</c> and
        /// <c>resumePrompt</c>, a pull request's <c>body</c> - against what the step can read from where it sits, each
        /// said at the line it first stands on: a name nothing provides, a path no schema holds, a name only another
        /// prompt reads, and in a <c>prompt</c>, which the first iteration is asked with too, the iteration before. A
        /// skill a reason step's prompt names is looked for by whoever reads the skills; a body is a template the engine
        /// fills and no agent reads, and reads what a step's own keys read.
        /// </summary>
        private void Prompts(Entry entry)
        {
            var step = entry.Step;
            var asked = new[]
            {
                ("prompt", step.Type == "reason" ? step.Prompt : null, ReadIn.StepPrompt),
                ("resumePrompt", step.Type == "reason" ? step.ResumePrompt : null, ReadIn.StepPrompt),
                ("body", step.Type == "pull-request" ? step.Body : null, ReadIn.Body),
            };

            foreach (var (key, declared, readIn) in asked)
            {
                if (declared is null)
                {
                    continue;
                }

                // A file read through an input of a used workflow is, at this place, the file the use fills that
                // input with; a file the step names itself is read as it is written, in its own workflow's directory.
                var file = AskedFile(entry, declared);
                var what = $"{entry.Path}.{key}";

                Placeholders($"step '{entry.Path}' {key} '{file}'", file, step.WorkflowOf, what, (read, at) =>
                {
                    if (read.Root == "previous" && key == "prompt")
                    {
                        _problems.Add($"{at} reads '{read.Whole}': a prompt is what the first iteration is asked with too, which has none before it; the iteration before is read in a resumePrompt");
                        return;
                    }

                    TemplateRead(entry, read, what, readIn);
                });
            }
        }

        /// <summary>
        /// The file a step is asked with, as its <paramref name="declared"/> key names it at this place: the key's own
        /// path where it names one, and where it reads an input of a used workflow - the one way a step is asked with a
        /// file it does not name (V63) - the file the use around it fills that input with, or the key as written where
        /// no fill is there to read, for the rule that refuses it says the rest.
        /// </summary>
        private string AskedFile(Entry entry, string declared)
            => declared.Contains('{') && InputRead(entry, declared, FlowInput.FileKind) is { Given: { } file }
                ? file
                : declared;

        /// <summary>
        /// Checks every skill a reason step's <c>prompt</c> and <c>resumePrompt</c> name, each at the line it first
        /// stands on: the lookup's answer - no such skill, with every place looked for it, or one that is there and
        /// cannot be used, its <c>allowed-tools</c> held to the capabilities of the step's agent - and a skill named
        /// twice in one prompt, for once is enough. Where the caller gave no lookup none is looked for, and where no
        /// files are given no prompt is read.
        /// </summary>
        private void Skills(Entry entry)
        {
            var step = entry.Step;

            if (step.Type != "reason")
            {
                return;
            }

            foreach (var (key, named) in new[] { ("prompt", step.Prompt), ("resumePrompt", step.ResumePrompt) })
            {
                if (named is null || AskedFile(entry, named) is not { } file || _files?.Invoke(file, step.WorkflowOf) is not { } text)
                {
                    continue;
                }

                var lines = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                var written = text.Split('\n');

                for (var line = 0; line < written.Length; line++)
                {
                    // Every placeholder of the line, not the reads it yields once each: a skill named twice on one line
                    // is named twice.
                    foreach (var piece in FlowTemplate.Pieces(written[line]).TakeWhile(each => each.Kind != FlowTemplate.PieceKind.Unclosed))
                    {
                        if (piece.Kind != FlowTemplate.PieceKind.Placeholder || piece.Name.Length == 0 || !AgentSkills.Names(piece.Name))
                        {
                            continue;
                        }

                        if (AgentSkills.NameIn(piece.Name, out var unnamed) is not { } name)
                        {
                            _problems.Add($"step '{entry.Path}' {key} '{named}' line {line + 1} names {unnamed}");
                            continue;
                        }

                        (lines.TryGetValue(name, out var at) ? at : lines[name] = []).Add(line + 1);
                    }
                }

                foreach (var (name, at) in lines)
                {
                    if (_skills is { } skills)
                    {
                        // The capabilities of the definition the step works by: its own, or the one the work it goes on
                        // with began with.
                        var (root, definition) = WorksAs(entry);
                        var may = definition?.May ?? AgentDefinition.AllCapabilities;

                        foreach (var problem in skills.Find(name) is { } found
                            ? found.ProblemsFor(entry.Path, root.Agent, may)
                            : [$"{key} '{named}' line {at[0]} names {{skill.{name}}}, and {skills.Missing(name)}"])
                        {
                            _problems.Add(problem);
                        }
                    }

                    if (at.Count > 1)
                    {
                        _problems.Add($"{key} '{named}' names {{skill.{name}}} {(at.Count == 2 ? "twice" : $"{at.Count} times")} (lines {Spelled(at)}): once is enough");
                    }
                }
            }
        }

        /// <summary>A list of line numbers as a refusal writes it: <c>14 and 40</c>, <c>14, 40 and 51</c>.</summary>
        private static string Spelled(IReadOnlyList<int> numbers)
            => numbers.Count == 1
                ? numbers[0].ToString(System.Globalization.CultureInfo.InvariantCulture)
                : $"{string.Join(", ", numbers.Take(numbers.Count - 1).Select(number => number.ToString(System.Globalization.CultureInfo.InvariantCulture)))} and {numbers[^1]}";

        /// <summary>
        /// Reads every agent definition the flow names, whatever the configuration: a reason step's, the advisor's and
        /// the reporter's. Its file's problems and its body's placeholders are said whatever providers were given, for
        /// they are the definition's own and not the configuration's half.
        /// </summary>
        private void AgentBodies()
        {
            foreach (var entry in _entries.Where(entry => entry.Step.Type == "reason"))
            {
                _ = DefinitionOf(entry.Step.Agent, entry.Step.WorkflowOf);
            }

            foreach (var (advisor, _, workflow, _) in Advisors())
            {
                _ = DefinitionOf(advisor.Agent, workflow);
            }

            if (_definition.Reporter is { } reporter)
            {
                _ = DefinitionOf(reporter.Agent);
            }
        }

        /// <summary>
        /// V43 and V45: the advisor's prompt and the reporter's two are checked as a step's are, from no place in the flow
        /// - they are asked about the run, and are run in none of it - so none of them reads a step by name. The advisor a
        /// use that governs itself declares is read in its own workflow's directory, inside the use: its names are that
        /// workflow's own.
        /// </summary>
        private void AgentPrompts()
        {
            foreach (var (advisor, where, workflow, use) in Advisors())
            {
                AgentPrompt(where, "prompt", advisor.Prompt, ReadIn.AdvisorPrompt, "the advisor's prompt reads no step by name; what the run stored of the steps a question is about is {question.record}", use, workflow);
            }

            if (_definition.Reporter is { } reporter)
            {
                const string Unnamed = "the reporter's prompts read no step by name; the run's directory, {status.records}, is all the reporter reads";

                AgentPrompt("reporter", "statusPrompt", reporter.StatusPrompt, ReadIn.StatusPrompt, Unnamed);
                AgentPrompt("reporter", "askPrompt", reporter.AskPrompt, ReadIn.AskPrompt, Unnamed);
            }
        }

        /// <summary>
        /// Checks one prompt of the advisor's or the reporter's, <paramref name="agent"/>'s <paramref name="key"/>: a read
        /// of the run's own names as a step's is, from the top of the flow after every step - or, for the advisor a use
        /// that governs itself declares, from inside <paramref name="use"/>, its file read in <paramref name="workflow"/>'s
        /// directory - and a read of a step by name refused for <paramref name="unnamed"/>.
        /// </summary>
        private void AgentPrompt(string agent, string key, string file, ReadIn readIn, string unnamed, Entry? use = null, string? workflow = null)
        {
            var asked = use is null
                ? new Entry(new WorkflowStep { Name = agent }, agent, AfterEveryStep, null)
                : new Entry(new WorkflowStep { Name = "advisor" }, $"{use.Path}.advisor", AfterEveryStep, use);
            var what = $"{agent}.{key}";

            Placeholders($"{agent} {key} '{file}'", file, workflow, what, (read, at) =>
            {
                if (WorkflowExpressions.IsRoot(read.Root))
                {
                    TemplateRead(asked, read, what, readIn);
                }
                else
                {
                    _problems.Add($"{at} reads '{read.Whole}': {unnamed}");
                }
            });
        }

        /// <summary>
        /// Checks each placeholder of <paramref name="file"/> once by <paramref name="check"/>, said at the line it first
        /// stands on (<paramref name="where"/> and the line): a refusal that names <paramref name="what"/>, the key it is
        /// read in, names the file and the line instead. A file not written whole is refused for it and read no further;
        /// without the files, none is read. <paramref name="owner"/> is the workflow whose directory holds the file,
        /// where it is one of a used workflow's.
        /// </summary>
        private void Placeholders(string where, string file, string? owner, string what, Action<WorkflowExpressions.Read, string> check)
        {
            if (_files?.Invoke(file, owner) is not { } text)
            {
                return;
            }

            if (WorkflowExpressions.TemplateSyntaxProblem(text) is { } broken)
            {
                _problems.Add($"{where} is not written whole: {broken}, and nothing it reads past there would be checked");
                return;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            var lines = text.Split('\n');

            for (var line = 0; line < lines.Length; line++)
            {
                foreach (var read in WorkflowExpressions.TemplateReads(lines[line]).Where(read => seen.Add(read.Whole)))
                {
                    var at = $"{where} line {line + 1}";
                    var before = _problems.Count;

                    check(read, at);

                    for (var index = before; index < _problems.Count; index++)
                    {
                        if (_problems[index].StartsWith($"'{what}' ", StringComparison.Ordinal))
                        {
                            _problems[index] = at + _problems[index][$"'{what}'".Length..];
                        }
                    }
                }
            }
        }

        /// <summary>
        /// A pull request's <c>base</c>, which <c>orient</c>, the first step, fills to say <c>orient.baseBranch</c> before
        /// any other step has run: it reads nothing but the run's inputs and <c>run.name</c>, and every pull request of the
        /// flow names the same one, since <c>orient.baseBranch</c> is one branch every step reads.
        /// </summary>
        private void PullRequests()
        {
            var requests = _entries.Where(entry => entry.Step.Type == "pull-request").ToList();

            foreach (var request in requests)
            {
                if (request.Step.Base is not { } named || WorkflowExpressions.TemplateSyntaxProblem(named) is not null)
                {
                    continue;
                }

                foreach (var read in WorkflowExpressions.TemplateReads(named).Where(read => read.Whole != "run.name"))
                {
                    if (read.Root != "input")
                    {
                        _problems.Add($"step '{request.Path}' opens its pull request against {{{read.Whole}}}: a pull request's base reads nothing but the run's inputs and run.name, so that orient, the first step, fills orient.baseBranch with it before any other step has run");
                    }
                    else if (UseOf(request) is { Step.Use: { } use })
                    {
                        // Inside a used workflow an input is the use's (V61), and the base is filled from the run's own
                        // before any use is opened: the read would name one input and be filled with another.
                        _problems.Add($"step '{request.Path}' opens its pull request against {{{read.Whole}}}, and is a step of the workflow '{use.Name}' used as a step: orient fills orient.baseBranch from the run's own inputs before any use is opened, and an input inside a used workflow is the one its use fills - name the branch, or read the run's input in a pull request of the run's own workflow");
                    }
                }
            }

            foreach (var other in requests.Skip(1).Where(other => !string.Equals(other.Step.Base, requests[0].Step.Base, StringComparison.Ordinal)))
            {
                _problems.Add($"steps '{requests[0].Path}' and '{other.Path}' open pull requests against different bases, {Base(requests[0])} and {Base(other)}; orient.baseBranch is one branch, which every step of the flow reads");
            }

            static string Base(Entry request) => request.Step.Base is { } named ? $"'{named}'" : "the remote's default branch";
        }

        /// <summary>
        /// The remote the run's branch goes to (<see cref="FlowCheckout.Remote"/>), which its pushes send it to and its pull
        /// request is opened on, is one: every push step that names a remote names the same one, so which repository that is
        /// never turns on which push step is declared first. A push step that names none goes to that one.
        /// </summary>
        private void Pushes()
        {
            var named = _entries.Where(entry => entry.Step.Type == "push" && entry.Step.Remote is not null).ToList();

            foreach (var other in named.Skip(1).Where(other => !string.Equals(other.Step.Remote, named[0].Step.Remote, StringComparison.Ordinal)))
            {
                _problems.Add($"steps '{named[0].Path}' and '{other.Path}' push to different remotes, '{named[0].Step.Remote}' and '{other.Step.Remote}'; the run's branch goes to one remote, which every push sends it to and its pull request is opened on");
            }
        }
    }
}
