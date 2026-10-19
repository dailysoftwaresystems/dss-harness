using System.Text.Json.Nodes;
using DssHarness.Core.Execution;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// One question a run raises, in memory while it is open: what was asked, of which step, with the answers it takes.
/// The run's one file of questions and answers - and the answers a configuration gives beforehand - come with the run
/// on disk; this is the question itself, as the coordinator holds it.
/// </summary>
/// <param name="Id">The question's id (<c>q1</c>, <c>q2</c>, in the order raised).</param>
/// <param name="Kind">The kind of question it is, one of <see cref="WorkflowValidator.QuestionKinds"/>.</param>
/// <param name="Instance">The instance that asks it, by its id.</param>
/// <param name="Problem">What the question says: the problem, in the engine's own words.</param>
/// <param name="Options">The answers it takes, spelled as the kind spells them.</param>
public sealed record FlowQuestion(string Id, string Kind, string Instance, string Problem, IReadOnlyList<string> Options);

/// <summary>One answer a question was given, and what it carried beside it.</summary>
/// <param name="Question">The question's id.</param>
/// <param name="Answer">The answer, as the question's options spell it.</param>
public sealed record FlowAnswerGiven(string Question, string Answer);

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
public sealed record FlowStepRun(
    WorkflowStep Step,
    string Instance,
    int Attempt,
    FlowValue Item,
    int? Index,
    int? Iteration,
    IReadOnlyList<FlowAnswerGiven> Answers,
    IFlowRunEvents Events)
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
    /// <param name="cancellationToken">Stops the work; nothing of the run is changed by a cancellation.</param>
    Task<FlowStepResult> RunAsync(FlowStepRun run, CancellationToken cancellationToken);
}

/// <summary>
/// What a step tells the coordinator while it runs, beside the result it ends with: where each of its legs works, and
/// when each one's copy is made and each one's work in the tree itself has ended. The engine owns the claims; these
/// events are how the runner says what the leg services know.
/// </summary>
public interface IFlowRunEvents
{
    /// <summary>
    /// One leg of this step works in a copy of its own (<paramref name="inACopy"/> true), and not in the tree itself
    /// (false): the step's claim of run on the tree ends when every copy is made and every leg in the tree itself has
    /// ended, and not before.
    /// </summary>
    /// <param name="leg">The leg's name.</param>
    /// <param name="inACopy">Whether the leg works in a copy of its own.</param>
    void LegPlaced(string leg, bool inACopy);

    /// <summary>The copy the leg <paramref name="leg"/> works in is made: the tree is let go, for this leg.</summary>
    /// <param name="leg">The leg's name.</param>
    void LegCopyMade(string leg);

    /// <summary>The leg <paramref name="leg"/>'s work in the tree itself has ended: for a leg that works in the tree, the tree is held to this.</summary>
    /// <param name="leg">The leg's name.</param>
    void LegEnded(string leg);
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

    /// <summary>Every leg the step ran, with each one's own verdict; the steps that run no legs give none.</summary>
    public IReadOnlyList<FlowLeg> Legs { get; init; } = [];

    /// <summary>The typed cause, where the exit code alone cannot say which kind of refusal it was.</summary>
    public OutcomeCause Cause { get; init; }

    /// <summary>A result for a step that ended: passed or not, with its code.</summary>
    public static FlowStepResult Ended(bool passed, int exit, string? error = null, JsonNode? output = null)
        => new() { Asked = null, Passed = passed, Exit = exit, Error = error, Output = output };

    /// <summary>A result for a step that asks: no ending, the question open.</summary>
    public static FlowStepResult Asking(FlowQuestion question)
        => new() { Asked = question, Passed = false, Exit = HarnessExit.Incomplete };
}
