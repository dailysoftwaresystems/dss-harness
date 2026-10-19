using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>How one instance of a step stands while a run works.</summary>
public enum FlowInstanceState
{
    /// <summary>Declared, and the run has not reached it.</summary>
    NotStarted,

    /// <summary>Running now, or between two attempts of one visit.</summary>
    Running,

    /// <summary>Its work ended as the run wanted it, whatever it found.</summary>
    Done,

    /// <summary>The flow decided not to run it: a false <c>when</c>, a checkpoint push while the switch is off, a skip an answer gave.</summary>
    Skipped,

    /// <summary>Its work ended failed, and nothing passed the failure over.</summary>
    Failed,

    /// <summary>Waiting on a question it asked or its failure raised; it holds nothing while it waits.</summary>
    Paused,

    /// <summary>Never reached: something it depends on failed and was neither handled nor passed over, or the run ended first.</summary>
    NotRun,
}

/// <summary>How a whole run stands.</summary>
public enum FlowRunStatus
{
    /// <summary>Steps are running, or nothing has ended the run yet.</summary>
    Running,

    /// <summary>Everything ended as asked; failures there were were passed over and are counted and listed.</summary>
    Done,

    /// <summary>A failure nothing passed over ended it.</summary>
    Failed,

    /// <summary>A question is open, and nothing more can run until it is answered.</summary>
    Paused,
}

/// <summary>How a session of a run ended: its status, the failures it counted, the questions it leaves open.</summary>
/// <param name="Status">How the run stands at the session's end.</param>
/// <param name="PassedOver">The instances whose failures the run passed over (<c>onFailure: continue</c>), in the order they ended.</param>
/// <param name="Failed">The instances whose failures ended the run failed, in the order they ended.</param>
/// <param name="OpenQuestions">The questions the session leaves open, in the order they were raised.</param>
/// <param name="Summary">The session in words: <c>done; 2 steps failed and were passed over: ci, items[3].verify</c>.</param>
public sealed record FlowSessionEnd(
    FlowRunStatus Status,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<string> Failed,
    IReadOnlyList<FlowQuestion> OpenQuestions,
    string Summary);

/// <summary>
/// One instance of a step of a run, as the coordinator holds it: its state, its output, what it ended with, and what
/// the run knows of its legs. Its id is as <see cref="FlowInstanceId"/> spells it (<c>rounds#2.items[1].verify</c>),
/// and its path is its declaration's among the flow, a member's under its group's.
/// </summary>
public sealed class FlowInstance(string id, WorkflowStep step, string path)
{
    /// <summary>The instance's id, as <see cref="FlowInstanceId"/> spells it.</summary>
    public string Id { get; } = id;

    /// <summary>The step as it was declared.</summary>
    public WorkflowStep Step { get; } = step;

    /// <summary>The declaration's path among the flow.</summary>
    public string Path { get; } = path;

    /// <summary>How this instance stands.</summary>
    public FlowInstanceState State { get; internal set; } = FlowInstanceState.NotStarted;

    /// <summary>Which attempt of this visit has run: 1 for the first, one more for each retry.</summary>
    public int Attempt { get; internal set; } = 1;

    /// <summary>The step's output, as later steps read it; <see langword="null"/> where it wrote none.</summary>
    public JsonNode? Output { get; internal set; }

    /// <summary>The code the step's own work ended with.</summary>
    public int? Exit { get; internal set; }

    /// <summary>Why the step ended as it did, where the code alone cannot say.</summary>
    public string? Error { get; internal set; }

    /// <summary>Why the flow decided not to run it, where it is <see cref="FlowInstanceState.Skipped"/>.</summary>
    public string? SkippedBecause { get; internal set; }

    /// <summary>Why the run never reached it, where it is <see cref="FlowInstanceState.NotRun"/>.</summary>
    public string? NotRunBecause { get; internal set; }

    /// <summary>Whether the run passed this failure over (<c>onFailure: continue</c>), so what depends on it runs on its output as Missing.</summary>
    public bool PassedOver { get; internal set; }

    /// <summary>
    /// The kind of the question whose <c>fail</c> answer ended this step, where one did: the engine's fact
    /// <c>&lt;step&gt;.question</c>, which a handler reads. <see langword="null"/> for every other ending.
    /// </summary>
    public string? EndedByQuestion { get; internal set; }

    /// <summary>Whether this step runs in the always phase: it says <c>always</c>, or a group around it does.</summary>
    public bool IsAlways { get; internal set; }

    /// <summary>The work item of the <c>forEach</c> this instance is one of, or Missing outside every one.</summary>
    public FlowValue Item { get; internal set; } = FlowValue.Missing;

    /// <summary>The item's index, from 0, or <see langword="null"/> outside every <c>forEach</c>.</summary>
    public int? Index { get; internal set; }

    /// <summary>The iteration of the <c>repeat</c> this instance is one of, from 1, or <see langword="null"/> outside every one.</summary>
    public int? Iteration { get; internal set; }

    /// <summary>Every leg the step ran, with each one's own verdict; empty for the steps that run no legs.</summary>
    public IReadOnlyList<FlowLeg> Legs { get; internal set; } = [];

    /// <summary>The red legs alone, in the order they ran; the step's failure names the worst of them.</summary>
    public IReadOnlyList<FlowLeg> RedLegs { get; internal set; } = [];

    /// <summary>The answers the questions of this visit were given, in the order they were given.</summary>
    public IReadOnlyList<FlowAnswerGiven> Answers => _answers;

    internal readonly List<FlowAnswerGiven> _answers = [];
    internal Scope? DeclaredScope;
    internal FlowInstance? GroupOf;
    internal Scope? OwnScope;
    internal readonly List<Scope> Children = [];
    internal (int Exit, string? Error, string Member)? Failing;
    internal int ExtraIterations;
    internal readonly List<FlowClaim> HeldClaims = [];
    internal readonly HashSet<string> LegsInCopies = [];
    internal readonly HashSet<string> LegsInTree = [];
    internal readonly HashSet<string> CopiesMade = [];
    internal readonly HashSet<string> TreeLegsEnded = [];
}

/// <summary>
/// The coordinator: one single-threaded owner of the instance graph of a run. Step runners run as tasks and post their
/// completions, and the events of their legs, to a channel; the coordinator alone changes the graph. Steps that are
/// ready at once are started in a fixed order, declaration order and then instance id.
/// </summary>
/// <remarks>
/// <para>
/// This is the engine's core, in memory: the instances of a workflow's steps and their bounds, the claims a step holds
/// on trees, the limits a run admits against all-or-nothing, the retries of an incomplete step, what a step that runs
/// legs ends with - each leg's own verdict read on its own - the failure policies, <see cref="FlowInstanceState.Skipped"/>
/// and <see cref="FlowInstanceState.NotRun"/>, the <c>always</c> steps and what a block gathers when it ends. Nothing
/// here writes a file: the run on disk - the records, <c>flow.json</c>, the event log, the resume - is built on this,
/// and the step runner it calls is given, not looked up: the tests give fakes, and the services each type calls come
/// with the slices that own them.
/// </para>
/// <para>
/// What is not here, by the plan's own order: the safe pause, the hard stop and the budget, the handlers an ending
/// raises (<c>on</c>, <c>then</c>, and the visits a redirect sends the run back for), a repeat's <c>rounds</c> and
/// <c>blocking</c>, and the recovery. A workflow that asks this coordinator to run either of the last two is refused
/// loudly when the run meets it, never quietly misplayed.
/// </para>
/// </remarks>
public sealed class FlowCoordinator
{
    private readonly WorkflowDefinition _definition;
    private readonly IFlowStepRunner _runner;
    private readonly IFlowRunnerFacts _runnerFacts;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _inputs;

    private readonly Channel<Message> _messages = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, FlowInstance> _instances = new(StringComparer.Ordinal);
    private readonly List<FlowInstance> _created = [];
    private readonly List<Scope> _scopes = [];
    private readonly List<FlowQuestion> _questions = [];
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowInstance> _answerable = new(StringComparer.Ordinal);
    private readonly List<FlowInstance> _passedOver = [];
    private readonly List<FlowInstance> _failed = [];
    private readonly List<Wake> _wakes = [];
    private readonly Dictionary<string, FlowInstance> _agentsOpen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _declarationOrder = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _answerSpent = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _answerWhere = new(StringComparer.Ordinal);

    private Scope _top = null!;
    private bool _stopping;
    private bool _alwaysPhase;
    private bool _inSession;
    private bool _answeredAtOnce;
    private int _questionsRaised;
    private FlowSessionEnd? _last;
    private Task<Message>? _pending;

    /// <summary>Starts a run of <paramref name="definition"/> over <paramref name="runner"/>.</summary>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="runner">The step runner every step of the run is given to.</param>
    /// <param name="runnerFacts">What the engine asks of the runners a workflow names.</param>
    /// <param name="clock">How the run waits: every retry waits by this.</param>
    /// <param name="inputs">The values the run is started with, by input name.</param>
    /// <exception cref="HarnessException">An input the run needs is missing, or a value is outside the ones its input takes.</exception>
    public FlowCoordinator(
        WorkflowDefinition definition,
        IFlowStepRunner runner,
        IFlowRunnerFacts runnerFacts,
        TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? inputs = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(runnerFacts);

        _definition = definition;
        _runner = runner;
        _runnerFacts = runnerFacts;
        _clock = clock ?? TimeProvider.System;
        _inputs = StartValues(definition, inputs);

        foreach (var (path, _) in FlowScope.Declared(definition))
        {
            _declarationOrder.Add(path, _declarationOrder.Count);
        }
    }

