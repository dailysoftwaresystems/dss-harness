using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DssHarness.Core.Output;
using DssHarness.Core.Results;

namespace DssHarness.Core.Orchestration.Flow;

/// <summary>One instance of a check, as the texts of what a run checked read it from the run's records.</summary>
/// <param name="Instance">The instance's id.</param>
/// <param name="Kind">What check it is: its step's type, or <c>independent</c> for a reason step that is one.</param>
/// <param name="WorksIn">The agent whose worktree it worked in; null where it worked in the main tree, or in none.</param>
/// <param name="State">How it stands.</param>
/// <param name="Output">What it answered; null where it answered nothing.</param>
/// <param name="Exit">The code its work ended with; null where it never ran.</param>
/// <param name="Why">Why it ended as it did, where its state alone cannot say: why it was skipped or not run, or what failed it.</param>
/// <param name="PassedOver">Whether the run passed its failure over.</param>
/// <param name="HandledBy">The handler that took its failure, where one did.</param>
/// <param name="EndedAt">When it ended; null while it has not.</param>
public sealed record FlowCheck(
    string Instance,
    string Kind,
    string? WorksIn,
    FlowInstanceState State,
    JsonNode? Output,
    int? Exit,
    string? Why,
    bool PassedOver,
    string? HandledBy,
    DateTimeOffset? EndedAt);

/// <summary>
/// What a run checked, did not check and knows to be red, in words: <c>{run.ran}</c>, <c>{run.notRun}</c> and
/// <c>{run.red}</c> of the whole run so far, and <c>{carried.ran}</c>, <c>{carried.notRun}</c> and <c>{carried.red}</c>
/// of what one commit carries - the same three texts, of the checks the engine hands them (part 04, "What ran, in words").
/// A check is a step of a type the plan's <c>Checks</c> column marks (<see cref="WorkflowStepTypes.StepType.Checks"/>) or
/// a reason step marked <c>independent</c>, whatever it is named. Each text is a line a check, <c>none</c> where there is
/// none; pure, over the records it is handed, so every reader of the same records says the same.
/// </summary>
public static class FlowChecks
{
    /// <summary>What a run's text for a template says where it names nothing: each of these, and what its loops left, the issues it found there before and what its agents assumed.</summary>
    public const string None = "none";

    /// <summary>What a reason step that checks is called in the texts: its type says only that it reasons.</summary>
    public const string Independent = "independent";

    /// <summary>Whether a step of <paramref name="step"/> is a check.</summary>
    /// <param name="step">The step, as it was declared.</param>
    public static bool IsCheck(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Type is { } type && (WorkflowStepTypes.Find(type)?.Checks == true || (type == "reason" && step.Independent == true));
    }

    /// <summary>What check a step of <paramref name="step"/> is: its type, or <see cref="Independent"/> for a reason step.</summary>
    /// <param name="step">The step, a check.</param>
    public static string KindOf(WorkflowStep step)
    {
        ArgumentNullException.ThrowIfNull(step);

        return step.Type == "reason" ? Independent : step.Type ?? string.Empty;
    }

    /// <summary>
    /// Every check of <paramref name="checks"/> that ended, a line each in the order they ended: the instance, what check it
    /// is, where it ran - the main tree, an agent's worktree, or CI - and its verdict, each leg's for a step that runs legs.
    /// </summary>
    /// <param name="checks">The checks the text is of.</param>
    public static string Ran(IEnumerable<FlowCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        return Lines(checks.Where(Ended).OrderBy(check => check.EndedAt).Select(check => $"{Named(check)}: {Verdict(check)}"));
    }

    /// <summary>
    /// Every check of <paramref name="checks"/> that did not check - skipped, or never reached, each with its reason, and
    /// where <paramref name="passedOver"/> says so, each whose failure was passed over - then each of
    /// <paramref name="pending"/>, a check of the definition that has not run yet, by its step's name.
    /// </summary>
    /// <param name="checks">The checks the text is of.</param>
    /// <param name="pending">Each check that has not run yet, as <c>step (kind)</c>.</param>
    /// <param name="passedOver">Whether a check whose failure was passed over is one that did not check.</param>
    public static string NotRun(IEnumerable<FlowCheck> checks, IEnumerable<string> pending, bool passedOver)
    {
        ArgumentNullException.ThrowIfNull(checks);
        ArgumentNullException.ThrowIfNull(pending);

        var lines = checks
            .OrderBy(check => check.EndedAt)
            .Select(check => check switch
            {
                { State: FlowInstanceState.Skipped } => $"{Named(check)}: skipped{Because(check.Why)}",
                { State: FlowInstanceState.NotRun } => $"{Named(check)}: not run{Because(check.Why)}",
                { PassedOver: true } when passedOver => $"{Named(check)}: failed and passed over{Because(check.Why)}",
                _ => null,
            })
            .OfType<string>();

        return Lines(lines.Concat(pending));
    }

