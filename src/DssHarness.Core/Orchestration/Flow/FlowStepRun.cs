using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One answer a question was given, and what it carried beside it.</summary>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer, as the question's options spell it.</param>
/// <param name="Text">The text given with the answer, where it takes one; null where none was.</param>
/// <param name="Kind">The question's kind, as the runner reads it to serve the answer; null where it was not said.</param>
/// <param name="Attempt">The attempt the answer was given to; null where it was not said. An answer that goes on with an attempt belongs to it alone, and a later attempt serves none of an earlier one's.</param>
public sealed record FlowAnswerGiven(string Question, string Answer, string? Text = null, string? Kind = null, int? Attempt = null);

/// <summary>
/// One issue a reason step's answer reports having met that was there before the run's changes: it reports; it never
/// fixes one outside its own work. Each issue raises one instance of the step the reporting step's
/// <c>onPreExisting</c> names, which triages it.
/// </summary>
/// <param name="Title">What it is, in a line.</param>
/// <param name="Description">What it is, in the words the reporting agent gave.</param>
/// <param name="Where">The paths or the command it is in, as the repository spells them.</param>
/// <param name="Evidence">What the agent read or ran and saw: a file and line it quotes, a command and the output.</param>
/// <param name="Blocks">Whether the issue stops the reporting step's own work.</param>
public sealed record FlowPreExistingReport(string Title, string Description, string Where, string Evidence, bool Blocks);

/// <summary>
/// The run a step belongs to: where it works, its name, the commit and the branch it started from and the workflow it
/// runs.
/// </summary>
/// <param name="Repository">The main checkout's root: where every service a step calls is asked from.</param>
/// <param name="Run">The run's name, which names its orchestrator.</param>
/// <param name="StartCommit">The commit the run started from; null where its records name none.</param>
/// <param name="Definition">The workflow the run runs, as it was frozen when the run began.</param>
/// <param name="Branch">
/// The branch the run started on, as its records keep it (<see cref="FlowRunContext.Branch"/>): the one its commits, its
/// pushes and its pull request work on (<see cref="OffItsBranch"/>); null where its records name none.
/// </param>
public sealed record FlowRunPlace(string Repository, string Run, string? StartCommit, WorkflowDefinition Definition, string? Branch = null)
{
    /// <summary>
    /// Why a step that publishes the run's work - a commit, a push, a pull request - is refused with the main checkout on
    /// <paramref name="branch"/>: it is not the branch the run started on - a person checked another out while the run was
    /// paused - and the run's work would go onto that one. The one guard of the three; null where the checkout is on the
    /// run's branch, or where the run's records name none.
    /// </summary>
    /// <param name="instance">The step's instance, which the refusal names.</param>
    /// <param name="branch">The branch the main checkout is on now; null where its HEAD is detached.</param>
    /// <param name="untouched">What the step did not do, as the refusal's last sentence says it: <c>staged</c>, <c>pushed</c> or <c>opened</c>.</param>
    public string? OffItsBranch(string instance, string? branch, string untouched)
        => Branch is { } started && !string.Equals(branch, started, StringComparison.Ordinal)
            ? $"'{instance}' works on '{started}', the branch the run started on, and the main checkout {(branch is null ? "has its HEAD detached" : $"is on '{branch}'")} now: check out '{started}', then resume. Nothing was {untouched}."
            : null;
}

/// <summary>
/// The agent a step makes, as the engine named it before the step began: written to the step's record with the model
/// it is made for before anything makes it, so a step run again asks for the same agent whatever the configuration
/// says by then.
/// </summary>
/// <param name="Name">The agent's name, <c>a&lt;N&gt;</c>.</param>
/// <param name="Model">The model it is made for; null where none resolved for the step, which its runner refuses.</param>
public sealed record FlowMadeAgent(string Name, string? Model);