    /// <summary>Every instance the run has made so far, by its id.</summary>
    public IReadOnlyDictionary<string, FlowInstance> Instances => _instances;

    /// <summary>Every question the run has raised, answered and open, in the order raised.</summary>
    public IReadOnlyList<FlowQuestion> Questions => _questions;

    /// <summary>The questions the run leaves open, in the order raised.</summary>
    public IReadOnlyList<FlowQuestion> OpenQuestions => [.. _questions.Where(question => _open.Contains(question.Id))];

    /// <summary>How the run stands now.</summary>
    public FlowRunStatus Status
    {
        get
        {
            if (_last is { } ended)
            {
                return ended.Status;
            }

            if (_created.Any(instance => instance.State is FlowInstanceState.Running) || _wakes.Count > 0)
            {
                return FlowRunStatus.Running;
            }

            return _open.Count > 0 ? FlowRunStatus.Paused : _stopping || _failed.Count > 0 ? FlowRunStatus.Failed : FlowRunStatus.Done;
        }
    }

    /// <summary>
    /// Drives the run until nothing more can run: a session. A session ends done, failed, or paused on an open
    /// question; a paused run goes on when a question is answered (<see cref="TryAnswer(string, string, out string?)"/>) and this is called again.
    /// </summary>
    /// <param name="cancellationToken">Stops the waiting; nothing of the run is changed by a cancellation.</param>
    public async Task<FlowSessionEnd> RunAsync(CancellationToken cancellationToken)
    {
        if (_top is null)
        {
            _top = NewScope(string.Empty, string.Empty, null, null, FlowValue.Missing, null, null, null);
        }

        if (_inSession)
        {
            throw new InvalidOperationException("A session of this run is already being driven; the coordinator owns one at a time.");
        }

        _inSession = true;

        try
        {
            while (true)
            {
                Progress();

                if (_answeredAtOnce)
                {
                    // A configuration answered a question this pass raised, in the place of a person: its answer has
                    // already changed the graph, so the loop goes round again rather than wait on what asked.
                    _answeredAtOnce = false;
                    continue;
                }

                var started = Admit();

                if (_answeredAtOnce)
                {
                    // A pause step this pass started asked and was answered at once, or its question could not be
                    // filled and it failed: either way the graph changed inside Admit, and what follows from it has
                    // yet to be drawn - so the loop goes round again rather than end over work it never ran.
                    _answeredAtOnce = false;
                    continue;
                }

                // Anything to wait for: a step in flight - a leaf, for a group is only bookkeeping over its members
                // and a group whose member waits on a question can run nothing until it is answered - a retry's time
                // coming, or a completion still unread. Where nothing runs, nothing is due and this pass started
                // nothing, no step the run has left can ever start - one held behind a limit that nothing will free
                // among them - and the session ends rather than wait for it; what never started is NotRun.
                if (_created.Any(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup) || _wakes.Count > 0 || started > 0)
                {
                    await WaitNextAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (TryEnterAlwaysPhase())
                {
                    continue;
                }

                break;
            }
        }
        finally
        {
            _inSession = false;
        }

        _last = Finish();
        return _last;
    }

    /// <summary>
    /// Answers a question the run asked: the answer must name an open question and be one it takes - <c>abort</c> is
    /// always taken. What an answer does is the question's own: <c>retry</c> runs the step again as a new attempt,
    /// <c>skip</c> ends it skipped, <c>more</c> gives a loop one more iteration, <c>accept</c> takes an ending as it
    /// stands, <c>fail</c> ends the step failed with the question's kind, <c>abort</c> ends the run - and a step that
    /// asked is asked again with the answer among what it is given.
    /// </summary>
    /// <param name="question">The question's id.</param>
    /// <param name="answer">The answer, spelled as the question's options spell it.</param>
    /// <param name="problem">Why the answer was not taken, where it was not.</param>
    /// <returns>Whether the answer was taken.</returns>
    public bool TryAnswer(string question, string answer, out string? problem)
        => TryAnswer(question, answer, null, out problem);

    /// <summary>
    /// Answers a question the run asked, with the text an answer can carry: a <c>pause</c> step's option that names
    /// one in its <c>text</c> needs it, and an option that names none refuses it. <c>abort</c> is always taken.
    /// </summary>
    /// <param name="question">The question's id.</param>
    /// <param name="answer">The answer, spelled as the question's options spell it.</param>
    /// <param name="text">The text given with the answer, where it takes one.</param>
    /// <param name="problem">Why the answer was not taken, where it was not.</param>
    /// <returns>Whether the answer was taken.</returns>
    public bool TryAnswer(string question, string answer, string? text, out string? problem)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        if (!_open.Contains(question))
        {
            problem = $"'{question}' is not a question this run leaves open";
            return false;
        }

        var asked = _questions.First(asking => asking.Id == question);

        if (!asked.Options.Contains(answer, StringComparer.Ordinal) && answer != "abort")
        {
            problem = $"'{answer}' is not one of the answers '{question}' takes: {string.Join(", ", asked.Options)}";
            return false;
        }

        var needsText = asked.Kind == "pause" && _answerable[question].Step.Text.Contains(answer, StringComparer.Ordinal);

        if (needsText && string.IsNullOrWhiteSpace(text))
        {
            problem = $"'{answer}' is an answer '{question}' takes with a text; give one";
            return false;
        }

        if (!needsText && !string.IsNullOrWhiteSpace(text) && asked.Kind != "ask")
        {
            problem = $"'{answer}' is an answer '{question}' takes with no text; give none";
            return false;
        }

        _open.Remove(question);
        problem = null;
        _last = null;
        var instance = _answerable[question];
        _ = _answerable.Remove(question);

        ApplyAnswer(instance, asked, answer, string.IsNullOrWhiteSpace(text) ? null : text, by: null);

        return true;
    }

    /// <summary>
    /// Applies one answer to the question that waits on it, recording who gave it: a person (a caller of
    /// <see cref="TryAnswer(string, string, out string?)"/>) or the configuration, which answered the kind beforehand where <paramref name="by"/>
    /// says so. The question's own kind says what the answer does.
    /// </summary>
    private void ApplyAnswer(FlowInstance instance, FlowQuestion asked, string answer, string? text, string? by)
    {
        asked._given.Add(new FlowAnswerEntry(answer, text, by ?? "person", by is null ? null : _answerWhere[asked.Id]));
        asked.State = "answered";

        if (answer == "abort" && asked.Kind is not ("failure" or "loopBound"))
        {
            // abort is taken for any question, and ends the run as a hard stop does.
            _stopping = true;
            _failed.Add(instance);
            End(instance, FlowInstanceState.Failed, HarnessExit.CommandFailed, "the run was aborted by the answer to its question");
            return;
        }

        switch (asked.Kind)
        {
            case "failure":
                AnswerFailure(instance, answer);
                break;
            case "loopBound":
                AnswerLoopBound(instance, answer);
                break;
            case "pause":
                AnswerPause(instance, answer, text);
                break;
            default:
                AnswerTheKind(instance, asked, answer);
                break;
        }
    }

    /// <summary>
    /// Answers a question of a kind a step's own work raises - a fold's consents, a reason step's limits, a wait for
    /// checks. <c>retry</c> runs the step again as a new attempt; <c>more</c>, <c>accept</c>, <c>allow</c>,
    /// <c>merge</c>, <c>stay</c> and <c>compact</c> hand the answer to the runner in the same attempt;
    /// <c>skip</c> ends the step skipped; <c>fail</c> ends it failed with the question's kind.
    /// </summary>
    private void AnswerTheKind(FlowInstance instance, FlowQuestion asked, string answer)
    {
        switch (answer)
        {
            case "retry":
                instance.Attempt++;
                instance.State = FlowInstanceState.NotStarted;
                instance.Exit = null;
                instance.Error = null;
                instance.EndedByQuestion = null;
                break;
            case "skip":
                End(instance, FlowInstanceState.Skipped, instance.Exit, instance.Error);
                instance.SkippedBecause = "skipped by the answer to its question";
                break;
            case "fail":
                instance.EndedByQuestion = asked.Kind;
                End(instance, FlowInstanceState.Failed, FlowQuestions.FailCodeOf(asked.Kind), asked.Problem);
                HandleFailure(instance);
                break;
            default:
                instance._answers.Add(new FlowAnswerGiven(asked.Id, answer));
                instance.State = FlowInstanceState.NotStarted;
                break;
        }
    }

    /// <summary>Answers a <c>pause</c> step's question: the step ends done, its answer - and the text an option took - its
    /// output, which the steps after it read as any output.</summary>
    private void AnswerPause(FlowInstance instance, string answer, string? text)
    {
        instance.Output = new JsonObject { ["answer"] = answer, ["text"] = text };
        End(instance, FlowInstanceState.Done, null, null);
    }

    /// <summary>
    /// Answers the question a failed step's <c>onFailure: pause</c> raised: <c>retry</c> runs the step again,
    /// <c>skip</c> ends it skipped and lets what depends on it go on, <c>abort</c> ends the run failed.
    /// </summary>
    private void AnswerFailure(FlowInstance instance, string answer)
    {
        switch (answer)
        {
            case "retry":
                instance.Attempt++;
                instance.State = FlowInstanceState.NotStarted;
                instance.Exit = null;
                instance.Error = null;
                instance.EndedByQuestion = null;
                break;
            case "skip":
                End(instance, FlowInstanceState.Skipped, instance.Exit, instance.Error);
                instance.SkippedBecause = "skipped by the answer to its failure";
                break;
            default:
                _stopping = true;
                _failed.Add(instance);
                End(instance, FlowInstanceState.Failed, instance.Exit, instance.Error);
                break;
        }
    }