    /// <summary>
    /// Every red result of <paramref name="checks"/> not seen green since - a later visit or iteration of the same step
    /// of the same work item that ended green - a line each: each red leg of a step that runs legs, each red check of a
    /// wait for CI, the findings of an anchor check, the problems of a plan, and a failure that says no more.
    /// </summary>
    /// <param name="checks">The checks the text is of.</param>
    public static string Red(IEnumerable<FlowCheck> checks)
    {
        ArgumentNullException.ThrowIfNull(checks);

        var ended = checks.Where(Ended).OrderBy(check => check.EndedAt).ToList();

        return Lines(ended
            .Where((check, at) => Passed(check) == false
                && !ended.Skip(at + 1).Any(later => FlowInstanceId.SameStep(later.Instance, check.Instance) && Passed(later) != false))
            .SelectMany(RedLines));
    }

    /// <summary>Whether <paramref name="check"/> ended: done, or failed.</summary>
    private static bool Ended(FlowCheck check) => check.State is FlowInstanceState.Done or FlowInstanceState.Failed;

    /// <summary>
    /// Whether <paramref name="check"/> passed: false where it failed or answered that it did not pass, true where it
    /// answered that it did, and null for a check that answers no verdict of its own - a reason step's.
    /// </summary>
    private static bool? Passed(FlowCheck check)
        => check.State == FlowInstanceState.Failed ? false
            : Flag(check.Output, "passed") ?? Flag(check.Output, "ok");

    /// <summary>
    /// The instance, what check it is and where it ran: an agent's worktree, the main tree, or - a type that holds no tree,
    /// which reads the forge - CI.
    /// </summary>
    private static string Named(FlowCheck check)
        => $"{check.Instance} ({check.Kind}) {(check.WorksIn is { } agent ? $"in {agent}'s worktree" : WorkflowStepTypes.Find(check.Kind) is { ClaimsOf.Count: 0 } ? "on CI" : "in the main tree")}";

    /// <summary>A check's verdict in words, each leg's after it for a step that runs legs, and what became of a failure.</summary>
    private static string Verdict(FlowCheck check)
    {
        var said = check.State == FlowInstanceState.Failed
            ? $"failed ({(check.Exit ?? HarnessExit.InternalError).ToString(CultureInfo.InvariantCulture)}){(check.Why is { } why ? $": {why}" : string.Empty)}"
            : Passed(check) switch { true => "passed", false => "red", null => "done" };
        var legs = Each(check.Output, "verdicts").Select(verdict => $"{Text(verdict, "leg")} {Text(verdict, "verdict")}").ToList();

        return said
            + (legs.Count > 0 ? $" - {string.Join(", ", legs)}" : string.Empty)
            + (check.PassedOver ? "; passed over" : check.HandledBy is { } handler ? $"; handled by {handler}" : string.Empty);
    }

    /// <summary>The lines a red check says: each red entry its output names, else its findings or problems counted, else its verdict.</summary>
    private static IEnumerable<string> RedLines(FlowCheck check)
    {
        var red = Each(check.Output, "red").ToList();

        if (red.Count > 0)
        {
            return red.Select(entry => $"{Named(check)}: {Text(entry, "leg") ?? Text(entry, "name")} {Text(entry, "verdict") ?? Text(entry, "conclusion") ?? "red"}"
                + (Text(entry, "phase") is { } phase ? $" at {phase}" : string.Empty));
        }

        var findings = Each(check.Output, "findings").Count();
        var problems = Each(check.Output, "problems").Count();

        return [findings > 0 ? $"{Named(check)}: {ReportText.Counted(findings, "finding")}"
            : problems > 0 ? $"{Named(check)}: {ReportText.Counted(problems, "problem")}"
            : $"{Named(check)}: {Verdict(check)}"];
    }

    /// <summary>Lines joined, or <see cref="None"/> where there is none.</summary>
    private static string Lines(IEnumerable<string> lines)
    {
        var said = lines.ToList();
        return said.Count == 0 ? None : string.Join("\n", said);
    }

    /// <summary>Why, after a dash; nothing where there is no why.</summary>
    private static string Because(string? why) => why is null ? string.Empty : $" - {why}";

    /// <summary>The truth <paramref name="output"/> holds under <paramref name="name"/>; null where it holds none.</summary>
    private static bool? Flag(JsonNode? output, string name)
        => output is JsonObject found && found[name] is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : null;

    /// <summary>The text <paramref name="entry"/> holds under <paramref name="name"/>; null where it holds none.</summary>
    private static string? Text(JsonNode? entry, string name)
        => entry is JsonObject found && found[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    /// <summary>The members of the list <paramref name="output"/> holds under <paramref name="name"/>; none where it holds none.</summary>
    private static IEnumerable<JsonNode?> Each(JsonNode? output, string name)
        => output is JsonObject found && found[name] is JsonArray list ? list : [];
}
