using System.Diagnostics;
using DssHarness.Core.Anchors;
using DssHarness.Core.Repository;

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
        ["recoveryBound"] = ["more", "skip", "abort"],
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

    /// <summary>The answers after which the same question can come again, and so are given a counted number of times.</summary>
    private static readonly IReadOnlyList<string> GoesOn = ["more", "retry", "compact"];

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
        ["foldNewRows"] = (step, _) => Folds(step),
        ["foldOutside"] = (step, _) => Folds(step),
        ["foldTwoChanges"] = (step, _) => Folds(step) || step.Type is "refresh" or "rebase",
        ["foldConflicts"] = (step, _) => Folds(step) || step.Type is "refresh" or "rebase",

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

    /// <summary>Whether <paramref name="step"/> folds an agent's work: a fold, or a deletion that does not discard it.</summary>
    private static bool Folds(WorkflowStep step)
        => step.Type == "fold" || (step.Type == "delete-agent" && step.DiscardUncommitted is not true);

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
    {
        ArgumentNullException.ThrowIfNull(definition);

        var check = new Check(definition, hasCommitTemplate);
        return check.Problems;
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

        internal Check(WorkflowDefinition definition, bool hasCommitTemplate)
        {
            _definition = definition;
            _hasCommitTemplate = hasCommitTemplate;

            DefinitionRules();
            Index(definition.Steps, parent: null);
            IndexRecover();
            Shadowing();
            Size();
            Cycle();
            WorkflowAnswers();
            Steps();
            Amend();
            Outputs();
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
            Within(_definition.MaxReasoningAgents, 1, 32, "maxReasoningAgents");
            Positive(_definition.MaxParallelProcesses, "maxParallelProcesses");
            Positive(_definition.MaxOpenAgents, "maxOpenAgents");
            Within(_definition.MaxRetries, 1, 20, "maxRetries");
            Within(_definition.BackoffSeconds, 1, WorkflowStep.MostBackoffSeconds, "backoffSeconds");
            Within(_definition.MaxInstances, 100, 100_000, "maxInstances");
            Within(_definition.HandoffSeconds, 30, 900, "handoffSeconds");

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
                Within(recover.MaxVisits, 1, FlowRecover.MostVisits, "recover.maxVisits");

                if (recover.OnFailure == WorkflowStep.Recover)
                {
                    _problems.Add(CallsItself("recover", "gives a failure of its own work to the recovery (onFailure: recover)"));
                }
                else if (recover.OnFailure == "continue")
                {
                    _problems.Add("recover passes a failure of its own work over (onFailure: continue); the step that gave it its failure would be neither put right nor failed - say pause or stop");
                }
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

            if (_definition.Advisor is { Kinds.Count: > 0 } advisor)
            {
                var unknown = advisor.Kinds.Where(kind => !QuestionKinds.Contains(kind, StringComparer.Ordinal)).ToList();

                if (unknown.Count > 0)
                {
                    _problems.Add($"advisor.kinds names {string.Join(", ", unknown.Select(kind => $"'{kind}'"))}, which {IsAre(unknown.Count)} no question kind. Known: {string.Join(", ", QuestionKinds.Select(kind => $"'{kind}'"))}.");
                }
            }
        }

        /// <summary>Walks the flow once, placing every step and checking every sibling list's names.</summary>
        private void Index(IReadOnlyList<WorkflowStep> steps, Entry? parent)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            var parentPath = parent?.Path;

            foreach (var step in steps)
            {
                var where = parentPath is null ? step.Name : $"{parentPath}.{step.Name}";

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
            if (_definition.Recover is not { } recover)
            {
                return;
            }

            if (!_entries.Any(entry => entry.Step.OnFailure == WorkflowStep.Recover || entry.Step.OnAddress == WorkflowStep.Recover))
            {
                _problems.Add("the workflow declares recover, and no step gives it a failure or an issue to fix; a step says onFailure: recover, itself or through a group around it, for its failures to reach it, or onAddress: recover for the pre-existing issues it decides to fix");
            }

            // The recovery's own onFailure is what a failure of one of its steps does where the step says none, as a
            // group's is for its members. Its place is the recovery's whatever a step of the flow is named, as
            // FlowScope reads the path.
            _recover = new Entry(new WorkflowStep { Name = FlowScope.Recovery, Steps = recover.Steps, OnFailure = recover.OnFailure }, FlowScope.Recovery, _entries.Count, null);
            _entries.Add(_recover);
            _byPath[FlowScope.Recovery] = _recover;
            Index(recover.Steps, _recover);
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
            var declared = _entries.Count(entry => !ReferenceEquals(entry, _recover));

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
                if (ReferenceEquals(entry, _recover))
                {
                    // The recovery's own place takes no keys: its steps are checked as every step is.
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

                switch (type.Type)
                {
                    case "reason":
                        ReasonRules(entry);
                        break;

                    case "build":
                    case "test":
                    case "run":
                    case "check-mutations":
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
                        Within(entry.Step.WaitMinutes, 1, 720, $"{where}: waitMinutes");
                        Within(entry.Step.PollSeconds, 10, 600, $"{where}: pollSeconds");
                        Within(entry.Step.TimeoutMinutes, 1, 240, $"{where}: timeoutMinutes");
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
                            _problems.Add($"{where} names no message and the configuration declares no commit.template; one of the two writes the commit's message");
                        }

                        Within(entry.Step.TimeoutMinutes, 1, 240, $"{where}: timeoutMinutes");
                        break;

                    case "push":
                    case "pull-request":
                        Within(entry.Step.TimeoutMinutes, 1, 240, $"{where}: timeoutMinutes");
                        break;

                    case "pause":
                        PauseRules(entry);
                        break;

                    default:
                        break;
                }

                Templates(entry, type);
            }
        }

        /// <summary>The keys every step takes, group or typed step alike.</summary>
        private void CommonStepRules(Entry entry)
        {
            var step = entry.Step;
            var where = step.IsGroup ? $"group '{entry.Path}'" : $"step '{entry.Path}'";

            Within(step.Retries, 1, 20, $"{where}: retries");
            HandlerRules(entry, where);

            if (step.OnFailure == WorkflowStep.Recover)
            {
                if (_definition.Recover is null)
                {
                    _problems.Add($"{where} gives its failure to the recovery (onFailure: recover), and the workflow declares no recover; declare one, or say what the failure does");
                }
                else if (InRecovery(entry))
                {
                    _problems.Add(CallsItself(where, "is a step of the recovery and gives its failure to the recovery"));
                }
            }

            // A reasoning step is never recovered, by its own word or a group's: its agent is tried again and on its
            // fallback, an account at its limit or a provider that fails pauses the run, and what still fails is asked.
            if (step.Type == "reason" && EffectiveOnFailure(entry) is { Policy: WorkflowStep.Recover } recovered)
            {
                _problems.Add(ReferenceEquals(recovered.Owner, entry)
                    ? $"{where} is a reasoning step and gives its failure to the recovery; a reasoning step is never recovered - its agent is tried again and on its fallback, and what still fails is asked: say pause"
                    : $"{where} is a reasoning step, and its failure would go to the recovery by the onFailure of '{recovered.Owner.Path}'; a reasoning step is never recovered - say pause on it");
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

            if (step.Then is not { } then)
            {
                return;
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

                if (repeat.Blocking is not null)
                {
                    Expression(entry, repeat.Blocking, "repeat.blocking", within: true);
                }

                if (repeat.Leftover is not null)
                {
                    Expression(entry, repeat.Leftover, "repeat.leftover", within: true);
                }

                Within(repeat.MaxIterations, 1, 20, $"{where}: repeat.maxIterations");
                Within(repeat.Rounds, 1, 20, $"{where}: repeat.rounds");
                Within(repeat.BlockersLimitRounds, 1, 20, $"{where}: repeat.blockersLimitRounds");

                if (repeat.BlockersUnbounded && _definition.Budget is null)
                {
                    _problems.Add($"{where}: repeat.blockersLimitRounds is false and the workflow has no budget: nothing would bound the loop");
                }
            }

            if (step.ForEach is not null)
            {
                Expression(entry, step.ForEach, "forEach");
            }
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

            if (step.Agent is null && step.Resume is null)
            {
                _problems.Add($"{where} names no agent and resumes no step; a reason step runs an agent of its own, or goes on with the agent of the step it resumes");
            }

            var resumesItself = string.Equals(step.Resume, step.Name, StringComparison.Ordinal);
            var resumesAnother = step.Resume is not null && !resumesItself;

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
                _problems.Add($"{where} resumes '{step.Resume}' and names {string.Join(", ", named)}; a step that goes on with another's work runs in that step's agent, on its model and in its worktree, and names none of agent, provider, client, tier, model, fallback, access, in or writes");
            }

            if (step.ResumePrompt is not null && !resumesItself)
            {
                _problems.Add($"{where} says resumePrompt and does not resume itself; resumePrompt is what a step that goes on with its own work is asked with when it runs again, and any other step is asked with its prompt");
            }

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
        /// The keys <paramref name="step"/> names that a step going on with another's work takes from that step - its
        /// agent and how the agent's model is chosen, its access, its worktree and what it writes - in the order the
        /// refusal lists them.
        /// </summary>
        private static IReadOnlyList<string> ResumedKeys(WorkflowStep step)
            => [.. new (string Key, bool Named)[]
                {
                    ("agent", step.Agent is not null),
                    ("provider", step.Provider is not null),
                    ("client", step.Client is not null),
                    ("tier", step.Tier is not null),
                    ("model", step.Model is not null),
                    ("fallback", step.Fallback.Count > 0),
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

            if (answer.Answer == "skip" && owner?.Step.Skippable is not true)
            {
                _problems.Add($"{where} answers '{kind}' with skip, and {(owner is null ? "the workflow is" : $"'{owner.Path}' is")} no skippable step; skip ends a step skipped only where it says skippable");
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

        /// <summary>Checks every template key of one typed step, against the step's own place in the flow.</summary>
        private void Templates(Entry entry, WorkflowStepTypes.StepType type)
        {
            var step = entry.Step;

            foreach (var key in type.TemplateKeys)
            {
                var texts = key switch
                {
                    "filter" => step.Filter is null ? [] : [step.Filter],
                    "exclude" => step.Exclude,
                    "labels" => step.Labels,
                    "inputs" => step.Inputs.Values.ToList(),
                    "base" => step.Base is null ? [] : [step.Base],
                    "branch" => step.Branch is null ? [] : [step.Branch],
                    "commit" => step.Commit is null ? [] : [step.Commit],
                    "message" => step.Message is null ? [] : [step.Message],
                    "variables" => step.Variables.Values.ToList(),
                    "title" => step.Title is null ? [] : [step.Title],
                    "question" => step.Question is null ? [] : [step.Question],
                    _ => throw new UnreachableException($"'{key}' is marked a template key of '{type.Type}', and nothing checks it."),
                };

                foreach (var text in texts)
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
                        CheckRead(entry, read, $"{entry.Path}.{key}");
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

            foreach (var read in WorkflowExpressions.ReadsOf(expression))
            {
                CheckRead(entry, read, $"{entry.Path}.{key}", within);
            }

            return true;
        }

        /// <summary>
        /// Checks one read - of an expression or of a template - against the steps, inputs and params
        /// the definition declares.
        /// </summary>
        private void CheckRead(Entry entry, WorkflowExpressions.Read read, string what, bool within = false)
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

                    if (!_definition.ParamsByName.ContainsKey(segments[1]))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', and the workflow declares no param '{segments[1]}'");
                    }

                    return;

                case "item":
                    if (!Inside(entry, group => group.ForEach is not null))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', whose 'item' names the work item of a forEach group, and no group holding '{entry.Path}' runs one");
                    }

                    return;

                case "previous":
                    {
                        // The iteration before this one: readable inside a repeat group, whose loop's
                        // own keys (within) are read once an iteration has ended, and whose members
                        // name one another as the iteration before theirs. A repeat group's other
                        // keys are read before its first iteration, so only a group around it
                        // answers for them. The step it names is found as a bare name is, from where
                        // it is read, and is one the repeat holds.
                        var repeated = RepeatAround(within ? entry : entry.Parent);

                        if (repeated is null)
                        {
                            _problems.Add($"'{what}' reads '{read.Whole}', whose 'previous' names the iteration of a repeat group before this one, and no group holding '{entry.Path}' runs one");
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

                case "skill":
                    _problems.Add($"'{what}' names a skill, and a skill's text is a prompt's alone; the step's own keys read the run's names, never a skill");
                    return;

                case "source":
                    if (!Inside(entry, scope => scope.On is not null) && !InRecovery(entry) && entry.Step.Raised is null)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', whose 'source' names the step whose ending raised a handler, and '{entry.Path}' is neither a handler, nor in one, nor a step of the recovery, nor one raised by a pre-existing issue");
                    }

                    return;

                case "issue":
                    if (!InRecovery(entry) && entry.Step.Raised is null)
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', whose 'issue' is the pre-existing issue a step reported, and '{entry.Path}' is neither raised by one nor a step of the recovery that fixes one");
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
        /// writes one, with the step that writes it; <see langword="null"/> where none does.
        /// </summary>
        private (string Policy, Entry Owner)? EffectiveOnFailure(Entry entry)
        {
            for (var scope = entry; scope is not null; scope = scope.Parent)
            {
                if (scope.Step.OnFailure is { } policy)
                {
                    return (policy, scope);
                }
            }

            return null;
        }

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

        /// <summary>Whether <paramref name="entry"/> is one of the recovery's steps, or inside one.</summary>
        private bool InRecovery(Entry entry) => _recover is not null && _recover.Owns(entry);

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
        /// <paramref name="holds"/> says: a group's own loop is its own expressions' to read, as its members' is.
        /// </summary>
        private bool Inside(Entry entry, Func<WorkflowStep, bool> holds)
        {
            for (var scope = entry; scope is not null; scope = scope.Parent)
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
        /// iteration before this one <c>previous</c> names; none where <paramref name="entry"/> is none, above the top level.
        /// </summary>
        private Entry? RepeatAround(Entry? entry)
        {
            for (var scope = entry; scope is not null; scope = scope.Parent)
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
            else if (!FlowScope.IsWellFormedName(name))
            {
                _problems.Add($"the {what} '{name}' is not a lower-case word with dashes within");
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

        private void Within(int? value, int least, int most, string what)
        {
            if (value < least || value > most)
            {
                _problems.Add($"{what} must be from {least.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)} to {most.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)}, found {value}");
            }
        }

        private static string IsAre(int count) => count == 1 ? "is" : "are";
    }
}