    /// <summary>
    /// Answers the question a repeat at its bound asks: <c>more</c> gives the loop one more iteration (and the same
    /// question is asked again if it comes to that), <c>accept</c> ends the repeat as it stands, <c>abort</c> ends the
    /// run failed.
    /// </summary>
    private void AnswerLoopBound(FlowInstance instance, string answer)
    {
        switch (answer)
        {
            case "more":
                instance.ExtraIterations++;
                instance.State = FlowInstanceState.Running;
                break;
            case "accept":
                if (instance.Children.Count > 0 && instance.Children[^1].GroupInstance is { State: FlowInstanceState.Running } last)
                {
                    End(last, FlowInstanceState.Done, null, null);
                }

                End(instance, FlowInstanceState.Done, null, null);
                break;
            default:
                if (instance.Children.Count > 0 && instance.Children[^1].GroupInstance is { State: FlowInstanceState.Running } stopped)
                {
                    End(stopped, FlowInstanceState.Done, null, null);
                }

                _stopping = true;
                _failed.Add(instance);
                End(instance, FlowInstanceState.Failed, HarnessExit.CommandFailed, "aborted at its bound");
                break;
        }
    }

    /// <summary>Everything the coordinator decides between two events, until nothing more follows: instantiate what its dependencies freed, end what its members finished, carry failures to their policies, and a stopping run's reach.</summary>
    private void Progress()
    {
        var changed = true;

        while (changed)
        {
            changed = false;

            foreach (var scope in _scopes.ToList())
            {
                changed |= Instantiate(scope);
            }

            changed |= EndGroups();
            changed |= CarryTheStop();
        }
    }

    /// <summary>A stopping run starts nothing that is not an always step: what it had not started is NotRun, and what is in flight runs to its end.</summary>
    private bool CarryTheStop()
    {
        if (!_stopping)
        {
            return false;
        }

        var stopped = _created.Where(instance => instance.State is FlowInstanceState.NotStarted && !instance.IsAlways).ToList();

        foreach (var instance in stopped)
        {
            End(instance, FlowInstanceState.NotRun, null, null);
            instance.NotRunBecause = "the run ended before it";
        }

        return stopped.Count > 0;
    }

    /// <summary>
    /// Instantiates the declarations of <paramref name="scope"/> whose dependencies have freed them: a leaf becomes a
    /// candidate, a group opens, a declaration a failed dependency blocks for ever is NotRun at once, and a false
    /// <c>when</c> ends skipped. An always step waits for the always phase, and a stopping run reaches nothing that is
    /// not one.
    /// </summary>
    private bool Instantiate(Scope scope)
    {
        var changed = false;

        foreach (var declaration in DeclarationsOf(scope))
        {
            if (scope.Instantiated.Contains(declaration.Name))
            {
                continue;
            }

            if (IsAlways(declaration, scope) && !_alwaysPhase)
            {
                // The always steps wait for the rest to be quiescent, in any state, and run once.
                continue;
            }

            if (_stopping && !IsAlways(declaration, scope))
            {
                scope.Instantiated.Add(declaration.Name);
                var stopped = Create(scope, declaration, FlowInstanceState.NotRun);
                stopped.NotRunBecause = "the run ended before it";
                changed = true;
                continue;
            }

            var (every, blocked, unreachable) = WaitsOf(declaration, scope, IsAlways(declaration, scope));

            if (unreachable)
            {
                scope.Instantiated.Add(declaration.Name);
                var notRun = Create(scope, declaration, FlowInstanceState.NotRun);
                End(notRun, FlowInstanceState.NotRun, null, null);
                notRun.NotRunBecause = "a step it depends on failed and was neither handled nor passed over";
                changed = true;
                continue;
            }

            if (blocked || !every)
            {
                continue;
            }

            scope.Instantiated.Add(declaration.Name);

            if (declaration.When is { } when)
            {
                var read = Evaluate(when, scope, out var problem);

                if (problem is not null)
                {
                    var unread = Create(scope, declaration, FlowInstanceState.Failed);
                    unread.Exit = HarnessExit.ConfigInvalid;
                    unread.Error = Unreadable("when", unread.Id, problem);
                    HandleFailure(unread);
                    changed = true;
                    continue;
                }

                if (!read.AsBoolean(when))
                {
                    var skipped = Create(scope, declaration, FlowInstanceState.Skipped);
                    skipped.SkippedBecause = "its when read false";
                    changed = true;
                    continue;
                }
            }

            if (declaration.IsGroup)
            {
                OpenGroup(scope, declaration);
                changed = true;
                continue;
            }

            if (declaration.Type == "push" && declaration.Checkpoint == true && _definition.PushCheckpoints != true)
            {
                var off = Create(scope, declaration, FlowInstanceState.Skipped);
                off.SkippedBecause = "checkpoint pushes are off (pushCheckpoints)";
                changed = true;
                continue;
            }

            Create(scope, declaration, FlowInstanceState.NotStarted);
            changed = true;
        }

        return changed;
    }

    /// <summary>Opens a group its dependencies freed: a block runs its members at once, a forEach expands to an item scope for each work item, a repeat makes its first iteration.</summary>
    private void OpenGroup(Scope scope, WorkflowStep declaration)
    {
        var instance = Create(scope, declaration, FlowInstanceState.Running);

        if (declaration.ForEach is { } items)
        {
            var list = EvaluateList(items, scope, out var problem);

            if (problem is not null)
            {
                FailGroup(instance, HarnessExit.ConfigInvalid, Unreadable("forEach", instance.Id, problem), instance.Path);
                return;
            }

            var members = SubtreeSize(PathOf(scope, declaration));
            var wouldBring = _created.Count + list.Count * (1 + members);

            if (wouldBring > Limit().MaxInstances)
            {
                FailGroup(instance, HarnessExit.Refused, InstanceBound(instance.Id, wouldBring, Limit().MaxInstances), instance.Path);
                return;
            }

            for (var index = 0; index < list.Count; index++)
            {
                var item = NewScope($"{instance.Id}[{index}]", PathOf(scope, declaration), scope, declaration, list[index], index, null, instance);
                instance.Children.Add(item);
                _ = Create(item, declaration, FlowInstanceState.Running, item.Id);
            }

            if (list.Count == 0)
            {
                End(instance, FlowInstanceState.Done, null, null);
            }

            return;
        }

        if (declaration.Repeat is { } repeat)
        {
            if (repeat.MaxIterations is null)
            {
                FailGroup(
                    instance,
                    HarnessExit.InternalError,
                    "this engine runs a repeat's iterations under maxIterations alone; a repeat bound by rounds and blocking is not run by it",
                    instance.Path);
                return;
            }

            _ = NextIteration(scope, declaration, instance);
            return;
        }

        instance.OwnScope = NewScope(instance.Id, PathOf(scope, declaration), scope, declaration, FlowValue.Missing, null, null, null);
        instance.OwnScope.OwnGroupInstance = instance;
    }

    /// <summary>Makes the repeat group's next iteration where its bound allows one, and asks at its bound where it does not.</summary>
    private bool NextIteration(Scope scope, WorkflowStep declaration, FlowInstance instance)
    {
        var iteration = instance.Children.Count + 1;
        var bound = (declaration.Repeat?.MaxIterations ?? 0) + instance.ExtraIterations;
        var wouldBring = _created.Count + 1 + SubtreeSize(PathOf(scope, declaration));

        if (wouldBring > Limit().MaxInstances)
        {
            FailGroup(instance, HarnessExit.Refused, InstanceBound(instance.Id, wouldBring, Limit().MaxInstances), instance.Path);
            return true;
        }

        if (iteration > bound)
        {
            _ = Ask(
                instance,
                "loopBound",
                $"'{instance.Id}' has run {iteration - 1} iterations and its until still reads false; at most {declaration.Repeat?.MaxIterations} are allowed (maxIterations){(instance.ExtraIterations > 0 ? $", and {instance.ExtraIterations} more were given by its answers" : string.Empty)}",
                WorkflowValidator.AnswersOf["loopBound"]);
            return false;
        }

        var body = NewScope($"{instance.Id}#{iteration}", PathOf(scope, declaration), scope, declaration, FlowValue.Missing, null, iteration, instance);
        instance.Children.Add(body);
        _ = Create(body, declaration, FlowInstanceState.Running, body.Id);
        return true;
    }

    /// <summary>Reads the end of a repeat's last iteration: a true <c>until</c> ends it, a false one makes the next iteration, and a false one at its bound asks.</summary>
    private bool Iterate(FlowInstance instance)
    {
        if (instance.Children.Count == 0 || !ScopeEnded(instance.Children[^1]))
        {
            return false;
        }

        if (instance.Children[^1].GroupInstance is { State: FlowInstanceState.Running } ended)
        {
            End(ended, FlowInstanceState.Done, null, null);
        }

        var until = Evaluate(instance.Step.Repeat!.Until, instance.Children[^1], out var problem);

        if (problem is not null)
        {
            FailGroup(instance, HarnessExit.ConfigInvalid, Unreadable("until", instance.Children[^1].GroupInstance?.Id ?? instance.Id, problem), instance.Path);
            return true;
        }

        if (until.AsBoolean(instance.Step.Repeat.Until))
        {
            End(instance, FlowInstanceState.Done, null, null);
            return true;
        }

        return NextIteration(instance.DeclaredScope!, instance.Step, instance);
    }