/// <summary>
/// What one step is asked to run: its definition, the instance of it that runs, the scope the instance sits in, and
/// the answers this visit's questions were given.
/// </summary>
/// <param name="Step">The step as it was declared.</param>
/// <param name="Instance">The instance's id, as <see cref="FlowInstanceId"/> spells it.</param>
/// <param name="Attempt">Which attempt of the visit this is: 1 for the first, one more for each retry.</param>
/// <param name="Item">The work item of the <c>forEach</c> this step is an instance of, or Missing outside every one.</param>
/// <param name="Index">The item's index, from 0, or <see langword="null"/> outside every <c>forEach</c>.</param>
/// <param name="Iteration">The iteration of the <c>repeat</c> this step is an instance of, from 1, or <see langword="null"/> outside every one.</param>
/// <param name="Answers">The answers the questions of this visit were given, in the order they were given.</param>
/// <param name="Events">What the step tells the coordinator as it runs, beside the result it ends with.</param>
/// <param name="Handoff">The handoff this attempt is given, where a stop cut the attempt before it off; null where it is given none.</param>
/// <param name="Issue">The pre-existing issue this instance is raised to triage, where it is one; null for every step the schedule starts.</param>
/// <param name="Visit">Which visit of the step this is: 1 for the first, one more for each time a redirect, a handler or the recovery sent the run back to it.</param>
/// <param name="Place">Where the run works; null for a run held in memory alone, which no step that calls a service can run in.</param>
    /// <param name="Read">
    /// What the engine read for the step where it starts, by key: a <c>validate-plan</c> step's <c>items</c> and
    /// <c>scope</c>, each where the step gives it; an <c>orient</c> step's <c>base</c>, the flow's <c>pull-request</c> step's
    /// base filled (<see cref="FlowTemplate.PullRequestBase"/>), where one names it; an anchor check's <c>rows</c> or
    /// <c>paths</c>, the names its expression yielded, none where it read nothing; a fold's or a deletion's <c>within</c>,
    /// <c>allow.new</c> and <c>allow.merge</c>, a refresh's <c>paths</c>, <c>except</c>, <c>stale</c> and <c>merge</c> and a
    /// rebase's <c>merge</c>, each the names its expression yielded, under its key - <c>allow.new</c> and <c>allow.merge</c>
    /// spelt so - and none where one that may read nothing did; a <c>ci-wait</c> step's <c>commit</c> where it names none,
    /// the commit the run last pushed; a <c>commit</c> step's <c>folds</c>, the instances of the fold and delete-agent steps
    /// upstream of it that fold; a <c>pull-request</c> step's <c>body</c>, the text of the file it names, filled; a
    /// <c>reason</c> step's <c>prompt</c> and <c>resumePrompt</c>, the text of each file it names, filled with every name
    /// but a skill's, and its <c>writes</c>, the names its expression yielded, narrowed as a written-out list narrows
    /// before its runner is asked. Empty for every other step.
    /// </param>
/// <param name="Made">The agent a <c>create-agent</c> step makes, as the engine named it; null for every other step.</param>
    /// <param name="WorksIn">
    /// The agent the step works in or acts on - the one its <c>in</c> or <c>of</c> names, as that step's create-agent made
    /// it; a reason step that goes on with another's work, the agent that step works in; a reason step that names none,
    /// the engine's own read-only agent, named for it - whose worktree a leg step works in and whose directory keeps the
    /// step's records; null for a step that names none.
    /// </param>
/// <param name="Target">
/// The agent a refresh or a rebase of <c>others</c> works on in this attempt: each of its targets in turn, one at a time,
/// each kept in the step's directory under <c>targets/&lt;agent&gt;/</c>; null for every other step.
/// </param>
/// <param name="WorkedFrom">
/// A reason step's: the instances whose working sets its context pack is seeded from (<see cref="FlowWorkingSets"/>), in
/// order - the step it goes on with, each reason step it depends on directly (a group by each one in it), then its own
/// earlier visits, newest first - each that began once and no other; none for every other step.
/// </param>
/// <param name="Telling">
/// How the engine tells the attempt's agent while it runs: what it posts into the conversation the attempt is in, and
/// the handoff it raises where a plan update ends the step in order. One per attempt - a telling of an attempt that is
/// over says nothing about the one the run is on now; null for every step but a reason one.
/// </param>
/// <param name="PlanTold">
/// What a plan update that told this work said, with what raised it: the part of its own (<c>&lt;plan-updates&gt;</c>)
/// that an attempt's first message holds, so a step that starts or goes on later in work a <c>told</c> piece named
/// reads it whole. Null where no update told this work, and for a step that carries a piece out, which its own prompt
/// reads as <c>{piece.*}</c>.
/// </param>
public sealed record FlowStepRun(
    WorkflowStep Step,
    string Instance,
    int Attempt,
    FlowValue Item,
    int? Index,
    int? Iteration,
    IReadOnlyList<FlowAnswerGiven> Answers,
    IFlowRunEvents Events,
    string? Handoff = null,
    FlowPreExistingReport? Issue = null,
    int Visit = 1,
    FlowRunPlace? Place = null,
    IReadOnlyDictionary<string, FlowValue>? Read = null,
    FlowMadeAgent? Made = null,
    string? WorksIn = null,
    string? Target = null,
    IReadOnlyList<FlowWorkedFrom>? WorkedFrom = null,
    FlowTelling? Telling = null,
    string? PlanTold = null);

