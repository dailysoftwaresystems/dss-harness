using System.Text;
using System.Text.Json;
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

    /// <summary>
    /// A safe pause, a hard stop or the answer <c>abort</c> ended it where it stood: it runs again - or is recorded -
    /// at the resume, and nothing it depends on is satisfied or blocked for ever by it.
    /// </summary>
    Stopped,

    /// <summary>Never reached: something it depends on failed and was neither handled nor passed over, or the run ended first.</summary>
    NotRun,
}

/// <summary>
/// Why a step is <see cref="FlowInstanceState.NotRun"/>, kept apart from the words that say it: a failure that blocked it
/// and a run that ended before it are different facts, and a resume considers each afresh by what it is. A hard stop, a
/// safe pause and a session that ends on a question leave what never started as it was, and make nothing NotRun.
/// </summary>
public enum FlowNotRunCause
{
    /// <summary>Something it depends on failed and was neither handled nor passed over - or, for a handler, its source was never reached.</summary>
    DependencyFailed,

    /// <summary>A member of its group failed and the group took the failure, so nothing more of the group was started.</summary>
    GroupFailed,

    /// <summary>The run ended before it: a failure whose <c>onFailure</c> is <c>stop</c> ended the run, and nothing more was started.</summary>
    RunEnded,

    /// <summary>
    /// The agent it works in or acts on was never made: the create-agent step its <c>in</c> or <c>of</c> names ended
    /// otherwise than done, where what depends on it starts all the same - skipped, passed over, handled, or for an
    /// <c>always</c> step failed or never run.
    /// </summary>
    AgentNotMade,
}

/// <summary>
/// Where a step's failure left the run: one fact, held once on the instance and named once in its record
/// (<c>failure</c>), so a resume reads from the records alone which failures stand, which it runs again and which it
/// still owes. A failure a handler took says so by its <c>handledBy</c>, and one waiting on a question by the question.
/// </summary>
public enum FlowFailure
{
    /// <summary>The run passed it over (<c>onFailure: continue</c>): what depends on it runs on its output as Missing.</summary>
    PassedOver,

    /// <summary>
    /// Nothing handled it and its policy ended the run (<c>stop</c>): the session ended failed over it, and the next
    /// session runs it again, as a new attempt - save one an answer <c>fail</c> ended, which stands.
    /// </summary>
    EndedRun,

    /// <summary>
    /// It ended while a safe pause recorded the machine steps the pause left running, when nothing is raised and nothing
    /// asked: what its failure owes - its handlers, its policy - is the next session's to make, before anything runs.
    /// </summary>
    Owed,

    /// <summary>It wrote no <c>onFailure</c> and handed its failure to its group, which starts nothing more and ends failed with its code.</summary>
    TakenByGroup,
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

    /// <summary>A hard stop, the answer <c>abort</c>, or the run asked to stop where it stood ended it: it goes on at a resume.</summary>
    Stopped,
}

/// <summary>How a session of a run ended: its status, the failures it counted, the questions it leaves open.</summary>
/// <param name="Status">How the run stands at the session's end.</param>
/// <param name="PassedOver">The instances whose failures the run passed over (<c>onFailure: continue</c>), in the order they ended.</param>
/// <param name="Failed">The instances whose failures ended the run failed, in the order they ended.</param>
/// <param name="OpenQuestions">The questions the session leaves open, in the order they were raised.</param>
/// <param name="Summary">The session in words: <c>done; 2 steps failed and were passed over: ci, items[3].verify</c>.</param>
/// <param name="Pause">The safe pause the session ended by, where one asked for it: its cause, whether the machine steps were stopped too, and every handoff it wrote.</param>
public sealed record FlowSessionEnd(
    FlowRunStatus Status,
    IReadOnlyList<string> PassedOver,
    IReadOnlyList<string> Failed,
    IReadOnlyList<FlowQuestion> OpenQuestions,
    string Summary,
    FlowPauseState? Pause = null);

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

    /// <summary>When this instance's work last began; null until it has.</summary>
    public DateTimeOffset? StartedAt { get; internal set; }

    /// <summary>When this instance ended; null until it has.</summary>
    public DateTimeOffset? FinishedAt { get; internal set; }

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

    /// <summary>Which kind of reason <see cref="NotRunBecause"/> says, where the step is <see cref="FlowInstanceState.NotRun"/>; null otherwise.</summary>
    public FlowNotRunCause? NotRunCause { get; internal set; }

    /// <summary>Where this step's failure left the run, where it failed and a standing was reached; null for every other ending.</summary>
    public FlowFailure? Failure { get; internal set; }

    /// <summary>Whether the run passed this failure over (<c>onFailure: continue</c>), so what depends on it runs on its output as Missing.</summary>
    public bool PassedOver => Failure == FlowFailure.PassedOver;

    /// <summary>
    /// The kind of the question whose <c>fail</c> answer ended this step, where one did: the engine's fact
    /// <c>&lt;step&gt;.question</c>, which a handler reads. <see langword="null"/> for every other ending.
    /// </summary>
    public string? EndedByQuestion { get; internal set; }

    /// <summary>The handoff a safe pause wrote when it stopped this step, where one did; <see langword="null"/> otherwise.</summary>
    public FlowHandoff? Handoff { get; internal set; }

    /// <summary>The id of the question this instance waits on, where one is open; <see langword="null"/> otherwise.</summary>
    public string? WaitingQuestion { get; internal set; }

    /// <summary>Whether a resume brought this instance in as it stood: what it left Running is a crash's own, for nothing else runs between two sessions.</summary>
    internal bool FromResume;

    /// <summary>
    /// The agent a create-agent step makes, as the engine named it before the step first ran - its output names the same
    /// agent once it is made; null for every other step, and for one whose name is spent until it runs again.
    /// </summary>
    public string? Made { get; internal set; }

    /// <summary>The model the agent of <see cref="Made"/> is made for; null where <see cref="Made"/> is, and where none resolved.</summary>
    public string? MadeModel { get; internal set; }

    /// <summary>
    /// The agent this step works in or acts on - the one its <c>in</c> or <c>of</c> names, as that step's create-agent made
    /// it, or the one the step it goes on with works in - stored where the step is drawn, or else where it first starts, as
    /// <see cref="Made"/> is: a later visit of that create-agent making another agent never changes which agent this one
    /// works on, so its record, its directory and the agents the run holds open name the same agent from first to last.
    /// Null for a step that names none, and until its agent is made.
    /// </summary>
    public string? WorksIn { get; internal set; }

    /// <summary>Cancels what this instance's runner runs: a safe pause that stops machine steps and a hard stop cancel it.</summary>
    internal CancellationTokenSource? Stoppable;

    /// <summary>The runner's work for the attempt that runs now, until it has answered: a stop waits for it to end before it says the step stopped.</summary>
    internal Task? Runner;

    /// <summary>Whether a stop cancelled this attempt and waits, bounded, for its runner to end: whatever it then answers, it ends stopped.</summary>
    internal bool Draining;

    /// <summary>
    /// The timer of the step's time limit (<see cref="WorkflowStepTypes.TimeLimitOf"/>: its own <c>timeoutMinutes</c>, else
    /// its type's) for the run in flight, from its start until its runner answers; null where neither the step nor its type
    /// says one, and while it waits on a question.
    /// </summary>
    internal CancellationTokenSource? TimeLimit;

    /// <summary>Whether this attempt passed its time limit and was stopped for it: whatever its runner then answers, it ends incomplete.</summary>
    internal bool PastTimeLimit;

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

    /// <summary>
    /// What the step's agent assumed where a person could have told it - the envelope's own list, masked - kept for the
    /// step's record whatever the attempt ended by, each answer of the step adding to it; empty where nothing was assumed.
    /// </summary>
    public IReadOnlyList<Agents.AgentAssumption> Assumptions { get; internal set; } = [];

    /// <summary>The red legs alone, in the order they ran; the step's failure names the worst of them.</summary>
    public IReadOnlyList<FlowLeg> RedLegs { get; internal set; } = [];

    /// <summary>The answers the questions of this visit were given, in the order they were given.</summary>
    public IReadOnlyList<FlowAnswerGiven> Answers => _answers;

    /// <summary>
    /// The targets of a refresh or a rebase of <c>others</c>, in the order of their names, each with where it stands:
    /// resolved where the step first starts, and kept in its record; null for every other step, and before it starts.
    /// </summary>
    public IReadOnlyList<FlowTargetRecord>? Targets { get; internal set; }

    /// <summary>Which visit of the step this instance is: 1 for the first, one more for each time the run was sent back to it.</summary>
    public int Visit { get; internal set; } = 1;

    /// <summary>
    /// The handler that took this step's failure, where one did: its name. Set when the failure counts as handled, so
    /// what depends on the step starts and the report says what handled it.
    /// </summary>
    public string? HandledBy { get; internal set; }

    /// <summary>What a repeat that ended with its until still false says it left, as its leftover expression read it.</summary>
    public JsonNode? Leftover { get; internal set; }

    /// <summary>The pre-existing issue this instance was raised to triage, where it is one; null for every step the schedule starts.</summary>
    public FlowPreExistingReport? Issue { get; internal set; }

    /// <summary>The pre-existing issues this step reported whose triage is not raised yet, in the order reported; empty where none is owed.</summary>
    public IReadOnlyList<FlowPreExistingReport> IssuesOwed { get; internal set; } = [];

    /// <summary>The handler whose redirect sent the run back past this visit, by its instance id; null where none did.</summary>
    public string? LeftBy { get; internal set; }

    /// <summary>What the code alone cannot say of how this attempt ended - a refusal whose only obstacle was a held lock; none for every other ending.</summary>
    public OutcomeCause Cause { get; internal set; }

    internal readonly List<FlowAnswerGiven> _answers = [];
    internal Scope? DeclaredScope;
    internal FlowInstance? GroupOf;
    internal Scope? OwnScope;
    internal readonly List<Scope> Children = [];
    internal (int Exit, string? Error, string Member)? Failing;
    internal int ExtraIterations;
    internal readonly List<FlowClaim> HeldClaims = [];

    /// <summary>
    /// What each attempt that ended while its runner still ran - its drain's bound passed - claimed, by that attempt: each
    /// held until its own runner answers, so nothing that conflicts with what that runner may still write is admitted
    /// meanwhile, a later attempt of the step itself among it. A step of <c>others</c> may have more than one - one for
    /// each target whose runner outlived its turn - and the answer of one gives back its own alone. Empty where no runner
    /// outlived its attempt.
    /// </summary>
    internal readonly List<(int Attempt, IReadOnlyList<FlowClaim> Claims)> Lingering = [];

    internal readonly HashSet<string> LegsInCopies = [];
    internal readonly HashSet<string> LegsInTree = [];
    internal readonly HashSet<string> CopiesMade = [];
    internal readonly HashSet<string> LegsEnded = [];

    /// <summary>Whether this attempt's runner has said every leg it runs is placed.</summary>
    internal bool EveryLegPlaced;

    /// <summary>Whether this attempt let its tree go while its legs work on: every copy made, every leg in the tree itself ended.</summary>
    internal bool Copied;

    /// <summary>The instance whose ending raised this one - a handler's source, the step whose failure a recovery took.</summary>
    internal FlowInstance? RaisedBy;

    /// <summary>The id of the instance whose ending raised this one, as the record holds it until the resume resolves it.</summary>
    internal string? RaisedByRecord;

    /// <summary>
    /// Whether the failure of this instance is settled: handled by a handler, passed over, asked of a person, or ended
    /// the run. While it is not, what depends on the instance waits, for a handler or a recovery may still take it.
    /// </summary>
    internal bool FailureSettled = true;

    /// <summary>What the ending of this step owes and owes no longer: each handler declared beside it, and where it stands.</summary>
    internal readonly List<FlowHandlerState> _handlers = [];

    /// <summary>The fix a person gave for the failure a recovery of this instance works on, where recoveryBound was answered direct.</summary>
    internal string? Direction;
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
/// The handlers an ending raises (<c>on</c>, <c>then</c>, and the visits a redirect sends the run back for), a repeat's
/// <c>rounds</c> and <c>blocking</c>, the recovery, the pre-existing issues a step reports, the safe pause, the hard stop
/// and the budget are here too, in memory. Their records on disk - the report, <c>flow.json</c>, <c>stop.json</c>, the
/// handoff file, the resume and the refusals that guard it - are built on this, through <see cref="IFlowRecords"/>.
/// </para>
/// </remarks>
public sealed class FlowCoordinator
{
    private readonly WorkflowDefinition _definition;
    private readonly IFlowStepRunner _runner;
    private readonly IFlowRunnerFacts _runnerFacts;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _inputs;
    private readonly FlowRunPlace? _place;
    private readonly IReadOnlyDictionary<string, string> _agentModels;

    /// <summary>Reads the text of a file the run's workflow names, by its name in the definition; null where the run reads none.</summary>
    private readonly Func<string, string>? _workflowFile;

    /// <summary>Deletes a read-only agent of the run whose work is over, as the engine is to (<c>DeleteAsync</c>, applied); null where the run deletes none.</summary>
    private readonly Func<string, string, CancellationToken, Task<Results.CommandOutcome>>? _deleteAgent;

    /// <summary>Every read-only agent deletion the run has begun, seen to before its session ends: a deletion left half-done by a session's end is a record the next command reads as one.</summary>
    private readonly List<Task> _deletions = [];

    /// <summary>Every agent name the session gave, a spent one's among them: never given again, though no step holds it any more.</summary>
    private readonly HashSet<string> _agentNamesGiven = new(StringComparer.Ordinal);

    private readonly Channel<Message> _messages = Channel.CreateUnbounded<Message>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<string, FlowInstance> _instances = new(StringComparer.Ordinal);
    private readonly List<FlowInstance> _created = [];
    private readonly List<Scope> _scopes = [];
    private readonly List<FlowQuestion> _questions = [];

    /// <summary>Every answer handed over through the inbox that the run refused, in the order refused: written into <c>questions.json</c> with the questions.</summary>
    private readonly List<FlowRefusedAnswer> _refused = [];
    private readonly HashSet<string> _open = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FlowInstance?> _answerable = new(StringComparer.Ordinal);
    private readonly List<Wake> _wakes = [];
    private readonly Dictionary<string, FlowInstance> _agentsOpen = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _declarationOrder = new(StringComparer.Ordinal);

    /// <summary>
    /// How many times a configuration has given each counted answer, by where it is counted
    /// (<see cref="FlowQuestions.CountedAt"/>): counted as the engine gives each, and counted again by a resume from the
    /// answers <c>questions.json</c> holds, so a count spent in one session stays spent in every later one.
    /// </summary>
    private readonly Dictionary<string, int> _answerSpent = new(StringComparer.Ordinal);

    private Scope _top = null!;
    private bool _stopping;
    private bool _alwaysPhase;
    private bool _inSession;
    private bool _answeredAtOnce;
    private volatile bool _pausing;

    /// <summary>A hard stop was asked: read on every thread that asks for a pause, a stop or a handoff turn.</summary>
    private volatile bool _hardStop;

    /// <summary>The safe pause of this session is complete - its last handoff written - so a later stop is what <c>--all</c> is.</summary>
    private volatile bool _pauseComplete;
    private FlowPauseRequest? _pauseRequest;
    private FlowPauseState? _pause;
    private readonly FlowBudgetSpent _spent = new();
    private readonly string? _runName;
    private readonly Dictionary<string, DateTimeOffset> _reasonStarted = new(StringComparer.Ordinal);
    private readonly List<CancellationTokenSource> _handoffBounds = [];
    private int _budgetGranted;
    private bool _pauseAllAsked;
    private bool _stopApplied;
    private int _questionsRaised;
    private FlowSessionEnd? _last;
    private Task<Message>? _pending;
    private IFlowRecords? _records;

    /// <summary>
    /// The writes a session's start made, held in the order made - each with the run as it stood at that write - until
    /// it is known the session goes on: a resume refused writes nothing of itself. Each answer weighed meanwhile is held
    /// with them, settled once what became of it is written and handed back where the hold is dropped.
    /// </summary>
    private List<HeldWrite>? _held;

    /// <summary>
    /// An answer was taken before the session's first admit - answer-orchestrator's, one handed over to an engine that
    /// ended, one offered before the session began, or the configuration's at its start: the session goes on, never
    /// refused.
    /// </summary>
    private bool _answeredAtStart;

    /// <summary>The session is at its start: taking up what the run owes, before its first admit.</summary>
    private bool _starting;