    /// <summary>Ends the groups whose work has finished, gathers what a block gathers, and carries each ended group's failure to its policy.</summary>
    private bool EndGroups()
    {
        var changed = false;

        foreach (var instance in _created.Where(group => group.Step.IsGroup && group.State is FlowInstanceState.Running).ToList())
        {
            var scopes = ScopesOf(instance);

            if (instance.Failing is { } failure)
            {
                if (scopes.All(ScopeEnded))
                {
                    foreach (var child in instance.Children.Select(child => child.GroupInstance).Where(child => child is { State: FlowInstanceState.Running }))
                    {
                        End(child!, FlowInstanceState.Failed, failure.Exit, failure.Error);
                    }

                    Gather(instance);
                    End(instance, FlowInstanceState.Failed, failure.Exit, failure.Error);
                    HandleFailure(instance);
                    changed = true;
                }

                continue;
            }

            if (instance.Step.ForEach is not null)
            {
                if (instance.Children.Count > 0 && instance.Children.All(ScopeEnded))
                {
                    foreach (var child in instance.Children.Select(child => child.GroupInstance).Where(child => child is { State: FlowInstanceState.Running }))
                    {
                        End(child!, FlowInstanceState.Done, null, null);
                    }

                    End(instance, FlowInstanceState.Done, null, null);
                    changed = true;
                }

                continue;
            }

            if (instance.Step.Repeat is not null)
            {
                changed |= Iterate(instance);
                continue;
            }

            if (instance.OwnScope is { } block && ScopeEnded(block))
            {
                Gather(instance);
                End(instance, FlowInstanceState.Done, null, null);
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>Admits every step that can be admitted, in the fixed order: all-or-nothing against the limits and the claims, with a claim-blocked step reserving what it waits for. Answers how many it started.</summary>
    private int Admit()
    {
        var reserved = new List<FlowClaim>();
        var started = 0;

        foreach (var instance in _created
            .Where(instance => instance.State is FlowInstanceState.NotStarted && !instance.Step.IsGroup)
            .OrderBy(instance => _declarationOrder.GetValueOrDefault(instance.Path, int.MaxValue))
            .ThenBy(instance => instance.Id, StringComparer.Ordinal))
        {
            if (_stopping && !instance.IsAlways)
            {
                continue;
            }

            if (instance.Step.Alone == true && _created.Any(running => running.State is FlowInstanceState.Running))
            {
                // The quiescence barrier waits for quiet and reserves nothing while it waits.
                continue;
            }

            if (instance.Step.Alone != true && _created.Any(running => running.State is FlowInstanceState.Running && running.Step.Alone == true))
            {
                // Nothing starts while an alone step runs.
                continue;
            }

            var claims = FlowClaims.Of(instance.Step, _runnerFacts);

            if (claims.Any(claim => HeldClaims(reserved).Any(claim.Conflicts)))
            {
                // Blocked on a claim: the step reserves every claim it waits for, so no later step takes what it waits
                // for first, and a fold waiting behind readers is not starved. A step blocked on a limit reserves
                // nothing, and a step a hold or a pause keeps from starting reserves nothing either.
                reserved.AddRange(claims);
                continue;
            }

            if (!CountersAllow(instance))
            {
                continue;
            }

            if (Start(instance, claims))
            {
                started++;
            }
        }

        return started;
    }

    /// <summary>
    /// Runs one admitted instance: its runner is asked as a task, and what it answers is posted to the channel. A
    /// <c>pause</c> step asks no runner at all - the engine asks its question itself - so it answers that nothing of
    /// the step is in flight.
    /// </summary>
    private bool Start(FlowInstance instance, IReadOnlyList<FlowClaim> claims)
    {
        instance.State = FlowInstanceState.Running;
        instance.HeldClaims.Clear();
        instance.HeldClaims.AddRange(claims);
        instance.LegsInCopies.Clear();
        instance.CopiesMade.Clear();
        instance.TreeLegsEnded.Clear();

        if (instance.Step.Type == "pause")
        {
            // A pause step asks and asks only: the engine fills its question from the same names an expression reads,
            // and no runner is asked. A question that cannot be filled fails the step, as any step its template
            // breaks. Either way the graph changed where the loop was admitting, so it goes round again.
            Release(instance);
            _answeredAtOnce = true;

            if (Filled(instance, instance.Step.Question ?? string.Empty, out var problem) is { } question)
            {
                _ = Ask(instance, "pause", question, FlowQuestions.OptionsOf("pause", instance.Step));
            }
            else
            {
                End(instance, FlowInstanceState.Failed, HarnessExit.ConfigInvalid, Unreadable("question", instance.Id, problem!));
                HandleFailure(instance);
            }

            return false;
        }

        _ = AskRunner();

        return true;

        async Task AskRunner()
        {
            try
            {
                var result = await _runner.RunAsync(
                    new FlowStepRun(
                        instance.Step,
                        instance.Id,
                        instance.Attempt,
                        instance.Item,
                        instance.Index,
                        instance.Iteration,
                        [.. instance._answers],
                        new RunEvents(this, instance)),
                    CancellationToken.None).ConfigureAwait(false);

                _ = _messages.Writer.TryWrite(new Message(instance, result, null));
            }
            catch (Exception ex)
            {
                _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, HarnessExit.InternalError, ex.Message), null));
            }
        }
    }

    /// <summary>Reads one answer of a runner: the step asked, or ended - and what its ending does.</summary>
    private void Complete(FlowInstance instance, FlowStepResult result)
    {
        if (instance.State is not FlowInstanceState.Running)
        {
            return;
        }

        if (result.Asked is { } asking)
        {
            // A step that asks holds nothing while it waits: it gives back every claim and every counter, and is
            // admitted again, against the limits and the claims as they then are, when it is answered.
            Release(instance);
            _ = Ask(instance, asking.Kind, asking.Problem, asking.Options);
            return;
        }

        if (result.DryRun is { } dry)
        {
            // A fold family step's dry run stood short of applying: a consent it lacks raises the question its kind
            // names, one at a time, a person's first; a refusal for conflicting merges alone raises foldConflicts,
            // for a conflict is never answered merge; any other refusal fails the step, refused.
            Release(instance);

            if (dry.Standing == RequestStanding.NeedsConsent && dry.Consents.Count > 0)
            {
                var consent = FlowQuestions.FirstOf(dry.Consents);
                var kind = ConsentNameOf(consent);
                _ = Ask(instance, kind, $"'{instance.Id}' was not applied: it lacks what nobody gave ({kind}): {dry.Summary}", FlowQuestions.OptionsOf(kind, instance.Step));
            }
            else if (dry.Standing == RequestStanding.Refused && dry.Refusals.Count > 0 && dry.Refusals.All(reason => reason == RefusalKind.Conflict))
            {
                _ = Ask(
                    instance,
                    "foldConflicts",
                    $"'{instance.Id}' was refused: paths both trees changed conflict in their three-way merge: {string.Join(", ", dry.Named)}",
                    FlowQuestions.OptionsOf("foldConflicts", instance.Step));
            }
            else
            {
                End(instance, FlowInstanceState.Failed, HarnessExit.Refused, dry.Summary);
                HandleFailure(instance);
            }

            return;
        }

        if (result.Limit is { } limit)
        {
            // A reason step at a limit of its own asks and is no failure. Its question is raised however the step's
            // onFailure reads, for the step has not failed.
            Release(instance);
            var kind = FlowQuestions.LimitKindOf(limit);
            _ = Ask(instance, kind, $"'{instance.Id}' reached its {limit}{(result.LimitDetail is { } detail ? $" ({detail})" : string.Empty)}", FlowQuestions.OptionsOf(kind, instance.Step));
            return;
        }

        if (result.Checks is { } found)
        {
            Release(instance);
            var kind = FlowQuestions.ChecksKindOf(found);
            _ = Ask(instance, kind, $"'{instance.Id}' found what it cannot end on: {result.ChecksDetail ?? found}", FlowQuestions.OptionsOf(kind, instance.Step));
            return;
        }

        var (passed, exit, error) = Ends(instance.Step, result);
        instance.Legs = result.Legs;
        instance.RedLegs = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Red);
        instance.Output = result.Output;

        // An incomplete step is run again by the table's row for its code, capped by its retries: a reasoning step is
        // never retried at step level, for its client owns its transient errors.
        if (!passed
            && instance.Step.Type != "reason"
            && FlowRetry.Wait(exit, result.Cause, instance.Attempt - 1) is { } wait
            && instance.Attempt <= RetryCap(instance.Step))
        {
            // Its attempt is over, its claims go back, and its next attempt waits as the table says - at once, where
            // the table says no wait.
            Release(instance);
            _wakes.Add(new Wake(instance, _clock.GetUtcNow() + wait, wait == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(wait, _clock)));
            return;
        }

        Release(instance);
        End(instance, passed ? FlowInstanceState.Done : FlowInstanceState.Failed, exit, error);