/// <summary>
/// The one who runs a step: the code a step type calls - a machine step's service, a reason step's client runner - asked
/// as a task, one instance at a time. The engine owns the graph; this owns the work, and answers with
/// <see cref="FlowStepResult"/>, posting the events of its legs to <see cref="IFlowRunEvents"/> as it runs.
/// </summary>
public interface IFlowStepRunner
{
    /// <summary>Runs one instance of a step of a run.</summary>
    /// <param name="run">The step, the instance, the scope it sits in, and the answers its questions were given.</param>
    /// <param name="cancellationToken">Stops the work: a safe pause that stops machine steps and a hard stop cancel it; nothing of the run is changed by a cancellation.</param>
    Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken);

    /// <summary>
    /// The handoff turn of a reason step a safe pause stopped: the agent's account of what it did, what is left and
    /// what it was in the middle of. Answer <see langword="null"/> where the agent gave none - its client had ended,
    /// its turn passed its bound, its answer was empty - and the engine writes the handoff itself.
    /// </summary>
    /// <param name="run">The step, the instance and its scope, as it was last asked.</param>
    /// <param name="cancellationToken">Cancels the turn: a hard stop, or the bound the engine applies, ends it.</param>
    Task<string?> HandoffAsync(FlowStepRun run, CancellationToken cancellationToken);
}

/// <summary>
/// How the engine tells the agent of a reason step that runs: the messages it posts into the conversation the attempt
/// is in - landing when the agent's calls have finished, never cutting its turn - and the handoff it raises where a
/// plan update ends the step in order, whose lead tells the piece before the agent is asked where the work stands.
/// Made where the attempt starts, one per attempt: a telling of an attempt that is over says nothing about the one the
/// run is on now, and nothing posted to one is lost unsaid, for the channel of an ended attempt takes nothing.
/// </summary>
public sealed class FlowTelling(int attempt, TimeSpan handoffBound)
{
    /// <summary>The attempt this telling belongs to, from one.</summary>
    public int Attempt { get; } = attempt;

    /// <summary>Where the engine posts what it has to tell the agent while it works.</summary>
    public Agents.AgentMessages Messages { get; } = new();

    /// <summary>The handoff signal the engine raises where a plan update ends the step in order.</summary>
    public Agents.AgentHandoffSignal Handoff { get; } = new(handoffBound);

    /// <summary>Why the handoff was raised and what the agent was told before it was asked, where it was; null where it was not.</summary>
    public Agents.AgentHandoffAsked? Asked { get; private set; }

    /// <summary>Whether this is the telling of <paramref name="attempt"/>: one of an attempt that is over is spoken to no more.</summary>
    /// <param name="attempt">The attempt the engine would tell.</param>
    public bool For(int attempt) => Attempt == attempt;

    /// <summary>
    /// Posts <paramref name="message"/> into the conversation the attempt is in, where this is the attempt's telling:
    /// the client gives it to the agent when the calls it is running have finished, and its turn is not cut for it.
    /// </summary>
    /// <param name="attempt">The attempt the engine would tell.</param>
    /// <param name="message">The message, whole.</param>
    /// <returns>Whether the attempt's conversation holds it now.</returns>
    public bool Post(int attempt, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        return For(attempt) && Messages.Post(message);
    }

    /// <summary>
    /// Raises the handoff for the attempt's own agent - its turn ended where it stands, one more asked of it, the
    /// handoff turn, with <paramref name="lead"/> told before the question - where this is the attempt's telling and
    /// none was raised before.
    /// </summary>
    /// <param name="attempt">The attempt the engine would tell.</param>
    /// <param name="why">Why the step is ended in order, as the record says it.</param>
    /// <param name="lead">What the agent is told first: the piece that names its work, whole.</param>
    /// <returns>Whether the handoff was raised for a live attempt of the step.</returns>
    public bool Ask(int attempt, string why, string? lead)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(why);

