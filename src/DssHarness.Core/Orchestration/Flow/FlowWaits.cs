namespace DssHarness.Core.Orchestration.Flow;

/// <summary>
/// The words a step that has not started is said to wait on in its record where no step it depends on holds it: the
/// admission writes each from here, and every reader of a run's records reads each by its word here, so the walk that
/// decides and the facts a run is asked about read one wait one way. Each holds a space, and a step's name never does,
/// so none is ever read as a step of the same name.
/// </summary>
public static class FlowWaits
{
    /// <summary>A claim another step holds, or one a step before it in the walk waits for.</summary>
    public const string Claim = "a claim";

    /// <summary>The run to be quiet: an <c>alone</c> step running, or one waiting for nothing else to run.</summary>
    public const string Quiet = "the run to be quiet";

    /// <summary>Room among the reasoning agents the run lets run at once.</summary>
    public const string ReasoningAgents = "reasoning agents";

    /// <summary>Room among the processes the run lets run at once.</summary>
    public const string ParallelProcesses = "parallel processes";

    /// <summary>Room among the agents the run lets be open at once.</summary>
    public const string OpenAgents = "open agents";

    /// <summary>The run's resume: a hard stop or a pause under way, or a step a stop left stopped.</summary>
    public const string Resume = "the resume";

    /// <summary>The always phase, which begins once the rest of the run is quiescent.</summary>
    public const string Always = "the always phase";

    /// <summary>A stop under way to end: a stopping run starts its always steps alone.</summary>
    public const string Stop = "the stop to end";

    /// <summary>An advice of the step's own question that was ended, and whose runner has not ended yet.</summary>
    public const string Advice = "an advice to end";

    /// <summary>A redirect not yet made, which will reset the step.</summary>
    public const string Redirect = "a redirect";

    /// <summary>A plan update under way, planned by <paramref name="planner"/>: nothing starts outside the scope it is planned in until its plan stands.</summary>
    /// <param name="planner">The id of the step instance that plans the update.</param>
    public static string Hold(string planner) => $"hold {planner}";

    /// <summary>The step whose work the planning of an update goes on with, in flight: the planning starts once it has ended.</summary>
    /// <param name="inFlight">The id of the step instance in flight.</param>
    public static string Resumes(string inFlight) => $"resumes {inFlight}";

    /// <summary>The run bringing the step's instance to rest for a plan update: nothing starts in it.</summary>
    /// <param name="update">The update's id.</param>
    public static string EndedForUpdate(string update) => $"ended for update {update}";

    /// <summary>
    /// The answer to the question a loop that decides at a step of its own asked there, at its bound: nothing of the
    /// iteration starts until it says whether the loop goes on or ends.
    /// </summary>
    /// <param name="question">The question's id.</param>
    public static string Answer(string question) => $"{AnswerTo}{question}";

    /// <summary>
    /// The fix of an issue the recovery is making, which holds files the step's work item owns: the item has not begun,
    /// and starts once the fix's visit has ended, so it builds on the tree the fix leaves (<see cref="FlowIssueHold"/>).
    /// </summary>
    /// <param name="title">The issue's title.</param>
    /// <param name="fixer">The id of what fixes it: the recovery's visit, or the triage that sent it while that visit waits to be made.</param>
    /// <param name="paths">The paths the issue holds that the item owns.</param>
    public static string Fix(string title, string fixer, IReadOnlyList<string> paths)
        => $"the fix of '{title}' ({fixer}), which holds {string.Join(", ", paths)}";

    /// <summary>The machine a step's legs run on, its dispatch queue: every leg of the step still to run waits its turn there.</summary>
    public const string MachineQueue = "the machine's queue";

    /// <summary>The machine a step's legs run on, its memory in use: every leg of the step still to run waits for it to fall.</summary>
    public const string MachineMemory = "the machine's memory";

    /// <summary>The machine a step's legs run on, the room its builds need: every leg of the step still to run waits for it.</summary>
    public const string MachineRoom = "the machine's room";

    /// <summary>
    /// Whether <paramref name="wait"/> is a wait for room - a claim, the run to be quiet, a counter of the run's limits,
    /// or the machine its legs run on holding them in its queue, for its memory or for its room - which only the run's
    /// or the machine's own pace ends, and whose length no step's time says.
    /// </summary>
    /// <param name="wait">One word of a record's <c>waitsOn</c>.</param>
    public static bool ForRoom(string wait)
        => wait is Claim or Quiet or ReasoningAgents or ParallelProcesses or OpenAgents or MachineQueue or MachineMemory or MachineRoom;

    /// <summary>
    /// Whether <paramref name="wait"/> is a wait on a person: a question's own id, which the step that asked it waits on,
    /// or the answer to one a loop's bound asked (<see cref="Answer"/>) - a wait a person ends, whether or not an engine
    /// runs while it is open.
    /// </summary>
    /// <param name="wait">One word of a record's <c>waitsOn</c>.</param>
    public static bool ForAPerson(string wait)
        => FlowQuestionIds.IsWellFormed(wait) || (wait.StartsWith(AnswerTo, StringComparison.Ordinal) && FlowQuestionIds.IsWellFormed(wait[AnswerTo.Length..]));

    /// <summary>The words that begin a wait on the answer to a question (<see cref="Answer"/>).</summary>
    private const string AnswerTo = "the answer to ";
}