        if (!passed)
        {
            HandleFailure(instance);
        }
    }

    /// <summary>
    /// What a step's own work ended with: a step that runs legs is read from each leg's own verdict, and every other
    /// step from what its runner said. Under <c>failOn: red</c> any red leg fails the step with its worst red leg's
    /// code; under <c>never</c> red is no failure, and the legs that still decide - a failure that is not red, an
    /// incomplete one - decide as they would without it.
    /// </summary>
    private static (bool Passed, int Exit, string? Error) Ends(WorkflowStep step, FlowStepResult result)
    {
        if (result.Legs.Count == 0)
        {
            return (result.Passed, result.Exit, result.Error);
        }

        var red = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Red);
        var notRed = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.FailureThatIsNotRed);
        var incomplete = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Incomplete);

        if (red.Count > 0 && (step.FailOn ?? WorkflowStepTypes.Find(step.Type ?? string.Empty)?.FailOnDefault) == "red")
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(red)!.Value);
            return (false, code, WorstLine(red, code));
        }

        if (notRed.Count > 0)
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(notRed)!.Value);
            return (false, code, WorstLine(notRed, code));
        }

        if (incomplete.Count > 0)
        {
            var worst = FlowLegVerdicts.Worst(incomplete)!.Value;
            return (false, FlowLegVerdicts.CodeOf(worst), $"leg '{incomplete.First(leg => leg.Verdict == worst).Leg}' ended {Verdicts.Display(worst)}; it is run again");
        }

        if (red.Count > 0)
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(red)!.Value);
            return (true, code, WorstLine(red, code));
        }

        return (true, HarnessExit.Success, null);
    }

    /// <summary>The line that says what the worst of <paramref name="legs"/> found, as the step's error keeps it.</summary>
    private static string WorstLine(IReadOnlyList<FlowLeg> legs, int code)
    {
        var worst = FlowLegVerdicts.Worst(legs)!.Value;
        var leg = legs.First(leg => leg.Verdict == worst);

        return $"leg '{leg.Leg}' ended {Verdicts.Display(worst)} ({code})";
    }

    /// <summary>Applies the failure policy of <paramref name="instance"/>: its own where it wrote one, its group's where it is a member that wrote none, and stop at the top.</summary>
    private void HandleFailure(FlowInstance instance)
    {
        if (instance.Step.OnFailure is { } policy)
        {
            Apply(instance, policy);
            return;
        }

        if (instance.GroupOf is { } group)
        {
            // A member that wrote no onFailure hands its failure to its group: nothing more of the group starts, its
            // members in flight run to their end, and the group ends failed with that member's code - the first in
            // declaration order where several did so.
            FailGroup(group, instance.Exit ?? HarnessExit.InternalError, instance.Error, instance.Path);
            return;
        }

        Apply(instance, "stop");
    }

    /// <summary>Marks a group failed with a member's code: nothing more of it starts, and it ends when its members in flight have ended.</summary>
    private void FailGroup(FlowInstance group, int exit, string? error, string failed)
    {
        // The first member in declaration order that failed so decides the code, even where a later one's ending was
        // read first.
        if (group.Failing is { } earlier && _declarationOrder.GetValueOrDefault(earlier.Member, int.MaxValue) <= _declarationOrder.GetValueOrDefault(failed, int.MaxValue))
        {
            return;
        }

        group.Failing = (exit, error, failed);

        foreach (var scope in ScopesOf(group))
        {
            foreach (var declaration in DeclarationsOf(scope).Where(declaration => !scope.Instantiated.Contains(declaration.Name)).ToList())
            {
                scope.Instantiated.Add(declaration.Name);
                var notRun = Create(scope, declaration, FlowInstanceState.NotRun);
                End(notRun, FlowInstanceState.NotRun, null, null);
                notRun.NotRunBecause = $"'{failed}' failed and its group took the failure";
            }
        }

        foreach (var member in ScopesOf(group).SelectMany(scope => scope.Members.Values).Where(member => member.State is FlowInstanceState.NotStarted).ToList())
        {
            End(member, FlowInstanceState.NotRun, null, null);
            member.NotRunBecause = $"'{failed}' failed and its group took the failure";
        }
    }

    /// <summary>Applies one failure policy to the instance that ended failed.</summary>
    private void Apply(FlowInstance instance, string policy)
    {
        switch (policy)
        {
            case "continue":
                instance.PassedOver = true;
                _passedOver.Add(instance);
                break;
            case "pause":
                _ = Ask(
                    instance,
                    "failure",
                    $"'{instance.Id}' failed ({instance.Exit}): {instance.Error ?? "its work ended failed"}. What does the run do?",
                    FlowQuestions.OptionsOf("failure", instance.Step));
                break;
            case WorkflowStep.Recover:
                throw new HarnessException(HarnessExit.InternalError, "this engine does not run a workflow's recovery; it is not built into the coordinator yet");
            default:
                _stopping = true;
                _failed.Add(instance);
                break;
        }
    }

    /// <summary>
    /// Raises a question on <paramref name="instance"/>, pausing it: it holds nothing while it waits, and only what
    /// depends on it is blocked. A configuration that answered the kind beforehand - the nearest <c>answers</c> that
    /// names it, and never a kind only a person answers - is given its answer at once, in the place of a person: the
    /// step is never paused for it, and the answer is recorded on the question as every answer is, saying where it was
    /// configured.
    /// </summary>
    private string Ask(FlowInstance instance, string kind, string problem, IReadOnlyList<string> options)
    {
        var id = $"q{++_questionsRaised}";
        var asked = new FlowQuestion(id, kind, instance.Id, problem, options);
        _questions.Add(asked);

        if (ConfiguredFor(instance, kind) is { } configured)
        {
            var (entry, where) = configured;
            var place = FlowQuestions.PlaceOf(kind, instance);
            var key = $"{kind}|{place}";
            var given = _answerSpent.GetValueOrDefault(key);

            // An answer that lets the same thing go on is given a counted number of times and no more; an answer that
            // ends the matter is given whenever the question comes, for it ends it.
            var answer = WorkflowValidator.GoesOn.Contains(entry.Answer, StringComparer.Ordinal)
                ? given < (entry.Times ?? 1)
                    ? Give(entry.Answer)
                    : entry.Then is { } then ? then : null
                : entry.Answer;

            if (answer is { } atOnce)
            {
                _answerWhere[id] = where;
                _answeredAtOnce = true;
                ApplyAnswer(instance, asked, atOnce, null, by: "configuration");
                return id;
            }

            string Give(string answer)
            {
                _answerSpent[key] = given + 1;
                return answer;
            }
        }

        _open.Add(id);
        _answerable.Add(id, instance);
        instance.State = FlowInstanceState.Paused;
        return id;
    }

    /// <summary>
    /// The configured answer a question of <paramref name="kind"/> on <paramref name="instance"/> is given at once:
    /// the nearest <c>answers</c> that names the kind - the step's own, then each group around it from the inside out,
    /// then the workflow's - and none where the kind is a person's alone, or answered by the step's own key
    /// (<c>failure</c> by <c>onFailure</c>, <c>ask</c> by <c>ask</c>), or a <c>pause</c> step answered anywhere but on
    /// the step itself and never one that says <c>person</c>, or the run's budget answered anywhere but on the
    /// workflow.
    /// </summary>
    private (FlowAnswer Entry, string Where)? ConfiguredFor(FlowInstance instance, string kind)
    {
        if (FlowQuestions.PersonsKinds.Contains(kind, StringComparer.Ordinal) || kind is "failure" or "ask")
        {
            return null;
        }

        if (kind == "pause")
        {
            return instance.Step.Person is true
                ? null
                : instance.Step.Answers.TryGetValue(kind, out var own) ? (own, $"step '{instance.Step.Name}'") : null;
        }

        if (kind == "runBudget")
        {
            return _definition.Answers.TryGetValue(kind, out var run) ? (run, "the orchestrator's answers") : null;
        }

        if (instance.Step.Answers.TryGetValue(kind, out var nearest))
        {
            return (nearest, $"step '{instance.Step.Name}'");
        }

        for (var at = instance.DeclaredScope; at is not null; at = at.Parent)
        {
            if (at.Group?.Answers.TryGetValue(kind, out var group) == true)
            {
                return (group, $"group '{at.Group.Name}'");
            }
        }

        return _definition.Answers.TryGetValue(kind, out var orchestrator) ? (orchestrator, "the orchestrator's answers") : null;
    }

    /// <summary>Ends <paramref name="instance"/> and gives back what it held.</summary>
    private void End(FlowInstance instance, FlowInstanceState state, int? exit, string? error)
    {
        instance.State = state;
        instance.Exit ??= exit;
        instance.Error ??= error;
        Release(instance);

        if (instance.Step.Type == "create-agent" && state == FlowInstanceState.Done)
        {
            _agentsOpen[instance.Id] = instance;
        }

        // Only a deletion releases an open agent's slot: a fold carries its work and leaves it open. The create-agent
        // step the deletion names is found where its declaration is, in the deletion's scope or one around it.
        if (instance.Step.Type == "delete-agent"
            && instance.Step.Of is { } made
            && state == FlowInstanceState.Done
            && CreateStepOf(instance, made) is { } createStep)
        {
            _ = _agentsOpen.Remove(createStep.Id);
        }
    }

    /// <summary>The create-agent step a fold family step's <c>of</c> names, found in its own scope or one around it.</summary>
    private FlowInstance? CreateStepOf(FlowInstance instance, string made)
    {
        for (var at = instance.DeclaredScope; at is not null; at = at.Parent)
        {
            if (at.Members.TryGetValue(made, out var found) && found.Step.Type == "create-agent")
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>Gives back every claim <paramref name="instance"/> still holds.</summary>
    private void Release(FlowInstance instance)
    {
        instance.HeldClaims.Clear();
    }

    /// <summary>Every claim held by a running instance, and every claim a blocked step reserved.</summary>
    private IEnumerable<FlowClaim> HeldClaims(IReadOnlyList<FlowClaim> reserved)
        => _created.Where(instance => instance.State is FlowInstanceState.Running).SelectMany(instance => instance.HeldClaims)
            .Concat(reserved);

    /// <summary>Whether the limits of the run still leave room for <paramref name="instance"/>: all-or-nothing, so a step short of one of its resources starts with none of them.</summary>
    private bool CountersAllow(FlowInstance instance)
    {
        var running = _created.Where(running => running.State is FlowInstanceState.Running).ToList();

        return instance.Step.Type switch
        {
            "reason" => running.Count(step => step.Step.Type == "reason") < Limit().MaxReasoningAgents,
            "build" or "test" or "run" or "check-mutations"
                => running.Count(step => step.Step.Type is "build" or "test" or "run" or "check-mutations") < Limit().MaxParallelProcesses,
            "create-agent" => running.Count(step => step.Step.Type == "create-agent") + _agentsOpen.Count < Limit().MaxOpenAgents,
            _ => true,
        };
    }

    /// <summary>Starts the always steps of the run, once, where the rest is quiescent in any state; a session runs them after everything else has ended.</summary>
    private bool TryEnterAlwaysPhase()
    {
        if (_alwaysPhase)
        {
            return false;
        }

        var pending = _scopes
            .SelectMany(scope => DeclarationsOf(scope).Where(declaration => !scope.Instantiated.Contains(declaration.Name)).Select(declaration => (scope, declaration)))
            .Count(entry => IsAlways(entry.declaration, entry.scope));

        if (pending == 0)
        {
            return false;
        }

        _alwaysPhase = true;
        return true;
    }

    /// <summary>Ends the session: what never started is NotRun - where no question is open, for what waits on one goes on when it is answered - and the run's status is said with its failures and its passed-over steps.</summary>
    private FlowSessionEnd Finish()
    {
        if (_open.Count == 0)
        {
            foreach (var instance in _created.Where(instance => instance.State is FlowInstanceState.NotStarted).ToList())
            {
                End(instance, FlowInstanceState.NotRun, null, null);
                instance.NotRunBecause = "the run ended before it";
            }
        }

        var open = OpenQuestions;
        var status = _stopping || _failed.Count > 0 ? FlowRunStatus.Failed : open.Count > 0 ? FlowRunStatus.Paused : FlowRunStatus.Done;
        var summary = status switch
        {
            FlowRunStatus.Done when _passedOver.Count > 0 => _passedOver.Count == 1
                ? $"done; 1 step failed and was passed over: {_passedOver[0].Id}"
                : $"done; {_passedOver.Count} steps failed and were passed over: {string.Join(", ", _passedOver.Select(instance => instance.Id))}",
            FlowRunStatus.Done => "done",
            FlowRunStatus.Failed => _failed.Count == 1
                ? $"failed; 1 step failed: {_failed[0].Id}"
                : $"failed; {_failed.Count} steps failed: {string.Join(", ", _failed.Select(instance => instance.Id))}",
            _ => $"paused; waiting on {string.Join(", ", open.Select(question => question.Id))}",
        };

        return new FlowSessionEnd(status, [.. _passedOver.Select(instance => instance.Id)], [.. _failed.Select(instance => instance.Id)], open, summary);
    }

    /// <summary>Waits for the next thing the run learns - a completion, an event of a leg, or a retry's time coming - and takes it. One read of the channel is kept open across the waits a retry's turn takes, so no message is read by a wait that no longer stands.</summary>
    private async Task WaitNextAsync(CancellationToken cancellationToken)
    {
        // A read cancelled by a session's end is dropped, not kept: the next session arms its own.
        if (_pending is { IsCanceled: true } or { IsFaulted: true })
        {
            _pending = null;
        }

        if (_pending is null || _pending.IsCompleted)
        {
            if (_pending is { IsCompleted: true })
            {
                Take(_pending.Result);
                _pending = null;
                return;
            }

            _pending = _messages.Reader.ReadAsync(cancellationToken).AsTask();
        }

        if (_wakes.Count == 0)
        {
            Take(await _pending.ConfigureAwait(false));
            _pending = null;
            return;
        }

        var earliest = _wakes.OrderBy(wake => wake.At).First();
        var done = await Task.WhenAny(_pending, earliest.Delay).ConfigureAwait(false);

        if (done != _pending)
        {
            _wakes.Remove(earliest);
            earliest.Instance.Attempt++;
            earliest.Instance.State = FlowInstanceState.NotStarted;
            return;
        }

        Take(_pending.Result);
        _pending = null;

        void Take(Message message)
        {
            if (message.Result is { } result)
            {
                _ = _wakes.RemoveAll(wake => ReferenceEquals(wake.Instance, message.Instance));
                Complete(message.Instance, result);
            }
            else if (message.Leg is { } leg)
            {
                TakeEvent(message.Instance, leg);
            }
        }
    }

    /// <summary>Reads one event of a running step's legs: where each works, when each copy is made, when each one's work in the tree itself has ended.</summary>
    private void TakeEvent(FlowInstance instance, FlowLegEvent leg)
    {
        if (instance.State is not FlowInstanceState.Running)
        {
            return;
        }

        if (leg.CopyMade)
        {
            _ = instance.CopiesMade.Add(leg.Leg);
        }
        else if (leg.Ended)
        {
            _ = instance.TreeLegsEnded.Add(leg.Leg);
        }
        else
        {
            if (leg.InACopy)
            {
                _ = instance.LegsInCopies.Add(leg.Leg);
            }
            else
            {
                _ = instance.LegsInTree.Add(leg.Leg);
            }
        }

        // The tree is let go once every leg that works in a copy has its copy made and every leg that works in the
        // tree itself has ended - and not before the runner has said where its legs work.
        var known = instance.LegsInCopies.Concat(instance.LegsInTree).ToList();

        if (known.Count > 0
            && known.All(leg => instance.LegsInCopies.Contains(leg) ? instance.CopiesMade.Contains(leg) : instance.TreeLegsEnded.Contains(leg)))
        {
            instance.HeldClaims.RemoveAll(claim => claim.Kind == FlowClaimKind.Run);
        }
    }

    /// <summary>Creates one instance of <paramref name="declaration"/> in <paramref name="scope"/>: a member of the scope, or the instance a scope of a loop is.</summary>
    private FlowInstance Create(Scope scope, WorkflowStep declaration, FlowInstanceState state, string? id = null)
    {
        var instance = new FlowInstance(id ?? MemberId(scope, declaration), declaration, PathOf(scope, declaration))
        {
            State = state,
            DeclaredScope = id is null ? scope : null,
            GroupOf = id is null && scope.Group is not null ? GroupInstanceIn(scope) : null,
            IsAlways = IsAlways(declaration, scope),
            Item = scope.Item,
            Index = scope.Index,
            Iteration = scope.Iteration,
        };

        _instances.Add(instance.Id, instance);
        _created.Add(instance);

        if (id is null)
        {
            scope.Members[declaration.Name] = instance;
        }
        else
        {
            scope.GroupInstance = instance;
        }

        return instance;
    }

    /// <summary>The id of a member of <paramref name="scope"/>: its name under the scope's path.</summary>
    private static string MemberId(Scope scope, WorkflowStep declaration)
        => scope.Id.Length == 0 ? declaration.Name : $"{scope.Id}.{declaration.Name}";

    /// <summary>The instance of the group a scope's members hand their failures to: a block's own, or a loop's around its items and iterations.</summary>
    private FlowInstance GroupInstanceIn(Scope scope)
        => scope.OwnGroupInstance ?? scope.Parent!.Members[scope.Group!.Name];

    /// <summary>Makes a scope of the run: the top, a block's members, or one item or iteration of a loop.</summary>
    private Scope NewScope(string id, string groupPath, Scope? parent, WorkflowStep? group, FlowValue item, int? index, int? iteration, FlowInstance? loopOf)
    {
        var scope = new Scope(id, groupPath, parent, group, item, index, iteration, loopOf);
        _scopes.Add(scope);
        return scope;
    }

    /// <summary>
    /// The dependencies of <paramref name="declaration"/>: every one satisfied, one still blocking the rest, or one
    /// that can never be satisfied - unless the declaration is an always step, which runs even where its dependencies
    /// did not reach it.
    /// </summary>
    private static (bool Every, bool Blocked, bool Unreachable) WaitsOf(WorkflowStep declaration, Scope scope, bool always)
    {
        var every = true;
        var blocked = false;

        foreach (var name in declaration.DependsOn)
        {
            if (!scope.Members.TryGetValue(name, out var dependency))
            {
                every = false;
                blocked = true;
                continue;
            }

            switch (dependency.State)
            {
                case FlowInstanceState.Done or FlowInstanceState.Skipped:
                    break;
                case FlowInstanceState.Failed when dependency.PassedOver:
                    break;
                case FlowInstanceState.Failed or FlowInstanceState.NotRun when always:
                    break;
                case FlowInstanceState.Failed or FlowInstanceState.NotRun:
                    return (false, false, true);
                default:
                    every = false;
                    blocked = true;
                    break;
            }
        }

        return (every, blocked, false);
    }

    /// <summary>Whether every declaration of <paramref name="scope"/> has been instantiated and every one of its members has ended.</summary>
    private bool ScopeEnded(Scope scope)
        => DeclarationsOf(scope).All(declaration => scope.Instantiated.Contains(declaration.Name))
            && scope.Members.Values.All(member => member.State is not (FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Paused));

    /// <summary>The scopes of a group: a block's own one, or every item and iteration of a loop.</summary>
    private static IReadOnlyList<Scope> ScopesOf(FlowInstance group)
    {
        var scopes = new List<Scope>();

        if (group.OwnScope is { } block)
        {
            scopes.Add(block);
        }

        scopes.AddRange(group.Children);

        return scopes;
    }

    /// <summary>The declarations a scope holds, in the order the definition declared them.</summary>
    private IReadOnlyList<WorkflowStep> DeclarationsOf(Scope scope)
        => scope.Group?.Steps ?? (scope.Parent is null ? _definition.Steps : []);

    /// <summary>How many step instances one item or iteration of the group at <paramref name="path"/> makes besides its own: every step it declares, however deep.</summary>
    private int SubtreeSize(string path)
        => FlowScope.Declared(_definition).Count(declared => declared.Path.StartsWith(path + ".", StringComparison.Ordinal));

    /// <summary>What a path's root yields, read in <paramref name="scope"/>: a step's output or one of its facts, an input, a param, one of the run's own names, an item, an iteration, a previous iteration's members. The names after the root the tree walks itself.</summary>
    private FlowValue Read(FlowExpression.Path path, Scope scope)
    {
        switch (path.Root)
        {
            case "input":
                return FlowValue.Of(Inputs());
            case "param":
                return FlowValue.Of(Params());
            case "item":
                return scope.Item;
            case "index":
                return scope.Index is { } index ? FlowValue.Of((long)index) : FlowValue.Missing;
            case "iteration":
                return scope.Iteration is { } iteration ? FlowValue.Of((long)iteration) : FlowValue.Missing;
            case "visit":
                return FlowValue.Of(1L);
            case "previous":
                return PreviousOf(scope) is { } before ? FlowValue.Of(MembersObjectOf(before)) : FlowValue.Missing;
            case "run":
                return FlowValue.Of(RunValues());
            default:
                var names = WorkflowExpressions.ReadOf(path.Whole, template: false, insideAny: false).Names;
                var located = FlowScope.Read(names, _definition, scope.GroupPath.Length == 0 ? null : scope.GroupPath, out _);
                return located is null ? FlowValue.Missing : ValueOf(Locate(located.Value.Step.Path, scope), path.Steps.Count == 0);
        }
    }

    /// <summary>The instance of the declared step at <paramref name="path"/> that a read in <paramref name="scope"/> means: the newest in the nearest scope that holds it, or null where the run has none.</summary>
    private FlowInstance? Locate(string path, Scope scope)
    {
        var segments = path.Split('.');

        for (var at = scope; at is not null; at = at.Parent)
        {
            var prefix = at.GroupPath.Length == 0 ? [] : at.GroupPath.Split('.');

            if (!segments.Take(prefix.Length).SequenceEqual(prefix, StringComparer.Ordinal))
            {
                continue;
            }

            return Walk(at, [.. segments.Skip(prefix.Length)]);
        }

        return null;

        FlowInstance? Walk(Scope? scope, IReadOnlyList<string> names)
        {
            if (names.Count == 0)
            {
                return scope?.GroupInstance;
            }

            FlowInstance? instance = null;

            foreach (var (name, last) in names.Select((name, index) => (name, index == names.Count - 1)))
            {
                if (scope is null || !scope.Members.TryGetValue(name, out instance))
                {
                    return null;
                }

                if (last)
                {
                    return instance;
                }

                // A step of a group read from outside it: a block's member, or a repeat's in its last iteration; a
                // forEach's member is one of many, and nothing says which, so nothing is read.
                scope = instance.Step.ForEach is not null ? null : instance.OwnScope ?? (instance.Children.Count > 0 ? instance.Children[^1] : null);
            }

            return instance;
        }
    }

    /// <summary>The value an instance's name yields: its output alone, or its output's members beside the engine's facts of it. A step whose failure the run passed over is read as Missing - its output, not its facts, which stay what they were.</summary>
    private FlowValue ValueOf(FlowInstance? instance, bool whole)
    {
        if (instance is null || (instance.PassedOver && whole))
        {
            return FlowValue.Missing;
        }

        if (whole)
        {
            return FlowValue.Of(instance.Output);
        }

        var merged = new JsonObject();

        if (instance.Output is JsonObject output && !instance.PassedOver)
        {
            foreach (var (name, value) in output)
            {
                merged[name] = value?.DeepClone();
            }
        }

        merged["outcome"] = instance.State switch
        {
            FlowInstanceState.Done => "done",
            FlowInstanceState.Failed => "failed",
            FlowInstanceState.Skipped => "skipped",
            FlowInstanceState.NotRun => "notRun",
            _ => null,
        };
        merged["code"] = instance.Exit;
        merged["error"] = instance.Error;
        merged["question"] = instance.EndedByQuestion;
        merged["latest"] = instance.Output?.DeepClone();
        merged["assumptions"] = new JsonArray();

        return FlowValue.Of(merged);
    }

    /// <summary>The scope before this one in its repeat's iterations, as <c>previous</c> reads it.</summary>
    private Scope? PreviousOf(Scope scope)
    {
        for (var at = scope; at is not null; at = at.Parent)
        {
            if (at.Iteration is { } iteration && at.LoopOf is { } loop)
            {
                return iteration > 1 ? loop.Children[iteration - 2] : null;
            }
        }

        return null;
    }

    /// <summary>The merged values of a scope's members, as a namespace read walks them.</summary>
    private JsonObject MembersObjectOf(Scope scope)
    {
        var members = new JsonObject();

        foreach (var (name, instance) in scope.Members)
        {
            members[name] = ValueOf(instance, whole: false).ToNode();
        }

        return members;
    }

    /// <summary>The run's own values, as <c>run</c> reads them, said without its always steps: only they read these, so none reads its own effect.</summary>
    private JsonObject RunValues()
    {
        var main = _created.Where(instance => !instance.IsAlways).ToList();
        var failed = main.Any(instance => instance.State is FlowInstanceState.Failed && !instance.PassedOver);
        var paused = _open.Count > 0 || main.Any(instance => instance.State is FlowInstanceState.Paused);

        return new JsonObject
        {
            ["name"] = "r1",
            ["status"] = failed ? "failed" : paused ? "paused" : "done",
            ["done"] = !failed && !paused,
            ["failed"] = failed,
            ["paused"] = paused,
            ["stopped"] = _stopping,
            ["passedOver"] = main.Count(instance => instance.PassedOver),
        };
    }

    /// <summary>The run's inputs as one object.</summary>
    private JsonObject Inputs()
    {
        var values = new JsonObject();

        foreach (var (name, value) in _inputs)
        {
            values[name] = value;
        }

        return values;
    }

    /// <summary>The workflow's params as one object.</summary>
    private JsonObject Params()
    {
        var values = new JsonObject();

        foreach (var parameter in _definition.Params)
        {
            values[parameter.Name] = parameter.Default;
        }

        return values;
    }

    /// <summary>
    /// Gathers what a block gathers when it ends, done or failed alike: each named list of its members' outputs, in
    /// declaration order - a member that was skipped or passed over, or whose output holds no such list, adds nothing,
    /// and what every member left out is an empty list and never Missing.
    /// </summary>
    private void Gather(FlowInstance group)
    {
        if (group.Step.Gather.Count == 0 || group.OwnScope is not { } scope)
        {
            return;
        }

        var gathered = new JsonObject();

        foreach (var name in group.Step.Gather)
        {
            var list = new JsonArray();

            foreach (var declaration in DeclarationsOf(scope))
            {
                if (scope.Members.TryGetValue(declaration.Name, out var member)
                    && !member.PassedOver
                    && member.Output is JsonObject output
                    && output.ContainsKey(name)
                    && output[name] is JsonArray memberList)
                {
                    foreach (var item in memberList)
                    {
                        list.Add(item?.DeepClone());
                    }
                }
            }

            gathered[name] = list;
        }

        group.Output = gathered;
    }

    /// <summary>
    /// Evaluates a boolean expression of <paramref name="scope"/>: an unreadable one - one whose reading throws, or
    /// one that reads nothing at all - says why, in <paramref name="problem"/>, and yields false.
    /// </summary>
    private FlowValue Evaluate(string expression, Scope scope, out string? problem)
    {
        try
        {
            var value = FlowExpression.Parse(expression).Evaluate(path => Read(path, scope));

            if (value.IsMissing)
            {
                problem = $"'{expression}' reads nothing";
                return FlowValue.Of(false);
            }

            problem = null;
            return value;
        }
        catch (UnresolvedReadException unreadable)
        {
            problem = unreadable.Message;
            return FlowValue.Of(false);
        }
    }

    /// <summary>
    /// Evaluates a list-yielding expression of <paramref name="scope"/>: a <c>forEach</c>'s work items, or why it
    /// could not be read - one whose reading throws, or one that reads nothing at all.
    /// </summary>
    private IReadOnlyList<FlowValue> EvaluateList(string expression, Scope scope, out string? problem)
    {
        try
        {
            var value = FlowExpression.Parse(expression).Evaluate(path => Read(path, scope));

            if (value.IsMissing)
            {
                problem = $"'{expression}' reads nothing";
                return [];
            }

            problem = null;
            return [.. value.AsList(expression).Select(item => FlowValue.Of(item))];
        }
        catch (UnresolvedReadException unreadable)
        {
            problem = unreadable.Message;
            return [];
        }
    }

    /// <summary>
    /// Fills a template of <paramref name="instance"/>'s step - a pause step's question - by the one grammar: a brace
    /// doubled stands for one, and any other name in braces is read by the same roots an expression reads. A name that
    /// reads nothing says why, in <paramref name="problem"/>, and fills nothing.
    /// </summary>
    private string? Filled(FlowInstance instance, string template, out string? problem)
    {
        var filled = new StringBuilder(template.Length);
        var at = 0;

        while (at < template.Length)
        {
            if (template[at..].StartsWith("{{", StringComparison.Ordinal))
            {
                // A doubled brace stands for one.
                filled.Append('{');
                at += 2;
                continue;
            }

            if (template[at] != '{')
            {
                var next = template.IndexOf('{', at);

                if (next < 0)
                {
                    filled.Append(template[at..]);
                    break;
                }

                filled.Append(template[at..next]);
                at = next;
                continue;
            }

            var close = template.IndexOf('}', at);

            if (close < 0)
            {
                problem = $"'{template[at..]}' is no name in braces";
                return null;
            }

            var name = template[(at + 1)..close];

            // The grammar every template is read by: the name may say how its value is written, after '|', and the
            // filler fills the value and not the writing of it; whitespace around the name is nothing.
            name = name.Split('|', 2)[0].Trim();

            if (name.Length == 0)
            {
                problem = $"'{{{template[(at + 1)..close]}}}' is no name in braces";
                return null;
            }

            try
            {
                var value = FlowExpression.Parse(name).Evaluate(path => Read(path, instance.DeclaredScope!));

                if (value.IsMissing)
                {
                    problem = $"'{{{name}}}' reads nothing";
                    return null;
                }

                filled.Append(value.AsTemplateText());
            }
            catch (UnresolvedReadException unreadable)
            {
                problem = $"'{{{name}}}': {unreadable.Message}";
                return null;
            }
            catch (ArgumentException notWhole)
            {
                problem = $"'{{{name}}}': {notWhole.Message}";
                return null;
            }

            at = close + 1;
        }

        problem = null;
        return filled.ToString();
    }

    /// <summary>The question kind a consent a fold's dry run lacks is named by.</summary>
    private static string ConsentNameOf(ConsentKind consent)
        => consent switch
        {
            ConsentKind.FoldNewRows => "foldNewRows",
            ConsentKind.FoldLostRows => "foldLostRows",
            ConsentKind.FoldOverCells => "foldOverCells",
            ConsentKind.FoldTwoChanges => "foldTwoChanges",
            _ => throw new ArgumentOutOfRangeException(nameof(consent), consent, "a consent no fold's question is named by"),
        };

    /// <summary>Says an unreadable expression the way a step's record keeps it: which expression of which instance could not be read, and why.</summary>
    private static string Unreadable(string key, string instance, string problem)
        => $"expression '{key}' of '{instance}': {problem}";

    /// <summary>Whether a step runs in the always phase: it says <c>always</c>, or a group around it does.</summary>
    private static bool IsAlways(WorkflowStep declaration, Scope scope)
    {
        if (declaration.Always == true)
        {
            return true;
        }

        for (var at = scope; at is not null; at = at.Parent)
        {
            if (at.Group?.Always == true)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The declaration path of a step declared in <paramref name="scope"/>.</summary>
    private static string PathOf(Scope scope, WorkflowStep declaration)
        => scope.GroupPath.Length == 0 ? declaration.Name : $"{scope.GroupPath}.{declaration.Name}";

    /// <summary>The retries a step is given: its own where it says, the workflow's where it does not, and three where neither does.</summary>
    private int RetryCap(WorkflowStep step) => step.Retries ?? _definition.MaxRetries ?? 3;

    /// <summary>The limits of this run, as the definition says them and as the plan defaults them.</summary>
    private (int MaxReasoningAgents, int MaxParallelProcesses, int MaxOpenAgents, int MaxInstances) Limit()
        => (
            _definition.MaxReasoningAgents ?? 2,
            _definition.MaxParallelProcesses ?? 1,
            _definition.MaxOpenAgents ?? _definition.MaxReasoningAgents ?? 2,
            _definition.MaxInstances ?? WorkflowDefinition.DefaultMaxInstances);

    /// <summary>The refusal at the instance bound, worded once.</summary>
    private static string InstanceBound(string group, int wouldBring, int most)
        => $"group '{group}' would bring this run to {wouldBring} step instances, and at most {most} are allowed (maxInstances). "
            + "A step instance is one run of a step: one for each work item, each iteration and each visit. Raise maxInstances, or lower a loop's bounds";

    /// <summary>The start values of a run: its inputs resolved against what the workflow declares.</summary>
    private static IReadOnlyDictionary<string, string> StartValues(WorkflowDefinition definition, IReadOnlyDictionary<string, string>? given)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in definition.Inputs)
        {
            var value = given is not null && given.TryGetValue(input.Name, out var supplied) ? supplied : input.Default;

            if (value is null)
            {
                if (input.Required)
                {
                    throw new HarnessException(HarnessExit.Refused, $"input '{input.Name}' is required, and nothing gave the run a value for it");
                }

                continue;
            }

            if (input.Options.Count > 0 && !input.Options.Contains(value, StringComparer.Ordinal))
            {
                throw new HarnessException(HarnessExit.Refused, $"input '{input.Name}' takes one of {string.Join(", ", input.Options)}, and the run was given '{value}'");
            }

            values[input.Name] = value;
        }

        foreach (var parameter in definition.Params)
        {
            if (parameter.Required && parameter.Default is null)
            {
                throw new HarnessException(HarnessExit.Refused, $"param '{parameter.Name}' is required, and the workflow declares no default for it");
            }
        }

        return values;
    }

    /// <summary>One thing the coordinator learns while it waits: a step's ending, or an event of one of its legs.</summary>
    private readonly record struct Message(FlowInstance Instance, FlowStepResult? Result, FlowLegEvent? Leg);

    /// <summary>One event of one leg of a running step.</summary>
    /// <param name="Leg">The leg's name.</param>
    /// <param name="CopyMade">The leg's copy is made.</param>
    /// <param name="Ended">The leg's work in the tree itself has ended.</param>
    /// <param name="InACopy">The leg works in a copy of its own, and not in the tree itself.</param>
    private readonly record struct FlowLegEvent(string Leg, bool CopyMade, bool Ended, bool InACopy);

    /// <summary>One retry's time coming.</summary>
    /// <param name="Instance">The instance that waits.</param>
    /// <param name="At">When its next attempt runs.</param>
    /// <param name="Delay">The wait itself.</param>
    private sealed record Wake(FlowInstance Instance, DateTimeOffset At, Task Delay);

    /// <summary>The events of one running step, posted to the coordinator's channel.</summary>
    private sealed class RunEvents(FlowCoordinator coordinator, FlowInstance instance) : IFlowRunEvents
    {
        /// <inheritdoc/>
        public void LegPlaced(string leg, bool inACopy)
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, new FlowLegEvent(leg, CopyMade: false, Ended: false, InACopy: inACopy)));

        /// <inheritdoc/>
        public void LegCopyMade(string leg)
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, new FlowLegEvent(leg, CopyMade: true, Ended: false, InACopy: false)));

        /// <inheritdoc/>
        public void LegEnded(string leg)
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, new FlowLegEvent(leg, CopyMade: false, Ended: true, InACopy: false)));
    }
}