        if (!For(attempt) || Asked is not null)
        {
            return false;
        }

        Handoff.Ask(why, lead);
        Asked = new Agents.AgentHandoffAsked(why, lead);
        return true;
    }
}

/// <summary>
/// What a step tells the coordinator while it runs, beside the result it ends with: where each of its legs works, and
/// when each one's copy is made and each one's work in the tree itself has ended, and what its reasoning spent. The
/// engine owns the claims and the budget; these events are how the runner says what the leg services and the clients
/// know.
/// </summary>
public interface IFlowRunEvents
{
    /// <summary>
    /// One leg of this step works in a copy of its own (<paramref name="inACopy"/> true), and not in the tree itself
    /// (false): the step's claim of run on the tree ends when every leg is placed (<see cref="EveryLegPlaced"/>), every
    /// copy is made and every leg in the tree itself has ended, and not before.
    /// </summary>
    /// <param name="leg">The leg's name.</param>
    /// <param name="inACopy">Whether the leg works in a copy of its own.</param>
    void LegPlaced(string leg, bool inACopy);

    /// <summary>
    /// Every leg this attempt runs is placed: <see cref="LegPlaced"/> was said of each. Until it is said the step holds
    /// its tree whatever the legs placed so far did, for a leg placed later may work in the tree itself.
    /// </summary>
    void EveryLegPlaced();

    /// <summary>The copy the leg <paramref name="leg"/> works in is made: the tree is let go, for this leg.</summary>
    /// <param name="leg">The leg's name.</param>
    void LegCopyMade(string leg);

    /// <summary>
    /// The leg <paramref name="leg"/> has ended: whatever of the tree it was to read, it has read. A leg that works in the
    /// tree itself holds it to this, and one placed in a copy whose copy was never made - its sync refused or failed -
    /// lets it go here too.
    /// </summary>
    /// <param name="leg">The leg's name.</param>
    void LegEnded(string leg);

    /// <summary>What a reason step's conversation has spent as it runs: tokens and dollars, counted against the run's budget.</summary>
    /// <param name="tokens">The tokens the clients report: input, cache write, cache read and output.</param>
    /// <param name="usd">What they cost, at the prices the configuration gives each model.</param>
    void Used(long tokens, double usd);

    /// <summary>
    /// The prompt that holds this attempt's <see cref="FlowStepRun.Handoff"/> is written: the handoff is spent, and its
    /// file goes. Until it is said - or the attempt ends done - the step keeps it, so a crash between the two leaves it
    /// in one place or the other and never in neither.
    /// </summary>
    void HandoffTaken();
}

/// <summary>What one consent a fold family step's dry run lacks is about: its kind, and the rows, the cells or the paths it names.</summary>
/// <param name="Kind">The consent.</param>
/// <param name="Named">What it is about, as the dry run's plan names it.</param>
public sealed record FlowNamed(ConsentKind Kind, IReadOnlyList<string> Named);