    private FlowResumeState? _resume;
    private bool _replaying;
    private int _sessions;
    private DateTimeOffset? _endedAt;
    private readonly Dictionary<string, PendingEnding> _pendingEndings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Scope, string Step), int> _recoveryVisits = new();
    private readonly Dictionary<(string Scope, string Step), int> _recoveryExtra = new();

    /// <summary>The answers a resume found written whose change the step's record does not hold yet: the next session carries each out at its start, once.</summary>
    private readonly List<(FlowInstance Waiting, FlowQuestion Asked)> _carryAtStart = [];

    private readonly List<PreExistingIssue> _issues = [];
    private readonly List<PreExistingTally> _tally = [];
    private readonly List<FlowInstance> _pendingFixes = [];
    /// <summary>The redirects the run owes, in the order they were decided: each is made in its turn, once nothing it resets is running.</summary>
    private readonly List<PendingRedirect> _redirects = [];

    /// <summary>The alone step waiting for quiet, while it waits: nothing new is admitted until it has started.</summary>
    private FlowInstance? _barrier;

    /// <summary>One pre-existing issue and what became of it, as <c>{run.preExisting}</c> says it and the run counts it.</summary>
    /// <param name="State">What became of it: <c>listed</c>, <c>being fixed</c>, <c>fixed</c>, <c>duplicate of pe-&lt;n&gt;</c> or <c>unproven</c>.</param>
    /// <param name="Line">The issue as a person reads it: its title and where it is.</param>
    /// <param name="Triage">The triage instance that took it; null for none.</param>
    private sealed record PreExistingTally(string State, string Line, FlowInstance? Triage = null);

    /// <summary>One ending whose handlers are raised and not all settled: the source, and what each handler did.</summary>
    /// <param name="Source">The step whose ending raised them.</param>
    /// <param name="Failed">Whether the ending was a failure.</param>
    /// <param name="Handlers">Every handler raised for it, in declaration order.</param>
    /// <param name="Then">The one among them that names a then, where one does.</param>
    private sealed record PendingEnding(FlowInstance Source, bool Failed, List<FlowInstance> Handlers, FlowInstance? Then);

    /// <summary>A redirect the run owes: written to its records before the first reset, made in its turn once nothing it resets runs.</summary>
    private sealed class PendingRedirect(FlowInstance handler, string target)
    {
        public FlowInstance Handler { get; } = handler;

        public string Target { get; } = target;

        /// <summary>The declarations the redirect resets: the target and every step of its scope that depends on it.</summary>
        public List<(Scope Scope, WorkflowStep Declaration)> Set { get; } = [];

        /// <summary>
        /// What a pending redirect keeps from starting: for each scope it touches, the member names it will reset -
        /// or null where the whole scope is reset, a group with all that is in it.
        /// </summary>
        public Dictionary<string, HashSet<string>?> Frozen { get; } = new(StringComparer.Ordinal);

        /// <summary>The question the redirect asked where it would leave an agent open, and none answered yet.</summary>
        public string? Asking { get; set; }
    }

    /// <summary>Starts a run of <paramref name="definition"/> over <paramref name="runner"/>.</summary>
    /// <param name="definition">The workflow, as it was read.</param>
    /// <param name="runner">The step runner every step of the run is given to.</param>
    /// <param name="runnerFacts">What the engine asks of the runners a workflow names.</param>
    /// <param name="clock">How the run waits: every retry waits by this.</param>
    /// <param name="inputs">The values the run is started with, by input name.</param>
    /// <param name="records">Records the run on disk at every change, in the run's own order; null for a run that holds nothing.</param>
    /// <param name="resume">The state a killed or stopped run left, to go on with; null for a run that begins.</param>
    /// <param name="runName">
    /// The run's name - <c>r&lt;N&gt;</c>, or the one <c>--run</c> gave - which <c>run.name</c> reads and the budget's
    /// question says; null for a run held in memory alone, whose name reads empty and whose question says "the run".
    /// </param>
    /// <param name="place">Where the run works, handed to every step; null for a run held in memory alone.</param>
    /// <param name="agentModels">
    /// The model each create-agent step makes its agent for, and each reason step that names no agent its own read-only
    /// one, by the step's path (<see cref="WorkflowValidator.AgentModels"/>); a step it names none for is handed none,
    /// which its runner refuses.
    /// </param>
    /// <param name="workflowFile">
    /// Reads the text of a file the run's workflow names, by its name in the definition, throwing with why where it cannot
    /// (<see cref="WorkflowNamedFiles.ReadText"/>): a pull request's body, and a reason step's prompt files. Null where
    /// the run reads none, and a step that needs one then fails 12, saying so.
    /// </param>
    /// <param name="deleteAgent">
    /// Deletes a read-only agent of the run whose work is over, applied, as the engine is to
    /// (<c>IAgentService.DeleteAsync</c>); null where the run deletes none, which a run held in memory alone never does.
    /// </param>
    /// <exception cref="HarnessException">An input the run needs is missing, or a value is outside the ones its input takes.</exception>
    public FlowCoordinator(
        WorkflowDefinition definition,
        IFlowStepRunner runner,
        IFlowRunnerFacts runnerFacts,
        TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? inputs = null,
        IFlowRecords? records = null,
        FlowResumeState? resume = null,
        string? runName = null,
        FlowRunPlace? place = null,
        IReadOnlyDictionary<string, string>? agentModels = null,
        Func<string, string>? workflowFile = null,
        Func<string, string, CancellationToken, Task<Results.CommandOutcome>>? deleteAgent = null)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(runnerFacts);

        _definition = definition;
        _runner = runner;
        _runnerFacts = runnerFacts;
        _clock = clock ?? TimeProvider.System;
        _inputs = FlowInputs.Resolve(definition, inputs);
        _records = records;
        _runName = runName;
        _place = place;
        _agentModels = agentModels ?? new Dictionary<string, string>(StringComparer.Ordinal);
        _workflowFile = workflowFile;
        _deleteAgent = deleteAgent;

        foreach (var (path, _) in FlowScope.Declared(definition))
        {
            _declarationOrder.Add(path, _declarationOrder.Count);
        }

        if (resume is { } state)
        {
            _resume = state;
            Replay(state);
        }
    }

    /// <summary>Every instance the run has made so far, by its id.</summary>
    public IReadOnlyDictionary<string, FlowInstance> Instances => _instances;

    /// <summary>Every question the run has raised, answered and open, in the order raised.</summary>
    public IReadOnlyList<FlowQuestion> Questions => _questions;

    /// <summary>The questions the run leaves open, in the order raised.</summary>
    public IReadOnlyList<FlowQuestion> OpenQuestions => [.. _questions.Where(question => _open.Contains(question.Id))];

    /// <summary>The failures that stand as <paramref name="failure"/> says, in the order they ended: read from each instance's own standing, the one owner of it.</summary>
    private List<FlowInstance> Standing(FlowFailure failure)
        => [.. _created.Where(instance => instance.Failure == failure).OrderBy(instance => instance.FinishedAt ?? DateTimeOffset.MaxValue)];

    /// <summary>The failures a handler - or an answer that accepted an ending at its bound - took, in the order they ended.</summary>
    private List<FlowInstance> Handled
        => [.. _created.Where(instance => instance.State is FlowInstanceState.Failed && instance.HandledBy is not null).OrderBy(instance => instance.FinishedAt ?? DateTimeOffset.MaxValue)];

    /// <summary>
    /// Sets where <paramref name="instance"/>'s failure left the run, and writes its own record with it: the standing is
    /// on disk the moment it is reached, for a resume reads it from the step's own record, never from memory.
    /// </summary>
    private void SetFailure(FlowInstance instance, FlowFailure? failure)
    {
        instance.Failure = failure;
        WriteStepRecordOf(instance);
    }

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

            return _open.Count > 0 ? FlowRunStatus.Paused : _hardStop ? FlowRunStatus.Stopped : _pausing ? FlowRunStatus.Paused : _stopping || Standing(FlowFailure.EndedRun).Count > 0 ? FlowRunStatus.Failed : FlowRunStatus.Done;
        }
    }

    /// <summary>
    /// Asks for a safe pause of the run: the first interrupt typed at the engine's terminal, the command
    /// <c>pause-orchestrator</c>, a front end's stop button, or the engine's own budget at its reserve. The session
    /// this run is in ends in order: no step more is started, every reason step in progress hands off, the machine
    /// steps in progress are left to finish (<paramref name="all"/> stops them) and are recorded, and the run ends
    /// <see cref="FlowRunStatus.Paused"/>. A later request that says <paramref name="all"/> after one that did not
    /// stops the machine steps the first left running. A step its time limit stopped already is neither handed off nor
    /// stopped again: it ends as that stop ends it.
    /// </summary>
    /// <param name="cause">Who asks for the pause, one of <see cref="FlowPauseRequest.Causes"/>.</param>
    /// <param name="all">Whether the machine steps in progress are stopped too.</param>
    public void RequestPause(string cause, bool all = false)
    {
        if (!FlowPauseRequest.Causes.Contains(cause, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(cause), cause, "a pause is asked for by an interrupt, a command, a front end, or the budget");
        }

        if (_hardStop)
        {
            return;
        }

        if (_pausing)
        {
            // A later request changes one thing at most: one that says all, after one that did not, stops the machine
            // steps the first left running. The loop applies it, for the graph is the loop's alone to change; this
            // only asks.
            if (all)
            {
                _pauseAllAsked = true;
                WakeTheLoop();
            }

            return;
        }

        _pauseRequest = new FlowPauseRequest(cause, all);
        WakeTheLoop();
    }

    /// <summary>
    /// The second interrupt, or <c>stop-orchestrator</c>, by when it comes. While the handoffs are being written - or
    /// where no pause is under way - it stops the run where it stands: everything in flight is cancelled, the handoff
    /// turns with it, and ends <see cref="FlowInstanceState.Stopped"/> once its runner has ended; no handoff is kept, the
    /// <c>always</c> steps do not run, and the run ends <see cref="FlowRunStatus.Stopped"/>, as the answer <c>abort</c>
    /// ends it too. Once the pause is complete - every agent handed off - it is what <c>--all</c> is: the machine steps
    /// the engine records are stopped, and the run stays <see cref="FlowRunStatus.Paused"/>. The loop applies it where a
    /// session is running; between sessions the next one begins by applying it.
    /// </summary>
    public void RequestStop()
    {
        if (_pauseComplete && !_hardStop)
        {
            _pauseAllAsked = true;
            WakeTheLoop();
            return;
        }

        BeginHardStop();
    }

    /// <summary>
    /// Asks for the hard stop: what a second interrupt, <c>stop-orchestrator</c> and the answer <c>abort</c> share. It
    /// changes nothing of the graph, which is the loop's alone: it cancels the handoff turns under way - a turn that
    /// begins after it ends at once - and wakes the loop, which applies it.
    /// </summary>
    private void BeginHardStop()
    {
        _hardStop = true;
        _pauseRequest = null;
        CancelHandoffTurns();
        WakeTheLoop();
    }

    /// <summary>
    /// The stop itself, applied by the one who owns the graph: the loop, or a session that begins over a stop asked
    /// between sessions. Every step in flight is stopped - each waited for until its runner has ended - and nothing that
    /// waits is touched: a step that never started stays as it was and a question stays open, for the resume goes on
    /// with them by the same path a pause's resume takes.
    /// </summary>
    private void ApplyTheStop()
    {
        _stopApplied = true;
        _pausing = false;
        _pauseRequest = null;

        foreach (var instance in _created.Where(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup).ToList())
        {
            Stop(instance);
        }

        // No handoff this pause wrote is kept: the stop cancelled its turns, and whatever they had written is not a
        // stop's to keep. One an earlier pause wrote that no prompt holds yet stays, for the resume gives it.
        foreach (var handed in _created.Where(instance => instance.Handoff is not null && _pause?._handoffs.Contains(instance.Id) == true).ToList())
        {
            handed.Handoff = null;
        }

        if (_pause is { })
        {
            _pause._handoffs.Clear();
            _pause = null;
        }
    }

    /// <summary>
    /// Stops one step in flight - a hard stop, a pause that says <c>--all</c>, a <c>ci-wait</c> a pause meets: its work
    /// is cancelled, and it ends <see cref="FlowInstanceState.Stopped"/> once its runner has ended - at once where none
    /// runs, as a retry waiting its turn - and no later than <see cref="PointOfNoReturn.SafePauseDrain"/> after the stop,
    /// which a runner that does not end is recorded as having passed.
    /// </summary>
    private void Stop(FlowInstance instance)
    {
        if (StopWork(instance))
        {
            instance.Draining = true;
            return;
        }

        End(instance, FlowInstanceState.Stopped, null, null);
    }

    /// <summary>
    /// What every stop of a step in flight does first - a stop's, and its time limit's: its work cancelled, its timer and
    /// its wakes gone, a reason step's minutes counted, and where its runner still runs the drain's bound armed, by which a
    /// runner that does not end is waited for no longer. Whether its runner still runs.
    /// </summary>
    private bool StopWork(FlowInstance instance)
    {
        instance.Stoppable?.Cancel();
        DisarmTimeLimit(instance);
        _ = _wakes.RemoveAll(wake => ReferenceEquals(wake.Instance, instance));
        CountMinutes(instance);

        if (instance.Runner is not { IsCompleted: false })
        {
            return false;
        }

        var bound = PointOfNoReturn.SafePauseDrain;
        _wakes.Add(new Wake(instance, _clock.GetUtcNow() + bound, Task.Delay(bound, _clock), Drain: true));
        return true;
    }

    /// <summary>What a step whose runner did not end within the drain's bound of its stop says of it.</summary>
    private static string RunnerNotEnded
        => $"its runner did not end within {PointOfNoReturn.SafePauseDrain.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s of the stop";

    /// <summary>Cancels every handoff turn in flight, wherever the loop is parked awaiting them.</summary>
    private void CancelHandoffTurns()
    {
        CancellationTokenSource[] inFlight;

        lock (_handoffBounds)
        {
            inFlight = [.. _handoffBounds];
        }

        foreach (var bound in inFlight)
        {
            // A turn that ended inside its bound already has let go of it.
            bound.TryCancel();
        }
    }

    /// <summary>Wakes the loop where it waits on the channel: a request made from outside the session must reach it.</summary>
    private void WakeTheLoop()
        => _ = _messages.Writer.TryWrite(default);

    /// <summary>
    /// Drives the run until nothing more can run: a session. A session ends done, failed, paused on an open
    /// question, or ended by a safe pause or a hard stop; a paused run goes on when a question is answered
    /// (<see cref="TryAnswer(string, string, out string?)"/>) and this is called again.
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

        // A stop the last session applied has ended that session, and this one is its resume: the run goes on. One
        // asked since - between sessions, by an abort answered then - is applied before anything else, and this
        // session says so over what it finds.
        var stopPending = _hardStop && !_stopApplied;

        if (!stopPending)
        {
            _hardStop = false;
            _stopping = false;
        }

        _inSession = true;
        _pausing = false;
        _pauseComplete = false;
        _pauseAllAsked = false;
        _alwaysPhase = false;
        _stopApplied = false;
        _pause = null;
        _endedAt = null;
        _sessions++;

        // What the session's start writes is held until it is known the session goes on: a resume that can run nothing
        // is refused below, and a refused resume writes nothing of itself.
        _held = _records is null ? null : [];
        _starting = true;

        try
        {
            foreach (var (waiting, asked) in _carryAtStart.ToList())
            {
                // An answer written whose change the step's record does not hold yet: carried out now, once, as it would
                // have been had the session that took it gone on.
                _ = _carryAtStart.Remove((waiting, asked));
                var last = asked.Given[^1];
                var was = waiting.State;
                Carry(waiting, asked, last.Answer, last.Text, last.By);
                Record(FlowChangeKind.Step, waiting, from: was, to: waiting.State, attempt: waiting.Attempt, detail: $"'{asked.Id}' answered '{last.Answer}', carried out at the resume");
            }

            if (stopPending)
            {
                ApplyTheStop();
            }
            else
            {
                AnswerWhatTheConfigurationAnswers();
            }

            // A group stands open over its members whatever stopped them, and goes on as it was: it is no step that
            // runs, and what it holds is its members'.
            foreach (var group in _created.Where(instance => instance.Step.IsGroup && (instance.State is FlowInstanceState.Stopped || instance.FromResume)).ToList())
            {
                group.FromResume = false;

                if (group.State is FlowInstanceState.Stopped)
                {
                    group.State = FlowInstanceState.Running;
                }
            }

            // A stop asked between sessions - an abort answered then - ends the session it begins where the run stands,
            // as a stop in a session ends it: nothing it found stopped runs again or is given a handoff in place of its
            // own, nothing owed is made, nothing starts. All of it stays as the records hold it, for the session that
            // goes on.
            if (!stopPending)
            {
                GoOnWhereTheRunStood();
            }

            // The session is recorded where a run's records are: begun, or taken up from one another left.
            Record(
                _sessions == 1 ? FlowChangeKind.SessionBegan : FlowChangeKind.Resumed,
                detail: _resume is null ? null : $"taken up from a run state holding {_resume.File.Steps.Count} steps");

            // What waits for the loop already is taken before its first admit: an answer offered before the session
            // began is taken by it, and what a step spent is counted.
            EachQueued(TakeMessage);

            // A resume beside an open question is not refused where something can run; it is refused only where
            // nothing can until the question is answered - before anything of its own is written, for the held writes
            // are never made. A session an answer began goes on, as does one whose budget question is open: it is
            // paused again on that question, which a person answers.
            if (_resume is not null
                && _sessions == 1
                && !_answeredAtStart
                && OpenBudgetQuestion is null
                && NextIds().Count == 0
                && _created.FirstOrDefault(instance => instance.WaitingQuestion is not null) is { } waitsOnOne)
            {
                throw new HarnessException(
                    HarnessExit.Refused,
                    $"nothing of this run can run until the question of step '{waitsOnOne.Id}' is answered; answer-orchestrator answers it");
            }

            // The session goes on: what its start wrote is written now, in the order it was made.
            _starting = false;
            MakeTheHold();

            while (true)
            {
                Progress();
                CheckBudget();

                if (_hardStop && !_stopApplied)
                {
                    // A stop asked while the loop was parked - between two waits, or while the handoff turns ran -
                    // is applied here, by the one who owns the graph.
                    ApplyTheStop();
                }

                if (_pauseAllAsked && _pause is { All: false })
                {
                    // A later request that says all, after one that did not - or a stop once the pause is complete:
                    // it stops the machine steps the first left running, never a group, which stands open over its
                    // members, and the pause's record says so. A step its time limit stopped already is stopped no
                    // second time: it ends as that stop ends it, as the pause itself left it.
                    _pauseAllAsked = false;
                    _pause.All = true;

                    foreach (var instance in _created.Where(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup && instance.Step.Type is not ("reason" or "ci-wait") && !instance.Draining && !instance.PastTimeLimit).ToList())
                    {
                        Stop(instance);
                    }
                }

                if (_answeredAtOnce)
                {
                    // A configuration answered a question this pass raised, in the place of a person: its answer has
                    // already changed the graph, so the loop goes round again rather than wait on what asked.
                    _answeredAtOnce = false;
                    continue;
                }

                if (_pauseRequest is { } request && !_pausing)
                {
                    // The safe pause, in order: recorded, nothing more started from here on, the handoffs written,
                    // the machine steps left to finish or stopped.
                    await PauseInOrderAsync(request, cancellationToken).ConfigureAwait(false);
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
                // and a group whose member waits on a question can run nothing until it is answered - a stopped
                // step's runner still ending, a retry's time coming, or a completion still unread.
                if (_created.Any(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup) || _wakes.Count > 0 || started > 0)
                {
                    await WaitNextAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // A step that waits on a claim a runner that outlived its attempt still holds waits for that runner's
                // answer, which gives it back: it is never failed as stalled over a claim that will be freed.
                if (_created.Any(instance => instance.Lingering.Count > 0) && AdmissionWalk().Any(decision => decision.WaitsOn.Contains("claim")))
                {
                    await WaitNextAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                // An answer offered from outside the session may sit unread while nothing runs: it is taken before
                // the session ends over a question an answer has settled, for the loop alone applies it.
                if (_messages.Reader.TryPeek(out _))
                {
                    await WaitNextAsync(cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (TryEnterAlwaysPhase())
                {
                    continue;
                }

                // Nothing runs, nothing is due and nothing more can be drawn: a step left waiting now - held behind a
                // limit nothing will free - can never start, and fails as any step does, so the session never ends
                // done over it.
                if (FailStalled())
                {
                    continue;
                }

                break;
            }
        }
        finally
        {
            // A start refused, or ended by a defect, leaves nothing held to be written by whatever comes after it - an
            // answer it weighed handed back, for nothing records it; and what reached the loop after its last read is
            // handed back or counted, however the session ended.
            var dropped = _held;
            _held = null;
            Drop(dropped ?? []);
            _starting = false;
            HandBackWhatIsLeft();
            _inSession = false;

            // Every read-only agent deletion the session began is seen to before it ends, for a deletion left
            // half-done by an ending session is a record the next command reads as one. Each swallows its own failure:
            // the deleter said it, and the run is not stopped by it.
            if (_deletions is [_, ..] && !(_pausing || _hardStop))
            {
                await Task.WhenAll(_deletions).ConfigureAwait(false);
                _deletions.Clear();
            }
        }

        _last = Finish();
        return _last;
    }

    /// <summary>
    /// A session's start where the run goes on, made before anything runs: what a pause, a stop or a crash stopped runs
    /// again as a new attempt, its handoff given; a failure that ended the run runs again, and what its stop left is
    /// drawn again; the always steps are drawn for this session; and what the run owed is made - owed failures, endings
    /// owed their handlers, endings whose handlers all ended, issues owed their triage, fixes owed the recovery.
    /// </summary>
    private void GoOnWhereTheRunStood()
    {
        // What a pause or a stop stopped, runs again: a new attempt of its visit, as a retry is, its handoff
        // spent. What the pause left owed - the policies of failures recorded while it recorded what it left
        // running - is made now, at the resume, before anything else runs.
        foreach (var stopped in _created.Where(instance => !instance.Step.IsGroup
                && (instance.State is FlowInstanceState.Stopped || instance.State is FlowInstanceState.Running && instance.FromResume)).ToList())
        {
            var was = stopped.State;

            if (was is FlowInstanceState.Running)
            {
                Release(stopped);
            }

            if (stopped.Step.Type == "reason" && stopped.Handoff is null)
            {
                // The attempt before was cut off and left no handoff - a crash ended the engine, or a stop ended
                // the run - so the engine writes one, for the next attempt is told what the cut-off one had done,
                // on every client and after every kind of stop. One the attempt still held - an agent's account no
                // prompt holds yet - is never written over: the next attempt is given it.
                stopped.Handoff = FlowHandoff.ByEngine(
                    was is FlowInstanceState.Running ? "the engine's process ended" : "the run was stopped",
                    $"Attempt {stopped.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)} was cut off.");
                Record(FlowChangeKind.Handoff, stopped, detail: stopped.Handoff.Why);
            }

            NewAttempt(stopped);
            stopped.FromResume = false;
            Record(FlowChangeKind.Step, stopped, from: was, to: FlowInstanceState.NotStarted, attempt: stopped.Attempt);
        }

        // A failure that ended the run - nothing handled it, its policy stopped the run - runs again, as a new
        // attempt, as what a stop stopped does: the session it ended is over, and this one goes on where the run
        // stood. A group runs again as a retry opens it, a new visit whole, its failed visit standing as it
        // ended. One an answer fail ended stands, and the run stays stopped on it.
        foreach (var failed in Standing(FlowFailure.EndedRun).Where(instance => instance.EndedByQuestion is null))
        {
            if (failed.Step.IsGroup)
            {
                SetFailure(failed, null);
                Retry(failed);
                continue;
            }

            NewAttempt(failed, reopenAnswered: false);
            Record(FlowChangeKind.Step, failed, from: FlowInstanceState.Failed, to: FlowInstanceState.NotStarted, attempt: failed.Attempt);
        }

        _stopping = Standing(FlowFailure.EndedRun).Count > 0;

        if (!_stopping)
        {
            RedrawWhatTheStopLeft();
        }

        // The always steps run once per session, however the run went: those an earlier session ran are drawn
        // again in this one's always phase, as new visits, the visits they had standing as they ended.
        foreach (var scope in _scopes.Where(ScopeLive).ToList())
        {
            foreach (var declaration in DeclarationsOf(scope).Where(declaration => IsAlways(declaration, scope)))
            {
                if (scope.Members.GetValueOrDefault(declaration.Name) is { State: FlowInstanceState.Done or FlowInstanceState.Failed or FlowInstanceState.Skipped or FlowInstanceState.NotRun })
                {
                    _ = scope.Members.Remove(declaration.Name);
                    _ = scope.Instantiated.Remove(declaration.Name);
                }
            }
        }

        // What a pause left owed, its records say, and it is made now, before anything else runs: each failure
        // recorded while the pause recorded what it left running - its handlers, its policy - and then each ending
        // whose raise it found owed, save one whose question is open, which the answer makes.
        foreach (var owed in Standing(FlowFailure.Owed))
        {
            HandleFailure(owed);
        }

        foreach (var owed in _created.Where(instance => instance.Step.On is null && instance.WaitingQuestion is null && instance._handlers.Any(handler => handler.State == "owed")).ToList())
        {
            if (owed.State == FlowInstanceState.Failed)
            {
                HandleFailure(owed);
            }
            else if (owed.State == FlowInstanceState.Done)
            {
                _ = RaiseFor(owed);
            }
        }

        foreach (var pending in _pendingEndings.Values.Where(pending => pending.Handlers.All(Ended)).ToList())
        {
            // An ending whose handlers have all ended and that its record does not say settled: a crash came between
            // the last handler's end and the source's record. It is settled now, as that end would have settled it.
            SettleEnding(pending);
        }

        foreach (var reporter in _created.Where(instance => instance.IssuesOwed.Count > 0).ToList())
        {
            // The issues a step reported while nothing could be started - a pause or a stop under way, or a crash
            // before their triage was raised - are owed in its own record, and their triage is raised now.
            RaiseOwedIssues(reporter);
        }

        // An issue a triage answered fix for whose recovery was never raised - it waited behind a visit at work, or
        // a crash came between the triage's end and the raise - is given to the recovery now, one at a time.
        DrainPendingFixes();
    }

    /// <summary>
    /// One safe pause, in order: recorded before anything is touched; nothing more started from here on; every reason
    /// step in progress handed off, all at once, each within the run's <c>handoffSeconds</c> - an agent that gives no
    /// account leaves a handoff the engine writes; the machine steps in progress left to finish - or stopped, where
    /// the request says <c>all</c> - and recorded as they end; a <c>ci-wait</c> ends Stopped at once, for it runs
    /// nothing between two reads. A step its time limit stopped already is left to end as that stop ends it.
    /// </summary>
    private async Task PauseInOrderAsync(FlowPauseRequest request, CancellationToken cancellationToken)
    {
        _pausing = true;
        _pauseRequest = null;
        _pause = new FlowPauseState(request.Cause, request.All, _clock.GetUtcNow());
        Record(FlowChangeKind.PauseBegan, detail: $"{request.Cause}{(request.All ? ", all" : string.Empty)}");

        // No retry that was waiting is started: the run goes on with it at the resume, as the instance it belongs to.
        foreach (var wake in _wakes.Where(wake => !wake.Drain).ToList())
        {
            _ = _wakes.Remove(wake);
            NewAttempt(wake.Instance);
        }

        var bound = _definition.HandoffSeconds ?? 180;
        var handingOff = new List<FlowInstance>();

        // A step its time limit has stopped already is left to end as that stop ends it - incomplete, whatever its runner
        // answers or once the drain's bound passes, and recorded as the pause records an ending: failed, or stopped where
        // a retry is left - with no handoff turn and no second stop.
        foreach (var instance in _created.Where(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup && !instance.PastTimeLimit).ToList())
        {
            if (instance.Step.Type == "reason")
            {
                // Its turn is ended where it stands - what it started is cancelled - and it is given one more, the
                // handoff turn, beside the others - which its time limit does not bound: the pause's own does.
                instance.Stoppable?.Cancel();
                DisarmTimeLimit(instance);
                CountMinutes(instance);
                Release(instance);
                handingOff.Add(instance);
            }
            else if (instance.Step.Type == "ci-wait" || request.All)
            {
                Stop(instance);
            }
        }

        foreach (var instance in handingOff)
        {
            instance.State = FlowInstanceState.Stopped;
        }

        if (handingOff.Count > 0)
        {
            // All the turns at once, each bounded; what each spent is counted, for the budget counts a handoff turn
            // as it counts the attempt it ends.
            var turns = handingOff.Select(instance => HandoffTurnAsync(instance, bound)).ToList();
            var accounts = await Task.WhenAll(turns).ConfigureAwait(false);

            for (var at = 0; at < handingOff.Count; at++)
            {
                var instance = handingOff[at];
                _spent.Minutes += accounts[at].Minutes.TotalMinutes;

                if (_hardStop)
                {
                    // No handoff is kept by a stop: the turn was cancelled, and whatever it wrote is not the stop's
                    // to keep.
                    continue;
                }

                // Where the agent gave none the engine writes it, saying why in its first line: the bound passed, the
                // turn failed - with what it failed with - or the agent's answer held nothing. An agent's account the
                // attempt still holds - an earlier pause wrote it, and no prompt holds it yet - is never written over
                // by the engine's: it stands as written, for the next attempt, and this pause wrote none.
                if (accounts[at].Text is not { Length: > 0 } && instance.Handoff is { By: "agent" })
                {
                    Record(FlowChangeKind.Step, instance, from: FlowInstanceState.Running, to: FlowInstanceState.Stopped, attempt: instance.Attempt, detail: "its handoff stands as the agent wrote it");
                    continue;
                }

                instance.Handoff = accounts[at].Text is { Length: > 0 } written
                    ? new FlowHandoff(written, "agent")
                    : FlowHandoff.ByEngine(accounts[at].Why);
                _pause._handoffs.Add(instance.Id);
                Record(FlowChangeKind.Handoff, instance, detail: instance.Handoff.By);
            }
        }

        if (_hardStop)
        {
            // A stop came while the handoffs were written: the pause never completes, and the loop applies the stop.
            return;
        }

        // The pause is complete: its last handoff written, and everything it left running recorded as it ended. A
        // stop from here on is what --all is.
        _pause.CompletedAt = _clock.GetUtcNow();
        _pauseComplete = true;
        Record(FlowChangeKind.PauseCompleted, detail: $"{_pause._handoffs.Count} handoff(s)");
    }

    /// <summary>
    /// One reason step's handoff turn: the agent's account where it gives one, what the turn spent, and - where it gave
    /// none - why, as the handoff the engine writes says it.
    /// </summary>
    private async Task<(string? Text, TimeSpan Minutes, string Why)> HandoffTurnAsync(FlowInstance instance, int seconds)
    {
        var began = _clock.GetUtcNow();
        string? text = null;
        var why = "the agent's answer to its handoff turn held no account";
        var boundPassed = false;

        try
        {
            using var boundToCancel = new CancellationTokenSource();
            lock (_handoffBounds)
            {
                _handoffBounds.Add(boundToCancel);
            }

            if (_hardStop)
            {
                // A stop asked before this turn was listed found nothing of it to cancel: the turn ends at once, for a
                // stop keeps no handoff and no turn runs out its bound after one.
                boundToCancel.Cancel();
            }

            try
            {
                using var timer = _clock.CreateTimer(_ =>
                {
                    // A turn answered inside its bound has let go of the source: the timer outlived it.
                    boundPassed = true;
                    boundToCancel.TryCancel();
                }, null, TimeSpan.FromSeconds(seconds), TimeSpan.FromMilliseconds(-1));

                // The turn is asked of the attempt the pause stopped, with every fact that attempt was asked with: a reason
                // step reads nothing where it starts, and fills no template key.
                var run = RunOf(instance, instance.Step, instance.Attempt, new Dictionary<string, FlowValue>(StringComparer.Ordinal), handoff: null);

                try
                {
                    text = await _runner.HandoffAsync(run, boundToCancel.Token).ConfigureAwait(false);

                    if (boundPassed)
                    {
                        why = $"the agent's handoff turn passed its {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s";
                    }
                }
                catch (OperationCanceledException)
                {
                    why = $"the agent's handoff turn passed its {seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} s";
                }
                catch (Exception failed)
                {
                    // An agent that cannot answer - its client ended, its conversation out of context - still leaves
                    // a handoff: the engine writes it, saying what the turn failed with.
                    why = $"the agent's handoff turn failed: {failed.Message}";
                }
            }
            finally
            {
                lock (_handoffBounds)
                {
                    _ = _handoffBounds.Remove(boundToCancel);
                }
            }
        }
        finally
        {
        }

        return (text, _clock.GetUtcNow() - began, why);
    }

    /// <summary>
    /// Adds the minutes <paramref name="instance"/>'s turn ran, where it is a reason step counted running: once, whichever of its
    /// endings - its answer, a stop, a pause, its time limit - reaches it first.
    /// </summary>
    private void CountMinutes(FlowInstance instance)
    {
        if (instance.Step.Type == "reason" && _reasonStarted.Remove(instance.Id, out var began))
        {
            _spent.Minutes += (_clock.GetUtcNow() - began).TotalMinutes;
        }
    }

    /// <summary>
    /// The minutes the run's reasoning has spent as it stands now: what the ended attempts and handoff turns counted,
    /// and the time each reason step running now has run so far - two at once counting twice.
    /// </summary>
    private double MinutesSpent()
    {
        var now = _clock.GetUtcNow();
        return _spent.Minutes + _reasonStarted.Values.Sum(began => (now - began).TotalMinutes);
    }

    /// <summary>The share of <paramref name="amount"/> the run may spend before the reserve of <paramref name="budget"/> is reached, each grant of more allowing the amount once again.</summary>
    private double ReserveOf(FlowBudget budget, double amount)
        => amount * (_budgetGranted + 1) * (100 - (budget.ReservePercent ?? 10)) / 100.0;

    /// <summary>The run's question of its budget, where one is open: asked once, and open until a person answers it.</summary>
    private FlowQuestion? OpenBudgetQuestion
        => _questions.FirstOrDefault(asked => asked.Kind == "runBudget" && _open.Contains(asked.Id));

    /// <summary>
    /// How long until the reason steps running now bring the minutes to the budget's reserve, at the rate they run
    /// together - the tick of the clock the run waits on beside its other waits; null where no minutes are budgeted,
    /// no reason step runs, or the run is pausing or stopping already.
    /// </summary>
    private TimeSpan? MinutesToReserve()
    {
        if (_definition.Budget is not { Minutes: { } minutes } budget || _reasonStarted.Count == 0 || _pausing || _hardStop || _pauseRequest is not null)
        {
            return null;
        }

        var left = ReserveOf(budget, minutes) - MinutesSpent();
        return TimeSpan.FromMinutes(Math.Max(0, left / _reasonStarted.Count));
    }

    /// <summary>
    /// The run's budget at its reserve: what was spent - the minutes as the reason steps run - reached the share of an
    /// amount the reserve leaves. The question is the run's, and the workflow's own <c>answers</c> answer it as they
    /// answer every question, through the one owner of configured answers: <c>more</c> a counted number of times allows
    /// each amount once again and the run goes on; <c>abort</c> ends the run as a hard stop does. Where they give none,
    /// the run is paused before the budget is spent, and asks when the pause has ended. While its question is open the
    /// run is held: nothing starts until a person answers it.
    /// </summary>
    private void CheckBudget()
    {
        if (_definition.Budget is not { } budget || _pausing || _hardStop)
        {
            return;
        }

        if (OpenBudgetQuestion is not null)
        {
            // Asked already - by an earlier session, or answered abort and open again: a person answers it, and the
            // configuration that gave nothing then, or the abort, is not given again.
            _pauseRequest = new FlowPauseRequest("budget", false);
            return;
        }

        if (ReachedOf(budget) is not { } hit)
        {
            return;
        }

        var options = FlowQuestions.OptionsOf("runBudget", null);

        if (ConfiguredAnswer(null, "runBudget", options) is { } configured)
        {
            var asked = BudgetQuestion(hit);
            _questions.Add(asked);
            ApplyAnswer(null, asked, configured.Answer, null, "configuration", configured.Where, through: null);
            return;
        }

        // The reserve is what the handoffs are written in, so the pause begins before the budget ends and not at it.
        _pauseRequest = new FlowPauseRequest("budget", false);
    }

    /// <summary>The amount of <paramref name="budget"/> whose reserve the run has reached, with what was spent against it; null where none has.</summary>
    private (string Kind, string SpentOf)? ReachedOf(FlowBudget budget)
    {
        var times = _budgetGranted + 1;

        if (budget.Usd is { } usd && _spent.Usd >= ReserveOf(budget, usd))
        {
            return ("usd", $"{_spent.Usd.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} of {(usd * times).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)} USD");
        }

        if (budget.Tokens is { } tokens && _spent.Tokens >= ReserveOf(budget, tokens))
        {
            return ("tokens", $"{_spent.Tokens} of {tokens * times} tokens");
        }

        var minutesSpent = MinutesSpent();

        if (budget.Minutes is { } minutes && minutesSpent >= ReserveOf(budget, minutes))
        {
            return ("minutes", $"{minutesSpent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} of {(minutes * times).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)} minutes");
        }

        return null;
    }

    /// <summary>The question the run's budget asks at its reserve, worded as the run's person reads it: the run by its name, where it was given one.</summary>
    private FlowQuestion BudgetQuestion((string Kind, string SpentOf) reached)
    {
        var reserve = _definition.Budget?.ReservePercent ?? 10;
        var run = _runName is { Length: > 0 } name ? $"run '{name}'" : "the run";
        return new FlowQuestion(
            FlowQuestionIds.Of(++_questionsRaised),
            "runBudget",
            FlowQuestions.RunsOwn,
            $"{run} has spent {reached.SpentOf} (its reserve of {reserve}% was reached)",
            FlowQuestions.OptionsOf("runBudget", null))
        {
            RaisedAt = _clock.GetUtcNow(),
            Tried = "the amounts were counted as the reason steps ran",
            Means = null,
        };
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
    /// <param name="through">How a person gave it: <c>answer-orchestrator</c> or <c>front-end</c>; null where not said.</param>
    /// <returns>Whether the answer was taken.</returns>
    public bool TryAnswer(string question, string answer, string? text, out string? problem, string? through = null)
        => TryAnswer(question, answer, text, out problem, through, inbox: null);

    /// <summary>Answers a question the run asked, as a person's answer, recorded with the inbox entry it was handed over under where one was.</summary>
    private bool TryAnswer(string question, string answer, string? text, out string? problem, string? through, string? inbox)
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

        // Which answer takes a text is the question's own record of its answers: the one owner answer-orchestrator
        // reads too.
        var needsText = asked.Held.Any(option => option.Text && string.Equals(option.Answer, answer, StringComparison.Ordinal));

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

        problem = null;
        _last = null;
        TakeAnswer(asked, answer, string.IsNullOrWhiteSpace(text) ? null : text, "person", where: null, through, inbox);
        return true;
    }

    /// <summary>What the refusal of an answer that changes one already given says: the plan update it would raise, which this build does not take.</summary>
    internal const string PlanUpdateRefused = "changes an answer already given, which raises a plan update; this build takes no plan update yet";

    /// <summary>
    /// Weighs an answer handed over - through the run's inbox, to a running session or between two - as a person's, and
    /// records what became of it before anything else is said of it: taken, the answer is written into
    /// <c>questions.json</c> under the inbox entry's id; refused - its question no longer open, or it changes an answer
    /// already given - the refusal is written there under the id too, then told as an <c>answer</c> event naming why, so
    /// the run's own history says what became of an answer nobody saw refused. The one owner of an entry weighed once: one
    /// whose id the run's records already hold - taken or refused by this session or any before it, its removal from the
    /// inbox having failed or been cut off - is never weighed again, and nothing is written for it.
    /// </summary>
    /// <param name="question">The question's id.</param>
    /// <param name="answer">The answer, spelled as the question's options spell it.</param>
    /// <param name="text">The text given with the answer, where it takes one.</param>
    /// <param name="problem">Why the answer was not taken, where it was refused.</param>
    /// <param name="through">How a person gave it; <c>answer-orchestrator</c> where the command handed it over.</param>
    /// <param name="inbox">The id of the inbox entry it was handed over under; null for an answer no inbox handed over, which is weighed as it comes, its refusal told by its event alone, for no entry waits to be settled.</param>
    /// <param name="again">Whether it changes an answer already given: a plan update, which this build refuses.</param>
    /// <returns>What became of it.</returns>
    public FlowHandedOver AnswerHandedOver(string question, string answer, string? text, out string? problem, string? through = "answer-orchestrator", string? inbox = null, bool again = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        if (inbox is not null && Recorded(inbox))
        {
            problem = null;
            return FlowHandedOver.RecordedAlready;
        }

        if (again)
        {
            problem = $"it {PlanUpdateRefused}";
        }
        else if (TryAnswer(question, answer, text, out problem, through, inbox))
        {
            return FlowHandedOver.Taken;
        }

        if (inbox is not null)
        {
            // The refusal is the run's state before it is its history: on disk under the entry's id first, so a session
            // that meets the entry again - its removal from the inbox failed, or was cut off - only empties it.
            _refused.Add(new FlowRefusedAnswer(inbox, question, answer, _clock.GetUtcNow(), problem!));
            WriteQuestions();
        }

        Record(FlowChangeKind.Answer, detail: problem);
        return FlowHandedOver.Refused;
    }

    /// <summary>Whether the run's records hold what it did with the inbox entry <paramref name="inbox"/>: an answer taken under it, or its refusal.</summary>
    private bool Recorded(string inbox)
        => _refused.Any(refused => string.Equals(refused.Inbox, inbox, StringComparison.Ordinal))
            || _questions.Any(question => question.Given.Any(given => string.Equals(given.Inbox, inbox, StringComparison.Ordinal)));

    /// <summary>
    /// Takes one answer to an open question - a person's, or the configuration's in a person's place - by the one path:
    /// the question no longer open, the answer recorded and carried out, and the record of the step that waited on it
    /// written after, from what the answer changed.
    /// </summary>
    private void TakeAnswer(FlowQuestion asked, string answer, string? text, string by, string? where, string? through, string? inbox = null)
    {
        _answeredAtStart |= !_inSession || _starting;
        _ = _open.Remove(asked.Id);
        var instance = _answerable.GetValueOrDefault(asked.Id);
        _ = _answerable.Remove(asked.Id);

        if (instance is not null)
        {
            instance.WaitingQuestion = null;
        }

        var was = instance?.State;
        ApplyAnswer(instance, asked, answer, text, by, where, through, inbox);

        if (instance is not null)
        {
            Record(FlowChangeKind.Step, instance, from: was, to: instance.State, detail: by == "person" ? $"'{asked.Id}' answered '{answer}'" : $"'{asked.Id}' answered '{answer}' by the configuration");
        }
    }

    /// <summary>
    /// Applies one answer to the question that waits on it, recording who gave it: a <c>person</c> (a caller of
    /// <see cref="TryAnswer(string, string, out string?)"/>), and through what, or the <c>configuration</c>, which
    /// answered the kind beforehand, and where. The question's own kind says what the answer does; the run's own
    /// question, its budget's, is about no step, and <paramref name="instance"/> is null for it. An answer handed over
    /// through the inbox is written with its entry's id (<paramref name="inbox"/>), in the one write that records it.
    /// </summary>
    private void ApplyAnswer(FlowInstance? instance, FlowQuestion asked, string answer, string? text, string by, string? where, string? through, string? inbox = null)
    {
        var did = asked.Kind == "runBudget" ? answer == "more" ? "each amount is allowed once again" : "the run ends stopped" : null;
        asked._given.Add(new FlowAnswerEntry(answer, text, by, where, through, _clock.GetUtcNow(), did, Inbox: inbox));
        asked.State = "answered";

        if (answer == "abort")
        {
            // abort settles nothing: the question is open again, the abort kept among its answers, and its step waits
            // on it as it did - so the resume goes on with the question asked.
            WaitOn(instance, asked);
        }

        // The answer is on disk before anything it changes, as a question is before the record of the step that waits
        // on it: a crash between the two leaves the answer written, and the next session carries out what it changes
        // from the step's own record, which still names the question.
        WriteQuestions();
        Carry(instance, asked, answer, text, by);
    }

    /// <summary>
    /// Carries out what an answer changes, once it is recorded: the question's own kind says what it does. Where an
    /// answer reached <c>questions.json</c> and the record of the step that waited on it was not written after - a crash
    /// between the two - the next session carries it out from there, once.
    /// </summary>
    private void Carry(FlowInstance? instance, FlowQuestion asked, string answer, string? text, string by)
    {
        if (answer == "abort")
        {
            // abort is taken for any question, and ends the run as a hard stop ends it: everything in flight is
            // cancelled and ends stopped.
            BeginHardStop();
        }
        else if (asked.Kind == "runBudget")
        {
            // more: each amount is allowed once again - the same budget, one more time - and the run goes on.
            _budgetGranted++;
        }

        if (instance is null)
        {
            // No step changed: the run's own counts did, and the records hold them.
            Record(FlowChangeKind.Answer, detail: $"'{asked.Id}' answered '{answer}' by {(by == "person" ? "a person" : by)}");
            return;
        }

        if (answer == "abort")
        {
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
            case "visitBound":
                AnswerVisitBound(instance, asked, answer);
                break;
            case "recoveryBound":
                AnswerRecoveryBound(instance, answer, text);
                break;
            case "openAgents":
                AnswerOpenAgents(instance, asked, answer);
                break;
            case "pause":
                AnswerPause(instance, answer, text);
                break;
            default:
                AnswerTheKind(instance, asked, answer, text);
                break;
        }
    }

    /// <summary>
    /// Answers the question a handler at its bound asks of its source's ending, which stands as it ended - done or
    /// failed - while the question is open. <c>more</c> raises the handler once more (and the same question is asked
    /// again if it comes to that). <c>accept</c> takes the ending as it stands without the handler: the other handlers
    /// it fits are raised, and a failure none of them takes counts as handled. <c>fail</c>: after a failure the source's
    /// own failure stands, unhandled, and its onFailure decides; after a success the source ends failed with the
    /// question's words and its kind as its question fact, and its failure goes on as any does.
    /// </summary>
    private void AnswerVisitBound(FlowInstance instance, FlowQuestion asked, string answer)
    {
        var handler = asked.Handler;
        var failed = instance.State == FlowInstanceState.Failed;

        switch (answer)
        {
            case "more":
                // The answer grants the handler the question named one more visit in its scope, and the same question
                // comes again where it comes to that.
                if (handler is { Length: > 0 })
                {
                    var extra = instance.DeclaredScope!.ExtraVisits;
                    extra[handler] = extra.GetValueOrDefault(handler) + 1;
                }

                _ = RaiseFor(instance);
                break;
            case "accept":
                if (!RaiseFor(instance, without: handler) && failed)
                {
                    instance.HandledBy = asked.Id;
                    instance.FailureSettled = true;
                    WriteStepRecordOf(instance);
                }

                break;
            case "fail" when failed:
                // The answer decided the failure stands: it is one a person answered fail, which a later session
                // does not run again whatever its policy makes of it.
                instance.EndedByQuestion = asked.Kind;
                PassOwed(instance);
                ApplyPolicy(instance);
                break;
            case "fail":
                instance.EndedByQuestion = asked.Kind;
                instance.Exit = null;
                instance.Error = null;
                End(instance, FlowInstanceState.Failed, FlowQuestions.FailCodeOf(asked.Kind), asked.Problem);
                HandleFailure(instance);
                break;
        }
    }

    /// <summary>
    /// Answers the question the recovery at its bound asks: <c>more</c> gives one more visit, <c>direct</c> one more
    /// with the fix given in the text - which that visit's steps read as <c>{source.direction}</c> - and <c>skip</c>
    /// skips the step, where it is skippable.
    /// </summary>
    private void AnswerRecoveryBound(FlowInstance instance, string answer, string? text)
    {
        if (answer is "more" or "direct")
        {
            var key = RecoveryOf(instance).Key;
            _recoveryExtra[key] = _recoveryExtra.GetValueOrDefault(key) + 1;

            // A direct answer's text is the fix the next visit is given, and that visit's alone.
            TakeRecoveryOf(instance, answer == "direct" ? text ?? string.Empty : null);
            return;
        }

        if (answer == "skip")
        {
            instance.FailureSettled = true;
            instance.SkippedBecause = "skipped by the answer to its bound";
            End(instance, FlowInstanceState.Skipped, instance.Exit, instance.Error);
        }
    }

    /// <summary>
    /// Answers the question a redirect that would leave an agent open asks: <c>retry</c> once each was folded or
    /// deleted - and asks again where one is still open - and <c>stay</c> gives the redirect up, the run going on as
    /// if the handler named no <c>then</c>.
    /// </summary>
    private void AnswerOpenAgents(FlowInstance handler, FlowQuestion asked, string answer)
    {
        if (_redirects.FirstOrDefault(pending => pending.Asking == asked.Id) is not { } redirect)
        {
            return;
        }

        redirect.Asking = null;

        if (answer == "stay")
        {
            _ = _redirects.Remove(redirect);

            if (handler.RaisedBy is { } source && source.State == FlowInstanceState.Failed && source.HandledBy is null)
            {
                source.HandledBy = handler.Step.Name;
                source.FailureSettled = true;
                WriteStepRecordOf(source);
            }

            return;
        }

        var open = OpenAgentsOf(redirect);

        if (open.Count > 0)
        {
            redirect.Asking = Ask(
                handler,
                "openAgents",
                $"'{handler.Step.Name}' would go back to '{redirect.Target}', and agents '{string.Join("', '", open)}' are still open",
                FlowQuestions.OptionsOf("openAgents", handler.Step));
            return;
        }

        Record(FlowChangeKind.Step, handler, detail: $"the run is sent to '{redirect.Target}' by '{handler.Id}'");
    }

    /// <summary>
    /// Answers a question of a kind a step's own work raises - a fold's consents, a reason step's limits, a wait for
    /// checks. <c>retry</c> runs the step again as a new attempt; <c>more</c>, <c>accept</c>, <c>allow</c>,
    /// <c>merge</c>, <c>stay</c> and <c>compact</c> hand the answer to the runner in the same attempt;
    /// <c>skip</c> ends the step skipped; <c>fail</c> ends it failed with the question's kind.
    /// </summary>
    private void AnswerTheKind(FlowInstance instance, FlowQuestion asked, string answer, string? text)
    {
        switch (answer)
        {
            case "retry":
                Retry(instance);
                break;
            case "skip":
                instance.SkippedBecause = "skipped by the answer to its question";
                End(instance, FlowInstanceState.Skipped, instance.Exit, instance.Error);
                break;
            case "fail" when instance.Step.Others is not null:
                // A step of others: the answer ends the target it asked of, and the step goes on with the next.
                EndTarget(instance, passed: false, FlowQuestions.FailCodeOf(asked.Kind), asked.Problem, endedByQuestion: asked.Kind);
                break;
            case "fail":
                instance.EndedByQuestion = asked.Kind;
                End(instance, FlowInstanceState.Failed, FlowQuestions.FailCodeOf(asked.Kind), asked.Problem);
                HandleFailure(instance);
                break;
            default:
                // The answer lets the step's work go on in the same attempt - a limit's `more` and `compact`, an agent's
                // `answer` and `decide` - and the runner serves it from there: the answer, its text, its kind and its
                // attempt are the attempt's, and the step is started again as the same attempt it was.
                instance._answers.Add(new FlowAnswerGiven(asked.Id, answer, text, asked.Kind, instance.Attempt));
                instance.State = FlowInstanceState.NotStarted;
                break;
        }
    }
    /// <summary>
    /// The targets of a refresh or a rebase of <c>others</c>: every agent of the run that is open with a worktree - each
    /// create-agent's whose agent no deletion closed - but the one <c>others</c> names, in the order of their names.
    /// </summary>
    private List<FlowTargetRecord> TargetsOf(FlowInstance instance)
    {
        var leftOut = CreateStepOf(instance, instance.Step.Others!)?.Made;

        return [.. _agentsOpen.Values
            .Select(made => made.Made)
            .OfType<string>()
            .Where(agent => !string.Equals(agent, leftOut, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Order(OrchestrationRules.AgentNameOrder)
            .Select(agent => new FlowTargetRecord(agent, FlowTargetRecord.NotStarted, 0))];
    }

    /// <summary>Whether <paramref name="agent"/> is still open: made by a create-agent step whose agent no deletion closed.</summary>
    private bool IsOpen(string agent) => _agentsOpen.Values.Any(made => string.Equals(made.Made, agent, StringComparison.Ordinal));

    /// <summary>The target whose turn it is: the first that has not ended, as the step's record holds them; null where every one has.</summary>
    private static FlowTargetRecord? CurrentTarget(FlowInstance instance) => instance.Targets?.FirstOrDefault(target => !target.Ended);

    /// <summary>
    /// The agent a step of <c>others</c> would work on were it admitted now - and so claims: the first target whose turn
    /// has not ended and that is still open, the targets resolved as its first start would resolve them; null where none is.
    /// </summary>
    private string? TargetOf(FlowInstance instance)
        => (instance.Targets ?? TargetsOf(instance)).FirstOrDefault(target => !target.Ended && IsOpen(target.Agent))?.Agent;

    /// <summary>
    /// Readies the turn of the target a step of <c>others</c> works on now: its targets resolved at its first start, each
    /// that was deleted before its turn passed over with why, and the attempt counted on the one whose turn it is. Where
    /// every target has ended, ends the step with them, and answers false. The targets a step run again takes its turn
    /// over are reopened by its new attempt (<see cref="NewAttempt"/>), before it is admitted on the first of them.
    /// </summary>
    private bool StartTarget(FlowInstance instance)
    {
        instance.Targets ??= TargetsOf(instance);

        while (CurrentTarget(instance) is { } target && !IsOpen(target.Agent))
        {
            instance.Targets = Replaced(instance, target with { State = FlowTargetRecord.PassedOver, Error = $"agent '{target.Agent}' was closed before its turn" });
        }

        if (CurrentTarget(instance) is not { } current)
        {
            EndTargets(instance);
            return false;
        }

        if (current.Attempts == 0)
        {
            instance.Targets = Replaced(instance, current with { Attempts = 1 });
        }

        return true;
    }

    /// <summary>
    /// Ends the turn of the target a step of <c>others</c> works on now, done or failed with its code and why; the step's
    /// next attempt is the next target's turn, admitted on its own, and where none is left the step ends with them.
    /// </summary>
    private void EndTarget(FlowInstance instance, bool passed, int exit, string? error, OutcomeCause cause = OutcomeCause.None, string? endedByQuestion = null)
    {
        var target = CurrentTarget(instance) ?? throw new InvalidOperationException($"'{instance.Id}' ended a target's turn, and no target's turn had begun");

        instance.Targets = Replaced(instance, passed
            ? target with { State = FlowTargetRecord.Done, Exit = HarnessExit.Success, Error = null, Cause = null, EndedByQuestion = null }
            : target with
            {
                State = FlowTargetRecord.Failed,
                Exit = exit,
                Error = error,
                Cause = cause == OutcomeCause.None ? null : cause.ToString(),
                EndedByQuestion = endedByQuestion,
            });
        Release(instance);

        if (CurrentTarget(instance) is not null)
        {
            var was = instance.State;
            NewAttempt(instance);
            Record(FlowChangeKind.Step, instance, from: was, to: instance.State, attempt: instance.Attempt, detail: $"target '{target.Agent}' {(passed ? "done" : "failed")}");
            return;
        }

        EndTargets(instance);
    }

    /// <summary>
    /// Ends a step of <c>others</c> whose every target has ended: done, or failed with the code of the first, by the order
    /// of their names, that failed - every other target having had its turn all the same - and with what its code alone
    /// cannot say: the cause of a held lock, the question whose fail answer ended it.
    /// </summary>
    private void EndTargets(FlowInstance instance)
    {
        if (instance.Targets?.FirstOrDefault(target => target.State == FlowTargetRecord.Failed) is not { } failed)
        {
            End(instance, FlowInstanceState.Done, HarnessExit.Success, null);
            return;
        }

        instance.Cause = failed.Cause is { } cause ? FlowRecordNames.Of<OutcomeCause>(cause) : OutcomeCause.None;
        instance.EndedByQuestion = failed.EndedByQuestion;
        End(instance, FlowInstanceState.Failed, failed.Exit, $"target '{failed.Agent}': {failed.Error ?? $"it ended {failed.Exit?.ToString(System.Globalization.CultureInfo.InvariantCulture)}"}");
        AfterFailure(instance);
    }

    /// <summary><paramref name="instance"/>'s targets with <paramref name="target"/> in the place of the one of its agent.</summary>
    private static List<FlowTargetRecord> Replaced(FlowInstance instance, FlowTargetRecord target)
        => [.. instance.Targets!.Select(each => string.Equals(each.Agent, target.Agent, StringComparison.Ordinal) ? target : each)];

    /// <summary>Answers a <c>pause</c> step's question: the step ends done, its answer - and the text an option took - its
    /// output, which the steps after it read as any output.</summary>
    private void AnswerPause(FlowInstance instance, string answer, string? text)
    {
        // An answer given with no text holds an empty one: what reads it reads nothing, never a null.
        instance.Output = new JsonObject { ["answer"] = answer, ["text"] = text ?? string.Empty };
        End(instance, FlowInstanceState.Done, null, null);
    }

    /// <summary>
    /// Answers the question a failed step's <c>onFailure: pause</c> raised: <c>retry</c> runs the step again,
    /// <c>skip</c> ends it skipped and lets what depends on it go on. <c>abort</c> is every question's, and ends the run
    /// as a hard stop ends it (<see cref="ApplyAnswer"/>).
    /// </summary>
    private void AnswerFailure(FlowInstance instance, string answer)
    {
        switch (answer)
        {
            case "retry":
                Retry(instance);
                break;
            case "skip":
                instance.SkippedBecause = "skipped by the answer to its failure";
                End(instance, FlowInstanceState.Skipped, instance.Exit, instance.Error);
                break;
        }
    }

    /// <summary>
    /// <c>retry</c>, wherever it is answered: the step runs again as a new attempt. A step is admitted afresh, its
    /// <c>when</c> read again first - false skips it, unreadable fails it again - for an attempt never runs past a
    /// when nobody read. A group is opened again whole, as a new visit of it with fresh members, through the schedule
    /// that opened it - its dependencies and its when - and its failed visit stands, settled by the answer. A visit of
    /// the recovery is given the same failure again: the recovery's next visit, held to its bound.
    /// </summary>
    private void Retry(FlowInstance instance)
    {
        if (!instance.Step.IsGroup)
        {
            NewAttempt(instance);

            if (instance.Step.When is { } when && instance.DeclaredScope is { } declared)
            {
                var holds = Holds(when, declared, out var problem, reader: instance);

                if (problem is not null)
                {
                    End(instance, FlowInstanceState.Failed, HarnessExit.ConfigInvalid, Unreadable("when", instance.Id, problem));
                    HandleFailure(instance);
                }
                else if (!holds)
                {
                    instance.SkippedBecause = "its when read false";
                    End(instance, FlowInstanceState.Skipped, null, null);
                }
            }

            return;
        }

        // The visit that failed stands as it ended, its failure settled by the answer.
        instance.State = FlowInstanceState.Failed;
        instance.FailureSettled = true;

        if (IsRecovery(instance) && instance.RaisedBy is { } source)
        {
            TakeRecoveryOf(source);
            return;
        }

        if (instance.DeclaredScope is { } scope && ReferenceEquals(scope.Members.GetValueOrDefault(instance.Step.Name), instance))
        {
            // Drawn again by the schedule, as it was first: what depends on it waits for the new visit.
            _ = scope.Members.Remove(instance.Step.Name);
            _ = scope.Instantiated.Remove(instance.Step.Name);
        }
    }

    /// <summary>
    /// The ending a step reads as <c>source</c>: what raised the step itself, or - for a member of a handler that is a
    /// group, or of a visit of the recovery, however deep - what raised the nearest such group around it.
    /// </summary>
    private static FlowInstance? RaisedSourceIn(Scope scope, FlowInstance? reader = null)
        => reader?.RaisedBy ?? RaisedGroupOf(scope)?.RaisedBy;

    /// <summary>The nearest group around <paramref name="scope"/> an ending raised - a handler that is a group, a visit of the recovery - or null outside every one.</summary>
    private static FlowInstance? RaisedGroupOf(Scope scope)
    {
        for (var at = scope; at is not null; at = at.Parent)
        {
            if ((at.OwnGroupInstance ?? at.GroupInstance) is { RaisedBy: not null } raised)
            {
                return raised;
            }
        }

        return null;
    }

    /// <summary>Makes <paramref name="instance"/>'s next attempt of its visit, not yet started: what the attempt before it ended with is its own.</summary>
    /// <param name="instance">The step instance.</param>
    /// <param name="reopenAnswered">
    /// Whether a step of <c>others</c> run again gives a target a <c>fail</c> answer ended its turn again: a person's
    /// retry does; a resume leaves it standing, as it leaves a step one ended.
    /// </param>
    private static void NewAttempt(FlowInstance instance, bool reopenAnswered = true)
    {
        instance.Attempt++;

        // A step of others counts the attempts of the target whose turn it is as it counts its own: a retry is one more
        // for that target, and a target whose turn has not begun counts its first where it starts. One that ended and
        // runs again - a retry, a resume - gives each target that failed its turn again, whole, those done or passed over
        // keeping what they did: reopened here, so it is admitted on the first of them as any start is.
        if (CurrentTarget(instance) is { Attempts: > 0 } target)
        {
            instance.Targets = Replaced(instance, target with { Attempts = target.Attempts + 1 });
        }
        else if (instance.Targets is { } targets && CurrentTarget(instance) is null)
        {
            instance.Targets = [.. targets.Select(ended => ended.State == FlowTargetRecord.Failed && (reopenAnswered || ended.EndedByQuestion is null)
                ? new FlowTargetRecord(ended.Agent, FlowTargetRecord.NotStarted, 0)
                : ended)];
        }

        instance.State = FlowInstanceState.NotStarted;
        DisarmTimeLimit(instance);
        instance.Exit = null;
        instance.Error = null;
        instance.Cause = OutcomeCause.None;
        instance.EndedByQuestion = null;
        instance.Failure = null;
        instance.FinishedAt = null;

        // What the attempt before it said of its tree is its own: the next has let nothing go yet.
        instance.Copied = false;
    }

    /// <summary>
    /// Answers the question a repeat at its bound asks: <c>more</c> gives the loop one more iteration (and the same
    /// question is asked again if it comes to that), <c>accept</c> ends the repeat as it stands. <c>abort</c> is every
    /// question's, and ends the run as a hard stop ends it (<see cref="ApplyAnswer"/>).
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

                // A loop ends on what its last iteration left, with its leftover said where it names one.
                EndRepeat(instance);
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

    /// <summary>
    /// A run a failure's <c>stop</c> policy is ending starts nothing that is not an always step: what it had not started
    /// is NotRun, and what is in flight runs to its end. A hard stop makes nothing NotRun: what never started stays as it
    /// was, for the resume.
    /// </summary>
    private bool CarryTheStop()
    {
        if (!_stopping || _hardStop)
        {
            return false;
        }

        var stopped = _created.Where(instance => instance.State is FlowInstanceState.NotStarted && !instance.IsAlways).ToList();

        foreach (var instance in stopped)
        {
            NotRun(instance, FlowNotRunCause.RunEnded, "the run ended before it");
        }

        return stopped.Count > 0;
    }

    /// <summary>
    /// Considers afresh, at a session that goes on with no failure standing to stop it, what a failure's <c>stop</c> left
    /// NotRun: a step the run ended before, and one a failed dependency blocked where that dependency no longer blocks it
    /// - it runs again, or was itself drawn afresh. Each is drawn again by the schedule, as it would have been had the
    /// run not ended, under the same id: what it waits on, its <c>when</c>, its group. One its group's failure or a
    /// failure that still stands blocks stays NotRun, and so does a handler's place-holder whose source is not drawn
    /// again; one whose source is, goes with it, for the source's ending decides it.
    /// </summary>
    private void RedrawWhatTheStopLeft()
    {
        var drawn = true;

        while (drawn)
        {
            drawn = false;

            foreach (var notRun in _created.Where(instance => instance is { State: FlowInstanceState.NotRun, DeclaredScope: { } scope, Step.On: null }
                    && ScopeLive(scope)
                    && ReferenceEquals(scope.Members.GetValueOrDefault(instance.Step.Name), instance)
                    && (instance.NotRunCause is FlowNotRunCause.RunEnded
                        || instance.NotRunCause is FlowNotRunCause.DependencyFailed && !WaitsOf(instance.Step, scope, instance.IsAlways).Unreachable)).ToList())
            {
                Redraw(notRun);
                drawn = true;
            }
        }
    }

    /// <summary>
    /// Takes a NotRun place-holder back out of its scope, so the schedule draws its declaration again under the same id:
    /// it never ran, holds nothing and wrote nothing, and its record is written over by the instance drawn in its place.
    /// The place-holders it left its handlers go with it.
    /// </summary>
    private void Redraw(FlowInstance notRun)
    {
        var scope = notRun.DeclaredScope!;

        _ = scope.Members.Remove(notRun.Step.Name);
        _ = scope.Instantiated.Remove(notRun.Step.Name);
        scope.Visits[notRun.Step.Name] = notRun.Visit - 1;
        _ = _instances.Remove(notRun.Id);
        _ = _created.Remove(notRun);

        foreach (var follower in DeclarationsOf(scope)
            .Where(declaration => declaration.On?.Step == notRun.Step.Name)
            .Select(declaration => scope.Members.GetValueOrDefault(declaration.Name))
            .Where(follower => follower is { State: FlowInstanceState.NotRun, RaisedBy: null, RaisedByRecord: null })
            .ToList())
        {
            Redraw(follower!);
        }
    }

    /// <summary>The reason a step a failed dependency blocks for ever is NotRun, as its record says it.</summary>
    private const string DependencyFailedLine = "a step it depends on failed and was neither handled nor passed over";

    /// <summary>Ends <paramref name="instance"/> NotRun with its reason, said before the ending is followed: the handlers it leaves NotRun say the same reason.</summary>
    private void NotRun(FlowInstance instance, FlowNotRunCause cause, string because)
    {
        instance.NotRunCause = cause;
        instance.NotRunBecause = because;
        End(instance, FlowInstanceState.NotRun, null, null);
    }

    /// <summary>
    /// Instantiates the declarations of <paramref name="scope"/> whose dependencies have freed them: a leaf becomes a
    /// candidate, a group opens, a declaration a failed dependency blocks for ever is NotRun at once, and a false
    /// <c>when</c> ends skipped. An always step waits for the always phase, and a stopping run reaches nothing that is
    /// not one. A pause under way starts nothing new either - no ready step, no next iteration, no expansion - save,
    /// once the machine steps it left running have ended, the always steps that are machine steps; a reason step
    /// among them does not run until the next session. A hard stop draws nothing at all: what it never reached is left
    /// undrawn, and the resume draws it.
    /// </summary>
    private bool Instantiate(Scope scope)
    {
        var changed = false;

        if (_hardStop)
        {
            return false;
        }

        foreach (var declaration in DeclarationsOf(scope))
        {
            if (scope.Instantiated.Contains(declaration.Name) || !Draws(scope, declaration))
            {
                // A scope a redirect or a failure left draws nothing more: its group's visit is over - save the always
                // steps of a visit that ended done or failed, which its ending never waited on.
                continue;
            }

            if (IsAlways(declaration, scope) && !_alwaysPhase)
            {
                // The always steps wait for the rest to be quiescent, in any state, and run once.
                continue;
            }

            if (declaration.On is not null)
            {
                // A handler is never started by the ordinary schedule: what brings it into being is its source's
                // ending, and until one raises it its name is NotRun, satisfying nothing.
                continue;
            }

            if (declaration.Raised is not null)
            {
                // A step raised by each pre-existing issue a step reports is never scheduled either: what brings it
                // into being is the issue, one instance of it for each.
                continue;
            }

            if (_pausing && (!_alwaysPhase || !IsAlways(declaration, scope) || declaration.Type == "reason"))
            {
                // A pause under way draws nothing new - and once its machine steps have ended, only the always steps
                // that are machine steps; a reason step among them does not run until the next session.
                continue;
            }

            if (_stopping && !IsAlways(declaration, scope))
            {
                // A stopping run reaches nothing more. A failure it depends on blocks it for ever whatever ends the
                // run, and is then its reason; otherwise the run ended before it.
                scope.Instantiated.Add(declaration.Name);
                var blocked = WaitsOf(declaration, scope, always: false).Unreachable;
                CreateNotRun(
                    scope,
                    declaration,
                    blocked ? FlowNotRunCause.DependencyFailed : FlowNotRunCause.RunEnded,
                    blocked ? DependencyFailedLine : "the run ended before it");
                changed = true;
                continue;
            }

            if (Frozen(scope, declaration.Name))
            {
                // Nothing new is started among the steps a pending redirect will reset - not even drawn, for one
                // drawn now would read the ending the redirect is about to undo.
                continue;
            }

            var (waiting, unreachable) = WaitsOf(declaration, scope, IsAlways(declaration, scope));

            if (unreachable)
            {
                scope.Instantiated.Add(declaration.Name);
                CreateNotRun(scope, declaration, FlowNotRunCause.DependencyFailed, DependencyFailedLine);
                changed = true;
                continue;
            }

            if (waiting.Count > 0)
            {
                continue;
            }

            scope.Instantiated.Add(declaration.Name);

            if (scope.Visits.GetValueOrDefault(declaration.Name) >= 99)
            {
                // A step the run was sent back to a hundredth time in one scope fails, saying so.
                _ = NewVisit(scope, declaration, FlowInstanceState.NotStarted);
                changed = true;
                continue;
            }

            if (declaration.When is { } when)
            {
                // A handler's or a recovery's steps read the source that raised them, in a when as anywhere else: the
                // block's own instance carries it.
                var holds = Holds(when, scope, out var problem, drawing: scope.Visits.GetValueOrDefault(declaration.Name) + 1);

                if (problem is not null)
                {
                    var unread = Create(scope, declaration, FlowInstanceState.Failed, made: unread =>
                    {
                        unread.Exit = HarnessExit.ConfigInvalid;
                        unread.Error = Unreadable("when", unread.Id, problem);
                    });
                    HandleFailure(unread);
                    changed = true;
                    continue;
                }

                if (!holds)
                {
                    var skipped = Create(scope, declaration, FlowInstanceState.Skipped, made: skipped => skipped.SkippedBecause = "its when read false");
                    HandlersFollow(skipped, FlowInstanceState.Skipped);
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
                var off = Create(scope, declaration, FlowInstanceState.Skipped, made: off => off.SkippedBecause = "checkpoint pushes are off (pushCheckpoints)");
                HandlersFollow(off, FlowInstanceState.Skipped);
                changed = true;
                continue;
            }

            if (declaration.Type == "create-agent" && LiveAgentOf(scope, declaration) is { } live)
            {
                // A create-agent step visited again in the same scope, while the agent it made is live, is Done at once
                // with that agent and makes nothing: the agent's slot passes to the new visit.
                var made = Create(scope, declaration, FlowInstanceState.NotStarted, made: made => (made.Made, made.MadeModel) = (live.Made, live.MadeModel));
                _ = _agentsOpen.Remove(live.Id);
                End(made, FlowInstanceState.Done, HarnessExit.Success, null);
                changed = true;
                continue;
            }

            Create(scope, declaration, FlowInstanceState.NotStarted);
            changed = true;
        }

        return changed;
    }

    /// <summary>Whether <paramref name="scope"/> still draws its steps: the flow's top level always, and a group's scope while that visit of the group is running.</summary>
    private static bool ScopeLive(Scope scope)
        => scope.Parent is null || (scope.OwnGroupInstance ?? scope.GroupInstance) is { State: FlowInstanceState.Running or FlowInstanceState.Paused };

    /// <summary>
    /// Whether <paramref name="scope"/> still draws <paramref name="declaration"/>: a live scope draws every step, and
    /// one whose group's visit ended done or failed still draws the steps that run after that ending
    /// (<see cref="RunsAfterItsGroup"/>), in the always phase - the ending never waited on them, and they are owed it
    /// however it went.
    /// </summary>
    private static bool Draws(Scope scope, WorkflowStep declaration)
        => ScopeLive(scope)
            || RunsAfterItsGroup(declaration, scope) && (scope.OwnGroupInstance ?? scope.GroupInstance) is { State: FlowInstanceState.Done or FlowInstanceState.Failed };

    /// <summary>
    /// Whether <paramref name="declaration"/>, a step of a group's <paramref name="scope"/>, runs after that group's visit
    /// has ended: it is an always step and the group is not - it runs in the always phase, after the rest of the run, the
    /// group's ending among that rest. The one owner of what a group's ending does not wait on (<see cref="ScopeEnded"/>)
    /// and what the scope of a visit that ended still draws (<see cref="Draws"/>). The members of an always group - always
    /// by it, or by a group around it - run in the always phase as their group does, within its visit, and it waits on
    /// them as any group waits on its members: it ends once they have run, gathers what they left, and fails with them.
    /// </summary>
    private static bool RunsAfterItsGroup(WorkflowStep declaration, Scope scope)
        => scope is { Parent: { } around, Group: { } group } && IsAlways(declaration, scope) && !IsAlways(group, around);

    /// <summary>Whether a redirect the run owes keeps <paramref name="name"/> in <paramref name="scope"/> from starting - not even drawn - for it will reset it.</summary>
    private bool Frozen(Scope scope, string name)
        => _redirects.Any(redirect => redirect.Frozen.TryGetValue(scope.Id, out var held) && (held is null || held.Contains(name)));

    /// <summary>The visit of a create-agent declaration in <paramref name="scope"/> whose agent is live still, where one is.</summary>
    private FlowInstance? LiveAgentOf(Scope scope, WorkflowStep declaration)
        => _created.LastOrDefault(visit => ReferenceEquals(visit.DeclaredScope, scope)
            && visit.Step.Name == declaration.Name
            && visit.Made is not null
            && _agentsOpen.ContainsKey(visit.Id));

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
            // One of two bounds says how far the loop goes: maxIterations, or rounds with blocking past them. The
            // validator refuses a repeat with neither, and one that says both.
            _ = NextIteration(scope, declaration, instance);
            return;
        }

        instance.OwnScope = NewScope(instance.Id, PathOf(scope, declaration), scope, declaration, FlowValue.Missing, null, null, null);
        instance.OwnScope.OwnGroupInstance = instance;
    }

    /// <summary>The iterations a repeat's own bound allows: its maxIterations, or its rounds and the further ones its blocking holds for.</summary>
    private static int BoundOf(WorkflowStep declaration, FlowInstance instance)
    {
        var repeat = declaration.Repeat!;

        // blockersLimitRounds: false is no bound but the budget: no count of iterations ever reaches it.
        return repeat.Rounds is { } rounds
            ? repeat.BlockersUnbounded ? int.MaxValue : rounds + (repeat.BlockersLimitRounds ?? 3) + instance.ExtraIterations
            : (repeat.MaxIterations ?? 0) + instance.ExtraIterations;
    }

    /// <summary>Makes the repeat group's next iteration where its bound allows one, and asks at its bound where it does not.</summary>
    private bool NextIteration(Scope scope, WorkflowStep declaration, FlowInstance instance)
    {
        var iteration = instance.Children.Count + 1;
        var bound = BoundOf(declaration, instance);
        var wouldBring = _created.Count + 1 + SubtreeSize(PathOf(scope, declaration));

        // The loop's own bound is said first: at it the run asks, and only an iteration the loop may have is held to
        // the instance bound.
        if (iteration > bound)
        {
            var said = declaration.Repeat!.Rounds is { } rounds
                ? $"{rounds.ToString(System.Globalization.CultureInfo.InvariantCulture)} rounds and {(declaration.Repeat.BlockersUnbounded ? "no further bound but the budget" : (declaration.Repeat.BlockersLimitRounds ?? 3).ToString(System.Globalization.CultureInfo.InvariantCulture) + " more while blocking holds")} (rounds, blockersLimitRounds)"
                : $"at most {(declaration.Repeat?.MaxIterations ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)} are allowed (maxIterations)";
            _ = Ask(
                instance,
                "loopBound",
                $"'{instance.Id}' has run {iteration - 1} iterations and its until still reads false; {said}{(instance.ExtraIterations > 0 ? $", and {instance.ExtraIterations.ToString(System.Globalization.CultureInfo.InvariantCulture)} more were given by its answers" : string.Empty)}",
                WorkflowValidator.AnswersOf["loopBound"]);
            return false;
        }

        if (wouldBring > Limit().MaxInstances)
        {
            FailGroup(instance, HarnessExit.Refused, InstanceBound(instance.Id, wouldBring, Limit().MaxInstances), instance.Path);
            return true;
        }

        var body = NewScope($"{instance.Id}#{iteration}", PathOf(scope, declaration), scope, declaration, FlowValue.Missing, null, iteration, instance);
        instance.Children.Add(body);
        _ = Create(body, declaration, FlowInstanceState.Running, body.Id);
        return true;
    }

    /// <summary>
    /// Ends a repeat that ends with its until still false: what its leftover expression says it left is kept in its
    /// record. A leftover that cannot be read fails the repeat as an unreadable until does, for what a loop left is
    /// never dropped unsaid.
    /// </summary>
    private void EndRepeat(FlowInstance instance)
    {
        if (instance.Step.Repeat?.Leftover is { } leftover && instance.Children.Count > 0)
        {
            var left = Evaluate(leftover, instance.Children[^1], out var problem);

            if (problem is not null)
            {
                FailGroup(instance, HarnessExit.ConfigInvalid, Unreadable("leftover", instance.Children[^1].GroupInstance?.Id ?? instance.Id, problem), instance.Path);
                return;
            }

            instance.Leftover = left.ToNode();
        }

        End(instance, FlowInstanceState.Done, null, null);
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

        var until = Holds(instance.Step.Repeat!.Until, instance.Children[^1], out var problem);

        if (problem is not null)
        {
            FailGroup(instance, HarnessExit.ConfigInvalid, Unreadable("until", instance.Children[^1].GroupInstance?.Id ?? instance.Id, problem), instance.Path);
            return true;
        }

        if (until)
        {
            End(instance, FlowInstanceState.Done, null, null);
            return true;
        }

        if (instance.Step.Repeat.Rounds is { } rounds && instance.Children.Count >= rounds)
        {
            // Past the rounds another iteration is created only while blocking holds: whether what is left has to be
            // dealt with before the loop may end, worked out by the engine from what the body's steps answered.
            var blocking = Holds(instance.Step.Repeat.Blocking!, instance.Children[^1], out var unreadable);

            if (unreadable is not null)
            {
                FailGroup(instance, HarnessExit.ConfigInvalid, Unreadable("blocking", instance.Children[^1].GroupInstance?.Id ?? instance.Id, unreadable), instance.Path);
                return true;
            }

            if (!blocking)
            {
                // Where it is false the repeat ends as it stands, with no question, and what it left is said.
                EndRepeat(instance);
                return true;
            }
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

            if (_pausing && scopes.Any(scope => scope.Members.Values.Any(member => member.Failure == FlowFailure.Owed)))
            {
                // A member the pause left running ended failed and its policy is owed to the resume: the group does
                // not end over it now, for what its failure owes has yet to decide the group.
                continue;
            }

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

        while (_redirects.Count > 0 && _redirects[0] is { Asking: null } next && !_pausing && !_hardStop && RedirectQuiet(next))
        {
            // The redirects the run owes are made in the order they were decided, each once nothing it resets is
            // running: a redirect cancels nothing, and what it resets that was in flight is recorded as the visit it
            // was. One still asking about an open agent holds the ones after it. A pause under way leaves them owed,
            // and so does a hard stop - which makes nothing owed, and stops what the redirect waited on - for the
            // resume makes what the run owes, from its records alone, once what was stopped stands to run again.
            ApplyRedirect(next);
            changed = true;
        }

        return changed;
    }

    /// <summary>What admission decides of one step that has not started: it starts now, with the claims it takes, or what it waits on.</summary>
    /// <param name="Instance">The step.</param>
    /// <param name="Claims">The claims it takes where it starts.</param>
    /// <param name="WaitsOn">What it waits on, in the words its record says them; empty where it starts.</param>
    private readonly record struct Admission(FlowInstance Instance, IReadOnlyList<FlowClaim> Claims, IReadOnlyList<string> WaitsOn)
    {
        /// <summary>Whether the step starts now.</summary>
        public bool Starts => WaitsOn.Count == 0;
    }

    /// <summary>
    /// The one walk admission makes, deciding nothing it does not say: every step that has not started, in the fixed
    /// order - declaration order, then instance id - held to what keeps the run from starting it (a hard stop, a pause
    /// under way, a stopping run, a pending redirect), to its dependencies, to the <c>alone</c> barrier, to the claims
    /// and to the limits, all-or-nothing, each step the walk admits counted as running for the ones after it. A step
    /// blocked on a claim reserves every claim it waits for, so no later step takes what it waits for first and a fold
    /// waiting behind readers is not starved; one blocked on a limit, a hold or a pause reserves nothing. Admission
    /// starts what it admits, the run's records list it as what starts next, and each step's record says what it waits
    /// on: all three read this walk.
    /// </summary>
    private List<Admission> AdmissionWalk()
    {
        var waiting = _created
            .Where(instance => instance.State is FlowInstanceState.NotStarted && !instance.Step.IsGroup)
            .OrderBy(instance => _declarationOrder.GetValueOrDefault(instance.Path, int.MaxValue))
            .ThenBy(instance => instance.Id, StringComparer.Ordinal)
            .ToList();

        // What runs is a leaf: a group stands open while its members work, and is no step that runs.
        var running = _created.Where(instance => instance.State is FlowInstanceState.Running && !instance.Step.IsGroup).ToList();

        // What runs holds its claims, and so does a runner that outlived its attempt until it answers.
        var held = running.SelectMany(instance => instance.HeldClaims).Concat(_created.SelectMany(instance => instance.Lingering.SelectMany(lingering => lingering.Claims))).ToList();
        var reserved = new List<FlowClaim>();
        var decisions = new List<Admission>();

        // The quiescence barrier: an alone step found waiting for quiet stays the barrier until it starts, and while it
        // waits nothing new is admitted - steps ready beside it in the same walk and declared before it went first, as
        // the fixed order says - so it starts when nothing runs, and nothing starts while it runs.
        var barrier = _barrier is { State: FlowInstanceState.NotStarted } waitingAlone && HoldOf(waitingAlone).Count == 0 ? waitingAlone : null;

        foreach (var instance in waiting)
        {
            var hold = HoldOf(instance);

            if (hold.Count > 0)
            {
                decisions.Add(new(instance, [], hold));
                continue;
            }

            if (running.Any(other => other.Step.Alone == true))
            {
                decisions.Add(new(instance, [], ["quiet"]));
                continue;
            }

            if (barrier is null && instance.Step.Alone == true && running.Count > 0)
            {
                barrier = instance;
            }

            if (barrier is not null && (!ReferenceEquals(instance, barrier) || running.Count > 0))
            {
                decisions.Add(new(instance, [], ["quiet"]));
                continue;
            }

            // A step that names an agent claims that agent's own tree, as its instance's create-agent made it - one that
            // goes on with another's work, that step's; a step of others claims its target's alone, each target admitted
            // on its own.
            var claims = FlowClaims.Of(ClaimingStepOf(instance), _runnerFacts, instance.Step.Others is null ? WorksInOf(instance) : TargetOf(instance));

            if (claims.Any(claim => held.Concat(reserved).Any(claim.Conflicts)))
            {
                reserved.AddRange(claims);
                decisions.Add(new(instance, [], ["claim"]));
                continue;
            }

            if (CounterFull(instance, running) is { } counter)
            {
                decisions.Add(new(instance, [], [counter]));
                continue;
            }

            running.Add(instance);
            held.AddRange(claims);
            decisions.Add(new(instance, claims, []));
        }

        return decisions;
    }

    /// <summary>
    /// What keeps <paramref name="instance"/> from starting before any claim or limit is asked: a hard stop or a pause
    /// under way (it runs at the resume; in a pause's always phase only an always step that is a machine step starts),
    /// an always step before the session's always phase (one carried into this session as well as one drawn in it),
    /// a stopping run (only always steps start), a pending redirect that will reset it, or a dependency it still waits
    /// on. Empty where nothing does.
    /// </summary>
    private IReadOnlyList<string> HoldOf(FlowInstance instance)
    {
        if (_hardStop || _pausing && (!_alwaysPhase || !instance.IsAlways || instance.Step.Type == "reason"))
        {
            return ["resume"];
        }

        if (instance.IsAlways && !_alwaysPhase)
        {
            // The always steps run once the rest is quiescent: one a pause stopped in the last session's always phase
            // waits for this one's, as the ones this session draws do.
            return ["always"];
        }

        if (_stopping && !instance.IsAlways)
        {
            return ["stop"];
        }

        if (instance.DeclaredScope is not { } scope)
        {
            return [];
        }

        if (Frozen(scope, instance.Step.Name))
        {
            return ["redirect"];
        }

        return WaitsOf(instance.Step, scope, instance.IsAlways).Waiting;
    }

    /// <summary>Admits every step the walk admits, in its order, and answers how many it started.</summary>
    private int Admit()
    {
        var started = 0;
        var walk = AdmissionWalk();

        // An alone step the walk left waiting for quiet is the barrier from now until it starts.
        _barrier = walk.FirstOrDefault(decision => decision.Instance.Step.Alone == true && decision.WaitsOn is ["quiet"]).Instance;

        foreach (var decision in walk.Where(decision => decision.Starts))
        {
            if (_answeredAtOnce || _hardStop || decision.Instance.State is not FlowInstanceState.NotStarted)
            {
                // A start that changed the graph - a pause step that asked, answered at once or unfillable - ends this
                // walk: the loop draws what follows from it and walks again.
                break;
            }

            if (Start(decision.Instance, decision.Claims))
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
        if (instance.Step.Others is not null && !StartTarget(instance))
        {
            // Every target has ended: the step ends with them, and the graph changed where the loop was admitting.
            _answeredAtOnce = true;
            return false;
        }

        if (AgentNeverMade(instance) is { } never)
        {
            // What it depends on let it start, and the agent it works in was never made: it can never run, and is NotRun
            // - the dependency semantics are the engine's, and no runner is asked to run in an agent that is not there.
            NotRun(instance, FlowNotRunCause.AgentNotMade, never);
            _answeredAtOnce = true;
            return false;
        }

        // The agent it works in is the one made by now, and stays its own whatever a later visit of its create-agent makes.
        instance.WorksIn ??= NamedAgentOf(instance);

        var was = instance.State;

        // The handoff goes to the attempt and stays the step's until the runner says the prompt that holds it is
        // written (HandoffTaken), or the attempt ends done: it is always in the file or in the prompt, never neither.
        var handoff = instance.Handoff;

        instance.State = FlowInstanceState.Running;
        instance.StartedAt ??= _clock.GetUtcNow();
        // What an attempt says of its legs is its own: the attempt before it placed its legs where it did.
        instance.HeldClaims.Clear();
        instance.HeldClaims.AddRange(claims);
        instance.LegsInCopies.Clear();
        instance.LegsInTree.Clear();
        instance.CopiesMade.Clear();
        instance.LegsEnded.Clear();
        instance.EveryLegPlaced = false;
        instance.Copied = false;
        instance.Draining = false;
        instance.PastTimeLimit = false;

        // The agent a create-agent step makes is named, with the model it is made for, before the record that says the
        // step runs - written before its runner is asked - so a step run again, after a crash or a stop, asks for the
        // same agent whatever the configuration says by then. A name once given is never given again; a model that did
        // not resolve is looked for again, since nothing was made without one.
        if (instance.Step.Type == "create-agent")
        {
            instance.Made ??= NameAgent();
            instance.MadeModel ??= _agentModels.GetValueOrDefault(instance.Path);
        }

        // A reason step that makes an agent of its own - one that reads and begins work of its own, neither working in
        // another's worktree nor going on with another's work (a step that resumes itself begins its own again) - is
        // given the engine's own read-only one, named the same way a create-agent's is: before the record, with the
        // model the step resolves to. Its records live in that agent's directory, as every reason step's do. A step
        // that resumes itself keeps one agent over all its visits - a repeat's iterations of one share the first's -
        // and every other step's each visit makes its own.
        if (instance.Step.Type == "reason" && WorkflowStepTypes.MakesAgent(instance.Step) && WorksInOf(instance) is null)
        {
            instance.Made ??= (instance.Step.Resume is { } self && string.Equals(self, instance.Step.Name, StringComparison.Ordinal)
                ? _created.FirstOrDefault(earlier => !ReferenceEquals(earlier, instance)
                    && string.Equals(earlier.Path, instance.Path, StringComparison.Ordinal)
                    && earlier.Made is not null)?.Made
                : null)
                ?? NameAgent();
            instance.MadeModel ??= _agentModels.GetValueOrDefault(instance.Path);
            instance.WorksIn ??= instance.Made;
        }

        Record(FlowChangeKind.Step, instance, from: was, to: FlowInstanceState.Running, attempt: instance.Attempt);

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

        ArmTimeLimit(instance);
        instance.Runner = AskRunner();

        return true;

        async Task AskRunner()
        {
            // What this attempt is, taken where it begins: a message of an attempt that is over - the step was
            // stopped and runs again, or was answered - says nothing about the attempt the run is on now.
            var attempt = instance.Attempt;

            try
            {
                instance.Stoppable = new CancellationTokenSource();
                var token = instance.Stoppable.Token;

                if (instance.Step.Type == "reason")
                {
                    _reasonStarted[instance.Id] = _clock.GetUtcNow();
                }

                // What the step reads where it starts, as when is read where it is admitted (ReadAtStart): a test
                // step's filterWhen - false gives the runner no filter and fills no template, so every test of the
                // step's legs runs - a validate-plan's items and scope, the pull request's base orient says, an anchor
                // check's rows or paths, a fold family step's lists of names - a fold's and a deletion's within,
                // allow.new and allow.merge, a refresh's paths, except, stale and merge, a rebase's merge - the commit
                // the run last pushed for a ci-wait that names none, and a commit's folds upstream of it. One that cannot
                // be read - a filterWhen that
                // is neither true nor false among them - fails the step 12, as an unreadable when does.
                var step = instance.Step;
                var read = new Dictionary<string, FlowValue>(StringComparer.Ordinal);

                if (ReadAtStart(instance, read) is { } unreadable)
                {
                    _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, unreadable), null, Attempt: attempt));
                    return;
                }

                if (read.Remove("filterWhen", out var filterWhen) && !filterWhen.AsBoolean(step.FilterWhen!))
                {
                    step = step with { Filter = null };
                }

                // Every text the step hands on, filled where it starts by the names the step reads: the runner is given
                // the step as it is to run. One that reads a step with no output fails the step, as an expression does.
                if (FlowStepTemplates.Fill(step, (string text, out string? problem) => Filled(instance, text, out problem), out var key, out var broken) is not { } filled)
                {
                    _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, Unreadable(key!, instance.Id, broken!)), null, Attempt: attempt));
                    return;
                }

                step = filled;

                // A reason step's prompt is its runner's to fill - the skill blocks a step starts with are put in there
                // - so the engine reads the files and fills every other name here, handing the texts as read. One that
                // cannot be filled fails the step, as a template key's that breaks. A run held in memory alone reads
                // no files: its runner is handed none, and refuses where it needs one.
                if (step.Type == "reason" && _workflowFile is { } readFile)
                {
                    foreach (var (promptKey, promptFileNamed) in new[] { ("prompt", step.Prompt), ("resumePrompt", step.ResumePrompt) })
                    {
                        if (promptFileNamed is null)
                        {
                            continue;
                        }

                        if (Filled(instance, readFile(promptFileNamed), out var promptBroken) is not { } promptText)
                        {
                            _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, HarnessExit.ConfigInvalid, Unreadable(promptKey, instance.Id, promptBroken!)), null, Attempt: attempt));
                            return;
                        }

                        read[promptKey] = FlowValue.Of(promptText);
                    }
                }

                var result = await _runner.RunAsync(RunOf(instance, step, attempt, read, handoff?.Text), token).ConfigureAwait(false);

                _ = _messages.Writer.TryWrite(new Message(instance, result, null, Attempt: attempt));
            }
            catch (HarnessException refused)
            {
                // A runner that refuses says so with its own code - a step's config, a lock held, a command that
                // failed - and the step ends with it, as every ending does.
                _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, refused.ExitCode, refused.Message), null, Attempt: attempt));
            }
            catch (Exception ex)
            {
                // Anything else is the engine's own fault, and ends the step 70 with what it was.
                _ = _messages.Writer.TryWrite(new Message(instance, FlowStepResult.Ended(passed: false, HarnessExit.InternalError, ex.Message), null, Attempt: attempt));
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

        if (instance.Draining)
        {
            // A stop cancelled this attempt and waited for its runner: it has ended now, and whatever it answered the
            // step is stopped, to run again at the resume.
            End(instance, FlowInstanceState.Stopped, null, null);
            return;
        }

        if (instance.PastTimeLimit)
        {
            // The attempt passed its time limit and was stopped for it: whatever its runner answered once stopped, the
            // step ends incomplete, and runs again as an incomplete step does.
            result = FlowStepResult.Ended(passed: false, HarnessExit.Incomplete, PastTimeLimitError(instance));
        }

        // The budget's minutes are the time reason steps ran, each attempt and each handoff turn counted.
        CountMinutes(instance);

        // What the attempt's agent assumed, asked or not: kept whatever else the attempt ends by, for a later step, a
        // report and the person read what an unattended run took for granted.
        if (result.Assumptions is [_, ..] assumed)
        {
            instance.Assumptions = [.. instance.Assumptions, .. assumed];
        }

        if (result.Asked is { } asking)
        {
            // A step that asks holds nothing while it waits: it gives back every claim and every counter, and is
            // admitted again, against the limits and the claims as they then are, when it is answered.
            Release(instance);
            _ = Ask(instance, asking.Kind, asking.Problem, asking.Options, agentQuestions: asking.AgentQuestions, agent: WorksInOf(instance) ?? instance.Made);
            return;
        }

        if (result.Pauses is { } pausing)
        {
            // Every account a reason step could use is at its limit, or its provider's service failed past the client's
            // own tries: the run pauses safely and resumes that very step, never a failure and never a question, for
            // every model the step could move to would meet the same. The step is stopped where it stands, as a pause
            // stops it, with why as its handoff - the resume's next attempt is told what the attempt met - and runs
            // again at the resume.
            Release(instance);
            instance.Handoff ??= FlowHandoff.ByEngine(pausing);
            Record(FlowChangeKind.Handoff, instance, detail: instance.Handoff.By);
            End(instance, FlowInstanceState.Stopped, HarnessExit.Incomplete, pausing);
            Record(FlowChangeKind.Step, instance, detail: "the run pauses for it");
            RequestPause("limits");
            return;
        }

        if (result.DryRun is { } dry)
        {
            // A fold family step's dry run stood short of applying: it raises the question the one rule names, with what
            // it shows - a consent it lacks, one at a time, a person's first; paths its within leaves out; conflicting
            // merges alone - and any other refusal fails the step, refused.
            Release(instance);

            if (FlowQuestions.FoldQuestionOf(dry) is { } question)
            {
                _ = Ask(instance, question.Kind, FlowQuestions.FoldProblemOf(instance.Id, dry, question), FlowQuestions.OptionsOf(question.Kind, instance.Step));
            }
            else if (instance.Step.Others is not null)
            {
                EndTarget(instance, passed: false, HarnessExit.Refused, dry.Summary);
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
            // onFailure reads, for the step has not failed. The engine writes the attempt's handoff before the question
            // is raised - one line saying why, for the agent's run had ended and cannot answer - so the answer `retry`
            // begins the next attempt with it, as a pause's next attempt begins with the agent's. `more` is offered
            // only where a stored conversation of the attempt can be handed, for more of the same conversation could
            // not help anywhere else.
            Release(instance);
            var kind = FlowQuestions.LimitKindOf(limit);
            var options = FlowQuestions.OptionsOf(kind, instance.Step);

            if (!result.StoredConversation)
            {
                options = [.. options.Where(option => option != "more")];
            }

            instance.Handoff ??= FlowHandoff.ByEngine($"'{instance.Id}' reached its {limit}");
            Record(FlowChangeKind.Handoff, instance, detail: instance.Handoff.By);
            _ = Ask(instance, kind, $"'{instance.Id}' reached its {limit}{(result.LimitDetail is { } detail ? $" ({detail})" : string.Empty)}", options);
            return;
        }

        if (result.Checks is { } found)
        {
            Release(instance);
            var kind = FlowQuestions.ChecksKindOf(found);
            _ = Ask(instance, kind, $"'{instance.Id}' found what it cannot end on: {result.ChecksDetail ?? found}", FlowQuestions.OptionsOf(kind, instance.Step));
            return;
        }

        var (passed, exit, error, cause) = Ends(instance.Step, result);

        // A step that did not pass never ends with the code of success, which every record of it - a target's among them -
        // reads as done: a runner that answers so is the engine's own fault, and the step ends 70 saying what it answered.
        if (!passed && exit == HarnessExit.Success)
        {
            (exit, error, cause) = (HarnessExit.InternalError, $"'{instance.Id}' answered that it did not pass, with {HarnessExit.Success}, the code of success{(error is null ? string.Empty : $": {error}")}", OutcomeCause.None);
        }

        instance.Legs = result.Legs;
        instance.RedLegs = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Red);
        instance.Output = result.Output;

        // A triage instance's answer is read before it ends: one that says no course the engine takes, or an issue to
        // list that does not read back, fails the step - a record is never guessed at, and nothing vanishes unsaid.
        if (passed && instance.Step.Raised == WorkflowStep.RaisedByPreExisting && TriageRefusal(instance) is { } refused)
        {
            (passed, exit, error) = (false, HarnessExit.ConfigInvalid, refused);
        }

        // The agent a create-agent step made is the one the engine named for it - the one every step that names the step
        // with in or of works in, and whose directory those steps keep theirs in: an output that names another is the
        // engine's own fault. A name the step's runner says is spent is given again to none, and the step's next attempt
        // is named anew.
        if (instance.Step.Type == "create-agent")
        {
            if (passed && TextOf(result.Output?["agent"]) is { } named && !string.Equals(named, instance.Made, StringComparison.Ordinal))
            {
                (passed, exit, error) = (false, HarnessExit.InternalError, $"'{instance.Id}' answered that it made agent '{named}', and the engine named its agent '{instance.Made}'");
            }

            if (!passed && result.NameSpent)
            {
                (instance.Made, instance.MadeModel) = (null, null);
            }
        }

        // An incomplete step is run again by the table's row for its code, capped by its retries: a reasoning step is
        // never retried at step level, for its client owns its transient errors. A pause under way starts no retry:
        // one that ends incomplete with retries left is stopped where it stands and runs again at the resume.
        // A step of others counts the attempts of the target whose turn it is: each target is retried on its own.
        var attempts = instance.Step.Others is not null && CurrentTarget(instance) is { } running ? running.Attempts : instance.Attempt;

        if (!passed
            && instance.Step.Type != "reason"
            && FlowRetry.Wait(exit, cause, attempts - 1) is { } wait
            && attempts <= RetryCap(instance.Step))
        {
            if (_pausing)
            {
                Release(instance);
                End(instance, FlowInstanceState.Stopped, exit, error);
                return;
            }

            // Its attempt is over, its claims go back, and its next attempt waits as the table says - at once, where
            // the table says no wait.
            Release(instance);
            _wakes.Add(new Wake(instance, _clock.GetUtcNow() + wait, wait == TimeSpan.Zero ? Task.CompletedTask : Task.Delay(wait, _clock)));
            return;
        }

        // An ending the table would run again that ends here - its retries spent - says how often it ran: the error is
        // worded once the retry was decided, and never says the step runs again where it does not.
        if (!passed && instance.Step.Type != "reason" && attempts > 1 && FlowRetry.RunsAgain(exit, cause))
        {
            error = $"{error ?? $"it ended {exit.ToString(System.Globalization.CultureInfo.InvariantCulture)}"}, and still so after {attempts.ToString(System.Globalization.CultureInfo.InvariantCulture)} attempts";
        }

        if (instance.Step.Others is not null)
        {
            // An ending no retry runs again ends the target whose turn it was; the step goes on with the next.
            instance.Output = null;
            EndTarget(instance, passed, exit, error, cause);
            return;
        }

        Release(instance);

        if (result.PreExisting.Count > 0 && instance.Step.OnPreExisting is not null)
        {
            // The issues it reported are owed their triage in its own record from its ending's first write, so no crash
            // before their triage is raised loses them.
            instance.IssuesOwed = result.PreExisting;
        }

        if (passed && instance.Step.Raised == WorkflowStep.RaisedByPreExisting)
        {
            // A triage instance ends: its answer says what becomes of the issue it was raised for, in the run's list
            // and tally, and its end is recorded after - one write that carries both, in the one order.
            TriageOf(instance);
        }

        // What the code alone cannot say of a failure - a refusal over a held lock - is the ending's own, in its record:
        // the run that ends on it counts it as incomplete, from the records alone.
        instance.Cause = passed ? OutcomeCause.None : cause;
        End(instance, passed ? FlowInstanceState.Done : FlowInstanceState.Failed, exit, error);

        if (!passed)
        {
            AfterFailure(instance);
        }

        if (instance.Step.Raised == WorkflowStep.RaisedByPreExisting && instance.State == FlowInstanceState.Done && CourseOf(instance) == "fix")
        {
            // A triage that answered fix gives its issue to the recovery, its end recorded.
            FixOf(instance);
        }

        if (instance.IssuesOwed.Count > 0)
        {
            if (_pausing || _stopping || _hardStop)
            {
                // Nothing is started while a pause records what it left or a stop ends the run: the issues stay owed
                // in the step's own record, and the next session raises their triage.
                Record(FlowChangeKind.Step, instance, detail: $"{instance.IssuesOwed.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} pre-existing issue(s) reported, their triage owed to the resume");
            }
            else
            {
                // Each issue a reason step's answer reports raises one instance of the step its onPreExisting names,
                // which triages it: one visit of it for each issue, in the order the answer held them.
                RaiseOwedIssues(instance);
            }
        }
    }

    /// <summary>What a failure that has just ended a step goes on to: what handles it, now or, under a pause, in the next session.</summary>
    private void AfterFailure(FlowInstance instance)
    {
        if (_pausing)
        {
            // A machine step the pause left running ended failed: it is recorded as it ended, its failure owed, and
            // what its ending owes - its policy, the handler it would raise - waits for the next session, for
            // nothing is started and no question is asked while the run records what it left running.
            _ = OweHandlers(instance);
            instance.FailureSettled = false;
        }
        else
        {
            HandleFailure(instance);
        }
    }

    /// <summary>
    /// Arms the time limit of the run of <paramref name="instance"/> that starts now, where its step says one: a timer of
    /// the engine's clock that tells the loop once it passes. The message names the timer it came from, so a run that
    /// starts again in the same attempt - after an answer - is never ended by the timer of the run before it.
    /// </summary>
    private void ArmTimeLimit(FlowInstance instance)
    {
        DisarmTimeLimit(instance);

        if (WorkflowStepTypes.TimeLimitOf(instance.Step) is not { } minutes)
        {
            return;
        }

        var limit = instance.TimeLimit = new CancellationTokenSource();
        var attempt = instance.Attempt;
        _ = PassAsync(limit.Token);

        async Task PassAsync(CancellationToken disarmed)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(minutes), _clock, disarmed).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            _ = _messages.Writer.TryWrite(new Message(instance, null, null, Attempt: attempt, TimeLimit: limit));
        }
    }

    /// <summary>Ends the timer of <paramref name="instance"/>'s time limit, where one runs: its run has answered, stopped or ended.</summary>
    private static void DisarmTimeLimit(FlowInstance instance)
    {
        if (instance.TimeLimit is { } limit)
        {
            instance.TimeLimit = null;
            limit.Cancel();
            limit.Dispose();
        }
    }

    /// <summary>
    /// The run of <paramref name="instance"/> in flight passed its step's time limit: it is stopped as a stop stops one -
    /// its work cancelled, a reason step's minutes counted - and once its runner has answered it ends incomplete,
    /// whatever it answered (<see cref="Complete"/>). A runner that does not end within
    /// <see cref="PointOfNoReturn.SafePauseDrain"/> fails the step all the same, saying so, and the step is not retried.
    /// A runner that has answered already is left to its answer, which is on its way to the loop.
    /// </summary>
    private void PassTimeLimit(FlowInstance instance)
    {
        DisarmTimeLimit(instance);

        if (instance.State is not FlowInstanceState.Running || instance.Draining || instance.Runner is not { IsCompleted: false })
        {
            return;
        }

        instance.PastTimeLimit = true;
        Record(FlowChangeKind.Step, instance, from: instance.State, to: instance.State, attempt: instance.Attempt, detail: $"{PastTimeLimitError(instance)}: stopped");
        _ = StopWork(instance);
    }

    /// <summary>What a step that passed its time limit says of its ending.</summary>
    private static string PastTimeLimitError(FlowInstance instance)
        => $"passed its timeoutMinutes ({WorkflowStepTypes.TimeLimitOf(instance.Step)?.ToString(System.Globalization.CultureInfo.InvariantCulture)}{(instance.Step.TimeoutMinutes is null ? ", its type's" : string.Empty)})";

    /// <summary>
    /// Raises the triage of the issues <paramref name="reporter"/>'s record holds owed, and then writes them raised: a
    /// crash between the two leaves them owed, and the next session raises only those no triage was raised for yet.
    /// </summary>
    private void RaiseOwedIssues(FlowInstance reporter)
    {
        RaiseIssues(reporter, reporter.Step.OnPreExisting!, reporter.IssuesOwed);
        reporter.IssuesOwed = [];
        WriteStepRecordOf(reporter);
    }

    /// <summary>Raises one instance of the triage step for each issue a reporting step's answer reported, with the reporting step as its source - once for each.</summary>
    private void RaiseIssues(FlowInstance reported, string triage, IReadOnlyList<FlowPreExistingReport> issues)
    {
        var declaration = _definition.Steps.FirstOrDefault(step => step.Name == triage);

        if (declaration is null)
        {
            return;
        }

        // A triage already raised for an issue - a crash came between raising it and the reporter's record - is that
        // issue's, each claimed once, so an issue reported twice is still triaged twice.
        var raisedAlready = _created.Where(raised => ReferenceEquals(raised.RaisedBy, reported) && raised.Step.Name == triage).ToList();

        foreach (var issue in issues)
        {
            if (raisedAlready.FirstOrDefault(raised => raised.Issue == issue) is { } claimed)
            {
                _ = raisedAlready.Remove(claimed);
                continue;
            }

            _ = NewVisit(_top, declaration, FlowInstanceState.NotStarted, made: raised =>
            {
                raised.RaisedBy = reported;
                raised.Issue = issue;
            });
        }
    }

    /// <summary>The courses a triage answers, and the engine takes: <c>fix</c>, <c>list</c>, <c>duplicate</c>, <c>unproven</c>.</summary>
    private static readonly string[] TriageCourses = ["fix", "list", "duplicate", "unproven"];

    /// <summary>
    /// Why a triage instance's answer cannot be acted on, read before the step ends: a course that is none the engine
    /// takes, or an issue to list that does not read back; null where it can be.
    /// </summary>
    private string? TriageRefusal(FlowInstance instance)
    {
        var course = CourseOf(instance);

        if (course is null || !TriageCourses.Contains(course, StringComparer.Ordinal))
        {
            return course is null
                ? $"its answer says no course: it is one of {string.Join(", ", TriageCourses)}"
                : $"its answer's course '{course}' is none of {string.Join(", ", TriageCourses)}";
        }

        return course == "list" && IssueOf(instance).Problem() is { } problem
            ? $"its answer's issue does not read back: {problem}"
            : null;
    }

    /// <summary>The course a triage instance answered; null where its answer says none as text.</summary>
    private static string? CourseOf(FlowInstance triage)
        => TextOf(triage.Output?["course"]);

    /// <summary>The id of the listed issue a triage answered <c>duplicate</c> of, as its answer's <c>duplicates</c> names it; null where it names none.</summary>
    private static string? DuplicateOf(FlowInstance triage)
        => TextOf(triage.Output?["duplicates"]) is { Length: > 0 } of ? of : null;

    /// <summary>What a triage that answered <c>duplicate</c> or <c>unproven</c> leaves in the run's tally: the course, with the issue a duplicate repeats.</summary>
    private static PreExistingTally SettledTally(FlowInstance triage, string course)
        => new(course == "duplicate" && DuplicateOf(triage) is { } of ? $"duplicate of {of}" : course, TitleOf(triage));

    /// <summary>A value of a step's answer as text; null where it is none, or not text.</summary>
    private static string? TextOf(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    /// <summary>
    /// What a triage instance's answer makes of its issue in the run's list and tally, before its end is recorded - so
    /// the end's one write carries them, in the one order: list adds it to the run's file, fix counts it being fixed,
    /// duplicate and unproven are counted and not listed. What fix gives the recovery waits for the end (<see cref="FixOf"/>).
    /// </summary>
    private void TriageOf(FlowInstance instance)
    {
        var course = CourseOf(instance);

        switch (course)
        {
            case "fix":
                _tally.Add(new PreExistingTally("being fixed", $"{TitleOf(instance)} ({WhereOf(instance)})", instance));
                break;
            case "list":
                ListIssue(instance);
                break;
            case "duplicate":
                _tally.Add(SettledTally(instance, "duplicate"));
                break;
            case "unproven":
                // A claim no one could prove is never shown to a person as a fact: the triage instance's own record
                // keeps why, and nothing is listed.
                _tally.Add(SettledTally(instance, "unproven"));
                break;
            default:
                // Read before the step ended (TriageRefusal): an answer with no course the engine takes failed it.
                throw new InvalidOperationException($"'{instance.Id}' ends done with the course '{course}', which its ending refuses.");
        }
    }

    /// <summary>
    /// Gives the issue a triage answered fix for to the recovery, once the triage's end is recorded: the recovery fixes
    /// it at its root as it fixes a failure; where it blocks the reporting step, that step runs again from where it
    /// stopped once the fix is done. A fix asked while the recovery is already at work waits for that visit's end: the
    /// recovery never calls itself (V50).
    /// </summary>
    private void FixOf(FlowInstance instance)
    {
        if (_created.Any(atWork => IsRecovery(atWork) && atWork.State is FlowInstanceState.Running or FlowInstanceState.Paused or FlowInstanceState.NotStarted))
        {
            _pendingFixes.Add(instance);
            return;
        }

        TakeRecoveryOf(instance);
    }

    /// <summary>The issue a triage instance was raised for, as the tally names it.</summary>
    private static string TitleOf(FlowInstance instance)
        => instance.Issue?.Title ?? TextOf(instance.Output?["title"]) ?? "an issue";

    /// <summary>Where the issue a triage instance was raised for is, as the tally names it.</summary>
    private static string WhereOf(FlowInstance instance)
        => instance.Issue?.Where ?? string.Join(", ", (instance.Output?["where"] as JsonArray ?? []).Select(TextOf).OfType<string>());

    /// <summary>
    /// The issue a triage instance answered <c>list</c> for, as the run's file would hold it next: its quote or its
    /// output cut by the one owner of the cut, the cut said.
    /// </summary>
    private PreExistingIssue IssueOf(FlowInstance instance)
    {
        var where = instance.Output?["where"] as JsonArray;
        var proof = instance.Output?["proof"] as JsonObject;

        return new PreExistingIssue(
            $"pe-{(_issues.Count + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}",
            TextOf(instance.Output?["title"]) ?? instance.Issue?.Title ?? "an issue",
            TextOf(instance.Output?["description"]) ?? instance.Issue?.Description ?? string.Empty,
            [.. (where ?? []).Select(path => TextOf(path) ?? instance.Issue?.Where ?? string.Empty)],
            new PreExistingProof(
                TextOf(proof?["kind"]) ?? "read",
                TextOf(proof?["at"]) ?? string.Empty,
                TextOf(proof?["file"]),
                TextOf(proof?["lines"]),
                TextOf(proof?["quote"]) is { } quote ? PreExistingIssuesWriter.Cut(quote) : null,
                TextOf(proof?["command"]),
                TextOf(proof?["output"]) is { } output ? PreExistingIssuesWriter.Cut(output) : null),
            TextOf(instance.Output?["why"]) ?? "none of the three: not related to the work, not critical, and in no step's way",
            instance.RaisedBy?.Id ?? instance.Id,
            instance.FinishedAt ?? _clock.GetUtcNow());
    }

    /// <summary>
    /// Whether the run's list holds the issue <paramref name="triage"/> answered <c>list</c> for: one the same in all
    /// but its id and when it was found, for those are the list's own - two the same in all else are one issue.
    /// </summary>
    private bool Listed(FlowInstance triage)
    {
        var issue = IssueOf(triage);

        return _issues.Any(listed => listed with { Id = issue.Id, FoundAt = issue.FoundAt, Where = issue.Where, Proof = issue.Proof } == issue
            && listed.Where.SequenceEqual(issue.Where, StringComparer.Ordinal)
            && listed.Proof == issue.Proof);
    }

    /// <summary>
    /// Adds the issue a triage instance answered <c>list</c> for to the run's list, proven - before the triage's end is
    /// recorded, so that end's one write carries the list, in the one order. Its answer was read back before
    /// (<see cref="TriageRefusal"/>).
    /// </summary>
    private void ListIssue(FlowInstance instance)
    {
        var issue = IssueOf(instance);
        _issues.Add(issue);
        _tally.Add(new PreExistingTally("listed", $"{issue.Title} ({string.Join(", ", issue.Where)})"));
    }

    /// <summary>Writes what an ending owes its handlers, without raising anything: a pause finds it owed, and the resume makes it. Answers whether it owes any.</summary>
    private bool OweHandlers(FlowInstance instance)
    {
        if (instance.DeclaredScope is not { } scope)
        {
            return false;
        }

        var handlers = DeclarationsOf(scope).Where(declaration => declaration.On?.Step == instance.Step.Name).ToList();

        if (handlers.Count == 0)
        {
            return false;
        }

        instance._handlers.Clear();
        instance._handlers.AddRange(handlers.Select(handler => new FlowHandlerState(handler.Name, "owed")));
        instance.FailureSettled = instance.State != FlowInstanceState.Failed;
        WriteStepRecordOf(instance);
        return true;
    }

    /// <summary>
    /// What a step's own work ended with: a step that runs legs is read from each leg's own verdict, and every other
    /// step from what its runner said. Under <c>failOn: red</c> any red leg fails the step with its worst red leg's
    /// code, and a check with no legs that found red fails with its own; under <c>never</c> red is no failure, and the
    /// legs that still decide - a failure that is not red, an incomplete one - decide as they would without it.
    /// </summary>
    /// <returns>Whether it passed, its code, its error, and the typed cause its code is read with: the runner's for a step with no legs, the worst incomplete leg's own for one whose legs ended so.</returns>
    private static (bool Passed, int Exit, string? Error, OutcomeCause Cause) Ends(WorkflowStep step, FlowStepResult result)
    {
        var failsOnRed = (step.FailOn ?? WorkflowStepTypes.Find(step.Type ?? string.Empty)?.FailOnDefault) == "red";

        if (result.Legs.Count == 0)
        {
            return result.Red && result.Passed
                ? (!failsOnRed, result.Exit, result.Error, OutcomeCause.None)
                : (result.Passed, result.Exit, result.Error, result.Cause);
        }

        var red = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Red);
        var notRed = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.FailureThatIsNotRed);
        var incomplete = FlowLegVerdicts.OfClass(result.Legs, FlowLegClass.Incomplete);

        if (red.Count > 0 && failsOnRed)
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(red)!.Value);
            return (false, code, WorstLine(red, code), OutcomeCause.None);
        }

        if (notRed.Count > 0)
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(notRed)!.Value);
            return (false, code, WorstLine(notRed, code), OutcomeCause.None);
        }

        if (incomplete.Count > 0)
        {
            // What the step ends with; whether it is run again is the retry table's to say, and the error says it after.
            var worst = FlowLegVerdicts.Worst(incomplete)!.Value;
            var code = FlowLegVerdicts.CodeOf(worst);
            return (false, code, WorstLine(incomplete, code), FlowLegVerdicts.CauseOf(worst));
        }

        if (red.Count > 0)
        {
            var code = FlowLegVerdicts.CodeOf(FlowLegVerdicts.Worst(red)!.Value);
            return (true, code, WorstLine(red, code), OutcomeCause.None);
        }

        return (true, HarnessExit.Success, null, OutcomeCause.None);
    }

    /// <summary>The line that says what the worst of <paramref name="legs"/> found, as the step's error keeps it.</summary>
    private static string WorstLine(IReadOnlyList<FlowLeg> legs, int code)
    {
        var worst = FlowLegVerdicts.Worst(legs)!.Value;
        var leg = legs.First(leg => leg.Verdict == worst);

        return $"leg '{leg.Leg}' ended {Verdicts.Display(worst)} ({code})";
    }

    /// <summary>
    /// Carries a failure to where it is decided: its handlers are raised first, for a failure a handler takes never
    /// reaches a policy; where none was raised the policy decides - the step's own, the recovery where it says
    /// <c>recover</c>, the group a member that wrote none hands it to (the recovery taking it at a loop's member where
    /// the onFailure that decides says so), and the top level's default, <c>stop</c>, or <c>pause</c> in a workflow
    /// that declares a recovery.
    /// </summary>
    private void HandleFailure(FlowInstance instance)
    {
        // What decides the failure writes where it left the run: a handler raised for it, a policy, a question.
        ClearOwed(instance);

        if (RaiseFor(instance))
        {
            return;
        }

        ApplyPolicy(instance);
    }

    /// <summary>The failure policy of <paramref name="instance"/>, where no handler took its failure.</summary>
    private void ApplyPolicy(FlowInstance instance)
    {
        ClearOwed(instance);
        instance.FailureSettled = true;

        if (instance.Step.OnFailure == WorkflowStep.Recover)
        {
            TakeRecovery(instance);
            return;
        }

        if (instance.Step.OnFailure is { } policy)
        {
            Apply(instance, policy);
            MaybeSettle(instance);
            return;
        }

        if (instance.GroupOf is { } group)
        {
            if ((group.Step.ForEach is not null || group.Step.Repeat is not null) && PolicyAbove(instance.DeclaredScope!) == WorkflowStep.Recover)
            {
                // The hand-over stops at a member of a forEach or a repeat: the recovery takes the failure there, for
                // that step, so it never ends the loop's other items or iterations.
                TakeRecovery(instance);
                return;
            }

            // A group that has ended takes no failure: an always member of a group that is not always runs after its
            // group's ending (RunsAfterItsGroup), and its failure goes where the group's own would have gone - the
            // onFailure the group writes, or on to the group around it, up to the top level's default. A group done is
            // never failed over a step that ran after its ending was read.
            for (var taking = group; taking is not null; taking = taking.GroupOf)
            {
                if (taking.State is FlowInstanceState.Running or FlowInstanceState.Paused)
                {
                    // A member that wrote no onFailure hands its failure to its group: nothing more of the group starts,
                    // its members in flight run to their end, and the group ends failed with that member's code - the
                    // first in declaration order where several did so. The member's record says its group took it,
                    // which a resume reads to fail the group again where it is still open.
                    SetFailure(instance, FlowFailure.TakenByGroup);
                    FailGroup(taking, instance.Exit ?? HarnessExit.InternalError, instance.Error, instance.Path);
                    MaybeSettle(instance);
                    return;
                }

                if (taking.Step.OnFailure == WorkflowStep.Recover)
                {
                    TakeRecovery(instance);
                    return;
                }

                if (taking.Step.OnFailure is { } groupsPolicy)
                {
                    Apply(instance, groupsPolicy);
                    MaybeSettle(instance);
                    return;
                }
            }
        }

        // A workflow that declares a recovery pauses at the top where nothing else decides, for a person is asked.
        Apply(instance, _definition.Recover is null ? "stop" : "pause");
        MaybeSettle(instance);
    }

    /// <summary>Takes back the owed mark a failure is recorded with as it ends, once it is being decided: what decides it writes the step's record.</summary>
    private static void ClearOwed(FlowInstance instance)
    {
        if (instance.Failure is FlowFailure.Owed)
        {
            instance.Failure = null;
        }
    }

    /// <summary>The first <c>onFailure</c> written on <paramref name="scope"/> or any scope around it, and where the scope holds none.</summary>
    private static string? PolicyAbove(Scope scope)
    {
        for (var at = scope; at is not null; at = at.Parent)
        {
            if (at.Group?.OnFailure is { } policy)
            {
                return policy;
            }
        }

        return null;
    }

    /// <summary>
    /// Raises the handlers of <paramref name="instance"/>'s ending, in declaration order: every one whose outcome and
    /// codes fit, and whose <c>when</c> is not false, is raised - a new visit of it, admitted against the limits and
    /// the claims like any other step - and every one that does not fit is passed by. Answers whether anything was
    /// raised or the ending waits: while a handler runs, what depends on the source waits, and where nothing was
    /// raised the failure goes to its policy.
    /// </summary>
    /// <returns>Whether a handler was raised, or the ending waits on a question the raise asked.</returns>
    private bool RaiseFor(FlowInstance instance, string? without = null)
    {
        if (instance.DeclaredScope is not { } scope)
        {
            return false;
        }

        var failed = instance.State == FlowInstanceState.Failed;
        var outcome = failed ? "failure" : "success";
        var handlers = DeclarationsOf(scope).Where(declaration => declaration.On?.Step == instance.Step.Name).ToList();

        if (handlers.Count == 0)
        {
            return false;
        }

        // The ending is written with what it owes before anything is raised, so a pause between the two finds the
        // raise owed and the resume makes it.
        instance._handlers.Clear();
        instance._handlers.AddRange(handlers.Select(handler => new FlowHandlerState(handler.Name, "owed")));
        WriteStepRecordOf(instance);

        var fitting = new List<WorkflowStep>();
        WorkflowStep? atBound = null;

        foreach (var handler in handlers)
        {
            var on = handler.On!;

            if (RaisedFor(instance, handler, scope) is not null)
            {
                // Raised for this ending already: a crash after the handler's visit was made and before the source's
                // record said so left the raise made, and it is never made twice.
                fitting.Add(handler);
                continue;
            }

            if ((on.Outcome != "end" && on.Outcome != outcome)
                || (on.Codes.Count > 0 && (instance.Exit is not { } code || !on.Codes.Contains(code))))
            {
                PassBy(handler, instance, scope, $"not raised: '{instance.Step.Name}' ended {(failed ? "failed" : "done")}");
                continue;
            }

            if (handler.Name == without)
            {
                // Its bound was met and the answer took the ending without it.
                PassBy(handler, instance, scope, $"not raised: its bound was met, and '{instance.Step.Name}' was taken as it ended");
                continue;
            }

            if (handler.When is { } when)
            {
                var holds = Holds(when, scope, out var problem, instance, drawing: scope.Visits.GetValueOrDefault(handler.Name) + 1);

                if (problem is not null)
                {
                    var unread = NewVisit(scope, handler, FlowInstanceState.Failed, made: unread =>
                    {
                        unread.RaisedBy = instance;
                        unread.Exit = HarnessExit.ConfigInvalid;
                        unread.Error = Unreadable("when", unread.Id, problem);
                    });
                    End(unread, FlowInstanceState.Failed, unread.Exit, unread.Error);
                    ApplyPolicy(unread);
                    SetHandlerState(instance, handler.Name, "raised");
                    fitting.Add(handler);
                    continue;
                }

                if (!holds)
                {
                    PassBy(handler, instance, scope, $"not raised: '{instance.Step.Name}' ended, and '{handler.Name}' reads false of it");
                    continue;
                }
            }

            if (RaisedVisits(scope, handler) >= (handler.MaxVisits ?? 3) + scope.ExtraVisits.GetValueOrDefault(handler.Name))
            {
                if (failed && _definition.Recover is not null)
                {
                    // A handler at its bound after a failure is not asked about in a workflow that declares a
                    // recovery: the source's failure is left unhandled and the recovery takes it.
                    SetHandlerState(instance, handler.Name, "passed");
                    continue;
                }

                atBound ??= handler;
                continue;
            }

            fitting.Add(handler);
        }

        if (atBound is { } bound)
        {
            // The handler would be raised once more than its bound allows: the run asks, as a repeat at its bound
            // does, and the ending waits on the answer. What depends on the source waits with it.
            instance.FailureSettled = false;
            _ = Ask(
                instance,
                "visitBound",
                $"'{bound.Name}' has answered '{instance.Step.Name}' {RaisedVisits(scope, bound).ToString(System.Globalization.CultureInfo.InvariantCulture)} times, and '{instance.Step.Name}' {(failed ? $"failed again ({instance.Exit})" : "ended done again")}",
                FlowQuestions.OptionsOf("visitBound", bound),
                subject: bound.Name);
            return true;
        }

        if (fitting.Count == 0)
        {
            return false;
        }

        var raisedFor = new List<FlowInstance>();
        FlowInstance? then = null;

        foreach (var handler in fitting)
        {
            // One visit for each handler raised: the one a when that could not be read failed is that visit, and so is
            // one an earlier raise of this ending made.
            var raised = RaisedFor(instance, handler, scope) ?? NewVisit(scope, handler, FlowInstanceState.NotStarted, made: raised => raised.RaisedBy = instance);
            raisedFor.Add(raised);
            SetHandlerState(instance, handler.Name, "raised");

            if (handler.Then is not null)
            {
                then = raised;
            }
        }

        instance.FailureSettled = !failed;
        var pending = new PendingEnding(instance, failed, raisedFor, then);
        _pendingEndings[instance.Id] = pending;

        if (raisedFor.All(Ended))
        {
            // Every handler it raised has ended already - one whose when could not be read, one an earlier raise made
            // that ended before the crash: the ending is settled now, for no handler's end is left to settle it.
            SettleEnding(pending);
        }

        return true;
    }

    /// <summary>
    /// The visit of <paramref name="handler"/> raised for <paramref name="source"/>'s ending, where one was made: a source
    /// raises each handler once for an ending, so one found is that raise.
    /// </summary>
    private FlowInstance? RaisedFor(FlowInstance source, WorkflowStep handler, Scope scope)
        => _created.LastOrDefault(visit => ReferenceEquals(visit.DeclaredScope, scope) && visit.Step.Name == handler.Name && ReferenceEquals(visit.RaisedBy, source));

    /// <summary>
    /// How many times <paramref name="handler"/> was raised in <paramref name="scope"/>: its visits an ending raised, and
    /// never the place-holder a pass-by or a source that was not run leaves - what its bound counts.
    /// </summary>
    private int RaisedVisits(Scope scope, WorkflowStep handler)
        => _created.Count(visit => ReferenceEquals(visit.DeclaredScope, scope)
            && visit.Step.Name == handler.Name
            && (visit.RaisedBy is not null || visit.RaisedByRecord is not null));

    /// <summary>Passes a handler by: one never raised before is Skipped, with the reason, which satisfies what depends on it; one raised before keeps its last visit.</summary>
    private void PassBy(WorkflowStep handler, FlowInstance source, Scope scope, string reason)
    {
        SetHandlerState(source, handler.Name, "passed");

        if (scope.Members.ContainsKey(handler.Name))
        {
            return;
        }

        scope.Instantiated.Add(handler.Name);
        _ = Create(scope, handler, FlowInstanceState.Skipped, made: passed => passed.SkippedBecause = reason);
    }

    /// <summary>
    /// What a source that was Skipped or NotRun leaves its handlers: each never raised before follows it - Skipped
    /// with it, which satisfies what depends on it, or NotRun, which satisfies nothing - and one raised before keeps
    /// its last visit. A source that stopped raises nothing yet, for it runs again at the resume.
    /// </summary>
    private void HandlersFollow(FlowInstance source, FlowInstanceState state)
    {
        if (source.DeclaredScope is not { } scope)
        {
            return;
        }

        foreach (var handler in DeclarationsOf(scope).Where(declaration => declaration.On?.Step == source.Step.Name))
        {
            if (scope.Members.ContainsKey(handler.Name))
            {
                continue;
            }

            scope.Instantiated.Add(handler.Name);
            _ = Create(scope, handler, state, made: made =>
            {
                if (state == FlowInstanceState.Skipped)
                {
                    made.SkippedBecause = $"not raised: '{source.Step.Name}' was skipped";
                }
                else if (state == FlowInstanceState.NotRun)
                {
                    made.NotRunBecause = source.NotRunBecause is { } because ? $"'{source.Step.Name}': {because}" : $"'{source.Step.Name}' was not run";
                    made.NotRunCause = source.NotRunCause ?? FlowNotRunCause.DependencyFailed;
                }
            });
        }
    }

    /// <summary>Sets where one handler of an ending stands, and writes the source's own record with it.</summary>
    private void SetHandlerState(FlowInstance source, string handler, string state)
    {
        var held = source._handlers.FirstOrDefault(entry => entry.Handler == handler);

        if (held is null)
        {
            source._handlers.Add(new FlowHandlerState(handler, state));
        }
        else
        {
            source._handlers.Remove(held);
            source._handlers.Add(held with { State = state });
        }

        WriteStepRecordOf(source);
    }

    /// <summary>
    /// Passes every handler an ending still owes where its question was settled without raising them - answered
    /// <c>fail</c>, or withdrawn by a redirect - so no later session finds them owed in its record and raises them.
    /// </summary>
    private void PassOwed(FlowInstance source)
    {
        foreach (var owed in source._handlers.Where(handler => handler.State == "owed").ToList())
        {
            SetHandlerState(source, owed.Handler, "passed");
        }
    }

    /// <summary>Writes the source's own record alone, so what its ending owes stands on disk the moment it changes.</summary>
    private void WriteStepRecordOf(FlowInstance instance)
    {
        if (_records is null || _replaying)
        {
            return;
        }

        var snapshot = Snapshot();
        var (id, state, attempt) = (instance.Id, instance.State.ToString(), instance.Attempt);
        Put(records => records.Write(FlowChangeKind.Step, snapshot, id, state, state, attempt));
    }

    /// <summary>
    /// Whether the ending <paramref name="source"/>'s handlers were raised for was abandoned by a redirect, as its record
    /// says it: the redirect left one of them before it ended (<see cref="Leave"/>), so the ending is never settled - by
    /// that handler or by a sibling that ran - and no resume rebuilds it from the handlers its record holds raised.
    /// </summary>
    private static bool Abandoned(FlowInstance source)
        => source._handlers.Any(handler => handler.State == "left");

    /// <summary>Whether a handler of an ending has ended, its own failure settled with it.</summary>
    private static bool Ended(FlowInstance handler)
        => handler.State switch
        {
            FlowInstanceState.Done or FlowInstanceState.Skipped => true,
            FlowInstanceState.Failed => handler.FailureSettled,
            _ => false,
        };

    /// <summary>
    /// Settles an ending whose handlers all ended: with a <c>then</c> among them that ended done the run goes to the
    /// step it names; without one a failure counts as handled, and what depends on the source starts on what it left.
    /// </summary>
    private void SettleEnding(PendingEnding pending)
    {
        _ = _pendingEndings.Remove(pending.Source.Id);

        if (pending.Then is { } then && then.State == FlowInstanceState.Done && then.Step.Then is not null && !_redirects.Any(owed => ReferenceEquals(owed.Handler, then)))
        {
            // The redirect a then names is decided first, written to the run's records before the source's own record
            // says its ending is settled: a crash between the two leaves the ending to settle again, and the redirect
            // is decided once.
            DecideRedirect(then);
        }

        var source = pending.Source;

        foreach (var handler in pending.Handlers)
        {
            source._handlers.RemoveAll(entry => entry.Handler == handler.Step.Name);
            source._handlers.Add(new FlowHandlerState(handler.Step.Name, "done"));
        }

        if (pending.Failed)
        {
            // The failure counts as handled - by the handler that took it, whatever the handler went on to do with
            // its then - and what depends on the source starts on what it left.
            source.HandledBy = pending.Handlers[0].Step.Name;
            source.FailureSettled = true;
        }

        // The ending settled in one record: every handler done, and the failure handled.
        WriteStepRecordOf(source);
    }

    /// <summary>Where an instance's ending leaves what waits on it: a handler that ended settles its source's ending.</summary>
    private void MaybeSettle(FlowInstance instance)
    {
        if (instance.RaisedBy is { } source && _pendingEndings.TryGetValue(source.Id, out var pending) && pending.Handlers.All(Ended))
        {
            SettleEnding(pending);
        }
    }

    /// <summary>Whether <paramref name="instance"/> is a visit of the workflow's recovery.</summary>
    private bool IsRecovery(FlowInstance instance)
        => _definition.Recover is not null && instance.Step.Name == FlowScope.Recovery;

    /// <summary>The declaration the recovery's instances are made of: its steps, under the reserved name.</summary>
    private WorkflowStep RecoveryDeclaration()
        => new()
        {
            Name = FlowScope.Recovery,
            Steps = _definition.Recover!.Steps,
            OnFailure = _definition.Recover.OnFailure,
        };

    /// <summary>Decides the redirect a handler that ended done names: what it resets is written to the run's records before the first reset.</summary>
    private void DecideRedirect(FlowInstance handler)
    {
        if (ResolveThen(handler) is not { } target)
        {
            return;
        }

        DecideRedirectOf(handler, target.Scope, target.Declaration);
    }

    /// <summary>The step a handler's <c>then</c> names, with the scope that declares it: siblings first, then outward.</summary>
    private (Scope Scope, WorkflowStep Declaration)? ResolveThen(FlowInstance handler)
    {
        for (var at = handler.DeclaredScope; at is not null; at = at.Parent)
        {
            if (DeclarationsOf(at).FirstOrDefault(declaration => declaration.Name == handler.Step.Then) is { } declaration)
            {
                return (at, declaration);
            }
        }

        return null;
    }

    /// <summary>What a pending redirect keeps from starting: for each scope it touches, the member names it will reset - or the whole scope, a group with all that is in it.</summary>
    private void FrozenOf(PendingRedirect redirect)
    {
        foreach (var (scope, declaration) in redirect.Set)
        {
            if (declaration.IsGroup && scope.Members.GetValueOrDefault(declaration.Name) is { } group)
            {
                // A group resets with all that is in it, at every level, so nothing inside it starts either.
                foreach (var inner in ScopesUnder(group))
                {
                    redirect.Frozen[inner.Id] = null;
                }
            }
            else if (redirect.Frozen.TryGetValue(scope.Id, out var held))
            {
                held?.Add(declaration.Name);
            }
            else
            {
                redirect.Frozen[scope.Id] = [declaration.Name];
            }
        }
    }

    /// <summary>Decides one redirect: its set computed, its open agents asked about, itself written to the run's records before any reset.</summary>
    private void DecideRedirectOf(FlowInstance handler, Scope targetScope, WorkflowStep targetDeclaration)
    {
        // A redirect decided while others wait is queued behind them, never given up: each is made in its turn.
        var redirect = new PendingRedirect(handler, targetDeclaration.Name);
        Collect(redirect, targetScope, targetDeclaration);
        FrozenOf(redirect);

        _redirects.Add(redirect);
        var open = OpenAgentsOf(redirect);

        if (open.Count > 0)
        {
            // A redirect that would leave a group while an agent made in it still has its worktree does not go by
            // itself: it asks, naming each, and the answers say whether it goes.
            redirect.Asking = Ask(
                handler,
                "openAgents",
                $"'{handler.Step.Name}' would go back to '{redirect.Target}', and agents '{string.Join("', '", open)}', made since, are still open",
                FlowQuestions.OptionsOf("openAgents", handler.Step));
            return;
        }

        Record(FlowChangeKind.Step, handler, detail: $"the run is sent to '{redirect.Target}' by '{handler.Id}'");
    }

    /// <summary>Collects what a redirect resets: the target, and every step of its scope that depends on it, directly or through others - a group with all that is in it.</summary>
    private void Collect(PendingRedirect redirect, Scope scope, WorkflowStep declaration)
    {
        if (redirect.Set.Any(entry => entry.Scope == scope && entry.Declaration.Name == declaration.Name)
            || declaration.On is not null
            || IsAlways(declaration, scope))
        {
            // A handler is not given a visit by the reset: its next visit is made when it is next raised. An always
            // step runs once in a session and is never gone back to.
            return;
        }

        redirect.Set.Add((scope, declaration));

        foreach (var dependent in DeclarationsOf(scope).Where(dependent => dependent.DependsOn.Contains(declaration.Name, StringComparer.Ordinal)))
        {
            Collect(redirect, scope, dependent);
        }
    }

    /// <summary>The agents a redirect would leave open: those made in the groups it resets, still live.</summary>
    private List<string> OpenAgentsOf(PendingRedirect redirect)
    {
        var agents = new List<string>();

        foreach (var entry in redirect.Set.Where(entry => entry.Declaration.IsGroup))
        {
            if (entry.Scope.Members.GetValueOrDefault(entry.Declaration.Name) is not { } group)
            {
                continue;
            }

            foreach (var member in ScopesUnder(group).SelectMany(scope => scope.Members.Values))
            {
                if (member.Step.Type == "create-agent" && member.Made is not null && _agentsOpen.ContainsKey(member.Id))
                {
                    agents.Add(member.Made);
                }
            }
        }

        return agents;
    }

    /// <summary>
    /// Whether <paramref name="redirect"/> can be made: no step it resets is running - at any level of a group it resets,
    /// and only a step that runs counts: a group standing open over members that wait is no step running.
    /// </summary>
    private static bool RedirectQuiet(PendingRedirect redirect)
    {
        foreach (var entry in redirect.Set)
        {
            if (entry.Scope.Members.GetValueOrDefault(entry.Declaration.Name) is not { } instance)
            {
                continue;
            }

            var held = instance.Step.IsGroup ? ScopesUnder(instance).SelectMany(scope => scope.Members.Values) : [instance];

            if (held.Any(member => !member.Step.IsGroup && member.State is FlowInstanceState.Running))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Makes one redirect the run owes, as the moment before its target started: every step of its set is taken out of
    /// its scope's drawn steps - the visit it had stands as it ended, and one still waiting is left (<see cref="Leave"/>)
    /// - and the schedule draws each again as a new visit, through its dependencies and its when, as it drew the first:
    /// what depends on the target waits for its new visit. A group entered again begins whole - a repeat at iteration 1
    /// with its bounds intact, a forEach expanded anew. A part of the set in a scope an earlier redirect left is not
    /// reset: that visit is over.
    /// </summary>
    private void ApplyRedirect(PendingRedirect redirect)
    {
        var because = $"the run was sent back to '{redirect.Target}' by '{redirect.Handler.Step.Name}', leaving this visit";
        var reset = 0;

        foreach (var (scope, declaration) in redirect.Set.Where(entry => ScopeLive(entry.Scope)))
        {
            if (scope.Members.Remove(declaration.Name, out var current))
            {
                // The visit left says so in its own record, so a resume draws the next one from the records alone
                // whatever came after: the redirect leaves the run's records only once each visit it resets says it.
                current.LeftBy = redirect.Handler.Id;
                Leave(current, because);
                WriteStepRecordOf(current);
            }

            _ = scope.Instantiated.Remove(declaration.Name);
            reset++;
        }

        _ = _redirects.Remove(redirect);
        Record(FlowChangeKind.Step, redirect.Handler, detail: reset == 0
            ? $"the run was not sent to '{redirect.Target}': an earlier redirect left the scope it would reset"
            : $"the run was sent to '{redirect.Target}', resetting {reset.ToString(System.Globalization.CultureInfo.InvariantCulture)} of its scope");
    }

    /// <summary>
    /// Leaves one visit a redirect resets: a question it, or anything open inside it, waits on is withdrawn - to be
    /// asked anew by the new visit if it comes to that - a step that never started or waits on a question is skipped,
    /// and a group still open is skipped with all that is open in it, so nothing of the old visit runs. A visit that
    /// had ended stands as it ended.
    /// </summary>
    private void Leave(FlowInstance visit, string because)
    {
        if (visit.WaitingQuestion is { } question)
        {
            Withdraw(question);
            PassOwed(visit);
        }

        if (visit.Step.IsGroup && visit.State is FlowInstanceState.Running or FlowInstanceState.Paused)
        {
            foreach (var member in ScopesOf(visit).SelectMany(inner => inner.Members.Values).ToList())
            {
                Leave(member, because);
            }

            foreach (var child in visit.Children.Select(child => child.GroupInstance).Where(child => child is { State: FlowInstanceState.Running }))
            {
                child!.SkippedBecause = because;
                End(child, FlowInstanceState.Skipped, child.Exit, child.Error);
            }
        }
        else if (visit.State is not (FlowInstanceState.NotStarted or FlowInstanceState.Paused))
        {
            return;
        }

        if (visit.RaisedBy is { } source && _pendingEndings.TryGetValue(source.Id, out var pending) && pending.Handlers.Contains(visit))
        {
            // A handler left before it ended took nothing: the ending it was raised for is abandoned - never settled, by
            // it or by a sibling that ran, and its source never counted handled by a handler that never ran - and the
            // source's record says the handler was left, so no resume rebuilds the ending either (Abandoned).
            _ = _pendingEndings.Remove(source.Id);
            SetHandlerState(source, visit.Step.Name, "left");
        }

        visit.SkippedBecause = visit.State is FlowInstanceState.NotStarted ? $"withdrawn before it started: {because}" : because;
        End(visit, FlowInstanceState.Skipped, visit.Exit, visit.Error);
    }

    /// <summary>Makes a new visit of <paramref name="declaration"/> in <paramref name="scope"/>, and fails it where the run would visit a step more than 99 times in one scope.</summary>
    private FlowInstance NewVisit(Scope scope, WorkflowStep declaration, FlowInstanceState state, Action<FlowInstance>? made = null)
    {
        if (scope.Visits.GetValueOrDefault(declaration.Name) + 1 > 99)
        {
            var bound = Create(scope, declaration, FlowInstanceState.Failed, made: bound =>
            {
                made?.Invoke(bound);
                bound.Exit = HarnessExit.Refused;
                bound.Error = $"'{declaration.Name}' was visited 99 times in its scope; at most 99 visits of one step in one scope are allowed";
            });
            End(bound, FlowInstanceState.Failed, bound.Exit, bound.Error);
            HandleFailure(bound);
            return bound;
        }

        return Create(scope, declaration, state, made: made);
    }

    /// <summary>
    /// Takes a failure to the workflow's recovery: one visit of it for that step, in the scope the step failed in,
    /// admitted against the limits and the claims like any other step. Where its bound is met the run asks, with the
    /// advisor's recommendation as every question carries one.
    /// </summary>
    private void TakeRecovery(FlowInstance failed)
        => TakeRecoveryOf(failed);

    /// <summary>
    /// What a visit of the recovery taken for <paramref name="source"/> is for - the one owner every place that takes
    /// one, answers its bound, runs it again or rebuilds it at a resume asks, so all of them read the same from the
    /// records alone: a failed step's failure, the step itself running again once the recovery is done; or the issue a
    /// triage instance that ended done sent to fix, its reporter running again where the issue blocks it and the run
    /// simply going on where it does not. With where its visits are counted against the recovery's bound: the failed
    /// step's declaration in its scope, or the triage instance itself.
    /// </summary>
    private (FlowInstance? Rerun, FlowPreExistingReport? Issue, (string Scope, string Step) Key) RecoveryOf(FlowInstance source)
    {
        var scope = (source.DeclaredScope ?? _top).Id;

        return source.State != FlowInstanceState.Failed && source.Issue is { } issue
            ? (issue.Blocks ? source.RaisedBy : null, issue, (scope, source.Id))
            : (source, null, (scope, source.Path));
    }

    /// <summary>
    /// Takes one thing to the workflow's recovery, as <see cref="RecoveryOf"/> says what it is: a failure, or an issue a
    /// triage answered <c>fix</c> for.
    /// </summary>
    /// <param name="source">The step whose failure it takes, or the triage instance whose issue it fixes: what its steps read as <c>source</c>.</param>
    /// <param name="direction">The fix a person gave this visit, answering the bound direct; null for none.</param>
    private void TakeRecoveryOf(FlowInstance source, string? direction = null)
    {
        var recover = _definition.Recover!;
        var scope = source.DeclaredScope ?? _top;
        var (_, issue, key) = RecoveryOf(source);
        var visits = _recoveryVisits.TryGetValue(key, out var taken) ? taken + 1 : 1;
        var allowed = (recover.MaxVisits ?? FlowRecover.MostVisits) + _recoveryExtra.GetValueOrDefault(key);

        if (visits > allowed)
        {
            source.FailureSettled = false;
            _ = Ask(
                source,
                "recoveryBound",
                issue is { } sent
                    ? $"'{sent.Title}' is to be fixed at its root, and the recovery has taken it {allowed.ToString(System.Globalization.CultureInfo.InvariantCulture)} time{(allowed == 1 ? string.Empty : "s")} without putting it right"
                    : $"'{source.Step.Name}' failed, and the recovery took it {allowed.ToString(System.Globalization.CultureInfo.InvariantCulture)} time{(allowed == 1 ? string.Empty : "s")} without putting it right ({source.Exit}: {source.Error ?? "its work ended failed"})",
                FlowQuestions.OptionsOf("recoveryBound", source.Step));
            return;
        }

        _recoveryVisits[key] = visits;
        source.FailureSettled = false;

        // The source's own record says its failure - or its issue - is the recovery's now, before the visit is made.
        WriteStepRecordOf(source);

        var declaration = RecoveryDeclaration();
        var instance = NewVisit(scope, declaration, FlowInstanceState.Running, made: visit =>
        {
            visit.RaisedBy = source;
            visit.Visit = visits;
            visit.Direction = direction;
        });

        // A block of its own in the scope the source stands in: its steps are scheduled as any scope's are, and read
        // the source - the failed step, or the triage instance and its issue - as source and {issue}.
        instance.OwnScope = NewScope(instance.Id, PathOf(scope, declaration), scope, declaration, FlowValue.Missing, null, null, null);
        instance.OwnScope.OwnGroupInstance = instance;
        Record(FlowChangeKind.Step, instance, detail: issue is null
            ? $"the failure of '{source.Id}' is given to the recovery (visit {visits.ToString(System.Globalization.CultureInfo.InvariantCulture)})"
            : $"the issue '{source.Id}' was sent to fix is given to the recovery (visit {visits.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
    }

    /// <summary>Withdraws a question the run no longer waits on: its entry in questions.json says so, and the step's record says why.</summary>
    private void Withdraw(string question)
    {
        if (_questions.FirstOrDefault(asked => asked.Id == question) is not { } asked)
        {
            return;
        }

        asked.State = "withdrawn";
        _ = _open.Remove(question);

        if (_answerable.Remove(question) && _instances.TryGetValue(asked.Instance, out var waiting))
        {
            waiting.WaitingQuestion = null;
        }

        WriteQuestions();
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

        // Nothing more of the group starts - save its always steps, which run however the run went, in the always
        // phase: an always member of a group that is not always after the group ended, which never waited for it, and
        // an always group's members within its visit, which ends failed once they have run.
        foreach (var scope in ScopesOf(group))
        {
            foreach (var declaration in DeclarationsOf(scope).Where(declaration => !scope.Instantiated.Contains(declaration.Name) && !IsAlways(declaration, scope)).ToList())
            {
                scope.Instantiated.Add(declaration.Name);
                CreateNotRun(scope, declaration, FlowNotRunCause.GroupFailed, $"'{failed}' failed and its group took the failure");
            }
        }

        foreach (var member in ScopesOf(group).SelectMany(scope => scope.Members.Values).Where(member => member.State is FlowInstanceState.NotStarted && !member.IsAlways).ToList())
        {
            NotRun(member, FlowNotRunCause.GroupFailed, $"'{failed}' failed and its group took the failure");
        }
    }

    /// <summary>Applies one failure policy to the instance that ended failed.</summary>
    private void Apply(FlowInstance instance, string policy)
    {
        switch (policy)
        {
            case "continue":
                SetFailure(instance, FlowFailure.PassedOver);
                break;
            case "pause":
                _ = Ask(
                    instance,
                    "failure",
                    $"'{instance.Id}' failed ({instance.Exit}): {instance.Error ?? "its work ended failed"}. What does the run do?",
                    FlowQuestions.OptionsOf("failure", instance.Step));
                break;
            case WorkflowStep.Recover:
                TakeRecovery(instance);
                break;
            default:
                _stopping = true;
                SetFailure(instance, FlowFailure.EndedRun);
                break;
        }
    }

    /// <summary>
    /// Raises a question on <paramref name="instance"/>: a step whose work asks is paused, holding nothing while it
    /// waits, and a question of an ending already made (<see cref="FlowQuestions.AsksOfAnEnding"/>) leaves the step as
    /// it ended; either way only what depends on it is blocked. A configuration that answered the kind beforehand - the
    /// nearest <c>answers</c> that names it, and never a kind only a person answers - is given its answer at once, in
    /// the place of a person, where the question offers that answer: the step is never paused for it, and the answer is
    /// recorded on the question as every answer is, saying where it was configured. One the question does not offer -
    /// a skip where the step is not skippable - is not given, nor counted, and a person is asked.
    /// </summary>
    /// <param name="instance">The instance the question is about.</param>
    /// <param name="kind">The question's kind.</param>
    /// <param name="problem">What happened, as the question states it.</param>
    /// <param name="options">The answers the question takes.</param>
    /// <param name="subject">What a counted answer of this question is counted for, where it is not the instance alone: a handler's name, a source's own.</param>
    /// <param name="agentQuestions">The questions the step's agent asked, where it asked; empty for the engine's own.</param>
    /// <param name="agent">The agent that asked, where one did; null for the engine's own question.</param>
    private string Ask(FlowInstance instance, string kind, string problem, IReadOnlyList<string> options, string? subject = null, IReadOnlyList<Agents.AgentQuestion>? agentQuestions = null, string? agent = null)
    {
        var id = FlowQuestionIds.Of(++_questionsRaised);
        var asked = new FlowQuestion(id, kind, instance.Id, problem, options)
        {
            RaisedAt = _clock.GetUtcNow(),
            Tried = TriedOf(instance),
            Means = MeansOf(instance),
            Held = FlowQuestions.HeldOptionsOf(kind, instance.Step, options),
            Handler = subject,
            AgentQuestions = agentQuestions ?? [],
            RaisedBy = agent is { } ? new FlowQuestionRaisedBy("step", instance.Id, agent) : new FlowQuestionRaisedBy("step", instance.Id),
            Recommendation = agentQuestions is [{ }, ..]
                ? new FlowRecommendation("given", Why: "each of its questions names the answer the agent recommends", By: "agent", Agent: agent)
                : FlowRecommendation.NotSought,
        };
        _questions.Add(asked);

        var was = instance.State;
        var configured = ConfiguredAnswer(instance, kind, options, subject);
        WaitOn(instance, asked);

        // The question is on disk before the step's own record and before the event: a client woken by either reads
        // what was asked already written.
        WriteQuestions();
        Record(FlowChangeKind.Step, instance, from: was, to: instance.State, attempt: instance.Attempt, detail: kind);

        if (configured is { } atOnce)
        {
            // A configuration that answered the kind beforehand answers at once, in a person's place, by the path a
            // person's answer takes: asked and waiting first, then answered - so a crash at any point between leaves
            // records the next session completes, never an answer whose change no record holds.
            _answeredAtOnce = true;
            TakeAnswer(asked, atOnce.Answer, null, "configuration", atOnce.Where, through: null);
        }

        return id;
    }

    /// <summary>
    /// Answers, at a session's start, each question left open with no answer that the configuration answers: a crash
    /// came between the question and its configured answer, and the configuration gives it now, as it would have then.
    /// The run's budget is asked of its configuration at its reserve alone, and one answered already - an abort that
    /// opened it again - waits for a person.
    /// </summary>
    private void AnswerWhatTheConfigurationAnswers()
    {
        foreach (var asked in OpenQuestions.Where(asked => asked.Given.Count == 0 && asked.Kind != "runBudget"))
        {
            if (_answerable.GetValueOrDefault(asked.Id) is { } about && ConfiguredAnswer(about, asked.Kind, asked.Options, asked.Handler) is { } configured)
            {
                TakeAnswer(asked, configured.Answer, null, "configuration", configured.Where, through: null);
            }
        }
    }

    /// <summary>
    /// Leaves <paramref name="asked"/> open with <paramref name="instance"/> waiting on it, holding nothing: where a
    /// question is raised, and where an <c>abort</c> settled nothing of it. A step whose work asked is paused; a question
    /// of an ending leaves the step in the state it ended in; the run's own question, its budget's, waits on no step.
    /// </summary>
    private void WaitOn(FlowInstance? instance, FlowQuestion asked)
    {
        asked.State = "open";
        _open.Add(asked.Id);
        _answerable[asked.Id] = instance;

        if (instance is null)
        {
            // The run's own question: no step waits on it, and the run is held while it is open.
            return;
        }

        instance.WaitingQuestion = asked.Id;

        if (!FlowQuestions.AsksOfAnEnding(asked.Kind))
        {
            instance.State = FlowInstanceState.Paused;
        }
    }

    /// <summary>What the run tried about <paramref name="instance"/> before a question of it asked: its attempts, and the answers its questions were already given.</summary>
    private string TriedOf(FlowInstance instance)
    {
        var given = _questions.Count(question => question.Instance == instance.Id && question.State == "answered");
        var tried = $"attempt {instance.Attempt.ToString(System.Globalization.CultureInfo.InvariantCulture)} of the step";
        return given > 0 ? $"{tried}, and {given.ToString(System.Globalization.CultureInfo.InvariantCulture)} answer{(given == 1 ? string.Empty : "s")} given already" : tried;
    }

    /// <summary>What the answer changes, as the run's person reads it: which steps wait on this one, by name; null where none do.</summary>
    private string? MeansOf(FlowInstance instance)
    {
        var waiting = _created
            .Where(held => held.DeclaredScope == instance.DeclaredScope && held.Step.DependsOn.Contains(instance.Step.Name, StringComparer.Ordinal))
            .Select(held => held.Id)
            .ToList();

        return waiting.Count == 0 ? null : $"'{string.Join("', '", waiting)}' wait{(waiting.Count == 1 ? "s" : string.Empty)} on this";
    }

    /// <summary>Writes the run's questions and answers, whole, through the records seam: the one owner of what every question said, and of what became of every answer handed over.</summary>
    private void WriteQuestions()
    {
        if (_records is null)
        {
            return;
        }

        List<FlowQuestionEntry> entries = [.. _questions.Select(asked => asked.ToEntry())];
        List<FlowRefusedAnswer> refused = [.. _refused];
        Put(records => records.Question(entries, refused));
    }

    /// <summary>
    /// The answer the configuration gives at once to a question of <paramref name="kind"/> on <paramref name="instance"/>
    /// - null for the run's own question - with where it is configured, counted where it is given; null where a person
    /// is asked. An answer that lets the same thing go on is given a counted number of times and no more, and then its
    /// <c>then</c>; an answer that ends the matter is given whenever the question comes, for it ends it. A direct answer
    /// is a person's alone, for its text is the person's fix, and one the question does not offer - a skip where the
    /// step is not skippable - is neither given nor counted.
    /// </summary>
    private (string Answer, string Where)? ConfiguredAnswer(FlowInstance? instance, string kind, IReadOnlyList<string> options, string? subject = null)
    {
        if (ConfiguredFor(instance, kind) is not { } configured)
        {
            return null;
        }

        var (entry, where) = configured;

        var key = FlowQuestions.CountedAt(kind, instance?.Id, subject);
        var given = _answerSpent.GetValueOrDefault(key);
        var counted = WorkflowValidator.GoesOn.Contains(entry.Answer, StringComparer.Ordinal) && given < (entry.Times ?? 1);
        var answer = entry.Answer == "direct"
            ? null
            : WorkflowValidator.GoesOn.Contains(entry.Answer, StringComparer.Ordinal)
                ? counted ? entry.Answer : entry.Then
                : entry.Answer;

        if (answer is not { } atOnce || !(options.Contains(atOnce, StringComparer.Ordinal) || atOnce == "abort"))
        {
            return null;
        }

        if (counted)
        {
            _answerSpent[key] = given + 1;
        }

        return (atOnce, where);
    }

    /// <summary>
    /// The configured answer a question of <paramref name="kind"/> on <paramref name="instance"/> is given at once:
    /// the nearest <c>answers</c> that names the kind - the step's own, then each group around it from the inside out,
    /// then the workflow's - and none where the kind is a person's alone, or answered by the step's own key
    /// (<c>failure</c> by <c>onFailure</c>, <c>ask</c> by <c>ask</c>), or a <c>pause</c> step answered anywhere but on
    /// the step itself and never one that says <c>person</c>, or the run's budget answered anywhere but on the
    /// workflow.
    /// </summary>
    private (FlowAnswer Entry, string Where)? ConfiguredFor(FlowInstance? instance, string kind)
    {
        if (FlowQuestions.PersonsKinds.Contains(kind, StringComparer.Ordinal) || kind is "failure" or "ask")
        {
            return null;
        }

        if (kind == "runBudget")
        {
            return _definition.Answers.TryGetValue(kind, out var run) ? (run, "the orchestrator's answers") : null;
        }

        if (instance is null)
        {
            return null;
        }

        if (kind == "pause")
        {
            return instance.Step.Person is true
                ? null
                : instance.Step.Answers.TryGetValue(kind, out var own) ? (own, $"step '{instance.Step.Name}'") : null;
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
        var was = instance.State;

        instance.State = state;
        instance.Exit ??= exit;
        instance.Error ??= error;
        instance.FinishedAt ??= _clock.GetUtcNow();

        if (state is FlowInstanceState.Failed && instance.DeclaredScope is not null)
        {
            // A failure is recorded owed as it ends, and what decides it - a handler, its policy, a question - writes
            // where it left the run over that: a crash between the two leaves it owed, and the next session decides it.
            instance.Failure = FlowFailure.Owed;
        }

        if (state is FlowInstanceState.Done or FlowInstanceState.Skipped or FlowInstanceState.NotRun)
        {
            // A done attempt was given its handoff, and a visit that will not run again has no use for one. A failed
            // one keeps it, for the retry that may follow is given it, as a stopped one is at the resume.
            instance.Handoff = null;
        }
        Release(instance);
        instance.Stoppable?.Dispose();
        instance.Stoppable = null;
        DisarmTimeLimit(instance);
        Record(FlowChangeKind.Step, instance, from: was, to: state, attempt: instance.Attempt);

        if (instance.Step.Type == "create-agent" && state == FlowInstanceState.Done)
        {
            _agentsOpen[instance.Id] = instance;
        }

        // Only a deletion releases an open agent's slot: a fold carries its work and leaves it open. The slot released is
        // the one of the agent the deletion worked on, as it stored it - never the agent a later visit of its create-agent
        // step made.
        if (instance.Step.Type == "delete-agent"
            && state == FlowInstanceState.Done
            && WorksInOf(instance) is { } deleted)
        {
            foreach (var holder in _agentsOpen.Where(open => string.Equals(open.Value.Made, deleted, StringComparison.Ordinal)).Select(open => open.Key).ToList())
            {
                _ = _agentsOpen.Remove(holder);
            }
        }

        // A read-only agent's work is over when the last step that can go on with it has ended and the run goes on:
        // deleted here, and the deletions seen to before the session ends. A run that is pausing or stopping leaves
        // every agent live for the resume, and so does a step that failed, whose work the run may yet go on with. A
        // group's end settles its members' work too, so it sweeps as well.
        if (_deleteAgent is not null && instance.Step.Type is "reason" or null)
        {
            foreach (var readOnly in ReadOnlyAgentsOver())
            {
                _deletions.Add(DeleteReadOnlyAsync(readOnly));
            }
        }

        if (instance.DeclaredScope is null)
        {
            return;
        }

        switch (state)
        {
            case FlowInstanceState.Done when _pausing:
                // Nothing is raised while a pause records what it left running: what the ending owes is written owed,
                // and the next session raises it from that record.
                _ = OweHandlers(instance);
                break;
            case FlowInstanceState.Done:
                // A done ending raises its handlers too: every one that fits, though a failure is the usual case.
                _ = RaiseFor(instance);
                break;
            case FlowInstanceState.Skipped or FlowInstanceState.NotRun:
                // A source the flow decided not to run, or never reached, leaves its handlers as it ended: Skipped
                // with it, which satisfies what depends on them, or NotRun, which satisfies nothing.
                HandlersFollow(instance, state);
                break;
        }

        MaybeSettle(instance);

        if (IsRecovery(instance) && state == FlowInstanceState.Done && instance.RaisedBy is { } again)
        {
            // onSuccess: failed-step - the step that gave the recovery its failure runs again from where it stopped,
            // as a new visit with its retries whole; what depended on it was waiting, and starts when it ends. An
            // issue the recovery fixed blocks no one, where it did not block its reporter: the run goes on.
            again.FailureSettled = true;

            if (again.Issue is not null)
            {
                var fixedEntry = _tally.FirstOrDefault(entry => ReferenceEquals(entry.Triage, again));

                if (fixedEntry is not null)
                {
                    _tally.Remove(fixedEntry);
                    _tally.Add(fixedEntry with { State = "fixed" });
                }
            }

            if (RecoveryOf(again).Rerun is { } target && target.DeclaredScope is { } targetScope && ResolveStep(targetScope, target.Step.Name) is { } declaration)
            {
                DecideRedirectOf(instance, targetScope, declaration);
            }
        }

        if (IsRecovery(instance) && state is FlowInstanceState.Done or FlowInstanceState.Failed)
        {
            DrainPendingFixes();
        }
    }

    /// <summary>Gives the recovery each issue a triage answered fix for while a visit of it was already at work: one at a time, for the recovery never calls itself (V50).</summary>
    private void DrainPendingFixes()
    {
        if (_created.Any(atWork => IsRecovery(atWork) && atWork.State is FlowInstanceState.Running or FlowInstanceState.Paused or FlowInstanceState.NotStarted))
        {
            return;
        }

        foreach (var waiting in _pendingFixes.ToList())
        {
            _ = _pendingFixes.Remove(waiting);

            if (waiting.State == FlowInstanceState.Done)
            {
                // Counted being fixed when it ended; now given to the recovery, or waiting again behind it.
                FixOf(waiting);
            }
        }
    }

    /// <summary>The declaration a scope holds under a name, and one around it where none does.</summary>
    private WorkflowStep? ResolveStep(Scope scope, string name)
    {
        for (var at = scope; at is not null; at = at.Parent)
        {
            if (DeclarationsOf(at).FirstOrDefault(declaration => declaration.Name == name) is { } declaration)
            {
                return declaration;
            }
        }

        return null;
    }

    /// <summary>The create-agent step a fold family step's <c>of</c> names, found in its own scope or one around it.</summary>
    private static FlowInstance? CreateStepOf(FlowInstance instance, string made) => DeclaredOf(instance, made, "create-agent");

    /// <summary>The step of <paramref name="type"/> named <paramref name="name"/>, found in <paramref name="instance"/>'s own scope or one around it.</summary>
    private static FlowInstance? DeclaredOf(FlowInstance instance, string name, string type)
    {
        for (var at = instance.DeclaredScope; at is not null; at = at.Parent)
        {
            if (at.Members.TryGetValue(name, out var found) && found.Step.Type == type)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>
    /// The instance whose agent, access and tree <paramref name="instance"/> works with: its own - or, for a reason step that
    /// goes on with another's work, that of the step it resumes, found in its own scope or one around it, through each
    /// resume in turn.
    /// </summary>
    private static FlowInstance WorkingOf(FlowInstance instance)
    {
        var at = instance;
        var seen = new HashSet<FlowInstance>(ReferenceEqualityComparer.Instance);

        while (at.Step is { Type: "reason", Resume: { } resumed } step
            && !string.Equals(resumed, step.Name, StringComparison.Ordinal)
            && seen.Add(at)
            && DeclaredOf(at, resumed, "reason") is { } found)
        {
            at = found;
        }

        return at;
    }

    /// <summary>
    /// The step as its claims are read: its own - a reason step that goes on with another's work with that step's <c>in</c>
    /// and access, which it names none of.
    /// </summary>
    private static WorkflowStep ClaimingStepOf(FlowInstance instance)
        => WorkingOf(instance) is { } working && !ReferenceEquals(working, instance)
            ? instance.Step with { In = working.Step.In, Access = working.Step.Access }
            : instance.Step;

    /// <summary>Gives back every claim <paramref name="instance"/> still holds.</summary>
    private void Release(FlowInstance instance)
    {
        instance.HeldClaims.Clear();
    }

    /// <summary>
    /// Keeps what <paramref name="instance"/>'s attempt claimed held beyond the attempt's end, until its runner - which did
    /// not end within the drain's bound of its stop - answers (<see cref="FlowInstance.Lingering"/>).
    /// </summary>
    private static void Linger(FlowInstance instance)
    {
        if (instance.HeldClaims.Count > 0)
        {
            instance.Lingering.Add((instance.Attempt, [.. instance.HeldClaims]));
            instance.HeldClaims.Clear();
        }
    }

    /// <summary>
    /// Gives back what the attempt <paramref name="message"/> answers for kept held past its end, where it is that
    /// attempt's runner's answer: that runner has ended, and writes nothing more. What another attempt's runner still
    /// holds stays held. Whether it was such an answer.
    /// </summary>
    private static bool Outlived(Message message)
    {
        if (message is not { Result: not null, Instance: { } instance })
        {
            return false;
        }

        return instance.Lingering.RemoveAll(lingering => lingering.Attempt == message.Attempt) > 0;
    }

    /// <summary>
    /// The limit of the run that leaves no room for <paramref name="instance"/> beside <paramref name="running"/>, as its
    /// record names it - reasoning agents, parallel processes, open agents - or null where every limit it counts against
    /// leaves room: all-or-nothing, so a step short of one of its resources starts with none of them.
    /// </summary>
    private string? CounterFull(FlowInstance instance, IReadOnlyList<FlowInstance> running)
        => instance.Step.Type switch
        {
            "reason" when running.Count(step => step.Step.Type == "reason") >= Limit().MaxReasoningAgents => "reasoning agents",
            "build" or "test" or "run" or "check-mutations"
                when running.Count(step => step.Step.Type is "build" or "test" or "run" or "check-mutations") >= Limit().MaxParallelProcesses => "parallel processes",
            "create-agent" when running.Count(step => step.Step.Type == "create-agent") + _agentsOpen.Count >= Limit().MaxOpenAgents => "open agents",
            _ => null,
        };

    /// <summary>Starts the always steps of the run, once, where the rest is quiescent in any state; a session runs them after everything else has ended.</summary>
    private bool TryEnterAlwaysPhase()
    {
        if (_alwaysPhase)
        {
            return false;
        }

        // What the phase starts: the always steps not drawn yet where their scope draws them, and those carried into
        // this session waiting to start.
        var pending = _scopes
            .SelectMany(scope => DeclarationsOf(scope).Where(declaration => !scope.Instantiated.Contains(declaration.Name)).Select(declaration => (scope, declaration)))
            .Count(entry => IsAlways(entry.declaration, entry.scope) && Draws(entry.scope, entry.declaration))
            + _created.Count(instance => instance.IsAlways && instance.State is FlowInstanceState.NotStarted);

        if (pending == 0)
        {
            return false;
        }

        _alwaysPhase = true;
        return true;
    }

    /// <summary>
    /// Fails every step still waiting to start where the session can do nothing more - nothing runs, nothing is due,
    /// nothing more can be drawn, no question is open and no pause or stop keeps it for the resume: what it waits on is
    /// held by nothing that will let it go (an open agent no deletion will close), so it never could. It fails refused,
    /// saying what it waits on, and its own <c>onFailure</c> decides as for any failure - a question under
    /// <c>pause</c>, the run ended failed under <c>stop</c> - never a session that ends done over a step that never ran.
    /// Answers whether it failed any.
    /// </summary>
    private bool FailStalled()
    {
        if (_hardStop || _pausing || _open.Count > 0 || _created.Any(instance => instance.WaitingQuestion is not null))
        {
            return false;
        }

        var stalled = AdmissionWalk().Where(decision => !decision.Starts).ToList();

        foreach (var (instance, _, waitsOn) in stalled)
        {
            if (instance.State is not FlowInstanceState.NotStarted)
            {
                continue;
            }

            End(instance, FlowInstanceState.Failed, HarnessExit.Refused, $"it could not start: it waits on {string.Join(", ", waitsOn)}, and nothing left in the run frees it");
            HandleFailure(instance);
        }

        return stalled.Count > 0;
    }

    /// <summary>
    /// Ends the session, and says the run's status with its failures, its passed-over steps, and the pause or stop that
    /// ended it. What never started is left as it is - for the question it waits on, the pause's resume or the stop's -
    /// and a session that left a step unended is never said done.
    /// </summary>
    private FlowSessionEnd Finish()
    {
        if (_pause is { Cause: "budget" } && !_hardStop && OpenBudgetQuestion is null)
        {
            // The budget's pause has ended: the run holds its question, once, which a person settles - more allows each
            // amount once again, abort ends the run. The workflow's own answers were asked at the reserve, and gave
            // none.
            var reached = ReachedOf(_definition.Budget!);
            var question = BudgetQuestion(reached ?? ("usd", "its budget"));
            _questions.Add(question);
            WaitOn(null, question);
            WriteQuestions();
        }

        var waiting = _created
            .Where(instance => instance.WaitingQuestion is not null)
            .Select(instance => instance.WaitingQuestion!)
            .ToList();

        var open = OpenQuestions;
        var passedOver = Standing(FlowFailure.PassedOver);
        var failed = Standing(FlowFailure.EndedRun);
        var handled = Handled;
        var status = _hardStop ? FlowRunStatus.Stopped
            : _pausing ? FlowRunStatus.Paused
            : _stopping || failed.Count > 0 ? FlowRunStatus.Failed
            : open.Count > 0 || waiting.Count > 0 ? FlowRunStatus.Paused
            : FlowRunStatus.Done;

        // Done says everything ended as asked: a step left unended - never started, still open, paused on nothing, or
        // never even drawn though its scope still draws - makes the session failed, and says which, never done over
        // work that never ran. A handler is drawn by its source's ending and a triage step by an issue, never by the
        // schedule, so neither is owed a drawing.
        var unended = status == FlowRunStatus.Done
            ? [.. _created.Where(instance => instance.State is FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Paused).Select(instance => instance.Id),
                .. _scopes.SelectMany(scope => DeclarationsOf(scope)
                    .Where(declaration => declaration.On is null && declaration.Raised is null && !scope.Instantiated.Contains(declaration.Name) && Draws(scope, declaration))
                    .Select(declaration => MemberId(scope, declaration)))]
            : new List<string>();

        if (unended.Count > 0)
        {
            status = FlowRunStatus.Failed;
        }

        var summary = status switch
        {
            FlowRunStatus.Stopped => "stopped",
            FlowRunStatus.Paused when _pausing => PausedSummary(open),
            FlowRunStatus.Done when passedOver.Count > 0 && handled.Count > 0 => $"done; {HandledLine()}, and {(passedOver.Count == 1 ? "1 step failed and was passed over" : $"{passedOver.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} steps failed and were passed over")}: {string.Join(", ", passedOver.Select(instance => instance.Id))}",
            FlowRunStatus.Done when passedOver.Count > 0 => passedOver.Count == 1
                ? $"done; 1 step failed and was passed over: {passedOver[0].Id}"
                : $"done; {passedOver.Count} steps failed and were passed over: {string.Join(", ", passedOver.Select(instance => instance.Id))}",
            FlowRunStatus.Done when handled.Count > 0 => $"done; {HandledLine()}",
            FlowRunStatus.Done => "done",
            FlowRunStatus.Failed when unended.Count > 0 => $"failed; the session ended with steps that never ended: {string.Join(", ", unended)}",
            FlowRunStatus.Failed => failed.Count == 1
                ? $"failed; 1 step failed: {failed[0].Id}"
                : $"failed; {failed.Count} steps failed: {string.Join(", ", failed.Select(instance => instance.Id))}",
            _ => $"paused; waiting on {string.Join(", ", open.Count > 0 ? open.Select(question => question.Id) : waiting)}",
        };

        _endedAt = _clock.GetUtcNow();
        Record(FlowChangeKind.SessionEnded, detail: summary);

        return new FlowSessionEnd(
            status,
            [.. passedOver.Select(instance => instance.Id)],
            [.. failed.Select(instance => instance.Id)],
            open,
            summary,
            _pause);

        string PausedSummary(IReadOnlyList<FlowQuestion> openQuestions)
        {
            var handoffs = _pause?._handoffs.Count ?? 0;
            var head = handoffs == 0 ? "paused" : handoffs == 1 ? "paused; 1 handoff written" : $"paused; {handoffs} handoffs written";
            return openQuestions.Count > 0 ? $"{head}, waiting on {string.Join(", ", openQuestions.Select(question => question.Id))}" : head;
        }

        string HandledLine()
            => handled.Count == 1
                ? $"1 failure was handled: {handled[0].Step.Name}, by {handled[0].HandledBy}"
                : $"{handled.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)} failures were handled: {string.Join(", ", handled.Select(instance => $"{instance.Step.Name}, by {instance.HandledBy}"))}";
    }

    /// <summary>Waits for the next thing the run learns - a completion, an event of a leg, a retry's time coming, or the budget's tick - and takes it. One read of the channel is kept open across the waits a retry's turn takes, so no message is read by a wait that no longer stands.</summary>
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
                TakeMessage(_pending.Result);
                _pending = null;
                return;
            }

            _pending = _messages.Reader.ReadAsync(cancellationToken).AsTask();
        }

        // The minutes a reason step runs count while it runs: the run wakes when those running now would bring the
        // budget to its reserve, so a long one pauses the run there and not only when its attempt ends.
        using var tickEnds = new CancellationTokenSource();
        var tick = MinutesToReserve() is { } due ? Task.Delay(due, _clock, tickEnds.Token) : null;

        try
        {
            if (_wakes.Count == 0 && tick is null)
            {
                TakeMessage(await _pending.ConfigureAwait(false));
                _pending = null;
                return;
            }

            var earliest = _wakes.OrderBy(wake => wake.At).FirstOrDefault();
            var waits = new List<Task>(3) { _pending };

            if (earliest is not null)
            {
                waits.Add(earliest.Delay);
            }

            if (tick is not null)
            {
                waits.Add(tick);
            }

            var done = await Task.WhenAny(waits).ConfigureAwait(false);

            if (done == tick)
            {
                // The loop goes round, and reads the budget as it stands now.
                return;
            }

            if (done != _pending)
            {
                Woken(earliest!);
                return;
            }
        }
        finally
        {
            tickEnds.Cancel();
        }

        // Awaited, not read: a read the session's token cancelled ends the session cancelled, as every wait does.
        TakeMessage(await _pending.ConfigureAwait(false));
        _pending = null;

        void Woken(Wake earliest)
        {
            _wakes.Remove(earliest);

            if (earliest.Drain)
            {
                // Its runner did not end within the drain's bound, and may still write: what the attempt claimed stays
                // held until the runner answers, so nothing that would conflict with it starts meanwhile.
                Linger(earliest.Instance);

                if (earliest.Instance.PastTimeLimit && !earliest.Instance.Draining)
                {
                    var error = $"{PastTimeLimitError(earliest.Instance)}, and {RunnerNotEnded}";

                    if (earliest.Instance.Step.Others is not null)
                    {
                        // A step of others: the target whose turn it was fails, never run again beside a runner still
                        // going, and the step goes on with the next - each target is ended on its own.
                        EndTarget(earliest.Instance, passed: false, HarnessExit.Incomplete, error);
                        return;
                    }

                    // A step stopped for its time limit whose runner did not end within the drain's bound: it fails
                    // all the same, saying so, and is not retried beside a runner still going.
                    End(earliest.Instance, FlowInstanceState.Failed, HarnessExit.Incomplete, error);
                    AfterFailure(earliest.Instance);
                    return;
                }

                // A stopped step's runner did not end within the drain's bound: it is recorded stopped all the same,
                // saying so, and what it answers later says nothing of the run.
                End(earliest.Instance, FlowInstanceState.Stopped, null, RunnerNotEnded);
                return;
            }

            NewAttempt(earliest.Instance);
        }
    }

    /// <summary>
    /// Reads every message already waiting for the loop - what an earlier read holds, then what the channel holds - and
    /// hands each to <paramref name="take"/>, waiting on nothing. A read still armed takes the channel's next message
    /// as it comes, so the channel holds none meanwhile and is left to it.
    /// </summary>
    private void EachQueued(Action<Message> take)
    {
        if (_pending is { IsCompleted: true } read)
        {
            _pending = null;

            if (read.IsCompletedSuccessfully)
            {
                take(read.Result);
            }
        }

        while (_pending is null && _messages.Reader.TryRead(out var message))
        {
            take(message);
        }
    }

    /// <summary>
    /// What reached the loop after its last read, as its session ends however it ends: an answer offered is handed back
    /// cancelled - never refused, for it was never weighed, and it stays in the inbox for whoever takes it next - and
    /// what a reason step spent is counted, for it was spent. What else was left speaks of an attempt that is over, save
    /// that the answer of a runner that outlived its attempt gives back what that attempt kept held. An answer offered
    /// after this is the next session's, taken at its start.
    /// </summary>
    private void HandBackWhatIsLeft()
    {
        EachQueued(message =>
        {
            if (message.Offered is { } offered)
            {
                _ = offered.Completion.TrySetCanceled();
            }
            else if (message.Usage is not null)
            {
                TakeMessage(message);
            }
            else
            {
                // A runner that outlived its attempt has ended: what it held is given back, for the next session.
                _ = Outlived(message);
            }
        });
    }

    /// <summary>Takes one message of the channel, as the loop alone does: an answer offered, what a step spent, or what a running attempt says.</summary>
    private void TakeMessage(Message message)
    {
        if (message.Offered is { } offered)
        {
            // An answer from outside the session - taken from the run's inbox while the engine runs. The loop
            // alone applies it, for the graph is the loop's alone to change - and settles it only once what became
            // of it is written: at once, or after the writes of a session's start it was weighed in; a start refused
            // writes none of them, and hands it back.
            var handed = AnswerHandedOver(offered.Question, offered.Answer, offered.Text, out _, offered.Through, offered.Inbox);
            Put(_ => offered.Completion.TrySetResult(handed), dropped: () => offered.Completion.TrySetCanceled());
            return;
        }

        if (message.Instance is not null && message.Usage is { } usage)
        {
            // What a reason step's conversation spent as it ran, counted against the run's budget the moment it
            // is reported - an attempt that is over among them, for what it spent was spent.
            _spent.Usd += usage.Usd;
            _spent.Tokens += usage.Tokens;
            return;
        }

        // The answer of a runner that outlived its attempt gives back what that attempt kept held, whatever attempt the run
        // is on now.
        _ = Outlived(message);

        if (message.Instance is null || message.Attempt != message.Instance.Attempt)
        {
            // A message of an attempt that is over - the step was stopped and runs again, or was answered - says
            // nothing about the attempt the run is on now.
            return;
        }

        if (message.Result is { } result)
        {
            _ = _wakes.RemoveAll(wake => ReferenceEquals(wake.Instance, message.Instance));
            DisarmTimeLimit(message.Instance);
            Complete(message.Instance, result);
        }
        else if (message.TimeLimit is { } limit)
        {
            // Only the timer of the run in flight speaks: one of a run that answered already was disarmed with it.
            if (ReferenceEquals(limit, message.Instance.TimeLimit))
            {
                PassTimeLimit(message.Instance);
            }
        }
        else if (message.HandoffTaken)
        {
            TakeHandoff(message.Instance);
        }
        else if (message.Leg is { } leg)
        {
            TakeEvent(message.Instance, leg);
        }
    }

    /// <summary>
    /// The runner says the prompt that holds this attempt's handoff is written: the handoff is spent, and the step's
    /// record says it holds none - so its file goes only once the prompt is on disk.
    /// </summary>
    private void TakeHandoff(FlowInstance instance)
    {
        if (instance.State is not FlowInstanceState.Running || instance.Handoff is null)
        {
            return;
        }

        instance.Handoff = null;
        Record(FlowChangeKind.Step, instance, from: instance.State, to: instance.State, attempt: instance.Attempt, detail: "its handoff is in its prompt");
    }

    /// <summary>Reads one event of a running step's legs: where each works, that every one is placed, when each copy is made, when each one's work in the tree itself has ended.</summary>
    private void TakeEvent(FlowInstance instance, FlowLegEvent leg)
    {
        if (instance.State is not FlowInstanceState.Running)
        {
            return;
        }

        switch (leg.Kind)
        {
            case FlowLegEventKind.PlacedInACopy:
                _ = instance.LegsInCopies.Add(leg.Leg!);
                break;
            case FlowLegEventKind.PlacedInTheTree:
                _ = instance.LegsInTree.Add(leg.Leg!);
                break;
            case FlowLegEventKind.EveryLegPlaced:
                instance.EveryLegPlaced = true;
                break;
            case FlowLegEventKind.CopyMade:
                _ = instance.CopiesMade.Add(leg.Leg!);
                break;
            case FlowLegEventKind.Ended:
                _ = instance.LegsEnded.Add(leg.Leg!);
                break;
        }

        // The tree is let go once the runner has said every leg is placed, every leg that works in a copy has its
        // copy made - or ended without one, its sync refused or failed, reading the tree no more - and every leg that
        // works in the tree itself has ended; and not before: a leg placed later may work in the tree itself. A write
        // the step holds on its tree is never let go here: its runner writes the tree its legs work in.
        if (instance.EveryLegPlaced
            && instance.LegsInCopies.All(copied => instance.CopiesMade.Contains(copied) || instance.LegsEnded.Contains(copied))
            && instance.LegsInTree.All(instance.LegsEnded.Contains)
            && instance.HeldClaims.RemoveAll(claim => claim.Kind == FlowClaimKind.Run) > 0)
        {
            instance.Copied = true;
            Record(FlowChangeKind.Step, instance, from: instance.State, to: instance.State, attempt: instance.Attempt, detail: "every copy its legs work in is made and every leg in its tree has ended: the tree is let go");
        }
    }

    /// <summary>
    /// Creates one instance of <paramref name="declaration"/> in <paramref name="scope"/>: a member of the scope - its
    /// id carrying its visit, so a name resolves to the newest - or, where <paramref name="scopeId"/> names it, the
    /// instance a scope of a loop is.
    /// </summary>
    /// <param name="scope">The scope the instance lives in.</param>
    /// <param name="declaration">The declaration the instance is one of.</param>
    /// <param name="state">How the instance begins.</param>
    /// <param name="scopeId">The id of the instance a loop's item or iteration scope is; null for a member.</param>
    /// <param name="memberId">The id of a member a resume holds, visits spelled as the record spells them; null where this visit is new.</param>
    /// <param name="made">
    /// What the instance is made with beyond its declaration - what raised it, why it was skipped or not run, its code -
    /// set before its first record is written, so no crash leaves a record of it without them.
    /// </param>
    private FlowInstance Create(Scope scope, WorkflowStep declaration, FlowInstanceState state, string? scopeId = null, string? memberId = null, Action<FlowInstance>? made = null)
    {
        var member = scopeId is null;
        var name = member ? declaration.Name : scopeId!.Split(['[', '#', '+'], 2)[0];

        // A visit after the first is +n after the name, so every id of a step the run was never sent back to is what
        // it would be without visits. A member a resume holds carries its visit in its id; one made now takes the next.
        var visit = memberId is null
            ? scope.Visits.GetValueOrDefault(name) + 1
            : FlowInstanceId.VisitOf(memberId);

        var instance = new FlowInstance(
            scopeId ?? memberId ?? (visit == 1 ? MemberId(scope, declaration) : $"{MemberId(scope, declaration)}+{visit}"),
            declaration,
            PathOf(scope, declaration))
        {
            State = state,
            Visit = visit,

            // One drawn ended - skipped, or failed for an unreadable when - ended as it was drawn; one drawn not run is
            // ended by NotRun, and a resume gives back what its record says.
            FinishedAt = state is FlowInstanceState.Skipped or FlowInstanceState.Failed ? _clock.GetUtcNow() : null,
            Failure = member && state is FlowInstanceState.Failed && !_replaying ? FlowFailure.Owed : null,
            DeclaredScope = member ? scope : null,
            GroupOf = member && scope.Group is not null ? GroupInstanceIn(scope) : null,
            IsAlways = IsAlways(declaration, scope),
            Item = scope.Item,
            Index = scope.Index,
            Iteration = scope.Iteration,
        };

        _instances.Add(instance.Id, instance);
        _created.Add(instance);

        scope.Visits[name] = member ? visit : Math.Max(scope.Visits.GetValueOrDefault(name), visit);

        if (member)
        {
            scope.Members[declaration.Name] = instance;
            scope.Instantiated.Add(declaration.Name);
        }
        else
        {
            scope.GroupInstance = instance;
        }

        if (member && memberId is null)
        {
            // The agent it works in, where its create-agent has made one: stored before its first record, so its record and
            // its directory never move to another agent a later visit of that create-agent makes. A resume reads it back.
            instance.WorksIn = NamedAgentOf(instance);
        }

        made?.Invoke(instance);
        Record(FlowChangeKind.Step, instance, to: state, attempt: instance.Attempt);

        return instance;
    }

    /// <summary>Draws <paramref name="declaration"/> in <paramref name="scope"/> NotRun, its first record saying why and of which kind.</summary>
    private void CreateNotRun(Scope scope, WorkflowStep declaration, FlowNotRunCause cause, string because)
        => NotRun(
            Create(scope, declaration, FlowInstanceState.NotRun, made: notRun =>
            {
                notRun.NotRunCause = cause;
                notRun.NotRunBecause = because;
            }),
            cause,
            because);

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
    /// The dependencies of <paramref name="declaration"/>: the ones it still waits on - none where every one is
    /// satisfied - or that one can never be satisfied - unless the declaration is an always step, which runs even where
    /// its dependencies did not reach it. The one owner of what a dependency's state means to what depends on it: what
    /// is drawn reads it, and so does what admission says a step waits on.
    /// </summary>
    private static (IReadOnlyList<string> Waiting, bool Unreachable) WaitsOf(WorkflowStep declaration, Scope scope, bool always)
    {
        var waiting = new List<string>();

        foreach (var name in declaration.DependsOn)
        {
            if (!scope.Members.TryGetValue(name, out var dependency))
            {
                waiting.Add(name);
                continue;
            }

            if (dependency.WaitingQuestion is not null)
            {
                // A question of its ending is open - its handler at its bound, the recovery at its bound: what depends
                // on it waits for the answer, which may change how it ended.
                waiting.Add(name);
                continue;
            }

            switch (dependency.State)
            {
                case FlowInstanceState.Done or FlowInstanceState.Skipped:
                    break;
                case FlowInstanceState.Failed when dependency.PassedOver || dependency.HandledBy is not null:
                    break;
                case FlowInstanceState.Failed or FlowInstanceState.NotRun when always:
                    break;
                case FlowInstanceState.Failed when !dependency.FailureSettled:
                    // A failure a handler or the recovery may still take blocks what depends on it: it waits, for
                    // what took it may send the run back past the failure altogether.
                    waiting.Add(name);
                    break;
                case FlowInstanceState.Failed or FlowInstanceState.NotRun:
                    return ([], true);
                default:
                    waiting.Add(name);
                    break;
            }
        }

        return (waiting, false);
    }

    /// <summary>
    /// Whether every declaration of <paramref name="scope"/> has been instantiated and every one of its members has
    /// ended, with no question of its ending open and no redirect owed that will reset a step of it: that work is to
    /// be done again. A redirect that resets the group itself lets this visit end as it stands, and draws a new one.
    /// A member a failure's <c>stop</c> left unfinished - the failure that ended the run, which the next session runs
    /// again, and a step NotRun because the run ended before it - has not ended its work: the group stands open over
    /// it, as over a stopped one, and is never ended done over work the next session does. A member that runs after
    /// the group's ending (<see cref="RunsAfterItsGroup"/>) and has not started - not drawn yet, or drawn and waiting -
    /// is not waited on: it runs once the rest of the run is quiescent, and the group's ending is part of that rest -
    /// its failure decided and what depends on it run before any always step starts. The visit's scope still draws it
    /// then (<see cref="Draws"/>). An always group's members are waited on as any group's are.
    /// </summary>
    private bool ScopeEnded(Scope scope)
        => !_redirects.Any(redirect => redirect.Set.Any(entry => ReferenceEquals(entry.Scope, scope)))
            && DeclarationsOf(scope).All(declaration => scope.Instantiated.Contains(declaration.Name) || RunsAfterItsGroup(declaration, scope))
            && scope.Members.Values.All(member => member.State is FlowInstanceState.NotStarted && RunsAfterItsGroup(member.Step, scope)
                || member.WaitingQuestion is null
                    && member.State is not (FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Paused or FlowInstanceState.Stopped)
                    && member.Failure is not FlowFailure.EndedRun
                    && member.NotRunCause is not FlowNotRunCause.RunEnded);

    /// <summary>
    /// Every scope under a group, at every level: its own (<see cref="ScopesOf"/>), then those of each group among their
    /// members, and so down - what a redirect that resets the group freezes, waits on and leaves.
    /// </summary>
    private static IReadOnlyList<Scope> ScopesUnder(FlowInstance group)
    {
        var scopes = new List<Scope>();

        foreach (var scope in ScopesOf(group))
        {
            scopes.Add(scope);

            foreach (var inner in scope.Members.Values.Where(member => member.Step.IsGroup))
            {
                scopes.AddRange(ScopesUnder(inner));
            }
        }

        return scopes;
    }

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

    /// <summary>
    /// What a path's root yields, read in <paramref name="scope"/>: a step's output or one of its facts, an input, a
    /// param, one of the run's own names, an item, an iteration, a previous iteration's members, and - inside a handler
    /// and inside every step of a handler that is a group, and inside the recovery - the source, the step whose ending
    /// raised what is being read for. The names after the root the tree walks itself.
    /// </summary>
    /// <param name="path">The path being read.</param>
    /// <param name="scope">The scope the read is made in.</param>
    /// <param name="source">The ending a handler's when reads as <c>source</c>, where the read is of a step it would answer; null for what raised the reader or the group around it.</param>
    /// <param name="reader">The instance the read is made for, where one is: whose template is being filled, or whose when is read again.</param>
    /// <param name="drawing">The visit a declaration being drawn will be, where its when is read as it is drawn.</param>
    /// <returns>
    /// What the root yields - or, for a step's name, what the step the read names yields, a group's member walked down
    /// to by the names after the group's - and how many of the path's steps that walk consumed: the tree walks the rest.
    /// </returns>
    private FlowExpression.PathRead Read(FlowExpression.Path path, Scope scope, FlowInstance? source = null, FlowInstance? reader = null, int? drawing = null)
    {
        // The ending a read means by source: the one it is given - a handler's when, read of the step it would answer -
        // or what raised the reader, or the raised group around it.
        source ??= RaisedSourceIn(scope, reader);

        switch (path.Root)
        {
            case "input":
                return new(FlowValue.Of(Inputs()), 0);
            case "param":
                return new(FlowValue.Of(Params()), 0);
            case "item":
                return new(scope.Item, 0);
            case "index":
                return new(scope.Index is { } index ? FlowValue.Of((long)index) : FlowValue.Missing, 0);
            case "iteration":
                return new(scope.Iteration is { } iteration ? FlowValue.Of((long)iteration) : FlowValue.Missing, 0);
            case "visit":
                // A raised step's own visit; inside a handler that is a group, or a visit of the recovery, that visit;
                // else the reader's, or - read in a when as it is drawn - the visit it will be.
                return new(FlowValue.Of((long)(reader is { RaisedBy: not null } ? reader.Visit : RaisedGroupOf(scope)?.Visit ?? reader?.Visit ?? drawing ?? 1)), 0);
            case "issue":
            {
                // The issue a raised instance or the recovery over it triages: the item itself, as a read sees it.
                var item = reader?.Issue ?? source?.Issue;

                return new(
                    item is { } found
                        ? FlowValue.Of(new JsonObject
                        {
                            ["title"] = found.Title,
                            ["description"] = found.Description,
                            ["where"] = found.Where,
                            ["evidence"] = found.Evidence,
                            ["blocks"] = found.Blocks,
                        })
                        : FlowValue.Missing,
                    0);
            }
            case "previous":
                // The iteration before this one, read by the names after 'previous' as a step's name is read: its step,
                // a group's member walked down to, from where that iteration stands.
                return PreviousOf(scope) is { } before ? StepRead(path, keysFrom: 0, scope, before) : new(FlowValue.Missing, 0);
            case "run":
                return new(FlowValue.Of(RunValues()), 0);
            case "carried":
                // What one commit carries: read in a commit step's message and variables alone, which the validator holds it to.
                return new(reader is { Step.Type: "commit" } ? FlowValue.Of(CarriedOf(reader)) : FlowValue.Missing, 0);
            case "source":
                return new(source is null ? FlowValue.Missing : FlowValue.Of(SourceFacts(source, RaisedGroupOf(scope))), 0);
            default:
                return StepRead(path, keysFrom: -1, scope, scope);
        }
    }

    /// <summary>
    /// What a read that names a step yields: the step found as <see cref="FlowScope.Read"/> finds it from where the read
    /// is made - each name after it that names a member of a group going down into that member - and the instance of it
    /// the run means, located from <paramref name="at"/>; with how many of the path's steps the names that went down
    /// consumed. A path that indexes a step before it names a member reads nothing.
    /// </summary>
    /// <param name="path">The path being read.</param>
    /// <param name="keysFrom">Where the step's own name is: -1 for the root, or the index of the path's step that holds it (<c>previous</c>'s).</param>
    /// <param name="scope">The scope the read is made in, where its names are looked up.</param>
    /// <param name="at">The scope the instance is located from: the reader's, or the iteration before it.</param>
    private FlowExpression.PathRead StepRead(FlowExpression.Path path, int keysFrom, Scope scope, Scope at)
    {
        var leading = path.Steps.Skip(keysFrom + 1).TakeWhile(step => step.IsKey).Select(step => step.Key!).ToList();
        IReadOnlyList<string> names = keysFrom < 0 ? [path.Root, .. leading] : [path.Steps[keysFrom].Key!, .. leading];

        if (keysFrom >= 0 && !path.Steps[keysFrom].IsKey)
        {
            return new(FlowValue.Missing, 0);
        }

        if (FlowScope.Read(names, _definition, scope.GroupPath.Length == 0 ? null : scope.GroupPath, out _) is not { } located)
        {
            return new(FlowValue.Missing, 0);
        }

        // The step's own name and each member name the walk went down by are consumed; the names after them are its
        // output's. A root is no step of the path, and previous's step name is its first.
        var walked = keysFrom + (names.Count - located.After.Count);
        return new(ValueOf(Locate(located.Step.Path, at), path.Steps.Count == walked), walked);
    }

    /// <summary>
    /// The facts a handler or a recovery reads of its source: its output beside the engine's own facts of how it ended
    /// - outcome, code, error, question - and, for a recovery, the step as the report names it and the fix a person
    /// gave where its bound was answered direct - for the visit it was given to, and empty on every other.
    /// </summary>
    private static JsonObject SourceFacts(FlowInstance source, FlowInstance? raised)
    {
        var facts = new JsonObject();

        if (source.Output is JsonObject output && !source.PassedOver)
        {
            foreach (var (name, value) in output)
            {
                facts[name] = value?.DeepClone();
            }
        }

        facts["outcome"] = source.State switch
        {
            FlowInstanceState.Done => "done",
            FlowInstanceState.Failed => "failed",
            FlowInstanceState.Skipped => "skipped",
            FlowInstanceState.Stopped => "stopped",
            FlowInstanceState.NotRun => "notRun",
            _ => null,
        };
        facts["code"] = source.Exit;
        facts["error"] = source.Error;
        facts["question"] = source.EndedByQuestion;
        facts["step"] = source.Id;
        // The fix a person gave is the visit's of the recovery it was given to, and that visit's alone.
        facts["direction"] = raised?.Direction ?? string.Empty;

        return facts;
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
            FlowInstanceState.Stopped => "stopped",
            FlowInstanceState.NotRun => "notRun",
            _ => null,
        };
        merged["code"] = instance.Exit;
        merged["error"] = instance.Error;
        merged["question"] = instance.EndedByQuestion;
        merged["assumptions"] = AssumptionsOf(instance);

        // The newest output of the work, as its output is read: a step whose failure was passed over left nothing it
        // is read by, so its latest is Missing too.
        if (!instance.PassedOver)
        {
            merged["latest"] = instance.Output?.DeepClone();
        }

        return FlowValue.Of(merged);
    }

    /// <summary>
    /// What <paramref name="instance"/>'s agents assumed, as a step's <c>assumptions</c> fact reads it: the envelope's
    /// own shape of each - what was open, what it took, and why - of every attempt of the visit.
    /// </summary>
    private static JsonArray AssumptionsOf(FlowInstance instance)
    {
        var assumed = new JsonArray();

        foreach (var one in instance.Assumptions)
        {
            assumed.Add(new JsonObject { ["about"] = one.About, ["assumed"] = one.Assumed, ["why"] = one.Why });
        }

        return assumed;
    }

    /// <summary>The scope before this one in its repeat's iterations, as <c>previous</c> reads it.</summary>
    private Scope? PreviousOf(Scope scope)    {
        for (var at = scope; at is not null; at = at.Parent)
        {
            if (at.Iteration is { } iteration && at.LoopOf is { } loop)
            {
                return iteration > 1 ? loop.Children[iteration - 2] : null;
            }
        }

        return null;
    }

    /// <summary>
    /// The run's own values, as <c>run</c> reads them, said without its always steps: only they read these, so none
    /// reads its own effect. The run is failed where a failure ended it - one its policy stopped the run on - and done
    /// where every step that failed was passed over or handled. Its texts of what it checked (<see cref="FlowChecks"/>)
    /// are of every check of the run so far, its always steps among them.
    /// </summary>
    private JsonObject RunValues()
    {
        var checks = _created.Where(instance => FlowChecks.IsCheck(instance.Step)).ToList();

        var main = _created.Where(instance => !instance.IsAlways).ToList();
        var failed = Standing(FlowFailure.EndedRun).Any(instance => !instance.IsAlways);
        var paused = _open.Count > 0 || main.Any(instance => instance.State is FlowInstanceState.Paused);
        var leftover = _created
            .Where(instance => instance.Step.Repeat?.Leftover is not null && instance.Leftover is not null)
            .Select(instance => $"'{instance.Id}' left {FlowValue.Of(instance.Leftover!.DeepClone()).AsTemplateText()}")
            .ToList();
        var preExisting = _tally.Select(entry => $"{entry.State} - {entry.Line}").ToList();
        var assumed = _created
            .Where(instance => instance.Assumptions is [_, ..])
            .SelectMany(instance => instance.Assumptions.Select(one => $"'{instance.Step.Name}' assumed about {one.About}: {one.Assumed} ({one.Why})"))
            .ToList();

        return new JsonObject
        {
            ["name"] = _runName ?? string.Empty,
            ["status"] = failed ? "failed" : paused ? "paused" : "done",
            ["done"] = !failed && !paused,
            ["failed"] = failed,
            ["paused"] = paused,
            ["stopped"] = _stopping,
            ["passedOver"] = main.Count(instance => instance.PassedOver),
            ["leftover"] = leftover.Count > 0 ? string.Join("; ", leftover) : FlowChecks.None,
            ["preExisting"] = preExisting.Count > 0 ? string.Join("; ", preExisting) : FlowChecks.None,
            // What the run's agents assumed without asking, of every step that did: a person reads it and corrects it.
            ["assumptions"] = assumed.Count > 0 ? string.Join("; ", assumed) : FlowChecks.None,
            ["ran"] = FlowChecks.Ran(checks.Select(CheckOf)),
            ["notRun"] = FlowChecks.NotRun(checks.Select(CheckOf), PendingChecks(path => true, checks), passedOver: false),
            ["red"] = FlowChecks.Red(checks.Select(CheckOf)),
        };
    }

    /// <summary>
    /// What the commit <paramref name="commit"/> carries, as <c>carried</c> reads it: the texts of the checks upstream of
    /// it - its ancestors in the flow, every visit and iteration of them, and never a sibling's - that ended after the
    /// newest commit upstream of it that was done (<see cref="EndedAfter"/>) - so a later visit of a check, drawn again by
    /// a redirect, is carried by the commit's later visit - or since the run's start where none was; with each check
    /// upstream of it the run never drew, among what did not check.
    /// </summary>
    private JsonObject CarriedOf(FlowInstance commit)
    {
        var upstream = _created.Where(other => !ReferenceEquals(other, commit) && Upstream(other, commit)).ToList();

        var before = upstream.Where(other => other.Step.Type == "commit" && other.State == FlowInstanceState.Done).Aggregate((FlowInstance?)null, (newest, other) => newest is null || EndedAfter(other, newest) ? other : newest);
        var checks = upstream
            .Where(other => FlowChecks.IsCheck(other.Step) && (before is null || EndedAfter(other, before)))
            .ToList();

        return new JsonObject
        {
            ["ran"] = FlowChecks.Ran(checks.Select(CheckOf)),
            ["notRun"] = FlowChecks.NotRun(checks.Select(CheckOf), PendingChecks(path => FlowGraph.Waits(_definition, commit.Path, path), upstream), passedOver: true),
            ["red"] = FlowChecks.Red(checks.Select(CheckOf)),
        };
    }

    /// <summary>
    /// Whether <paramref name="one"/> ended after <paramref name="other"/>: later, or at the same time and made later - what
    /// an instance waits for is made before it, so of two that ended at once the one waited for comes first. One that has
    /// not ended is after none that has.
    /// </summary>
    private bool EndedAfter(FlowInstance one, FlowInstance other)
        => one.FinishedAt > other.FinishedAt || (one.FinishedAt == other.FinishedAt && _created.IndexOf(one) > _created.IndexOf(other));

    /// <summary>
    /// Whether <paramref name="target"/> is upstream of <paramref name="of"/>: an earlier visit or iteration of a group
    /// both sit in holds it, or - in the same group instances - <paramref name="of"/>'s declaration waits for its own
    /// (<see cref="FlowInstanceId.Precedes"/>, then <see cref="FlowGraph.Waits"/>). Another item of a <c>forEach</c> is a
    /// sibling, never upstream.
    /// </summary>
    private bool Upstream(FlowInstance target, FlowInstance of)
        => FlowInstanceId.Precedes(target.Id, of.Id) ?? FlowGraph.Waits(_definition, of.Path, target.Path);

    /// <summary>One instance of a check, as <see cref="FlowChecks"/> reads it.</summary>
    private FlowCheck CheckOf(FlowInstance instance)
    {
        // An engine-made agent is a read-only one, with no worktree: its step ran in the main tree, reading it, and
        // the line says so - never that it ran in a worktree there is none of.
        var worksIn = WorksInOf(instance) is { } named
            && _created.FirstOrDefault(made => made.Made is { } madeName && string.Equals(madeName, named, StringComparison.Ordinal)) is not { Step.Type: "reason" }
            ? named
            : null;

        return new(
            instance.Id,
            FlowChecks.KindOf(instance.Step),
            worksIn,
            instance.State,
            instance.Output,
            instance.Exit,
            instance.SkippedBecause ?? instance.NotRunBecause ?? instance.Error,
            instance.PassedOver,
            instance.HandledBy,
            instance.FinishedAt);
    }

    /// <summary>
    /// The checks the definition holds that no instance <paramref name="drawn"/> holds has ended - done, failed, skipped or
    /// never reached - by step name as <c>step (kind)</c>, since the instances still to come are not known yet,
    /// among the steps <paramref name="counted"/> takes: those the schedule draws - never a handler, a step raised for an
    /// issue, a step of the recovery, nor one inside any of those, which run only where something raises them.
    /// </summary>
    private IEnumerable<string> PendingChecks(Func<string, bool> counted, IReadOnlyList<FlowInstance> drawn)
    {
        var raised = new List<string>();

        foreach (var (path, step) in FlowScope.Declared(_definition))
        {
            if (path == FlowScope.Recovery || path.StartsWith(FlowScope.Recovery + ".", StringComparison.Ordinal) || step.On is not null || step.Raised is not null)
            {
                raised.Add(path);
                continue;
            }

            if (raised.Any(around => path.StartsWith(around + ".", StringComparison.Ordinal))
                || !FlowChecks.IsCheck(step)
                || !counted(path)
                || drawn.Any(instance => instance.Path == path && instance.State is FlowInstanceState.Done or FlowInstanceState.Failed or FlowInstanceState.Skipped or FlowInstanceState.NotRun))
            {
                continue;
            }

            yield return $"{path} ({FlowChecks.KindOf(step)})";
        }
    }

    /// <summary>The run's inputs as one object.</summary>
    private JsonObject Inputs() => FlowInputs.AsObject(_inputs);

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
    /// <param name="expression">The expression, as the definition wrote it.</param>
    /// <param name="scope">The scope the expression is read in.</param>
    /// <param name="problem">Why the expression could not be read, where it could not.</param>
    /// <param name="source">The ending a handler's when reads as <c>source</c>; null for what raised the reader or the group around it.</param>
    /// <param name="reader">The instance the expression is read for, where one is.</param>
    /// <param name="drawing">The visit a declaration being drawn will be, where its when is read as it is drawn.</param>
    /// <param name="mayReadNothing">Whether reading nothing is what the key means by nothing, and is yielded as read rather than said to be a problem.</param>
    private FlowValue Evaluate(string expression, Scope scope, out string? problem, FlowInstance? source = null, FlowInstance? reader = null, int? drawing = null, bool mayReadNothing = false)
    {
        try
        {
            var value = FlowExpression.Parse(expression).Evaluate(path => Read(path, scope, source, reader, drawing));

            if (value.IsMissing && !mayReadNothing)
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
    /// Reads a boolean expression of <paramref name="scope"/> - a <c>when</c>, a repeat's <c>until</c> and
    /// <c>blocking</c> - as true or false: one that cannot be read, or that yields anything but true or false, says why in
    /// <paramref name="problem"/>, and reads false. The one way the engine asks an expression whether something holds.
    /// </summary>
    /// <param name="expression">The expression, as the definition wrote it.</param>
    /// <param name="scope">The scope the expression is read in.</param>
    /// <param name="problem">Why the expression could not be read as true or false, where it could not.</param>
    /// <param name="source">The ending a handler's when reads as <c>source</c>; null for what raised the reader or the group around it.</param>
    /// <param name="reader">The instance the expression is read for, where one is.</param>
    /// <param name="drawing">The visit a declaration being drawn will be, where its when is read as it is drawn.</param>
    private bool Holds(string expression, Scope scope, out string? problem, FlowInstance? source = null, FlowInstance? reader = null, int? drawing = null)
    {
        var value = Evaluate(expression, scope, out problem, source, reader, drawing);

        return problem is null && TruthOf(value, expression, out problem);
    }

    /// <summary>
    /// <paramref name="value"/>, which <paramref name="expression"/> yielded, as true or false: anything else says why in
    /// <paramref name="problem"/>, and reads false.
    /// </summary>
    private static bool TruthOf(FlowValue value, string expression, out string? problem)
    {
        try
        {
            problem = null;
            return value.AsBoolean(expression);
        }
        catch (UnresolvedReadException unreadable)
        {
            problem = unreadable.Message;
            return false;
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

    /// <summary>What an expression a step reads where it starts yields, as its runner is handed it.</summary>
    private enum StartRead
    {
        /// <summary>The value as it reads.</summary>
        Value,

        /// <summary>A list of names: none where it read nothing or null, and anything else cannot be read.</summary>
        Names,

        /// <summary>True or false, and anything else cannot be read.</summary>
        Truth,
    }

    /// <summary>
    /// What <paramref name="instance"/>'s step reads where it starts, into <paramref name="read"/> by key: a test step's
    /// <c>filterWhen</c>, true or false - anything else cannot be read; a <c>validate-plan</c> step's <c>items</c> and
    /// <c>scope</c>, each where it gives it - a scope that reads nothing is no update, and is handed on as read; an anchor
    /// balance's <c>rows</c> and an anchor citations check's <c>paths</c>, a fold's and a deletion's <c>within</c>,
    /// <c>allow.new</c> and <c>allow.merge</c>, a refresh's <c>paths</c>, <c>except</c>, <c>stale</c> and <c>merge</c> and
    /// a rebase's <c>merge</c>, each where it gives an expression, as the list of names its expression yields - one that
    /// may read nothing reads none where it does, or reads null, and one that yields anything but a list of names cannot
    /// be read; an <c>orient</c> step's <c>base</c>, the flow's pull request's base filled as the run's start fills it,
    /// where a pull-request step names one; a <c>ci-wait</c> step's <c>commit</c> where it names none, the commit the
    /// run last pushed - the output of the push step that ended done last - where it pushed one; and a <c>commit</c>
    /// step's <c>folds</c>, the instances of the steps that fold upstream of it (<see cref="Upstream"/>). Why one could not
    /// be read, as the step's record keeps it; null where each was.
    /// </summary>
    private string? ReadAtStart(FlowInstance instance, Dictionary<string, FlowValue> read)
    {
        var step = instance.Step;

        // Each expression the step reads where it starts, under the key its runner reads it by: whether it may read nothing
        // (which reads as none), and what it yields - a list of names or a truth is read as one here, so the runner is
        // handed names, or true or false.
        IEnumerable<(string Key, string? Expression, bool MayReadNothing, StartRead Yields)> expressions = step.Type switch
        {
            "test" => [("filterWhen", step.FilterWhen, false, StartRead.Truth)],
            "validate-plan" => [("items", step.Items, false, StartRead.Value), ("scope", step.Scope, true, StartRead.Value)],
            "anchor-balance" => [("rows", step.Rows?.Expression, true, StartRead.Names)],
            "anchor-citations" => [("paths", step.AnchorPaths?.Expression, true, StartRead.Names)],
            "fold" or "delete-agent" =>
            [
                ("within", step.Within?.Expression, false, StartRead.Names),
                ("allow.new", step.Allow?.New?.Expression, true, StartRead.Names),
                ("allow.merge", step.Allow?.Merge?.Expression, true, StartRead.Names),
            ],
            "refresh" =>
            [
                ("paths", step.Paths?.Expression, false, StartRead.Names),
                ("except", step.Except?.Expression, false, StartRead.Names),
                ("stale", step.Stale?.Expression, false, StartRead.Names),
                ("merge", step.Merge?.Expression, true, StartRead.Names),
            ],
            "rebase" => [("merge", step.Merge?.Expression, true, StartRead.Names)],
            _ => [],
        };

        foreach (var (key, expression, mayReadNothing, yields) in expressions)
        {
            if (expression is null)
            {
                continue;
            }

            var value = Evaluate(expression, instance.DeclaredScope!, out var problem, reader: instance, mayReadNothing: mayReadNothing);

            if (problem is not null)
            {
                return Unreadable(key, instance.Id, problem);
            }

            if (yields == StartRead.Truth)
            {
                var truth = TruthOf(value, expression, out var untrue);

                if (untrue is not null)
                {
                    return Unreadable(key, instance.Id, untrue);
                }

                value = FlowValue.Of(truth);
            }

            if (yields == StartRead.Names)
            {
                if (Names(value, expression, out var names) is { } unnamed)
                {
                    return Unreadable(key, instance.Id, unnamed);
                }

                value = FlowValue.Of(new JsonArray([.. names.Select(name => (JsonNode?)name)]));
            }

            read[key] = value;
        }

        if (step.Type == "ci-wait" && step.Commit is null && LastPushed() is { } pushed)
        {
            read["commit"] = FlowValue.Of(pushed);
        }

        if (step.Type == "commit")
        {
            read["folds"] = FlowValue.Of(new JsonArray([.. _created
                .Where(other => WorkflowStepTypes.Folds(other.Step) && Upstream(other, instance))
                .Select(other => (JsonNode?)other.Id)]));
        }

        if (step.Type == "pull-request" && step.Body is { } body)
        {
            // The body's file is read as the step starts, live as every file a workflow names is, and filled by the one
            // grammar as a template key is: its reads are the step's.
            string text;

            try
            {
                text = _workflowFile?.Invoke(body)
                    ?? throw new HarnessException(HarnessExit.ConfigInvalid, $"'{body}', its body, is a file of the workflow, and this run reads none");
            }
            catch (HarnessException unread)
            {
                return Unreadable("body", instance.Id, unread.Message);
            }

            if (Filled(instance, text, out var unfilled) is not { } filled)
            {
                return Unreadable("body", instance.Id, unfilled!);
            }

            read["body"] = FlowValue.Of(filled);
        }

        if (step.Type == "orient")
        {
            if (FlowTemplate.PullRequestBase(_definition, _inputs, _runName ?? string.Empty, out var problem) is { } filled)
            {
                read["base"] = FlowValue.Of(filled);
            }
            else if (problem is not null)
            {
                return Unreadable("base", instance.Id, problem);
            }
        }

        return null;
    }

    /// <summary>
    /// The names <paramref name="value"/> - what an expression of a key that takes a list of names yielded - holds: none
    /// where it read nothing or null, each text of the list otherwise. Why it holds no names, where it yields anything else.
    /// </summary>
    private static string? Names(FlowValue value, string expression, out IReadOnlyList<string> names)
    {
        names = [];

        if (value.IsMissing || value.IsNull)
        {
            return null;
        }

        if (value.ToNode() is not JsonArray list || list.Any(item => item is not JsonValue text || text.GetValueKind() != JsonValueKind.String))
        {
            return $"'{expression}' yields no list of names";
        }

        names = [.. list.Select(item => item!.GetValue<string>())];
        return null;
    }

    /// <summary>
    /// The commit the run last pushed: the <c>sha</c> of the push step that ended done last, or null where none has -
    /// a run that pushed nothing has no commit of its own to wait for.
    /// </summary>
    private string? LastPushed()
        => _created
            .Where(instance => instance.Step.Type == "push" && instance.State == FlowInstanceState.Done)
            .Where(instance => instance.Output?["sha"] is JsonValue sha && sha.GetValueKind() == JsonValueKind.String)
            .OrderBy(instance => instance.FinishedAt)
            .Select(instance => instance.Output!["sha"]!.GetValue<string>())
            .LastOrDefault();

    /// <summary>
    /// The name the run gives the next agent it makes: one past the highest any step of it holds, any name the session
    /// gave and any agent directory the run has (<see cref="IFlowRecords.NameAgent"/>); a run held in memory alone
    /// reckons by the names it holds and gave.
    /// </summary>
    private string NameAgent()
    {
        IReadOnlyCollection<string> taken = [.. _created.Select(instance => instance.Made).OfType<string>().Concat(_agentNamesGiven).Distinct(StringComparer.Ordinal)];
        var name = _records is null ? OrchestrationRules.NextAgentName(taken) : _records.NameAgent(taken);

        _ = _agentNamesGiven.Add(name);
        return name;
    }

    /// <summary>
    /// Fills a template of <paramref name="instance"/>'s step - a pause step's question, each text its keys hand on
    /// (<see cref="FlowStepTemplates"/>), and a reason step's prompt files - by the one grammar
    /// (<see cref="FlowTemplate.Fill"/>), each name read by the same roots an expression reads. A name that reads nothing
    /// says why, in <paramref name="problem"/>, and fills nothing.
    /// </summary>
    private string? Filled(FlowInstance instance, string template, out string? problem)
        => FlowTemplate.Fill(template, path => Read(path, instance.DeclaredScope!, reader: instance), out problem);

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

    /// <summary>Records one change of the run where its records are, whole: the snapshot is the run as it stands, and the records own the order they are written in.</summary>
    private void Record(FlowChangeKind kind, FlowInstance? instance = null, FlowInstanceState? from = null, FlowInstanceState? to = null, int? attempt = null, string? detail = null)
    {
        if (_records is null || _replaying)
        {
            // A run that holds nothing writes nothing, and the replay of a taken-up run writes nothing step by step:
            // the resume's own record, made once at its entry, says what was taken up.
            return;
        }

        var snapshot = Snapshot();
        var id = instance?.Id;
        Put(records => records.Write(kind, snapshot, id, from?.ToString(), to?.ToString(), attempt, detail));
    }

    /// <summary>
    /// Every write of the run's records goes through here: made at once, or - while a session's start is held - kept,
    /// with the run as it stood when it was made, to be made in the same order once the session goes on, or
    /// <paramref name="dropped"/> where it never will be.
    /// </summary>
    /// <param name="write">The write.</param>
    /// <param name="dropped">What is done instead where the hold it is kept in is dropped; null for nothing.</param>
    private void Put(Action<IFlowRecords> write, Action? dropped = null)
    {
        if (_held is not null)
        {
            _held.Add(new HeldWrite(write, dropped));
            return;
        }

        write(_records!);
    }

    /// <summary>
    /// Makes what a session's start held, in the order it was held - an answer weighed in it settled after what became of
    /// it is written - and ends the hold. Where a write fails, what it had not made yet is dropped, as a refused start's
    /// is: an answer whose record was not written is handed back, never settled.
    /// </summary>
    private void MakeTheHold()
    {
        if (_held is not { } held)
        {
            return;
        }

        _held = null;
        var made = 0;

        try
        {
            for (; made < held.Count; made++)
            {
                held[made].Write(_records!);
            }
        }
        finally
        {
            Drop(held.Skip(made));
        }
    }

    /// <summary>Drops what a session's start held and will never write: each held answer handed back, as one the loop never read is.</summary>
    private static void Drop(IEnumerable<HeldWrite> held)
    {
        foreach (var write in held)
        {
            write.Dropped?.Invoke();
        }
    }

    /// <summary>One write a session's start holds, and what is done instead where the hold is dropped.</summary>
    /// <param name="Write">The write, made with the run as it stood when it was held.</param>
    /// <param name="Dropped">What is done where it will never be made: an answer weighed handed back; null for nothing.</param>
    private sealed record HeldWrite(Action<IFlowRecords> Write, Action? Dropped);

    /// <summary>The run whole, as its records read it: every instance with its state and what it waits on, what runs next, and the run's own standing.</summary>
    private FlowSnapshot Snapshot()
    {
        var waiting = new List<string>(_open);

        foreach (var instance in _created.Where(instance => instance.State is FlowInstanceState.Paused && instance.WaitingQuestion is not null))
        {
            if (!waiting.Contains(instance.WaitingQuestion!, StringComparer.Ordinal))
            {
                waiting.Add(instance.WaitingQuestion!);
            }
        }

        // One walk says both what starts next and what each step that has not started waits on.
        var walk = AdmissionWalk();
        var decided = walk.ToDictionary(decision => decision.Instance);

        return new FlowSnapshot(
            Status,
            _endedAt,
            _spent,
            _budgetGranted,
            _pause,
            [.. _created.Select(instance => SnapshotStepOf(instance, decided))],
            [.. walk.Where(decision => decision.Starts).Select(decision => decision.Instance.Id)],
            waiting,
            _redirects.Count == 0
                ? null
                : [.. _redirects.Select(redirect => new FlowRedirectRecord(
                    redirect.Handler.Id,
                    redirect.Target,
                    [.. redirect.Set.Select(entry => entry.Scope.Members.GetValueOrDefault(entry.Declaration.Name)?.Id ?? MemberId(entry.Scope, entry.Declaration))],
                    redirect.Asking))],
            _tally.Count == 0 ? null : new WorkflowPreExisting(
                _tally.Count(entry => entry.State == "listed"),
                _tally.Count(entry => entry.State == "fixed"),
                _tally.Count(entry => entry.State.StartsWith("duplicate", StringComparison.Ordinal)),
                _tally.Count(entry => entry.State == "unproven")),
            [.. _issues]);
    }

    /// <summary>One instance, as its records read it, what it waits on as the admission walk said it.</summary>
    private FlowSnapshotStep SnapshotStepOf(FlowInstance instance, IReadOnlyDictionary<FlowInstance, Admission> walk)
        => new(
            instance.Id,
            instance.Path,
            instance.Step.Type,
            instance.Step.IsGroup,
            instance.State,
            instance.Attempt,
            instance.Visit,
            instance.StartedAt,
            instance.FinishedAt,
            instance.Exit,
            instance.Error,
            instance.SkippedBecause,
            instance.NotRunBecause,
            instance.Failure,
            instance.EndedByQuestion,
            WorksInOf(instance),
            instance.Made,
            instance.WaitingQuestion,
            instance.Handoff,
            instance.Output?.DeepClone(),
            instance.Item,
            instance.Index,
            instance.Iteration,
            [.. instance._answers],
            WaitsOnOf(instance, walk),
            [.. instance.Assumptions],
            [.. instance._handlers],
            instance.HandledBy,
            instance.Leftover?.DeepClone(),
            instance.RaisedBy?.Id,
            instance.Direction,
            instance.NotRunCause,
            instance.Issue,
            instance.IssuesOwed.Count == 0 ? null : [.. instance.IssuesOwed],
            instance.LeftBy,
            instance.Cause,
            instance.MadeModel,
            instance.Copied,
            instance.Targets);

    /// <summary>
    /// The agent this step works in: the one stored on it (<see cref="FlowInstance.WorksIn"/>), or - before it was stored -
    /// the one its <c>in</c> or <c>of</c> names now (<see cref="NamedAgentOf"/>).
    /// </summary>
    private string? WorksInOf(FlowInstance instance) => instance.WorksIn ?? NamedAgentOf(instance);

    /// <summary>
    /// The agent this step's <c>in</c> or <c>of</c> names as the run stands now: the one its create-agent's newest visit
    /// made, once that visit is done - the name it was given beforehand names no agent yet - or, for a reason step that
    /// goes on with another's work, the one that step's names; null for the create-agent itself and every step that
    /// names none. What is stored on a step where it is drawn or starts, and read again of no step that has it.
    /// </summary>
    private string? NamedAgentOf(FlowInstance instance)
    {
        var working = WorkingOf(instance);

        if (CreateStepNamedBy(working) is { } create)
        {
            return create is { State: FlowInstanceState.Done, Made: { } made } ? made : null;
        }

        // A reason step that goes on with another's work works as that step's agent (<see cref="WorkingOf"/> walked the
        // chain): the one the resumed step works in, or its own engine-made one - read of no step that has one stored.
        if (!ReferenceEquals(working, instance))
        {
            return working.WorksIn ?? working.Made;
        }

        return null;
    }

    /// <summary>
    /// The create-agent step <paramref name="working"/>'s <c>in</c> or <c>of</c> names, its newest visit, found in its own
    /// scope or one around it; null for a create-agent step, a step that names none, and one whose create-agent is not
    /// drawn.
    /// </summary>
    private static FlowInstance? CreateStepNamedBy(FlowInstance working)
        => working.Step.Type == "create-agent" || (working.Step.In ?? working.Step.Of) is not { } named ? null : CreateStepOf(working, named);

    /// <summary>
    /// Why <paramref name="instance"/> - a step that works in or acts on an agent - can never run: the create-agent step its
    /// <c>in</c> or <c>of</c> names ended otherwise than done - skipped, or failed and passed over or handled, which
    /// satisfy what depends on them, or failed or never run where an always step starts over it - so the agent it works
    /// in was never made. Null where its agent is made, where it names none, and where its create-agent step has not
    /// ended - a step the definition's check would refuse for not waiting starts beside it, naming no agent yet.
    /// </summary>
    private string? AgentNeverMade(FlowInstance instance)
    {
        if (instance.WorksIn is not null || NamedAgentOf(instance) is not null || CreateStepNamedBy(WorkingOf(instance)) is not { } create)
        {
            // A step that goes on with another's work can never work as it where the step its chain begins with ended
            // without an agent of its own - skipped, or never run - for there is none to work as.
            if (instance.Step.Type == "reason" && instance.Step.Resume is not null
                && WorkingOf(instance) is { } source && !ReferenceEquals(source, instance)
                && source.State is FlowInstanceState.Skipped or FlowInstanceState.NotRun)
            {
                return $"the step it goes on with never made an agent: '{source.Id}' is {source.State}";
            }

            return null;
        }

        return create.State is FlowInstanceState.Skipped or FlowInstanceState.Failed or FlowInstanceState.NotRun
            ? $"the agent it works in was never made: '{create.Id}' is {create.State}"
            : null;
    }

    /// <summary>
    /// Every read-only agent of the run whose work is over, as an ending settles what can still go on with it: each
    /// agent a reason step of this run made as its own, where its making step has ended past going on and no step
    /// whose chain of resumed work reaches the making one can still run - none of such a step's instances is unended
    /// or redrawable, and no group around its declaration is either, for a repeat may raise one yet. The run going on
    /// - not pausing, not stopping - is the ground: a run that failed, was paused or was stopped leaves every agent
    /// live for the resume.
    /// </summary>
    private IReadOnlyList<string> ReadOnlyAgentsOver()
    {
        if (_deleteAgent is null || _pausing || _hardStop)
        {
            return [];
        }

        var declarations = FlowScope.Declared(_definition).ToList();
        var over = new List<string>();

        foreach (var makers in _created.Where(made => made.Made is not null && made.Step.Type == "reason").GroupBy(made => made.Made!, StringComparer.Ordinal))
        {
            if (makers.Any(made => !PastGoingOn(made)) || ResumersCanStillRun(declarations, makers.First().Path))
            {
                continue;
            }

            over.Add(makers.Key);
        }

        return over;
    }

    /// <summary>Whether a step that goes on with the work made at <paramref name="makerPath"/> can still run: one of its instances is unended or not drawn yet, or a group around its declaration is unended, for a repeat may raise one yet.</summary>
    /// <param name="declarations">Every declaration, with its path.</param>
    /// <param name="makerPath">The making step's declaration path.</param>
    private bool ResumersCanStillRun(IReadOnlyList<(string Path, WorkflowStep Step)> declarations, string makerPath)
    {
        foreach (var (path, declared) in declarations)
        {
            if (declared.Type != "reason" || declared.Resume is null || !ResumesTransitively(declarations, path, makerPath))
            {
                continue;
            }

            var drawn = _created.Where(one => string.Equals(one.Path, path, StringComparison.Ordinal)).ToList();

            if (drawn.Count == 0 || drawn.Any(one => !PastGoingOn(one)) || _created.Any(one => IsAround(one.Path, path) && !PastGoingOn(one)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the step declared at <paramref name="path"/> goes on with the work declared at <paramref name="maker"/>
    /// - resuming it, or resuming a step whose chain of resumed work reaches it: each resume in turn, until the chain
    /// names the maker, ends, or turns on itself.
    /// </summary>
    /// <param name="declarations">Every declaration, with its path.</param>
    /// <param name="path">The declaration that may go on with the maker's work.</param>
    /// <param name="maker">The making step's declaration path.</param>
    private static bool ResumesTransitively(IReadOnlyList<(string Path, WorkflowStep Step)> declarations, string path, string maker)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var at = path;

        while (seen.Add(at))
        {
            if (string.Equals(at, maker, StringComparison.Ordinal))
            {
                return true;
            }

            var current = declarations.FirstOrDefault(one => string.Equals(one.Path, at, StringComparison.Ordinal));

            if (current.Step is not { Resume: { } resumed } || string.Equals(resumed, current.Step.Name, StringComparison.Ordinal))
            {
                return false;
            }

            // The resumed step, by name: the definition's check resolved the name in its own scope, and the first of
            // that name is the one it found.
            var next = declarations.FirstOrDefault(one => one.Step.Type == "reason" && string.Equals(one.Step.Name, resumed, StringComparison.Ordinal));

            if (next.Step is null)
            {
                return false;
            }

            at = next.Path;
        }

        return false;
    }

    /// <summary>Whether <paramref name="group"/> names a group the declaration at <paramref name="declaration"/> sits in, or one around it.</summary>
    /// <param name="group">A group's declaration path.</param>
    /// <param name="declaration">The declaration's path.</param>
    private static bool IsAround(string group, string declaration)
        => declaration.StartsWith(group + ".", StringComparison.Ordinal);

    /// <summary>Whether <paramref name="instance"/> has ended past going on: done or skipped. A failed, stopped, waiting or never-run step may yet go on with its agent's work - a never-run one may be drawn again once what kept it is settled.</summary>
    private static bool PastGoingOn(FlowInstance instance)
        => instance.State is FlowInstanceState.Done or FlowInstanceState.Skipped;

    /// <summary>
    /// Deletes the read-only agent whose work is over, apart from the run's own thread: its failure changes nothing of
    /// the run, which goes on, and is the deleter's to say - the next command over the orchestrator sweeps what a
    /// failed deletion leaves.
    /// </summary>
    /// <param name="agent">The agent to delete.</param>
    private async Task DeleteReadOnlyAsync(string agent)
    {
        try
        {
            await _deleteAgent!(_runName ?? string.Empty, agent, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Said by the deleter, whose own words the run's log keeps: the run itself is not stopped by it.
        }
    }

    /// <summary>
    /// What <paramref name="instance"/>'s runner is asked with for <paramref name="attempt"/>: the step as it is to run, what
    /// the engine read for it where it started and the handoff it is given, beside every fact of the instance - its scope,
    /// its answers, where the run works, the agent it makes, the agent it works in and, of <c>others</c>, the target whose
    /// turn it is. The one builder of it, for an attempt and for the handoff turn a pause gives one alike.
    /// </summary>
    private FlowStepRun RunOf(FlowInstance instance, WorkflowStep step, int attempt, IReadOnlyDictionary<string, FlowValue> read, string? handoff)
        => new(
            step,
            instance.Id,
            attempt,
            instance.Item,
            instance.Index,
            instance.Iteration,
            [.. instance._answers],
            new RunEvents(this, instance, attempt),
            handoff,
            instance.Issue,
            instance.Visit,
            _place,
            read,
            instance.Made is { } made ? new FlowMadeAgent(made, instance.MadeModel) : null,
            WorksInOf(instance),
            instance.Step.Others is null ? null : CurrentTarget(instance)?.Agent,
            WorkedFromOf(instance));

    /// <summary>
    /// The instances whose working sets a reason step's pack is seeded from (value 372), in order: the step it goes on
    /// with, each step it depends on directly - its own <c>dependsOn</c> and <c>on</c> and each group's around it, as the
    /// run holds them now, a group by each reason step in it - then its own earlier visits of the same work item, newest
    /// first; each a reason step that began, once. None for every other step.
    /// </summary>
    private IReadOnlyList<FlowWorkedFrom> WorkedFromOf(FlowInstance instance)
    {
        if (instance.Step.Type != "reason")
        {
            return [];
        }

        var sources = new List<FlowInstance>();

        // A step that goes on with its own work finds itself here, and is left out below: its earlier visits are its sources.
        if (instance.Step.Resume is { } resumed && DeclaredOf(instance, resumed, "reason") is { } goesOnWith)
        {
            sources.Add(goesOnWith);
        }

        for (var at = instance; at is not null; at = at.GroupOf)
        {
            if (at.DeclaredScope is not { } scope)
            {
                continue;
            }

            foreach (var name in at.Step.DependsOn.Concat(at.Step.On is { } raised ? [raised.Step] : []))
            {
                if (scope.Members.TryGetValue(name, out var dependency))
                {
                    sources.AddRange(ReasonStepsOf(dependency));
                }
            }
        }

        sources.AddRange(_created
            .TakeWhile(other => !ReferenceEquals(other, instance))
            .Where(other => FlowInstanceId.SameStep(other.Id, instance.Id))
            .Reverse());

        return [.. sources
            .Where(source => !ReferenceEquals(source, instance) && source.Step.Type == "reason" && source.StartedAt is not null)
            .Distinct<FlowInstance>(ReferenceEqualityComparer.Instance)
            .Select(source => new FlowWorkedFrom(source.Id, WorksInOf(source)))];
    }

    /// <summary>The steps <paramref name="instance"/> is or holds: itself, or each member of a group, at every level, as the run holds them now - in each scope the definition's order.</summary>
    private IEnumerable<FlowInstance> ReasonStepsOf(FlowInstance instance)
        => !instance.Step.IsGroup
            ? [instance]
            : ScopesUnder(instance).SelectMany(scope => DeclarationsOf(scope)
                .Select(declared => scope.Members.GetValueOrDefault(declared.Name))
                .OfType<FlowInstance>());

    /// <summary>What one instance waits on, where it waits: the question it asked, the resume, or what admission says keeps it - a dependency, the quiet an alone step waits for, a claim, a counter, a redirect, the resume.</summary>
    private static IReadOnlyList<string> WaitsOnOf(FlowInstance instance, IReadOnlyDictionary<FlowInstance, Admission> walk)
        => instance.State switch
        {
            FlowInstanceState.Paused when instance.WaitingQuestion is { } waiting => [waiting],
            FlowInstanceState.Stopped => ["resume"],
            FlowInstanceState.NotStarted when walk.TryGetValue(instance, out var admission) => admission.WaitsOn,
            _ => [],
        };

    /// <summary>The steps that would start now, in the order they would start: what the admission walk admits, starting nothing.</summary>
    private List<string> NextIds()
        => [.. AdmissionWalk().Where(decision => decision.Starts).Select(decision => decision.Instance.Id)];

    /// <summary>
    /// Rebuilds the run's graph from the state a taken-up run left: every scope re-expanded to the items and iterations
    /// its records name, every instance set as its own record reads it, and the run's spend, its budget and its pause
    /// restored. The replay writes nothing: the resume's own record, made once at its entry, says what was taken up.
    /// </summary>
    /// <param name="resume">The state a killed or stopped run left.</param>
    private void Replay(FlowResumeState resume)
    {
        _replaying = true;

        try
        {
            var top = NewScope(string.Empty, string.Empty, null, null, FlowValue.Missing, null, null, null);
            _top = top;

            var scopes = new Dictionary<string, Scope>(StringComparer.Ordinal) { [string.Empty] = top };

            // The records are taken in the order the run made them - the index's own, each parent before what is in it,
            // what only a step's directory held after it all - and never sorted again: b+10 stays after b+2, and
            // rounds#10 after rounds#2. One whose scope is not placed yet - an adopted record of a scope the index
            // names later - waits for it; one whose scope never comes is refused.
            var unplaced = resume.File.Steps.ToList();

            while (unplaced.Count > 0)
            {
                var placed = unplaced.RemoveAll(record =>
                {
                    var lastDot = record.Instance.LastIndexOf('.');
                    return scopes.TryGetValue(lastDot < 0 ? string.Empty : record.Instance[..lastDot], out var declaring) && Place(record, declaring);
                });

                if (placed == 0)
                {
                    throw new HarnessException(
                        HarnessExit.Refused,
                        $"'{unplaced[0].Instance}' is inside a scope the run's state holds no record of; the run's state and the definition do not agree, and a record is never guessed at");
                }
            }

            // Places one record in its scope; false where it is an item or an iteration whose loop is not placed yet.
            bool Place(FlowStepRecord record, Scope declaring)
            {
                var lastDot = record.Instance.LastIndexOf('.');
                var parentPrefix = lastDot < 0 ? string.Empty : record.Instance[..lastDot];
                var segment = lastDot < 0 ? record.Instance : record.Instance[(lastDot + 1)..];
                var name = segment.Split(['[', '#', '+'], 2)[0];
                var recovery = _definition.Recover is not null && name == FlowScope.Recovery;
                var declaration = recovery
                    ? RecoveryDeclaration()
                    : DeclarationsOf(declaring).FirstOrDefault(declared => declared.Name == name)
                        ?? throw new HarnessException(
                            HarnessExit.Refused,
                            $"'{record.Instance}' names no step of the definition among '{(declaring.Id.Length == 0 ? "the flow" : declaring.Id)}'; the run's state and the definition do not agree, and a record is never guessed at");

                FlowInstance instance;

                // An item or an iteration is spelled with its index or its number after its loop's own id, visit and
                // all (rounds+2#1, items[0]); a loop visited again is rounds+2, a member like any other.
                var loopEnd = segment.IndexOfAny(['[', '#']);

                if (!recovery && declaration.IsGroup && loopEnd > 0)
                {
                    // One item or iteration of a loop: its scope is made against the loop's own declaring scope, as
                    // the loop's expansion made it, with the item and the index or the iteration the record holds.
                    var outerId = parentPrefix.Length == 0 ? segment[..loopEnd] : $"{parentPrefix}.{segment[..loopEnd]}";

                    if (!_instances.TryGetValue(outerId, out var outer))
                    {
                        // Its loop's own record is not placed yet: it waits for it, and is refused where it never comes.
                        return false;
                    }

                    if (outer.Step != declaration)
                    {
                        throw new HarnessException(
                            HarnessExit.Refused,
                            $"'{record.Instance}' is an instance of a loop the run's state holds no whole record of ('{outerId}'); the run's state and the definition do not agree, and a record is never guessed at");
                    }

                    var scope = record.Index is { } index
                        ? NewScope($"{outer.Id}[{index.ToString(System.Globalization.CultureInfo.InvariantCulture)}]", PathOf(declaring, declaration), declaring, declaration, FlowValue.Of(record.Item?.DeepClone()), index, null, outer)
                        : NewScope($"{outer.Id}#{record.Iteration?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1"}", PathOf(declaring, declaration), declaring, declaration, FlowValue.Missing, null, record.Iteration, outer);

                    outer.Children.Add(scope);
                    scopes[scope.Id] = scope;
                    instance = Create(scope, declaration, StateOf(record), scope.Id);
                }
                else if (recovery || declaration.IsGroup && declaration.ForEach is null && declaration.Repeat is null)
                {
                    // A block: its own scope holds its members, keyed by its instance's id - in whatever state it
                    // ended, for a block that failed or was left holds the records of its members too.
                    instance = Create(declaring, declaration, StateOf(record), memberId: record.Instance);
                    instance.OwnScope = NewScope(instance.Id, PathOf(declaring, declaration), declaring, declaration, FlowValue.Missing, null, null, null);
                    instance.OwnScope.OwnGroupInstance = instance;
                    scopes[instance.Id] = instance.OwnScope;
                }
                else
                {
                    // The declaration is drawn in its scope again, for the resume holds what it made; a member's id
                    // carries its visit, so a name resolves to the newest of its visits as it ran.
                    instance = Create(declaring, declaration, StateOf(record), memberId: record.Instance);
                }

                Apply(instance, record, resume);
                return true;
            }

            foreach (var raised in _created.Where(created => created.RaisedByRecord is not null))
            {
                // What raised what: a handler's source, the step whose failure a recovery took - read back from the
                // records, for both may be drawn before what raised them.
                if (_instances.TryGetValue(raised.RaisedByRecord!, out var source))
                {
                    raised.RaisedBy = source;
                }
            }

            foreach (var visit in _created.Where(created => IsRecovery(created) && created.RaisedBy is not null))
            {
                var key = RecoveryOf(visit.RaisedBy!).Key;
                _recoveryVisits[key] = Math.Max(_recoveryVisits.GetValueOrDefault(key), visit.Visit);
            }

            // The agents the run holds open, as its records say: each agent a create-agent step made, held by its newest
            // visit - a visit again while it is live takes its slot - that no delete-agent step deleted. A deletion's
            // record names the agent it deleted as the one it worked on, stored where it started (FlowInstance.WorksIn).
            var deleted = new HashSet<string>(
                _created.Where(created => created.Step.Type == "delete-agent" && created.State == FlowInstanceState.Done)
                    .Select(deletion => deletion.WorksIn)
                    .OfType<string>(),
                StringComparer.Ordinal);

            foreach (var holder in _created
                .Where(created => created.Step.Type == "create-agent" && created.State == FlowInstanceState.Done)
                .GroupBy(created => created.Made ?? created.Id, StringComparer.Ordinal)
                .Where(agent => !deleted.Contains(agent.Key))
                .Select(agent => agent.Last()))
            {
                _agentsOpen[holder.Id] = holder;
            }

            foreach (var member in _created.Where(created => created.Failure == FlowFailure.TakenByGroup && created.GroupOf is { State: FlowInstanceState.Running or FlowInstanceState.Stopped }).ToList())
            {
                // A member that handed its failure to a group still open: the group starts nothing more and ends failed
                // with the first such member's code once its members in flight have ended, as it would have had the
                // session gone on - its own record says it took the failure, never the memory of the session before.
                FailGroup(member.GroupOf!, member.Exit ?? HarnessExit.InternalError, member.Error, member.Path);
            }

            foreach (var source in _created.Where(created => created.Step is { On: null } && created._handlers.Any(handler => handler.State is "owed" or "raised")))
            {
                // A raise the pause found owed stays owed in the source's record, and the session makes it from there.
                // An ending a redirect abandoned is never waited on again, whatever its other handlers did.
                if (!source._handlers.Any(handler => handler.State == "owed") && source._handlers.Any(handler => handler.State == "raised") && !Abandoned(source))
                {
                    // An ending whose handlers were raised and not all done: the run goes on waiting for them.
                    var handlers = _created.Where(created => created.RaisedBy == source).ToList();
                    _pendingEndings[source.Id] = new PendingEnding(
                        source,
                        source.State == FlowInstanceState.Failed,
                        handlers,
                        handlers.FirstOrDefault(handler => handler.Step.Then is not null));
                    source.FailureSettled = source.State != FlowInstanceState.Failed;
                }
            }

            foreach (var scope in _scopes)
            {
                foreach (var name in scope.Members.Where(member => member.Value.LeftBy is not null).Select(member => member.Key).ToList())
                {
                    // A redirect took the run back past this visit, and its next visit is not drawn yet: the schedule
                    // draws it, as it would have had the session gone on - the redirect itself may be gone from the
                    // run's records, which it leaves once every visit it resets says so.
                    _ = scope.Members.Remove(name);
                    _ = scope.Instantiated.Remove(name);
                }
            }


            _spent.Usd = resume.File.Spent.Usd;
            _spent.Tokens = resume.File.Spent.Tokens;
            _spent.Minutes = resume.File.Spent.Minutes;

            // The issues the run listed are its file's, restored whole; what became of each the tally rebuilds from
            // the triage instances' own records, for the file holds only the listed ones.
            if (resume.Issues is { } listed)
            {
                _issues.AddRange(listed);
            }

            foreach (var listedIssue in _issues)
            {
                _tally.Add(new PreExistingTally("listed", $"{listedIssue.Title} ({string.Join(", ", listedIssue.Where)})"));
            }

            foreach (var triage in _created.Where(created => created.Step.Raised == WorkflowStep.RaisedByPreExisting && created.State == FlowInstanceState.Done))
            {
                switch (CourseOf(triage))
                {
                    case "list" when !Listed(triage):
                        // Its end is written, and the run's file of issues - written after it in the same change - is
                        // not: the issue is listed from its answer, as the end listed it, and the next write writes it.
                        ListIssue(triage);
                        break;
                    case "fix":
                        // Fixed where a recovery it raised ended done; being fixed otherwise - and where none was raised
                        // yet, the issue is owed to the recovery, which this session gives it.
                        var raised = _created.Where(created => IsRecovery(created) && ReferenceEquals(created.RaisedBy, triage)).ToList();
                        _tally.Add(new PreExistingTally(raised.Any(created => created.State == FlowInstanceState.Done) ? "fixed" : "being fixed", $"{TitleOf(triage)} ({WhereOf(triage)})", triage));

                        if (raised.Count == 0)
                        {
                            _pendingFixes.Add(triage);
                        }

                        break;
                    case "duplicate" or "unproven":
                        _tally.Add(SettledTally(triage, CourseOf(triage)!));
                        break;
                }
            }

            // Every question the run raised is its file's, restored whole - answered, withdrawn and open alike, each
            // with every answer it was given: the file is the one owner of what every question said and who answered
            // it, and the next write of it is made from these. The run goes on answering the open ones by id as it did,
            // the asking instance waiting as its own record says; the next question takes the id after the highest of
            // any state, never one a step's record already names; and what the answers granted - a counted answer a
            // configuration spent, one more iteration, visit or recovery - is counted again from them.
            // What the run did with each answer handed over through the inbox is its file's too - one taken under its
            // entry's id comes back with its question, one refused here - so no session weighs an entry twice.
            _refused.AddRange(resume.Refused ?? []);

            foreach (var entry in resume.Questions ?? [])
            {
                var asked = FlowQuestion.FromEntry(entry);
                _questions.Add(asked);
                _questionsRaised = Math.Max(_questionsRaised, int.Parse(asked.Id[1..], System.Globalization.CultureInfo.InvariantCulture));

                if (asked.State != "open")
                {
                    continue;
                }

                var about = _instances.GetValueOrDefault(asked.Instance);
                _open.Add(asked.Id);
                _answerable[asked.Id] = about;

                if (about is not null)
                {
                    // A question is written before the record of the step that waits on it: a crash between the two
                    // leaves the question open and the record behind it, which is completed from the question - the
                    // step waits on it, paused where its own work asked, never run again beside it.
                    about.WaitingQuestion = asked.Id;

                    if (!FlowQuestions.AsksOfAnEnding(asked.Kind) && about.State is FlowInstanceState.NotStarted or FlowInstanceState.Running or FlowInstanceState.Stopped)
                    {
                        about.State = FlowInstanceState.Paused;
                    }
                }
            }

            foreach (var waiting in _created.Where(created => created.WaitingQuestion is { } question && !_open.Contains(question)).ToList())
            {
                // A step whose record still waits on a question the file holds answered: the answer was written and the
                // record that follows what it changed was not. The next session carries the answer out, once; one the
                // file holds withdrawn waits on nothing.
                var asked = _questions.FirstOrDefault(question => question.Id == waiting.WaitingQuestion);
                waiting.WaitingQuestion = null;

                if (asked is { State: "answered", Given.Count: > 0 })
                {
                    _carryAtStart.Add((waiting, asked));
                }
            }

            foreach (var owed in resume.File.Redirects ?? [])
            {
                // Every redirect the run owed, in the order decided, each written before its first reset: the session
                // makes each in its turn, once nothing it resets is running - one asking about an open agent waits
                // for the answer, the ones after it behind it.
                var handler = _instances.GetValueOrDefault(owed.Handler)
                    ?? throw new HarnessException(
                        HarnessExit.Refused,
                        $"the run owes a redirect by '{owed.Handler}', which its state holds no record of; the run's state is not whole, and a record is never guessed at");
                var redirect = new PendingRedirect(handler, owed.Target) { Asking = owed.Question is { } question && _open.Contains(question) ? question : null };

                foreach (var id in owed.Steps)
                {
                    redirect.Set.Add(ResetOf(id) ?? throw new HarnessException(
                        HarnessExit.Refused,
                        $"the redirect by '{owed.Handler}' resets '{id}', which names no step of the run's state; the run's state and the definition do not agree, and a record is never guessed at"));
                }

                FrozenOf(redirect);
                _redirects.Add(redirect);
            }

            CountTheAnswers();

            if (resume.File.Pause is { } pause && pause.CompletedAt is not null)
            {
                _pause = new FlowPauseState(pause.Cause, pause.All, pause.At) { CompletedAt = pause.CompletedAt };

                foreach (var id in pause.Handoffs)
                {
                    _pause._handoffs.Add(id);
                }
            }
        }
        finally
        {
            _replaying = false;
        }

        // Every name a record holds is read by the one strict reader of the engine's names, never guessed at.
        static FlowInstanceState StateOf(FlowStepRecord record)
            => FlowRecordNames.Of<FlowInstanceState>(record.State);

        // What one id a redirect's record resets names: a visit the state holds, or - for a step not drawn yet - the
        // declaration its first visit would be, under the scope its id names.
        (Scope Scope, WorkflowStep Declaration)? ResetOf(string id)
        {
            if (_instances.TryGetValue(id, out var visit))
            {
                return visit.DeclaredScope is { } declared ? (declared, visit.Step) : null;
            }

            var dot = id.LastIndexOf('.');
            var scopeId = dot < 0 ? string.Empty : id[..dot];
            var name = dot < 0 ? id : id[(dot + 1)..];

            return _scopes.FirstOrDefault(scope => scope.Id == scopeId) is { } at && DeclarationsOf(at).FirstOrDefault(declaration => declaration.Name == name) is { } declaration
                ? (at, declaration)
                : null;
        }

        void Apply(FlowInstance instance, FlowStepRecord record, FlowResumeState from)
        {
            instance.State = StateOf(record);
            instance.Attempt = record.Attempt;
            instance.Visit = record.Visit;
            instance.Issue = record.Issue;
            instance.IssuesOwed = record.IssuesOwed ?? [];
            instance.Exit = record.Exit;
            instance.Error = record.Error;
            instance.SkippedBecause = record.SkippedBecause;
            instance.NotRunBecause = record.NotRunBecause;
            instance.NotRunCause = record.NotRunCause is { } cause ? FlowRecordNames.Of<FlowNotRunCause>(cause) : null;
            instance.Cause = record.Cause is { } outcome ? FlowRecordNames.Of<OutcomeCause>(outcome) : OutcomeCause.None;
            instance.Failure = record.FailureOf();
            instance.EndedByQuestion = record.EndedByQuestion;
            instance.Output = record.Output?.DeepClone();
            instance.StartedAt = record.StartedAt;
            instance.FinishedAt = record.FinishedAt;
            instance.Made = record.Made;
            instance.MadeModel = record.Model;
            instance.WorksIn = record.Agent;
            instance.WaitingQuestion = record.Question;
            instance.HandledBy = record.HandledBy;
            instance.Leftover = record.Leftover?.DeepClone();
            instance.Direction = record.Direction;
            instance.LeftBy = record.LeftBy;
            instance.RaisedByRecord = record.RaisedBy;
            instance.Targets = record.Targets;
            instance.FromResume = true;
            instance._handlers.AddRange(record.Handlers ?? []);
            instance._answers.AddRange(record.Answers);
            instance.Assumptions = record.Assumptions ?? [];

            // Whether a failure is settled is read from where it left the run: passed over, ended the run, taken by its
            // group or by a handler. One still owed, or waiting on what raised for it, is not.
            instance.FailureSettled = instance.State != FlowInstanceState.Failed
                || instance.Failure is FlowFailure.PassedOver or FlowFailure.EndedRun or FlowFailure.TakenByGroup
                || instance.HandledBy is not null;

            if (instance.State is FlowInstanceState.Stopped or FlowInstanceState.Running or FlowInstanceState.Paused && record.Handoff is { } handoff)
            {
                // The handoff the stop or the limit left is in the step's own directory - and so is one an attempt a
                // crash cut off still held, no prompt holding it yet, and one a step paused on a question was given for
                // the attempt the answer goes on with: the resume is given it where it is, never one in its place.
                instance.Handoff = from.Handoffs.TryGetValue(instance.Id, out var text)
                    ? new FlowHandoff(text, handoff.By, handoff.Why)
                    : null;
            }
        }
    }

    /// <summary>
    /// What the answers <c>questions.json</c> holds granted, counted again over the graph a resume rebuilt: each counted
    /// answer a configuration gave, where it is counted, so its <c>times</c> are spent across sessions as within one;
    /// each <c>more</c> a repeat's bound was answered, one more iteration of that repeat; each <c>more</c> a handler's
    /// bound was answered, one more visit of that handler in its scope; and each <c>more</c> or <c>direct</c> the
    /// recovery's bound was answered, one more visit of it. The answers are the one record of these grants.
    /// </summary>
    private void CountTheAnswers()
    {
        _budgetGranted = 0;

        foreach (var asked in _questions)
        {
            var about = _instances.GetValueOrDefault(asked.Instance);
            var carried = _carryAtStart.Any(pending => ReferenceEquals(pending.Asked, asked));

            foreach (var given in asked.Given)
            {
                if (given.By == "configuration" && WorkflowValidator.GoesOn.Contains(given.Answer, StringComparer.Ordinal))
                {
                    var counted = FlowQuestions.CountedAt(asked.Kind, asked.Instance, asked.Handler);
                    _answerSpent[counted] = _answerSpent.GetValueOrDefault(counted) + 1;
                }

                if (asked.Kind == "runBudget" && given.Answer == "more")
                {
                    // Each more the budget was answered allows each amount once again: the answers are the one record of it.
                    _budgetGranted++;
                }

                if (carried && ReferenceEquals(given, asked.Given[^1]))
                {
                    // What the answer the session carries out at its start grants, the carrying grants.
                    continue;
                }

                switch (asked.Kind, given.Answer)
                {
                    case ("loopBound", "more") when about is not null:
                    {
                        about.ExtraIterations++;
                        break;
                    }

                    case ("visitBound", "more") when about?.DeclaredScope is { } scope && asked.Handler is { } handler:
                    {
                        scope.ExtraVisits[handler] = scope.ExtraVisits.GetValueOrDefault(handler) + 1;
                        break;
                    }

                    case ("recoveryBound", "more" or "direct") when about is not null:
                    {
                        var key = RecoveryOf(about).Key;
                        _recoveryExtra[key] = _recoveryExtra.GetValueOrDefault(key) + 1;
                        break;
                    }
                }
            }
        }
    }

    /// <summary>
    /// One thing the coordinator learns while it waits: a step's ending, an event of one of its legs, what a reasoning step
    /// spent, an answer offered from outside, a handoff taken, or a step's time limit passing - named by the timer it came
    /// from, so the timer of a run that has answered already says nothing.
    /// </summary>
    private readonly record struct Message(FlowInstance Instance, FlowStepResult? Result, FlowLegEvent? Leg, (double Usd, long Tokens)? Usage = null, int Attempt = 0, (string Question, string Answer, string? Text, string? Through, string? Inbox, TaskCompletionSource<FlowHandedOver> Completion)? Offered = null, bool HandoffTaken = false, CancellationTokenSource? TimeLimit = null);

    /// <summary>
    /// Offers an answer to a running session from outside it - taken from the run's inbox while the engine runs, by
    /// the same hand a person at the terminal is answered by. The loop alone applies it, for the graph is the loop's
    /// alone to change; where the question is no longer open - a redirect withdrew it, or another answer came first -
    /// the answer is refused and said. One offered under an inbox entry the run's records already hold is never weighed
    /// again (<see cref="AnswerHandedOver"/>). One offered before a session begins - the first, or the next - is taken at
    /// that session's start, before anything is admitted; one left unread as a session ends is handed back cancelled -
    /// never refused, for it was never weighed - so it stays where it was handed over for whoever takes it next. What
    /// became of it is told only once it is written: one weighed at a session's start, whose writes are held, is told
    /// when they are made, and handed back cancelled where they never are - a resume refused writes nothing of itself.
    /// </summary>
    /// <param name="question">The question's id.</param>
    /// <param name="answer">The answer, spelled as the question's options spell it.</param>
    /// <param name="text">The text given with the answer, where it takes one.</param>
    /// <param name="through">How a person gave it; <c>answer-orchestrator</c> where the command handed it over.</param>
    /// <param name="inbox">The id of the inbox entry it was handed over under; null for an answer no inbox handed over.</param>
    /// <returns>What became of the answer, once the session has weighed it and written what it did; cancelled where no session weighed it, or none wrote it.</returns>
    public Task<FlowHandedOver> OfferAnswerAsync(string question, string answer, string? text = null, string? through = "answer-orchestrator", string? inbox = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(question);
        ArgumentException.ThrowIfNullOrWhiteSpace(answer);

        var completion = new TaskCompletionSource<FlowHandedOver>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = _messages.Writer.TryWrite(new Message(null!, null, null, null, 0, (question, answer, text, through, inbox, completion)));
        return completion.Task;
    }

    /// <summary>What one event of a running step's legs says.</summary>
    private enum FlowLegEventKind
    {
        /// <summary>The leg works in a copy of its own, and not in the tree itself.</summary>
        PlacedInACopy,

        /// <summary>The leg works in the tree itself.</summary>
        PlacedInTheTree,

        /// <summary>Every leg the attempt runs is placed.</summary>
        EveryLegPlaced,

        /// <summary>The leg's copy is made.</summary>
        CopyMade,

        /// <summary>The leg's work in the tree itself has ended.</summary>
        Ended,
    }

    /// <summary>One event of a running step's legs.</summary>
    /// <param name="Kind">What it says.</param>
    /// <param name="Leg">The leg's name; null where the event is of every leg.</param>
    private readonly record struct FlowLegEvent(FlowLegEventKind Kind, string? Leg);

    /// <summary>One wait the loop keeps beside the channel: a retry's turn, or - where <paramref name="Drain"/> - the bound on a stopped step's runner ending.</summary>
    /// <param name="Instance">The step it is for.</param>
    /// <param name="At">When it ends.</param>
    /// <param name="Delay">The wait itself, by the run's clock.</param>
    /// <param name="Drain">Whether it bounds a stopped step's drain rather than a retry's turn.</param>
    private sealed record Wake(FlowInstance Instance, DateTimeOffset At, Task Delay, bool Drain = false);

    /// <summary>The events of one running step, posted to the coordinator's channel.</summary>
    private sealed class RunEvents(FlowCoordinator coordinator, FlowInstance instance, int attempt) : IFlowRunEvents
    {
        /// <inheritdoc/>
        public void LegPlaced(string leg, bool inACopy)
            => Post(new FlowLegEvent(inACopy ? FlowLegEventKind.PlacedInACopy : FlowLegEventKind.PlacedInTheTree, leg));

        /// <inheritdoc/>
        public void EveryLegPlaced()
            => Post(new FlowLegEvent(FlowLegEventKind.EveryLegPlaced, null));

        /// <inheritdoc/>
        public void LegCopyMade(string leg)
            => Post(new FlowLegEvent(FlowLegEventKind.CopyMade, leg));

        /// <inheritdoc/>
        public void LegEnded(string leg)
            => Post(new FlowLegEvent(FlowLegEventKind.Ended, leg));

        /// <summary>Posts one event of this attempt's legs.</summary>
        private void Post(FlowLegEvent leg)
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, leg, Attempt: attempt));

        /// <inheritdoc/>
        public void Used(long tokens, double usd)
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, null, (usd, tokens), attempt));

        /// <inheritdoc/>
        public void HandoffTaken()
            => _ = coordinator._messages.Writer.TryWrite(new Message(instance, null, null, Attempt: attempt, HandoffTaken: true));
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

    /// <summary>How many visits of each member the scope has made: the newest is what the member's name resolves to.</summary>
    public Dictionary<string, int> Visits { get; } = new(StringComparer.Ordinal);

    /// <summary>How many more visits of each member an answer granted past the bound its declaration names.</summary>
    public Dictionary<string, int> ExtraVisits { get; } = new(StringComparer.Ordinal);
}