/// <summary>
/// One scope of a run: the top, the members of a block, or one item of a <c>forEach</c> or one iteration of a
/// <c>repeat</c>. A scope holds the instances of the declarations its group declares, and what the loops around it
/// bind: the item, the index, the iteration. Its id is the empty string for the top, a group instance's id for a
/// block's members, an item's or an iteration's for a loop's; its group path is the declaration path of the group whose
/// steps it holds.
/// </summary>
internal sealed class Scope(
    string id,
    string groupPath,
    Scope? parent,
    WorkflowStep? group,
    FlowValue item,
    int? index,
    int? iteration,
    FlowInstance? loopOf)
{
    /// <summary>The scope's id.</summary>
    public string Id { get; } = id;

    /// <summary>The declaration path of the group whose steps the scope holds.</summary>
    public string GroupPath { get; } = groupPath;

    /// <summary>The scope around this one; null at the top.</summary>
    public Scope? Parent { get; } = parent;

    /// <summary>The group whose steps the scope holds; null at the top.</summary>
    public WorkflowStep? Group { get; } = group;

    /// <summary>The work item, where the scope is one item of a <c>forEach</c>.</summary>
    public FlowValue Item { get; } = item;

    /// <summary>The item's index, from 0, where the scope is one item of a <c>forEach</c>.</summary>
    public int? Index { get; } = index;

    /// <summary>The iteration, from 1, where the scope is one iteration of a <c>repeat</c>.</summary>
    public int? Iteration { get; } = iteration;

    /// <summary>The loop group's instance, where the scope is one item or iteration of one.</summary>
    public FlowInstance? LoopOf { get; } = loopOf;

    /// <summary>The instance this scope is: a block's own, or one item's or one iteration's; null at the top and before a loop's scope instance is made.</summary>
    public FlowInstance? GroupInstance { get; set; }

    /// <summary>The block instance whose members this scope holds; null for the top and for the scopes of loops.</summary>
    public FlowInstance? OwnGroupInstance { get; set; }

    /// <summary>The scope's members by name, newest instance of each.</summary>
    public Dictionary<string, FlowInstance> Members { get; } = new(StringComparer.Ordinal);

    /// <summary>The member names already instantiated, so each declaration becomes one instance per scope.</summary>
    public HashSet<string> Instantiated { get; } = new(StringComparer.Ordinal);
}