/// <summary>
/// What a fold family step's dry run stood, as its runner reports it over the verbs' own facts: the consents it lacks
/// by kind with what each is about, the paths it would write that the step's <c>within</c> leaves out, or its refusal -
/// for paths whose three-way merge conflicts alone, or with anything else among its reasons, which fails the step.
/// </summary>
/// <param name="Standing">What the dry run wants of its caller.</param>
/// <param name="Consents">Each consent the request lacks, by kind, in the order the dry run named them.</param>
/// <param name="Named">What each consent it lacks is about, by its kind.</param>
/// <param name="Refusals">Each reason the request stands refused for, by kind, where it stands refused.</param>
/// <param name="Summary">The dry run's own summary, the line it ended on.</param>
public sealed record FlowDryRun(
    RequestStanding Standing,
    IReadOnlyList<ConsentKind> Consents,
    IReadOnlyList<FlowNamed> Named,
    IReadOnlyList<RefusalKind> Refusals,
    string Summary)
{
    /// <summary>The paths both trees changed whose three-way merge conflicts, as its plan names them.</summary>
    public IReadOnlyList<string> Conflicting { get; init; } = [];

    /// <summary>The paths it would write that the step's <c>within</c> leaves out; empty where the step names none, or it writes none.</summary>
    public IReadOnlyList<string> Outside { get; init; } = [];

    /// <summary>The lines its dry run said under its summary.</summary>
    public IReadOnlyList<string> Lines { get; init; } = [];

    /// <summary>The agent the step works on, as its questions name it; null where a test says none.</summary>
    public string? Agent { get; init; }

    /// <summary>The target a step of <c>others</c> works on now, which its questions name beside the step; null for every other step.</summary>
    public string? Target { get; init; }

    /// <summary>What the consent of <paramref name="kind"/> is about; nothing where the dry run lacks no such consent.</summary>
    /// <param name="kind">The consent.</param>
    public IReadOnlyList<string> NamedOf(ConsentKind kind) => Named.FirstOrDefault(named => named.Kind == kind)?.Named ?? [];

    /// <summary>
    /// What a fold family step's dry run stood, read from the verb's own facts: its standing, each consent it lacks with
    /// what its plan names it is about, its refusals, the paths its merges conflict on and its lines - and
    /// <paramref name="outside"/>, the paths the step's <c>within</c> leaves out, which the verb never sees.
    /// </summary>
    /// <param name="dry">What the dry run answered with; its standing weighed.</param>
    /// <param name="outside">The paths it would write that the step's <c>within</c> leaves out.</param>
    /// <param name="agent">The agent the step works on.</param>
    /// <param name="target">The target a step of <c>others</c> works on now; null for every other step.</param>
    /// <exception cref="ArgumentException">The dry run's standing could not be weighed: no question is asked of it.</exception>
    public static FlowDryRun Of(AgentVerbFacts dry, IReadOnlyList<string> outside, string agent, string? target)
    {
        ArgumentNullException.ThrowIfNull(dry);

        return new FlowDryRun(
            dry.Standing ?? throw new ArgumentException("a dry run whose standing could not be weighed raises no question", nameof(dry)),
            dry.Needs,
            [.. dry.Needs.Select(kind => new FlowNamed(kind, dry.Planned?.Named(kind) ?? []))],
            dry.Refusals,
            dry.Summary)
        {
            Conflicting = dry.Planned?.Conflicting ?? [],
            Outside = outside,
            Lines = dry.Lines,
            Agent = agent,
            Target = target,
        };
    }
}

/// <summary>How one step's run ended, as its runner reports it to the coordinator.</summary>
/// <remarks>
/// A leg step reports each leg's verdict on its own, and never the command's own exit code as the verdicts: the
/// coordinator reads the verdicts and says what the step ends with. A step that asks returns with its question and no
/// ending; when the question is answered the runner is asked again, with the answers among what it is given.
/// </remarks>
public sealed record FlowStepResult
{
    /// <summary>The step asks this question and no ending has come: its visit pauses on the answer.</summary>
    public required FlowQuestion? Asked { get; init; }

    /// <summary>The step's work ended: it passed, whatever <c>failOn</c> says of what it found.</summary>
    public required bool Passed { get; init; }

    /// <summary>
    /// The code the step's own work ended with - for a leg step, the worst of what its legs' verdicts say, and never the
    /// command's own code.
    /// </summary>
    public required int Exit { get; init; }

    /// <summary>Why the step ended as it did, where the code alone cannot say.</summary>
    public string? Error { get; init; }

    /// <summary>The step's output, as the run keeps it and later steps read it; <see langword="null"/> where it wrote none.</summary>
    public JsonNode? Output { get; init; }

    /// <summary>
    /// The issues a reason step's answer reports having met that were there before the run's changes: each raises one
    /// instance of the step its <c>onPreExisting</c> names, which triages it. Empty for every other step.
    /// </summary>
    public IReadOnlyList<FlowPreExistingReport> PreExisting { get; init; } = [];

    /// <summary>Every leg the step ran, with each one's own verdict; the steps that run no legs give none.</summary>
    public IReadOnlyList<FlowLeg> Legs { get; init; } = [];

    /// <summary>
    /// A check that runs no legs - an anchor check, a CI read, a wait for checks - reached its verdict, and it is red:
    /// under <c>failOn: red</c> the step fails with <see cref="Exit"/>, the code its check gives what it found; under
    /// <c>never</c> it is done, and the steps after it read <c>passed</c>. <see cref="Passed"/> says the check did its
    /// work. A step that runs legs says red by their verdicts, and never sets it.
    /// </summary>
    public bool Red { get; init; }

