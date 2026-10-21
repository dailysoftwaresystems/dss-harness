using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Orchestration;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One answer a question was given, and what it carried beside it.</summary>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer, as the question's options spell it.</param>
public sealed record FlowAnswerGiven(string Question, string Answer);

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
    FlowPreExistingReport? Issue = null)
{
    /// <summary>Which visit of the step this is: the first, until a redirect sends the run back (the redirect is its own slice).</summary>
    public int Visit => 1;
}

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

    /// <summary>The leg <paramref name="leg"/>'s work in the tree itself has ended: for a leg that works in the tree, the tree is held to this.</summary>
    /// <param name="leg">The leg's name.</param>
    void LegEnded(string leg);

    /// <summary>What a reason step's conversation has spent as it runs: tokens and dollars, counted against the run's budget.</summary>
    /// <param name="tokens">The tokens the clients report: input, cache write, cache read and output.</param>
    /// <param name="usd">What they cost, at the prices the configuration gives each model.</param>
    void Used(long tokens, double usd);
}

/// <summary>
/// What a fold family step's dry run stood, as its runner reports it over the verbs' own facts: the consents it lacks
/// by kind with what it named, or its refusal - for paths whose three-way merge conflicts alone, or with anything else
/// among its reasons, which fails the step.
/// </summary>
/// <param name="Standing">What the dry run wants of its caller.</param>
/// <param name="Consents">Each consent the request lacks, by kind, in the order the dry run named them.</param>
/// <param name="Named">What the dry run named: the rows, the cells or the paths each consent is about.</param>
/// <param name="Refusals">Each reason the request stands refused for, by kind, where it stands refused.</param>
/// <param name="Summary">The dry run's own summary, the line it ended on.</param>
public sealed record FlowDryRun(
    RequestStanding Standing,
    IReadOnlyList<ConsentKind> Consents,
    IReadOnlyList<string> Named,
    IReadOnlyList<RefusalKind> Refusals,
    string Summary);

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

    /// <summary>A result for a step that ended: passed or not, with its code.</summary>
    public static FlowStepResult Ended(bool passed, int exit, string? error = null, JsonNode? output = null, IReadOnlyList<FlowPreExistingReport>? preExisting = null)
        => new() { Asked = null, Passed = passed, Exit = exit, Error = error, Output = output, PreExisting = preExisting ?? [] };

    /// <summary>A result for a step that asks: no ending, the question open.</summary>
    public static FlowStepResult Asking(FlowQuestion question)
        => new() { Asked = question, Passed = false, Exit = HarnessExit.Incomplete };

    /// <summary>A result for a fold family step whose dry run stood short of applying.</summary>
    public static FlowStepResult Standing(FlowDryRun dryRun)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.Refused, DryRun = dryRun };

    /// <summary>A result for a reason step at a limit of its own.</summary>
    public static FlowStepResult AtLimit(string limit, string? detail = null)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.CommandFailed, Limit = limit, LimitDetail = detail };

    /// <summary>A result for a wait for checks that came to nothing it could end on.</summary>
    public static FlowStepResult Found(string checks, string? detail = null)
        => new() { Asked = null, Passed = false, Exit = HarnessExit.CommandFailed, Checks = checks, ChecksDetail = detail };
}
