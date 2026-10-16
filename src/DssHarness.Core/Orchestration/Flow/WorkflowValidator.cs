using System.Diagnostics;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The structural rules a workflow's definition must hold, checked on the parsed definition alone:
/// that every name a step, an expression or a template reads is declared, and that every key means
/// what its step can do with it.
/// </summary>
/// <remarks>
/// <para>
/// Run by <c>validate-workflow</c>, and by the engine at the start of a run; never by the
/// configuration reader, which reads no files. Every problem is collected and reported together,
/// because a definition usually goes wrong in the same way several times over.
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
    /// <summary>The question kinds a configuration can answer beforehand, and how each is spelled.</summary>
    public static readonly IReadOnlyList<string> QuestionKinds =
    [
        "loopBound", "visitBound", "openAgents",
        "foldNewRows", "foldLostRows", "foldOverCells", "foldTwoChanges", "foldConflicts", "foldOutside",
        "turnLimit", "truncated", "stepBudget", "contextLimit", "runBudget",
        "noChecks", "checksUnsettled", "checksPending",
        "failure", "pause", "ask",
    ];

    /// <summary>How a step's name is spelled: a lower-case word, with dashes within.</summary>
    /// <remarks>
    /// Dots separate the segments of a read, so a name may not hold one; and a read's first segment
    /// is resolved against names and reserved words alike, so a name may not be a reserved word.
    /// </remarks>
    private static readonly System.Text.RegularExpressions.Regex NamePattern = new("^[a-z][a-z0-9-]*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Returns every problem found, in the order the definition declares what they concern.</summary>
    /// <param name="definition">A workflow's parsed definition.</param>
    /// <param name="hasCommitTemplate">
    /// Whether the configuration declares a <c>commit.template</c>: where it does, a commit step
    /// says the values the template reads, and its own <c>message</c> is refused.
    /// </param>
    public static IReadOnlyList<string> Validate(WorkflowDefinition definition, bool hasCommitTemplate = false)
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

        /// <summary>Every step's full path, from the flow's root, to its entry.</summary>
        private readonly Dictionary<string, Entry> _byPath = new(StringComparer.Ordinal);

        internal Check(WorkflowDefinition definition, bool hasCommitTemplate)
        {
            _definition = definition;
            _hasCommitTemplate = hasCommitTemplate;

            DefinitionRules();
            Index(definition.Steps, parentPath: null);
            WorkflowAnswers();
            Steps();
            Amend();
            Outputs();
        }

        internal IReadOnlyList<string> Problems => _problems;

        /// <summary>One step, placed in the flow.</summary>
        private sealed record Entry(WorkflowStep Step, string Path, int Order, string? ParentPath)
        {
            /// <summary>Whether <paramref name="other"/> is declared inside this step's own subtree.</summary>
            public bool Owns(Entry other) => other.Path.StartsWith(Path + ".", StringComparison.Ordinal);
        }

        private void DefinitionRules()
        {
            Positive(_definition.MaxReasoningAgents, "maxReasoningAgents");
            Positive(_definition.MaxParallelProcesses, "maxParallelProcesses");
            Positive(_definition.MaxOpenAgents, "maxOpenAgents");
            Positive(_definition.MaxRetries, "maxRetries");
            Positive(_definition.MaxInstances, "maxInstances");
            Positive(_definition.HandoffSeconds, "handoffSeconds");

            if (_definition.Budget is { } budget)
            {
                if (budget.Usd is not > 0 && budget.Tokens is not > 0 && budget.Minutes is not > 0)
                {
                    _problems.Add("budget holds no usd, tokens or minutes; a budget with nothing to count is no budget. Give at least one, or take the section out to have none.");
                }

                if (budget.Usd is < 0)
                {
                    _problems.Add($"budget.usd cannot be below zero, found {budget.Usd.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }

                if (budget.Tokens is < 0)
                {
                    _problems.Add($"budget.tokens cannot be below zero, found {budget.Tokens}");
                }

                if (budget.Minutes is < 0)
                {
                    _problems.Add($"budget.minutes cannot be below zero, found {budget.Minutes.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }

                if (budget.ReservePercent is < 0 or > 100)
                {
                    _problems.Add($"budget.reservePercent is {budget.ReservePercent}, and so not a share between 0 and 100");
                }
            }

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
        private void Index(IReadOnlyList<WorkflowStep> steps, string? parentPath)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var step in steps)
            {
                var where = parentPath is null ? step.Name : $"{parentPath}.{step.Name}";

                if (WorkflowExpressions.IsReserved(step.Name))
                {
                    _problems.Add($"step '{where}' is named '{step.Name}', a word a definition reads for its own; a read could not tell the step from the word");
                }
                else if (!NamePattern.IsMatch(step.Name))
                {
                    _problems.Add($"step '{where}' is named '{step.Name}', which is not a lower-case word with dashes within; a read's segments are told apart by dots, so a name may not hold one");
                }

                if (!names.Add(step.Name))
                {
                    _problems.Add($"two steps of '{parentPath ?? "the flow's root"}' are named '{step.Name}'; what depends on it, or reads it, could only ever mean one of them");
                }

                var entry = new Entry(step, where, _entries.Count, parentPath);
                _entries.Add(entry);
                _byPath[where] = entry;

                if (step.Steps.Count > 0)
                {
                    Index(step.Steps, where);
                }
            }
        }

        private void WorkflowAnswers()
        {
            foreach (var (kind, answer) in _definition.Answers)
            {
                AnswerRules(kind, answer, "the workflow's answers", Siblings(null));
            }
        }

        /// <summary>Checks every step's own keys, once the whole flow is placed.</summary>
        private void Steps()
        {
            foreach (var entry in _entries)
            {
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

                Positive(entry.Step.Retries, $"{where}: retries");
                Positive(entry.Step.MaxVisits, $"{where}: maxVisits");

                if (entry.Step.On is { } raisedBy && raisedBy.Codes.Any(code => code < 1))
                {
                    _problems.Add($"{where} is raised by the codes {string.Join(", ", raisedBy.Codes)}, and a code below 1 is no code");
                }

                switch (type.Type)
                {
                    case "reason":
                        ReasonRules(entry);
                        break;

                    case "ci-read":
                    case "ci-wait":
                        Positive(entry.Step.Limit, $"{where}: limit");
                        Positive(entry.Step.AppearSeconds, $"{where}: appearSeconds");
                        Positive(entry.Step.WaitMinutes, $"{where}: waitMinutes");
                        Positive(entry.Step.PollSeconds, $"{where}: pollSeconds");
                        Positive(entry.Step.TimeoutMinutes, $"{where}: timeoutMinutes");
                        break;

                    case "fold":
                    case "delete-agent":
                    case "refresh":
                    case "rebase":
                        FoldRules(entry);
                        break;

                    case "validate-plan":
                        Expression(entry, entry.Step.Items ?? string.Empty, "items");

                        if (entry.Step.Scope is not null)
                        {
                            Expression(entry, entry.Step.Scope, "scope");
                        }

                        break;

                    case "commit":
                        if (entry.Step.Message is null && !_hasCommitTemplate)
                        {
                            _problems.Add($"{where} names no message and the configuration declares no commit.template; one of the two writes the commit's message");
                        }

                        if (entry.Step.Message is not null && _hasCommitTemplate)
                        {
                            _problems.Add($"{where} writes a message while the configuration declares a commit.template; the template writes the message, and the step says only the values it reads");
                        }

                        Positive(entry.Step.TimeoutMinutes, $"{where}: timeoutMinutes");
                        break;

                    case "push":
                    case "pull-request":
                        Positive(entry.Step.TimeoutMinutes, $"{where}: timeoutMinutes");
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
            }

            if (step.When is not null)
            {
                Expression(entry, step.When, "when");
            }

            if (step.Then is not null && step.On is null)
            {
                _problems.Add($"{where} says then without on; then is where a handler sends the run when it is done, and a step the schedule starts has nothing to send back");
            }

            if (step.On is { } raisedBy && Sibling(entry, raisedBy.Step) is null)
            {
                _problems.Add($"{where} is raised by '{raisedBy.Step}', which is no step declared beside it; a handler is raised by its own siblings");
            }

            if (step.Then is not null && Sibling(entry, step.Then) is null)
            {
                _problems.Add($"{where} sends the run to '{step.Then}', which is no step declared beside it");
            }

            foreach (var (kind, answer) in step.Answers)
            {
                AnswerRules(kind, answer, $"{where}: answers", Siblings(entry));
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

                if (!hasRounds && (repeat.Blocking is not null || repeat.BlockersLimitRounds is not null))
                {
                    _problems.Add($"{where} says {(repeat.Blocking is not null ? "blocking" : "blockersLimitRounds")} without rounds; past the rounds is the only place either is read");
                }

                Expression(entry, repeat.Until, "repeat.until");

                if (repeat.Blocking is not null)
                {
                    Expression(entry, repeat.Blocking, "repeat.blocking");
                }

                if (repeat.Leftover is not null)
                {
                    Expression(entry, repeat.Leftover, "repeat.leftover");
                }

                Positive(repeat.MaxIterations, $"{where}: repeat.maxIterations");
                Positive(repeat.Rounds, $"{where}: repeat.rounds");
                Positive(repeat.BlockersLimitRounds, $"{where}: repeat.blockersLimitRounds");
            }

            if (step.ForEach is not null)
            {
                Expression(entry, step.ForEach, "forEach");
            }
        }

        private void ReasonRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"step '{entry.Path}'";

            if (step.Agent is null && step.Resume is null)
            {
                _problems.Add($"{where} names no agent and resumes no step; a reason step runs an agent of its own, or goes on with the agent of the step it resumes");
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
                    _problems.Add($"{where} resumes '{step.Resume}', which is a {resumed.Step.Type} step; only a reason step leaves a conversation to go on with");
                }
                else if (resumed.Order > entry.Order)
                {
                    _problems.Add($"{where} resumes '{step.Resume}', which is declared after it; work cannot be gone on with before it is begun");
                }
                else if (!ReferenceEquals(resumed, entry) && step.Agent is not null)
                {
                    // Going on with its own work, a step keeps its own agent; another's work runs in
                    // that step's agent, and the step names none of its own.
                    _problems.Add($"{where} names the agent '{step.Agent}' and resumes '{step.Resume}'; a step that goes on with another's work runs in that step's agent, and cannot both go on with another's work and name another agent");
                }
            }

            if (step.Independent is true && step.Resume is not null)
            {
                _problems.Add($"{where} is independent and resumes '{step.Resume}'; independence is never being given the author's work, and resuming is going on with exactly that");
            }

            if (step.Access == "write")
            {
                if (step.In is null)
                {
                    _problems.Add($"{where} writes and names no 'in'; a writing step works in an agent's worktree, and the create-agent step that made it is the one to name");
                }
                else if (Scope(entry, step.In) is not { } worksIn)
                {
                    _problems.Add($"{where} works in '{step.In}', which is no step an enclosing group declares");
                }
                else if (worksIn.Step.Type != "create-agent")
                {
                    _problems.Add($"{where} works in '{step.In}', which is a {worksIn.Step.Type} step; a worktree is made by a create-agent step");
                }
                else if (worksIn.Order > entry.Order)
                {
                    _problems.Add($"{where} works in '{step.In}', which is declared after it; a worktree cannot be worked in before it is made");
                }
            }
            else if (step.In is not null)
            {
                _problems.Add($"{where} reads and names 'in'; a reading step has no worktree to work in, and reads the main tree");
            }

            if (step.Writes?.Expression is { } writes)
            {
                Expression(entry, writes, "writes");
            }

            if (step.AskRounds is not null && step.Ask is not true)
            {
                _problems.Add($"{where} says askRounds without ask; how often a step may ask is a step that asks's own bound");
            }

            Positive(step.AskRounds, $"{where}: askRounds");
        }

        private void FoldRules(Entry entry)
        {
            var step = entry.Step;
            var where = $"step '{entry.Path}'";

            if (step.Of is not null && step.Others is true)
            {
                _problems.Add($"{where} names 'of' and says 'others'; it works on one agent, or on every other one, and never both");
            }

            if (step.Of is not null)
            {
                if (Scope(entry, step.Of) is not { } named)
                {
                    _problems.Add($"{where} names '{step.Of}', which is no step an enclosing group declares");
                }
                else if (named.Step.Type != "create-agent")
                {
                    _problems.Add($"{where} names '{step.Of}', which is a {named.Step.Type} step; an agent is made by a create-agent step");
                }
            }

            if (step.Paths is not null && step.All is true)
            {
                _problems.Add($"{where} says paths and all; all is every path the main tree moved, in place of paths, and a step that said both would never tell which it weighed");
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

            if (step.Person is true && step.Answers.Count > 0)
            {
                _problems.Add($"{where} is a person's and answers its own question beforehand; what only a person may answer, no line of a configuration answers");
            }

            if (step.Questions is not null)
            {
                Expression(entry, step.Questions, "questions");
            }

            foreach (var before in step.PreAnswer)
            {
                Expression(entry, before.When, "preAnswer.when");

                if (!options.Contains(before.Answer))
                {
                    _problems.Add($"{where} is answered beforehand with '{before.Answer}', which is not one of the options it offers");
                }

                if (before.Quote is { } quote)
                {
                    Expression(entry, quote.Text, "preAnswer.quote.text");

                    if (!_definition.InputsByName.ContainsKey(quote.Input))
                    {
                        _problems.Add($"{where} quotes the input '{quote.Input}', which the workflow does not declare");
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
                Expression(planner with { Order = int.MaxValue }, amend.Scope, "amend.scope");

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

            Positive(amend.ChangedAnswerMinutes, "amend.changedAnswerMinutes");
            Positive(amend.MaxAddedIterations, "amend.maxAddedIterations");
        }

        private void Outputs()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);

            foreach (var output in _definition.Outputs)
            {
                DeclaredNameRules(output.Name, "output");

                if (!names.Add(output.Name))
                {
                    _problems.Add($"the workflow declares the output '{output.Name}' more than once");
                }

                // An output is read when the run has ended: the whole flow is behind it.
                Expression(new Entry(new WorkflowStep { Name = output.Name }, output.Name, int.MaxValue, null), output.From, $"output '{output.Name}'.from");
            }
        }

        /// <summary>Checks one answer a configuration gives a question kind beforehand.</summary>
        private void AnswerRules(string kind, FlowAnswer answer, string where, IReadOnlyList<Entry> siblings)
        {
            if (!QuestionKinds.Contains(kind, StringComparer.Ordinal))
            {
                _problems.Add($"{where} answers the kind '{kind}', which is no question kind. Known: {string.Join(", ", QuestionKinds.Select(known => $"'{known}'"))}.");
            }

            if (string.IsNullOrWhiteSpace(answer.Answer))
            {
                _problems.Add($"{where} answers the kind '{kind}' with nothing; an answer is spelled as the question's options spell it");
            }

            Positive(answer.Times, $"{where}: the times of the answer to '{kind}'");

            if (answer.Then is not null && siblings.All(sibling => !string.Equals(sibling.Step.Name, answer.Then, StringComparison.Ordinal)))
            {
                _problems.Add($"{where} sends the run to '{answer.Then}' on '{kind}', which is no step declared beside the step that asks");
            }
        }

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
                    foreach (var read in WorkflowExpressions.TemplateReads(text))
                    {
                        CheckRead(entry, read, $"{entry.Path}.{key}");
                    }
                }
            }
        }

        /// <summary>Checks one expression's reads against the flow's own names.</summary>
        private void Expression(Entry entry, string expression, string key)
        {
            foreach (var read in WorkflowExpressions.ReadsOf(expression))
            {
                CheckRead(entry, read, $"{entry.Path}.{key}");
            }
        }

        /// <summary>
        /// Checks one read - of an expression or of a template - against the steps, inputs and params
        /// the definition declares.
        /// </summary>
        private void CheckRead(Entry entry, WorkflowExpressions.Read read, string what)
        {
            var segments = read.Whole.Split('.');

            switch (read.Root)
            {
                case "input":
                    if (segments.Length < 2)
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
                    if (segments.Length < 2)
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

                    return;                case "previous":
                    {
                        // The iteration before this one: readable inside a repeat group, whose own
                        // until is read once an iteration has ended, and whose members name one
                        // another as the iteration before theirs.
                        var repeated = RepeatAround(entry);

                        if (repeated is null)
                        {
                            _problems.Add($"'{what}' reads '{read.Whole}', whose 'previous' names the iteration of a repeat group before this one, and no group holding '{entry.Path}' runs one");
                            return;
                        }

                        if (segments.Length > 1 && !NamedIn(repeated, segments[1]))
                        {
                            _problems.Add($"'{what}' reads '{read.Whole}', and '{segments[1]}' is no step of the group '{repeated.Path}' repeats");
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

                default:
                    if (WorkflowExpressions.IsReserved(read.Root))
                    {
                        return;
                    }

                    if (!Readable(entry, read.Whole))
                    {
                        _problems.Add($"'{what}' reads '{read.Whole}', which no step before '{entry.Path}', no input and no param provides");
                    }

                    return;
            }
        }

        /// <summary>
        /// Whether <paramref name="name"/> is readable where <paramref name="entry"/> sits: a step
        /// declared before it in a scope the step can see - its own siblings, or the siblings of a
        /// group holding it - or a step inside the step's own subtree, which its own loops read.
        /// </summary>
        private bool Readable(Entry entry, string name)
        {
            var segments = name.Split('.');

            // A read of several segments names a step by its path from the flow's root, or names a
            // visible step and reads a member of it. The longest path that is a step decides, so
            // that 'a.b' is the step 'a.b' where one is there, and a member 'b' of a step 'a' only
            // where none is.
            for (var length = segments.Length; length >= 1; length--)
            {
                var candidate = string.Join('.', segments[..length]);

                if (_byPath.TryGetValue(candidate, out var found))
                {
                    // Pre-order places every ancestor and every earlier step before the reader; a
                    // step's own subtree is its loops' to read, however late it is declared.
                    return found.Order < entry.Order || entry.Owns(found);
                }
            }

            return Visible(entry, segments[0]);
        }

        /// <summary>Whether a single name is visible where <paramref name="entry"/> sits, and declared before it.</summary>
        private bool Visible(Entry entry, string name)
        {
            for (var scope = entry; scope is not null; scope = scope.ParentPath is null ? null : _byPath.GetValueOrDefault(scope.ParentPath))
            {
                if (_entries.Any(other => other.ParentPath == scope.ParentPath && other.Order < entry.Order && string.Equals(other.Step.Name, name, StringComparison.Ordinal)))
                {
                    return true;
                }
            }

            // A step's own subtree is readable by the step itself: a group's loop reads its members.
            return _entries.Any(other => entry.Owns(other) && string.Equals(other.Step.Name, name, StringComparison.Ordinal));
        }

        /// <summary>The sibling of <paramref name="entry"/> named <paramref name="name"/>, or <see langword="null"/>.</summary>
        private Entry? Sibling(Entry entry, string name)
            => _entries.FirstOrDefault(other => other.ParentPath == entry.ParentPath
                && string.Equals(other.Step.Name, name, StringComparison.Ordinal));

        /// <summary>
        /// The step a name resolves to where <paramref name="entry"/> sits: itself, or a step of a
        /// scope an enclosing group holds - the scopes a step's own <c>of</c>, <c>in</c> and
        /// <c>resume</c> name, which reach the agents and worktrees of the groups around them.
        /// </summary>
        private Entry? Scope(Entry entry, string name)
        {
            for (var scope = entry; scope is not null; scope = scope.ParentPath is null ? null : _byPath.GetValueOrDefault(scope.ParentPath))
            {
                if (_entries.FirstOrDefault(other => other.ParentPath == scope.ParentPath
                        && string.Equals(other.Step.Name, name, StringComparison.Ordinal))
                    is { } found)
                {
                    return found;
                }
            }

            return null;
        }

        /// <summary>Every step declared beside <paramref name="entry"/> - at the flow's root, where it is the root.</summary>
        private IReadOnlyList<Entry> Siblings(Entry? entry)
            => [.. _entries.Where(other => other.ParentPath == (entry?.ParentPath ?? null))];

        /// <summary>
        /// Whether any group <paramref name="entry"/> is part of - itself, or one holding it - holds what
        /// <paramref name="holds"/> says: a group's own loop is its own expressions' to read, as its members' is.
        /// </summary>
        private bool Inside(Entry entry, Func<WorkflowStep, bool> holds)
        {
            for (var scope = entry; scope is not null; scope = scope.ParentPath is null ? null : _byPath.GetValueOrDefault(scope.ParentPath))
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
        /// iteration before this one <c>previous</c> names.
        /// </summary>
        private Entry? RepeatAround(Entry entry)
        {
            for (var scope = entry; scope is not null; scope = scope.ParentPath is null ? null : _byPath.GetValueOrDefault(scope.ParentPath))
            {
                if (scope.Step.Repeat is not null)
                {
                    return scope;
                }
            }

            return null;
        }

        /// <summary>Whether a step named <paramref name="name"/> is <paramref name="scope"/> itself or declared inside it.</summary>
        private bool NamedIn(Entry scope, string name)
            => string.Equals(scope.Step.Name, name, StringComparison.Ordinal)
                || _entries.Any(other => scope.Owns(other) && string.Equals(other.Step.Name, name, StringComparison.Ordinal));

        private void DeclaredNameRules(string name, string what)
        {
            if (WorkflowExpressions.IsReserved(name))
            {
                _problems.Add($"the {what} '{name}' is named with a word a definition reads for its own; name it another way");
            }
            else if (!NamePattern.IsMatch(name))
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

        private static string IsAre(int count) => count == 1 ? "is" : "are";
    }
}