    /// <summary>The typed cause, where the exit code alone cannot say which kind of refusal it was.</summary>
    public OutcomeCause Cause { get; init; }

    /// <summary>
    /// The step's dry run stood short of applying: it lacks a consent, or it stands refused for conflicting merges
    /// alone, and the coordinator raises the question for it. Never set together with an ending.
    /// </summary>
    public FlowDryRun? DryRun { get; init; }

    /// <summary>
    /// A reason step reached a limit of its own - <c>turns</c>, <c>budget</c>, <c>truncated</c> or <c>context</c> -
    /// which asks and is no failure; <see langword="null"/> for every other result. Never set together with an ending.
    /// </summary>
    public string? Limit { get; init; }

    /// <summary>What the limit's question says was spent against it, where the runner reports one.</summary>
    public string? LimitDetail { get; init; }

    /// <summary>
    /// A wait for checks found none (<c>none</c>), found them still running (<c>pending</c>) or found one that ended
    /// neither green nor red (<c>unsettled</c>): each asks and is no failure; <see langword="null"/> for every other
    /// result. Never set together with an ending.
    /// </summary>
    public string? Checks { get; init; }

    /// <summary>What the wait's question says of what it found, where the runner reports it.</summary>
    public string? ChecksDetail { get; init; }

    /// <summary>
    /// A <c>create-agent</c> step's name is spent: the agent the engine named for it was deleted, and can never be made
    /// again under that name, so the step's next attempt is named anew. False for every other result.
    /// </summary>
    public bool NameSpent { get; init; }

    /// <summary>
    /// The run must pause safely and this step runs again at the resume, never a failure: every account a reason step
    /// could use is at its limit, or its provider's service failed past the client's own tries - the run's own answer,
    /// for every model the step could move to would meet the same. Holds why; null for every other result.
    /// </summary>
    public string? Pauses { get; init; }

    /// <summary>
    /// What the step's agent assumed where a person could have told it - the envelope's own list, masked - kept for the
    /// step's record whatever else the attempt ended by; empty where it assumed nothing.
    /// </summary>
    public IReadOnlyList<Agents.AgentAssumption> Assumptions { get; init; } = [];

    /// <summary>
    /// Whether a stored conversation of this attempt can be handed to a client - which is where the answer
    /// <c>more</c> is offered, for more of the same conversation could not help elsewhere.
    /// </summary>
    public bool StoredConversation { get; init; }

    /// <summary>A result for a step that ended: passed or not, with its code.</summary>
    public static FlowStepResult Ended(bool passed, int exit, string? error = null, JsonNode? output = null, IReadOnlyList<FlowPreExistingReport>? preExisting = null)
        => new() { Asked = null, Passed = passed, Exit = exit, Error = error, Output = output, PreExisting = preExisting ?? [] };

    /// <summary>
    /// A result for a step whose call of a service ended as <paramref name="outcome"/> did, short of success: its code, its
    /// cause - a held lock, which the retry table waits out - and its words, or <paramref name="error"/> where the step says
    /// them itself. The one way a runner hands on a failed outcome, so none drops the cause the code alone cannot say.
    /// </summary>
    /// <param name="outcome">How the call ended.</param>
    /// <param name="error">The step's own words for it; null for the outcome's.</param>
    public static FlowStepResult Failed(CommandOutcome outcome, string? error = null)
    {
        ArgumentNullException.ThrowIfNull(outcome);
        return Ended(passed: false, outcome.ExitCode, error ?? outcome.Message) with { Cause = outcome.Cause };
    }

    /// <summary>A result for a step that asks: no ending, the question open.</summary>
    public static FlowStepResult Asking(FlowQuestion question)
        => new() { Asked = question, Passed = false, Exit = HarnessExit.Incomplete };

    /// <summary>A result for a fold family step whose dry run stood short of applying.</summary>
    public static FlowStepResult Standing(FlowDryRun dryRun)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.Refused, DryRun = dryRun };

    /// <summary>A result for a reason step at a limit of its own.</summary>
    public static FlowStepResult AtLimit(string limit, string? detail = null)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.CommandFailed, Limit = limit, LimitDetail = detail };

    /// <summary>
    /// A result for a reason step the run pauses safely for and resumes at that very step: never a failure, never a
    /// question, for every model it could move to would meet the same.
    /// </summary>
    /// <param name="why">Why the run pauses: what every account or the provider's service did.</param>
    public static FlowStepResult SafelyPausing(string why)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.Incomplete, Pauses = why };

    /// <summary>A result for a wait for checks that came to nothing it could end on.</summary>
    public static FlowStepResult Found(string checks, string? detail = null)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.CommandFailed, Checks = checks, ChecksDetail = detail };
}

/// <summary>One asking of the run's advisor: the question it recommends on, with every text its prompt reads.</summary>
/// <param name="Advisor">The orchestrator's advisor block, as the definition declares it.</param>
/// <param name="Run">The orchestrator run's name.</param>
/// <param name="Workflow">The workflow's name, whose folder holds the advisor's agent and prompt.</param>
/// <param name="QuestionId">The question's id.</param>
/// <param name="Kind">The question's kind.</param>
/// <param name="Problem">The problem as the engine states it.</param>
/// <param name="Tried">What the run tried before it asked.</param>
/// <param name="Means">What the answer changes; null where the run has not said.</param>
/// <param name="Answers">The answers it takes, each with what it does and whether it takes a text.</param>
/// <param name="Step">The asking step's instance id, its advice recorded under its own directory.</param>
/// <param name="Agent">The agent the asking step works as, whose directory and workspace the advice shares; null where it names none yet.</param>
/// <param name="Instance">The asking step's instance id, as its records name it.</param>
/// <param name="Advice">Which asking of this question this is, from 1: the directory under the asking step.</param>
/// <param name="Inputs">The run's inputs, as its person gave them.</param>
/// <param name="Record">What the run stored of the asking step, rendered as a continuation record renders a step.</param>
/// <param name="Bar">The repository's bar for a recommendation, read where the question was raised.</param>
/// <param name="Place">Where the run works; null for a run held in memory alone.</param>
public sealed record FlowAdvisorCall(
    FlowAdvisor Advisor,
    string Run,
    string Workflow,
    string QuestionId,
    string Kind,
    string Problem,
    string? Tried,
    string? Means,
    IReadOnlyList<FlowQuestionOption> Answers,
    string Step,
    string? Agent,
    string Instance,
    int Advice,
    System.Text.Json.Nodes.JsonObject Inputs,
    string? Record,
    string Bar,
    FlowRunPlace? Place = null);

/// <summary>
/// The advice one question of the run's was given: the answer its advisor recommends with why and a text where the
/// answer takes one, or - where none was obtained - why, said as the question's own recommendation says it.
/// </summary>
/// <param name="Answer">The answer recommended, one of the question's; null where none was obtained.</param>
/// <param name="Why">Why that answer, by the bar the advisor was given; null where none was obtained.</param>
/// <param name="Text">The text recommended with it, where the answer takes one; null otherwise.</param>
/// <param name="Model">The model that served the advisor, where one did.</param>
/// <param name="Directory">Where the advisor's own records are, under the asking step; null where none were kept.</param>
/// <param name="NotObtained">Why no recommendation was obtained, where it was sought and not had; null otherwise.</param>
/// <param name="Usd">What the advice cost, where a model served it; null where nothing is known.</param>
/// <param name="Tokens">The tokens its clients reported, where a model served it; null where nothing is known.</param>
public sealed record FlowAdvice(
    string? Answer,
    string? Why,
    string? Text,
    string? Model = null,
    string? Directory = null,
    string? NotObtained = null,
    double? Usd = null,
    long? Tokens = null);

/// <summary>
/// The engine's advisor: one reason step that only reads, run for a question the engine raises before a person is left
/// with it. Never between a person and an answer - the caller raises the question at once, and an answer ends the
/// asking, its record kept.
/// </summary>
public interface IFlowAdvisorRunner
{
    /// <summary>Recommends an answer to the question <paramref name="call"/> names, or why none was obtained.</summary>
    /// <param name="call">The asking: the question, its texts, and the asking step's place.</param>
    /// <param name="cancellationToken">Ends the asking: a person answered, or the run stopped.</param>
    Task<FlowAdvice> AdviseAsync(FlowAdvisorCall call, CancellationToken cancellationToken);
}
